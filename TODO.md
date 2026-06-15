# TODO

## UI / Client

- **Nearby list: show nearest player(s) when nothing qualifies within 3000 km** (`nearby.cs`): the 3000 km threshold is calibrated for Europe's dense Jamulus scene. For users in Tacoma (or anywhere with a thin local scene), the list can be nearly empty, then the "≤2 distant records get promoted" rule lets in whoever happens to be in the distant bucket — which can be Bangkok (11,000 km away) rather than a European player (8,000 km). All of these are beyond the threshold; the promotion rule just picks whoever lands in `distantRecords` first, not the closest. Fix: instead of the ≤2 promotion rule, always guarantee at least one result by appending the globally-closest player(s) if the qualifying list is empty, with their distance shown in the location column (e.g. "9,200 km away"). This gives Tacoma users something meaningful rather than a near-empty list or an arbitrary distant pick.

- **Visual regression detector** (`playwright`): headless browser script that loads the app, polls `page.evaluate` every ~1s to snapshot all `.server-card` bounding boxes, and reports frames where any card position jumps >20px or a card appears/disappears without an intermediate opacity transition. Useful for catching silence-gate snaps and Active Only filter layout shifts. Trigger: run after any change to card visibility logic in `Client.cshtml` or `site.css`.

- **Silence emoji fade-in / fade-out animation** (`Pages/Client.cshtml`): give the 🔇 emoji a slow, deliberate entrance and exit to make silence state transitions feel intentional rather than abrupt. **On first quiet sample:** instead of the emoji snapping onto the card, fade it in slowly (e.g., `opacity 1.5s ease-in`). **On second quiet sample (Active Only):** the card disappears via the ghost mechanism as today. **On quiet-in (server returns):** after the fresh card is built and placed at the ghost's slot, immediately show the 🔇 emoji on the new card, then fade it out with the same duration as the fade-in (so the silence emoji appears and dissolves as a brief "was quiet" signal). The fade-out should complete before or alongside the entrance animation so the card arrives looking active. Implementation: add a `.silence-emoji-fading` CSS class with `opacity: 0; transition: opacity 1.5s ease-in-out` and toggle it on the emoji span element in the quiet-out and quiet-in paths.

- **Fading easter egg text**: Some UI element (TBD) starts fully legible when the product is fresh/new, then gradually fades over time until it's nearly unreadable. The fade is a function of how old the installation or user session is — an ambient signal of age baked into the typography itself.

- **Active Only: show new arrivals briefly regardless of occupancy** (`Pages/Client.cshtml`): when Active Only is checked, a server with a new arrival (singleton or new duo) should still appear even if the server is otherwise silent. The visibility window should match however long the "just arrived" message is displayed — that duration is the natural TTL for a new-arrival card under Active Only. Once the arrival message expires, the server card follows the normal Active Only suppression logic.

- **fetchAndRender at 15s** (`Pages/Client.cshtml:2641`): changed from 20s. Consider dropping to 10s once 15s has been in production without complaint. Diminishing returns below 10s since the server-side poll loop is ~5s.

- **"Any Genre Asia" label** (`Pages/Client.cshtml:715`): `renderServerHeader` strips `"Genre "` but alt-source returns `"Any Genre Asia"` → shows `"Any Asia"`. Fix: `cat = cat.replace("Any Genre Asia", "Any 3/Asia").replace("Genre ", "").replace(" ", "&nbsp;");`

- **Nearby Only fallback: expand 50% when zero results** (`Pages/Client.cshtml`, `wwwroot/css/site.css`): zero cards → silently expand 3000km→4500km, show single nearest. Add `checkNearbyFallback()`; call after render, `processDiff`, and checkbox `change`. Also patch `updateServersList` (line 2135).

- **Nearby Only: global peek strip** (`Pages/Client.cshtml`, `wwwroot/css/site.css`): when Nearby Only is active, render a compact strip just above the Nearby Only checkbox showing the hidden (distant) servers as tiny colored pills — same color scheme and sequence as the full cards, showing only a people-count badge. On hover, the pill expands into a full-size ghost of the server card so the user can read it without leaving Nearby Only mode. The strip is invisible when Nearby Only is off. Implementation sketch: collect suppressed server elements in a separate array during `processDiff`; build pills with `background-color` copied from the card's category color; CSS `:hover` transition `width`/`height` to expand; position pills in a `flex-wrap` row inside a `#global-peek-strip` div inserted before the checkbox container.

- **Mobile: PWA install prompt** (`Pages/Shared/_Layout.cshtml`): intercept `beforeinstallprompt`, show banner on second visit. **Do not implement until Web Push is working.**

- **Mobile: haptic feedback on tracked arrival** (`Pages/Client.cshtml`): `navigator.vibrate([200, 100, 200])` when starred player arrives in foreground. Opt-in checkbox. Implement after title-bar alert is shipped.

- **Mobile: Web Push notifications** (`Pages/Client.cshtml`, `Program.cs`, service worker): VAPID keys → `data/`; `wwwroot/sw.js`; push subscription store `data/push-subscriptions.json`; fan-out in polling loop. Prerequisite: PWA install prompt.

## LLM Welcome

- **Session gap + reunion recognition** (`WelcomeContext.cs`, `DailyEssayService.cs`): when a player returns after a significant absence (suggest ≥14 days since last fleet appearance in `fleet-guid-ip.csv`) AND known co-players from their history are currently on the same server, surface the reunion explicitly. Welcome: "You've been away for 6 weeks — Jonas and Felix are both here." Essay: narrative mention when a known regular returns after a gap; the gap duration itself is worth naming ("first time back in months" lands differently than "haven't seen you in two weeks"). Applies to both fleet welcome messages and the daily essay for web app users. Gap duration comes from `fleet-guid-ip.csv` last-seen timestamp per GUID.

- **Returning-player recognition** (`WelcomeContext.cs`, `data/server-lore.json`): `WelcomeContext.cs` already knows `returningPlayer`. For 2nd+ visits the LLM prompt should signal "this person is a familiar face — acknowledge their return and the community they're part of, not generic orientation." A `returning_themes` field in `server-lore.json` (separate from `themes`) lets each server customize this voice without a prompt change. Requires: text edit to `server-lore.json` + small C# addition to pass `returning_themes` when `returningPlayer == true`.


## Daily Essay

- **Per-IP rate limit on `/api/nearby-essay`** (`Program.cs`): add `ConcurrentDictionary<string, (int Count, DateTime Window)>` with same pattern as `/chat-url-client` (line 29). Limit: 5 requests per hour per IP. Return 204 on limit — client already handles 204 gracefully. No need to distinguish cache-hit vs generation at the route layer; just gate all requests.

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

  **"You're in this one" badge**: if the reader's own name (resolved from `m_guidNamePairs[guid]`) appears as a `<b>` tag in the essay, change the `#essay-age-top` badge text from "Written Xmin ago" to "You're in this one · Written Xmin ago". No server state needed — the reader's name is a simple search on the cached HTML.

  **Co-jammer near-miss line**: if the reader's GUID was active in the last 24h (appears in census.csv for today) AND one or more of their top-3 co-jammers was also active but on a different server, append a short italic line after the essay's closing `<p>`: `<p class="essay-nearmiss"><i>Jonas was on Freiheit last night while you were on CBVB.</i></p>`. Derived entirely from census.csv + timeTogether — no LLM. Skip if they were on the same server.

  **Crew summary line**: if the reader was NOT active last 24h but their top co-jammers were, append: `<p class="essay-crew"><i>Your people last night: Jonas on <i>Freiheit</i>, Felix on <i>Studio D</i>.</i></p>`. Server resolves crew names + server names from census.csv. No LLM. Shows even when the reader has no personal session to anchor to.

- **Jammer map hyperlinks in the essay** *(disabled — `DailyEssayService.cs` line ~290 returns empty list)*: once per essay, the LLM may hyperlink a group name to the jammer map. C# builds the URL from `jammer-map.json` GUIDs; post-processor strips unauthorized links. Re-enable when ready: replace the `new List<...>()` stub with the `BuildJammerMapLinks(...)` call.


## Geolocation / Geo-diag

- **region-centroids.json**: canonical lat/lon per `{countryCode}:{regionName}` from GeoNames/Natural Earth, to replace ip-api datacenter-IP coordinates.

- **geo-diag: Fleet/JE IP disagreement categories** (`nearby.cs`): `same-ip`, `/8-agree`, `/8-differ,geo-agree`, `/8-differ,geo-differ`. Add `[JE-FLEET-DIVERGE]` log + `fleet-ranges` indicator.

- **geo-diag: page-level agreement stats**: summary line at top.

- **InferredRegion open concerns**: strength 2–15 gap (threshold ≤1 too conservative); `country-adjacency.json` not yet wired; `fleet(1d)` should also apply ≤1 override.

- **InferredRegion: nightly cull cron + /login enhancement**: prune stale server-region cache; pre-filter by visitor IP.


## Fleet / Infrastructure


- **Self-updating server lore** (`data/server-lore.json`, new script `lore-update.py`): weekly cron job that reads `census.csv` (last 4 weeks) and updates `server-lore.json` automatically — adding emerging patterns, removing dead ones. For each fleet server: detect recurring session windows (≥2 of last 4 matching weekday/hour slots with ≥5 players), compare against current `events[]` entries, add new ones and remove any that have missed ≥2 consecutive scheduled occurrences. Also refresh `themes[]` from instrument/genre distribution if shifted significantly. No manual hand-holding — runs weekly, commits the result or at minimum writes `data/server-lore.json` in-place (app re-reads on next call). Pair with the existing `band-finder.py` cron cadence. The hype must reflect reality.


- **Ear silence sampler** (`NonFleetSilencePoller.cs`): rewrite scheduler with GUID-urgency scoring. Kill switch currently active (`silence-poller-disabled`). Design:

  **Scheduler — score-based server selection (replaces interval table):**
  Pick the highest-scoring eligible server each cycle. Score per server:
  ```
  score(server) = Σ t_unseen(g)  for each GUID g currently on that server
  ```
  `t_unseen(g)` = minutes since GUID g's server was last probed by Ear, OR minutes since g first appeared in `LastReportedList` if never probed. **No never-sampled multiplier** — new GUIDs accrue time like everyone else. This prevents name-changers (new GUID every few minutes) from inflating a server's score; their fresh GUIDs add only a few minutes each, easily outcompeted by hour-old GUIDs on other servers.

  **New data structures:**
  - `_guidFirstSeen: ConcurrentDictionary<string, DateTime>` — set on first appearance, never updated
  - `_guidLastSampled: ConcurrentDictionary<string, DateTime>` — updated for every GUID present when a server is probed
  - Drop `_pollInterval` (the doubling/halving table is replaced by scoring)

  **Hard constraints — applied as filters before scoring picks the winner:**
  1. **Per-GUID Ear-visit floor (20 min, always):** track `_guidLastEarVisit` per GUID. If every GUID on a candidate server was seen by Ear within the last 20 minutes, skip that server entirely. This is a promise: no player ever sees Ear's named connection more than once per 20 minutes, regardless of how high their server scores. Separate from audio-level data (which can come from fleet UDP or lounge sources without this constraint).
  2. **Active-server throttle (15 min):** if the last probe of a server detected audio (not quiet), don't probe it again for at least 15 minutes. Active sessions don't need rapid re-sampling, and the Ear visit is visible.
  3. **Silence-confirm override (4 min, excepted from per-GUID floor):** when a server reports quiet for the first time (first-quiet result), schedule a mandatory re-probe at +4 minutes regardless of score or GUID floor. This confirms the silence before the 🔇 emoji settles and Active Only removal can proceed. This is the one case where the 20-min floor is bypassed — silence confirmation is a server-state check, not a GUID-harvest probe.

  **Cleanup:** when a GUID disappears from `LastReportedList` for more than ~4 hours, remove from `_guidFirstSeen` and `_guidLastSampled`. On re-appearance it starts fresh (as-if new), which is correct — it may be the same player on a new session.

  **Logging** (one line per probe):
  ```
  [EAR] ip:port "Server Name" guids=N score=X quiet=T/F audible=N/M method=ear|1028 next=constraint
  ```
  where `next=constraint` is `active-floor`, `guid-floor`, or `scored` to indicate why this server was chosen and what limits the next visit.

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


- **fleet-guid-ip.csv fallback for prediction geolocation** (`Program.cs`, `FleetGuidCache`): use most recent non-blocked `client_ip` when join-events col 11 is empty.

- **VPN-user geofencing** (`Program.cs`, `FleetGuidCache`): GUID-level VPN allowlist from blocked-IP frequency in fleet-guid-ip.csv.

- **Lounge bot summon command logging**: lounge bot should POST summon requests (timestamp, requester, target server) to a JamFan22 endpoint.

- **Listener-triggered temporary lease** (`StreamGate.cs`, `harvest.cs`): when `lobbyAudience > 0` (someone is actually listening) AND the server has an active, sound-producing group (audible fleet level data or confirmed non-quiet), automatically create a short-duration temporary lease on that server. The lease prevents `StreamGate` from pulling the lounge bot away mid-session, protecting the live audience. Unlike recurring weekly leases, this one expires and does not repeat — it only covers the current session. Design sketch: detect the condition in `ChannelLevelPollLoopAsync` (where both audience count and level data are available); call a new `StreamGate.TryCreateAudienceLease(serverKey, durationMinutes)` that refuses if a lease already exists; log `[LEASE-AUTO] audience={N} on {serverKey} duration={T}min`. Duration suggestion: 30 min, renewable while audience remains.

## Telemetry / Analytics

- **`user-awareness.py`: fix engagement_score()** — current score inflates for bots and long-idle tabs via `total_sec // 60` (dwell time). Reweight to favor deliberate actions: `hover_server`, `scroll_depth`, `click_musician`, `click_more`, `click_listen`, `tab_switch`, `nearby_toggle`, `return_visit`, `tracked_arrival`, `ui_hide`. Cap or discount dwell time alone.


## SignalR / GUID-personalized push (sketch)

ChatHub is live and idle. The interesting opportunity: tag each SignalR connection with a GUID (obtained via login, or inferred from telemetry/census without login — we already have IP→GUID evidence from fleet-guid-ip.csv and join-events), then push targeted events to that tab when something meaningful happens to that specific player.

What "meaningful" could mean — none of these are decided, just possibilities:
- **Your usual crew just arrived** — push when a high-timeTogether pair partner appears on any server, while the viewer is on the radar page but not on that server themselves.
- **Someone who plays with you is here right now** — on page load, check if any current server has a GUID the viewer has 10h+ history with; highlight it immediately, no wait for the next poll.
- **Your server just got a new arrival** — if we know which server the viewer is currently on (from fleet data), push when someone joins, rather than waiting for the 5s poll.
- **Predicted arrival imminent** — predicted.csv says a regular is due in 15 min; push a heads-up.

GUID acquisition without login: IP→GUID from fleet-guid-ip.csv (most recent non-blocked row for the client IP). Confidence is lower but sufficient for non-critical nudges. Login gives certainty.

Open question: what is the actual experience? A toast? A highlighted card that pulses? A sound? The push mechanism is clear; the UX is not. Don't implement until the use case is specific.

Prerequisite for any of this: `OnConnectedAsync`/`OnDisconnectedAsync` overrides in `ChatHub.cs` to register/unregister the connection→GUID mapping in a static `ConcurrentDictionary`.



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

