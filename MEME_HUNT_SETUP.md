# AR Meme Hunt — Setup & Test Guide
### Bharatiya Vyapar Mahotsav 2026 · 12–15 August · Bharat Mandapam, New Delhi

This implements the "AR Meme Hunt" campaign concept end-to-end using the existing
ARRISE stack. **No Unity C# runtime code was changed** — the hunt logic lives in the
WebGL template overlay + a new PHP controller.

---

## What was added

| Piece | File(s) | Purpose |
|---|---|---|
| Challenge engine (backend) | `ar-analytics/api/controllers/hunt.php` | register / start / scan / status / leaderboard / config + admin list & CSV export. Auto-creates its two tables on first request. |
| Reference schema | `ar-analytics/sql/hunt_schema.sql` | Optional — hunt.php auto-migrates. |
| Landing page | `ar-analytics/hunt/index.html` | Registration (name + phone + consent), "You're In!" instructions, Start Scanning → redirects to the AR build with the participant token. |
| Live leaderboard | `ar-analytics/hunt/leaderboard.html` | Top 10, gold/silver/bronze highlights, auto-refresh 5 s, ARRISE lead-gen CTA. |
| Hunt admin | `ar-analytics/hunt/admin.html` | Admin login (same accounts as the main admin panel), per-poster ✓/○ table with phone numbers, CSV export, Verify buttons for winner verification, suspect flags, pre-event data reset. |
| In-AR overlay | `Assets/WebGLTemplates/iTracker/hunt-overlay.js` (+ copy in `iTracker6/`) | Progress chips (5 posters), timer, hint popups after each scan, completion screen, offline scan queue. Wired into both templates' `index.html`. |
| Scene builder | `Assets/Editor/MemeHuntSceneBuilder.cs` | **Tools → Meme Hunt → 1. Build MemeHunt Scene** — generates `Assets/Scenes_1/MemeHunt.unity` with the 5 test posters. |
| Router/htaccess | `ar-analytics/api/index.php`, `ar-analytics/.htaccess` | `hunt` controller + `/hunt/` folder passthrough. |

## The 5 test posters (all use EXISTING project images + working CDN videos)

| # | Target id | Print/show this image | Video (jsDelivr CDN) |
|---|---|---|---|
| 1 | `FIFA_Target` | `Assets/AR_Assets/Images/Fifa Target Image.jpg` | `videos/Fifa%20Video.mp4` |
| 2 | `One8Traget` | `Assets/AR_Assets/Images/One8.png` | `videos/one8.mp4` |
| 3 | `BookCover` | `Assets/AR_Assets/BookCover/Book_Cover_AR_Target_Image.png` | `videos/BookCoverArGreenScreen.mp4` (green screen) |
| 4 | `CultGym` | `Assets/AR_Assets/GYM Poster/GYM Video Target.png` | `videos/Gym%203D%20Target%20A3.mp4` |
| 5 | `Shoes` | `Assets/AR_Assets/Images/Shoes_Poster.png` | `videos/Shoes_GreenScreen.mp4` (green screen) |

For the real event, replace these with the 5 meme posters: register each meme image
with the same ids (or update ids in BOTH `hunt.php` → `huntPosters()` AND
`MemeHuntSceneBuilder.cs` → `Posters`), swap the CDN video URLs, rebuild.

Hints shown after each scan are defined in `hunt.php` → `huntPosters()` — edit freely,
no rebuild needed (server-side).

**Timer semantics:** the ranked clock runs from the FIRST poster scan to the FIFTH.
Build download time and the walk to poster 1 are never counted, so slow venue
Wi-Fi doesn't punish anyone. Completions faster than 60 s total or with under
10 s between posters are auto-flagged **SUSPECT** and hidden from the public
leaderboard until an admin verifies them (floors configurable at the top of
`hunt.php`: `HUNT_MIN_TOTAL_MS` / `HUNT_MIN_GAP_MS` — tune to the real poster
distances at the venue).

---

## Setup steps

### 1. Deploy the backend (Hostinger)
Upload these to the existing `dashboard.rionick.com` deployment (same layout as the repo):

```
ar-analytics/api/controllers/hunt.php   →  /api/controllers/hunt.php
ar-analytics/api/index.php              →  /api/index.php          (updated)
ar-analytics/.htaccess                  →  /.htaccess              (updated)
ar-analytics/hunt/                      →  /hunt/                  (whole folder)
ar-analytics/sql/hunt_schema.sql        →  optional
```

Then open `https://dashboard.rionick.com/api/controllers/hunt.php?action=config` —
you should see the 5 posters JSON (tables are auto-created on this first request).

### 2. Build the Unity scene (editor is already open)
1. In Unity: **Tools → Meme Hunt → 1. Build MemeHunt Scene**
   (creates `Assets/Scenes_1/MemeHunt.unity`, makes it the only enabled build scene)
2. Optional but recommended for the event: **Tools → Meme Hunt → 2. Trim Global Targets To Hunt 5**
   (page load feature-extracts every registered target — trimming 12 → 5 makes startup faster.
   Re-register other campaigns' targets before building THOSE scenes again.)
3. **File → Build Settings → WebGL → Build** into e.g. `WebGLBuild/MemeHunt`
   (keep the active template `iTracker`; the post-build step injects the 5 `<imagetarget>` tags).

Batch alternative (Unity must be CLOSED):
```bash
"C:/Program Files/Unity/Hub/Editor/6000.3.9f1/Editor/Unity.exe" -batchmode -quit -projectPath "D:/Prashant_WorkSpace/UnityProjects/Prashant_Unity/AR_ImageTracking" -executeMethod MemeHuntSceneBuilder.BuildSceneBatch -logFile "D:/Prashant_WorkSpace/UnityProjects/Prashant_Unity/AR_ImageTracking/Logs/memehunt_batch.log"
```

### 3. Host the WebGL build + connect the pieces
1. Upload the build folder to any HTTPS host (Hostinger works; camera access requires HTTPS).
2. In `ar-analytics/hunt/index.html` set:
   ```js
   var HUNT_AR_URL = 'https://<your-host>/<memehunt-build-path>/';
   ```
   and re-upload that file. (The landing page passes `?hunt_token=...` to the AR page —
   this is how identity crosses domains.)
3. QR code for the posters → `https://dashboard.rionick.com/hunt/`

### 4. Overlay configuration (only if the backend moves)
`hunt-overlay.js` defaults to `https://dashboard.rionick.com`. To point elsewhere,
either edit the `DEFAULTS` block at the top, or test ad-hoc with
`?hunt_api=https://other-host` on the AR page URL.

---

## End-to-end test checklist

1. `/hunt/` on a phone → register with name + phone → "You're In!" → Start Scanning.
2. AR page opens with the hunt HUD (5 chips bottom, 0/5 + timer top).
3. Point the camera at the FIFA card image (screen or print) → video plays →
   "1/5 completed" hint popup appears → chip ticks green, timer starts.
4. Re-scan the same poster → "already scanned" toast, count unchanged.
5. Scan the remaining 4 → completion screen with total time + rank → leaderboard.
   (An indoor speed-run will likely be flagged SUSPECT — that's the plausibility
   floor working. Verify it in admin to make it appear on the leaderboard.)
6. `/hunt/leaderboard.html` shows the entry; `/hunt/admin.html` (existing admin login)
   shows the row with phone + 5 ✓ and CSV export works.
7. Register with the same phone + same name on another device → session resumes;
   same phone + different name → blocked with a clear message.
8. Airplane-mode one scan → chip ticks with "will sync" toast → back online → syncs.
9. **Before the event: `/hunt/admin.html` → "Reset All Data"** to wipe the test
   participants and scans (type `RESET` to confirm).

### Event-day notes
- Print posters matte (glare kills tracking); A3 or larger recommended.
- iOS: first video needs the existing tap-for-sound flow — already handled by the template.
- The scan timestamp is server-side — participants must be online for a scan to count
  (offline scans sync with the arrival time, which stays fair).
- Verify the top 3 with the **Verify** button in `/hunt/admin.html` before giving
  prizes (PDF §15). Verifying also clears a SUSPECT flag and restores the entry
  to the public leaderboard.

## Known limitations — what the system does NOT prevent

Scan events are client-asserted: there is no cryptographic proof the camera
actually saw a poster. Concretely:

1. **Forged scans.** A technical attendee can POST scan requests directly (poster
   ids are visible to the client). Mitigation: the plausibility floors flag
   impossible times as SUSPECT and keep them off the public leaderboard; the
   top 3 must always be verified in person before prizes.
2. **Photographed posters.** The tracker fires on a sharp photo of a poster shown
   on another phone's screen, so someone could "hunt" from one spot. The
   minimum-gap floor catches the fast version of this; a slow version is
   indistinguishable from honest play. In-person verification (ask the winner to
   re-scan one poster in front of staff) is the real control.
3. **Multiple phone numbers.** Uniqueness is per phone number — a person with two
   numbers gets two attempts. Acceptable for a fun event challenge; check the
   admin table for same-name duplicates before prizes.

## Security note (carried over from the earlier review)
Registration collects PII (names + phones). Before the event, strongly consider:
rotating the DB password + APP_SECRET in `api/config.php` (they are in a public repo),
and deleting `api/controllers/debug.txt` logging in `pixel.php`. The hunt controller
itself stores phones server-side only (never exposed on public endpoints).
