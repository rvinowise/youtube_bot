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

    let private pick (tracks: CaptionTrack list) =
        tracks
        |> List.tryFind (fun t -> t.Ext = "json3")
        |> Option.orElse (tracks |> List.tryFind (fun t -> t.Ext = "srv3"))
        |> Option.orElse (List.tryHead tracks)

    let fetch (tracks: CaptionTrack list) : Task<Result<TranscriptSegment list, TranscriptError>> =
        task {
            match pick tracks with
            | None -> return Error(NoCaptions "no caption tracks")
            | Some t when String.IsNullOrWhiteSpace t.Url -> return Error(NoCaptions "empty caption url")
            | Some t ->
                let! response = Http.getStringStatus t.Url

                match response with
                | Error 429 -> return Error(Transient "429 Too Many Requests")
                | Error code when code >= 500 -> return Error(Transient(sprintf "HTTP %d" code))
                | Error code when code > 0 -> return Error(NoCaptions(sprintf "HTTP %d" code))
                | Error _ -> return Error(Transient "network error")
                | Ok body when String.IsNullOrWhiteSpace body -> return Error(NoCaptions "empty caption body")
                | Ok body ->
                    try
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
                                        | true, v when v.ValueKind = JsonValueKind.String ->
                                            sb.Append(v.GetString()) |> ignore
                                        | _ -> ()
                                | _ -> ()

                                let text = sb.ToString().Trim()

                                if text <> "" then
                                    segments.Add { StartMs = startMs; Text = text }
                        | _ -> ()

                        if segments.Count = 0 then
                            return Error(NoCaptions "no segments")
                        else
                            return Ok(List.ofSeq segments)
                    with e ->
                        return Error(NoCaptions e.Message)
        }

    let toText (segments: TranscriptSegment list) =
        segments
        |> List.map (fun s -> s.Text)
        |> String.concat " "
        |> fun s -> Regex.Replace(s, @"\s+", " ").Trim()
