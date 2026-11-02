namespace Scraping

/// A single video found in search results or on a channel page.
type Video =
    { Id: string
      Title: string
      Channel: string
      ChannelId: string
      Published: string
      AgeDays: int
      Views: string
      Url: string }

/// Channel size metadata scraped from the "about" page.
type ChannelInfo =
    { Id: string
      Name: string
      Subs: int
      SubsText: string
      Videos: int
      VideosText: string }

/// A channel that matched at least one query, before the topic-dispersion scan.
type Candidate =
    { Source: string
      ChannelId: string
      Name: string
      Subs: int
      SubsText: string
      Videos: int
      VideosText: string
      Matches: int
      Sample: string
      Url: string }

/// How well a channel fits the "beginner who mentioned it in passing" profile.
type Profile =
    /// Small channel, few seed hits -> the topic is still assignable.
    | Target
    /// The channel is already built around the topic.
    | Dedicated
    /// No seed words in titles (the mention was only in speech/description).
    | NoSeedHits

    member this.Label =
        match this with
        | Target -> "TARGET (mentioned in passing)"
        | Dedicated -> "dedicated (too on-topic)"
        | NoSeedHits -> "no seed hits"

    member this.Rank =
        match this with
        | Target -> 0
        | Dedicated -> 1
        | NoSeedHits -> 2

/// A single video that matched a seed word, with a clickable URL.
type VideoHit =
    { Id: string
      Title: string
      Url: string }

/// Result of scanning all titles of a channel for seed words.
type ScannedChannel =
    { Profile: Profile
      Name: string
      ChannelId: string
      SubsText: string
      Videos: int
      SeedHits: int
      HitRatio: float
      HitTitles: string
      /// The matching videos themselves, so the URLs are not lost.
      HitVideos: VideoHit list
      Url: string }

/// A topic is a named set of terms. A term is a `+`-joined conjunction of
/// stems, and matching is regex-based on word boundaries:
///
///   `"вечн+жизн"`  ->  \bвечн\w*\W+жизн\w*     "вечная жизнь", not "лететь вечно"
///   `"бессмерт"`   ->  \bбессмерт\w*           "бессмертие", not "обессмертить"
///
/// `\b` (start of word) + stem + `\w*` (any word ending) is what stops
/// "перезагрузка" from matching "загрузк", and `\W+` between stems requires
/// them to be adjacent words rather than scattered across a long caption.
module HitTerms =

    open System.Collections.Concurrent
    open System.Text.RegularExpressions

    /// Default vocabulary for the immortality / transhumanism topic.
    let defaultStems =
        [ "бессмерт"
          "иммортал"
          "трансгуман"
          "крионик"
          "вечн+жизн"
          "цифров+бессмерт"
          "продлени+жизн"
          "старени"
          // "воскрес" matched "воскресенье" (Sunday); the resurrection stem is
          // "воскресени" (воскресение / воскрешение).
          "воскресени"
          // Digital uploading: two separate terms, both word-bounded, so
          // "перезагрузка" no longer fires.
          "загрузк+сознани"
          "оцифровк+сознани" ]

    /// `"загрузк+сознани"` -> `["загрузк"; "сознани"]`.
    let stemsOf (term: string) : string list =
        term.Split '+'
        |> Array.map (fun s -> s.Trim().ToLowerInvariant())
        |> Array.filter (fun s -> s <> "")
        |> List.ofArray

    let private cache = ConcurrentDictionary<string, Regex>()

    /// Compiled regex for one term. Stems are regex-escaped, so a stray `.` or
    /// `(` in the vocabulary cannot break the matcher.
    let regex (term: string) : Regex =
        cache.GetOrAdd(
            term,
            fun t ->
                let pattern =
                    stemsOf t
                    |> List.map (fun s -> @"\b" + Regex.Escape s + @"\w*")
                    |> String.concat @"\W+"

                Regex(pattern, RegexOptions.Compiled ||| RegexOptions.IgnoreCase)
        )

    /// True when the term matches anywhere in `text`.
    let matches (text: string) (term: string) =
        not (System.String.IsNullOrEmpty text) && (regex term).IsMatch text

    /// Position of the match, 0 when there is none.
    let firstIndex (text: string) (term: string) =
        let m = (regex term).Match text
        if m.Success then m.Index else 0

module Domain =
    /// Parse "1,88 тыс. подписчиков" / "1,2 млн" / "759 подписчиков" into a number.
    let parseCount (text: string) : int =
        if System.String.IsNullOrWhiteSpace text then 0
        else
            let m = System.Text.RegularExpressions.Regex.Match(text, @"(\d[\d\s.,]*)")
            if not m.Success then 0
            else
                // YouTube uses NBSP (U+00A0) as a thousands separator, so strip
                // every kind of whitespace, not just ' '.
                let raw =
                    System.Text.RegularExpressions.Regex.Replace(m.Groups.[1].Value, @"\s", "").Replace(",", ".")
                match System.Double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                | true, value ->
                    let multiplier =
                        if text.Contains "тыс" then 1000.0
                        elif text.Contains "млн" then 1_000_000.0
                        else 1.0
                    int (value * multiplier)
                | _ -> 0
