namespace Scraping

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Serilog

type ScanOptions =
    { Topic: string
      Stems: string list
      SeedVideos: string list
      RelatedLimit: int
      DelayMs: int
      /// Stop after N expanded videos. 0 = run until the frontier is empty
      /// (which, with a live recommendation graph, means "until you kill it").
      Limit: int }

/// Live counters for the progress line and the heartbeat file.
type Counters =
    { mutable Topic: string
      mutable Depth: int
      mutable Expanded: int
      mutable Queued: int
      mutable Captions: int
      mutable NoCaptions: int
      mutable Hits: int
      mutable Failed: int
      mutable Current: string
      mutable StartedAt: DateTime }

/// Endless breadth-first walk over YouTube's recommendation graph.
///
/// The queue lives in PostgreSQL (`frontier`, one per topic), so the process can
/// be killed at any moment and resumed with the same command: it picks up the
/// shallowest queued video and keeps going. There is no depth limit — you stop
/// it yourself.
module Scan =

    let private counters =
        { Topic = ""
          Depth = 0
          Expanded = 0
          Queued = 0
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
        let rate = float c.Expanded / elapsed

        let line =
            sprintf
                "[%s] %s | depth %d | expanded %d | queued %d | caps %d | nocaps %d | hits %d | fail %d | %.2f/s | now: %s"
                (DateTime.Now.ToString "HH:mm:ss")
                c.Topic
                c.Depth
                c.Expanded
                c.Queued
                c.Captions
                c.NoCaptions
                c.Hits
                c.Failed
                rate
                c.Current

        Console.Write("\r" + line.PadRight(170))

    let private heartbeat () =
        try
            let c = counters
            let elapsed = max 0.001 (DateTime.UtcNow - c.StartedAt).TotalSeconds

            let payload =
                {| ts = DateTime.UtcNow.ToString "o"
                   pid = Environment.ProcessId
                   topic = c.Topic
                   depth = c.Depth
                   expanded = c.Expanded
                   queued = c.Queued
                   captions = c.Captions
                   no_captions = c.NoCaptions
                   hits = c.Hits
                   failed = c.Failed
                   rate = Math.Round(float c.Expanded / elapsed, 2)
                   current = c.Current |}

            Directory.CreateDirectory Settings.workDir |> ignore
            File.WriteAllText(heartbeatPath, JsonSerializer.Serialize payload)
        with e ->
            Log.Warning(e, "heartbeat failed")

    // ------------------------------------------------------------ hits

    let private hitsFromSegments (stems: string list) (segments: TranscriptSegment list) =
        [ for term in stems do
              match
                  segments
                  |> List.tryFind (fun s -> HitTerms.matches (s.Text.ToLowerInvariant()) term)
              with
              | Some seg ->
                  let low = seg.Text.ToLowerInvariant()
                  let idx = HitTerms.firstIndex low term
                  let start = max 0 (idx - 80)
                  let len = min (seg.Text.Length - start) 220
                  yield term, seg.Text.Substring(start, len), seg.StartMs
              | None -> () ]

    /// Same, but over a flat transcript where timestamps are gone.
    let hitsFromText (stems: string list) (text: string) =
        let low = text.ToLowerInvariant()

        [ for term in stems do
              if HitTerms.matches low term then
                  let idx = HitTerms.firstIndex low term
                  let start = max 0 (idx - 80)
                  let len = min (text.Length - start) 220
                  yield term, text.Substring(start, len), 0 ]

    // ------------------------------------------------------------ one video

    let private parseUploadDate (s: string) =
        match DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, d -> Nullable d
        | _ -> Nullable()

    /// Fetch the transcript unless another topic already did.
    /// `Ok None` means "already had it, no segments available".
    let private ensureTranscript
        (exe: string)
        (videoId: string)
        : Task<Result<TranscriptSegment list option, TranscriptError>> =
        task {
            let existing = Db.getTranscript videoId

            if not (isNull (box existing)) then
                Db.markVideoCached videoId
                return Ok None
            else
                let! meta = YtDlp.fetchMeta exe videoId

                match meta with
                | Error reason when YtDlp.isPermanentError reason ->
                    // gone / private / age-gated: retrying will never help
                    Db.markVideoFailed videoId reason
                    return Error(NoCaptions reason)
                | Error reason ->
                    // bot check, 429, network: leave the row alone and let the
                    // frontier retry it later
                    return Error(Transient reason)
                | Ok m ->
                    let! transcript = Transcript.fetch m.Captions

                    match transcript with
                    | Ok segments ->
                        Db.saveTranscript videoId (Transcript.toText segments)
                        Db.markVideoDone videoId m.Duration (parseUploadDate m.UploadDate)
                        return Ok(Some segments)
                    | Error(NoCaptions reason) ->
                        Db.markVideoNoCaptions videoId reason m.Duration
                        return Error(NoCaptions reason)
                    | Error(Transient reason) -> return Error(Transient reason)
        }

    let private processItem (exe: string) (topicId: int64) (stems: string list) (options: ScanOptions) (item: Frontier_row) =
        task {
            counters.Current <- sprintf "d%d %s" item.depth item.video_id
            counters.Depth <- item.depth
            render ()

            let! transcript = ensureTranscript exe item.video_id

            let hits =
                match transcript with
                | Ok(Some segments) ->
                    counters.Captions <- counters.Captions + 1
                    hitsFromSegments stems segments
                | Ok None ->
                    let stored = Db.getTranscript item.video_id

                    if isNull (box stored) then [] else hitsFromText stems stored.text
                | Error(NoCaptions _) ->
                    counters.NoCaptions <- counters.NoCaptions + 1
                    []
                | Error(Transient _) -> []

            if not hits.IsEmpty then
                Db.insertHits topicId item.video_id hits
                counters.Hits <- counters.Hits + hits.Length

                Log.Information(
                    "hits: {Video} depth {Depth} -> {Terms}",
                    item.video_id,
                    item.depth,
                    hits |> List.map (fun (t, _, _) -> t) |> String.concat ", "
                )

            // Expand regardless of the transcript outcome: a video without
            // captions is still a node in the recommendation graph, and the
            // whole point is to keep walking.
            let! related = YouTube.relatedVideos item.video_id options.RelatedLimit

            for (id, _title) in related do
                Db.enqueue topicId id (item.depth + 1) item.video_id "related"

            match transcript with
            | Error(Transient reason) ->
                counters.Failed <- counters.Failed + 1
                Db.markFrontierRetry topicId item.video_id item.attempts
                Log.Warning("transient: {Video} depth {Depth} — {Reason}", item.video_id, item.depth, reason)
            | _ ->
                Db.markExpanded topicId item.video_id

                Log.Information(
                    "expanded: {Video} depth {Depth} -> {Related} related",
                    item.video_id,
                    item.depth,
                    related.Length
                )
        }

    // ------------------------------------------------------------ entry

    let run (options: ScanOptions) : Task<unit> =
        task {
            counters.StartedAt <- DateTime.UtcNow
            counters.Topic <- options.Topic

            let topicId = Db.ensureTopic options.Topic options.Stems
            let stems = Db.topicStems topicId

            Log.Information(
                "topic {Topic} (id {Id}), terms: {Stems}",
                options.Topic,
                topicId,
                String.concat ", " stems
            )

            // crash recovery: whatever was mid-flight goes back to the queue
            Db.resetRunning topicId

            for videoId in options.SeedVideos do
                Db.enqueue topicId videoId 0 null "seed"
                Log.Information("seed: {Video}", videoId)

            counters.Queued <- Db.queuedCount topicId
            Console.WriteLine(sprintf "topic: %s | queued: %d" options.Topic counters.Queued)

            let exe = YtDlp.resolvePath Settings.ytDlpPath
            let mutable go = true
            let mutable idleRounds = 0
            let mutable consecutiveFailures = 0

            while go && (options.Limit <= 0 || counters.Expanded < options.Limit) do
                let item = Db.claimFrontier topicId

                if isNull (box item) then
                    // Everything queued is waiting out its backoff. Keep the
                    // process alive for a while instead of reporting "done",
                    // but do not spin forever if the queue is stuck.
                    let queued = Db.queuedCount topicId

                    if queued > 0 && idleRounds < 60 then
                        idleRounds <- idleRounds + 1
                        counters.Queued <- queued
                        counters.Current <- sprintf "backoff, %d queued" queued
                        render ()
                        heartbeat ()
                        do! Http.delay 15000
                    else
                        go <- false
                else
                    idleRounds <- 0
                    let failedBefore = counters.Failed
                    do! processItem exe topicId stems options item
                    counters.Expanded <- counters.Expanded + 1
                    counters.Queued <- Db.queuedCount topicId
                    render ()
                    heartbeat ()

                    if counters.Failed > failedBefore then
                        consecutiveFailures <- consecutiveFailures + 1

                        if consecutiveFailures >= 3 then
                            // Global rate limit: happening one video after
                            // another with a per-video backoff still queued.
                            // Pause the whole scanner, otherwise an overnight
                            // run makes no progress at all.
                            counters.Current <- "cooling down 5 min"
                            render ()
                            Log.Warning("rate limit: cooling down 5 min after {N} consecutive failures", consecutiveFailures)
                            do! Http.delay 300000
                            consecutiveFailures <- 0
                    else
                        consecutiveFailures <- 0

                    do! Http.delay options.DelayMs

            Console.WriteLine()
            heartbeat ()

            Log.Information(
                "frontier empty, stopping. expanded {Expanded}, captions {Caps}, no captions {NoCaps}, hits {Hits}, failed {Failed}",
                counters.Expanded,
                counters.Captions,
                counters.NoCaptions,
                counters.Hits,
                counters.Failed
            )
        }
