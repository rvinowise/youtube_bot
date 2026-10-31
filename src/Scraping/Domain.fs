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

/// Vocabulary that gets pre-computed into the `hit` table at caption time.
/// (display form, stems that must ALL be present in the same segment)
///
/// Multi-word terms need every stem: matching "цифров" alone hits
/// "матриц цифровых камер", which is not about digital immortality at all.
module HitTerms =
    let all =
        [ "бессмертие", [ "бессмерт" ]
          "иммортализм", [ "иммортал" ]
          "трансгуманизм", [ "трансгуман" ]
          "крионика", [ "крионик" ]
          "вечная жизнь", [ "вечн"; "жизн" ]
          "загрузка сознания", [ "загрузк"; "сознани" ]
          "цифровое бессмертие", [ "цифров"; "бессмерт" ]
          "продление жизни", [ "продлени"; "жизн" ]
          "старение", [ "старени" ]
          "воскрешение", [ "воскрес" ] ]

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
