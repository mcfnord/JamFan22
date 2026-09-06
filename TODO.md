# TODO

## UI / Client

- **Server card directory nudges: language matching** (`Pages/Client.cshtml`, `nearby.cs`): nudges that point a player toward a server's directory (e.g. "Join at jamulus.io") should try to match the language of their Jamulus client installation. Inference order: (1) geolocation of the browser IP → map country to dominant language; (2) if a GUID is matched to that IP, the country code stored with the GUID (Jamulus client's self-reported country) may confirm or refine the choice. No guarantee of correctness — treat it as a best guess, defaulting to English when ambiguous.

- **Visual regression detector** (`playwright`): headless browser script that loads the app, polls `page.evaluate` every ~1s to snapshot all `.server-card` bounding boxes, and reports frames where any card position jumps >20px or a card appears/disappears without an intermediate opacity transition. Useful for catching silence-gate snaps and Active Only filter layout shifts. Trigger: run after any change to card visibility logic in `Client.cshtml` or `site.css`.

- **Silence emoji fade-in / fade-out animation** (`Pages/Client.cshtml`): give the 🔇 emoji a slow, deliberate entrance and exit to make silence state transitions feel intentional rather than abrupt. **On first quiet sample:** instead of the emoji snapping onto the card, fade it in slowly (e.g., `opacity 1.5s ease-in`). **On second quiet sample (Active Only):** the card disappears via the ghost mechanism as today. **On quiet-in (server returns):** after the fresh card is built and placed at the ghost's slot, immediately show the 🔇 emoji on the new card, then fade it out with the same duration as the fade-in (so the silence emoji appears and dissolves as a brief "was quiet" signal). The fade-out should complete before or alongside the entrance animation so the card arrives looking active. Implementation: add a `.silence-emoji-fading` CSS class with `opacity: 0; transition: opacity 1.5s ease-in-out` and toggle it on the emoji span element in the quiet-out and quiet-in paths.

- **Fading easter egg text**: Some UI element (TBD) starts fully legible when the product is fresh/new, then gradually fades over time until it's nearly unreadable. The fade is a function of how old the installation or user session is — an ambient signal of age baked into the typography itself.

- **Active Only: show new arrivals briefly regardless of occupancy** (`Pages/Client.cshtml`): when Active Only is checked, a server with a new arrival (singleton or new duo) should still appear even if the server is otherwise silent. The visibility window should match however long the "just arrived" message is displayed — that duration is the natural TTL for a new-arrival card under Active Only. Once the arrival message expires, the server card follows the normal Active Only suppression logic.

- **Custom client: video link false-positive** (`chatreporter.cpp` or equivalent): when visiting a server, the client reads the server's welcome message and mistakenly treats any video URL found there as a live stream. Static welcome messages can contain video links (e.g. a tutorial or promo) that are not live. Fix: only treat a video URL as live if it was received outside of the initial welcome/MOTD message — e.g., posted mid-session in chat, not present in the first message burst on connect.

- **fetchAndRender at 15s** (`Pages/Client.cshtml:2641`): changed from 20s. Consider dropping to 10s once 15s has been in production without complaint. Diminishing returns below 10s since the server-side poll loop is ~5s.

- **"Any Genre Asia" label** (`Pages/Client.cshtml:715`): `renderServerHeader` strips `"Genre "` but alt-source returns `"Any Genre Asia"` → shows `"Any Asia"`. Fix: `cat = cat.replace("Any Genre Asia", "Any 3/Asia").replace("Genre ", "").replace(" ", "&nbsp;");`

- **Nearby Only: global peek strip** (`Pages/Client.cshtml`, `wwwroot/css/site.css`): when Nearby Only is active, render a compact strip just above the Nearby Only checkbox showing the hidden (distant) servers as tiny colored pills — same color scheme and sequence as the full cards, showing only a people-count badge. On hover, the pill expands into a full-size ghost of the server card so the user can read it without leaving Nearby Only mode. The strip is invisible when Nearby Only is off. Implementation sketch: collect suppressed server elements in a separate array during `processDiff`; build pills with `background-color` copied from the card's category color; CSS `:hover` transition `width`/`height` to expand; position pills in a `flex-wrap` row inside a `#global-peek-strip` div inserted before the checkbox container.

- **Mobile: PWA install prompt** (`Pages/Shared/_Layout.cshtml`): intercept `beforeinstallprompt`, show banner on second visit. **Do not implement until Web Push is working.**

- **Mobile: haptic feedback on tracked arrival** (`Pages/Client.cshtml`): `navigator.vibrate([200, 100, 200])` when starred player arrives in foreground. Opt-in checkbox. Implement after title-bar alert is shipped.

- **Mobile: Web Push notifications** (`Pages/Client.cshtml`, `Program.cs`, service worker): VAPID keys → `data/`; `wwwroot/sw.js`; push subscription store `data/push-subscriptions.json`; fan-out in polling loop. Prerequisite: PWA install prompt.

## LLM Welcome


- **"First time here!" on dormant fleet rejoin** (`CensusIndex.GetGuidOnServer`): dormant fleet servers change IP on every restart; `GetGuidOnServer` is keyed by `ip:port`, so all prior visit history is silently lost on each IP change. Fix: use `WelcomeCache` (or a separate short-TTL cache) to remember `(guid, serverName)` pairs from recent welcomes — if a match is found, suppress "first time here!" and inject a short rejoin hint. Static fleet servers (stable IPs) are unaffected.

- **"Back to your top server" after a 21-minute-old first visit** (`WelcomeContext.cs` history signals): observed 2026-07-10 — Dig Bick got "first time here!" on Agora at 00:25, then "back to your top server!" at 00:46 (`history:0h,top`). Technically his most-visited server after one visit, but the phrasing implies a long-standing habit. Consider requiring a minimum visit count / history depth before the `top` flag is set, or softer phrasing when history is thin. Watch for recurrence.

- **Bimodal welcome LLM latency — ~22s cluster** (`WelcomeMessageGenerator.cs`): observed 2026-07-10 — generation times cluster at either ~2–4s or ~22s (e.g. 22051, 22229, 22232, 22413, 23368 ms on consecutive Agora welcomes; interleaved Paradiso/Studio D welcomes ran 2.4–2.9s). The tight ~22s grouping suggests a timeout+retry or fallback path rather than natural variance. A 22s greeting may land well after the player has settled in. Check whether a first-attempt timeout is configured near 20s; instrument retries if so. Watch for recurrence.

- **India welcome-language mismatch** (`Program.cs:1130`, `WelcomeContext.cs:142,165`): three tables disagree — static header table says Hindi (`["IN"]="hi"`), LLM body-language table says English (`["IN"]="English"`), name-based table says Hindi (`["India"]="Hindi"`). An Indian player can get a Hindi "You've joined" header on an English body. One-line fix either direction; product call which language wins (lean English — Indian Jamulus scene skews English-speaking).

- **Suppress welcomes during stress tests** (`WelcomeMessageGenerator.cs` or `/ip-allowed` path): when a flood of clients with names matching a stress-test pattern arrive simultaneously (e.g. `gjstress-\d+`), suppress LLM welcome calls for those arrivals. Detection: if the arriving name matches a configurable regex (in `welcome-config.txt` or hardcoded), skip the LLM call entirely and return no message. Avoids burning tokens on synthetic load and prevents spammy nearly-identical messages from landing in chat.

- **Group-assembled stream reminder** (`WelcomeContext.cs`, `StreamGate.cs`): after a group has been stably assembled for ~6 minutes with the lobby/lease held, send a chat reminder pointing to `https://ear.jamulus.live`, gated to fire only when the lease has ≥30 min remaining. Today the URL is only ever surfaced reactively, riding on an arrival event (`WelcomeContext.cs:1205-1260` — `urlsAllowed` 20-min cooldown gate + branches on `streamActiveHere`/`lobbyPresent`/`minsUntilLobbyStream`); if no new player joins after the group settles in, nobody gets reminded regardless of lease time remaining. Needs design before implementing: (1) how to detect "group assembled" without an arrival to hang the check on — no timer/polling loop currently watches steady-state groups; (2) delivery channel — piggyback on the next welcome (cheap, but silent if nobody joins) vs. a direct fleet chat RPC (`sendClientChatMessage`, same path as `StreamGate.cs`'s `LoungeAnnouncement`) so it reaches an already-settled group; (3) precise meaning of "have the lobby" — already streaming (`streamActiveHere`, lease held + gojam connected) vs. merely eligible/free-to-request.

- **One-time broadcast notice** (`WelcomeContext.cs`, `Program.cs`): reusable mechanism for weaving a single announcement into welcome messages — "Happy New Year", "Welcome to our new Thai server", etc. Ripped out after the audio-dropout campaign; restore when needed.

  **Design (previously proven):**
  - `data/broadcast-notice.txt` — two lines: line 1 is the message to weave in (e.g. `Happy New Year from the JamFan network!`), line 2 is an expiry datetime in UTC ISO 8601 (e.g. `2027-01-03T00:00:00Z`). Empty file or missing file = no active notice.
  - `data/broadcast-notified.txt` — append-only list of GUIDs (one per line, 32-char MD5) that have already received this notice. Clear this file when starting a new campaign.
  - `WelcomeContext.LoadBroadcastNotice()` — reads both files at startup; re-reads `broadcast-notice.txt` on each welcome call (hot-reloadable so you can activate mid-flight without restart). Skips if expired.
  - `WelcomeContext.HasPendingBroadcast(guid)` — returns true if notice is active, not expired, and GUID not in notified set.
  - Injection point (line ~1274 in original): `if (HasPendingBroadcast(arrivingGuid)) sb.AppendLine($"One-time notice to weave in naturally: \"{_broadcastMessage}\"");`
  - `WelcomeContext.MarkBroadcastNotified(guid)` — appends to `broadcast-notified.txt`; called in `Program.cs` after the welcome is sent.
  - `rich` override: `|| HasPendingBroadcast(capturedGuid)` so the LLM fires even for otherwise-thin contexts.
  - To start a campaign: write `broadcast-notice.txt`, clear `broadcast-notified.txt`. To end early: delete or clear `broadcast-notice.txt`.


- **Offline-predicted name color: replace `#aaa` gray with `<i>` italic** (`WelcomeMessageGenerator.cs`, `WelcomeContext.cs`): `#aaa` is invisible on dark-mode backgrounds. Offline/predicted player names (currently colored `#aaa` in `nameColors`) should instead be rendered as `<i>name</i>` with no color. In `ApplyNameColors`, when `color == "#aaa"` (or a sentinel like `""`) emit `<i>{name}</i>` instead of a `<font>` tag. Also remove the `#aaa` assignment from wherever it's set in `WelcomeContext.cs`. Must test on both light and dark OS mode before shipping.

- **Session gap + reunion recognition** (`WelcomeContext.cs`, `DailyEssayService.cs`): when a player returns after a significant absence (suggest ≥14 days since last appearance in `census.csv`) AND known co-players from their history are currently on the same server, surface the reunion explicitly. Welcome: "You've been away for 6 weeks — Jonas and Felix are both here." Essay: narrative mention when a known regular returns after a gap; the gap duration itself is worth naming ("first time back in months" lands differently than "haven't seen you in two weeks"). Gap duration: last GUID row in census.csv vs. current time.

- **Returning-player recognition** (`WelcomeContext.cs`, `data/server-lore.json`): `WelcomeContext.cs` already knows `returningPlayer`. For 2nd+ visits the LLM prompt should signal "this person is a familiar face — acknowledge their return and the community they're part of, not generic orientation." A `returning_themes` field in `server-lore.json` (separate from `themes`) lets each server customize this voice without a prompt change. Requires: text edit to `server-lore.json` + small C# addition to pass `returning_themes` when `returningPlayer == true`.


## Group Song Palette — "You've shared some great music here"

When a group assembles on a fleet server, check if they collectively have ≥6 distinct titled songs
attributed to ≥2 distinct GUID sources in `url-guids.csv`. If so, slip a link into the group welcome
message: "You've shared some great music here." followed by `/songs/<group-hash>`. No explanation of
how we know — let the page speak.

**Data foundation** (`data/url-guids.csv`): Already exists and working. Schema:
`minutes, server_ip:port, encoded_url, encoded_title, pipe-separated-guids`. 86 rows so far (all Thai
server), 33 distinct titled songs, 127 GUIDs. Titles resolve for chordtabs.in.th song URLs and
chords69cl room URLs when Firebase title returns non-empty. `trim_url_guids.sh` keeps 90 days.

**Room URL handling**: chords69cl URLs are not song-addressable (same URL → different songs each
session). Show their resolved title as text-only in the palette. YouTube, chordtabs.in.th, UG, etc.
are song-addressable — show as clickable links. The palette may be a mix of both.

**Broad "you"**: Songs attributed to any GUID currently present count toward the threshold and appear
on the palette — not just songs that individual posted. It's the collective history.

**Group hash**: Stable hash (SHA256 or similar) of the sorted set of GUIDs that contributed ≥1 titled
song. URL: `/songs/<group-hash>`. Same group → same URL across sessions as long as contributing
membership is stable.

**Current data findings (2026-06-25)**: 77 titled rows, 62 distinct songs, 127 GUIDs — all from
the Thai server. Diagnostic against `url-guids.csv` found **44 qualifying groups** (≥6 songs, ≥2
sources). Top groups have 38 songs from 4–8 GUID sources. All current songs are `[text]` (chords69cl
room URLs — title resolves, but no song-specific link). chordtabs.in.th URLs will add `[link]` items
as they accumulate. Titles in `url-guids.csv` are URL-encoded; use `HttpUtility.UrlDecode` when
rendering.

**Implementation steps**:
- [ ] **Trigger query** (`WelcomeContext.cs`): load `url-guids.csv` once at startup (or cache with
  short TTL), filter rows where any pipe-separated GUID matches the current player set, aggregate
  distinct titled songs and contributing GUID sources, check threshold (≥6 songs, ≥2 sources).
  **Implement as a handoff** — run `claude` on `jamulus.live` with `WelcomeContext.cs` in scope.
- [ ] **Palette page** (`Pages/Songs.cshtml` or minimal API endpoint): on-demand render from
  `url-guids.csv`. Linked items for song-addressable URLs (non-chords69cl), text-only titles for
  room URLs. Mobile-friendly (Thai musicians on phones). Decode URL-encoded titles before display.
- [ ] **Welcome injection**: one sentence + link added to group welcome when threshold met. No
  explanation of how we know. Let the page speak.
- [ ] **Thai fleet server** (prerequisite — see ops TODO): trim jazz studios to free budget, then
  spin up AWS ap-southeast-1 (Singapore). Thai lobby monitoring gives dense passive URL harvest.
  44 qualifying groups already waiting in `url-guids.csv` — easter egg fires on day one.

## Season Essay — "The Full Season" (95-day retrospective)

A long-form prose essay covering the full trailing 95 days of the Jamulus network. Combined angle: character portraits (concise, individual) with songs woven in as windows into who each player is — not a separate music section, but music as texture throughout. Generated in English and Thai.

**Two jobs:**
1. **Published artifact** — something regulars will actually want to read; players who appear in it will seek it out.
2. **AI context** — distilled into `data/season-digest.json`, a structured machine-readable file consumed by `DailyEssayService` and welcome messages for continuity and specificity.

**Two digests, two time windows:**
- **`data/recent-digest.json`** (30-day rolling) — who is a current regular, recent servers, recent co-players. Primary source for welcome messages, which need fresh data.
- **`data/season-digest.json`** (95-day) — long-term patterns, established relationships, who has been around for months. Primary source for daily essays and season essays.

Both are derived from census.csv, censusgeo.csv, timeTogether.json, urls.csv — no LLM. Build and test one before wiring into anything. Welcome messages currently do expensive per-request lookups in `WelcomeContext.GatherAsync`; a pre-computed digest replaces that with a fast GUID keyed lookup.

**Digest schema** (`data/season-digest.json`):
- `generated_at` — ISO timestamp
- `window_days` — integer (95)
- `players` — array, each entry: `{guid, name, instrument, nation, days_active, top_servers: [{key: "ip:port", display_name: "...", tick_count}], known_partners: [guid, ...]}`
- `pairs` — array: `{guid_a, guid_b, name_a, name_b, hours_together, top_server_key, top_server_name}`
- `servers` — array: `{key: "ip:port", display_name, nation, unique_players, top_songs: ["title — artist", ...]}`

**Naming collision rule:** server names and relationship labels must never share a string. Use `ip:port` as the primary key for servers throughout. Never use natural-language labels like `"trio"` or `"duo"` as field names — use `group_size: 3` or reference members by GUID. The consuming LLM sees both server names and relationship descriptions; ambiguous strings cause hallucinated cross-references (e.g. a server named *Trio* being described as a trio grouping).

**Essay generation** (`ops/long-essay.py`): add a new `PROMPT_SEASON` combining portraits concision with songs-as-texture. Replace the three-variant output with a single essay per language: `wwwroot/essays/season-en.html` and `wwwroot/essays/season-th.html`. Add `--lang Thai` mode (Gemini writes Thai directly). Embed `generated_at` timestamp in both footers. Monthly cron — cost ~$0.06/month (Gemini 2.5 Pro, two languages).

**Monitoring:** read several iterations before publishing. Bot content, listener-only players, and server churn are the likely weak spots. Do not mention Cameron.

**Serving:** once quality confirmed, two subtle links at the bottom of the daily essay — Thai link first, English second, one shared age label between them: `<i>รายงานฤดูกาล</i> · <i>Seasonal report</i> · written N days ago`. Age computed from `season-en.html` mtime (both essays regenerate together). Routes in `Program.cs` for both files.

**Implementation order:**
1. Write `ops/recent-digest.py` — emit `data/recent-digest.json` (30-day) from census data. No LLM. Inspect the output before wiring anywhere.
2. Wire `recent-digest.json` into welcome context (`WelcomeContext.GatherAsync`). Observe welcome message quality.
3. Write `ops/season-digest.py` — emit `data/season-digest.json` (95-day). Wire into `DailyEssayService.BuildContext`. Observe essay quality.
4. Add `PROMPT_SEASON` to `long-essay.py`; add Thai mode; generate `season-en.html` and `season-th.html`.
5. Add localhost-only routes; monitor essay quality over a few iterations.
6. Add Thai-first + English links at bottom of daily essay once quality confirmed.

## Daily Essay

- **Essay rate limit blocks legitimate polling — real users never see the essay (diagnosed 2026-07-16)** (`Program.cs:390-409`, `Pages/Client.cshtml:3006-3017`, `DailyEssayService.cs:145-159`): the per-IP rate limit above shipped (as `_essayRateLimit`, `rl.Count > 2` → 204 — tighter than the original 5/hr spec), but two bugs compound so generation works fine (959 recent LLM calls in `output.log`, only 10 errors) while almost nobody actually sees a result:

  1. **Client retry loop exhausts the limit before the essay is ready.** `checkForEssay`/`fetchAndShowEssay` polls `/api/nearby-essay` every 60–240s for as long as nothing is shown. The route allows only 2 real attempts per rolling hour per IP — a tab open more than ~4-6 min without a ready essay has already burned its quota, so it gets silent 204s even after the essay finishes generating server-side. Confirmed in `output.log`: many IPs stuck at climbing rate-limit counts (one lifetime count: 2707 — an unattended tab open for weeks).
  2. **Solo visitors in quiet regions can never trigger generation.** `GetEssayHtmlAsync` requires a second, *different* IP to request the same `region:language` key before it calls the LLM — a lone visitor's own repeat requests never count as that second trigger. Confirmed pattern: `reqCount=1 — first request, waiting for 2nd` → `same IP as first, not triggering`, repeating for hours in quiet regions (e.g. NA-W/Tacoma).

  **Status (2026-07-16):** step (b) implemented — `/api/nearby-essay` (`Program.cs`) now checks the existing count/window without incrementing, and only calls `_essayRateLimit.AddOrUpdate` after `DailyEssayService.GetEssayHtmlAsync` returns non-null html. Polling while not-ready no longer costs quota; the 40/day `s_onDemandToday` cap in `DailyEssayService.cs` remains the real abuse guard. Watch `output.log` `[ESSAY] rate-limit` frequency over the next day or two — should drop sharply. If quiet-region visitors (solo, no second IP) still report never seeing an essay, that's the separate two-distinct-IP generation gate (`DailyEssayService.cs:155-159`) — candidate fix: relax it to trigger solo after a longer wait (e.g. 90s), trading a small LLM-cost risk for actually serving those visitors. Client-side retry-cap idea overlaps with **Client-side trigger redesign** below — revisit together if (b) alone isn't enough.

- **Daily on-demand generation cap** (`DailyEssayService.cs`): static `int` counter + `DateTime` reset-at-UTC-midnight. Inside `GetEssayHtmlAsync`, after the first cache-miss check, if `onDemandToday >= 40` return null (→ 204). Scheduled pre-gen calls bypass this counter (they go through `GenerateAsync` directly). Log `[ESSAY] daily-cap-hit ip={clientIp} key={cacheKey}` when blocked. Cap of 40 gives headroom above the typical 24–30 on-demand calls/day.

- **Web-user GUID personalization** (`DailyEssayService.cs`, new helper `WebUserIndex.cs`): match web-app browser IPs to player GUIDs via join-events.csv, then shape the shared essay around what those readers care about most. Re-evaluate match quality around **2026-06-16** (when join-events.csv will have accumulated more IPs). Current hit rate: 5% (5/110 Tier1 IPs) — expected to grow.

  **Matching logic** (validated by analysis script):
  1. Parse telemetry.log for Tier1 browser IPs in last **72 hours** (broader window = more unique readers; exclude owner/fleet IPs).
  2. Scan join-events.csv col 11 (client IP) → col 12 (strength) → col 2 (GUID). Per IP keep the row with highest strength, break ties by recency (col 0 minute).
  3. Filter lobby/bot GUIDs (name contains "lobby" or "No+Name" with strength 0).
  4. Result: `webUserGuids` — the reader population as musicians.

  **What the essay does with this knowledge:**
  - **Feature readers by name.** Web-user GUIDs get more narrative weight — name-drop order, which server leads the opening paragraph, whose instrument gets a description rather than a mention. A brief appearance from a reader is more meaningful than a long session from a stranger.
  - **Feature the people readers know best.** For each web-user GUID, pull their top contacts from `timeTogether.json`. These are the people the reader cares about most. Surface them in the essay even if their session wasn't the longest: "Fabrice dropped in for 20 minutes — his usual collaborator TJ had already been running for two hours."
  - **Let relationships anchor the narrative.** If two readers regularly play together, their co-presence (or near-miss) on a given night is a story worth telling. Use `timeTogether.json` pairs across all web-user GUIDs to find these.
  - **Geographic lead.** If a web-user GUID's inferred region matches an essay center, that center's local arc should open with their session, not bury it.

  **LLM signal (add to context builder):**
  ```
  KNOWN READERS (players who are also web app users — do not mention the app):
    Fabrice (guitar) — frequent collaborators: TJ, Jonas
    TJ (bass) — frequent collaborators: Fabrice, Herb M
    ...
  Give these players and their known collaborators slightly more narrative weight.
  Even a brief appearance is meaningful. Do not reveal or imply this knowledge.
  ```

  **Welcome message leverage** (implement after essay):
  - When a fleet welcome fires for a web-user GUID, add `knownWebUser: true` to context. The LLM should speak to them as someone who already knows the network — they know how to find jamulus.live, they've used the radar, they may have read the daily essay. Speak with more specificity and confidence: cross-server activity, session history, known crew — all fair game. Do not reference any website or app by name.
  - If the arriving player's top timeTogether contact is already on the server: high-value reunion — name them explicitly in the welcome.
  - If the server is empty but their usual crew is active elsewhere on the network: the LLM already has a "crew elsewhere" hint; for web users this lands with more weight since they may have seen it on the grid.
  - Key premise: a player who has visited jamulus.live understands the network in a way that justifies richer, less hand-holding language. The LLM can skip orientation clichés and go straight to what's specific and true about this person's place in the community.

- **Client-side trigger redesign** (`Pages/Client.cshtml`): replace 60s empty-state timer with freshness-biased edition model. Server returns `{html, edition}` from `/api/nearby-essay`. Client stores `essay_seen_edition` in localStorage. Show condition: current edition ≠ localStorage, regardless of server cards; no delay. Persist until tab close; live-replace on new edition. No re-show on refresh. Keep: close tabs before showing, fade in below tab bar. Retire: 60s timer, hide-on-server-cards behavior.

- **GUID Personalization v2**: when user is GUID-logged-in, augment base essay with a second Flash call inserting their personal 24h journey (servers, co-players, minutes). Base essay same for region; personalization layer is per-user.

- **GUID-aware essay post-processing** (`Program.cs` `/api/nearby-essay` handler, `EncounterTracker`): client sends its GUID as `?guid=HASH`; server applies a fast post-processing pass on the cached essay HTML before returning it. No extra LLM call — all in-memory string operations on the shared cached result.

  **Friend highlights** (implement first): look up the requesting GUID's top 3 co-jammers by `timeTogether`, resolve their names via `m_guidNamePairs` (HtmlDecode before matching; skip empty names; skip names shorter than 4 chars to avoid false positives). Replace each `<b>NAME</b>` in the essay HTML with `<b class="essay-friend">NAME</b>`. CSS: `.essay-friend { color: #e8a840; }`. Rare and meaningful — only fires when the reader's actual friends appear in the essay.

  **"You're in this one" badge** — *self-mention now covered for the no-login case by the IP-resolved banner below (shipped). This `?guid=`-supplied variant remains only as a possible complement for explicitly logged-in users; do not re-implement the same self-mention detection.*

- **"Hey, you're in this one!" banner — IP-resolved variant** — **IMPLEMENTED (staged, needs restart)**: server-side IP→GUID→banner. `DailyEssayService.TryGetVisitorBanner(ip, cacheKey)` resolves the visitor's IP via `IdentityManager.GetGuidStrengths` (floor ≥16), intersects with a featured GUID→name provenance map captured at generation time (`GenerateAsync`, stored in the widened `s_cache` tuple — the open `LookupCensusName` item was sidestepped by storing GUID→name directly, so the name is the census name that was fed to the LLM), confirms the name survived into the prose via word-boundary regex, and returns an inline-styled left-border callout. Endpoint (`Program.cs` `/api/nearby-essay`) prepends it after `GetEssayHtmlAsync`; `ResolveCacheKeyAsync` locates the visitor's own region essay. No client CSS needed (inline style).

  **Observability (shipped):** every attempt logs `[ESSAY-YOU] eval … result=<token>` to `output.log`, where token ∈ {no-essay, no-guids, below-floor, no-mention-match, name-not-in-prose, shown}. Success rate = `grep -c "result=shown"` ÷ `grep -c "\[ESSAY-YOU\] eval"`. Shown notifications also append to `data/essay-you.log` (git-ignored) with GUID, name, IP, `strength` (merged), `golden` (join-events-only strength — <16 means the qualifying strength came from fleet synth, weaker), `guidIPs` (distinct join-events IPs for that GUID — >1 flagged `MULTI-IP`), `qualifying` (how many ≥16 GUIDs the IP resolved to), `featured` (essay's candidate count). Flags `SYNTH-ONLY` / `MULTI-IP` / `MULTI-QUALIFYING` mark lower-confidence firings. `IdentityManager.GetIpCountForGuid` backs the multi-IP signal. **Live since 2026-08-10 restart; confirmed working** — first real firing (EU-W:Dutch, "Rens") logged `result=shown`. **Owner decision (2026-08-10): false positives are acceptable** — a wrong "you're in this one" now and then is a fine trade. Do NOT tighten: fleet-synth-16 stays valid (no `golden ≥ 16` floor), no `guidIPs` ceiling, harvester shared-IP denylist dropped. The `SYNTH-ONLY`/`MULTI-IP`/`MULTI-QUALIFYING` flags remain as ledger visibility only, not gates.

  **Co-jammer near-miss line**: if the reader's GUID was active in the last 24h (appears in census.csv for today) AND one or more of their top-3 co-jammers was also active but on a different server, append a short italic line after the essay's closing `<p>`: `<p class="essay-nearmiss"><i>Jonas was on Freiheit last night while you were on CBVB.</i></p>`. Derived entirely from census.csv + timeTogether — no LLM. Skip if they were on the same server.

  **Crew summary line**: if the reader was NOT active last 24h but their top co-jammers were, append: `<p class="essay-crew"><i>Your people last night: Jonas on <i>Freiheit</i>, Felix on <i>Studio D</i>.</i></p>`. Server resolves crew names + server names from census.csv. No LLM. Shows even when the reader has no personal session to anchor to.

- **Essay time-fixation: reduce timing detail & prompt surface area** (`DailyEssayService.cs:641`, `data/essay-system-prompt.txt`): essays over-narrate duration/start/end times ("kept a steady jazz pulse going... for nearly a full day..."), despite repeated prior attempts to suppress this via prompt rules. Two feeding mechanisms identified: (1) every session in the context gets three separate timing representations — local start-end, UTC start-end, and an approx-duration phrase — the most complete, always-present data field in the whole input, present for every session unlike songs/instruments which are spotty; (2) ~25 of the prompt's ~90 WRONG/RIGHT examples concern time/duration phrasing, which anchors model attention on the topic even though every one of those examples is a prohibition, not an encouragement.

  **Full plan (in order of increasing cost):**
  1. **Prompt-only frequency cap** — add one explicit instruction near the top of `essay-system-prompt.txt`: mention time-of-day or duration in at most ~1 in 3 sessions described; most sessions should carry zero time language. No code change, no context change.
  2. **Trim prompt example library** — consolidate the ~25 time-related WRONG/RIGHT pairs down to 4-5 representative ones, freeing up prompt space currently spent reinforcing the topic even in negative form.
  3. **Context reduction** (`BuildContext`, `DailyEssayService.cs:628-669`) — give full start/end + UTC + duration detail only to the single headline session (top local + top global by score); replace the three timing fields for every other session with one coarse day-part tag (morning/afternoon/evening/night) — no numbers, no duration phrase at all.

  **Minimum-step approach (try this first):** step 1 alone — cheapest possible test, pure prompt edit, reversible in one line. Generate a handful of essays and read them before deciding whether steps 2–3 are still needed. Steps 2 and 3 stay on the shelf unless step 1 alone doesn't move the needle.

  **Status (2026-07-15):** step 1 done — added a frequency-cap bullet to `essay-system-prompt.txt` (1-in-3 sessions may carry time/duration language, zero for the rest, no more than one time-opened paragraph per essay). No code change, no restart needed (prompt re-read per call). Next: read the next few generated essays (`data/essay-llm.log`, or trigger via the spoofed-IP curl in `JamFan22/CLAUDE.md`) and judge whether steps 2–3 are still warranted.

## Geolocation / Geo-diag

- **region-centroids.json**: canonical lat/lon per `{countryCode}:{regionName}` from GeoNames/Natural Earth, to replace ip-api datacenter-IP coordinates.

- **InferredRegion open concerns**: `country-adjacency.json` not yet wired; `fleet(1d)` should also apply ≤1 override.

- **InferredRegion: nightly cull cron + /login enhancement**: prune stale server-region cache; pre-filter by visitor IP.


## Data / Infrastructure

- **`/chat-url-client`: remove join-events GUID resolution** (`Program.cs`): the modified client always supplies `serverAddr`, so the `IdentityManager.GetGuidStrengths` lookup is dead weight in this path — it can never know the server better than the client does. Remove the GUID-resolution block (lines ~482–520) and the `bestGuid`/`bestStrength` variables; keep only the `req.serverAddr` validity check and the identity gate (`guidStrengths.Count == 0 && FleetGuidCache…`). The identity gate itself may also be removable if the fleet binary is the only caller — audit before dropping.

- **`server.csv` timestamp** — deployed 2026-06-24 or earlier; confirmed live. Schema: `ip:port,name,city,country,minute` (col 4 added). Readers using cols 0–3 are unaffected. `server-lore.json` `name` field remains the authoritative override for stale names.

  **Pruning plan** (file is pure append-only, currently ~45 MB / 867K lines, never pruned):
  - **Near-term (a few months post-deploy):** drop all rows where col 4 is absent (old format). Safe once the pre-restart backlog is no longer the latest row for any `ip:port`.
  - **Long-term:** for each `ip:port`, keep only the row with the highest minute value; drop rows older than ~1 year. Script: group by `ip:port`, keep `max(col 4)` row per key, discard the rest. Do not run until the file is mostly minute-stamped — before that, "last by position" and "last by minute" diverge for old rows.

## Fleet / Infrastructure

- **Rename TH + HK dormant fleet instances to "destiny"-themed native names** (remote systemd service files on the fleet hosts — NOT this repo): the limited-time "See your destiny" joiner-centered essay welcome (see **LLM Welcome** → the essay feature; code already shipped in `Program.cs` `DormantEssayFeature`, `WelcomeMessageGenerator.GetEssayAsync`, `WelcomeContext.EssayLanguage`, `data/welcome-essay-prompt.txt`) fires on servers geolocated in Thailand and Hong Kong. The owner wants the **actual Jamulus server display names** of our dormant instances in those two countries changed to reflect "see your destiny" in the native script — Thai for the TH instances, Chinese (Traditional) for the HK instances — because that's where the destiny essays appear. The destiny theme applies **only** to TH and HK — nowhere else in the fleet.

  **Important distinction:** the app-side work is done and only brands the welcome *chat message* (a `🔮` banner: `ดูโชคชะตาของคุณ` / `看見你的命運`). The **directory display name** each instance registers is set per-instance in its own Jamulus systemd unit (the `-n`/`--servername` / `--serverinfo` arg in `ExecStart`) on the remote fleet host — untouched by anything in this repo. That is what this task changes.

  **What the agent must do:**
  1. Identify which dormant fleet instances are in TH and HK. Start from `data/fleet-server-ips.txt` (IPs like `43.208.241.171`, `43.212.6.248` → AWS `ap-southeast-7` Bangkok; `16.163.141.108` → AWS `ap-east-1` Hong Kong — **verify by geolocation, don't trust the guess**) and cross-reference the dormant-instance tooling: `ops` `dormant-monitor.py` (runs as `dormant-monitor.service`, starts/stops instances via boto3), `/root/dormant-ip-cache.json` (instanceId→IP), and `dormant-instances.json` (per-instance templates / `fleet_entries`). Current custom names seen in `data/welcome-events.log` for TH: *Route 66*, *Krub Club*, *Piper Club*, *Esplanade*, *Sei*; and *女巫店* (already Chinese) — confirm each one's actual country before renaming.
  2. Determine access to each host's systemd unit. Per prior TODO notes, **no SSH key on this host currently reaches the fleet instances** — resolve access first (this was an open blocker for reading Jamulus service configs; see the `/debug/fleet-rpc` item's "Concerns"). Dormant instances get a fresh EC2 instance per wake, so a durable rename must live in the **AMI / user-data startup script**, not just the running instance, or it reverts on next wake.
  3. Edit the servername in each TH/HK unit to a destiny-themed native name, restart that Jamulus process, and confirm the new name appears in the directory feed. **Final names (picked — assign one per instance, primary first; do not duplicate a name across two live instances):**
     - **Thailand (Thai):** `โชคชะตา` (*chôhk-chá-taa*, "Destiny" — flagship/busiest instance) · `พรหมลิขิต` (*phrom-lí-kìt*, "Fate Foretold") · `เผยชะตา` (*phə̌əy chá-taa*, "Fate Revealed") · `ดวงชะตา` (*duang chá-taa*, "Your Stars").
     - **Hong Kong (Traditional Chinese):** `命運` (*mihng-wahn*, "Destiny" — flagship) · `緣分` (*yùhn-fahn*, "Fated Meeting" — the fate that draws people together; ideal for the busiest HK instance) · `天命` (*tīn-mihng*, "Heaven's Will") · `窺見命運` (*kwāi-gin mihng-wahn*, "A Glimpse of Destiny").
     - All are short by design — long names get truncated in the grid. Owner has approved the destiny theme; confirm the exact per-instance assignment before restarting.

  **Cautions:** outward-facing, hard-to-reverse infra change on production hosts across multiple cloud accounts — confirm the exact instance list and the final names with the owner before restarting anything. Record the old names so the rename can be reverted when the essay feature expires (hardcoded `2026-08-05`, or after 29 essays).

- **Chile expansion (GCP `southamerica-west1`, Santiago)**: census shows 6,925 player-sessions / 5 active servers in Chile with no fleet coverage. Add a dormant GCP instance in Santiago. Lowest-effort: spin up, install Jamulus + ChatReporter, snapshot as an AMI/image, keep stopped until demand justifies it.

- **Persistent outbound WebSocket channel for welcome delivery** — eliminates the need to open inbound RPC ports (9999/9998) on cloud fleet servers.

  **Problem:** JamFan22 currently opens an outbound TCP connection *to* the fleet server's RPC port to deliver welcomes. Cloud firewalls (AWS security groups, OCI VCN ACLs) block inbound TCP by default — TCP hole-punching cannot fix this, as the deny rule blocks the inbound SYN regardless. Every new fleet instance requires a manual firewall rule. Servers without it (e.g. `24.199.107.192`) receive no welcome messages at all.

  **Solution:** Flip the connection direction. Each fleet server binary (`chatreporter.cpp`) opens a persistent outbound WebSocket to `wss://jamulus.live/fleet-rpc-channel?port=NNNN` at startup, riding the existing HTTPS port 443 — no new port, no new firewall rule on either end. JamFan22 receives the connection, registers it, and pushes delivery instructions down the socket. The binary delivers each message via loopback RPC to `127.0.0.1:m_rpcPort`. All TCP is binary-initiated outbound.

  **JamFan22 side (`Program.cs`) — ✅ DONE (2026-07-03):**
  - `GET /fleet-rpc-channel?port=NNNN` WebSocket endpoint live; registry in `_fleetWsRegistry` (keyed `{RemoteIpAddress}:{port}`).
  - Personal welcome and group welcome blocks both check WS first; fall back to existing TCP if not registered or send fails.
  - Log prefixes: `[fleet-rpc-channel] <UTC ts> connected/disconnected` (timestamped 2026-08-19; grep `\[fleet-rpc-channel\].*connected`, not the two words as one literal), `[PLAYER-IDENTIFIED-WELCOME] ws`, `[PLAYER-IDENTIFIED-GROUP] ws`.

  **C++ side (`chatreporter.cpp`/`.h` + `main.cpp`) — ✅ DEPLOYED (2026-07-04 build; source uncommitted in central `/root/jamulus`, commit pending — see central TODO.md). `[fleet-rpc-channel] connected` lines confirm live fleet connections. Spec as built:**
  1. Add `QWebSocket* m_fleetSocket` to `ChatReporter`. At `ChatReporter::start()`, connect to `wss://jamulus.live/fleet-rpc-channel?port=NNNN` where `NNNN` is `m_port`.
  2. On `textMessageReceived`: parse `[{"channelId": N, "message": "..."}]`. For each entry, call new `deliverWelcomeViaRpc(channelId, message)` — opens a `QTcpSocket` to `127.0.0.1:m_rpcPort`, sends `jamulus/apiAuth` + `jamulusserver/sendClientChatMessage`, closes. All async via Qt event loop; never blocks the audio path.
  3. On disconnect: `QTimer::singleShot(5000, ...)` in the `disconnected` slot, doubling up to 60 seconds.

  **Multiple servers on the same host:** each Jamulus process connects with its own `?port=NNNN` (e.g. `22224`, `22225`). JamFan22 keys the registry by `{RemoteIpAddress}:{port}` so they are always distinct entries, even on shared-IP instances (Freiheit+Jazzstübchen, Louvre+New Morning, the full Trio cluster, etc.).

  **Rollout:** deploy new C++ binary to one server; watch for `[fleet-rpc-channel] connected` in the JamFan22 log; trigger a test join; confirm welcome appears. Old and new binaries coexist safely via the fallback TCP path. Once all servers are on the new binary, remove the TCP fallback from JamFan22.

  **Reliability improvement:** eliminates the entire class of `OperationCanceledException` / TCP-connect-timeout welcome failures. All currently cloud-firewalled servers (including `24.199.107.192`) start receiving welcomes the moment they connect.

- **`/debug/fleet-rpc` — localhost RPC-over-WebSocket debug endpoint** (proposed 2026-07-08, owner undecided — revisit): ~15-line GET endpoint in `Program.cs` next to `/debug/fleet-levels`, same loopback-only gate. Takes `?key=IP:PORT&method=...` (default `jamulusserver/getServerProfile`), looks up the live WS in `_fleetWsRegistry`, and sends the call through the existing `FleetWsRpcCallAsync` (same 5s timeout/locking welcomes already use). Empty params, so only parameterless reads work in practice.

  **Rationale:** the 22226 rooms on Paris and Milan have unknown names — never directory-registered (absent from server.csv/census), and their RPC port 9997 is blocked by the AWS security groups (only 9999/9998 open; confirmed by sweep — only the permanent Dallas host answers 9997). Their only reachable interface is the outbound WS channel each room's ChatReporter holds open to JamFan22. This endpoint would let us ask any connected room its own name/registration status — this class of invisible-room mystery generally.

  **Concerns:** adds a production code path + restart for what is essentially a one-off lookup; alternative is reading the Jamulus service configs on the instances once SSH/console access exists (no key on this host works today). Related cleanup regardless of decision: Paris/Milan `fleet_entries` templates in `dormant-instances.json` carry only 22224/22225, so their 22226 lines in `fleet-server-ips.txt` are stranded on old IPs (e.g. `15.160.126.238`, `52.47.185.95`) and never get patched on wake.

- **ChatReporter missing from all dormant instances + `147.182.199.22`**: 11 fleet servers have never sent a `player-identified` call to JamFan22 — meaning no welcome messages, no IP tracking, and no census audible data for any player who joins them.

  **Affected servers (as of 2026-07-04):**
  - All 10 currently-tracked dormant instances (Oregon, Thailand, Milan, Taipei, No Way, Montreal, Paris, Sao Paulo, Singapore, Spain, Garibaldi, Calgary) — all AWS, all rotating IPs
  - `147.182.199.22` (DigitalOcean Santa Clara, **permanent**) — also absent from census entirely

  **Root cause:** these servers were provisioned after the initial ChatReporter deployment. The dormant instances each get a new EC2 instance on first spin-up that never had the ChatReporter binary installed. `147.182.199.22` is a permanent server that was also missed.

  **Fix:** deploy the ChatReporter binary (and the new outbound WebSocket variant per the persistent-channel item above) to each instance. For dormant instances, this means baking it into the AMI or user-data startup script so each fresh instance gets it automatically. `147.182.199.22` can be done manually via SSH like any other permanent fleet server.

  **Update 2026-07-07:** the 2026-07-04 deploy put the WS-capable binary on the *running* dormant instances (Milan and Maple were recovered from failed deploys the same day — missing `libqt5websockets5` / wrong-arch binary; see central TODO.md). Remaining: stopped dormant instances on next wake, the AMI/user-data bake, and `147.182.199.22`.

  **Impact:** players on dormant servers get no welcome, no co-player context, no lore — they're invisible to JamFan22 from the inside despite appearing in census from the directory feed.


- **`132.226.27.144:22225` (Rising jazz) intermittently fails hole-punch** (`harvest.cs:417-497`): **fix implemented 2026-07-28, build-verified, not yet deployed.** Root cause: once a server is confirmed `s_requiresPunch=true`, there was no in-cycle retry on timeout — it fell straight into the unconditional `catch` with no second attempt, unlike the *discovery* path which retries once before giving up. Evidence: 2,928/53,787 (~5.4%) failures over the full log; ~1/3 clustered with same-cycle failures on `132.226.27.144:22224` (same host, different directory host), pointing at OCI NAT/security-list flakiness rather than a directory-specific issue.

  **Fix:** widened the retry `catch` guard from `when (!requiresPunch && hasDirInfo)` to `when (hasDirInfo)` so confirmed-punch servers now also get one punch+retry before giving up for the cycle; guarded the `s_requiresPunch[ipport]=true` log line with `if (!requiresPunch)` so already-confirmed servers don't re-log "marked as requiring hole-punch" on every successful retry. `dotnet build` clean, 0 errors. Needs `systemctl restart jamfan22` to take effect in production.

- **Paris jazz server** (`15.188.59.20`): add a second Jamulus process on port 22225 for jazz genre. RPC port 9998 already pre-wired in `fleet-rpc-ports.txt`. Steps: SSH to Paris VM → start Jamulus server on 22225 → open port 22225 in cloud firewall → add `15.188.59.20:22225:9998:jazz.jamulus.io:22324` to `fleet-server-ips.txt`. Name suggestion: *Jazz Café* or similar. (Louvre RPC confirmed working via `output.log` — `nc` timeout was misleading; the VM's firewall allows outbound connections to us on 443 but blocks our inbound TCP probe on 9999.)

- **Welcome messages blocked: `24.199.107.192`** (large multi-port Trio cluster): every welcome attempt fails with `OperationCanceledException` on RPC ports 10001, 10002, 10007. The host's firewall blocks inbound TCP from jamulus.live — same connectivity issue that forces hole-punch for all its game ports. No welcome messages are reaching any player on this cluster (ports 22121–22127). Resolution: the outbound WebSocket channel — the Trio cluster gets welcomes once it runs a jamfan binary with the WS client (Trio fleet-integration plan, central `/root/TODO.md`). Do not pursue inbound RPC port openings; the outbound channel is the durable fix.



## Client-sourced level data (`POST /client-levels`)

The operator's jamfan Jamulus client (chatreporter.cpp) is frequently connected to non-fleet servers. It already receives per-channel level nibbles (message 1015) and per-channel GUID data (via `reportClientInfo`). This makes it a natural long-running silence sampler for servers the lounge bot isn't watching.

**Endpoint:** `POST /client-levels` on JamFan22
**IP gate:** read allowed IPs from `data/client-level-reporter-ips.txt` (operator's home/VPN IPs); return 403 for anything else. No shared secret in the binary — IP gate is sufficient for the single-operator threat model.

**Request body:**
```json
{"server": "ip:port", "channels": [{"guid": "abc...", "level": 3}, ...]}
```
- `guid` — MD5(name + phpCountryName(countryId) + phpInstrumentName(instrumentId)), same as chatreporter's existing GUID logic
- `level` — 0–15 nibble from the 1015 channel level list

**C++ side (chatreporter.cpp):** add a 30-second timer that fires while connected. Cross-join the stored channel-info table (keyed by channel slot) with the most recent 1015 nibbles; POST for each slot where channel info is known. Fire at most once per 30s; cancel/restart on connect/disconnect.

**JamFan22 side:** store latest `(guid → level)` per `ip:port` in a new `m_clientLevels` dict (same shape as `m_fleetClientLevels`). When `JamulusCacheManager` writes census.csv rows for non-fleet servers, fall through to `m_clientLevels` the same way it currently falls through to `NonFleetSilencePoller.ClientLevels` (that path is already wired, currently empty). Census col 3 (`audible`) semantics stay identical: `0` = this GUID's level was 0, `1`-`f` = audible, value = total audible player count in session. No schema change.

**Cadence note:** client reports every ~30s; census writer samples once per minute. JamFan22 stores the latest report and the census writer picks it up at write time — same behavior as fleet polling (harvest.cs polls every ~50s, census writes every ~60s).

**Silent vs quiet-but-present distinction:** the existing `audible=0` / `audible>0` encoding already captures the binary presence signal. A GUID showing `audible=0` across hundreds of samples is a strong silence signal. A GUID showing `audible=1` or `audible=2` (active but in a near-empty or quiet session) is real presence. The census consumer (welcome context, essay) should treat `audible>0` as "present" regardless of the session size value — do NOT suppress a GUID with `audible=2` the same way as `audible=0`. The current LLM prompt context should already reflect this; verify before assuming.

**What this does NOT capture:** the GUID's own loudness within a non-zero sample. `audible=1` could be a whisper or a roar — only the boolean is encoded. If loudness distinction ever matters (e.g., "barely audible vs. clearly playing"), that would require a raw-level file (deferred — don't create it now).

**Prerequisite:** operator's current home/VPN IPs collected and written to `data/client-level-reporter-ips.txt` before enabling. Start with this file and the 403 gate before wiring any C++ timer.


- **Ear silence sampler** (`NonFleetSilencePoller.cs`): rewrite scheduler with GUID-urgency scoring. Kill switch currently active (`silence-poller-disabled`).

  **Stealth principle:** Ear is visible. Every probe is a named connection that appears briefly to every player on that server. The constraints below are designed so Ear arrives rarely, at the highest-value moment, and never where audio data is already flowing from another source.

  **Optimal target selection — score-based, patience-first:**
  Each 5-second cycle the scheduler checks whether to fire. It only fires when two conditions are both true:
  1. The global cap allows it (no probe in the last 60 min)
  2. The top-scoring eligible server clears the minimum score threshold

  Score per server:
  ```
  score(server) = Σ t_unseen(g)  for each GUID g currently on that server
  ```
  `t_unseen(g)` = minutes since GUID g was last sampled by Ear, OR minutes since g was first observed anywhere across all directory feeds (not just this feed) if never sampled. **No never-sampled multiplier** — unknown GUIDs accrue time like everyone else; their advantage is naturally time-compounding (unseen for 3 hours beats sampled 1 hour ago). This prevents name-changers (new GUID every few minutes) from gaming the score.

  **Lay-in-wait / pounce behavior:** During the blocked window (60 min after last probe), the scheduler watches all servers every 5 seconds as their scores climb. The moment the cap clears, it immediately pounces on whatever has the highest score at that instant — the server with the largest group of longest-unseen GUIDs. If no server clears the threshold at cap-clear time, it keeps watching and pounces the cycle the threshold is first crossed. The pounce time within the second hour is determined by when the optimal target emerges, not by a fixed offset from the hour boundary.

  **Data structures to ADD:**
  - `_guidFirstSeen: ConcurrentDictionary<string, DateTime>` — set on first appearance, never updated
  - `_guidLastSampled: ConcurrentDictionary<string, DateTime>` — updated for every GUID on a server when that server is probed
  - `_probeTimestamps: Queue<DateTime>` — rolling window for global cap; entries older than 60 min are purged each cycle

  **Data structures to DROP:**
  - `_lastPolled` — replaced by per-GUID tracking
  - `_pollInterval` — replaced by scoring; the doubling/halving interval table is gone

  **GUID computation:** use `EncounterTracker.GetHash(name, country, instrument)` — same MD5 as used in `Api.cshtml.cs`. GUIDs are derived from the `sv.clients` entries in the directory listing.

  **Filters — applied before scoring:**
  - ~~**Per-GUID Ear-visit floor (20 min):**~~ **RETIRED.** With a global cap of 1 probe/hr, no server can be re-hit within an hour anyway — the floor is always satisfied automatically.
  - ~~**Silence-confirm re-probe (4 min):**~~ **REMOVED.** Don't re-probe to confirm silence; let the 🔇 emoji resolve via TTL in `Api.cshtml.cs` instead (see below). Burning the hourly cap on a confirmation probe is not worth it.
  - **Coverage exclusion:** skip fleet servers (existing `_fleetIps` check) and any non-fleet server currently keyed in `JamulusAnalyzer.m_connectedLounges` — lounge SSE already provides audio state for those.
  - **Minimum occupancy (≥3 GUIDs):** lone and duo servers don't justify the visibility cost; small groups are also easy to startle.

  **Minimum score threshold — wait for the ideal sample:**
  Don't probe unless top score ≥ **60 GUID-minutes** (e.g., 3 players each unseen for 20 min). Tunable via `_minScoreThreshold`. Below the threshold, idle. This is the patience mechanism: no pressure to probe a marginal server just because the cap window is open.

  **After a probe:** update `_guidLastSampled` for every GUID present on the probed server; push `DateTime.UtcNow` to `_probeTimestamps`.

  **Cleanup:** when a GUID disappears from `LastReportedList` for >4 hours, remove from `_guidFirstSeen` and `_guidLastSampled`. On re-appearance it starts fresh — correct, since it may be a new session.

  **Logging** (one line per probe; one line per idle skip):
  ```
  [EAR] ip:port "Server Name" guids=N score=X quiet=T/F audible=N/M
  [EAR] idle — cap|threshold|no-candidates (next cap window in Xmin)
  ```

  **Quiet-state TTL in `Api.cshtml.cs`** (replaces silence-confirm):
  A single Ear probe that returns quiet must not permanently suppress a card. Add a 90-min TTL to the `isQuiet` and `isSignalKnown` checks (lines ~360–365). Add to the NonFleetSilencePoller clause:
  ```csharp
  && _nfq.Quiet && _nfq.Error == null
  && (DateTime.UtcNow - _nfq.UpdatedAt).TotalMinutes < 90
  ```
  And apply the same TTL to `isSignalKnown` so expired entries don't appear as "known signal." 90-min TTL: if Ear doesn't re-probe within 90 min, the quiet state expires and the card re-emerges as "unknown signal" — better to show a potentially-active server than to permanently hide it.

  **Implementation checklist:**

  `NonFleetSilencePoller.cs` — full rewrite:
  - **Add:** `_guidFirstSeen: ConcurrentDictionary<string, DateTime>` (set on first appearance, never updated); `_guidLastSampled: ConcurrentDictionary<string, DateTime>` (updated per probe); `_probeTimestamps: Queue<DateTime>` + `_probeTimestampsLock: object` (global cap rolling window); `_lastIdleLog: DateTime` (throttle idle log to once per 5 min); `_minScoreThreshold = 60.0`
  - **Drop:** `_lastPolled`, `_pollInterval`, `_concurrencyGate` (no concurrency needed — 1 probe/hr)
  - **Keep unchanged:** `Status`, `ClientLevels`, `_fleetIps`, `_directoryHosts`, kill-switch fields, `BuildUdpFrame`, `JamulusCrc`, `Parse1013Body`, `TryUdp1014Async`, `PollLoopAsync` outer shape
  - **New `RunOneCycleAsync` steps:**
    1. Kill switch check → `Status.Clear(); ClientLevels.Clear(); return`
    2. Iterate `LastReportedList` → build `serverToGuids` dict (ipport → guids+dirHost+name); call `_guidFirstSeen.TryAdd(guid, now)` for each GUID; accumulate `allVisibleGuids`
    3. Purge absent GUIDs: `_guidFirstSeen` keys not in `allVisibleGuids` and older than 4h → `TryRemove` from both dicts
    4. Purge `_probeTimestamps` entries older than 60 min (under lock)
    5. Remove `Status`/`ClientLevels` keys not in `serverToGuids`
    6. Check cap: `capped = _probeTimestamps.Count > 0`
    7. Score eligible servers: skip lounge-connected, skip if `guids.Count < 3`; `score = Σ (now - baseline).TotalMinutes` where `baseline = _guidLastSampled[g]` if sampled else `_guidFirstSeen[g]`
    8. If capped → throttled idle log `[EAR] idle — cap (next cap window in Xmin)`; return
    9. If no candidates or `bestScore < _minScoreThreshold` → throttled idle log `[EAR] idle — threshold|no-candidates`; return
    10. Probe top server → update `_guidLastSampled[g] = now` for each GUID on server; push `now` to `_probeTimestamps`
  - **New `ProbeServerAsync` signature:** `(string ipPort, string? directoryHost, string serverName, List<string> guids, double score)` — drop all `_pollInterval` manipulation; log `[EAR] ip:port "Server Name" guids=N score=X quiet=T/F audible=N/M`

  `Api.cshtml.cs` lines ~360–365 — quiet-state TTL:
  - `isQuiet` NonFleet clause: add `&& _nfq.Error == null && (DateTime.UtcNow - _nfq.UpdatedAt).TotalMinutes < 90`
  - `isSignalKnown` NonFleet clause: replace `NonFleetSilencePoller.Status.ContainsKey(serverAddress)` with `NonFleetSilencePoller.Status.TryGetValue(serverAddress, out var _nfqk) && (DateTime.UtcNow - _nfqk.UpdatedAt).TotalMinutes < 90`

- **census.csv `audible` column** — per-GUID hex char: `0`=this GUID silent, `1`-`f`=this GUID was audible AND value=total audible count on server (capped at f=15), empty=no data. Goal: filter silent players from essay narratives.

  **Silence-only GUID suppression** *(future)*: if a GUID has ≥1 audible sample and ALL samples are `0`, hide or deprioritize that GUID from the nearby list and essay. Evidence of all-silence = likely listener/bot. Only fires on positive evidence, not absence. Implement after Ear coverage grows.

  **Fleet level-detection accuracy tests** (validate 1014+1028 same-socket slot mapping):
  - **census.csv audible column spot-check**: for a fleet server during a known active session, `grep` census.csv rows for that time window and verify the audible field is non-zero for GUIDs that were playing.
  - Probe live: `python3 fleet-probe.py` (optional `--loop`). Cross-reference: `curl -s http://localhost:5000/debug/fleet-levels | python3 -m json.tool`.

  **M4 — Essay filtering** *(the payoff)*
  - `DailyEssayService.ScanCensusAsync`: when all of a GUID's ticks in a session window have `audible=0`, exclude that GUID from the player list passed to the LLM.
  - Log `[ESSAY] excluded N silent GUIDs from {centerId} session window`.

- **Server suppression simplification** (`Api.cshtml.cs` ~lines 255–331): add `confirmActive` override after all duration-based `fSuppress` logic: `if (fSuppress && NonFleetSilencePoller.Status.TryGetValue(serverAddress, out var nsProbe) && !nsProbe.Quiet) fSuppress = false;` — confirmed audio overrides duration heuristics; old rules remain fallback when no probe data. Fleet servers use `m_fleetSilenceStatus`, not NonFleetSilencePoller.

- **Alt-source cache staleness threshold** (`gather-server-data.py` on `24.199.107.192:5001`): currently 300s. With JamFan22 polling every 5s, worst-case latency from this layer is ~305s. Reducing to 30s would cut that to ~35s but at ~10× the call rate to Jamulus's `servers.php` endpoints. 30s is probably fine — Jamulus doesn't aggressively rate-limit — but confirm before changing on the remote host. This is independent of the sampler; it's purely upstream directory polling. **Join latency chain for reference:** alt-source cache (≤305s typical) → JamFan22 poll loop (≤5s) → browser fetchAndRender (≤15s) → card entrance animation (~1s) = worst-case ~326s, typical ~20s.


- **Geo-city column unused — keep it that way** (`join-events.csv` col 7): Layer 2 writes the IP-geolocated city here; JamFan22 never reads it. `nearby.cs` reads col 5 (user's self-typed Jamulus location) as `UserCity`. The distinction matters: self-reported city is user-controlled; IP-derived city is inferred without consent. Do not surface col 7 to any user-visible data product.

- **Alt-source silent-state slow polling** (`JamulusCacheManager.cs`): when all clients on a blocked server have `minsHere > 8h` (bots), skip N round-robin cycles. Clear on any short-duration client.


- **Blocked-GUID visibility** (`FleetGuidCache`, ops script): query `fleet-guid-ip.csv` for GUIDs where every recorded IP has `blocked=1` — these are players who hit the ASN/IP block gate every time they join a fleet server. Useful for spotting over-blocking or identifying VPN users who never get through. An ops script (not a runtime feature) is sufficient.

- **Lounge bot summon command logging**: lounge bot should POST summon requests (timestamp, requester, target server) to a JamFan22 endpoint.

- **Listener-triggered temporary lease** (`StreamGate.cs`, `harvest.cs`): when `lobbyAudience > 0` (someone is actually listening) AND the server has an active, sound-producing group (audible fleet level data or confirmed non-quiet), automatically create a short-duration temporary lease on that server. The lease prevents `StreamGate` from pulling the lounge bot away mid-session, protecting the live audience. Unlike recurring weekly leases, this one expires and does not repeat — it only covers the current session. Design sketch: detect the condition in `ChannelLevelPollLoopAsync` (where both audience count and level data are available); call a new `StreamGate.TryCreateAudienceLease(serverKey, durationMinutes)` that refuses if a lease already exists; log `[LEASE-AUTO] audience={N} on {serverKey} duration={T}min`. Duration suggestion: 30 min, renewable while audience remains.

## Telemetry / Analytics

- **`user-awareness.py`: fix engagement_score()** — current score inflates for bots and long-idle tabs via `total_sec // 60` (dwell time). Reweight to favor deliberate actions: `hover_server`, `scroll_depth`, `click_musician`, `click_more`, `click_listen`, `tab_switch`, `nearby_toggle`, `return_visit`, `tracked_arrival`, `ui_hide`. Cap or discount dwell time alone.


- **Browser compatibility audit — Firefox on Windows**: Web Audio API / MediaRecorder support; translated notice if unsupported; enumerate User-Agents from logs.

## Audio Highlights in the Daily Essay

**Concept:** Record the most musically active windows of fleet sessions, extract a highlight clip, and embed it as a playable `<audio>` element in the daily essay where that session is described. Reader arrives at a paragraph about the Wine Glass Crew's two-hour session on Freiheit and there's a 45-second clip right there.

**Transparency:** Jamulus shows a red recording-indicator bar to all connected players while recording is active. By triggering recording only during high-activity windows — not the whole session — the bar appears when something worth capturing is actually happening. Players see it at the exciting moments, not sitting on screen for hours while nothing much is going on. The owner operates all fleet servers, so no separate consent is needed.

**The trigger is already built:** `harvest.cs:ChannelLevelPollLoopAsync` polls fleet servers every 10s via UDP and parses per-channel VU levels (nibble-packed in the `PROTMESSID_CLM_REQ_CHANNEL_LEVEL_LIST` response). The result is already in `harvest.m_fleetSilenceStatus`. A new `AudioHighlightService` watches this data: when N channels show non-zero levels for M consecutive polls (sustained music, not a one-shot), call `jamulusserver/startRecording` via RPC. When activity drops below threshold for K polls, call `stopRecording`. Each resulting WAV is a "hot window" — already the interesting part.

**The rest of the pipeline:**
1. Post-recording Python script `highlight-extractor.py`:
   - If the recording is short (< 5 min) it's already the highlight — skip analysis.
   - Otherwise FFmpeg `silencedetect` + sliding RMS window picks the densest 60s.
   - FFmpeg filter chain: noise gate → `loudnorm` (LUFS normalization) → light denoiser → export 128 kbps MP3.
2. Clip + metadata sidecar (UTC window, server key, players from census.csv during that window) stored at `wwwroot/highlights/YYYY-MM-DD-HHmm-serverkey.mp3`.
3. `DailyEssayService` scans `wwwroot/highlights/` for clips from the last 24h, matches by server key and time overlap to the sessions it's describing, and passes: *"Audio highlight: [url]. Recorded 22:14–22:15 UTC. Players at time of recording: RustyShackleford, Z."*
4. LLM embeds `<figure><audio controls src="..."></audio><figcaption>...</figcaption></figure>` in the paragraph about that session. The LLM does not hear the audio — it only places the element where the narrative calls for it.

**Storage:** ~90 MB for 90 days of 60-second clips (negligible). Raw WAVs from hot windows kept 7 days for re-extraction.

**Key unknowns before starting:**
- Verify `jamulusserver/getRecorderStatus` and `startRecording` work on the actual fleet Jamulus builds. The RPC interface version may differ from the current release docs — test on one server before assuming.
- Jamulus records a stereo mix of all clients. Confirm the output is a single mixed WAV (not per-client tracks), since the pipeline assumes that.
- Tune the activity threshold (N channels, M polls) to avoid false triggers from one person noodling alone. Two or more active channels sustained for ~30s is a reasonable starting point.

**Implementation order:**
1. Test `getRecorderStatus` + `startRecording` / `stopRecording` RPC calls on one fleet server.
2. Instrument `AudioHighlightService` with the channel-level trigger; log what it would have done for a week before actually calling startRecording.
3. Write `highlight-extractor.py`, test on a real captured window.
4. Wire into `DailyEssayService`. Ship to one server, watch for reader engagement before expanding.

## JamFan22 Repo Split

Split `~/JamFan22/` into two side-by-side git repos on `jamulus.live`:

- **`~/JamFan22/`** — ASP.NET web app: radar, essay, welcome system, API, UI
- **`~/jamfan-ops/`** — ops tooling: fleet scripts, StreamGate docs, cron jobs, analysis tools

No new process or binary. Both repos stay on the same host. Scripts in `jamfan-ops` read `~/JamFan22/JamFan22/data/` by absolute path.

### What moves to `~/jamfan-ops/`

**Scripts** (currently in `~/JamFan22/`):
- `fleet-probe.py`, `fleet-server-stats.py`, `probe-1028.py` — fleet diagnostics
- `prospect-radar.py`, `user-awareness.py`, `traffic.py` — analytics tools
- `recurring-slots.py` — lore automation (not yet production)
- `cull-data.sh`, `gcdump-monitor.sh` — maintenance crons
- `make-paste-for-gemini.py`, `snapshot-prompts.sh`, `gemini-paste.txt`, `GEMINI.md` — prompt engineering tools
- `crawl.py` — admin/ops
- `band-finder.py`, `predict-future.py`, `jammer-map.py` — cron data writers (output stays in `data/`, scripts move)

**Docs**: `FLEET-ANALYSIS.md`; sections of `JamFan22/CLAUDE.md`: StreamGate architecture, NonFleetSilencePoller design, fleet JSON-RPC spec, cron schedule, gjprobe docs, audio highlights pipeline.

### What stays in `~/JamFan22/`

ASP.NET project (`JamFan22/`), `deploy-test-build.sh`, `jamfan-cli.sh`, `SCHEMA.md`, `RUNBOOK.md`, `WELCOME-NARRATOR.md`, `tail-welcomes.py`.

### Steps

1. `git init ~/jamfan-ops`
2. `mv` the scripts above; `git add` in new repo
3. Write `~/jamfan-ops/CLAUDE.md` with ops sections extracted from `~/JamFan22/CLAUDE.md`
4. Edit `~/JamFan22/CLAUDE.md`: remove extracted sections, add pointer to `~/jamfan-ops/CLAUDE.md`
5. Add `jamfan-ops` row to satellite table in `/root/CLAUDE.md`
6. Update cron entries that reference moved scripts to use new absolute paths

### CLAUDE.md boundary

| Section | Stays in JamFan22 | Moves to jamfan-ops |
|---------|------------------|--------------------|
| Build & Run, Testing, Architecture, Service Layer | ✓ | |
| LLM Welcome, Daily Essay, Active Only, Band Naming, InferredRegion | ✓ | |
| StreamGate design | | ✓ |
| NonFleetSilencePoller / Ear design | | ✓ |
| Fleet JSON-RPC spec | | ✓ |
| Audio Highlights pipeline | | ✓ |
| Cron schedule | | ✓ |
| gjprobe docs | | ✓ |

## Band Naming (BandIndex / bands.json)

- **Drop emoji-derived names entirely.** Emoji-prefix bands (💎, 🍷, 🕷, etc.) are self-labeling — the shared emoji IS their identity. Adding a human-readable gloss ("Wine Glass Crew", "Diamond Crew") adds nothing and may conflict with names they've chosen themselves.

- **Only the strongest non-emoji bands earn names.** Criteria: core duo/trio with a long shared history, clear geography, distinctive instruments, and/or song history that suggests a character. If none of those click into a genuinely descriptive label, leave `name` empty.

- **Name sources (in order of richness):** member first names or handles (e.g., "Bodil & Peter"), geography ("the Cascadia duo"), instruments ("the two bassists from Lyon"), song/URL history if distinctive. LLM-generated names are fine — pass member names, nation codes, server history, and any URL-derived song titles; let it propose a label rather than human-assigning one.

- **Names are rare shorthand, not primary identity.** The system is far more likely to mention members by name than to use the band label. A name appears occasionally in essays or welcome context as a conversational shortcut — never as the main way to refer to the group.

- **Suppress "assembling" canary for emoji-prefix bands when ≥2 members present.** The shared emoji already signals assembly visually; the "missing members" widget is redundant and clutters the card. Keep canary logic for non-emoji bands where the connection isn't obvious from names alone.

- **Soon: prediction TTL — expire after likely arrival window** (`BandIndex.cs`, `Api.cshtml.cs`): once a canary trigger fires, the "Soon:" orange line should expire automatically rather than persisting indefinitely. Design: record the minute the canary first fired (`BandSoonResult.TriggeredAtMinute`). Estimate the expected arrival window from `predict-future.py` data — if the predicted regular typically arrives within X minutes of their canary, use that as the TTL (e.g., 30–45 min). If no prediction data exists for the missing member, fall back to a fixed TTL (suggest 60 min). When `nowMinutes - triggeredAtMinute > ttl`, suppress the Soon line from the card. Log `[BAND-SOON] expired: {bandId} triggered={T} now={N}`. The trigger minute should be tracked in a static `ConcurrentDictionary<string, int>` in `BandIndex` keyed by `{bandId}:{serverKey}`, cleared when the predicted member actually arrives or the canary members depart.

- **Band canary hint in welcome messages** (`WelcomeContext.cs`, `Api.cshtml.cs`): when a player arrives at a server where a band canary is already present, pass that context to the welcome LLM so it can naturally mention the possibility — conversationally, not as a promise. The server card stays "Soon:" only; soft hedged language belongs in prose.

  **How it works:** `BandIndex.GetBandSoon` already runs per-server in `Api.cshtml.cs`. At welcome time (in the `/ip-allowed` path), call it again for the arriving player's server and, if it fires, include a hint in `WelcomeContext`: which canary member is present and which bandmates are typically expected. The LLM can then say something like "Z is already here — the rest of the crew often turns up." No bold claim, just conversational awareness.

  **Reliability signal:** `solo_trigger_rate` in `bands.json` measures what fraction of canary appearances lead to a full band session. Pass this value alongside the hint so the LLM can calibrate — low rate → "sometimes the others follow", high rate → more confident phrasing. The rate is already in the JSON; `BandIndex.cs` just needs to read it and expose it on `BandSoonResult` (add `double TriggerRate` to `BandMember`, `double CanaryTriggerRate` to `BandSoonResult`).

  **Out of Order specifically:** members are RustyShackleford, Z, sometimes Daniel, and Gentle Bunny (who plays under many names — GUID-matched, not name-matched). Not yet in bands.json; GUID-based clustering in `band-finder.py` will detect them once enough co-sessions accumulate in census.csv. Gentle Bunny's name-changing is fine — the clustering is GUID-based. When detected, their trigger rate will naturally govern how confident the welcome message sounds.

  **Changes required:**
  1. `BandIndex.cs` — add `double TriggerRate` to `BandMember`; read `solo_trigger_rate` from JSON; add `double CanaryTriggerRate` to `BandSoonResult`.
  2. `WelcomeContext.GatherAsync` — call `BandIndex.GetBandSoon` for the arriving player's server; if it fires, add a context section: `Band canary present: {canaryName} (usual crew: {missing}). Typical assembly rate: {rate:P0}.`
  3. `data/welcome-system-prompt.txt` — add guidance: when a band canary hint is present, weave it in as light anticipation, not a guarantee. Low rate → "sometimes"; high rate → more confident. Never say "Soon" verbatim.

## Band Fleet Invites — special-feature welcome for detected bands

Vision (2026-07-28): when we detect a player is part of a recognized band (`bands.json`, same clique detection as canary/lore), give them a one-off special-case welcome message inviting them to use the fleet for something a public server can't offer. Throttle to at most once per band per week — this is a pitch, not a nag.

Candidate features to invite them into (not yet designed/built):
- **Advance notice** — post "starting soon" publicly ~1 hour before a predicted session, piggybacking on the existing `[BAND-SOON]` canary/`predict-future.py` prediction machinery.
- **Off-Jamulus listen-in** — already exists: `StreamGate.cs` runs the lounge at `ear.jamulus.live`. This would just be pointing bands at a feature that's already live, not building new infra.
- **MP3 recording** — the lounge announcement string already says "Hear and record this jam" (`StreamGate.cs:359`); unclear whether recording is actually implemented end-to-end or just aspirational copy. Needs verification before promising it to anyone.
- **Persistent "your usual slot" link** — one stable URL for a band's regulars/fans instead of hunting for whichever fleet server is up that week.
- Others TBD.

Message content refinement (2026-07-28): the invite should name the player's other bandmates ("come center your sessions with X and Y here") — pulled straight from the `bands.json` member list already available via `BandIndex.FindBandForGuid`. But only pitch this on servers that will actually be there next time: **fixed-hour or 24h fleet servers only**, never the demand-scored dormant pool. Recommending an unpredictable server undermines the pitch. Classification uses data already on hand — no new fields needed:
- Fixed-hour: entries in `dormant-instances.json` with a `"schedule"` field (currently Paris, Milan).
- 24h: fleet servers *not* present in `dormant-instances.json` at all (lounge, harvest-pings, and the other always-on relays in `fleet-server-ips.txt`).
- Excluded: the remaining demand-scored dormant instances (most of the fleet) — up/down unpredictably, so never recommended as a destination.

Tradeoff: shrinks the eligible destination pool to a handful of servers, so most bands' invites funnel toward the same few boxes rather than their nearest fleet instance. Acceptable for a first version; revisit once there are more scheduled/24h boxes.

Reusable pieces already in the codebase: `BandIndex.cs` (band/member detection, canary), `StreamRequestManager.cs` (`IsWeekly` flag) and `StreamGate.cs` (`WeeklyReservation`: day/hour/duration) already model a weekly cadence — the "once a week" throttle for invites could piggyback on that pattern instead of inventing a new one. `WelcomeMessageGenerator.cs` / `WelcomeContext.cs` are the natural injection point for the special-case message itself.

Shipped (2026-07-28): detection + throttle, logging-only. `BandIndex.FindBandForGuid` looks up band membership; `BandInviteTracker` (new file) throttles to 1x/week per band via `data/band-invite-log.json`; hooked into `WelcomeContext.GatherAsync` right after `arrivingGuid`/`nowMinutes` are known — logs `[BAND-INVITE-ELIGIBLE] band_id=X band_name=Y guid=Z`, no change to what the player sees yet. Verified against band 3 (KP/VKP) via `/debug/welcome-preview` on the debug build: fires once, throttles on immediate repeat, silent for non-band guids.

Next step: pick a lead feature (advance notice, listen-in pointer, or recording — see above) and wire real copy into the welcome LLM using this same eligibility signal.

## Band Lore Paragraphs

Shipped: `band-finder.py` emits `first_session_date`, `session_count`, `span_weeks`, `core_stability`, `url_samples` per band. `BandLoreService.cs` generates per-language paragraphs on demand, 7-day disk cache, daily cap 10. `BandIndex.cs` sets `HasLore` on `BandSoonResult`. `/api/band-lore?id=X` endpoint live. Client staggered eye reveals (20s→40s→80s→120s cap, DOM order), popup with jamulus.live server map link. Prompt at `data/band-lore-prompt.txt`. Server/home-server mentions removed from context and prompt (2026-07-15); stale pre-fix cache entries site-wide purged and prompt now requires naming all members of 3+ groups instead of narrowing to the pair with pairwise-hours data (2026-07-16).

---

## HiBot Non-Fleet Welcome (`POST /hibot/arrival`)

Design spec for when HiBot (the operator's Jamulus client) is connected to a non-fleet server. HiBot broadcasts whatever this endpoint returns via `CreateChatTextMes` — it goes to the whole room, not a single client.

**Auth:** `X-HiBot-Secret` header validated against `data/hibot-secret.txt`. Return 401 if missing or wrong.

**Request body:** `{guid: string, serverAddr: string, countryVotes: [int]}`
- `guid` — arriving player's GUID (MD5 of name+country+instrument, same as fleet)
- `serverAddr` — address the operator typed when connecting (hostname or IP, possibly with port)
- `countryVotes` — non-zero QLocale country code ints from all connected clients (operator excluded by keeping their flag blank)

**Language detection:** map `countryVotes` ints to languages via `_countryLanguage`; geolocate the `serverAddr` IP for 1 additional vote; plurality wins.

**Name lookup:** scan `data/censusgeo.csv` for rows where col 0 == guid; last matching line wins; col 1 is URL-encoded name — decode it. Fall back to a nameless greeting if not found.

**LLM:** Gemini 2.5 Flash. System prompt: `data/hibot-welcome-system-prompt.txt` (new file, separate from fleet prompt). Key constraint: message is **public broadcast**, not private — frame as the group welcoming a newcomer, not the server addressing an individual. 1-2 sentences, HTML ok.

**Response:** plain text (the HTML greeting). HiBot calls `CreateChatTextMes()` with it.

**Do not touch** the fleet welcome path (`WelcomeContext.cs`, `WelcomeMessageGenerator.cs`, `/ip-allowed`). This is a separate code path.
