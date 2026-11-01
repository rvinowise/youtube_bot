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
| `requeue` | put failed / caption-less videos of a topic back in the queue |
| `sql` | run arbitrary SQL, print a table |
| `db` | check the connection and apply the schema |
| `find-bloggers` / `channel-videos` / `shorts` / `playlist` / `thumbs` / `verify` | one-shot helpers, no DB |

### scan

```bash
dotnet run --project src/Scraping -- scan \
  --topic immortality \
  --seed-video ALLkzW_Yzb0 \
  --related 20 --delay 3000
```

| Option | Default | Meaning |
|---|---|---|
| `--topic` | — | topic name (required) |
| `--stems` | built-in immortality vocabulary | `"a,b+c,d"`; `+` = all stems in one segment |
| `--seed-video` | — | starting video(s), comma separated, idempotent |
| `--related` | 20 | recommendations taken per video |
| `--delay` | 2000 | pause between videos, ms; below ~1 s YouTube answers 429 |
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

Run detached (survives closing the terminal — the parent shell exits and the
scanner is orphaned, so keep the machine awake):

```powershell
Start-Process -FilePath "src\Scraping\bin\Release\net10.0\Scraping.exe" `
  -ArgumentList 'scan','--topic','immortality','--seed-video','KES9yb3o-4Q','--related','20','--delay','4000' `
  -WorkingDirectory "C:\prj\browsing" `
  -RedirectStandardOutput "C:\prj\browsing\work\night_out.log" `
  -RedirectStandardError  "C:\prj\browsing\work\night_err.log" `
  -WindowStyle Hidden
Get-Process Scraping                      # pid, CPU
Get-Content work\progress.json -Raw       # counters
Stop-Process -Name Scraping               # stop it
```

Build once first with `dotnet build src\Scraping\Scraping.fsproj -c Release`; the
`bin\Release\...\Scraping.exe` path skips the `dotnet run` build step.

Alternate foreground form:

```bash
Start-Process dotnet -ArgumentList "run --project src\Scraping -- scan --topic immortality --seed-video XXX" `
  -RedirectStandardOutput work\run.log -WindowStyle Hidden
Get-Content work\run.log -Tail 3 -Wait
```

## How the transcript path works

1. `yt-dlp -4 --cookies-from-browser firefox --ignore-no-formats-error
   --skip-download -J <url>` → metadata **and** freshly signed caption URLs
   (`automatic_captions.ru[].url`).
2. `HttpClient` fetches that URL as `json3`.
3. `events[].segs[].utf8` → text, `events[].tStartMs` → timestamps.

**`-4` is mandatory.** Without it the caption/audio endpoints time out on this
network (IPv6 route).

**`--ignore-no-formats-error` is mandatory.** YouTube's `n` challenge makes
yt-dlp drop every real format (solving it needs a JS runtime, see EJS in the
yt-dlp wiki), so a plain `-J` exits non-zero with `Requested format is not
available` even though `automatic_captions` is fully populated. We never
download media — only caption URLs — so missing formats are irrelevant.

`visionos` / `ios` / `tv` clients return **storyboard-only** formats and an
empty `automatic_captions`. Do not set `--extractor-args
youtube:player_client=...` unless you want no captions; the default client with
cookies returns all 150+ caption languages.

**Never use the `captionTracks` URL from the watch page.** It is stale and
returns `200` with a zero-byte body. Only the URL from a fresh `yt-dlp -J`
response works.

## Failure handling

| Situation | Behaviour |
|---|---|
| 429 / 5xx / network on the caption URL | `Transient` → backoff, up to 5 attempts |
| "Sign in to confirm you're not a bot" | `Transient` → backoff |
| private, removed, age-gated, members-only | `Permanent` → video `failed`, frontier `expanded` |
| No caption track at all | video `no_captions`, **still expanded** — it is a valid graph node |

A video without captions is still walked: the whole point is the graph, not the
transcript.

Transient failures back off exponentially (`attempts² × 30 s` → 30 s, 2 min,
4.5 min, 8 min) instead of hammering the same video. While everything queued is
waiting out its backoff, the scanner prints `backoff, N queued` and keeps the
process alive for up to 15 minutes.

When a video that already has a cached transcript is reached again, it is
marked `done` without another yt-dlp call.

## Getting past YouTube

The working recipe (checked against a live scan: 0 failures, 4/4 videos):

```json
"yt_dlp": {
  "path": "utils/yt-dlp.exe",
  "cookies_from_browser": "firefox",
  "extra_args": "--ignore-no-formats-error"
}
```

Equivalent raw command:

```bash
utils/yt-dlp.exe -4 --cookies-from-browser firefox --ignore-no-formats-error \
  --no-warnings --skip-download -J "https://www.youtube.com/watch?v=ALLkzW_Yzb0"
```

- **Cookies defeat the bot check.** `firefox` works here; `chrome` fails with
  "Could not copy Chrome cookie database" while Chrome is running.
- **Rate limit is real.** With `--delay 300` YouTube starts answering
  `429 Too Many Requests` after ~5 videos. `--delay 2000` or more is stable.
- If three videos in a row fail transiently, the scanner pauses **5 minutes**
  globally and logs `rate limit: cooling down 5 min`, then resumes. Without
  this an unattended night run gets nothing done once the IP is throttled.
- If the bot check returns anyway, wait, or use a proxy
  (`yt_dlp.extra_args: "--ignore-no-formats-error --proxy http://host:port"`).
- Videos that failed during an earlier outage are not retried automatically —
  run `requeue --topic immortality` (add `--with-no-captions` to also retry the
  caption-less ones).

`yt_dlp.extra_args` is a raw string appended to every yt-dlp call, so any flag
works without a code change. `--cookies-from-browser` and
`--ignore-no-formats-error` are always passed by [`YtDlp.fs`](YtDlp.fs).

## Asking another model for help

[`tools/ask.ps1`](../../tools/ask.ps1) posts a question to OpenRouter (default
`google/gemini-2.5-flash`) and prints the answer. Handy for cryptic yt-dlp
errors and similar:

```bash
powershell -NoProfile -File tools\ask.ps1 -Prompt "why does yt-dlp say 'n challenge solving failed'?"
powershell -NoProfile -File tools\ask.ps1 -PromptFile work\err.txt -Files src\Scraping\YtDlp.fs
Get-Content work\err.txt | powershell -NoProfile -File tools\ask.ps1 -System "You are a DevOps expert."
```

The key is read from `-ApiKey`, `$env:OPENROUTER_API_KEY` or
`tools/openrouter.key` (git-ignored). Other useful flags: `-Model`, `-Out file`,
`-Json`, `-Temperature`.

Model answers are a hint, not a source of truth: asked about the `n` challenge
above, Gemini suggested a Playwright stealth extractor, while the actual fix was
`--ignore-no-formats-error` + cookies.

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
- **~3–5 s per video** (`-J` with cookies + captions + watch page), so
  ~800–1 200 videos/hour at `--delay 2000`.
- **No ASR.** Videos without auto-captions are marked `no_captions` and skipped
  (but still expanded). Adding `whisper.cpp` on the audio path is the next step.
