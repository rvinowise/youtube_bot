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
    { name: string
      stems: string[]  }

type Frontier_table() =
    override _.ToString() = "frontier"
    member _.topic_name = "topic_name"
    member _.video_id = "video_id"
    member _.depth = "depth"
    member _.parent_id = "parent_id"
    member _.state = "state"
    member _.attempts = "attempts"

[<CLIMutable>]
type Frontier_row =
    { topic_name: string
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
    member _.topic_name = "topic_name"
    member _.video_id = "video_id"
    member _.term = "term"
    member _.snippet = "snippet"
    member _.t_start = "t_start"
    member _.score = "score"

[<CLIMutable>]
type Hit_row =
    { topic_name: string
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
-- The topic NAME is the natural key: it is what pages merge on, so every
-- child table references it instead of a local serial.
create table if not exists topic (
    name       text primary key,
    stems      text[] not null,
    exclude    text[] not null default '{}',
    created_at timestamptz not null default now()
);

alter table topic add column if not exists exclude text[] not null default '{}';

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
    origin         text not null default '',
    updated_at     timestamptz not null default now()
);

alter table video add column if not exists depth          int  not null default 0;
alter table video add column if not exists parent_id      text null;
alter table video add column if not exists discovered_via text not null default 'seed';
alter table video add column if not exists channel_name   text not null default '';
alter table video add column if not exists channel_url    text not null default '';
alter table video add column if not exists channel_followers int not null default 0;
alter table video add column if not exists origin         text not null default '';

-- Everything below arrives in the same `yt-dlp -J` response as the title, so
-- storing it costs nothing and re-fetching it later costs a full scan.
alter table video add column if not exists description text   not null default '';
alter table video add column if not exists view_count  bigint not null default 0;
alter table video add column if not exists like_count  bigint not null default 0;
alter table video add column if not exists tags        text[] not null default '{}';

create index if not exists video_state_idx   on video (state);
create index if not exists video_channel_idx on video (channel_id);

create table if not exists frontier (
    topic_name text not null references topic(name) on delete cascade,
    video_id  text not null,
    depth     int  not null default 0,
    parent_id text null,
    state     text not null default 'queued',
    attempts  int  not null default 0,
    added_at  timestamptz not null default now(),
    next_attempt_at timestamptz not null default now(),
    priority  real not null default 0,
    primary key (topic_name, video_id)
);

alter table frontier add column if not exists next_attempt_at timestamptz not null default now();
alter table frontier add column if not exists priority real not null default 0;

-- v0.6 -> v0.7: frontier switches from a local topic_id to the topic name.
do $$
begin
    if exists (select 1 from information_schema.columns
               where table_name = 'frontier' and column_name = 'topic_id') then
        alter table frontier add column if not exists topic_name text;
        update frontier f set topic_name = t.name
          from topic t where t.id = f.topic_id and f.topic_name is null;
        alter table frontier alter column topic_name set not null;
        alter table frontier drop constraint if exists frontier_pkey;
        alter table frontier drop constraint if exists frontier_topic_id_fkey;
        alter table frontier drop column topic_id;
        alter table frontier add primary key (topic_name, video_id);
        alter table frontier add constraint frontier_topic_name_fkey
          foreign key (topic_name) references topic(name) on delete cascade;
    end if;
end $$;

create index if not exists frontier_queue_idx on frontier (topic_name, state, priority, next_attempt_at);

create table if not exists transcript_segment (
    video_id text not null references video(id) on delete cascade,
    idx      int  not null,
    start_ms int  not null,
    text     text not null,
    primary key (video_id, idx)
);

-- `getSegments`, `saveSegments` and the coverage report all look up by video_id;
-- without this the coverage query scans 12 M rows and times out.
create index if not exists transcript_segment_video_idx on transcript_segment (video_id);

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
    if exists (select 1 from information_schema.columns
               where table_name = 'hit' and column_name = 'id') then
        drop table hit;
    end if;
end $$;

create table if not exists hit (
    topic_name text   not null references topic(name) on delete cascade,
    video_id   text   not null references video(id) on delete cascade,
    term       text   not null,
    snippet    text   not null default '',
    t_start    int    not null default 0,
    score      real   not null default 0,
    unique (topic_name, video_id, term, t_start)
);

-- v0.6 -> v0.7: hit switches from topic_id to topic name.
do $$
begin
    if exists (select 1 from information_schema.columns
               where table_name = 'hit' and column_name = 'topic_id') then
        create table hit_v7 (
            topic_name text not null references topic(name) on delete cascade,
            video_id   text not null references video(id) on delete cascade,
            term       text not null,
            snippet    text not null default '',
            t_start    int  not null default 0,
            score      real not null default 0,
            unique (topic_name, video_id, term, t_start)
        );
        insert into hit_v7 (topic_name, video_id, term, snippet, t_start, score)
          select t.name, h.video_id, h.term, h.snippet, h.t_start, h.score
          from hit h join topic t on t.id = h.topic_id;
        drop table hit;
        alter table hit_v7 rename to hit;
    end if;
end $$;

create index if not exists hit_topic_idx on hit (topic_name, term);

-- Only after both children stopped referencing topic.id can it go away.
do $$
begin
    if exists (select 1 from information_schema.columns
               where table_name = 'topic' and column_name = 'id') then
        alter table topic drop constraint if exists topic_pkey;
        alter table topic add primary key (name);
        alter table topic drop column id;
    end if;
end $$;
"""

    /// Idempotent DDL. Safe to call on every start.
    let migrate () =
        use conn = openConnection()
        conn.Execute(ddl) |> ignore

    // ------------------------------------------------------------ topics

    /// Create the topic if missing, return its name (the natural key). Stems are
    /// only written on creation, so an existing topic keeps its vocabulary.
    let ensureTopic (name: string) (stems: string list) : string =
        use conn = openConnection()

        conn.Execute(
            "insert into topic (name, stems) values (@name, @stems) on conflict (name) do nothing",
            {| name = name; stems = List.toArray stems |}
        )
        |> ignore

        name

    let topicExists (name: string) : bool =
        use conn = openConnection()

        conn.ExecuteScalar<int64>("select count(*) from topic where name = @name", {| name = name |}) > 0L

    /// Replace the vocabulary of an existing topic (used when `--stems` is
    /// passed explicitly: without this, the stems of an existing topic were
    /// silently ignored).
    let setTopicStems (name: string) (stems: string list) =
        use conn = openConnection()

        conn.Execute(
            "update topic set stems = @stems where name = @name",
            {| name = name; stems = List.toArray stems |}
        )
        |> ignore

    /// Words that disqualify a hit even when a needed term matched.
    let setTopicExclude (name: string) (exclude: string list) =
        use conn = openConnection()

        conn.Execute(
            "update topic set exclude = @exclude where name = @name",
            {| name = name; exclude = List.toArray exclude |}
        )
        |> ignore

    let topicStems (name: string) : string list =
        use conn = openConnection()

        conn.ExecuteScalar<string[]>("select stems from topic where name = @name", {| name = name |})
        |> List.ofArray

    let topicExclude (name: string) : string list =
        use conn = openConnection()

        conn.ExecuteScalar<string[]>(
            "select coalesce(exclude, '{}') from topic where name = @name",
            {| name = name |}
        )
        |> List.ofArray

    type TopicStat =
        { name: string
          stems: string
          queued: int64
          expanded: int64
          videos: int64
          hits: int64 }

    let listTopics () =
        use conn = openConnection()

        conn.Query<TopicStat>(
            """
select t.name,
       array_to_string(t.stems, ', ') as stems,
       (select count(*) from frontier f where f.topic_name = t.name and f.state = 'queued')   as queued,
       (select count(*) from frontier f where f.topic_name = t.name and f.state = 'expanded') as expanded,
       (select count(*) from video v
          join frontier f2 on f2.video_id = v.id and f2.topic_name = t.name
         where v.state = 'done')                                                               as videos,
       (select count(*) from hit h where h.topic_name = t.name)                                as hits
from topic t
order by t.name
"""
        )
        |> Seq.toList

    // ------------------------------------------------------------ frontier

    /// Crash recovery: anything left `running` goes back to the queue.
    let resetRunning (topicName: string) =
        use conn = openConnection()

        conn.Execute(
            "update frontier set state = 'queued', next_attempt_at = now() where topic_name = @topic and state = 'running'",
            {| topic = topicName |}
        )
        |> ignore

    /// Add a video to the topic's queue. Idempotent.
    ///
    /// `priority` is inherited from the parent and bumped by the parent's own
    /// hits, so the crawl follows the thread that is paying off. `origin` is
    /// the node that discovered the video — it survives a merge, so you can
    /// still tell which machine (and which seed) found a lead.
    let enqueue
        (topicName: string)
        (videoId: string)
        (depth: int)
        (parentId: string)
        (via: string)
        (priority: float)
        (origin: string)
        =
        use conn = openConnection()

        conn.Execute(
            """
insert into video (id, depth, parent_id, discovered_via, origin)
values (@id, @depth, @parent, @via, @origin)
on conflict (id) do nothing
""",
            {| id = videoId
               depth = depth
               parent = (if isNull parentId then null else box parentId)
               via = via
               origin = origin |}
        )
        |> ignore

        conn.Execute(
            """
insert into frontier (topic_name, video_id, depth, parent_id, priority)
values (@topic, @video, @depth, @parent, @priority)
on conflict (topic_name, video_id) do update
set priority = greatest(frontier.priority, excluded.priority)
""",
            {| topic = topicName
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
    let claimFrontier (topicName: string) : Frontier_row =
        use conn = openConnection()

        conn.QuerySingleOrDefault<Frontier_row>(
            """
update frontier set state = 'running', attempts = attempts + 1
where (topic_name, video_id) = (
    select topic_name, video_id
    from frontier
    where topic_name = @topic and state = 'queued' and next_attempt_at <= now()
    order by priority desc, depth, next_attempt_at
    limit 1
    for update skip locked
)
returning topic_name, video_id, depth, parent_id, state, attempts, priority
""",
            {| topic = topicName |}
        )

    let markExpanded (topicName: string) (videoId: string) =
        use conn = openConnection()

        conn.Execute(
            "update frontier set state = 'expanded' where topic_name = @topic and video_id = @video",
            {| topic = topicName; video = videoId |}
        )
        |> ignore

    /// After a transient failure: retry with exponential backoff, give up after
    /// 5 attempts. The backoff matters: a 429 would otherwise be retried at
    /// once, burn the attempt budget and mark a perfectly good video `failed`.
    let markFrontierRetry (topicName: string) (videoId: string) (attempts: int) =
        use conn = openConnection()

        let state = if attempts >= 5 then "failed" else "queued"
        let delaySeconds = attempts * attempts * Settings.retryBaseSeconds

        conn.Execute(
            """
update frontier
set state = @state,
    next_attempt_at = now() + make_interval(secs => @delay)
where topic_name = @topic and video_id = @video
""",
            {| state = state
               delay = delaySeconds
               topic = topicName
               video = videoId |}
        )
        |> ignore

    let markFrontierFailed (topicName: string) (videoId: string) =
        use conn = openConnection()

        conn.Execute(
            "update frontier set state = 'failed' where topic_name = @topic and video_id = @video",
            {| topic = topicName; video = videoId |}
        )
        |> ignore

    let queuedCount (topicName: string) : int =
        use conn = openConnection()

        conn.ExecuteScalar<int64>(
            "select count(*) from frontier where topic_name = @topic and state = 'queued'",
            {| topic = topicName |}
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

        if not segments.IsEmpty then
            // One statement for the whole video: a per-segment INSERT meant
            // thousands of round-trips and WAL records per video, which is what
            // made the disk thrash.
            conn.Execute(
                """
insert into transcript_segment (video_id, idx, start_ms, text)
select @video, ord - 1, start_ms, txt
from unnest(@starts, @texts) with ordinality as t(start_ms, txt, ord)
""",
                {| video = videoId
                   starts = segments |> List.map fst |> List.toArray
                   texts = segments |> List.map snd |> List.toArray |}
            )
            |> ignore

    /// Dapper cannot materialise a tuple, so read the two columns directly —
    /// `getSegments` is on the hot path of `backfill` and `recheck`.
    let getSegments (videoId: string) : (int * string) list =
        use conn = openConnection()
        use cmd = new NpgsqlCommand("select start_ms, text from transcript_segment where video_id = @id order by idx", conn)
        cmd.Parameters.AddWithValue("id", videoId) |> ignore
        use reader = cmd.ExecuteReader()

        [ while reader.Read() do
              yield reader.GetInt32 0, reader.GetString 1 ]

    /// Videos of the topic whose hits have no timestamp (scored from flat text).
    let videosWithUntimedHits (topicName: string) (limit: int) : string list =
        use conn = openConnection()

        let lim = if limit <= 0 then Nullable<int>() else Nullable limit

        conn.Query<string>(
            """
select distinct h.video_id
from hit h
where h.topic_name = @topic and h.t_start = 0
limit @limit
""",
            {| topic = topicName; limit = lim |}
        )
        |> Seq.toList

    let deleteHitsForVideo (topicName: string) (videoId: string) =
        use conn = openConnection()

        conn.Execute(
            "delete from hit where topic_name = @topic and video_id = @video",
            {| topic = topicName; video = videoId |}
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
    let channelExpandedCount (topicName: string) (channel: string) : int =
        use conn = openConnection()

        conn.ExecuteScalar<int64>(
            """
select count(*)
from frontier f
join video v on v.id = f.video_id
where f.topic_name = @topic and f.state = 'expanded' and v.channel_name = @channel
""",
            {| topic = topicName; channel = channel |}
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

    /// Fields that only the `-J` response carries. Saved on every scan so that a
    /// later re-score, a new topic or a wider vocabulary never needs YouTube again.
    let saveVideoMeta (videoId: string) (description: string) (views: int) (likes: int) (tags: string list) =
        use conn = openConnection()

        conn.Execute(
            """
update video set description = @description,
                 view_count  = @views,
                 like_count  = @likes,
                 tags        = @tags,
                 updated_at  = now()
where id = @id
""",
            {| id = videoId
               description = description
               views = views
               likes = likes
               tags = Array.ofList tags |}
        )
        |> ignore

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
    let clearHits (topicName: string) : int =
        use conn = openConnection()

        conn.Execute("delete from hit where topic_name = @topic", {| topic = topicName |})

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
    let requeueFailed (topicName: string) (alsoNoCaptions: bool) : int =
        use conn = openConnection()

        let states = if alsoNoCaptions then [ "failed"; "no_captions" ] else [ "failed" ]

        let n =
            conn.Execute(
                """
update frontier set state = 'queued', attempts = 0, next_attempt_at = now()
where topic_name = @topic and state = any(@states)
""",
                {| topic = topicName; states = List.toArray states |}
            )

        conn.Execute(
            """
update video v set attempts = 0, last_error = null, state = 'pending', updated_at = now()
where v.state in ('failed', 'no_captions')
  and exists (select 1 from frontier f where f.video_id = v.id and f.topic_name = @topic and f.state = 'queued')
""",
            {| topic = topicName |}
        )
        |> ignore

        n

    // ------------------------------------------------------------ hits

    /// Insert the hits of one video for one topic. Idempotent.
    let insertHits (topicName: string) (videoId: string) (hits: (string * string * int) list) =
        if not hits.IsEmpty then
            use conn = openConnection()

            for (term, snippet, startMs) in hits do
                conn.Execute(
                    """
insert into hit (topic_name, video_id, term, snippet, t_start, score)
values (@topic, @video, @term, @snippet, @t_start, 1)
on conflict (topic_name, video_id, term, t_start) do nothing
""",
                    {| topic = topicName
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
where (@topic = '' or h.topic_name = @topic)
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

    [<CLIMutable>]
    type ChannelRow =
        { channel: string
          url: string
          subs: int64
          videos: int64
          videos_total: int64
          mentions: int64
          depth: int
          sample: string }

    /// Hits grouped by channel: the point is to find *people*, not videos.
    /// `maxSubs > 0` keeps only channels small enough to still be "a beginner
    /// blogger". `sortBy` = "mentions" (default) or "surprise": the latter
    /// ranks by how RARE the mention is inside the channel's own body of work —
    /// 1 mention in 200 videos is a much better lead than 3 in 3.
    let listChannels (topicName: string) (term: string) (limit: int) (maxSubs: int) (sortBy: string) =
        use conn = openConnection()

        let order =
            if sortBy = "surprise" then
                "(count(*)::numeric / greatest(max(vt.videos_total), 1)), count(*) desc"
            else
                "count(*) desc, count(distinct h.video_id) desc"

        conn.Query<ChannelRow>(
            $"""
select v.channel_name as channel,
       coalesce((array_agg(distinct v.channel_url))[1], '') as url,
       max(v.channel_followers) as subs,
       count(distinct h.video_id) as videos,
       max(vt.videos_total) as videos_total,
       count(*) as mentions,
       min(v.depth) as depth,
       (array_agg(v.title order by v.depth))[1] as sample
from hit h
join video v on v.id = h.video_id
left join (select channel_name, count(*) as videos_total from video group by channel_name) vt
       on vt.channel_name = v.channel_name
where (@topic = '' or h.topic_name = @topic)
  and (@term = '' or h.term = @term)
  and v.channel_name <> ''
group by v.channel_name
having (@max_subs <= 0 or max(v.channel_followers) <= @max_subs)
order by {order}
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
    let transcriptsWithoutHits (topicName: string) =
        use conn = openConnection()

        conn.Query<Transcript_row>(
            """
select t.video_id, t.lang, t.source, t.text, t.chars
from transcript t
join frontier f on f.video_id = t.video_id and f.topic_name = @topic
where not exists (select 1 from hit h where h.video_id = t.video_id and h.topic_name = @topic)
""",
            {| topic = topicName |}
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


    /// How much of the corpus can be re-scored without touching YouTube.
    /// `transcripts` vs `withSegments` is the real measure of stored value:
    /// a transcript without segments can still be searched, but a keyword found
    /// in it has no timestamp.
    let storageCoverage () =
        use conn = openConnection()

        let scalar (sql: string) = conn.ExecuteScalar<int64>(sql) |> int
        let transcripts = scalar "select count(*) from transcript"
        let withSegments = scalar "select count(distinct video_id) from transcript_segment"
        let segments = scalar "select count(*) from transcript_segment"
        let timedHits = scalar "select count(*) from hit where t_start > 0"
        let untimedHits = scalar "select count(*) from hit where t_start = 0"
        let described = scalar "select count(*) from video where description <> ''"
        transcripts, withSegments, segments, timedHits, untimedHits, described
