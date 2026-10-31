namespace Scraping

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks

type CaptionTrack =
    { Lang: string
      Ext: string
      Url: string }

type VideoMeta =
    { Id: string
      Title: string
      Duration: int
      ChannelId: string
      ChannelName: string
      UploadDate: string
      Captions: CaptionTrack list }

/// Thin wrapper around utils/yt-dlp.exe.
///
/// `-4` is mandatory: without it the caption/audio endpoints time out on this
/// network (IPv6 route). `-J` returns metadata *and* freshly signed caption
/// URLs — the URLs embedded in the watch page are stale and return 0 bytes.
module YtDlp =

    /// Resolve the configured path, walking up from the binary directory so
    /// `dotnet run` from the repo root and a published build both work.
    let resolvePath (configured: string) : string =
        let candidates =
            [ yield Path.GetFullPath configured
              yield Path.Combine(Directory.GetCurrentDirectory(), configured)
              let mutable dir = DirectoryInfo AppContext.BaseDirectory
              while not (isNull dir) do
                  yield Path.Combine(dir.FullName, configured)
                  dir <- dir.Parent ]

        candidates
        |> List.tryFind File.Exists
        |> Option.defaultValue (Path.GetFullPath configured)

    let private run (exe: string) (args: string) : Task<string * string * int> =
        task {
            let psi =
                ProcessStartInfo(
                    FileName = exe,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                )

            use proc = new Process(StartInfo = psi)
            proc.Start() |> ignore
            // Read both streams concurrently, otherwise a full pipe deadlocks.
            let! stdout = proc.StandardOutput.ReadToEndAsync()
            let! stderr = proc.StandardError.ReadToEndAsync()
            proc.WaitForExit()
            return stdout, stderr, proc.ExitCode
        }

    let private asString (el: JsonElement) (name: string) =
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> ""

    let private asInt (el: JsonElement) (name: string) =
        match el.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt32() with
            | true, n -> n
            | _ -> 0
        | _ -> 0

    let private parseCaptions (root: JsonElement) : CaptionTrack list =
        let fromObject (obj: JsonElement) (lang: string) =
            match obj.TryGetProperty lang with
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
                [ for t in arr.EnumerateArray() ->
                      { Lang = lang
                        Ext = asString t "ext"
                        Url = asString t "url" } ]
            | _ -> []

        match root.TryGetProperty "automatic_captions" with
        | true, caps when caps.ValueKind = JsonValueKind.Object -> fromObject caps "ru"
        | _ -> []

    /// `yt-dlp -4 -J` for one video.
    let fetchMeta (exe: string) (videoId: string) : Task<Result<VideoMeta, string>> =
        task {
            let args =
                sprintf "-4 --no-warnings --skip-download -J \"https://www.youtube.com/watch?v=%s\"" videoId

            let! stdout, stderr, code = run exe args

            if code <> 0 || String.IsNullOrWhiteSpace stdout then
                let reason =
                    if String.IsNullOrWhiteSpace stderr then sprintf "yt-dlp exit %d" code
                    else stderr.Trim().Split('\n') |> Array.last
                return Error reason
            else
                try
                    use doc = JsonDocument.Parse stdout
                    let root = doc.RootElement

                    return
                        Ok
                            { Id = asString root "id"
                              Title = asString root "title"
                              Duration = asInt root "duration"
                              ChannelId = asString root "channel_id"
                              ChannelName = asString root "channel"
                              UploadDate = asString root "upload_date"
                              Captions = parseCaptions root }
                with e ->
                    return Error e.Message
        }
