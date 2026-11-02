namespace Scraping

open System
open Npgsql
open Dapper

// ---------------------------------------------------------------- schema
// Same convention as twitter_bot: a class per table whose ToString() is the
// table name and whose members are the column names, plus a [<CLIMutable>]
// record per row.

type Topic_table() =
    override _.ToString() = "topic"
    member _.id = "id"
    member _.name = "name"
    member _.stems = "stems"

[<CLIMutable>]
type Topic_row =
    { id: int64
      name: string
      stems: string[] }

type Frontier_table() =
    override _.ToString() = "frontier"
    member _.topic_id = "topic_id"
    member _.video_id = "video_id"
    member _.depth = "depth"
    member _.parent_id = "parent_id"
    member _.state = "state"
    member _.attempts = "attempts"

[<CLIMutable>]
type Frontier_row =
    { topic_id: int64
      video_id: string
      depth: int
      parent_id: string
      state: string
      attempts: int
      priority: float }

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
    member _.depth = "depth"
    member _.parent_id = "parent_id"
    member _.discovered_via = "discovered_via"

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
      has_captions: bool
      depth: int
      parent_id: string
      discovered_via: string }

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
    member _.topic_id = "topic_id"
    member _.video_id = "video_id"
    member _.term = "term"
    member _.snippet = "snippet"
    member _.t_start = "t_start"
    member _.score = "score"

[<CLIMutable>]
type Hit_row =
    { topic_id: int64
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
create table if not exists topic (
    id         bigserial primary key,
    name       text not null unique,
    stems      text[] not null,
    created_at timestamptz not null default now()
);

create table if not exists frontier (
    topic_id  bigint not null references topic(id) on delete cascade,
    video_id  text not null,
    depth     int  not null default 0,
    parent_id text null,
    state     text not null default 'queued',
    attempts  int  not null default 0,
    added_at  timestamptz not null default now(),
    next_attempt_at timestamptz not null default now(),
    priority  real not null default 0,
    primary key (topic_id, video_id)
);

alter table frontier add column if not exists next_attempt_at timestamptz not null default now();
alter table frontier add column if not exists priority real not null default 0;

create index if not exists frontier_queue_idx on frontier (topic_id, state, priority, next_attempt_at);

create table if not exists video (
    id             text primary key,
    channel_id     text not null default '',
    channel_name   text not null default '',
    channel_url    text not null default '',
    title          text not null default '',
    duration       int  not null default 0,
    published      timestamptz null,
    state          text not null default 'pending',
    attempts       int  not null default 0,
    last_error     text null,
    has_captions   boolean not null default false,
    depth          int  not null default 0,
    parent_id      text null,
    discovered_via text not null default 'seed',
    updated_at     timestamptz not null default now()
);

alter table video add column if not exists depth          int  not null default 0;
alter table video add column if not exists parent_id      text null;
alter table video add column if not exists discovered_via text not null default 'seed';
alter table video add column if not exists channel_name   text not null default '';
alter table video add column if not exists channel_url    text not null default '';
alter table video add column if not exists channel_followers int not null default 0;

create index if not exists video_state_idx   on video (state);
create index if not exists video_channel_idx on video (channel_id);

create table if not exists transcript_segment (
    video_id text not null references video(id) on delete cascade,
    idx      int  not null,
    start_ms int  not null,
    text     text not null,
    primary key (video_id, idx)
);

create table if not exists transcript (
    video_id   text primary key references video(id) on delete cascade,
    lang       text not null default 'ru',
    source     text not null default 'captions',
    text       text not null default '',
    chars      int  not null default 0,
    fetched_at timestamptz not null default now()
);

-- v0.1 had a topic-less hit table; it is derived data, so drop and recreate.
do $$
begin
    if exists (
        select 1 from information_schema.columns
        where table_name = 'hit' and column_name = 'id'
    ) then
        drop table hit;
    end if;
end $$;

create table if not exists hit (
    topic_id bigint not null references topic(id) on delete cascade,
    video_id text   not null references video(id) on delete cascade,
    term     text   not null,
    snippet  text   not null default '',
    t_start  int    not null default 0,
    score    real   not null default 0,
    unique (topic_id, video_id, term, t_start)
);

create index if not exists hit_topic_idx on hit (topic_id, term);
"""

    /// Idempotent DDL. Safe to call on every start.
    let migrate () =
        use conn = openConnection()
        conn.Execute(ddl) |> ignore

    // ------------------------------------------------------------ topics

    /// Create the topic if missing, return its id. Stems are only written on
    /// creation, so an existing topic keeps its vocabulary.
    let ensureTopic (name: string) (stems: string list) : int64 =
        use conn = openConnection()

        conn.Execute(
            "insert into topic (name, stems) values (@name, @stems) on conflict (name) do nothing",
            {| name = name; stems = List.toArray stems |}
        )
        |> ignore

        conn.ExecuteScalar<int64>("select id from topic where name = @name", {| name = name |})

    let topicIdByName (name: string) : int64 option =
        use conn = openConnection()

        match conn.ExecuteScalar<Nullable<int64>>("select id from topic where name = @name", {| name = name |}) with
        | n when n.HasValue -> Some n.Value
        | _ -> None

    /// Replace the vocabulary of an existing topic (used when `--stems` is
    /// passed explicitly: without this, the stems of an existing topic were
    /// silently ignored).
    let setTopicStems (topicId: int64) (stems: string list) =
        use conn = openConnection()

        conn.Execute(
            "update topic set stems = @stems where id = @id",
            {| id = topicId; stems = List.toArray stems |}
        )
        |> ignore

    let topicStems (topicId: int64) : string list =
        use conn = openConnection()

        conn.ExecuteScalar<string[]>("select stems from topic where id = @id", {| id = topicId |})
        |> List.ofArray

    type TopicStat =
        { id: int64
          name: string
          stems: string
          queued: int64
          expanded: int64
          videos: int64
          hits: int64 }

    let listTopics () =
        use conn = openConnection()

        conn.Query<TopicStat>(
            """
select t.id,
       t.name,
       array_to_string(t.stems, ', ') as stems,
       (select count(*) from frontier f where f.topic_id = t.id and f.state = 'queued')   as queued,
       (select count(*) from frontier f where f.topic_id = t.id and f.state = 'expanded') as expanded,
       (select count(*) from video v
          join frontier f2 on f2.video_id = v.id and f2.topic_id = t.id
         where v.state = 'done')                                                          as videos,
       (select count(*) from hit h where h.topic_id = t.id)                               as hits
from topic t
order by t.id
"""
        )
        |> Seq.toList

    // ------------------------------------------------------------ frontier

    /// Crash recovery: anything left `running` goes back to the queue.
    let resetRunning (topicId: int64) =
        use conn = openConnection()

        conn.Execute(
            "update frontier set state = 'queued', next_attempt_at = now() where topic_id = @topic and state = 'running'",
            {| topic = topicId |}
        )
        |> ignore

    /// Add a video to the topic's queue. Idempotent.
    ///
    /// `priority` is inherited from the parent and bumped by the parent's own
    /// hits, so the crawl follows the thread that is paying off.
    let enqueue (topicId: int64) (videoId: string) (depth: int) (parentId: string) (via: string) (priority: float) =
        use conn = openConnection()

        conn.Execute(
            """
insert into video (id, depth, parent_id, discovered_via)
values (@id, @depth, @parent, @via)
on conflict (id) do nothing
""",
            {| id = videoId
               depth = depth
               parent = (if isNull parentId then null else box parentId)
               via = via |}
        )
        |> ignore

        conn.Execute(
            """
insert into frontier (topic_id, video_id, depth, parent_id, priority)
values (@topic, @video, @depth, @parent, @priority)
on conflict (topic_id, video_id) do update
set priority = greatest(frontier.priority, excluded.priority)
""",
            {| topic = topicId
               video = videoId
               depth = depth
               parent = (if isNull parentId then null else box parentId)
               priority = priority |}
        )
        |> ignore

    /// Take the highest-priority queued item and mark it `running`.
    ///
    /// Best-first: `priority` is accumulated down the tree from the seeds, so a
    /// video whose parent mentioned the topic is expanded long before an
    /// equally shallow video from an unrelated neighbourhood.
    let claimFrontier (topicId: int64) : Frontier_row =
        use conn = openConnection()

        conn.QuerySingleOrDefault<Frontier_row>(
            """
update frontier set state = 'running', attempts = attempts + 1
where (topic_id, video_id) = (
    select topic_id, video_id
    from frontier
    where topic_id = @topic and state = 'queued' and next_attempt_at <= now()
    order by priority desc, depth, next_attempt_at
    limit 1
    for update skip locked
)
returning topic_id, video_id, depth, parent_id, state, attempts, priority
""",
            {| topic = topicId |}
        )

    let markExpanded (topicId: int64) (videoId: string) =
        use conn = openConnection()

        conn.Execute(
            "update frontier set state = 'expanded' where topic_id = @topic and video_id = @video",
            {| topic = topicId; video = videoId |}
        )
        |> ignore

    /// After a transient failure: retry with exponential backoff, give up after
    /// 5 attempts. The backoff matters: a 429 would otherwise be retried at
    /// once, burn the attempt budget and mark a perfectly good video `failed`.
    let markFrontierRetry (topicId: int64) (videoId: string) (attempts: int) =
        use conn = openConnection()

        let state = if attempts >= 5 then "failed" else "queued"
        let delaySeconds = attempts * attempts * 30

        conn.Execute(
            """
update frontier
set state = @state,
    next_attempt_at = now() + make_interval(secs => @delay)
where topic_id = @topic and video_id = @video
""",
            {| state = state
               delay = delaySeconds
               topic = topicId
               video = videoId |}
        )
        |> ignore

    let markFrontierFailed (topicId: int64) (videoId: string) =
        use conn = openConnection()

        conn.Execute(
            "update frontier set state = 'failed' where topic_id = @topic and video_id = @video",
            {| topic = topicId; video = videoId |}
        )
        |> ignore

    let queuedCount (topicId: int64) : int =
        use conn = openConnection()

        conn.ExecuteScalar<int64>(
            "select count(*) from frontier where topic_id = @topic and state = 'queued'",
            {| topic = topicId |}
        )
        |> int

    // ------------------------------------------------------------ video / transcript

    let getTranscript (videoId: string) : Transcript_row =
        use conn = openConnection()

        conn.QuerySingleOrDefault<Transcript_row>(
            "select video_id, lang, source, text, chars from transcript where video_id = @id",
            {| id = videoId |}
        )

    /// Keep the timestamped segments, so `backfill --reset` can recompute hits
    /// with real timestamps instead of a flat string.
    let saveSegments (videoId: string) (segments: (int * string) list) =
        use conn = openConnection()

        conn.Execute("delete from transcript_segment where video_id = @id", {| id = videoId |})
        |> ignore

        segments
        |> List.iteri (fun i (startMs, text) ->
            conn.Execute(
                "insert into transcript_segment (video_id, idx, start_ms, text) values (@video, @idx, @start, @text)",
                {| video = videoId
                   idx = i
                   start = startMs
                   text = text |}
            )
            |> ignore)

    let getSegments (videoId: string) : (int * string) list =
        use conn = openConnection()

        conn.Query<int * string>(
            "select start_ms, text from transcript_segment where video_id = @id order by idx",
            {| id = videoId |}
        )
        |> Seq.toList

    /// Videos of the topic whose hits have no timestamp (scored from flat text).
    let videosWithUntimedHits (topicId: int64) (limit: int) : string list =
        use conn = openConnection()

        let lim = if limit <= 0 then Nullable<int>() else Nullable limit

        conn.Query<string>(
            """
select distinct h.video_id
from hit h
where h.topic_id = @topic and h.t_start = 0
limit @limit
""",
            {| topic = topicId; limit = lim |}
        )
        |> Seq.toList

    let deleteHitsForVideo (topicId: int64) (videoId: string) =
        use conn = openConnection()

        conn.Execute(
            "delete from hit where topic_id = @topic and video_id = @video",
            {| topic = topicId; video = videoId |}
        )
        |> ignore

    let saveTranscript (videoId: string) (text: string) =
        use conn = openConnection()

        conn.Execute(
            """
insert into transcript (video_id, lang, source, text, chars)
values (@video_id, 'ru', 'captions', @text, @chars)
on conflict (video_id) do update
set text = excluded.text, chars = excluded.chars, fetched_at = now()
""",
            {| video_id = videoId
               text = text
               chars = text.Length |}
        )
        |> ignore

    let markVideoDone
        (videoId: string)
        (title: string)
        (channelId: string)
        (channelName: string)
        (followers: int)
        (duration: int)
        (published: Nullable<DateTime>)
        =
        use conn = openConnection()

        conn.Execute(
            """
update video set state = 'done', has_captions = true, attempts = attempts + 1,
       last_error = null, duration = @duration, published = @published,
       title = case when @title = '' then title else @title end,
       channel_id = case when @channel_id = '' then channel_id else @channel_id end,
       channel_name = case when @channel_name = '' then channel_name else @channel_name end,
       channel_url = case when @channel_id = '' then channel_url
                          else 'https://www.youtube.com/channel/' || @channel_id end,
       channel_followers = greatest(channel_followers, @followers),
       updated_at = now()
where id = @id
""",
            {| duration = duration
               published = published
               title = title
               channel_id = channelId
               channel_name = channelName
               followers = followers
               id = videoId |}
        )
        |> ignore

    /// Fill title / channel for a video we already know (oEmbed backfill).
    let setVideoMeta (videoId: string) (title: string) (channelName: string) (channelUrl: string) =
        use conn = openConnection()

        conn.Execute(
            """
update video set title = @title, channel_name = @channel_name, channel_url = @channel_url,
       updated_at = now()
where id = @id
""",
            {| title = title
               channel_name = channelName
               channel_url = channelUrl
               id = videoId |}
        )
        |> ignore

    type ChannelNoSubs =
        { name: string
          url: string }

    /// Channels whose subscriber count we never learned, busiest first.
    let channelsWithoutSubs (limit: int) : ChannelNoSubs list =
        use conn = openConnection()

        let lim = if limit <= 0 then Nullable<int>() else Nullable limit

        conn.Query<ChannelNoSubs>(
            """
select channel_name as name, min(channel_url) as url
from video
where channel_followers = 0 and channel_url <> '' and channel_name <> ''
group by channel_name
order by count(*) desc
limit @limit
""",
            {| limit = lim |}
        )
        |> Seq.toList

    let setChannelSubs (channelName: string) (subs: int) =
        use conn = openConnection()

        conn.Execute(
            "update video set channel_followers = @subs where channel_name = @channel and channel_followers = 0",
            {| subs = subs; channel = channelName |}
        )
        |> ignore

    let videoChannel (videoId: string) : string =
        use conn = openConnection()

        conn.ExecuteScalar<string>(
            "select coalesce(channel_name, '') from video where id = @id",
            {| id = videoId |}
        )

    /// How many videos of this channel the topic has already expanded.
    let channelExpandedCount (topicId: int64) (channel: string) : int =
        use conn = openConnection()

        conn.ExecuteScalar<int64>(
            """
select count(*)
from frontier f
join video v on v.id = f.video_id
where f.topic_id = @topic and f.state = 'expanded' and v.channel_name = @channel
""",
            {| topic = topicId; channel = channel |}
        )
        |> int

    /// Videos still missing a title, shallowest first. `limit <= 0` = all of
    /// them (`LIMIT 0` in Postgres would return nothing, hence the NULL).
    let videosWithoutTitles (limit: int) : string list =
        use conn = openConnection()

        let lim = if limit <= 0 then Nullable<int>() else Nullable limit

        conn.Query<string>(
            "select id from video where title = '' order by depth, id limit @limit",
            {| limit = lim |}
        )
        |> Seq.toList

    /// The transcript was fetched earlier by another topic (or an earlier run),
    /// so yt-dlp never saw this video now. Still move it out of `pending`.
    let markVideoCached (videoId: string) =
        use conn = openConnection()

        conn.Execute(
            """
update video set state = 'done', has_captions = true, last_error = null, updated_at = now()
where id = @id and state <> 'done'
""",
            {| id = videoId |}
        )
        |> ignore

    let markVideoNoCaptions
        (videoId: string)
        (title: string)
        (channelId: string)
        (channelName: string)
        (followers: int)
        (reason: string)
        (duration: int)
        =
        use conn = openConnection()

        conn.Execute(
            """
update video set state = 'no_captions', has_captions = false, attempts = attempts + 1,
       last_error = @err, duration = @duration,
       title = case when @title = '' then title else @title end,
       channel_id = case when @channel_id = '' then channel_id else @channel_id end,
       channel_name = case when @channel_name = '' then channel_name else @channel_name end,
       channel_url = case when @channel_id = '' then channel_url
                          else 'https://www.youtube.com/channel/' || @channel_id end,
       channel_followers = greatest(channel_followers, @followers),
       updated_at = now()
where id = @id
""",
            {| err = reason
               duration = duration
               title = title
               channel_id = channelId
               channel_name = channelName
               followers = followers
               id = videoId |}
        )
        |> ignore

    /// Delete every hit of a topic, so `backfill` can recompute them after the
    /// vocabulary changed. Nothing else is touched: the frontier and the video
    /// table are history, not a match set.
    let clearHits (topicId: int64) : int =
        use conn = openConnection()

        conn.Execute("delete from hit where topic_id = @topic", {| topic = topicId |})

    let markVideoFailed (videoId: string) (reason: string) =
        use conn = openConnection()

        conn.Execute(
            """
update video set state = 'failed', attempts = attempts + 1, last_error = @err, updated_at = now()
where id = @id
""",
            {| err = reason; id = videoId |}
        )
        |> ignore

    /// Put every failed item of a topic back in the queue and clear the retry
    /// counters. Needed after the YouTube bot check or a transient outage --
    /// otherwise those videos stay failed forever.
    let requeueFailed (topicId: int64) (alsoNoCaptions: bool) : int =
        use conn = openConnection()

        let states = if alsoNoCaptions then [ "failed"; "no_captions" ] else [ "failed" ]

        let n =
            conn.Execute(
                """
update frontier set state = 'queued', attempts = 0, next_attempt_at = now()
where topic_id = @topic and state = any(@states)
""",
                {| topic = topicId; states = List.toArray states |}
            )

        conn.Execute(
            """
update video v set attempts = 0, last_error = null, state = 'pending', updated_at = now()
where v.state in ('failed', 'no_captions')
  and exists (select 1 from frontier f where f.video_id = v.id and f.topic_id = @topic and f.state = 'queued')
""",
            {| topic = topicId |}
        )
        |> ignore

        n

    // ------------------------------------------------------------ hits

    /// Insert the hits of one video for one topic. Idempotent.
    let insertHits (topicId: int64) (videoId: string) (hits: (string * string * int) list) =
        if not hits.IsEmpty then
            use conn = openConnection()

            for (term, snippet, startMs) in hits do
                conn.Execute(
                    """
insert into hit (topic_id, video_id, term, snippet, t_start, score)
values (@topic, @video, @term, @snippet, @t_start, 1)
on conflict (topic_id, video_id, term, t_start) do nothing
""",
                    {| topic = topicId
                       video = videoId
                       term = term
                       snippet = snippet
                       t_start = startMs |}
                )
                |> ignore

    type HitListRow =
        { video_id: string
          title: string
          channel: string
          term: string
          t_start: int
          snippet: string
          depth: int }

    let listHits (topicName: string) (term: string) (limit: int) =
        use conn = openConnection()

        conn.Query<HitListRow>(
            """
select h.video_id, v.title, v.channel_name as channel, h.term, h.t_start, h.snippet, v.depth
from hit h
join video v on v.id = h.video_id
join topic t on t.id = h.topic_id
where (@topic = '' or t.name = @topic)
  and (@term = '' or h.term = @term)
order by v.depth, h.t_start
limit @limit
""",
            {| topic = topicName; term = term; limit = limit |}
        )
        |> Seq.toList

    // ------------------------------------------------------------ browsing

    type VideoListRow =
        { id: string
          title: string
          channel: string
          state: string
          duration: int
          chars: int
          depth: int
          discovered_via: string }

    let listVideos (state: string) (term: string) (limit: int) =
        use conn = openConnection()

        conn.Query<VideoListRow>(
            """
select v.id, v.title, v.channel_name as channel, v.state, v.duration,
       coalesce(t.chars, 0) as chars, v.depth, v.discovered_via
from video v
left join transcript t on t.video_id = v.id
where (@state = '' or v.state = @state)
  and (@term = '' or v.title ilike @pattern or v.channel_name ilike @pattern)
order by v.updated_at desc
limit @limit
""",
            {| state = state
               term = term
               pattern = "%" + term + "%"
               limit = limit |}
        )
        |> Seq.toList

    type ChannelRow =
        { channel: string
          url: string
          subs: int64
          videos: int64
          mentions: int64
          depth: int
          sample: string }

    /// Hits grouped by channel: the point is to find *people*, not videos.
    /// A channel with 3 mentions in 3 different videos is a much better lead
    /// than a factory channel with 1 mention in 1 video. `maxSubs > 0` keeps
    /// only channels small enough to still be "a beginner blogger".
    let listChannels (topicName: string) (term: string) (limit: int) (maxSubs: int) =
        use conn = openConnection()

        conn.Query<ChannelRow>(
            """
select v.channel_name as channel,
       coalesce((array_agg(distinct v.channel_url))[1], '') as url,
       max(v.channel_followers) as subs,
       count(distinct h.video_id) as videos,
       count(*) as mentions,
       min(v.depth) as depth,
       (array_agg(v.title order by v.depth))[1] as sample
from hit h
join video v on v.id = h.video_id
join topic t on t.id = h.topic_id
where (@topic = '' or t.name = @topic)
  and (@term = '' or h.term = @term)
  and v.channel_name <> ''
group by v.channel_name
having (@max_subs <= 0 or max(v.channel_followers) <= @max_subs)
order by mentions desc, videos desc
limit @limit
""",
            {| topic = topicName
               term = term
               limit = limit
               max_subs = maxSubs |}
        )
        |> Seq.toList

    type TranscriptHit =
        { video_id: string
          title: string
          snippet: string }

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

    /// Transcripts of this topic that have no hits yet (for `backfill`).
    let transcriptsWithoutHits (topicId: int64) =
        use conn = openConnection()

        conn.Query<Transcript_row>(
            """
select t.video_id, t.lang, t.source, t.text, t.chars
from transcript t
join frontier f on f.video_id = t.video_id and f.topic_id = @topic
where not exists (select 1 from hit h where h.video_id = t.video_id and h.topic_id = @topic)
""",
            {| topic = topicId |}
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

    let stats () =
        use conn = openConnection()

        let scalar (sql: string) = conn.ExecuteScalar<int64>(sql) |> int
        scalar "select count(*) from topic",
        scalar "select count(*) from frontier",
        scalar "select count(*) from video",
        scalar "select count(*) from transcript",
        scalar "select count(*) from hit"
