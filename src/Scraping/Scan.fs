namespace Scraping

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Dapper
open Serilog

type ScanOptions =
    { Queries: string list
      MaxPages: int
      Limit: int
      DelayMs: int }

/// Live counters for the progress line and the heartbeat file.
type Counters =
    { mutable QueriesDone: int
      mutable QueriesTotal: int
      mutable VideosFound: int
      mutable VideosNew: int
      mutable Captions: int
      mutable NoCaptions: int
      mutable Hits: int
      mutable Failed: int
      mutable Current: string
      mutable StartedAt: DateTime }

/// Long-running scan: query -> video -> transcript, all state in PostgreSQL so
/// the process can be killed at any moment and resumed with the same command.
module Scan =

    let private counters =
        { QueriesDone = 0
          QueriesTotal = 0
          VideosFound = 0
          VideosNew = 0
          Captions = 0
          NoCaptions = 0
          Hits = 0
          Failed = 0
          Current = ""
          StartedAt = DateTime.UtcNow }

    let private heartbeatPath = Path.Combine(Settings.workDir, "progress.json")

    let private render () =
        let c = counters
        let elapsed = max 0.001 (DateTime.UtcNow - c.StartedAt).TotalSeconds
        let rate = float c.Captions / elapsed

        let line =
            sprintf
                "[%s] q %d/%d | found %d | new %d | caps %d | nocaps %d | hits %d | fail %d | %.1f/s | now: %s"
                (DateTime.Now.ToString "HH:mm:ss")
                c.QueriesDone
                c.QueriesTotal
                c.VideosFound
                c.VideosNew
                c.Captions
                c.NoCaptions
                c.Hits
                c.Failed
                rate
                c.Current

        Console.Write("\r" + line.PadRight(160))

    let private heartbeat () =
        try
            let c = counters
            let elapsed = max 0.001 (DateTime.UtcNow - c.StartedAt).TotalSeconds

            let payload =
                {| ts = DateTime.UtcNow.ToString "o"
                   pid = Environment.ProcessId
                   queries_done = c.QueriesDone
                   queries_total = c.QueriesTotal
                   videos_found = c.VideosFound
                   videos_new = c.VideosNew
                   captions = c.Captions
                   no_captions = c.NoCaptions
                   hits = c.Hits
                   failed = c.Failed
                   rate = Math.Round(float c.Captions / elapsed, 2)
                   current = c.Current |}

            Directory.CreateDirectory Settings.workDir |> ignore
            File.WriteAllText(heartbeatPath, JsonSerializer.Serialize payload)
        with e ->
            Log.Warning(e, "heartbeat failed")

    // ------------------------------------------------------------ queue

    /// Claims a pending query, or retries a failed one that still has attempts
    /// left. `attempts` is bumped at claim time, so a crash mid-query also
    /// counts as an attempt.
    let private claimQuery (conn: Npgsql.NpgsqlConnection) =
        conn.QuerySingleOrDefault<Query_row>(
            """
update query set state = 'running', attempts = attempts + 1, updated_at = now()
where id = (
    select id from query
    where state = 'pending' or (state = 'failed' and attempts < 3)
    order by id limit 1 for update skip locked
)
returning id, text, state, results
"""
        )

    let private claimVideo (conn: Npgsql.NpgsqlConnection) =
        conn.QuerySingleOrDefault<Video_row>(
            """
update video set state = 'running', updated_at = now()
where id = (
    select id from video where state = 'pending' order by updated_at limit 1 for update skip locked
)
returning id, channel_id, title, duration, published, state, attempts, last_error, has_captions
"""
        )

    let private parseUploadDate (s: string) =
        match DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, d -> Nullable d
        | _ -> Nullable()

    // ------------------------------------------------------------ stages

    let private enqueueQueries (queries: string list) =
        use conn = Db.openConnection()

        for q in queries do
            conn.Execute(
                "insert into query (text) values (@text) on conflict (text) do nothing",
                {| text = q |}
            )
            |> ignore

        counters.QueriesTotal <-
            conn.ExecuteScalar<int64>("select count(*) from query where state = 'pending'") |> int

    let private runSearchStage (options: ScanOptions) =
        task {
            let mutable go = true

            while go do
                use conn = Db.openConnection()
                let q = claimQuery conn

                if isNull (box q) then
                    go <- false
                else
                    counters.Current <- sprintf "search: %s" q.text
                    render ()

                    try
                        let! found = YouTube.search q.text options.MaxPages
                        counters.VideosFound <- counters.VideosFound + found.Length

                        let mutable added = 0

                        for v in found do
                            let n =
                                conn.Execute(
                                    """
insert into video (id, channel_id, title, duration, published, state)
values (@id, @channel_id, @title, 0, null, 'pending')
on conflict (id) do nothing
""",
                                    {| id = v.Id
                                       channel_id = v.ChannelId
                                       title = v.Title |}
                                )

                            added <- added + n

                        counters.VideosNew <- counters.VideosNew + added

                        conn.Execute(
                            "update query set state = 'done', results = @n, updated_at = now() where id = @id",
                            {| n = found.Length; id = q.id |}
                        )
                        |> ignore

                        Log.Information(
                            "query done: {Query} -> {Found} found, {Added} new",
                            q.text,
                            found.Length,
                            added
                        )
                    with e ->
                        Log.Error(e, "query failed: {Query}", q.text)

                        conn.Execute(
                            """
update query set
    state = case when attempts >= 3 then 'failed' else 'pending' end,
    updated_at = now()
where id = @id
""",
                            {| id = q.id |}
                        )
                        |> ignore

                    counters.QueriesDone <- counters.QueriesDone + 1
                    render ()
                    heartbeat ()
                    do! Http.delay options.DelayMs
        }

    /// Position of the first stem that is actually present.
    let private firstIndex (low: string) (stems: string list) =
        stems |> List.map low.IndexOf |> List.filter (fun i -> i >= 0) |> List.min

    /// First mention of each seed term, with a snippet and the timestamp.
    let private findHits (segments: TranscriptSegment list) =
        [ for (display, stems) in HitTerms.all do
              match
                  segments
                  |> List.tryFind (fun s ->
                      let low = s.Text.ToLowerInvariant()
                      stems |> List.forall low.Contains)
              with
              | Some seg ->
                  let low = seg.Text.ToLowerInvariant()
                  let idx = firstIndex low stems
                  let start = max 0 (idx - 80)
                  let len = min (seg.Text.Length - start) 220
                  yield display, seg.Text.Substring(start, len), seg.StartMs
              | None -> () ]

    /// Same as findHits, but over a flat transcript where timestamps are gone.
    let hitsFromText (text: string) =
        let low = text.ToLowerInvariant()

        [ for (display, stems) in HitTerms.all do
              if stems |> List.forall low.Contains then
                  let idx = firstIndex low stems
                  let start = max 0 (idx - 80)
                  let len = min (text.Length - start) 220
                  yield display, text.Substring(start, len), 0 ]

    let private runCaptionStage (options: ScanOptions) =
        task {
            let exe = YtDlp.resolvePath Settings.ytDlpPath
            let mutable processed = 0
            let mutable go = true

            while go && (options.Limit <= 0 || processed < options.Limit) do
                use conn = Db.openConnection()
                let v = claimVideo conn

                if isNull (box v) then
                    go <- false
                else
                    counters.Current <- sprintf "%s  %s" v.id v.title
                    render ()

                    let! meta = YtDlp.fetchMeta exe v.id

                    match meta with
                    | Error reason ->
                        counters.Failed <- counters.Failed + 1
                        let attempts = v.attempts + 1
                        let state = if attempts >= 3 then "failed" else "pending"

                        conn.Execute(
                            """
update video set state = @state, attempts = @attempts, last_error = @err, updated_at = now()
where id = @id
""",
                            {| state = state
                               attempts = attempts
                               err = reason
                               id = v.id |}
                        )
                        |> ignore

                        Log.Warning("meta failed ({Attempts}): {Video} — {Reason}", attempts, v.id, reason)
                    | Ok m ->
                        let! transcript = Transcript.fetch m.Captions

                        match transcript with
                        | Ok segments ->
                            let text = Transcript.toText segments

                            conn.Execute(
                                """
insert into transcript (video_id, lang, source, text, chars)
values (@video_id, 'ru', 'captions', @text, @chars)
on conflict (video_id) do update
set text = excluded.text, chars = excluded.chars, fetched_at = now()
""",
                                {| video_id = v.id
                                   text = text
                                   chars = text.Length |}
                            )
                            |> ignore

                            let hits = findHits segments
                            Db.insertHits v.id hits
                            counters.Hits <- counters.Hits + hits.Length

                            conn.Execute(
                                """
update video set state = 'done', has_captions = true, attempts = attempts + 1,
       last_error = null, duration = @duration, published = @published, updated_at = now()
where id = @id
""",
                                {| duration = m.Duration
                                   published = parseUploadDate m.UploadDate
                                   id = v.id |}
                            )
                            |> ignore

                            counters.Captions <- counters.Captions + 1
                            Log.Information("captions: {Video} {Chars} chars", v.id, text.Length)
                        | Error reason ->
                            counters.NoCaptions <- counters.NoCaptions + 1

                            conn.Execute(
                                """
update video set state = 'no_captions', has_captions = false, attempts = attempts + 1,
       last_error = @err, duration = @duration, updated_at = now()
where id = @id
""",
                                {| err = reason
                                   duration = m.Duration
                                   id = v.id |}
                            )
                            |> ignore

                            Log.Information("no captions: {Video} — {Reason}", v.id, reason)

                    processed <- processed + 1
                    render ()
                    heartbeat ()
                    do! Http.delay options.DelayMs
        }

    // ------------------------------------------------------------ entry

    let run (options: ScanOptions) : Task<unit> =
        task {
            counters.StartedAt <- DateTime.UtcNow
            enqueueQueries options.Queries
            Log.Information("queued queries, pending: {Count}", counters.QueriesTotal)

            do! runSearchStage options
            do! runCaptionStage options

            Console.WriteLine()
            heartbeat ()

            Log.Information(
                "scan finished: queries {Q}, found {Found}, new {New}, captions {Caps}, no captions {NoCaps}, failed {Failed}",
                counters.QueriesDone,
                counters.VideosFound,
                counters.VideosNew,
                counters.Captions,
                counters.NoCaptions,
                counters.Failed
            )
        }
