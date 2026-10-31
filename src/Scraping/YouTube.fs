/// YouTube scraping: search, channel metadata, channel/playlist enumeration,
/// Shorts enumeration and thumbnail download.
///
/// Everything works off the raw HTML/JSON payloads. YouTube's schema changes
/// often, so the parsers are regex-based and tolerant: a missing field becomes
/// an empty string, never an exception.
module Scraping.YouTube

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Scraping
open Scraping.Json

// ---------------------------------------------------------------- patterns

let private pVideoId = @"""videoId"":""([A-Za-z0-9_-]{11})"""
let private pContentId = @"""contentId"":""([A-Za-z0-9_-]{11})"""
let private pTitleRuns = @"""title"":\{""runs"":\[\{""text"":""((?:[^""\\]|\\.)*)"""
let private pLockupTitle = @"""title"":\{""content"":""((?:[^""\\]|\\.)*)"""
let private pOwner = @"""ownerText"":\{""runs"":\[\{""text"":""((?:[^""\\]|\\.)*)"".*?""browseId"":""(UC[A-Za-z0-9_-]{22})"""
let private pPublished = @"""publishedTimeText"":\{""simpleText"":""([^""]+)"""
let private pViews = @"""viewCountText"":\{""simpleText"":""([^""]+)"""
let private pChannelName = @"""channelMetadataRenderer"":\{""title"":""((?:[^""\\]|\\.)*)"""
let private pOgTitle = @"<meta property=""og:title"" content=""([^""]*)"""
let private pSubs = @"""subscriberCountText"":""([^""]+)"""
let private pVids = @"""videoCountText"":""([^""]+)"""
let private pApiKey = @"""INNERTUBE_API_KEY"":""([^""]+)"""
let private pClientVer = @"""INNERTUBE_CLIENT_VERSION"":""([^""]+)"""
let private pVisitor = @"""visitorData"":""([^""]+)"""
let private pAccessibility = @"""accessibilityText"":""((?:[^""\\]|\\.)*)"""

let private markerVideo = @"""videoRenderer"":{" 
let private markerLockup = @"""lockupViewModel"":{" 
let private markerPlaylistVideo = @"""playlistVideoRenderer"":{" 
let private markerShorts = @"""shortsLockupViewModel"":{" 

// ---------------------------------------------------------------- InnerTube

module private InnerTube =
    let url (endpoint: string) (apiKey: string) =
        sprintf "https://www.youtube.com/youtubei/v1/%s?key=%s&prettyPrint=false" endpoint apiKey

    let headers (clientVersion: string) =
        [ "X-Youtube-Client-Name", "1"
          "X-Youtube-Client-Version", clientVersion
          "Origin", "https://www.youtube.com" ]

    let continuationBody (clientVersion: string) (visitorData: string) (token: string) =
        let payload =
            {| context =
                {| client =
                    {| clientName = "WEB"
                       clientVersion = clientVersion
                       hl = "ru"
                       gl = "RU"
                       visitorData = visitorData |} |}
               continuation = token |}
        JsonSerializer.Serialize payload

    /// Pull the three values every InnerTube call needs out of a page.
    let session (html: string) =
        tryGroup pApiKey html 1 |> Option.defaultValue "",
        tryGroup pClientVer html 1 |> Option.defaultValue "",
        tryGroup pVisitor html 1 |> Option.defaultValue ""

// ---------------------------------------------------------------- helpers

/// "2 дня назад" -> 2, "3 недели назад" -> 21, "1 год назад" -> 365.
let parseAgeDays (text: string) : int =
    if String.IsNullOrWhiteSpace text then 9999
    else
        let m = Regex.Match(text, @"(\d+)\s*(секунд|минут|час|дн|недел|месяц|год|лет)")
        if not m.Success then 9999
        else
            let n = Int32.Parse m.Groups.[1].Value
            let unit = m.Groups.[2].Value
            if unit.StartsWith "секунд" || unit.StartsWith "минут" || unit.StartsWith "час" then 0
            elif unit.StartsWith "дн" then n
            elif unit.StartsWith "недел" then n * 7
            elif unit.StartsWith "месяц" then n * 30
            else n * 365

/// Strip the Shorts accessibility wrapper: `Воспроизвести короткое видео "TITLE, 1,2 тыс. просмотров"`.
let private cleanShortsTitle (raw: string) : string =
    let t =
        raw
            .Replace("Воспроизвести короткое видео ", "")
            .Replace("Play short video ", "")
            .Trim(' ', '"', '«', '»')
    let t = Regex.Replace(t, @",\s*[\d\s.,]+\s*(тыс\.|млн|K|M)?\s*просмотров\s*$", "")
    let t = Regex.Replace(t, @",\s*[\d.,]+[KMB]?\s*views\s*$", "")
    t.Trim(' ', '"', '«', '»')

// ---------------------------------------------------------------- search

let private parseSearchPayload (query: string) (payload: string) : Video list =
    let blocks = splitBlocks markerVideo payload
    [ for i in 1 .. blocks.Length - 1 do
          let c = clamp 20000 blocks.[i]
          match tryGroup pVideoId c 1 with
          | None -> ()
          | Some vid ->
              let published = tryGroup pPublished c 1 |> Option.defaultValue ""
              yield
                  { Id = vid
                    Title = tryText pTitleRuns c |> Option.defaultValue ""
                    Channel = tryGroup pOwner c 1 |> Option.map unescape |> Option.defaultValue ""
                    ChannelId = tryGroup pOwner c 2 |> Option.defaultValue ""
                    Published = published
                    AgeDays = parseAgeDays published
                    Views = tryGroup pViews c 1 |> Option.defaultValue ""
                    Url = sprintf "https://www.youtube.com/watch?v=%s" vid } ]

/// Global YouTube search, paginated. `maxPages` counts continuation pages
/// (page 0 is the initial HTML).
let search (query: string) (maxPages: int) : Task<Video list> =
    task {
        let url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString query
        let! html = Http.getString url
        let acc = ResizeArray<Video>(parseSearchPayload query html)
        let apiKey, clientVersion, visitor = InnerTube.session html
        let mutable token = longestToken html
        let mutable page = 0
        let mutable go = token.IsSome
        while go && page < maxPages do
            page <- page + 1
            let body = InnerTube.continuationBody clientVersion visitor token.Value
            let! json = Http.postJson (InnerTube.url "search" apiKey) body (InnerTube.headers clientVersion)
            let before = acc.Count
            acc.AddRange(parseSearchPayload query json)
            let next = longestToken json
            let stop = next.IsNone || next = token || acc.Count = before
            if stop then go <- false else token <- next
            do! Http.delay 300
        return List.ofSeq acc
    }

// ---------------------------------------------------------------- channel

/// Subscriber/video counts from the channel "about" page.
let channelInfo (channelId: string) : Task<ChannelInfo option> =
    task {
        let! html = Http.tryGetString (sprintf "https://www.youtube.com/channel/%s/about" channelId)
        match html with
        | None -> return None
        | Some h ->
            let name =
                tryText pChannelName h
                |> Option.orElse (tryGroup pOgTitle h 1)
                |> Option.defaultValue channelId
            let subsText = tryGroup pSubs h 1 |> Option.defaultValue ""
            let vidsText = tryGroup pVids h 1 |> Option.defaultValue ""
            return
                Some
                    { Id = channelId
                      Name = name
                      Subs = Domain.parseCount subsText
                      SubsText = subsText
                      Videos = Domain.parseCount vidsText
                      VideosText = vidsText }
    }

/// Pull (videoId, title) pairs out of a channel/playlist payload.
let private collectVideoEntries (payload: string) (acc: ResizeArray<string * string>) =
    let lockups = splitBlocks markerLockup payload
    for i in 1 .. lockups.Length - 1 do
        let c = clamp 20000 lockups.[i]
        match tryGroup pContentId c 1, tryText pLockupTitle c with
        | Some id, Some title -> acc.Add(id, title)
        | _ -> ()
    let videos = splitBlocks markerVideo payload
    for i in 1 .. videos.Length - 1 do
        let c = clamp 20000 videos.[i]
        match tryGroup pVideoId c 1, tryText pTitleRuns c with
        | Some id, Some title -> acc.Add(id, title)
        | _ -> ()

/// Every video on a channel's "Videos" tab as (videoId, title), paginated.
let channelVideos (channelId: string) (maxPages: int) : Task<(string * string) list> =
    task {
        let! htmlOpt = Http.tryGetString (sprintf "https://www.youtube.com/channel/%s/videos" channelId)
        match htmlOpt with
        | None -> return []
        | Some html ->
            let entries = ResizeArray<string * string>()
            collectVideoEntries html entries
            let apiKey, clientVersion, visitor = InnerTube.session html
            let mutable token = longestToken html
            let mutable page = 0
            let mutable go = token.IsSome
            while go && page < maxPages do
                page <- page + 1
                let body = InnerTube.continuationBody clientVersion visitor token.Value
                let! json = Http.postJson (InnerTube.url "browse" apiKey) body (InnerTube.headers clientVersion)
                let before = entries.Count
                collectVideoEntries json entries
                let next = longestToken json
                let stop = next.IsNone || next = token || entries.Count = before
                if stop then go <- false else token <- next
                do! Http.delay 250
            return List.ofSeq entries
    }

/// Every video title on a channel's "Videos" tab (paginated).
let channelTitles (channelId: string) (maxPages: int) : Task<string list> =
    task {
        let! entries = channelVideos channelId maxPages
        return entries |> List.map snd
    }

/// Every Short on a channel's "Shorts" tab as (videoId, title).
let channelShorts (channelId: string) (maxPages: int) : Task<(string * string) list> =
    task {
        let! htmlOpt = Http.tryGetString (sprintf "https://www.youtube.com/channel/%s/shorts" channelId)
        match htmlOpt with
        | None -> return []
        | Some html ->
            let items = ResizeArray<string * string>()
            let collect (payload: string) =
                let blocks = splitBlocks markerShorts payload
                for i in 1 .. blocks.Length - 1 do
                    let c = clamp 6000 blocks.[i]
                    match tryGroup pVideoId c 1 with
                    | None -> ()
                    | Some vid ->
                        let title =
                            tryGroup pAccessibility c 1
                            |> Option.map unescape
                            |> Option.map cleanShortsTitle
                            |> Option.defaultValue ""
                        items.Add(vid, title)
            collect html
            let apiKey, clientVersion, visitor = InnerTube.session html
            let mutable token = longestToken html
            let mutable page = 0
            let mutable go = token.IsSome
            while go && page < maxPages do
                page <- page + 1
                let body = InnerTube.continuationBody clientVersion visitor token.Value
                let! json = Http.postJson (InnerTube.url "browse" apiKey) body (InnerTube.headers clientVersion)
                let before = items.Count
                collect json
                let next = longestToken json
                let stop = next.IsNone || next = token || items.Count = before
                if stop then go <- false else token <- next
                do! Http.delay 250
            return List.ofSeq items
    }

/// Videos recommended next to `videoId` — the watch page sidebar.
/// This is the edge set the BFS walks.
let relatedVideos (videoId: string) (limit: int) : Task<(string * string) list> =
    task {
        let! htmlOpt = Http.tryGetString (sprintf "https://www.youtube.com/watch?v=%s" videoId)

        match htmlOpt with
        | None -> return []
        | Some html ->
            let entries = ResizeArray<string * string>()
            let blocks = splitBlocks markerLockup html

            for i in 1 .. blocks.Length - 1 do
                let c = clamp 20000 blocks.[i]

                match tryGroup pContentId c 1, tryText pLockupTitle c with
                | Some id, Some title when id <> videoId -> entries.Add(id, title)
                | _ -> ()

            return
                entries
                |> Seq.distinctBy fst
                |> Seq.truncate limit
                |> List.ofSeq
    }

// ---------------------------------------------------------------- playlist

/// Items of a playlist as (videoId, title).
let playlistItems (playlistId: string) (maxPages: int) : Task<(string * string) list> =
    task {
        let! htmlOpt = Http.tryGetString (sprintf "https://www.youtube.com/playlist?list=%s" playlistId)
        match htmlOpt with
        | None -> return []
        | Some html ->
            let items = ResizeArray<string * string>()
            let collect (payload: string) =
                let addFrom (marker: string) (titlePattern: string) =
                    let blocks = splitBlocks marker payload
                    for i in 1 .. blocks.Length - 1 do
                        let c = clamp 20000 blocks.[i]
                        match tryGroup pVideoId c 1 with
                        | None -> ()
                        | Some vid ->
                            let title = tryText titlePattern c |> Option.defaultValue ""
                            items.Add(vid, title)
                addFrom markerPlaylistVideo pTitleRuns
                addFrom markerLockup pLockupTitle
            collect html
            let apiKey, clientVersion, visitor = InnerTube.session html
            let mutable token = longestToken html
            let mutable page = 0
            let mutable go = token.IsSome
            while go && page < maxPages do
                page <- page + 1
                let body = InnerTube.continuationBody clientVersion visitor token.Value
                let! json = Http.postJson (InnerTube.url "browse" apiKey) body (InnerTube.headers clientVersion)
                let before = items.Count
                collect json
                let next = longestToken json
                let stop = next.IsNone || next = token || items.Count = before
                if stop then go <- false else token <- next
                do! Http.delay 250
            return List.ofSeq items
    }

// ---------------------------------------------------------------- thumbnails

/// Download the best available still for a video into `destDir`.
let downloadThumbnail (videoId: string) (destDir: string) : Task<bool> =
    task {
        Directory.CreateDirectory destDir |> ignore
        let dest = Path.Combine(destDir, videoId + ".jpg")
        if File.Exists dest then return true
        else
            let mutable ok = false
            for name in [ "frame0"; "hq720"; "maxresdefault"; "hqdefault" ] do
                if not ok then
                    let url = sprintf "https://i.ytimg.com/vi/%s/%s.jpg" videoId name
                    let! bytes = Http.tryGetBytes url
                    match bytes with
                    | Some b when b.Length > 2000 ->
                        do! File.WriteAllBytesAsync(dest, b)
                        ok <- true
                    | _ -> ()
            return ok
    }
