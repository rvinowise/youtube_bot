/// Helpers for pulling data out of YouTube's `ytInitialData` blobs.
///
/// YouTube ships a huge JSON object inside the HTML page. Rather than
/// deserialising it (the schema changes weekly), we split the raw text on
/// renderer markers and pull fields with regexes. This is deliberately
/// tolerant: a missing field yields `None`, never an exception.
module Scraping.json

open System
open System.Globalization
open System.Text
open System.Text.RegularExpressions

let private cache = Collections.Concurrent.ConcurrentDictionary<string, Regex>()

let private rx (pattern: string) =
    cache.GetOrAdd(pattern, fun p -> Regex(p, RegexOptions.Singleline ||| RegexOptions.Compiled))

/// Decode the JSON string escapes that actually occur in YouTube payloads.
let unescape (s: string) : string =
    if String.IsNullOrEmpty s then s
    else
        let sb = StringBuilder(s.Length)
        let mutable i = 0
        while i < s.Length do
            let c = s.[i]
            if c = '\\' && i + 1 < s.Length then
                match s.[i + 1] with
                | 'u' when i + 5 < s.Length ->
                    let hex = s.Substring(i + 2, 4)
                    match Int32.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) with
                    | true, code ->
                        sb.Append(char code) |> ignore
                        i <- i + 6
                    | _ ->
                        sb.Append c |> ignore
                        i <- i + 1
                | '"' -> sb.Append '"' |> ignore; i <- i + 2
                | '\\' -> sb.Append '\\' |> ignore; i <- i + 2
                | '/' -> sb.Append '/' |> ignore; i <- i + 2
                | 'n' | 'r' | 't' -> sb.Append ' ' |> ignore; i <- i + 2
                | _ -> sb.Append c |> ignore; i <- i + 1
            else
                sb.Append c |> ignore
                i <- i + 1
        sb.ToString()

/// First capture group of `pattern` in `text`, or None.
let tryGroup (pattern: string) (text: string) (group: int) : string option =
    if String.IsNullOrEmpty text then None
    else
        let m = (rx pattern).Match text
        if m.Success then Some m.Groups.[group].Value else None

/// First capture group, JSON-unescaped.
let tryText (pattern: string) (text: string) : string option =
    tryGroup pattern text 1 |> Option.map unescape

/// All capture groups of `pattern` in `text`.
let allGroups (pattern: string) (text: string) (group: int) : string list =
    if String.IsNullOrEmpty text then []
    else [ for m in (rx pattern).Matches text -> m.Groups.[group].Value ]

/// Split a payload into per-renderer chunks. Index 0 is the preamble.
let splitBlocks (marker: string) (text: string) : string[] =
    if String.IsNullOrEmpty text then [||]
    else Regex.Split(text, Regex.Escape marker)

/// Take the first `limit` characters of a chunk (keeps regexes fast).
let clamp (limit: int) (s: string) =
    if isNull s then "" elif s.Length > limit then s.Substring(0, limit) else s

/// Extract a balanced `{...}` object that starts at the first `{` after `marker`.
/// Used to isolate `ytInitialData` from the surrounding HTML.
let extractBalanced (marker: string) (text: string) : string option =
    let start = text.IndexOf(marker, StringComparison.Ordinal)
    if start < 0 then None
    else
        let braceStart = text.IndexOf('{', start)
        if braceStart < 0 then None
        else
            let mutable depth = 0
            let mutable inString = false
            let mutable escaped = false
            let mutable finish = -1
            let mutable i = braceStart
            while finish < 0 && i < text.Length do
                let c = text.[i]
                if inString then
                    if escaped then escaped <- false
                    elif c = '\\' then escaped <- true
                    elif c = '"' then inString <- false
                elif c = '"' then inString <- true
                elif c = '{' then depth <- depth + 1
                elif c = '}' then
                    depth <- depth - 1
                    if depth = 0 then finish <- i
                i <- i + 1
            if finish < 0 then None else Some(text.Substring(braceStart, finish - braceStart + 1))

/// Longest continuation token in a payload (grid continuations are ~2 KB,
/// the short ones belong to "about"/menu sections).
let longestToken (text: string) : string option =
    let pattern = @"""continuationCommand"":\{""token"":""((?:[^""\\]|\\.)*)"""
    allGroups pattern text 1
    |> List.sortByDescending (fun t -> t.Length)
    |> List.tryHead
    |> Option.map unescape
