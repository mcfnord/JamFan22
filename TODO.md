# TODO

## UI / Client

- **Server card directory nudges: language matching** (`Pages/Client.cshtml`, `nearby.cs`): nudges that point a player toward a server's directory (e.g. "Join at jamulus.io") should try to match the language of their Jamulus client installation. Inference order: (1) geolocation of the browser IP → map country to dominant language; (2) if a GUID is matched to that IP, the country code stored with the GUID (Jamulus client's self-reported country) may confirm or refine the choice. No guarantee of correctness — treat it as a best guess, defaulting to English when ambiguous.

- **Visual regression detector** (`playwright`): headless browser script that loads the app, polls `page.evaluate` every ~1s to snapshot all `.server-card` bounding boxes, and reports frames where any card position jumps >20px or a card appears/disappears without an intermediate opacity transition. Useful for catching silence-gate snaps and Active Only filter layout shifts. Trigger: run after any change to card visibility logic in `Client.cshtml` or `site.css`.

- **Silence emoji fade-in / fade-out animation** (`Pages/Client.cshtml`): give the 🔇 emoji a slow, deliberate entrance and exit to make silence state transitions feel intentional rather than abrupt. **On first quiet sample:** instead of the emoji snapping onto the card, fade it in slowly (e.g., `opacity 1.5s ease-in`). **On second quiet sample (Active Only):** the card disappears via the ghost mechanism as today. **On quiet-in (server returns):** after the fresh card is built and placed at the ghost's slot, immediately show the 🔇 emoji on the new card, then fade it out with the same duration as the fade-in (so the silence emoji appears and dissolves as a brief "was quiet" signal). The fade-out should complete before or alongside the entrance animation so the card arrives looking active. Implementation: add a `.silence-emoji-fading` CSS class with `opacity: 0; transition: opacity 1.5s ease-in-out` and toggle it on the emoji span element in the quiet-out and quiet-in paths.

- **Fading easter egg text**: Some UI element (TBD) starts fully legible when the product is fresh/new, then gradually fades over time until it's nearly unreadable. The fade is a function of how old the installation or user session is — an ambient signal of age baked into the typography itself.

- **Active Only: show new arrivals briefly regardless of occupancy** (`Pages/Client.cshtml`): when Active Only is checked, a server with a new arrival (singleton or new duo) should still appear even if the server is otherwise silent. The visibility window should match however long the "just arrived" message is displayed — that duration is the natural TTL for a new-arrival card under Active Only. Once the arrival message expires, the server card follows the normal Active Only suppression logic.

- **fetchAndRender at 15s** (`Pages/Client.cshtml:2771`): changed from 20s. Consider dropping to 10s once 15s has been in production without complaint. Diminishing returns below 10s since the server-side poll loop is ~5s.

- **"Any Genre Asia" label** (`Pages/Client.cshtml:727`): `renderServerHeader` strips `"Genre "` but alt-source returns `"Any Genre Asia"` → shows `"Any Asia"`. Fix: `cat = cat.replace("Any Genre Asia", "Any 3/Asia").replace("Genre ", "").replace(" ", "&nbsp;");`

- **Nearby Only: global peek strip** (`Pages/Client.cshtml`, `wwwroot/css/site.css`): when Nearby Only is active, render a compact strip just above the Nearby Only checkbox showing the hidden (distant) servers as tiny colored pills — same color scheme and sequence as the full cards, showing only a people-count badge. On hover, the pill expands into a full-size ghost of the server card so the user can read it without leaving Nearby Only mode. The strip is invisible when Nearby Only is off. Implementation sketch: collect suppressed server elements in a separate array during `processDiff`; build pills with `background-color` copied from the card's category color; CSS `:hover` transition `width`/`height` to expand; position pills in a `flex-wrap` row inside a `#global-peek-strip` div inserted before the checkbox container.

- **Mobile: PWA install prompt** (`Pages/Shared/_Layout.cshtml`): intercept `beforeinstallprompt`, show banner on second visit. **Do not implement until Web Push is working.**

- **Mobile: haptic feedback on tracked arrival** (`Pages/Client.cshtml`): `navigator.vibrate([200, 100, 200])` when starred player arrives in foreground. Opt-in checkbox. Implement after title-bar alert is shipped.

- **Mobile: Web Push notifications** (`Pages/Client.cshtml`, `Program.cs`, service worker): VAPID keys → `data/`; `wwwroot/sw.js`; push subscription store `data/push-subscriptions.json`; fan-out in polling loop. Prerequisite: PWA install prompt.

## LLM Welcome

- **"First time here!" on dormant fleet rejoin** (`CensusIndex.GetGuidOnServer`): dormant fleet servers change IP on every restart; `GetGuidOnServer` is keyed by `ip:port`, so all prior visit history is silently lost on each IP change. Fix: use `WelcomeCache` (or a separate short-TTL cache) to remember `(guid, serverName)` pairs from recent welcomes — if a match is found, suppress "first time here!" and inject a short rejoin hint. Static fleet servers (stable IPs) are unaffected.

- **"Back to your top server" after one prior visit** (`WelcomeContext.cs` history signals; seen 2026-07-10, `history:0h,top` 21 min after a "first time here!"): the `top` flag needs a minimum visit count or history depth, or softer phrasing when history is thin.

- **Bimodal welcome LLM latency** (`WelcomeMessageGenerator.cs`; seen 2026-07-10): generation times clustered at ~2–4 s or ~22 s. A tight 22 s cluster suggests a timeout-and-retry, but the file sets no explicit timeout (checked 2026-09-29). Measure again from the welcome log before instrumenting anything; if the cluster is gone, drop this item.

- **India welcome-language mismatch** (`Program.cs:1239`, `WelcomeContext.cs:148,178`; still three-way inconsistent 2026-09-29): three tables disagree — static header table says Hindi (`["IN"]="hi"`), LLM body-language table says English (`["IN"]="English"`), name-based table says Hindi (`["India"]="Hindi"`). An Indian player can get a Hindi "You've joined" header on an English body. One-line fix either direction; product call which language wins (lean English — Indian Jamulus scene skews English-speaking).

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

**Data (2026-09-29):** `url-guids.csv` has 481 rows (86 in June), all from Thai servers so far. The
June diagnostic found **44 qualifying groups** (≥6 songs, ≥2 sources); top groups had 38 songs from
4–8 GUID sources. All items are `[text]` (chords69cl room URLs) until chordtabs.in.th links
accumulate. Titles are URL-encoded; `HttpUtility.UrlDecode` before rendering.

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
- [x] **Thai fleet server** — dormant TH instances exist (jamfan-command `fleet.json`), and the
  passive URL harvest they enable is running: `url-guids.csv` grew 86 → 481 rows June → September.

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

**Status 2026-09-29:** nothing built — no `PROMPT_SEASON` in `ops/long-essay.py`, no digest files. The long-essay experiments in `wwwroot/essays/` (`essay-long-en-*.html`, `essay-profile-*.html`, `essay-songs-en.html`) are the prior art.

**Implementation order:**
1. Write `ops/recent-digest.py` — emit `data/recent-digest.json` (30-day) from census data. No LLM. Inspect the output before wiring anywhere.
2. Wire `recent-digest.json` into welcome context (`WelcomeContext.GatherAsync`). Observe welcome message quality.
3. Write `ops/season-digest.py` — emit `data/season-digest.json` (95-day). Wire into `DailyEssayService.BuildContext`. Observe essay quality.
4. Add `PROMPT_SEASON` to `long-essay.py`; add Thai mode; generate `season-en.html` and `season-th.html`.
5. Add localhost-only routes; monitor essay quality over a few iterations.
6. Add Thai-first + English links at bottom of daily essay once quality confirmed.

## Daily Essay

- **Nameless-sentence gate — LIVE since the 2026-09-28 06:09Z restart** (operator 2026-09-27: "let's kill these weak sentences"). `DailyEssayService.DropNamelessSentences()` runs after `StripUnauthorizedLinks()` and drops every sentence with no `<b>/<strong>` name, no `<i>/<em>` room, no markdown `**bold**` and no quoted title (the quote test runs on tag-stripped text, or `datetime="…"` would rescue the sentence). Empty paragraphs go; an essay left empty is returned untouched. Before it landed, 75 of 584 sentences in 40 essays (12%) were this filler ("Glasgow was still awake."). Observable: `[ESSAY] nameless-gate kept=N cut=M key=<center>:<lang>` in `output.log` — 46 lines by 2026-09-29 (e.g. `kept=14 cut=1 key=EU-W:Swedish`). Check from central command: `curl -k -s https://localhost/api/nearby-essay -H "X-Forwarded-For: <ip>" | python3 /root/jamfan-command/essay-gate-check.py` (exit 1 and a printed sentence = gate not running). **Known cost:** an abbreviation inside a named sentence ("St. Louis") splits it and the tail half is cut. Rollback: `DailyEssayService.cs.bak-namelessgate-20260927` + restart.

- **No names, no mention; bots are not people, not even as numbers — LIVE since 2026-09-28** (operator 2026-09-24: "If there are no names involved, NOTHING IS WORTH MENTIONING"). Standing rules, all in `DailyEssayService.cs`:
  1. `IsValidSession` requires `Players.Count > 0` — a nameless room never reaches the model (before: 322 of 5,765 session entries, 5.6%, and the model padded them from the lore).
  2. Bot GUIDs are stripped from `GuidTicks` in `GenerateAsync` before any count, span, rank or geolocation; a room left empty is dropped (before: Studio D's "stayed warm most of the day" was `lobby [0]`'s 1440 silent minutes; Cutlove read 11 players for 1 human + 10 `cap tester`s).
  3. Bots are matched by NAME (`s_botName`: lobby, Muh, gjstress, jamulus-lounge, Listener, soakbot, test bot, cap tester) because the lounge client roams the fleet. **This regex and `fleet-value.py` `BOT_RE` in jamfan-command must stay identical** (mistake 16's shape); a new bot name goes into both.
  4. An UNNAMED GUID whose span is ≥ 1296 min (`UnnamedAllDayMinutes`, 90% of the window) is a bot (Andre's US Sound's blank-name Streamer). Span, not distinct minutes: an unnamed human seen at both ends of the day is dropped too — that costs a number, never a name.

  **Still to prove live** (jamfan-command FOLLOW-UP 574): `tail -c <bytes since restart> data/essay-llm.log | python3 /root/jamfan-command/essay-nameless.py` must count 0 nameless sessions, and no new context may show `~most of the day` for a room whose named players spanned under an hour. Rollback: `.bak-stripbots-20260924` (bots only) or `.bak-namedonly-20260924` (both).

- **Songs reach the essay without their room — the model invents one (Defect C; agreed 2026-09-13; NOT STARTED as of 2026-09-29; do it next).** `BuildContext` lists `songs.Take(10)` from ALL of `urls.csv`'s last 24 h (`DailyEssayService.cs:868`), not just songs from the rooms in the essay. The prompt makes song mentions mandatory (`essay-system-prompt.txt:15`), so a song whose room is not in the context is attached to some room that is. On 2026-09-12 the operator's own songs from Rick's Raw Audio CHI (a room that blocks the sampler, so it is absent from census) were printed on two different wrong rooms across five runs. **The 2026-09-24 filters make this more frequent:** every room dropped as nameless or bot-only leaves its songs orphaned in the list.
  1. **Filter songs to the essay's rooms.** Build `sessionKeys` = `.Key` of `local` ∪ `global`, keep only songs whose `ServerKey` is in it, BEFORE `.Take(10)` (so orphans don't use up slots). Delete the `: key` fallback at `:870`: with the filter in place it is unreachable, and leaving it is how this comes back.
  2. **Fix the meta window in the same change ("both or neither").** `LoadServerMetaAsync` (`:664`) reads `ReadTailAsync("data/server.csv", 8_000_000)` — BYTES. Measured 2026-09-29: 8 MB = **16.2 h** of a 182 MB file (19.4 h on 09-24, 3.6 h on 09-12 — the window shrinks as the file grows). Short of 24 h, so a room seen only early in the window gets its raw `ip:port` as its heading. The durable fix is the `server.csv` prune under **Data / Infrastructure** (33,673 distinct keys; the whole pruned file fits in the window); until then 16 MB, or build the map only for the session keys.
  3. **Build, observe on :5000, do not restart production** (JamFan22 C# bar). Tell the operator what to check.

  **Done when** a generated context contains no song whose key is outside its session list, and no line matches `^[0-9.]+:[0-9]+ \(` (a raw `ip:port` heading). Still open after this ships: a room that blocks the sampler stays invisible and its songs are dropped rather than credited (jamfan-command `FLEET-SAMPLING-GAP.md`).

- **Two-distinct-IP generation gate — a solo visitor in a quiet region still gets no essay** (`DailyEssayService.cs:256-261`, `Program.cs` `/api/nearby-essay`). Shipped 2026-07-16: polling while the essay is not ready no longer costs quota (`_essayRateLimit` is incremented only after non-null html), and the 40/day `s_onDemandToday` cap (`[ESSAY] daily-cap-hit`) is the abuse guard. Still open: `GetEssayHtmlAsync` calls the LLM only after a SECOND, different IP asks for the same `region:language` key, so a lone visitor's own repeats never trigger generation. Measured 2026-09-29 over the last 300 MB of `output.log`: 1,905 `same IP as first, not triggering` lines and 1,411 `[ESSAY] rate-limit` lines. Candidate fix: trigger solo after a longer wait (e.g. 90 s), trading a small LLM-cost risk for serving those visitors; pair with **Client-side trigger redesign** below. **Done when** two spoofed requests from one IP in an otherwise idle region (`curl -k -s https://localhost/api/nearby-essay -H "X-Forwarded-For: <ip>"`, 2 min apart) return html on the second.

- **Web-user GUID personalization** (`DailyEssayService.cs`, new helper `WebUserIndex.cs`): match web-app browser IPs to player GUIDs via join-events.csv, then shape the shared essay around what those readers care about most. Not started (2026-09-29: no `WebUserIndex.cs`, no KNOWN READERS block in the context). Re-measure the IP→GUID hit rate first; it was 5% (5/110 Tier1 IPs) in June, before join-events.csv had accumulated.

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

- **"Hey, you're in this one!" banner — SHIPPED 2026-08-10** (`DailyEssayService.TryGetVisitorBanner`, prepended in `/api/nearby-essay`): visitor IP → GUIDs via `IdentityManager.GetGuidStrengths` (floor ≥16) → intersect with the essay's featured GUID→name map captured at generation → confirm the name survived into the prose → inline callout. Every attempt logs `[ESSAY-YOU] eval … result=<no-essay|no-guids|below-floor|no-mention-match|name-not-in-prose|shown>`; shown firings also go to `data/essay-you.log` (git-ignored — names and IPs) with `SYNTH-ONLY`/`MULTI-IP`/`MULTI-QUALIFYING` flags. **Owner decision 2026-08-10: false positives are acceptable — do NOT tighten** (no `golden ≥16` floor, no `guidIPs` ceiling; the flags are ledger visibility, not gates). Success rate = `grep -c "result=shown"` ÷ `grep -c "\[ESSAY-YOU\] eval"`.

  **Co-jammer near-miss line**: if the reader's GUID was active in the last 24h (appears in census.csv for today) AND one or more of their top-3 co-jammers was also active but on a different server, append a short italic line after the essay's closing `<p>`: `<p class="essay-nearmiss"><i>Jonas was on Freiheit last night while you were on CBVB.</i></p>`. Derived entirely from census.csv + timeTogether — no LLM. Skip if they were on the same server.

  **Crew summary line**: if the reader was NOT active last 24h but their top co-jammers were, append: `<p class="essay-crew"><i>Your people last night: Jonas on <i>Freiheit</i>, Felix on <i>Studio D</i>.</i></p>`. Server resolves crew names + server names from census.csv. No LLM. Shows even when the reader has no personal session to anchor to.

- **Essay time-fixation** (`data/essay-system-prompt.txt`, `DailyEssayService.BuildContext`): essays over-narrate start/end/duration because every session carries three timing fields (local, UTC, approx duration) and ~25 of the prompt's ~90 WRONG/RIGHT pairs are about time — a prohibition still anchors attention. Step 1 (2026-07-15, prompt only, live without restart): at most 1 in 3 sessions may carry time language, one time-opened paragraph per essay. On the shelf unless recent essays (`data/essay-llm.log`) still read as timelines: step 2, consolidate the ~25 time pairs to 4–5; step 3, full timing for the headline session only, a day-part word for the rest. The nameless-sentence gate above now removes the worst of these lines after the fact.

## Geolocation / Geo-diag

- **region-centroids.json**: canonical lat/lon per `{countryCode}:{regionName}` from GeoNames/Natural Earth, to replace ip-api datacenter-IP coordinates.

- **InferredRegion open concerns**: `country-adjacency.json` not yet wired (2026-09-29: no `.cs` file reads it); `fleet(1d)` should also apply ≤1 override.

- **InferredRegion: nightly cull cron + /login enhancement**: prune stale server-region cache; pre-filter by visitor IP.

## Data / Infrastructure

- **`/chat-url-client`: remove join-events GUID resolution** (`Program.cs`): the modified client always supplies `serverAddr`, so the `IdentityManager.GetGuidStrengths` lookup is dead weight in this path — it can never know the server better than the client does. Remove the GUID-resolution block (`Program.cs:641-661`, `GetGuidStrengths` → `bestGuid`) and the `bestGuid`/`bestStrength` variables; keep only the `req.serverAddr` validity check and the identity gate (`guidStrengths.Count == 0 && FleetGuidCache…`). The identity gate itself may also be removable if the fleet binary is the only caller — audit before dropping.

- **`server.csv` grows without bound — `trim_server.sh` no longer prunes it** (`data/server.csv`, `data/trim_server.sh`, cron `0 6 * * 0`). Schema since 2026-06-24: `ip:port,name,city,country,minute`; readers of cols 0–3 are unaffected; `server-lore.json` `name` stays the authoritative override for stale names. The weekly trim is `sort | uniq`, which drops only byte-identical rows — and since col 4 exists, one server's rows differ by the minute. Measured 2026-09-29: **182 MB, 2,810,854 rows, 2,371,367 distinct, 33,673 distinct `ip:port`, 35,918 old-format rows (1.3%)**; it was 45 MB in June. The essay's shrinking meta window (Defect C step 2, above) is a symptom.
  1. `grep -rl server.csv JamFan22/*.cs JamFan22/Services JamFan22/Pages ops/` — list every reader and confirm each needs only the newest row per key (the essay's `LoadServerMetaAsync` and the alt-name map do).
  2. Prune: keep, per `ip:port`, the row with the highest `minute`; drop rows with no minute (the old "wait until mostly minute-stamped" caution is met at 1.3%). Expected ≈ 34k rows, a few MB.
  3. Run once by hand (backup first), then make it the body of `trim_server.sh`, keeping the move-aside-then-append shape so the writer never loses a row.

  **Done when** the file is under 5 MB after a trim, `/api` server names are unchanged for a sample of ten keys, and the next essay context has no raw `ip:port` heading.

## Fleet / Infrastructure

Fleet hosts, expansion (Chile etc.) and deploys are decided in `/root/jamfan-command` (`oracle-expansion.py`, `deploy.sh`, `dormant-monitor.py`'s `post_wake_deploy()`), not in this repo. Closed here 2026-09-29: the outbound WebSocket welcome channel (both sides shipped 2026-07-03/04; Trio connected on it 2026-09-28 with build 3.12.5-JAMFAN-30, so its welcomes are unblocked); ChatReporter on dormants (installed on every wake by `post_wake_deploy()`); the Paris jazz room (New Morning, 22225/9998, exists); the hole-punch in-cycle retry (`harvest.cs:514`, live); the TH/HK "destiny" rename (the essay feature it served expired 2026-08-17 — the naming plan is in this file at commit `ef0cb38` if it is ever revived).

- **Remove the TCP welcome fallback once nothing needs it.** `Program.cs`'s welcome paths are WebSocket-only (`FleetWsRpcCallAsync`). `TcpClient` still appears in `WelcomeContext.cs` and `Services/StreamGate.cs` — read each use before deleting; a room that reaches JamFan22 only over inbound TCP would lose welcomes. **Check first:** every running fleet room has a `[fleet-rpc-channel] … connected key=<ip:port>` line since its last start (`grep -a "fleet-rpc-channel.*connected" output.log | tail -40` against `data/fleet-server-ips.txt`).

- **`/debug/fleet-rpc` — ask a connected room its own name over the WS channel** (proposed 2026-07-08, owner undecided). ~15 lines next to `/debug/fleet-levels`, loopback-only, `?key=IP:PORT&method=jamulusserver/getServerProfile` through `FleetWsRpcCallAsync`. Rationale: a room whose RPC port the cloud firewall blocks is reachable only through the socket it holds open to us. Cost: a production code path for a one-off lookup; the alternative is SSH to the host. The related stranded-22226 cleanup is done (every 22226 line in `fleet-server-ips.txt` sits on a current host; `dormant-instances.json` carries the port).

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

**Prerequisite:** operator's current home/VPN IPs collected and written to `data/client-level-reporter-ips.txt` before enabling. Start with this file and the 403 gate before wiring any C++ timer. **Status 2026-09-29:** neither the file nor the endpoint exists; nothing built.

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

  **M4 — Essay filtering** *(the payoff; not started — 2026-09-29: no such log line in `DailyEssayService.cs`)*
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

## Ops tooling layout

The June plan to split ops scripts into a second repo (`~/jamfan-ops`) was done differently: they live in `ops/` inside this repo with their own `ops/CLAUDE.md` (cron schedule, log markers, traffic tiers). Left from that plan: `JamFan22/CLAUDE.md` (396 lines) still carries the StreamGate, NonFleetSilencePoller/Ear, fleet JSON-RPC and gjprobe sections that belong in `ops/CLAUDE.md`. Move each when its subsystem is next touched, leaving a one-line pointer.

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

Vision (2026-07-28): when a player belongs to a recognized band (`bands.json`, the same clique detection as canary/lore), give them a one-off welcome inviting them to use the fleet for something a public server can't offer; at most once per band per week — a pitch, not a nag. Name their bandmates ("come center your sessions with X and Y here", from `BandIndex.FindBandForGuid`), and pitch only servers that will be there next time: **fixed-hour (a `schedule` in `dormant-instances.json`) or 24h fleet servers, never the demand-scored dormant pool** — recommending an unpredictable server undermines the pitch. That funnels most bands toward a few boxes; acceptable for v1.

**Shipped 2026-07-28, logging only:** `BandInviteTracker` throttles to 1×/week per band via `data/band-invite-log.json` (41 bands recorded by 2026-09-29) and logs `[BAND-INVITE-ELIGIBLE] band_id=X band_name=Y guid=Z` from `WelcomeContext.GatherAsync`; the player sees nothing yet (1 eligible event in the last 300 MB of `output.log`).

**Next:** pick the lead feature and wire real copy into the welcome LLM on this signal — advance notice (~1 h before a predicted session, off the `[BAND-SOON]`/`predict-future.py` machinery), the listen-in pointer (`ear.jamulus.live`, already live), a persistent "your usual slot" link, or MP3 recording (the lounge string already says "Hear and record this jam", `StreamGate.cs:359` — verify recording exists end-to-end before promising it). `StreamRequestManager.IsWeekly` / `StreamGate.WeeklyReservation` already model a weekly cadence for the throttle to reuse.

## Band Lore Paragraphs

Shipped: `band-finder.py` emits `first_session_date`, `session_count`, `span_weeks`, `core_stability`, `url_samples` per band. `BandLoreService.cs` generates per-language paragraphs on demand, 7-day disk cache, daily cap 10. `BandIndex.cs` sets `HasLore` on `BandSoonResult`. `/api/band-lore?id=X` endpoint live. Client staggered eye reveals (20s→40s→80s→120s cap, DOM order), popup with jamulus.live server map link. Prompt at `data/band-lore-prompt.txt`. Server/home-server mentions removed from context and prompt (2026-07-15); stale pre-fix cache entries site-wide purged and prompt now requires naming all members of 3+ groups instead of narrowing to the pair with pairwise-hours data (2026-07-16).

---

## Retired designs (full text at commit `ef0cb38`)

- **HiBot non-fleet welcome (`POST /hibot/arrival`)** — retired 2026-08-11 (`data/hibot-secret.txt.retired-20260811`; no route in `Program.cs`).
- **TH/HK "destiny" server renames** — the dormant-essay feature they served expired 2026-08-17.
