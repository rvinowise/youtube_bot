module Scraping.Program

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Scraping

// ---------------------------------------------------------------- CLI helpers

let private usage () =
    Console.WriteLine(
        """
scraping — YouTube / Rutube research toolkit

USAGE
  scraping scan           [options]        long-running scan into PostgreSQL
  scraping db                             check the database and apply the schema
  scraping videos [--state S] [--term T]  list videos with URLs and transcript size
  scraping hits [--term T] [--limit N]    pre-computed seed-term mentions with timestamps
  scraping backfill                       recompute hits for transcripts that have none
  scraping sql --query "select ..."       run arbitrary SQL, print a table
  scraping find --term "бессмертие"       search the collected transcripts
  scraping find-bloggers  [options]
  scraping channel-videos --channel <UC...> [--pages N]
  scraping shorts         --channel <UC...> [--pages N]
  scraping playlist       --id <PL...> [--pages N]
  scraping thumbs         --ids <id,id,...> [--out dir]
  scraping verify         --channel <UC...>

SCAN OPTIONS
  --queries "a,b,c"     comma separated search queries
  --pages N             search continuation pages per query (default 3)
  --limit N             stop after N videos in the caption stage (0 = all)
  --delay N             pause between items, ms (default 300)

FIND-BLOGGERS OPTIONS
  --queries "a,b,c"     comma separated search queries
  --source yt|rutube|both
  --max-per-query N     results kept per query (default 30)
  --max-subs N          subscriber ceiling (default 5000)
  --max-videos N        video-count ceiling (default 120)
  --max-days N          freshness window in days (default 400)
  --scan N              how many channels to scan (default 12)
  --no-scan             skip the title scan
  --out FILE            channel-level TSV (default yt_blogger_candidates.tsv)
  --videos-out FILE     video-level TSV with URLs (default yt_blogger_videos.tsv)
"""
    )

let private valueOf (args: string list) (name: string) (fallback: string) =
    let rec loop =
        function
        | a :: v :: _ when a = name -> v
        | _ :: rest -> loop rest
        | [] -> fallback
    loop args

let private flag (args: string list) (name: string) = List.contains name args

let private intOf (args: string list) (name: string) (fallback: int) =
    match Int32.TryParse(valueOf args name (string fallback)) with
    | true, n -> n
    | _ -> fallback

let private printTable (headers: string list) (rows: string list list) =
    if rows.IsEmpty then
        Console.WriteLine "  (no rows)"
    else
        let all = headers :: rows
        let widths =
            all
            |> List.map (List.map (fun c -> if isNull c then 0 else c.Length))
            |> List.reduce (List.map2 max)
        let pad (s: string) (w: int) =
            let s = if isNull s then "" else s
            if s.Length >= w then s else s + String(' ', w - s.Length)
        let line (cells: string list) = List.map2 pad cells widths |> String.concat "  "
        Console.WriteLine(line headers)
        Console.WriteLine(String('-', (widths |> List.sum) + 2 * (widths.Length - 1)))
        for row in rows do
            Console.WriteLine(line row)

// ---------------------------------------------------------------- commands

let private findBloggers (args: string list) : Task<int> =
    task {
        let queries =
            (valueOf args "--queries" (String.concat "," BloggerFinder.defaultQueries)).Split(',')
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> List.ofArray

        let options =
            { BloggerFinder.defaultOptions with
                Queries = queries
                Source = valueOf args "--source" "both"
                MaxPerQuery = intOf args "--max-per-query" 30
                MaxSubs = intOf args "--max-subs" 5000
                MaxVideos = intOf args "--max-videos" 120
                MaxDaysOld = intOf args "--max-days" 400
                MaxChannelsToScan = intOf args "--scan" 12
                SkipChannelScan = flag args "--no-scan" }

        Console.WriteLine(sprintf "queries: %d | source: %s" options.Queries.Length options.Source)
        let! candidates, scanned = BloggerFinder.run options

        Console.WriteLine(sprintf "\ncandidates: %d" candidates.Length)
        printTable
            [ "src"; "name"; "subs"; "videos"; "matches"; "sample" ]
            [ for c in candidates -> [ c.Source; c.Name; c.SubsText; c.VideosText; string c.Matches; c.Sample ] ]

        if not scanned.IsEmpty then
            Console.WriteLine(sprintf "\nscanned: %d" scanned.Length)
            printTable
                [ "profile"; "name"; "subs"; "videos"; "hits"; "ratio"; "hit titles" ]
                [ for s in scanned ->
                      [ s.Profile.Label
                        s.Name
                        s.SubsText
                        string s.Videos
                        string s.SeedHits
                        sprintf "%.1f%%" s.HitRatio
                        s.HitTitles ] ]

            let out = valueOf args "--out" "yt_blogger_candidates.tsv"
            let lines =
                scanned
                |> List.map (fun s ->
                    String.concat
                        "\t"
                        [ s.Profile.Label
                          s.Name
                          s.ChannelId
                          s.SubsText
                          string s.Videos
                          string s.SeedHits
                          sprintf "%.1f" s.HitRatio
                          s.HitTitles
                          s.Url ])
            File.WriteAllLines(out, lines)
            Console.WriteLine(sprintf "written %s" out)

            // video-level output: the actual URLs of the matching videos
            let videoRows =
                [ for s in scanned do
                      for v in s.HitVideos -> s.Profile.Label, s.Name, s.ChannelId, v.Title, v.Url ]

            Console.WriteLine(sprintf "\nvideo hits: %d" videoRows.Length)
            printTable
                [ "profile"; "channel"; "video"; "url" ]
                [ for (profile, channel, _, title, url) in videoRows -> [ profile; channel; title; url ] ]

            let videosOut = valueOf args "--videos-out" "yt_blogger_videos.tsv"
            let videoLines =
                videoRows
                |> List.map (fun (profile, channel, channelId, title, url) ->
                    String.concat "\t" [ profile; channel; channelId; title; url ])
            File.WriteAllLines(videosOut, videoLines)
            Console.WriteLine(sprintf "written %s" videosOut)

        return 0
    }

let private channelVideos (args: string list) : Task<int> =
    task {
        let channel = valueOf args "--channel" ""
        if channel = "" then
            Console.WriteLine "missing --channel"
            return 1
        else
            let! entries = YouTube.channelVideos channel (intOf args "--pages" 10)
            Console.WriteLine(sprintf "videos: %d" entries.Length)
            for (id, title) in entries do
                Console.WriteLine(sprintf "  %s\t%s\thttps://www.youtube.com/watch?v=%s" id title id)
            return 0
    }

let private channelShorts (args: string list) : Task<int> =
    task {
        let channel = valueOf args "--channel" ""
        if channel = "" then
            Console.WriteLine "missing --channel"
            return 1
        else
            let! items = YouTube.channelShorts channel (intOf args "--pages" 10)
            Console.WriteLine(sprintf "shorts: %d" items.Length)
            for (id, title) in items do
                Console.WriteLine(sprintf "  %s\t%s\thttps://www.youtube.com/shorts/%s" id title id)
            return 0
    }

let private playlist (args: string list) : Task<int> =
    task {
        let id = valueOf args "--id" ""
        if id = "" then
            Console.WriteLine "missing --id"
            return 1
        else
            let! items = YouTube.playlistItems id (intOf args "--pages" 10)
            Console.WriteLine(sprintf "items: %d" items.Length)
            for (vid, title) in items do
                Console.WriteLine(sprintf "  %s\t%s" vid title)
            return 0
    }

let private thumbs (args: string list) : Task<int> =
    task {
        let ids =
            (valueOf args "--ids" "").Split(',')
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
        let out = valueOf args "--out" "yt_thumbs"
        let mutable saved = 0
        for id in ids do
            let! ok = YouTube.downloadThumbnail id out
            if ok then saved <- saved + 1
            Console.WriteLine(sprintf "  %s %s" id (if ok then "ok" else "FAILED"))
        Console.WriteLine(sprintf "saved %d/%d -> %s" saved ids.Length out)
        return 0
    }

let private verify (args: string list) : Task<int> =
    task {
        let channel = valueOf args "--channel" ""
        if channel = "" then
            Console.WriteLine "missing --channel"
            return 1
        else
            let! info = YouTube.channelInfo channel
            let! videos = YouTube.channelTitles channel 40
            let! shorts = YouTube.channelShorts channel 40
            match info with
            | Some i ->
                Console.WriteLine(sprintf "channel:      %s" i.Name)
                Console.WriteLine(sprintf "claimed:      %s" i.VideosText)
            | None -> Console.WriteLine "channel:      (about page unavailable)"
            Console.WriteLine(sprintf "videos tab:   %d" videos.Length)
            Console.WriteLine(sprintf "shorts tab:   %d" shorts.Length)
            Console.WriteLine(sprintf "total:        %d" (videos.Length + shorts.Length))
            return 0
    }

// ---------------------------------------------------------------- scan / db

let private backfillCommand (_args: string list) : Task<int> =
    task {
        Db.init Settings.connectionString
        let rows = Db.transcriptsWithoutHits ()
        Console.WriteLine(sprintf "transcripts without hits: %d" rows.Length)

        let mutable total = 0

        for t in rows do
            let hits = Scan.hitsFromText t.text
            Db.insertHits t.video_id hits
            total <- total + hits.Length

        Console.WriteLine(sprintf "inserted hits: %d" total)
        return 0
    }

let private hitsCommand (args: string list) : Task<int> =
    task {
        Db.init Settings.connectionString
        let term = valueOf args "--term" ""
        let limit = intOf args "--limit" 100
        let rows = Db.listHits term limit

        Console.WriteLine(sprintf "hits: %d" rows.Length)

        printTable
            [ "url"; "at"; "term"; "title"; "snippet" ]
            [ for r in rows ->
                  [ sprintf "https://www.youtube.com/watch?v=%s&t=%ds" r.video_id (r.t_start / 1000)
                    sprintf "%d:%02d" (r.t_start / 60000) (r.t_start / 1000 % 60)
                    r.term
                    r.title
                    r.snippet ] ]

        return 0
    }

let private videosCommand (args: string list) : Task<int> =
    task {
        Db.init Settings.connectionString
        let state = valueOf args "--state" ""
        let term = valueOf args "--term" ""
        let limit = intOf args "--limit" 50
        let rows = Db.listVideos state term limit

        Console.WriteLine(sprintf "videos: %d" rows.Length)

        printTable
            [ "url"; "title"; "state"; "dur"; "chars" ]
            [ for r in rows ->
                  [ sprintf "https://www.youtube.com/watch?v=%s" r.id
                    r.title
                    r.state
                    string r.duration
                    string r.chars ] ]

        return 0
    }

let private sqlCommand (args: string list) : Task<int> =
    task {
        Db.init Settings.connectionString
        let sql = valueOf args "--query" ""

        if sql = "" then
            Console.WriteLine "missing --query"
            return 1
        else
            let headers, rows = Db.runSql sql
            printTable headers rows
            Console.WriteLine(sprintf "rows: %d" rows.Length)
            return 0
    }

let private findCommand (args: string list) : Task<int> =
    task {
        Db.init Settings.connectionString
        let term = valueOf args "--term" ""

        if term = "" then
            Console.WriteLine "missing --term"
            return 1
        else
            let limit = intOf args "--limit" 50
            let hits = Db.searchTranscripts term limit
            Console.WriteLine(sprintf "hits: %d" hits.Length)

            for h in hits do
                Console.WriteLine(sprintf "https://www.youtube.com/watch?v=%s\t%s" h.video_id h.title)
                Console.WriteLine(sprintf "    ...%s..." (h.snippet.Replace("\n", " ")))

            return 0
    }

let private dbCommand (_args: string list) : Task<int> =
    task {
        Db.init Settings.connectionString
        Db.migrate ()
        let queries, videos, transcripts = Db.stats ()
        Console.WriteLine "connection:  ok"
        Console.WriteLine(sprintf "queries:     %d" queries.Length)
        Console.WriteLine(sprintf "videos:      %d" videos.Length)
        Console.WriteLine(sprintf "transcripts: %d" transcripts.Length)
        return 0
    }

let private scanCommand (args: string list) : Task<int> =
    task {
        Db.init Settings.connectionString
        Db.migrate ()

        let queries =
            (valueOf args "--queries" (String.concat "," BloggerFinder.defaultQueries)).Split(',')
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> List.ofArray

        let options =
            { Queries = queries
              MaxPages = intOf args "--pages" 3
              Limit = intOf args "--limit" 0
              DelayMs = intOf args "--delay" 300 }

        Console.WriteLine(sprintf "queries: %d | pages: %d | limit: %d" options.Queries.Length options.MaxPages options.Limit)
        do! Scan.run options
        return 0
    }

// ---------------------------------------------------------------- entry point

[<EntryPoint>]
let main argv =
    Console.OutputEncoding <- Encoding.UTF8
    let args = List.ofArray argv
    let command, rest =
        match args with
        | c :: tail -> c, tail
        | [] -> "", []

    Settings.initLogging ()

    let work =
        match command with
        | "scan" -> scanCommand rest
        | "db" -> dbCommand rest
        | "videos" -> videosCommand rest
        | "hits" -> hitsCommand rest
        | "backfill" -> backfillCommand rest
        | "sql" -> sqlCommand rest
        | "find" -> findCommand rest
        | "find-bloggers" -> findBloggers rest
        | "channel-videos" -> channelVideos rest
        | "shorts" -> channelShorts rest
        | "playlist" -> playlist rest
        | "thumbs" -> thumbs rest
        | "verify" -> verify rest
        | "help" | "--help" | "-h" ->
            usage ()
            Task.FromResult 0
        | _ ->
            usage ()
            Task.FromResult 1

    work.GetAwaiter().GetResult()
