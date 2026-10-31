# Scraping — endless BFS over YouTube's recommendation graph

Walks the "related videos" graph from a seed video, forever, storing every
visited video as a trace and every keyword mention as a result. State lives in
PostgreSQL, so the process can be killed at any moment and resumed with the same
command.

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
docker run -d --name pg-scraping \
  -e POSTGRES_PASSWORD=scraping -e POSTGRES_DB=scraping \
  -p 5433:5432 -v D:/pgdata:/var/lib/postgresql/data \
  --restart unless-stopped postgres:17
```

`utils/yt-dlp.exe` must exist (already in the repo, gitignored — grab it from
the yt-dlp releases page if missing). Connection string and paths live in
[`appsettings.json`](appsettings.json).

## The model

```
topic      a named set of terms, e.g. "immortality"
           terms are `+`-joined stems: "загрузк+сознани" = both in one segment

frontier   the BFS queue, one per topic: (topic_id, video_id, depth, parent_id, state)
           queued -> running -> expanded | failed

video      global visited set: every video ever seen, with depth and parent
transcript global cache: fetched once, reused by every topic
hit        per topic: (topic_id, video_id, term, snippet, t_start)
```

A video reached by two topics is fetched once and scored twice. The frontier is
per topic, so two topics explore independently.

## Commands

| Command | What it does |
|---|---|
| `scan` | endless BFS: claim frontier → transcript → hits → expand related |
| `topics` | topics with queue, expanded, video and hit counts |
| `hits` | pre-computed term mentions with timestamps and deep links |
| `videos` | every visited video with URL, state, depth, transcript size |
| `find` | ad-hoc search over the collected transcripts |
| `backfill` | recompute hits for transcripts that have none |
| `sql` | run arbitrary SQL, print a table |
| `db` | check the connection and apply the schema |
| `find-bloggers` / `channel-videos` / `shorts` / `playlist` / `thumbs` / `verify` | one-shot helpers, no DB |

### scan

```bash
dotnet run --project src/Scraping -- scan \
  --topic immortality \
  --seed-video ALLkzW_Yzb0 \
  --related 20 --delay 300
```

| Option | Default | Meaning |
|---|---|---|
| `--topic` | — | topic name (required) |
| `--stems` | built-in immortality vocabulary | `"a,b+c,d"`; `+` = all stems in one segment |
| `--seed-video` | — | starting video(s), comma separated, idempotent |
| `--related` | 20 | recommendations taken per video |
| `--delay` | 300 | pause between videos, ms |
| `--limit` | 0 | stop after N expanded videos (0 = never) |

There is **no depth limit**. The frontier grows by ~20 per expanded video, so it
never empties on its own — you stop it yourself, and the next run continues.

### Watching it

```bash
dotnet run --project src/Scraping -- topics
dotnet run --project src/Scraping -- hits --topic immortality
dotnet run --project src/Scraping -- videos --state done
```

Live progress line (redrawn with `\r`):

```
[04:53:39] immortality | depth 1 | expanded 2 | queued 12 | caps 0 | nocaps 0 | hits 0 | fail 2 | 0.23/s | now: d1 U8ppEyVmLpc
```

Plus `work/progress.json` (heartbeat after every video) and
`work/scrapingYYYYMMDD.log` (Serilog compact JSON).

Run detached:

```bash
Start-Process dotnet -ArgumentList "run --project src\Scraping -- scan --topic immortality --seed-video XXX" `
  -RedirectStandardOutput work\run.log -WindowStyle Hidden
Get-Content work\run.log -Tail 3 -Wait
```

## How the transcript path works

1. `yt-dlp -4 --skip-download -J <url>` → metadata **and** freshly signed
   caption URLs (`automatic_captions.ru[].url`).
2. `HttpClient` fetches that URL as `json3`.
3. `events[].segs[].utf8` → text, `events[].tStartMs` → timestamps.

**`-4` is mandatory.** Without it the caption/audio endpoints time out on this
network (IPv6 route).

**Never use the `captionTracks` URL from the watch page.** It is stale and
returns `200` with a zero-byte body. Only the URL from a fresh `yt-dlp -J`
response works.

## Failure handling

| Situation | Behaviour |
|---|---|
| 429 / 5xx / network on the caption URL | `Transient` → frontier retries, up to 3 attempts |
| "Sign in to confirm you're not a bot" | `Transient` → retried later |
| "Requested format is not available", private, removed, age-gated | `Permanent` → video marked `failed`, frontier `expanded` |
| No caption track at all | video marked `no_captions`, **still expanded** — it is a valid graph node |

A video without captions is still walked: the whole point is the graph, not the
transcript.

## Bot detection

YouTube escalates to "Sign in to confirm you're not a bot" after a few hundred
requests from one IP. Options, in order of preference:

1. **Wait.** The flag decays over hours.
2. **Cookies.** Set `yt_dlp.cookies_from_browser` in `appsettings.json` to
   `"firefox"`, `"chrome"` or `"edge"`. Note: on this machine Chrome's cookie DB
   is locked while Chrome runs, and with cookies yt-dlp switched to clients that
   returned only storyboard formats — so cookies are not a free win here.
3. **Proxy.** `yt_dlp.extra_args`: `"--proxy http://host:port"`.
4. **po_token.** `bgutil-ytdlp-pot-provider` plugin + a Node sidecar.

`yt_dlp.extra_args` is a raw string appended to every yt-dlp call, so any flag
works without a code change.

## Layout

| File | Responsibility |
|---|---|
| [`Domain.fs`](Domain.fs) | records, `Profile`, `HitTerms` (`+` conjunction) |
| [`Settings.fs`](Settings.fs) | appsettings.json + Serilog |
| [`Http.fs`](Http.fs) | one `HttpClient`, `getString` / `getStringStatus` / `postJson` |
| [`Json.fs`](Json.fs) | `unescape`, `tryGroup`, `splitBlocks`, `longestToken` |
| [`Db.fs`](Db.fs) | schema, topic/frontier/video/transcript/hit operations |
| [`YtDlp.fs`](YtDlp.fs) | `yt-dlp -4 -J` wrapper, caption tracks, error classification |
| [`Transcript.fs`](Transcript.fs) | caption fetch + `json3` parsing, `NoCaptions` vs `Transient` |
| [`YouTube.fs`](YouTube.fs) | search, channel/shorts/playlist enumeration, **relatedVideos** |
| [`Rutube.fs`](Rutube.fs) | public JSON search API |
| [`BloggerFinder.fs`](BloggerFinder.fs) | one-shot discovery pipeline |
| [`Scan.fs`](Scan.fs) | the BFS loop, progress, heartbeat |
| [`Program.fs`](Program.fs) | CLI |

## Known limitations

- **Recommendations are not reproducible.** The same video yields different
  related lists over time and by region. "Resume" continues from what is already
  in the frontier, not from a deterministic graph.
- **Branching is ~20×.** Depth 3 from one seed is ~8 000 videos, depth 4
  ~160 000. Watch `queued` in the progress line.
- **~2–3 s per video** (`-J` + captions + watch page), so ~1 200 videos/hour.
- **No ASR.** Videos without auto-captions are marked `no_captions` and skipped
  (but still expanded). Adding `whisper.cpp` on the audio path is the next step.
