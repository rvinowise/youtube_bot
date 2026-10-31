/// The "beginner who mentioned immortality in passing" pipeline.
///
/// 1. Run a set of queries against YouTube and/or Rutube.
/// 2. Keep only fresh results.
/// 3. Group by channel, drop channels that are too big.
/// 4. Scan each surviving channel's titles for seed words.
/// 5. Classify: Target / Dedicated / NoSeedHits.
module Scraping.BloggerFinder

open System
open System.Threading.Tasks
open Scraping

type Options =
    { Queries: string list
      /// "yt" | "rutube" | "both"
      Source: string
      MaxPerQuery: int
      MaxSubs: int
      MaxVideos: int
      MaxDaysOld: int
      MaxChannelsToScan: int
      SkipChannelScan: bool }

let defaultQueries =
    [ "иммортализм"
      "трансгуманизм"
      "бессмертие"
      "вечная жизнь"
      "крионика"
      "загрузка сознания"
      "цифровое бессмертие"
      "продление жизни"
      "страх смерти"
      "смысл жизни" ]

let defaultOptions =
    { Queries = defaultQueries
      Source = "both"
      MaxPerQuery = 30
      MaxSubs = 5000
      MaxVideos = 120
      MaxDaysOld = 400
      MaxChannelsToScan = 12
      SkipChannelScan = false }

/// Words that mark a video as touching immortality / life-extension.
let seedWords =
    [ "бессмерт"; "иммортал"; "трансгуман"; "вечная жизнь"; "вечной жизни"
      "крионик"; "загрузк"; "оцифров"; "цифровое сознани"; "продлени"
      "старени"; "омоложе"; "смерт"; "смысл жизни"; "страх смерти"
      "воскрес"; "душ"; "загробн"; "перерожд" ]

let private hasSeed (title: string) =
    let low = title.ToLowerInvariant()
    seedWords |> List.exists low.Contains

/// A channel is a Target when it mentions the topic but is not built around it.
let classify (hits: int) (ratio: float) : Profile =
    if hits >= 1 && ratio <= 20.0 then Target
    elif hits >= 1 then Dedicated
    else NoSeedHits

let private dedupe (videos: Video list) =
    videos |> List.distinctBy (fun v -> v.Id)

let private sampleOf (videos: Video list) =
    videos
    |> List.sortBy (fun v -> v.AgeDays)
    |> List.truncate 2
    |> List.map (fun v -> v.Title)
    |> String.concat " || "

// ---------------------------------------------------------------- stages

let private collectHits (opt: Options) : Task<Video list * Video list> =
    task {
        let yt = ResizeArray<Video>()
        let rt = ResizeArray<Video>()
        for query in opt.Queries do
            if opt.Source = "yt" || opt.Source = "both" then
                let! found = YouTube.search query 3
                yt.AddRange(found |> List.truncate opt.MaxPerQuery)
                do! Http.delay 400
            if opt.Source = "rutube" || opt.Source = "both" then
                let! found = Rutube.search query 30
                rt.AddRange(found |> List.truncate opt.MaxPerQuery)
                do! Http.delay 300
        return dedupe (List.ofSeq yt), dedupe (List.ofSeq rt)
    }

let private youtubeCandidates (opt: Options) (hits: Video list) : Task<Candidate list> =
    task {
        let groups =
            hits
            |> List.filter (fun v -> v.AgeDays <= opt.MaxDaysOld && v.ChannelId <> "")
            |> List.groupBy (fun v -> v.ChannelId)
        let acc = ResizeArray<Candidate>()
        for (channelId, videos) in groups do
            let! info = YouTube.channelInfo channelId
            match info with
            | Some i ->
                acc.Add
                    { Source = "yt"
                      ChannelId = i.Id
                      Name = i.Name
                      Subs = i.Subs
                      SubsText = i.SubsText
                      Videos = i.Videos
                      VideosText = i.VideosText
                      Matches = videos.Length
                      Sample = sampleOf videos
                      Url = sprintf "https://www.youtube.com/channel/%s" i.Id }
            | None -> ()
            do! Http.delay 200
        return List.ofSeq acc
    }

let private rutubeCandidates (opt: Options) (hits: Video list) : Task<Candidate list> =
    task {
        let groups =
            hits
            |> List.filter (fun v -> v.AgeDays <= opt.MaxDaysOld && v.ChannelId <> "")
            |> List.groupBy (fun v -> v.ChannelId)
        let acc = ResizeArray<Candidate>()
        for (channelId, videos) in groups do
            let! count = Rutube.channelVideoCount channelId
            acc.Add
                { Source = "rutube"
                  ChannelId = channelId
                  Name = videos.Head.Channel
                  Subs = 0
                  SubsText = "n/a"
                  Videos = count
                  VideosText = sprintf "%d (оценка)" count
                  Matches = videos.Length
                  Sample = sampleOf videos
                  Url = sprintf "https://rutube.ru/video/person/%s/" channelId }
            do! Http.delay 200
        return List.ofSeq acc
    }

let private scanChannel (candidate: Candidate) : Task<ScannedChannel option> =
    task {
        let! entries = YouTube.channelVideos candidate.ChannelId 6
        if entries.IsEmpty then return None
        else
            let hits =
                entries
                |> List.filter (fun (_, title) -> hasSeed title)
                |> List.map (fun (id, title) ->
                    { Id = id
                      Title = title
                      Url = sprintf "https://www.youtube.com/watch?v=%s" id })
            let ratio =
                if entries.IsEmpty then 0.0
                else Math.Round(100.0 * float hits.Length / float entries.Length, 1)
            return
                Some
                    { Profile = classify hits.Length ratio
                      Name = candidate.Name
                      ChannelId = candidate.ChannelId
                      SubsText = candidate.SubsText
                      Videos = entries.Length
                      SeedHits = hits.Length
                      HitRatio = ratio
                      HitTitles = hits |> List.truncate 6 |> List.map (fun h -> h.Title) |> String.concat " | "
                      HitVideos = hits
                      Url = candidate.Url }
    }

// ---------------------------------------------------------------- pipeline

/// Returns every candidate plus the scanned subset (empty when scanning is off).
let run (opt: Options) : Task<Candidate list * ScannedChannel list> =
    task {
        let! ytHits, rtHits = collectHits opt
        let! ytCands = youtubeCandidates opt ytHits
        let! rtCands = rutubeCandidates opt rtHits

        let candidates =
            (ytCands
             |> List.filter (fun c -> c.Subs <= opt.MaxSubs && (c.Videos = 0 || c.Videos <= opt.MaxVideos)))
            @ rtCands
            |> List.sortBy (fun c -> c.Subs)

        if opt.SkipChannelScan then
            return candidates, []
        else
            let scanned = ResizeArray<ScannedChannel>()
            for candidate in candidates |> List.filter (fun c -> c.Source = "yt") |> List.truncate opt.MaxChannelsToScan do
                let! result = scanChannel candidate
                match result with
                | Some s -> scanned.Add s
                | None -> ()
            let ordered =
                scanned
                |> List.ofSeq
                |> List.sortBy (fun s -> s.Profile.Rank, s.HitRatio)
            return candidates, ordered
    }
