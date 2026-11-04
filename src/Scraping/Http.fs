module Scraping.Http

open System
open System.Net
open System.Net.Http
open System.Text
open System.Threading.Tasks
open System.IO

let userAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"

// Extract session cookies via yt-dlp to align HttpClient identity with your browser context
let private discoverBrowserCookies () : string =
    try
        let cookiesFile = Path.Combine(Path.GetTempPath(), "yt_scraper_cookies.txt")
        // Invokes yt-dlp to cleanly pull decrypted cookies out of Firefox's locked profile
        let psi = 
            System.Diagnostics.ProcessStartInfo(
                FileName = "yt-dlp.exe", 
                Arguments = sprintf "--cookies-from-browser firefox --cookies \"%s\" --version" cookiesFile,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            )
        use proc = System.Diagnostics.Process.Start(psi)
        proc.WaitForExit()
        
        if File.Exists(cookiesFile) then
            let lines = File.ReadAllLines(cookiesFile)
            // Filter out comments and format into a standard Cookie header string
            let cookiePairs = 
                lines 
                |> Array.filter (fun line -> not (line.StartsWith("#")) && not (String.IsNullOrWhiteSpace line))
                |> Array.choose (fun line ->
                    let parts = line.Split('\t')
                    if parts.Length >= 7 then Some (sprintf "%s=%s" parts.[5] parts.[6]) else None)
            File.Delete(cookiesFile) // Clean up temp storage
            String.concat "; " cookiePairs
        else
            "CONSENT=YES+1; SOCS=CAI"
    with _ -> 
        "CONSENT=YES+1; SOCS=CAI" // Fallback to safe defaults if browser is heavily locked

let private client =
    let handler = new HttpClientHandler(UseCookies = false) // Prevent default container overriding manually passed cookies
    let c = new HttpClient(handler, Timeout = TimeSpan.FromSeconds 60.0)
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent) |> ignore
    c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8") |> ignore
    
    let activeSessionCookies = discoverBrowserCookies()
    c.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", activeSessionCookies) |> ignore
    c

let private readUtf8 (resp: HttpResponseMessage) : Task<string> =
    task {
        let! bytes = resp.Content.ReadAsByteArrayAsync()
        return Encoding.UTF8.GetString bytes
    }

/// GET a URL and return the body as UTF-8 text. Throws on non-success status.
let getString (url: string) : Task<string> =
    task {
        use! resp = client.GetAsync(url)
        resp.EnsureSuccessStatusCode() |> ignore
        return! readUtf8 resp
    }

/// GET a URL and return the raw bytes (used for thumbnails).
let getBytes (url: string) : Task<byte[]> =
    task {
        use! resp = client.GetAsync(url)
        resp.EnsureSuccessStatusCode() |> ignore
        return! resp.Content.ReadAsByteArrayAsync()
    }

/// POST a JSON body to an InnerTube endpoint with the headers YouTube expects.
let postJson (url: string) (json: string) (extraHeaders: (string * string) list) : Task<string> =
    task {
        use req = new HttpRequestMessage(HttpMethod.Post, url)
        req.Content <- new StringContent(json, Encoding.UTF8, "application/json")
        for (name, value) in extraHeaders do
            req.Headers.TryAddWithoutValidation(name, value) |> ignore
        use! resp = client.SendAsync req
        resp.EnsureSuccessStatusCode() |> ignore
        return! readUtf8 resp
    }

/// GET that returns None instead of throwing (for optional lookups).
let tryGetString (url: string) : Task<string option> =
    task {
        try
            let! body = getString url
            return Some body
        with _ ->
            return None
    }

/// GET that reports the HTTP status instead of throwing, so callers can tell
/// "this video has no captions" (404) from "we are being rate-limited" (429).
let getStringStatus (url: string) : Task<Result<string, int>> =
    task {
        try
            let! body = getString url
            return Ok body
        with
        | :? HttpRequestException as e when e.StatusCode.HasValue -> return Error(int e.StatusCode.Value)
        | _ -> return Error -1
    }

/// GET that returns None instead of throwing (for optional binary lookups).
let tryGetBytes (url: string) : Task<byte[] option> =
    task {
        try
            let! bytes = getBytes url
            return Some bytes
        with _ ->
            return None
    }

/// Small helper so callers can `do! Http.delay 250`.
let delay (ms: int) : Task = Task.Delay ms

/// Delay with ±`spreadMs` of jitter. A crawl that sleeps exactly 4000 ms after
/// every video is trivially distinguishable from a human; the jitter costs
/// nothing and removes that constant.
let delayJitter (ms: int) (spreadMs: int) : Task =
    let spread = max 0 spreadMs
    let delta = if spread = 0 then 0 else Random.Shared.Next(-spread, spread + 1)
    Task.Delay(max 0 (ms + delta))

/// Introduces variable timing anomalies to disrupt rhythmic request pattern matching
let delayWithJitter (baseMs: int) (maxWindowMs: int) : Task =
    task {
        let rand = Random()
        let sleepDuration = baseMs + rand.Next(0, maxWindowMs)
        let! _ = Task.Delay sleepDuration
        return ()
    }

