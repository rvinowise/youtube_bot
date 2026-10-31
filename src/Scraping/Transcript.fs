namespace Scraping

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks

type TranscriptSegment =
    { StartMs: int
      Text: string }

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

    let fetch (tracks: CaptionTrack list) : Task<Result<TranscriptSegment list, string>> =
        task {
            match pick tracks with
            | None -> return Error "no caption tracks"
            | Some t when String.IsNullOrWhiteSpace t.Url -> return Error "empty caption url"
            | Some t ->
                try
                    let! body = Http.getString t.Url

                    if String.IsNullOrWhiteSpace body then
                        return Error "empty caption body"
                    else
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
                            return Error "no segments"
                        else
                            return Ok(List.ofSeq segments)
                with e ->
                    return Error e.Message
        }

    let toText (segments: TranscriptSegment list) =
        segments
        |> List.map (fun s -> s.Text)
        |> String.concat " "
        |> fun s -> Regex.Replace(s, @"\s+", " ").Trim()
