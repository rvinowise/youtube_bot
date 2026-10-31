namespace Scraping

open System
open Npgsql
open Dapper

// ---------------------------------------------------------------- schema
// Same convention as twitter_bot: a class per table whose ToString() is the
// table name and whose members are the column names, plus a [<CLIMutable>]
// record per row. Dapper.FSharp reads the names off these.

type Query_table() =
    override _.ToString() = "query"
    member _.id = "id"
    member _.text = "text"
    member _.state = "state"
    member _.results = "results"

[<CLIMutable>]
type Query_row =
    { id: int64
      text: string
      state: string
      results: int }

type Channel_table() =
    override _.ToString() = "channel"
    member _.id = "id"
    member _.name = "name"
    member _.subs = "subs"
    member _.videos = "videos"
    member _.state = "state"

[<CLIMutable>]
type Channel_row =
    { id: string
      name: string
      subs: int
      videos: int
      state: string }

type Video_table() =
    override _.ToString() = "video"
    member _.id = "id"
    member _.channel_id = "channel_id"
    member _.title = "title"
    member _.duration = "duration"
    member _.published = "published"
    member _.state = "state"
    member _.attempts = "attempts"
    member _.last_error = "last_error"
    member _.has_captions = "has_captions"

[<CLIMutable>]
type Video_row =
    { id: string
      channel_id: string
      title: string
      duration: int
      published: Nullable<DateTime>
      state: string
      attempts: int
      last_error: string
      has_captions: bool }

type Transcript_table() =
    override _.ToString() = "transcript"
    member _.video_id = "video_id"
    member _.lang = "lang"
    member _.source = "source"
    member _.text = "text"
    member _.chars = "chars"

[<CLIMutable>]
type Transcript_row =
    { video_id: string
      lang: string
      source: string
      text: string
      chars: int }

type Hit_table() =
    override _.ToString() = "hit"
    member _.id = "id"
    member _.video_id = "video_id"
    member _.term = "term"
    member _.snippet = "snippet"
    member _.t_start = "t_start"
    member _.score = "score"

[<CLIMutable>]
type Hit_row =
    { id: int64
      video_id: string
      term: string
      snippet: string
      t_start: int
      score: float32 }

// ---------------------------------------------------------------- connection

module Db =

    let mutable private dataSource: NpgsqlDataSource = null

    let init (connectionString: string) =
        if String.IsNullOrWhiteSpace connectionString then
            failwith "database:connection_string is empty — check appsettings.json"
        dataSource <- NpgsqlDataSource.Create connectionString

    let openConnection () : NpgsqlConnection = dataSource.OpenConnection()

    let private ddl =
        """
create table if not exists query (
    id          bigserial primary key,
    text        text not null unique,
    state       text not null default 'pending',
    results     int  not null default 0,
    attempts    int  not null default 0,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now()
);

-- older databases predate the attempts column
alter table query add column if not exists attempts int not null default 0;

create table if not exists channel (
    id          text primary key,
    name        text not null default '',
    subs        int  not null default 0,
    videos      int  not null default 0,
    state       text not null default 'pending',
    updated_at  timestamptz not null default now()
);

create table if not exists video (
    id           text primary key,
    channel_id   text not null default '',
    title        text not null default '',
    duration     int  not null default 0,
    published    timestamptz null,
    state        text not null default 'pending',
    attempts     int  not null default 0,
    last_error   text null,
    has_captions boolean not null default false,
    updated_at   timestamptz not null default now()
);

create index if not exists video_state_idx   on video (state);
create index if not exists video_channel_idx on video (channel_id);

create table if not exists transcript (
    video_id   text primary key references video(id) on delete cascade,
    lang       text not null default 'ru',
    source     text not null default 'captions',
    text       text not null default '',
    chars      int  not null default 0,
    fetched_at timestamptz not null default now()
);

create table if not exists hit (
    id        bigserial primary key,
    video_id  text not null references video(id) on delete cascade,
    term      text not null,
    snippet   text not null default '',
    t_start   int  not null default 0,
    score     real not null default 0,
    unique (video_id, term, t_start)
);

create index if not exists hit_video_idx on hit (video_id);
"""

    /// Idempotent DDL. Safe to call on every start.
    let migrate () =
        use conn = openConnection()
        conn.Execute(ddl) |> ignore

    /// Transcripts that were stored before the `hit` table existed (or before
    /// the seed vocabulary changed) and therefore have no pre-computed hits.
    let transcriptsWithoutHits () =
        use conn = openConnection()

        conn.Query<Transcript_row>(
            """
select t.video_id, t.lang, t.source, t.text, t.chars
from transcript t
where not exists (select 1 from hit h where h.video_id = t.video_id)
"""
        )
        |> Seq.toList

    type HitListRow =
        { video_id: string
          title: string
          term: string
          t_start: int
          snippet: string }

    /// Pre-computed seed-term matches, with the timestamp of the first mention.
    let listHits (term: string) (limit: int) =
        use conn = openConnection()

        conn.Query<HitListRow>(
            """
select h.video_id, v.title, h.term, h.t_start, h.snippet
from hit h
join video v on v.id = h.video_id
where (@term = '' or h.term = @term)
order by h.term, h.t_start
limit @limit
""",
            {| term = term; limit = limit |}
        )
        |> Seq.toList

    /// Replace the hits of one video (idempotent per (video, term, t_start)).
    let insertHits (videoId: string) (hits: (string * string * int) list) =
        if not hits.IsEmpty then
            use conn = openConnection()

            for (term, snippet, startMs) in hits do
                conn.Execute(
                    """
insert into hit (video_id, term, snippet, t_start, score)
values (@video_id, @term, @snippet, @t_start, 1)
on conflict (video_id, term, t_start) do nothing
""",
                    {| video_id = videoId
                       term = term
                       snippet = snippet
                       t_start = startMs |}
                )
                |> ignore

    type VideoListRow =
        { id: string
          title: string
          state: string
          duration: int
          chars: int }

    /// Videos with their transcript size, newest first.
    let listVideos (state: string) (term: string) (limit: int) =
        use conn = openConnection()

        conn.Query<VideoListRow>(
            """
select v.id,
       v.title,
       v.state,
       v.duration,
       coalesce(t.chars, 0) as chars
from video v
left join transcript t on t.video_id = v.id
where (@state = '' or v.state = @state)
  and (@term = '' or v.title ilike @pattern)
order by v.updated_at desc
limit @limit
""",
            {| state = state
               term = term
               pattern = "%" + term + "%"
               limit = limit |}
        )
        |> Seq.toList

    /// Run arbitrary SQL and return (headers, rows) for a generic table dump.
    let runSql (sql: string) =
        use conn = openConnection()
        use cmd = new NpgsqlCommand(sql, conn)
        use reader = cmd.ExecuteReader()

        let headers = [ for i in 0 .. reader.FieldCount - 1 -> reader.GetName i ]

        let rows =
            [ while reader.Read() do
                  yield
                      [ for i in 0 .. reader.FieldCount - 1 ->
                            if reader.IsDBNull i then "" else string (reader.GetValue i) ] ]

        headers, rows

    type TranscriptHit =
        { video_id: string
          title: string
          snippet: string }

    /// Full-text-ish search over the spoken text. Returns a snippet around the
    /// first occurrence so the caller can judge the context.
    let searchTranscripts (term: string) (limit: int) =
        use conn = openConnection()

        conn.Query<TranscriptHit>(
            """
select v.id as video_id,
       v.title,
       substring(t.text from greatest(1, position(lower(@term) in lower(t.text)) - 120) for 320) as snippet
from transcript t
join video v on v.id = t.video_id
where t.text ilike @pattern
order by v.published desc nulls last
limit @limit
""",
            {| term = term
               pattern = "%" + term + "%"
               limit = limit |}
        )
        |> Seq.toList

    let stats () =
        use conn = openConnection()
        conn.Query<Query_row>("select id, text, state, results from query order by id") |> Seq.toList,
        conn.Query<Video_row>(
            "select id, channel_id, title, duration, published, state, attempts, last_error, has_captions from video"
        )
        |> Seq.toList,
        conn.Query<Transcript_row>("select video_id, lang, source, text, chars from transcript")
        |> Seq.toList
