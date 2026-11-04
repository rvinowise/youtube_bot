# What we actually fetch from YouTube

Every request this project makes, with the exact command that reproduces it and
the exact bytes that came back. Captured on 2026-11-04 through the
`NorwaySandefjordS13` tunnel (exit IP `195.16.73.203`), with the same headers the
scraper uses.

There is **one** `yt-dlp` call. Everything else is plain HTTP against
`www.youtube.com` and its InnerTube endpoints, parsed with regexes that tolerate
schema changes ([`YouTube.fs`](YouTube.fs:1)).

## Shared request headers

[`Http.fs`](Http.fs:8) uses a single `HttpClient` (60 s timeout) with:

```
User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36
Accept-Language: ru-RU,ru;q=0.9,en;q=0.8
Cookie: CONSENT=YES+1; SOCS=CAI
```

No account, no API key of our own, no `Authorization`.

## Index

| # | What | Method and URL | Code |
|---|------|----------------|------|
| 1 | video metadata + caption track URLs | `yt-dlp ... -J` | [`YtDlp.fs`](YtDlp.fs:131) |
| 2 | related videos (BFS edges) | `GET /watch?v=ID` + `POST /youtubei/v1/browse` | [`YouTube.fs`](YouTube.fs:273) |
| 3 | search | `GET /results?search_query=Q` + `POST /youtubei/v1/search` | [`YouTube.fs`](YouTube.fs:119) |
| 4 | channel about | `GET /channel/UC.../about` | [`YouTube.fs`](YouTube.fs:144) |
| 5 | channel videos | `GET /channel/UC.../videos` + `POST /youtubei/v1/browse` | [`YouTube.fs`](YouTube.fs:200) |
| 6 | channel shorts | `GET /channel/UC.../shorts` + `POST /youtubei/v1/browse` | [`YouTube.fs`](YouTube.fs:233) |
| 7 | playlist items | `GET /playlist?list=PL...` + `POST /youtubei/v1/browse` | [`YouTube.fs`](YouTube.fs:300) |
| 8 | title + channel, cheaply | `GET /oembed?...` | [`YouTube.fs`](YouTube.fs:343) |
| 9 | thumbnail | `GET i.ytimg.com/vi/ID/NAME.jpg` | [`YouTube.fs`](YouTube.fs:370) |
| 10 | transcript | `GET <timedtext url from #1>` | [`Transcript.fs`](Transcript.fs:76) |

---

## 1. Video metadata — the only yt-dlp call

```
yt-dlp.exe -4 --cookies-from-browser "firefox" --ignore-no-formats-error --no-warnings --skip-download -J "https://www.youtube.com/watch?v=hplapUKhATw"
```

The JSON is **1,098,394 bytes** and takes about 16 s. Fields we read, exactly as
they arrived:

```
id=hplapUKhATw
title=27 октября 2026 г.СМЕРШ против Абвера: Агент, который вернулся из ада | Военный детектив
duration=5407
channel=Мёртвые герои
channel_id=UCu1-1EP__itTLOvgj8Da5qQ
channel_follower_count=1880
upload_date=20261029
track_name=Russian
track_ext=json3
track_url=https://www.youtube.com/api/timedtext?v=hplapUKhATw&ei=swLrapTkOubT6dsP0t-umQ8&caps=asr&opi=112496729&xoaf=4&xowf=1&xospf=1&hl=en&ip=0.0.0.0&ipbits=0&expire=1793811748&sparams=ip%2Cipbits%2Cexpire%2Cv%2Cei%2Ccaps%2Copi%2Cxoaf&signature=3132D76AE3E62C3CF42259A71E8FA8F06BF4C9F2.0BC809882094E896A8EA002C534429412C749CAA&key=yt8&kind=asr&lang=en&variant=timing-optimized&tlang=ru&fmt=json3
```

`automatic_captions` held 190 language keys, starting
`ab,aa,af,ak,sq,am,ar,hy,as,ay,az,bn,ba,eu,be,bho,bs,br,bg,my,ca,ceb,zh-Hans,zh-Hant,co,hr,cs,da,dv,nl,dz,en-orig,en,eo,...`
and ending `...,ve,vi,war,cy,fy,wo,xh,yi,yo,zu,ru-orig`.

Two flags are not optional:

- `-4` — IPv6 to YouTube times out on this machine;
- `--ignore-no-formats-error` — YouTube's `n` challenge makes yt-dlp drop every
  real format (it needs a JS runtime), so plain `-J` exits non-zero with
  "Requested format is not available" although `automatic_captions` is fully
  populated. We never download media, only caption URLs, so the missing formats
  are irrelevant.

## 2. Related videos — the BFS edge set

```
GET https://www.youtube.com/watch?v=hplapUKhATw
```

1,431,056 bytes of HTML. The parser splits on `"lockupViewModel":{` and reads
`contentId` plus `title.content`. All 20 pairs it produced:

```
YEZDTv4nfAg|МИНСК ГОРЕЛ ИЗНУТРИ: банда «Чёрный Кот» против СМЕРШа
HvVMFPvpuME|ГЕНЕРАЛ выгнал СОЛДАТА из штаба при офицерах. А получив ночью шифровку и увидев его позывной...
evbJo2ItBPU|АБВЕР ПРИСЛАЛ ЛУЧШИХ ДИВЕРСАНТОВ — НО В ГОРАХ ИХ ЖДАЛИ БЫВШИЕ УГОЛОВНИКИ
giTMJzdhc70|СМЕРШ против элитных диверсантов СС | Охота открывается | Военный боевик
orOTgQSUyzU|12 ШТРАФНИКОВ И ОДНА КАЗАШКА ПРОТИВ ЭЛИТНОГО ОТРЯДА СС
iRyN3yOwa7s|СМЕРШ ЗАДЕРЖАЛ КАВКАЗСКОГО ПРОВОДНИКА — А ШТРАФНИК УЗНАЛ В НЁМ НЕМЕЦКОГО АГЕНТА
vkqx7kVMIG8|31 СНАЙПЕР ЗА 26 ДНЕЙ: НЕМЦЫ НЕ ПОНИМАЛИ, КАК ОН ИХ ВИДИТ. Тайна охотника из Эвенкии
sTgmMQgp328|ОДИН ОХОТНИК ПРОТИВ 40 НЕМЦЕВ — эвенк гнал генерала по болотам, как соболя
d3GlsdESkVM|НЕМЦЫ СМЕЯЛИСЬ НАД «ДИКИМИ НА ПОНИ» — Через месяц они назвали их Чёрной смертью
ts0APpSqxTQ|Аненербе в СССР. Что же они искали?
AHZjhVNHhAY|Последние сутки Гитлера: бункер, свадьба, завещание и выстрел в 15:30
7YHYcOA5Gss|ГОСПИТАЛЬ В ТЫЛУ ОКАЗАЛСЯ ЯВКОЙ: СМЕРШ раскрыл сеть Абвера среди раненых офицеров
Frl-fkk7YrA|СТАЛИНГРАД ЗАХЛЕБНУЛСЯ В КРОВИ — СМЕРШ шёл по след
nZG5EbSdXEQ|Тайная роскошь Алины Кабаевой. Главные подарки от Путина
GiN8RYy5ZcE|Он лежал в снегу ПОД НИМИ. Восемь собак прошли — ни одна не почуяла...
h-B3eC84R9w|СМЕРШ против ФАНТОМА: Охота на Серого Волка | Военный детектив 1944
esrZgGiyemg|ОДИН ТУНГУС И БОЛОТО — Дивизия СС искала его 38 дней. Что нашли немцы...
IUNgKWPqQVM|Как НА САМОМ ДЕЛЕ служили в «Альфе» КГБ СССР: кого брали и к чему не могла подготовить учебка
wVGpHEy3LQQ|Опять «Рязанский сахар». Путин готовит новую войну. Выборы в Америке | АСЛАНЯН. Версия
5Ww1xZKvCFk|Главный палач Сталина Василий Блохин: что с ним сделали после смерти вождя?
```

The same page carries the InnerTube session values:

```
INNERTUBE_API_KEY = AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8
INNERTUBE_CLIENT_VERSION = 2.20261030.02.00
visitorData = CgtaSjZTQjctWjB4MCighqzXBjIKCgJSVRIEGgAgZmKrAgqoAkN0c0JDQVVTalFFQkRCNlBESGtSWUkwRks0REFRa2NwSGc2aXBCbGlTbkt3Rl8wbS1USHEzam1Sc1Vjei0xczBmTmdsczdDcnFjaWF2MTJiX01XRklUcHVYRzRuZFhBZTBTeEpkVTZjall0d2E4VG5uem9RT1lGME4wdDg2SHhUWlYwN0tMS0xRLTNTcmZrV09Sc01Kb0J1RFhPTlJvMnlWb2RILXhHRk1Dcm5DMWlvT0N3NS01OXB4djZCaUlucUJtXzBIOUlvQlRKRkFSeFk5V3JfSW5KSEx6ekU4bF9CdDlpeGh1VHV1czZRakhSd0h0dEhSNGp5eTR3T3hVZXZsblBLTU8zOTFGeXFOTGgtclcyVjk3N0toTU9VcXk1Vko4YURJY3dB
```

When the sidebar is paginated, the leftover token goes to:

```
POST https://www.youtube.com/youtubei/v1/browse?key=AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8&prettyPrint=false
X-Youtube-Client-Name: 1
X-Youtube-Client-Version: 2.20261030.02.00
Origin: https://www.youtube.com
Content-Type: application/json
```

with this body (compact, exactly as serialised):

```json
{"context":{"client":{"clientName":"WEB","clientVersion":"2.20261030.02.00","hl":"ru","gl":"RU","visitorData":"CgtRdkFPR1pwWnFaTSiIh6zXBjIKCgJSVRIEGgAgX2KqAgqnAkN0b0JDQVVTakFFQkRCNlBETmtFWEZuVFo0WGl4aXlSWHlEN084VDdyQVR3Mlc4Q1BQZmxqSnJhak9TQ1NpWTVheDdtd2szLURHLS12T2ZHbnJvMndpVFFEU0pVeDdOcXU0Nk9ieFpMZ0FNUGx5NVhjTllsd2hfLTB1Z3JhQlgtd3BSSE9yV195VFhIam9tMElQbVJXVENkTTgybVZwcjNoNHd6eHNDUFVhekFHc2VlYTFnblB4N25fbFhRWDVSdG5NUFk0U2dGTWtVQkhGajFha2JNUldvVWZjTFdwU2Q3N1dhQ1ExUUxWMC1DWUV0ZUJqYWg4R3VlbHFVYlR1ZVE0THgwd0NuZTNGWUlCbkE3bTM4bHVGMS1vck4yb1A1dlN1SWZmZ1E%3D"},"continuation":"EqMDEgtpbW1vcnRhbGl0eRqQA1NCU0NBUXRYWkZsaFIzUmZjMjB6VVlJQkRWSkVWMlJaWVVkMFgzTnRNMUdDQVF0d2R6UldSVmN6VTNSSmQ0SUJDMVZHUldreVJrOU5XRlp6Z2dFTFV6TnFjVlJYV0hkNlNtT0NBUXRzZVdaUGVtRkdhWEIwUllJQkMwSjZibTV6Wmtnd1JEaHZnZ0VMWkRoT1NIUlBTWG8yWmxXQ0FRczFZVzVNVUhjd1JXWnRiNElCQzJJeFRuQjFkMEZMUWtOcmdnRUxlRnBQTURGR1EyTlFSVFNDQVF0bE5sUkRjR3cxV1hGNE1JSUJDMkphYjJ4bVMyZFhOVWx6Z2dFTE4zZExOSEJsWlhvNWVrV0NBUXRSVGtGbVZIRXpVbEpwZDRJQkMyUlVUelZUV2paUlQzVnZnZ0VMWVMwdFZqSnVXa0owV0UyQ0FRdEZVRkJ2WmtWRlZUUTNZNElCQzNBNFdYQnVlRVpuZEY5WmdnRUxRWFpQVm05MVdIWTFWVW15QVFZS0JBZ1dFQUxxQVFRSUFoQVqgAgQYgeDoGCILc2VhcmNoLWZlZWQ%3D"}}
```

That is the token we extract with "longest `"continuation":"..."` in the page",
which is what makes search/channel/shorts/playlist pagination work at all.

## 3. Search

```
GET https://www.youtube.com/results?search_query=immortality
```

First five `videoRenderer` blocks, parsed into
`id|title|channel|channelId|published|views`:

```
WdYaGt_sm3Q|Céline Dion - Immortality ft. Bee Gees|Celine Dion|UC_yGU4qz9zAjEWLQxCg9NZQ|17 л. назад|98 398 279 просмотров
pw4VEW3StIw|Immortality|Pearl Jam|UClQT6Vnsm6BUm0I5kR26EkQ||9 320 314 просмотров
UFEi2FOMXVs|Cartoon, Jéja - Immortality (feat. Kristel Aaslaid) [Futuristik Remix) | NCS - Copyright Free Music|NoCopyrightSounds|UC_aEa8K-EOJ3D6gOs7HcyNg|10 л. назад|7 750 926 просмотров
lyfOzaFiptE|Immortal (2026) New Released Hindi Dubbed Full Movie | Starring G. V. Prakash \u0026 Kayadu Lohar|RG Entertainment|UCgfO009hB_847kwL6NQnn-w|4 дн. назад|778 209 просмотров
BznnsfH0D8o|Celine Dion - Immortality (Lyrics)|Alternate|UCDL-lqf51afYoUldSvZMTOA|3 г. назад|5 594 328 просмотров
```

Page 2 is a `POST /youtubei/v1/search` (same headers, body shape as above,
`continuation` from `"continuationCommand":{"token":"..."}`):

```
HTTP 200, 561,655 bytes, 16 further videos:
```

```
CuVsq9nlNJ0|The Immortality Scam
aDfREKdLCf4|Troy - Immortality
4F3-muD0k5E|Céline Dion - Immortality (Bee Gees Tribute) 1997 - 2017 (HD)
EPdYVjFS7Qc|Sympathy for the Immortal
UHVqlnMFCus|Quantum Immortality Explained
oHj5_dUj0iM|Seether - Immortality
klsiOwLGTXs|Самая странная идея в науке: квантовое бессмертие
IxEPfW8Y37U|7 уровней бессмертия в природе
u2iOnWgaQe4|Immortality (Demo Version)
vq3FoHg9qa8|Bee Gees -  Immortality
qIIVPOUH4Mo|Pearl Jam - Immortality - 10/2/1994 - Shoreline Amphitheatre
RfauJhhv5mk|Quantum Immortality | Why You’ve Already Died Hundreds of Times — But Don’t Remember | Documentary
7M3LqLNI1Ms|Мои любимые изображения бессмертия в аниме
o8tV-R7Rfhg|IMMORTALITY | Official Game Trailer | Netflix
hC2adtkiL6o|Why Immortality Is Closer Than You Think
8k-PMnW7HDM|Как стать бессмертным согласно даосизму (за 5 непростых шагов)
```

Between continuation pages the code sleeps 300 ms (`Http.delay 300`).

## 4. Channel about

```
GET https://www.youtube.com/channel/UCu1-1EP__itTLOvgj8Da5qQ/about
```

```
UCu1-1EP__itTLOvgj8Da5qQ|Мёртвые герои|1,88 тыс. подписчиков|7 видео
```

`subscriberCountText` / `videoCountText` are human strings (`1,88 тыс.`,
`7 видео`), which is why [`Domain.parseCount`](Domain.fs:1) exists: it turns them
into `1880` and `7` for the DB.

## 5. Channel videos

```
GET https://www.youtube.com/channel/UCu1-1EP__itTLOvgj8Da5qQ/videos
```

First three `lockupViewModel` entries:

```
giTMJzdhc70|СМЕРШ против элитных диверсантов СС | Охота открывается | Военный боевик
q_BsOFDhYhU|Местная мафия вздумала отобрать бизнес у одинокой матери.Они горько пожалели когда приехала её сестр
KJo6hQVIyl0|Они грабили стариков два года, пока в селение не приехала внучка-спецназовец!
```

Further pages: `POST /youtubei/v1/browse`, 250 ms apart.

## 6. Channel shorts

```
GET https://www.youtube.com/channel/UCu1-1EP__itTLOvgj8Da5qQ/shorts
```

Splits on `"shortsLockupViewModel":{`, reads `videoId` and
`accessibilityText`, then strips the wrapper — `accessibilityText` is literally
`Воспроизвести короткое видео "TITLE, 1,2 тыс. просмотров"`, so
[`cleanShortsTitle`](YouTube.fs:87) removes the prefix and the trailing
`, 1,2 тыс. просмотров`.

## 7. Playlist

```
GET https://www.youtube.com/playlist?list=PL...
```

Two markers, `"playlistVideoRenderer":{` and `"lockupViewModel":{`; the first is
the classic layout, the second the current one, and both are collected because
YouTube serves either depending on the playlist.

## 8. oEmbed

```
GET https://www.youtube.com/oembed?url=https://www.youtube.com/watch?v=hplapUKhATw&format=json
```

1,695 bytes, returned in full:

```json
{"title":"27 \u043e\u043a\u0442\u044f\u0431\u0440\u044f 2026\u202f\u0433.\u0421\u041c\u0415\u0420\u0428 \u043f\u0440\u043e\u0442\u0438\u0432 \u0410\u0431\u0432\u0435\u0440\u0430: \u0410\u0433\u0435\u043d\u0442, \u043a\u043e\u0442\u043e\u0440\u044b\u0439 \u0432\u0435\u0440\u043d\u0443\u043b\u0441\u044f \u0438\u0437 \u0430\u0434\u0430 | \u0412\u043e\u0435\u043d\u043d\u044b\u0439 \u0434\u0435\u0442\u0435\u043a\u0442\u0438\u0432","author_name":"\u041c\u0451\u0440\u0442\u0432\u044b\u0435 \u0433\u0435\u0440\u043e\u0438","author_url":"https://www.youtube.com/@%D0%9C%D1%91%D1%80%D1%82%D0%B2%D1%8B%D0%B5%D0%B3%D0%B5%D1%80%D0%BE%D0%B8","type":"video","height":113,"width":200,"version":"1.0","provider_name":"YouTube","provider_url":"https://www.youtube.com/","thumbnail_height":360,"thumbnail_width":480,"thumbnail_url":"https://i.ytimg.com/vi/hplapUKhATw/hqdefault.jpg","html":"\u003ciframe width=\u0022200\u0022 height=\u0022113\u0022 src=\u0022https://www.youtube.com/embed/hplapUKhATw?feature=oembed\u0022 frameborder=\u00220\u0022 allow=\u0022accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share\u0022 referrerpolicy=\u0022strict-origin-when-cross-origin\u0022 allowfullscreen title=\u002227 \u043e\u043a\u0442\u044f\u0431\u0440\u044f 2026\u202f\u0433.\u0421\u041c\u0415\u0420\u0428 \u043f\u0440\u043e\u0442\u0438\u0432 \u0410\u0431\u0432\u0435\u0440\u0430: \u0410\u0433\u0435\u043d\u0442, \u043a\u043e\u0442\u043e\u0440\u044b\u0439 \u0432\u0435\u0440\u043d\u0443\u043b\u0441\u044f \u0438\u0437 \u0430\u0434\u0430 | \u0412\u043e\u0435\u043d\u043d\u044b\u0439 \u0434\u0435\u0442\u0435\u043a\u0442\u0438\u0432\u0022\u003e\u003c/iframe\u003e"}
```

Decoded: `title` = `27 октября 2026 г.СМЕРШ против Абвера: Агент, который вернулся из ада | Военный детектив`,
`author_name` = `Мёртвые герои`,
`author_url` = `https://www.youtube.com/@Мёртвыегерои`.

No cookies are needed and it is not bot-checked, hence its use for backfilling
titles of videos queued before we stored metadata.

## 9. Thumbnails

Four candidate names are tried in order, the first one over 2 KB wins:

```
GET https://i.ytimg.com/vi/hplapUKhATw/frame0.jpg
GET https://i.ytimg.com/vi/hplapUKhATw/hq720.jpg
GET https://i.ytimg.com/vi/hplapUKhATw/maxresdefault.jpg
GET https://i.ytimg.com/vi/hplapUKhATw/hqdefault.jpg
```

All four answered here:

```
frame0|200|20410
hq720|200|261517
maxresdefault|200|261517
hqdefault|200|22568
```

So `frame0.jpg` (20,410 bytes) is saved as `hplapUKhATw.jpg`.

## 10. Transcript

```
GET <track_url from #1>
```

That URL must come straight out of the fresh `yt-dlp -J`: the `captionTracks`
URL embedded in the watch page returns HTTP 200 with a zero-byte body.

Parsing is `events[].tStartMs` + the concatenation of `events[].segs[].utf8`,
whitespace-collapsed. Real stored output for `tGJcrGzsyu8` (53 characters,
complete row):

```
Я собираюсь сказать: " Эй". Привет. Привет. Ммм. Ммм.
```

Real stored output for `hplapUKhATw` (63,471 characters) begins:

```
Десантник лежал лицом вниз в грязном снегу, широко раскинув руки, словно пытаясь обнять всю эту замерзшую землю. Сержант Макаров ткнул его ботинком.
```

### The failure this endpoint actually returns

While probing, the caption endpoint started answering 429 to `curl.exe` and
`Invoke-WebRequest` on **both** exit IPs we tried (Netherlands `45.131.111.92`
and Norway `195.16.73.203`), while the scraper's own `HttpClient` on the same IP
kept downloading captions at the same moment. The block follows the request
fingerprint, not the IP. Always exactly 1,103 bytes of HTML, always identical:

```html
<html><head><meta http-equiv="content-type" content="text/html; charset=utf-8"/><title>Sorry...</title><style> body { font-family: verdana, arial, sans-serif; background-color: #fff; color: #000; }</style></head><body><div><table><tr><td><b><font face=sans-serif size=10><font color=#4285f4>G</font><font color=#ea4335>o</font><font color=#fbbc05>o</font><font color=#4285f4>g</font><font color=#34a853>l</font><font color=#ea4335>e</font></font></b></td><td style="text-align: left; vertical-align: bottom; padding-bottom: 15px; width: 50%"><div style="border-bottom: 1px solid #dfdfdf;">Sorry...</div></td></tr></table></div><div style="margin-left: 4em;"><h1>We're sorry...</h1><p>... but your computer or network may be sending automated queries. To protect our users, we can't process your request right now.</p></div><div style="margin-left: 4em;">See <a href="https://support.google.com/websearch/answer/86640">Google Help</a> for more information.<br/><br/></div><div style="text-align: center; border-top: 1px solid #dfdfdf;"><a href="https://www.google.com">Google Home</a></div></body></html>
```

This is what [`Transcript.fetch`](Transcript.fs:76) classifies as `Transient` and
what the scan loop retries after a cooldown or an IP rotation — it is not a
missing caption, and it is the only endpoint that has ever fought back.
