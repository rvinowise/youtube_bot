module Scraping.Http

open System
open System.Net.Http
open System.Text
open System.Threading.Tasks

let userAgent =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"

let private client =
    let c = new HttpClient(Timeout = TimeSpan.FromSeconds 60.0)
    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent) |> ignore
    c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8") |> ignore
    c.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "CONSENT=YES+1; SOCS=CAI") |> ignore
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
