namespace Scraping

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks

type TranscriptSegment =
    { StartMs: int
      Text: string }

/// Why a transcript could not be obtained. The distinction matters: a 429 must
/// be retried, a missing track must not.
type TranscriptError =
    /// Permanent: no track, empty body, 404.
    | NoCaptions of string
    /// Temporary: 429, 5xx, network.
    | Transient of string

    member this.Message =
        match this with
        | NoCaptions m -> m
        | Transient m -> m

/// Downloads and parses the auto-caption track that yt-dlp handed us.
///
/// The URL must come from a fresh `yt-dlp -J` response: the `captionTracks`
/// URL embedded in the watch page is stale and returns a 200 with a zero-byte
/// body.
module Transcript =

    /// All usable tracks, best format first. YouTube often returns several
    /// `json3` tracks for the same language and one of them can answer 429
    /// while another works, so the caller must be able to fall through.
    let private candidates (tracks: CaptionTrack list) =
        let usable = tracks |> List.filter (fun t -> not (String.IsNullOrWhiteSpace t.Url))
        let json3 = usable |> List.filter (fun t -> t.Ext = "json3")
        let srv3 = usable |> List.filter (fun t -> t.Ext = "srv3")
        let rest = usable |> List.filter (fun t -> t.Ext <> "json3" && t.Ext <> "srv3")
        json3 @ srv3 @ rest

    let private parse (body: string) : TranscriptSegment list =
        use doc = JsonDocument.Parse body
        let segments = ResizeArray<TranscriptSegment>()

        match doc.RootElement.TryGetProperty "events" with
        | true, events when events.ValueKind = JsonValueKind.Array ->
            for ev in events.EnumerateArray() do
                let startMs =
                    match ev.TryGetProperty "tStartMs" with
                    | true, v when v.ValueKind = JsonValueKind.Number ->
                        match v.TryGetInt32() with
                        | true, n -> n
                        | _ -> 0
                    | _ -> 0

                let sb = StringBuilder()

                match ev.TryGetProperty "segs" with
                | true, segs when segs.ValueKind = JsonValueKind.Array ->
                    for s in segs.EnumerateArray() do
                        match s.TryGetProperty "utf8" with
                        | true, v when v.ValueKind = JsonValueKind.String -> sb.Append(v.GetString()) |> ignore
                        | _ -> ()
                | _ -> ()

                let text = sb.ToString().Trim()

                if text <> "" then
                    segments.Add { StartMs = startMs; Text = text }
        | _ -> ()

        List.ofSeq segments

    let fetch (tracks: CaptionTrack list) : Task<Result<TranscriptSegment list, TranscriptError>> =
        task {
            let candidates = candidates tracks

            if candidates.IsEmpty then
                return Error(NoCaptions "no caption tracks")
            else
                let mutable result = Error(NoCaptions "no usable caption track")
                let mutable go = true

                for t in candidates do
                    if go then
                        let! response = Http.getStringStatus t.Url

                        match response with
                        | Error 429 ->
                            // try the next track, remember that it was transient
                            result <- Error(Transient "429 Too Many Requests")
                        | Error code when code >= 500 -> result <- Error(Transient(sprintf "HTTP %d" code))
                        | Error code when code > 0 -> result <- Error(NoCaptions(sprintf "HTTP %d" code))
                        | Error _ -> result <- Error(Transient "network error")
                        | Ok body when String.IsNullOrWhiteSpace body -> result <- Error(NoCaptions "empty caption body")
                        | Ok body ->
                            try
                                let segments = parse body

                                if segments.IsEmpty then
                                    result <- Error(NoCaptions "no segments")
                                else
                                    result <- Ok segments
                                    go <- false
                            with e ->
                                result <- Error(NoCaptions e.Message)

                return result
        }

    let toText (segments: TranscriptSegment list) =
        segments
        |> List.map (fun s -> s.Text)
        |> String.concat " "
        |> fun s -> Regex.Replace(s, @"\s+", " ").Trim()
