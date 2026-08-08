# AR Meme Hunt — Setup, Operations & Feature Guide
### Bharatiya Vyapar Mahotsav 2026 · 12–15 August · Bharat Mandapam, New Delhi

The complete "AR Meme Hunt" campaign platform: 5 AR posters, timed scavenger
hunt, live leaderboard, sponsor ads, shareable victory cards. Built on the
existing ARRISE stack — **no Unity C# runtime code was changed**; hunt logic
lives in the WebGL template overlay + a PHP controller.

## Live deployment (as of 8 Aug 2026)

| Piece | URL | Server folder |
|---|---|---|
| Registration / landing | https://dashboard.rionick.com/hunt/ | `public_html/AR_Dashboard/hunt/` |
| AR experience (test build) | https://rionick.com/AR/memehunt/ | `public_html/AR/memehunt/` |
| Public leaderboard + live feed | https://dashboard.rionick.com/hunt/leaderboard.html | — |
| Hunt admin | https://dashboard.rionick.com/hunt/admin.html | — |
| Backend API | https://dashboard.rionick.com/api/controllers/hunt.php | `public_html/AR_Dashboard/api/controllers/` |

QR on the printed posters → the landing URL.

---

## Feature map

### Participant journey
1. **Register** (name + phone + consent) on the landing page → "You're In!" →
   Start Scanning → AR page opens with the token in the URL (cross-domain safe).
2. **Hunt HUD**: 5 poster chips (bottom), count + timer (top), draggable
   **poster-preview thumbnail** (top-left; tap = enlarge with the current clue
   text; drag anywhere; position remembered).
3. **Scan a poster** → meme video plays anchored to it → compact card
   "✓ 2/5 · Enjoy the meme!" → **Next Clue ▸** button appears (delay is
   dashboard-set) → optional **sponsor ad interstitial** (rotates, closeable)
   → clue card (✕ closeable; clue stays recoverable in the thumbnail).
4. **Timer** runs from FIRST scan to FIFTH (download/walk time never counted).
5. **Completion**: confetti, total time, rank, **📲 Share Victory Card**
   (canvas-generated personalized image → WhatsApp share sheet), leaderboard link.
6. **Resume**: closed tab / new device → "Already registered? Resume your hunt"
   (name + phone must both match) on the landing page; also deep-linked from the
   AR page's gate (`#resume`).
7. **Offline tolerance**: scans queue locally and sync when network returns
   (server arrival timestamp keeps it fair); 8s network timeouts everywhere.

### Booth / public screen
- Leaderboard: top 10 (gold/silver/bronze), "Your Rank" card for visitors with a
  session, "showing top N of M" note, ARRISE lead-gen CTA.
- **Live Hunt Feed**: scans/finishes/joins in near-real-time (first names only),
  "N hunting right now" pulse, optional finish "ding" (🔔 toggle), auto-refresh 5s.
- **TV mode**: append `?tv=1` (Chrome, F11) for 1.6× scaling on the booth screen.

### Admin (`/hunt/admin.html` — same accounts as Admin Nexus; username OR email)
- Participants table: per-poster ✓/○ with timestamps, phones (admin-only),
  ranks, **⚠ SUSPECT** flags, **Verify** buttons (verifying restores a suspect
  to the public board), CSV export, **Reset All Data** (pre-event cleanup).
- **⚙ Settings** (live for everyone instantly — no rebuild):
  - Poster **labels** (chips/thumbnail) and **hints** per poster
  - **Anti-cheat floors**: min total seconds + min gap seconds → SUSPECT flag
  - **Next Clue button delay** (seconds after scan; ≈ meme length − 1s)
  - **Sponsor Ads**: up to 4 image URLs + optional click-through links —
    rotate per Next Clue tap; empty = disabled; never before the first scan,
    never on the completion screen. Upload images to `hunt/` via file manager.

### Anti-cheat
Phone = unique participant (resume requires name match). Server-side ms
timestamps; each poster counts once (DB unique key). Completions faster than
the floors are flagged SUSPECT and hidden from the public board/feed until
admin-verified. Ranking: fastest total, ties → earlier completion.

---

## Architecture / file map

| Piece | File(s) |
|---|---|
| Challenge engine + settings + activity feed | `ar-analytics/api/controllers/hunt.php` (auto-creates `hunt_participants`, `hunt_scans`, `hunt_settings`) |
| Reference DDL (optional) | `ar-analytics/sql/hunt_schema.sql` |
| Landing / resume | `ar-analytics/hunt/index.html` (set `HUNT_AR_URL` to the AR build URL) |
| Leaderboard + live feed | `ar-analytics/hunt/leaderboard.html` |
| Hunt admin | `ar-analytics/hunt/admin.html` |
| In-AR overlay (all hunt UI) | `Assets/WebGLTemplates/iTracker/hunt-overlay.js` (+ identical copies in `iTracker6/` and the deployed build) |
| Template wiring | `<script src="hunt-overlay.js?v=N">` in both templates' `index.html`; fullscreen button; `viewport-fit=cover` |
| Scene generator | `Assets/Editor/MemeHuntSceneBuilder.cs` — Tools → Meme Hunt → 1. Build MemeHunt Scene |
| Router/htaccess | `case 'hunt'` in `api/index.php`; `/hunt/` passthrough in `.htaccess` |

**How the overlay hooks tracking:** `ImageTracker` already calls
`window.arAnalytics.arImageFound/arImageLost` (via `Analytics.jslib`) on every
target acquisition/loss — the overlay shims/wraps that object. Zero C# changes.

### The overlay update workflow (important!)
`hunt-overlay.js` is a static file copied verbatim into builds. To change hunt
UI behavior **without a Unity rebuild**:
1. Edit `Assets/WebGLTemplates/iTracker/hunt-overlay.js` (keep the three copies
   in sync: iTracker, iTracker6, `WebGLBuild/MemeHunt/`).
2. Bump the cache-buster in all three `index.html` files:
   `hunt-overlay.js?v=N` → `v=N+1` (currently **v=15**).
3. Upload `hunt-overlay.js` + `index.html` to `public_html/AR/memehunt/`.

### Fullscreen (all AR experiences — template-level)
⛶ button top-right: native Fullscreen API on Android/desktop; on iPhone (no
API exists in any browser) a pseudo-fullscreen collapses the browser bar to
its minimized strip via a scroll runway, with AR layers pinned to the viewport.

---

## The 5 test posters (current build)

| # | Target id (FIXED — never rename) | Image | Video |
|---|---|---|---|
| 1 | `FIFA_Target` | `Assets/AR_Assets/Images/Fifa Target Image.jpg` | `videos/Fifa%20Video.mp4` |
| 2 | `One8Traget` | `Assets/AR_Assets/Images/One8.png` | `videos/one8.mp4` |
| 3 | `BookCover` | `Assets/AR_Assets/BookCover/Book_Cover_AR_Target_Image.png` | `videos/BookCoverArGreenScreen.mp4` (green screen) |
| 4 | `CultGym` | `Assets/AR_Assets/GYM Poster/GYM Video Target.png` | `videos/Gym%203D%20Target%20A3.mp4` |
| 5 | `Shoes` | `Assets/AR_Assets/Images/Shoes_Poster.png` | `videos/Shoes_GreenScreen.mp4` (green screen) |

Videos are served free via jsDelivr from this public repo:
`https://cdn.jsdelivr.net/gh/prashant1998gupta/AR_ImageTracking@main/videos/<file>`

### Swapping in the REAL meme posters (deferred until content is ready)
Keep the same 5 ids (participants only see dashboard labels). The plan, ready
to regenerate on request: a config-driven `MemeHuntSceneBuilder` (per-poster
image path + video URL + green-screen flag) plus an auto trim/restore build
hook — both were drafted and reverted on 8 Aug pending real content. Until the
hook exists, **after any MemeHunt build restore the global target list before
committing**:
```bash
git checkout -- "Assets/Imagine/ImageTracker/Resources/ImageTrackerGlobalSettings.asset"
```
(The trimmed asset was accidentally pushed twice — restore commits `b95277d`, `dcbd84d`.)

Rebuild flow when content arrives: register meme images under the same ids →
rebuild scene (Tools menu) → build WebGL → upload over `AR/memehunt/` → set
labels/hints in dashboard Settings.

---

## Event-day runbook

**Before doors (day 1):**
1. Walk the real poster route once; set anti-cheat floors ≈ 60–70% of your
   honest time (⚙ Settings). Set Next Clue delay ≈ meme length − 1s.
2. Configure sponsor ads (or clear them).
3. **Reset All Data** (wipes test participants/scans; type `RESET`).
4. Booth screen: Chrome → `leaderboard.html?tv=1` → F11 → tap 🔔 once for sound.
5. Phones charged, posters matte-printed (A3+), QRs verified → `/hunt/`.

**During the event:**
- Hints/labels/floors/ads are all live-editable from a phone via admin Settings.
- Help desk: participants who lost their session → "Already registered? Resume"
  (exact registered name is visible in the admin table if they forget).

**Prizes:** verify the top 3 in person (ask them to re-scan one poster in front
of staff), press **Verify** on their rows, then award. Verifying also restores
any legitimately-fast SUSPECT run to the public board.

## Known limitations — what the system does NOT prevent

Scan events are client-asserted (no cryptographic proof-of-presence):
1. **Forged scans** via direct API calls — mitigated by SUSPECT floors + manual
   top-3 verification, not prevented.
2. **Photographed posters** scanned from another phone's screen — the min-gap
   floor catches fast versions; in-person verification is the real control.
3. **Multiple phone numbers** = multiple attempts. Check the admin table for
   same-name duplicates before prizes.

## Security notes
- Registration collects PII (names + phones). Phones appear ONLY on admin
  endpoints; the public leaderboard/feed uses first names.
- Pre-existing platform issues (reported 7 Aug, still open): DB password +
  APP_SECRET committed to this public repo (`api/config.php`) — rotate before
  the event; web-readable `debug.txt` logging in `pixel.php`; plaintext
  password columns. Set strong admin passwords — both admin accounts can see
  all phones and wipe hunt data.
- Ad-blocker note: hunt element ids/classes deliberately avoid "ad"/"sponsor"
  substrings (`hunt-spot`, `slotImg`, …) so blockers don't hide UI — keep it
  that way when editing.
