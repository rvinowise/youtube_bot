/// Rutube scraping. Rutube exposes a public JSON search API (no token needed),
/// which makes it much friendlier than YouTube for this kind of work.
module Scraping.rutube

open System
open System.Globalization
open System.Text.Json
open System.Threading.Tasks
open Scraping

let private asString (element: JsonElement) (name: string) : string =
    match element.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | true, v when v.ValueKind = JsonValueKind.Number -> v.GetRawText()
    | _ -> ""

let private asInt (element: JsonElement) (name: string) : int =
    match element.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt32() with
        | true, n -> n
        | _ -> 0
    | _ -> 0

/// Search Rutube videos. `created_ts` comes back as an ISO string, not a unix stamp.
let search (query: string) (limit: int) : Task<Video list> =
    task {
        let url =
            sprintf "https://rutube.ru/api/search/video/?query=%s&limit=%d" (Uri.EscapeDataString query) limit
        let! bodyOpt = http.tryGetString url
        match bodyOpt with
        | None -> return []
        | Some body ->
            use doc = JsonDocument.Parse body
            let results = ResizeArray<Video>()
            match doc.RootElement.TryGetProperty "results" with
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
                for item in arr.EnumerateArray() do
                    let authorName, authorId =
                        match item.TryGetProperty "author" with
                        | true, a when a.ValueKind = JsonValueKind.Object -> asString a "name", asString a "id"
                        | _ -> "", ""
                    let created = asString item "created_ts"
                    let ageDays, published =
                        match
                            DateTime.TryParse(
                                created,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                            )
                        with
                        | true, dt -> int (DateTime.UtcNow - dt).TotalDays, dt.ToString("yyyy-MM-dd")
                        | _ -> 9999, ""
                    let id = asString item "id"
                    results.Add
                        { Id = id
                          Title = asString item "title"
                          Channel = authorName
                          ChannelId = authorId
                          Published = published
                          AgeDays = ageDays
                          Views = asString item "hits"
                          Url = sprintf "https://rutube.ru/video/%s/" id }
            | _ -> ()
            return List.ofSeq results
    }

/// Rutube's API has no subscriber count; video count is the size proxy.
let channelVideoCount (authorId: string) : Task<int> =
    task {
        let! bodyOpt = http.tryGetString (sprintf "https://rutube.ru/api/video/person/%s/" authorId)
        match bodyOpt with
        | None -> return 0
        | Some body ->
            use doc = JsonDocument.Parse body
            return asInt doc.RootElement "num_pages" * asInt doc.RootElement "per_page"
    }
