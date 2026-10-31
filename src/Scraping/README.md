# Scraping — influencer discovery + transcript mining

Finds small channels that mention immortality / transhumanism in passing, and
mines what they actually *say* (auto-captions, no ASR needed).

Stack mirrors [`rvinowise/twitter_bot`](https://github.com/rvinowise/twitter_bot),
bumped to current versions:

| Layer | Package | Version |
|---|---|---|
| Runtime | .NET | **10.0** |
| DB driver | Npgsql | 10.0.3 |
| SQL | Dapper | 2.1.89 |
| Typed CRUD | Dapper.FSharp | 4.10.0 |
| Logging | Serilog + Sinks.File + Formatting.Compact | 4.4.0 / 7.0.0 / 3.0.0 |
| Config | Microsoft.Extensions.Configuration.Json + Binder | 10.0.14 |
| DB | PostgreSQL 17 (Docker) | 17.11 |

## Prerequisites

```bash
# PostgreSQL in Docker (port 5433 to avoid clashing with a local install)
docker run -d --name pg-scraping \
  -e POSTGRES_PASSWORD=scraping -e POSTGRES_DB=scraping \
  -p 5433:5432 -v D:/pgdata:/var/lib/postgresql/data \
  --restart unless-stopped postgres:17
```

`utils/yt-dlp.exe` must exist (already in the repo). `ffmpeg` is only needed if
you later add the audio/ASR fallback.

Connection string and paths live in [`appsettings.json`](appsettings.json).

## Commands

| Command | What it does |
|---|---|
| `scan` | long-running: queries → videos → transcripts, all state in PostgreSQL |
| `db` | check the connection and apply the schema |
| `videos` | list videos with URLs, state and transcript size |
| `hits` | pre-computed seed-term mentions with timestamps and deep links |
| `backfill` | recompute `hit` rows for transcripts that have none |
| `sql --query "..."` | run arbitrary SQL, print a table |
| `find --term "бессмертие"` | search the collected transcripts, print URLs + snippets |
| `find-bloggers` | one-shot channel discovery (no DB) |
| `channel-videos` / `shorts` / `playlist` / `thumbs` / `verify` | enumeration helpers |

### Viewing what was collected

```bash
# everything, newest first
dotnet run --project src/Scraping -- videos --limit 50

# only the ones with a transcript
dotnet run --project src/Scraping -- videos --state done

# filter by title
dotnet run --project src/Scraping -- videos --term "бессмерт"

# what did they actually say
dotnet run --project src/Scraping -- find --term "крионика" --limit 20

# pre-computed mentions, with a timestamp deep link
dotnet run --project src/Scraping -- hits --term "бессмертие"
```

`hits` reads the `hit` table, which is filled **at caption time** in
`Scan.runCaptionStage`: after the transcript is stored, `findHits` walks the
segments for every term in `HitTerms.all` and inserts the first mention with its
`tStartMs`. Videos processed before the `hit` table existed are covered by
`backfill` (no timestamps there — it works off the flat text, so `t=0`).

```bash
dotnet run --project src/Scraping -- backfill

# any SQL you like
dotnet run --project src/Scraping -- sql --query "select state, count(*) from video group by state"
```

`videos` prints `url | title | state | dur | chars`, so the URL is always the
first column — copy it straight into a browser.

Ad-hoc psql without leaving the console:

```bash
docker exec -it pg-scraping psql -U postgres -d scraping
```

### scan

```bash
dotnet run --project src/Scraping/Scraping.fsproj -- scan \
  --queries "крионика,квантовое бессмертие,загрузка сознания" \
  --pages 3 --limit 0 --delay 300
```

| Option | Default | Meaning |
|---|---|---|
| `--queries` | 10 seed queries | comma separated |
| `--pages` | 3 | search continuation pages per query |
| `--limit` | 0 | stop after N videos in the caption stage (0 = all) |
| `--delay` | 300 | pause between items, ms |

### find

```bash
dotnet run --project src/Scraping/Scraping.fsproj -- find --term "бессмерт" --limit 20
```

```
hits: 2
https://www.youtube.com/watch?v=ALLkzW_Yzb0	Квантовое бессмертие | Почему вы уже умирали сотни раз...
    ...Квантовое бессмертие. Теория, согласно которой ваше сознание физически не способно
    пережить собственную смерть. Каждый раз, когда вам угрожает гибель, вселенная расщепляется...
```

## How the transcript path works

1. `yt-dlp -4 --skip-download -J <url>` → metadata **and** freshly signed
   caption URLs (`automatic_captions.ru[].url`).
2. `HttpClient` fetches that URL as `json3`.
3. `events[].segs[].utf8` → text, `events[].tStartMs` → timestamps.

**`-4` is mandatory.** Without it the caption/audio endpoints time out on this
network (IPv6 route). With it: 31 KB of subtitles in under a second.

**Never use the `captionTracks` URL from the watch page.** It is stale and
returns `200` with a zero-byte body. Only the URL from a fresh `yt-dlp -J`
response works. This is the single most important gotcha in the whole project.

## Long-run behaviour

Everything is a queue in PostgreSQL, so the process can be killed at any moment:

```sql
query(id, text, state, results)          -- pending → running → done | failed
video(id, channel_id, title, duration, published, state, attempts, last_error, has_captions)
transcript(video_id, lang, source, text, chars)
hit(id, video_id, term, snippet, t_start, score)
```

Work is claimed with `FOR UPDATE SKIP LOCKED`, so several workers can run
against the same database without stepping on each other.

Progress is visible three ways:

- **live line** on stdout, redrawn with `\r`:
  `[11:28:18] q 1/1 | found 32 | new 32 | caps 2 | nocaps 0 | fail 0 | 0.1/s | now: <title>`
- **`work/progress.json`** heartbeat, rewritten after every item — an external
  watcher can tell "alive" from "hung"
- **`work/scraping.log`** — Serilog compact JSON, one event per line

Run it detached and watch without touching it:

```bash
Start-Process dotnet -ArgumentList "run --project src\Scraping -- scan" `
  -RedirectStandardOutput work\run.log -WindowStyle Hidden
Get-Content work\run.log -Tail 3 -Wait
Get-Content work\progress.json -Raw
```

## Layout

| File | Responsibility |
|---|---|
| [`Domain.fs`](Domain.fs) | records, `Profile`, `parseCount` |
| [`Settings.fs`](Settings.fs) | appsettings.json + Serilog |
| [`Http.fs`](Http.fs) | one `HttpClient`, `getString` / `postJson` / `tryGet*` |
| [`Json.fs`](Json.fs) | `unescape`, `tryGroup`, `splitBlocks`, `longestToken` |
| [`Db.fs`](Db.fs) | connection, DDL, table classes + `[<CLIMutable>]` rows, transcript search |
| [`YtDlp.fs`](YtDlp.fs) | `yt-dlp -4 -J` wrapper, caption-track extraction |
| [`Transcript.fs`](Transcript.fs) | caption fetch + `json3` parsing |
| [`YouTube.fs`](YouTube.fs) | search, channel/shorts/playlist enumeration, thumbnails |
| [`Rutube.fs`](Rutube.fs) | public JSON search API |
| [`BloggerFinder.fs`](BloggerFinder.fs) | one-shot discovery pipeline |
| [`Scan.fs`](Scan.fs) | queue stages, progress, heartbeat |
| [`Program.fs`](Program.fs) | CLI |

## Known limitations

- **Search caps at ~500–1000 results per query.** "Scan all of YouTube" is not
  achievable; the realistic model is query-space sampling + channel expansion +
  related-graph BFS.
- **Rutube has no subscriber count** in its public API; video count is a proxy.
- **`hitRatio` in `find-bloggers` is title-based** — a channel that is about
  immortality in substance but uses neutral titles will be misclassified.
- **No ASR yet.** Videos without auto-captions are marked `no_captions` and
  skipped. Adding `whisper.cpp` on the audio path is the next step.
