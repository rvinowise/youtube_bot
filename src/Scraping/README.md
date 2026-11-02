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
           a term is `+`-joined word-boundary patterns:
             "бессмерт"    -> \bбессмерт\w*       "бессмертие", not "обессмертить"
             "вечн+жизн"   -> \bвечн\w*\W+жизн\w* "вечная жизнь", not "лететь вечно"

frontier   the queue, one per topic: (topic_id, video_id, depth, parent_id, state, priority)
           queued -> running -> expanded | failed
           `priority` = parent's priority + parent's hit count, so this is a
           best-first walk, not a blind BFS

video      global visited set: every video ever seen, with depth, parent,
           title, channel_name, channel_url
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
| `channels` | hits grouped by channel — the actual "who to talk to" list |
| `backfill-subs` | fill subscriber counts from channel pages |
| `videos` | every visited video with URL, state, depth, transcript size |
| `find` | ad-hoc search over the collected transcripts |
| `backfill` | recompute hits for transcripts that have none |
| `backfill-titles` | fill missing titles/channels via the public oEmbed endpoint |
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
| `--max-per-channel` | 0 | stop expanding a channel after N videos (0 = no cap) |
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

## Excluded words, surprise ranking, origin

A hit can be technically on-topic and still useless — a religious sermon, a
conspiracy video, a factory channel name-dropping the word once. A topic
therefore has an **exclude** list, using exactly the same `+`-joined
word-boundary syntax as the needed terms:

```bash
dotnet run --project src/Scraping -- scan --topic immortality ^
  --exclude "бог,господ,церк,библи,молитв,свящ,христ,коран" --seed-video XXX
```

If a needed term matches but any excluded term also matches the same segment
(or the snippet window for text-only scoring), the hit is dropped. The list is
stored on the topic (`topic.exclude`), so `backfill --topic X --reset`
re-applies it retroactively. Empty by default: religion is a distraction in
many cases but some believers are allies, so the policy is yours.

`channels --sort surprise` ranks by how **rare** the mention is inside the
channel's own body of work — 1 mention in 127 videos outranks 3 in 3:

```
channel                            subs     videos  of   mentions
Центральное Телевидение            5010000  2       127  2
ИСТОРИЯ НА КАРТЕ                   117000   1       31   1
Ходорковский LIVE                  3400000  1       28   1
```

Every video records `origin` — the node name (`node` in appsettings, defaults
to the machine name) that discovered it. It survives a merge, so after merging
several databases you can still tell which machine found a lead.

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

## Who to talk to

`hits` answers "which videos mention it"; `channels` answers "which *people*
mention it", which is the actual goal:

```bash
dotnet run --project src/Scraping -- channels --topic immortality --limit 25
```

```
channel              videos  mentions  d  url                                  sample
Сонный Ученый        22      23        1  https://youtube.com/@сонный-ученый    Если законы физики работают в обе стороны…
Глеб Соломин         4       10        3  https://youtube.com/@glebsolomin      ИИ ЗАБЕРЁТ твою работу к 2027…
```

A channel with 3 mentions in 3 different videos is a much better lead than a
factory channel with 1 mention in 1 video — but the factory will dominate the
raw counts, which is why `--max-per-channel` exists: after N videos of the same
channel the scanner still scores them, but stops walking that channel's
neighbourhood. `--max-per-channel 5` is a good default for a focused run.

## Editing the vocabulary (important)

`topic.stems` is the *filter*, not the history. Concretely:

- Changing or deleting a term does **not** remove videos from the frontier and
  does **not** stop them being part of the topic: visited videos are a trace of
  where the crawl went, independent of what you are looking for.
- Hits already stored were computed with the *old* vocabulary and stay in the
  `hit` table, so `hits` / `channels` keep showing terms you just deleted.
- `backfill` alone will not fix that: it only fills transcripts that have **no
  hits at all**, which is why 6 000 transcripts were silently skipped before
  v0.6.

So after any vocabulary change:

```bash
dotnet run --project src/Scraping -- backfill --topic immortality --reset
```

`--reset` deletes the topic's hits first (nothing else — transcripts, frontier
and videos stay), then re-scores every stored transcript with the new terms.
It is offline: 6 000 transcripts take about a minute, no network.

`--stems "a,b+c"` on `scan` now also updates an existing topic; before, the flag
was silently ignored for any topic that already existed.

## Titles and channels

`hits` and `videos` show the channel name next to the title, because a title
alone does not tell you whether a video is a vlogger who mentioned immortality
once or a generic popular-science channel:

```
url                                    at        d  term       channel            title
https://youtu.be/vKQsrc4W9l4?t=7377    2:02:57   3  крионик    Сонный Ученый      ...
```

Titles come from `yt-dlp -J` at scan time. Videos queued before that was
implemented are filled by `backfill-titles`, which uses YouTube's public oEmbed
endpoint (`/oembed?url=...&format=json`) — no cookies, no bot check, ~200 ms per
video, so ~8 000 videos take about 40 minutes:

```bash
dotnet run --project src/Scraping -- backfill-titles --delay 100
```

oEmbed returns `title`, `author_name` and `author_url` (a `@handle` URL, not a
`UC...` id), so `channel_url` is stored as-is and `channel_id` stays empty for
backfilled rows. Videos that are gone return 404 and are counted as failures.

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
- **Terms are literal.** Matching is word-boundary + stem + any ending, so
  `бессмерт` still catches metaphors ("бессмертная память"). It no longer
  catches "обессмертить" (different word start). `вечн+жизн` requires the two
  words to be **adjacent**, so "вечная, светлая жизнь" is missed on purpose.
- **No ASR.** Videos without auto-captions are marked `no_captions` and skipped
  (but still expanded). Adding `whisper.cpp` on the audio path is the next step.
