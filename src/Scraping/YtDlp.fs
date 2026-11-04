namespace Scraping

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Serilog

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
      /// Subscriber count at scan time. 0 when YouTube hides it.
      Followers: int
      UploadDate: string
      /// Everything below is free in the same `-J` response and impossible to
      /// recover later without scanning the video again — so it is stored.
      Description: string
      ViewCount: int
      LikeCount: int
      Tags: string list
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

    /// Errors that will never succeed on retry: the video is gone, private or
    /// age-gated. NOTE: "requested format is not available" is NOT in this list
    /// any more -- `--ignore-no-formats-error` (always passed below) turns it
    /// into a successful run, because we only need the caption URLs.
    let isPermanentError (message: string) =
        let m = message.ToLowerInvariant()

        [ "video unavailable"
          "private video"
          "this video is not available"
          "has been removed"
          "members-only"
          "confirm your age"
          "age-restricted"
          "account associated with this video has been terminated" ]
        |> List.exists m.Contains

    /// `-4` is mandatory (IPv6 route times out), cookies defeat the bot check.
    ///
    /// `--ignore-no-formats-error` is mandatory too: YouTube's `n` challenge
    /// makes yt-dlp drop every real format (it needs a JS runtime), so a plain
    /// `-J` exits non-zero with "Requested format is not available" even though
    /// `automatic_captions` is fully populated. We never download media, only
    /// caption URLs, so those missing formats are irrelevant.
    /// Round-robin over the configured browser profiles. One account per call,
    /// so a penalty earned by one identity does not slow the others; a single
    /// `cookies_from_browser` stays as the fallback when no list is configured.
    let mutable private profileCursor = 0
    let mutable private lastProfile = ""

    let private cookiesArg () =
        match Settings.cookieProfiles with
        | [] ->
            if String.IsNullOrWhiteSpace Settings.cookiesFromBrowser then ""
            else sprintf "--cookies-from-browser \"%s\" " Settings.cookiesFromBrowser
        | profiles ->
            let i = profileCursor
            profileCursor <- (profileCursor + 1) % profiles.Length
            let profile = profiles.[i]

            if profile <> lastProfile then
                lastProfile <- profile
                Log.Information("cookies: using {Profile} (profile {Index} of {Count})", profile, i + 1, profiles.Length)

            sprintf "--cookies-from-browser \"%s\" " profile

    let private buildArgs (videoId: string) =
        let cookies = cookiesArg ()

        let extra =
            if String.IsNullOrWhiteSpace Settings.extraArgs then ""
            else Settings.extraArgs.Trim() + " "

        sprintf
            "-4 %s--ignore-no-formats-error %s--no-warnings --skip-download -J \"https://www.youtube.com/watch?v=%s\""
            cookies
            extra
            videoId

    /// `yt-dlp -4 -J` for one video.
    let fetchMeta (exe: string) (videoId: string) : Task<Result<VideoMeta, string>> =
        task {
            let args = buildArgs videoId

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

                    let tags =
                        match root.TryGetProperty "tags" with
                        | true, arr when arr.ValueKind = JsonValueKind.Array ->
                            [ for t in arr.EnumerateArray() do
                                  if t.ValueKind = JsonValueKind.String then yield t.GetString() ]
                        | _ -> []

                    return
                        Ok
                            { Id = asString root "id"
                              Title = asString root "title"
                              Duration = asInt root "duration"
                              ChannelId = asString root "channel_id"
                              ChannelName = asString root "channel"
                              Followers = asInt root "channel_follower_count"
                              UploadDate = asString root "upload_date"
                              Description = asString root "description"
                              ViewCount = asInt root "view_count"
                              LikeCount = asInt root "like_count"
                              Tags = tags
                              Captions = parseCaptions root }
                with e ->
                    return Error e.Message
        }
