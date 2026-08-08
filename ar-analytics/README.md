# AR Analytics Platform

A self-hosted analytics dashboard for your WebAR experiences. Track every scan, view, and interaction — with per-client dashboards and admin management.

## Quick Start (Hostinger)

### Step 1: Create Database
1. Login to [Hostinger hPanel](https://hpanel.hostinger.com)
2. Go to **Databases** → **MySQL Databases**
3. Create a new database (e.g., `ar_analytics`)
4. Note down: **database name**, **username**, **password**

### Step 2: Upload Files
1. Open **File Manager** in hPanel
2. Navigate to `public_html/` (or your preferred subdirectory)
3. Upload the entire `ar-analytics/` folder
4. The structure should be: `public_html/ar-analytics/`

### Step 3: Configure
1. Edit `ar-analytics/api/config.php` in File Manager
2. Update these values:
    ```php
    define('DB_HOST', 'localhost');
    define('DB_NAME', 'your_database_name');
    define('DB_USER', 'your_database_user');
    define('DB_PASS', 'your_database_password');
    define('APP_URL', 'https://yourdomain.com/ar-analytics');
    define('APP_SECRET', 'change_to_random_64_chars');
    ```

### Step 4: Install
1. Visit `https://yourdomain.com/ar-analytics/install.php`
2. Set your admin username and password
3. Click **Install Now**
4. **Delete `install.php`** after success!

### Step 5: Add Tracking to AR Builds
1. Open **Admin Panel** at `yourdomain.com/ar-analytics/admin/`
2. Create a client → Create a project → Get the API key
3. Add this ONE line to your AR build's `index.html`:

```html
<script src="https://yourdomain.com/ar-analytics/tracker/ar-analytics.js" 
        data-project="YOUR_PROJECT_API_KEY"></script>
```

That's it! Analytics start flowing immediately.

---

## URLs

| URL | Purpose |
|-----|---------|
| `yourdomain.com/ar-analytics/admin/` | Admin panel (manage clients & projects) |
| `yourdomain.com/ar-analytics/dashboard/` | Client dashboard (login with client email) |
| `yourdomain.com/ar-analytics/tracker/ar-analytics.js` | Tracker script |

---

## What Gets Tracked

### Automatic (no code needed)
- Device type (mobile/desktop/tablet)
- Operating system (iOS/Android/Windows)
- Browser (Chrome/Safari/Firefox)
- Country & City (IP geolocation)
- Language
- Screen size
- Traffic source & UTM parameters
- Session duration
- New vs returning visitors
- Peak usage hours

### AR-Specific (auto-hooked via Unity bridge)
- `ar_session_start` — Webcam activated
- `ar_image_found` — Image target detected (with target ID)
- `ar_image_lost` — Tracking lost (with duration)
- `ar_activation` — First scan per session
- `ar_scan_duration` — How long each target was visible
- `ar_cta_click` — URL opens, phone calls, shares
- `ar_screenshot` — Screenshot captured
- `ar_camera_flip` — Camera flipped
- `ar_error` — Camera/tracker errors

---

## Project Structure

```
ar-analytics/
├── .htaccess              ← URL rewriting & security
├── install.php            ← One-time setup (DELETE after install!)
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
├── hunt/                  ← AR Meme Hunt (event campaign module)
│   ├── index.html         ← Registration / resume / start
│   ├── leaderboard.html   ← Public top-10 + live activity feed (?tv=1)
│   └── admin.html         ← Hunt ops: participants, verify, settings, ads
└── sql/
    ├── schema.sql         ← Database schema
    └── hunt_schema.sql    ← Hunt tables (reference — auto-created by hunt.php)
```

---

## Meme Hunt Module (event campaigns)

A timed AR scavenger hunt built on this platform (see `../MEME_HUNT_SETUP.md`
for the full guide). `hunt.php` auto-creates its three tables
(`hunt_participants`, `hunt_scans`, `hunt_settings`) on first request.

- **Public:** register / resume (phone + name), scan recording with server-side
  ms timestamps, leaderboard, live activity feed (first names only).
- **Admin (`hunt/admin.html`, same accounts as the admin panel):** participant
  table with phones, winner verification, SUSPECT flags, CSV export, data
  reset, and live Settings: poster labels/hints, anti-cheat floors, Next Clue
  delay, up to 4 rotating sponsor ads.
- **In-AR UI** lives in the Unity WebGL template (`hunt-overlay.js`), which
  talks to `hunt.php` cross-origin (wildcard CORS is already platform policy).

---

## Unity Integration

The `Analytics.jslib` plugin and `ImageTracker.cs` modifications automatically track
image found/lost events. No additional C# code needed.

Files modified:
- `Assets/Imagine/Common/Plugins/Analytics.jslib` (new)
- `Assets/Imagine/ImageTracker/Scripts/ImageTracker.cs` (modified)
- `Assets/WebGLTemplates/iTracker/index.html` (modified)
