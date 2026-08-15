# ✅ AR Analytics Platform — Deployment Guide

## What Was Built

A complete **self-hosted, multi-tenant analytics platform** for your WebAR business.

### Files Created

| Component | Files | Purpose |
|-----------|-------|---------|
| **Database** | `sql/schema.sql` | 6 tables: admins, clients, projects, sessions, events, daily_stats |
| **PHP API** | `api/config.php`, `api/index.php` | Configuration & router |
| **Helpers** | `api/helpers/Database.php`, `Auth.php`, `Response.php` | PDO wrapper, token auth, JSON responses |
| **Controllers** | `api/controllers/track.php`, `auth.php`, `dashboard.php`, `admin.php` | Event collection, auth, dashboard data, admin CRUD |
| **Tracker JS** | `tracker/ar-analytics.js` | Lightweight (~3KB) tracker embedded in AR builds |
| **Client Dashboard** | `dashboard/index.html` | Beautiful dark-themed dashboard (matches your mockup!) |
| **Admin Panel** | `admin/index.html` | Client & project management |
| **Installer** | `install.php` | One-click database setup |
| **Unity Bridge** | `Assets/Imagine/Common/Plugins/Analytics.jslib` | C# → JS analytics bridge |

### Files Modified

| File | Change |
|------|--------|
| `Assets/Imagine/ImageTracker/Scripts/ImageTracker.cs` | Added `WebGLTrackImageFound/Lost` calls in `OnTrackingFound/Lost` |
| `Assets/WebGLTemplates/iTracker/index.html` | Added tracker `<script>` tag |

---

## 🚀 Deployment Steps (Hostinger)

### Step 1: Create MySQL Database
Go to **Hostinger hPanel** → **Databases** → Create database `ar_analytics`

### Step 2: Upload to Hostinger
Upload the entire `ar-analytics/` folder to `public_html/ar-analytics/` via File Manager or FTP

### Step 3: Update Config
Edit `api/config.php` with your Hostinger database credentials:
```php
define('DB_HOST', 'localhost');
define('DB_NAME', 'your_db_name');
define('DB_USER', 'your_db_user');
define('DB_PASS', 'your_db_pass');
define('APP_URL', 'https://yourdomain.com/ar-analytics');
define('APP_SECRET', 'random_64_character_string_here');
```

### Step 4: Run Installer
Visit `https://yourdomain.com/ar-analytics/install.php` → Set admin password → **Delete install.php after!**

### Step 5: Create First Client
1. Go to `yourdomain.com/ar-analytics/admin/`
2. Login with admin credentials
3. Click **+ Add Client** → fill details
4. Click **+ Add Project** → select client → get API key

### Step 6: Add to AR Builds
Add this one line to your AR build's `index.html` (before other scripts):
```html
<script src="https://yourdomain.com/ar-analytics/tracker/ar-analytics.js" 
        data-project="PASTE_API_KEY_HERE"></script>
```

### Step 7: Rebuild Unity WebGL
The template is already updated. Just rebuild — the tracker tag is in the template.

> **IMPORTANT:** For each new client, update the `data-project` attribute with their unique API key from the admin panel.

---

## What Gets Tracked Automatically

### 📊 Auto-Detected (Zero Config)
- Device Type (mobile/desktop/tablet)
- Operating System (iOS/Android/Windows)
- Browser (Chrome/Safari/Firefox)
- Country & City (IP geolocation)
- Language
- Screen Size
- Traffic Source & UTM params
- Session Duration
- New vs Returning Visitors
- Peak Hours

### 🎯 AR Events (Auto-Hooked)
- `ar_session_start` — Webcam activated
- `ar_image_found` — Target detected (with ID)
- `ar_image_lost` — Tracking lost (with duration)
- `ar_activation` — First scan per session
- `ar_scan_duration` — View time per target
- `ar_cta_click` — URL/phone/share clicks
- `ar_screenshot` — Screenshot captured
- `ar_camera_flip` — Camera flipped
- `ar_error` — Errors logged

---

## Architecture Flow

```
Client's AR Build ──(ar-analytics.js)──→ Your Hostinger Server
                                              │
                                         MySQL Database
                                              │
                                    ┌─────────┴─────────┐
                              Admin Panel         Client Dashboard
                              /admin/              /dashboard/
                         (You manage              (Clients see
                          clients &                their stats)
                          API keys)
```

---

## Project Structure

```
ar-analytics/
├── .htaccess              ← URL rewriting & security
├── install.php            ← One-time setup (DELETE after install!)
├── README.md              ← Quick start guide
├── DEPLOYMENT_GUIDE.md    ← This file
├── tracker/
│   └── ar-analytics.js    ← Lightweight tracker (~3KB)
├── api/
│   ├── config.php         ← Database & app configuration
│   ├── index.php          ← API router
│   ├── helpers/
│   │   ├── Database.php   ← PDO wrapper
│   │   ├── Auth.php       ← Token authentication
│   │   └── Response.php   ← JSON response helper
│   └── controllers/
│       ├── track.php      ← Event collection (public)
│       ├── auth.php       ← Login/logout (username OR email)
│       ├── dashboard.php  ← Dashboard data queries
│       ├── admin.php      ← Client/project CRUD
│       └── hunt.php       ← AR Meme Hunt challenge engine
├── admin/
│   └── index.html         ← Admin panel (SPA)
├── dashboard/
│   └── index.html         ← Client dashboard (SPA)
├── hunt/                  ← AR Meme Hunt event module (see below)
│   ├── index.html         ← Registration / resume / start
│   ├── leaderboard.html   ← Public top-10 + live activity feed
│   └── admin.html         ← Hunt ops: participants, verify, settings, ads
└── sql/
    ├── schema.sql         ← Database schema
    └── hunt_schema.sql    ← Hunt tables (reference — auto-created)
```

---

## 🏆 Meme Hunt Module — Deployment

Event scavenger-hunt campaign built on this platform. Full operations guide:
`../MEME_HUNT_SETUP.md` (repo root).

**Deploy** (no installer needed — `hunt.php` auto-creates `hunt_participants`,
`hunt_scans`, `hunt_settings`, `hunt_resume_throttle` and migrates new columns
like `player_code` on first request):
1. Upload `api/controllers/hunt.php`, updated `api/index.php`, updated
   `.htaccess`, and the whole `hunt/` folder alongside the existing deployment.
2. Open `.../api/controllers/hunt.php?action=config` once → poster JSON = live.
3. In `hunt/index.html`, set `HUNT_AR_URL` to the deployed Unity MemeHunt build.
4. The AR build ships its own `hunt-overlay.js` (from the Unity WebGL template);
   overlay updates = replace that one file + bump `?v=N` in the build's
   `index.html` — no Unity rebuild.

> **Deploying an update over a live event:** the migration backfills player
> codes for existing rows in a bounded batch on first request, then lazily for
> the rest — safe on a populated table. Pure PHP/HTML deploys (the event-control
> features, player codes) need no AR rebuild; only overlay changes require
> re-uploading the build's `hunt-overlay.js` + version bump.

**Operate:** `hunt/admin.html` (same admin accounts) → participants (with player
codes), winner verification, CSV export, Reset All Data, and the one-tap event
controls (registration open/close, leaderboard & live-feed show/hide, per-target
open/close) plus live Settings (poster list, labels/hints, anti-cheat floors,
sequential toggle, Next Clue delay, branding, up to 4 rotating sponsor ads).
Booth screen: `hunt/leaderboard.html?tv=1` in Chrome + F11.

---

## Monetization Tiers (Suggested)

| Feature | Free | Pro (₹499/mo) | Business (₹999/mo) |
|---------|------|---------------|-------------------|
| Page views tracked | 1,000/mo | 50,000/mo | Unlimited |
| Dashboard access | ❌ | ✅ | ✅ |
| Real-time data | ❌ | 15-min delay | ✅ Real-time |
| Data retention | 7 days | 90 days | 1 year |
| AR scan tracking | Basic | Full | Full + Heatmaps |
| Export CSV | ❌ | ✅ | ✅ |
| Custom branding | ❌ | ❌ | ✅ White-label |
| API access | ❌ | ❌ | ✅ |

---

## URLs After Deployment

| URL | Purpose |
|-----|---------|
| `yourdomain.com/ar-analytics/admin/` | Admin panel (manage clients & projects) |
| `yourdomain.com/ar-analytics/dashboard/` | Client dashboard (login with client email) |
| `yourdomain.com/ar-analytics/tracker/ar-analytics.js` | Tracker script |
| `yourdomain.com/ar-analytics/api/` | API status check |
