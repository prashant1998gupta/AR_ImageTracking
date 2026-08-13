<?php
/**
 * Hunt Controller — AR Meme Hunt challenge engine (Bharatiya Vyapar Mahotsav 2026)
 *
 * Public actions (no auth):
 *   POST ?action=register     {name, phone, company?, business_type?, consent}  -> {token, ...status}
 *   POST ?action=start        {token}                                          -> readiness check (timer starts at first scan)
 *   POST ?action=scan         {token, poster_id}                               -> records a poster scan (first scan starts the timer)
 *   GET  ?action=status&token=...                                              -> participant progress
 *   GET  ?action=config                                                        -> poster list
 *   GET  ?action=leaderboard                                                   -> public top 10 (no phones)
 *
 * Admin actions (Bearer token from auth.php?action=admin-login):
 *   GET  ?action=admin-participants                                            -> full table incl. phone
 *   GET  ?action=admin-export                                                  -> CSV download
 *
 * Anti-cheat: phone is the unique participant identifier, every scan gets a
 * server-side millisecond timestamp, each poster counts once per participant,
 * ranking = fastest total time, ties broken by earlier completion.
 *
 * Timer semantics: the clock runs from the FIRST poster scan to the FIFTH —
 * download/boot time and the walk to poster 1 are not counted. Completions
 * faster than the plausibility floors below are flagged is_suspect and hidden
 * from the public leaderboard until an admin verifies them.
 */

// Plausibility floors (scan events are client-asserted — see MEME_HUNT_SETUP.md)
define('HUNT_MIN_TOTAL_MS', 60000);   // faster than 60s across 5 posters = suspect
define('HUNT_MIN_GAP_MS', 10000);     // faster than 10s between posters = suspect

require_once __DIR__ . '/../helpers/Database.php';
require_once __DIR__ . '/../helpers/Auth.php';
require_once __DIR__ . '/../helpers/Response.php';

Response::cors();

$method = $_SERVER['REQUEST_METHOD'];
$action = $_GET['action'] ?? '';

// CSV export sends its own headers — everything else is JSON
if ($action !== 'admin-export') {
    Response::json();
}

// ─── Poster configuration ────────────────────────────────────────────
// Order = canonical hunt order (the "next hint" points to the first unscanned
// poster in this order). The ids MUST match the Unity image target ids and
// are fixed at build time; labels and hints below are DEFAULTS which can be
// overridden live from hunt/admin.html → Settings (stored in hunt_settings).
//
// UNITY OWNS THE IDS. They are baked into the WebGL build, so the server
// LEARNS them instead of hardcoding them: "Tools ▸ Meme Hunt ▸ 3. Copy Poster
// List JSON" exports the built scene's targets, and pasting that JSON into
// hunt/admin.html → Settings → Poster list stores it as the 'poster_list'
// setting, which then REPLACES the list below (any number of posters). With
// no 'poster_list' saved — or none of its entries valid — the defaults below
// are used exactly as before.
//
// $refresh busts the per-request cache; only the admin save path needs it,
// after writing a new 'poster_list'.
function huntPosters($refresh = false) {
    static $cached = null;
    if ($refresh) { $cached = null; }
    if ($cached !== null) { return $cached; }
    $posters = [
        ['id' => 'FIFA_Target', 'label' => 'FIFA',  'hint' => 'Kick-off ho chuka hai! Football wala poster dhoondo — jahan game ki baat hoti hai, FIFA card wahin hai.'],
        ['id' => 'One8Traget',  'label' => 'One8',  'hint' => 'Ab thodi King Kohli wali energy! One8 shoes ka poster aas-paas hi hai — sneakerheads ko turant dikh jayega.'],
        ['id' => 'BookCover',   'label' => 'Book',  'hint' => 'Ab thoda intellectual bano — ek book cover ka poster dhoondo. Padhai nahi karni, bas scan karna hai!'],
        ['id' => 'CultGym',     'label' => 'Gym',   'hint' => 'Networking zyada, patience kam? Gym poster ke paas jao — gains yahin milenge.'],
        ['id' => 'Shoes',       'label' => 'Shoes', 'hint' => 'Last one! Jo shoes sabse zyada chamak rahe hain, wahi poster scan karna hai. Finish line paas hai!'],
    ];
    // Saved Unity manifest wins over the built-in list (ids, order and count).
    $manifest = huntPosterList();
    if ($manifest !== null) { $posters = $manifest; }

    // Per-poster label/hint overrides apply ON TOP of whichever list won,
    // keyed by id — the admin's label/hint editor keeps working either way.
    $overrides = huntSetting('posters');
    if (is_array($overrides)) {
        foreach ($posters as $i => $p) {
            if (isset($overrides[$p['id']]) && is_array($overrides[$p['id']])) {
                $o = $overrides[$p['id']];
                // Empty override = fall back to the default (lets admins "reset")
                if (!empty($o['label'])) { $posters[$i]['label'] = $o['label']; }
                if (!empty($o['hint']))  { $posters[$i]['hint']  = $o['hint']; }
            }
        }
    }
    $cached = $posters;
    return $cached;
}

/**
 * The Unity-exported poster manifest, validated — or null when none is saved.
 *
 * Shape (exactly what MemeHuntSceneBuilder writes to hunt-posters.json):
 *   [ {"id":"FIFA_Target","label":"FIFA","hint":"…"}, … ]
 * null is the signal to keep the hardcoded defaults, so a missing/emptied/
 * all-invalid setting can never wipe the hunt.
 */
function huntPosterList() {
    $clean = normalizePosterList(huntSetting('poster_list'));
    return count($clean) ? $clean : null;
}

/**
 * Validate + normalise a raw poster manifest. Shared by the reader above and
 * the admin save path so both agree on exactly what a valid entry is.
 * Invalid ids and duplicates are skipped rather than failing the whole list.
 */
function normalizePosterList($raw) {
    $out = [];
    $seen = [];
    if (!is_array($raw)) { return $out; }
    foreach ($raw as $entry) {
        // hunt_settings.setting_value is TEXT (65 535 bytes). A huge list would be
        // truncated mid-JSON by MySQL, json_decode would then fail on the next read
        // and the server would silently fall back to the built-in placeholders.
        if (count($out) >= 60) { break; }
        if (!is_array($entry) || !isset($entry['id']) || !is_scalar($entry['id'])) { continue; }
        $id = trim(strval($entry['id']));
        if (!preg_match('/^[A-Za-z0-9_-]{1,64}$/', $id)) { continue; }
        if (isset($seen[$id])) { continue; }
        $seen[$id] = true;
        $label = (isset($entry['label']) && is_scalar($entry['label']))
            ? trim(mb_substr(strval($entry['label']), 0, 20, 'UTF-8')) : '';
        $hint = (isset($entry['hint']) && is_scalar($entry['hint']))
            ? trim(mb_substr(strval($entry['hint']), 0, 300, 'UTF-8')) : '';
        $out[] = ['id' => $id, 'label' => $label !== '' ? $label : $id, 'hint' => $hint];
    }
    return $out;
}

/** Read a JSON setting saved from the admin dashboard (null if absent). */
function huntSetting($key) {
    try {
        $db = Database::getInstance();
        $row = $db->queryOne("SELECT setting_value FROM hunt_settings WHERE setting_key = ?", [$key]);
        return $row ? json_decode($row['setting_value'], true) : null;
    } catch (Exception $e) { return null; }
}

function saveHuntSetting($db, $key, $value) {
    $db->execute(
        "INSERT INTO hunt_settings (setting_key, setting_value) VALUES (?, ?)
         ON DUPLICATE KEY UPDATE setting_value = VALUES(setting_value)",
        [$key, json_encode($value, JSON_UNESCAPED_UNICODE)]
    );
}

/** Drop a setting so its hardcoded default takes over again ("reset to built-in"). */
function deleteHuntSetting($db, $key) {
    $db->execute("DELETE FROM hunt_settings WHERE setting_key = ?", [$key]);
}

/** White-label branding — admin-editable; drives event name, brand name,
 *  colors and CTAs across the overlay, landing page, leaderboard and victory
 *  card. Defaults = the Bharatiya Vyapar Mahotsav campaign. */
function huntBranding() {
    $b = [
        'event_name' => 'Bharatiya Vyapar Mahotsav 2026',
        'event_meta' => '12–15 August · Bharat Mandapam, New Delhi',
        // "PREFIX|SUFFIX": the part after | is rendered in the accent color
        'brand_name' => 'AR|RISE',
        'hunt_title' => 'AR Meme Hunt',
        'powered_by' => 'ARRISE / RIONICK STUDIOS',
        'primary_color' => '#dc1e1e',
        'cta_headline' => 'Want to Make Your Marketing Interactive Too?',
        'cta_text' => 'Book a Demo',
        'cta_url' => 'https://dashboard.rionick.com',
    ];
    $o = huntSetting('branding');
    if (is_array($o)) {
        foreach ($b as $k => $v) {
            if (isset($o[$k]) && is_string($o[$k]) && trim($o[$k]) !== '') {
                $b[$k] = trim(mb_substr($o[$k], 0, 200, 'UTF-8'));
            }
        }
    }
    if (!preg_match('/^#[0-9a-fA-F]{6}$/', $b['primary_color'])) { $b['primary_color'] = '#dc1e1e'; }
    if (strpos($b['cta_url'], 'https://') !== 0) { $b['cta_url'] = 'https://dashboard.rionick.com'; }
    return $b;
}

/** Client UI tuning — dashboard-editable, delivered to the overlay in every
 *  status/scan response (applies live, no app rebuild). */
function huntUi() {
    $delay = 9;   // seconds until the "Next Clue" button appears after a scan
    $ads = [];    // sponsor interstitials, rotate per Next Clue tap ([] = disabled)
    $sequential = false;   // admin toggle: posters must be scanned in order
    $ui = huntSetting('ui');
    if (is_array($ui)) {
        if (isset($ui['next_btn_delay_s'])) { $delay = max(0, min(60, intval($ui['next_btn_delay_s']))); }
        if (!empty($ui['sequential'])) { $sequential = true; }
        if (isset($ui['ads']) && is_array($ui['ads'])) {
            foreach ($ui['ads'] as $ad) {
                if (is_array($ad) && !empty($ad['image'])) {
                    $ads[] = ['image' => strval($ad['image']), 'link' => strval(isset($ad['link']) ? $ad['link'] : '')];
                }
            }
        } elseif (!empty($ui['ad_image_url'])) {
            // settings saved by the earlier single-ad panel version
            $ads[] = ['image' => strval($ui['ad_image_url']), 'link' => strval(isset($ui['ad_link_url']) ? $ui['ad_link_url'] : '')];
        }
    }
    return [
        'next_btn_delay_s' => $delay,
        'sequential' => $sequential,
        'branding' => huntBranding(),
        'ads' => $ads,
        // legacy single-ad fields for any cached overlay still reading them
        'ad_image_url' => count($ads) ? $ads[0]['image'] : '',
        'ad_link_url' => count($ads) ? $ads[0]['link'] : '',
    ];
}

/** Anti-cheat plausibility floors — dashboard-tunable, defaults from the constants. */
function huntFloors() {
    $minTotal = HUNT_MIN_TOTAL_MS;
    $minGap = HUNT_MIN_GAP_MS;
    $f = huntSetting('floors');
    if (is_array($f)) {
        if (isset($f['min_total_s'])) { $minTotal = max(0, intval($f['min_total_s'])) * 1000; }
        if (isset($f['min_gap_s']))   { $minGap   = max(0, intval($f['min_gap_s'])) * 1000; }
    }
    return ['total' => $minTotal, 'gap' => $minGap];
}

function huntPosterIds() {
    $ids = [];
    foreach (huntPosters() as $p) { $ids[] = $p['id']; }
    return $ids;
}

$db = Database::getInstance();

// ─── Auto-migration: create hunt tables if missing ───────────────────
$hasParticipants = $db->scalar("SHOW TABLES LIKE 'hunt_participants'");
if (!$hasParticipants) {
    try {
        $db->execute("CREATE TABLE IF NOT EXISTS hunt_participants (
            id INT AUTO_INCREMENT PRIMARY KEY,
            name VARCHAR(100) NOT NULL,
            phone VARCHAR(20) NOT NULL,
            company VARCHAR(150) DEFAULT '',
            business_type VARCHAR(100) DEFAULT '',
            token VARCHAR(64) NOT NULL,
            consent BOOLEAN DEFAULT TRUE,
            registered_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            started_at DATETIME(3) NULL,
            completed_at DATETIME(3) NULL,
            total_ms BIGINT NULL,
            is_verified BOOLEAN DEFAULT FALSE,
            is_suspect BOOLEAN DEFAULT FALSE,
            UNIQUE KEY uk_phone (phone),
            UNIQUE KEY uk_token (token),
            INDEX idx_completed (completed_at)
        ) ENGINE=InnoDB");
    } catch (Exception $e) {
        error_log('[Hunt] create hunt_participants failed: ' . $e->getMessage());
        Response::error('Hunt storage unavailable', 500);
    }
} else {
    // Quick Migration: add is_suspect if the table predates it
    $hasColSuspect = $db->scalar("SHOW COLUMNS FROM hunt_participants LIKE 'is_suspect'");
    if (!$hasColSuspect) { try { $db->execute("ALTER TABLE hunt_participants ADD COLUMN is_suspect BOOLEAN DEFAULT FALSE AFTER is_verified"); } catch (Exception $e) {} }
}
$hasScans = $db->scalar("SHOW TABLES LIKE 'hunt_scans'");
if (!$hasScans) {
    try {
        $db->execute("CREATE TABLE IF NOT EXISTS hunt_scans (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            participant_id INT NOT NULL,
            poster_id VARCHAR(64) NOT NULL,
            scanned_at DATETIME(3) NULL,
            UNIQUE KEY uk_participant_poster (participant_id, poster_id),
            INDEX idx_poster (poster_id),
            FOREIGN KEY (participant_id) REFERENCES hunt_participants(id) ON DELETE CASCADE
        ) ENGINE=InnoDB");
    } catch (Exception $e) {
        error_log('[Hunt] create hunt_scans failed: ' . $e->getMessage());
        Response::error('Hunt storage unavailable', 500);
    }
}
$hasSettings = $db->scalar("SHOW TABLES LIKE 'hunt_settings'");
if (!$hasSettings) {
    // Non-fatal: without this table the hardcoded defaults still work
    try {
        $db->execute("CREATE TABLE IF NOT EXISTS hunt_settings (
            setting_key VARCHAR(64) PRIMARY KEY,
            setting_value TEXT,
            updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
        ) ENGINE=InnoDB");
    } catch (Exception $e) { error_log('[Hunt] create hunt_settings failed: ' . $e->getMessage()); }
}

// ─── Dispatch ────────────────────────────────────────────────────────
switch ($action) {
    case 'register':
        if ($method !== 'POST') Response::error('Method not allowed', 405);
        handleRegister($db);
        break;
    case 'resume':
        if ($method !== 'POST') Response::error('Method not allowed', 405);
        handleResume($db);
        break;
    case 'start':
        if ($method !== 'POST') Response::error('Method not allowed', 405);
        handleStart($db);
        break;
    case 'scan':
        if ($method !== 'POST') Response::error('Method not allowed', 405);
        handleScan($db);
        break;
    case 'status':
        handleStatus($db);
        break;
    case 'config':
        Response::success(['posters' => publicPosters(), 'total' => count(huntPosters()), 'ui' => huntUi()]);
        break;
    case 'leaderboard':
        handleLeaderboard($db);
        break;
    case 'activity':
        handleActivity($db);
        break;
    case 'admin-participants':
        Auth::requireAuth(['admin', 'super_admin']);
        handleAdminParticipants($db);
        break;
    case 'admin-export':
        Auth::requireAuth(['admin', 'super_admin']);
        handleAdminExport($db);
        break;
    case 'admin-verify':
        if ($method !== 'POST') Response::error('Method not allowed', 405);
        Auth::requireAuth(['admin', 'super_admin']);
        handleAdminVerify($db);
        break;
    case 'admin-add-player':
        if ($method !== 'POST') Response::error('Method not allowed', 405);
        Auth::requireAuth(['admin', 'super_admin']);
        handleAdminAddPlayer($db);
        break;
    case 'admin-remove':
        if ($method !== 'POST') Response::error('Method not allowed', 405);
        Auth::requireAuth(['admin', 'super_admin']);
        handleAdminRemove($db);
        break;
    case 'admin-reset':
        if ($method !== 'POST') Response::error('Method not allowed', 405);
        Auth::requireAuth(['admin', 'super_admin']);
        handleAdminReset($db);
        break;
    case 'admin-settings':
        Auth::requireAuth(['admin', 'super_admin']);
        handleAdminSettings($db);
        break;
    case 'admin-save-settings':
        if ($method !== 'POST') Response::error('Method not allowed', 405);
        Auth::requireAuth(['admin', 'super_admin']);
        handleAdminSaveSettings($db);
        break;
    default:
        Response::error('Unknown action', 404);
}

// ─── Public actions ──────────────────────────────────────────────────

function publicPosters() {
    $out = [];
    foreach (huntPosters() as $p) {
        $out[] = ['id' => $p['id'], 'label' => $p['label']];
    }
    return $out;
}

function normalizePhone($raw) {
    $digits = preg_replace('/[^0-9]/', '', $raw ?? '');
    // Strip Indian country code prefix if present
    if (strlen($digits) === 12 && substr($digits, 0, 2) === '91') {
        $digits = substr($digits, 2);
    }
    if (strlen($digits) === 11 && substr($digits, 0, 1) === '0') {
        $digits = substr($digits, 1);
    }
    return $digits;
}

function handleRegister($db) {
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input) Response::error('Invalid JSON body', 400);

    $name  = trim(mb_substr($input['name'] ?? '', 0, 100, 'UTF-8'));
    $phone = normalizePhone($input['phone'] ?? '');
    $company = trim(mb_substr($input['company'] ?? '', 0, 150, 'UTF-8'));
    $businessType = trim(mb_substr($input['business_type'] ?? '', 0, 100, 'UTF-8'));
    $consent = !empty($input['consent']);

    if (mb_strlen($name, 'UTF-8') < 2) Response::error('Please enter your full name', 400);
    if (strlen($phone) < 8 || strlen($phone) > 15) Response::error('Please enter a valid phone number', 400);
    if (!$consent) Response::error('Consent is required to participate', 400);

    $existing = $db->queryOne("SELECT * FROM hunt_participants WHERE phone = ?", [$phone]);
    if ($existing) {
        resumeExisting($db, $existing, $name);
    }

    $token = bin2hex(random_bytes(16));
    try {
        $db->insert(
            "INSERT INTO hunt_participants (name, phone, company, business_type, token, consent) VALUES (?, ?, ?, ?, ?, ?)",
            [$name, $phone, $company, $businessType, $token, $consent ? 1 : 0]
        );
    } catch (Exception $e) {
        // Duplicate phone raced past the SELECT above — converge on the resume path
        $existing = $db->queryOne("SELECT * FROM hunt_participants WHERE phone = ?", [$phone]);
        if ($existing) {
            resumeExisting($db, $existing, $name);
        }
        error_log('[Hunt] register insert failed: ' . $e->getMessage());
        Response::error('Could not register, please try again', 500);
    }
    $participant = $db->queryOne("SELECT * FROM hunt_participants WHERE token = ?", [$token]);
    Response::success(participantState($db, $participant, false), 'Registered');
}

/**
 * Resume guard: a bare phone number must not hand out someone else's session.
 * The name has to match the original registration (case-insensitive).
 */
function resumeExisting($db, $existing, $name) {
    if (mb_strtolower(trim($existing['name']), 'UTF-8') === mb_strtolower($name, 'UTF-8')) {
        Response::success(participantState($db, $existing, true), 'Welcome back');
    }
    Response::error('This phone number is already registered under a different name. Resume on the device you registered with, or visit the ARRISE team for help.', 409);
}

/**
 * Explicit resume for participants whose device lost the session token
 * (closed tab + cleared storage, new browser, in-app browser...).
 * Requires name + phone to match the original registration.
 */
function handleResume($db) {
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input) Response::error('Invalid JSON body', 400);

    $name = trim(mb_substr($input['name'] ?? '', 0, 100, 'UTF-8'));
    $phone = normalizePhone($input['phone'] ?? '');
    if (mb_strlen($name, 'UTF-8') < 2) Response::error('Please enter the name you registered with', 400);
    if (strlen($phone) < 8 || strlen($phone) > 15) Response::error('Please enter a valid phone number', 400);

    $existing = $db->queryOne("SELECT * FROM hunt_participants WHERE phone = ?", [$phone]);
    if (!$existing) Response::error('No registration found for this phone number — please register first', 404);
    resumeExisting($db, $existing, $name);
}

function requireParticipant($db, $token) {
    $token = trim($token ?? '');
    if ($token === '' || strlen($token) > 64) Response::error('Missing token', 401);
    $participant = $db->queryOne("SELECT * FROM hunt_participants WHERE token = ?", [$token]);
    if (!$participant) Response::error('Invalid token — please register again', 401);
    return $participant;
}

function handleStart($db) {
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input) Response::error('Invalid JSON body', 400);
    $participant = requireParticipant($db, $input['token'] ?? '');

    if ($participant['completed_at'] !== null) {
        Response::success(participantState($db, $participant, true), 'Already completed');
    }
    // The clock does NOT start here — it starts at the first poster scan, so
    // build download time and the walk to poster 1 are never counted.
    Response::success(participantState($db, $participant, true), 'Ready — the timer starts at your first scan');
}

function handleScan($db) {
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input) Response::error('Invalid JSON body', 400);
    $participant = requireParticipant($db, $input['token'] ?? '');

    $posterId = trim($input['poster_id'] ?? '');
    if (!in_array($posterId, huntPosterIds(), true)) Response::error('Unknown poster', 400);

    if ($participant['completed_at'] !== null) {
        $state = participantState($db, $participant, true);
        $state['duplicate'] = true;
        Response::success($state, 'Challenge already completed');
    }

    // Sequential mode (admin toggle): posters must be found in order. Checked
    // BEFORE the timer auto-start, so a rejected out-of-order first scan does
    // not start anyone's clock. Re-scans of already-counted posters fall
    // through to the normal duplicate path.
    $uiCfg = huntUi();
    if (!empty($uiCfg['sequential'])) {
        $have = [];
        foreach ($db->query("SELECT poster_id FROM hunt_scans WHERE participant_id = ?", [$participant['id']]) as $s) {
            $have[$s['poster_id']] = true;
        }
        if (empty($have[$posterId])) {
            $expected = null;
            foreach (huntPosters() as $p) {
                if (empty($have[$p['id']])) { $expected = $p; break; }
            }
            if ($expected && $posterId !== $expected['id']) {
                Response::error('Posters unlock in order — find the "' . $expected['label'] . '" poster next!', 409);
            }
        }
    }

    // Auto-start the timer on first scan (robustness: user skipped the Start screen)
    if ($participant['started_at'] === null) {
        $db->execute("UPDATE hunt_participants SET started_at = NOW(3) WHERE id = ? AND started_at IS NULL", [$participant['id']]);
    }

    $duplicate = false;
    try {
        $db->execute(
            "INSERT INTO hunt_scans (participant_id, poster_id, scanned_at) VALUES (?, ?, NOW(3))",
            [$participant['id'], $posterId]
        );
    } catch (PDOException $e) {
        $isDupKey = $e->getCode() == 23000 || (isset($e->errorInfo[1]) && intval($e->errorInfo[1]) === 1062);
        if ($isDupKey) {
            // Unique key (participant_id, poster_id) violated -> poster already counted
            $duplicate = true;
        } else {
            error_log('[Hunt] scan insert failed: ' . $e->getMessage());
            Response::error('Could not record the scan — please try again', 500);
        }
    }

    // Completion check — set completed_at exactly once (guarded UPDATE).
    // Counts only scans for posters that are CURRENTLY in the list: once the list
    // can change (a saved poster_list), rows left over from removed posters would
    // otherwise count toward completion and mark someone finished early.
    $liveIds = huntPosterIds();
    $ph = implode(',', array_fill(0, count($liveIds), '?'));
    $count = intval($db->scalar(
        "SELECT COUNT(*) FROM hunt_scans WHERE participant_id = ? AND poster_id IN ($ph)",
        array_merge([$participant['id']], $liveIds)
    ));
    if ($count >= count($liveIds)) {
        $changed = $db->execute(
            "UPDATE hunt_participants
             SET completed_at = (SELECT MAX(scanned_at) FROM hunt_scans WHERE participant_id = ?),
                 total_ms = TIMESTAMPDIFF(MICROSECOND, started_at, (SELECT MAX(scanned_at) FROM hunt_scans WHERE participant_id = ?)) DIV 1000
             WHERE id = ? AND completed_at IS NULL AND started_at IS NOT NULL",
            [$participant['id'], $participant['id'], $participant['id']]
        );
        if ($changed > 0) {
            flagIfImplausible($db, $participant['id']);
        }
    }

    $participant = $db->queryOne("SELECT * FROM hunt_participants WHERE id = ?", [$participant['id']]);
    $state = participantState($db, $participant, true);
    $state['duplicate'] = $duplicate;
    $state['poster_id'] = $posterId;
    Response::success($state, $duplicate ? 'Poster already scanned' : 'Poster scanned');
}

function handleStatus($db) {
    $participant = requireParticipant($db, $_GET['token'] ?? '');
    Response::success(participantState($db, $participant, true));
}

/**
 * Scan events are client-asserted (no cryptographic proof the camera saw the
 * poster), so implausibly fast completions are flagged and kept off the public
 * leaderboard until an admin verifies the participant in person.
 */
function flagIfImplausible($db, $participantId) {
    $row = $db->queryOne("SELECT total_ms FROM hunt_participants WHERE id = ?", [$participantId]);
    if (!$row || $row['total_ms'] === null) return;

    $floors = huntFloors();
    $suspect = intval($row['total_ms']) < $floors['total'];
    if (!$suspect) {
        $times = [];
        foreach ($db->query("SELECT scanned_at FROM hunt_scans WHERE participant_id = ? ORDER BY scanned_at ASC", [$participantId]) as $s) {
            $dt = DateTime::createFromFormat('Y-m-d H:i:s.u', $s['scanned_at'])
               ?: DateTime::createFromFormat('Y-m-d H:i:s', $s['scanned_at']);
            if ($dt) $times[] = floatval($dt->format('U.u'));
        }
        for ($i = 1; $i < count($times); $i++) {
            if (($times[$i] - $times[$i - 1]) * 1000 < $floors['gap']) { $suspect = true; break; }
        }
    }
    if ($suspect) {
        $db->execute("UPDATE hunt_participants SET is_suspect = TRUE WHERE id = ?", [$participantId]);
        error_log('[Hunt] participant ' . $participantId . ' flagged as suspect (implausibly fast completion)');
    }
}

function formatMs($ms) {
    if ($ms === null) return null;
    $totalSeconds = intval($ms / 1000);
    // Over an hour: H:MM:SS — "507:37" as minutes reads like a broken clock
    if ($totalSeconds >= 3600) {
        return sprintf('%d:%02d:%02d', intval($totalSeconds / 3600), intval(($totalSeconds % 3600) / 60), $totalSeconds % 60);
    }
    return sprintf('%02d:%02d', intval($totalSeconds / 60), $totalSeconds % 60);
}

function participantRank($db, $participant) {
    if ($participant['completed_at'] === null || $participant['total_ms'] === null) return null;
    if (!empty($participant['is_suspect']) && empty($participant['is_verified'])) return null;
    $ahead = intval($db->scalar(
        "SELECT COUNT(*) FROM hunt_participants
         WHERE completed_at IS NOT NULL AND id != ?
           AND NOT (is_suspect = TRUE AND is_verified = FALSE)
           AND (total_ms < ? OR (total_ms = ? AND completed_at < ?))",
        [$participant['id'], $participant['total_ms'], $participant['total_ms'], $participant['completed_at']]
    ));
    return $ahead + 1;
}

function participantState($db, $participant, $includeProgress) {
    $scannedRows = $db->query(
        "SELECT poster_id FROM hunt_scans WHERE participant_id = ? ORDER BY scanned_at ASC",
        [$participant['id']]
    );
    $scanned = [];
    foreach ($scannedRows as $row) { $scanned[] = $row['poster_id']; }

    $next = null;
    foreach (huntPosters() as $p) {
        if (!in_array($p['id'], $scanned, true)) {
            $next = ['id' => $p['id'], 'label' => $p['label'], 'hint' => $p['hint']];
            break;
        }
    }

    $completed = $participant['completed_at'] !== null;
    $totalMs = $participant['total_ms'] !== null ? intval($participant['total_ms']) : null;

    // Elapsed time since start (for the in-AR HUD timer)
    $elapsedMs = null;
    if ($completed) {
        $elapsedMs = $totalMs;
    } elseif ($participant['started_at'] !== null) {
        $elapsedMs = intval($db->scalar(
            "SELECT TIMESTAMPDIFF(MICROSECOND, started_at, NOW(3)) DIV 1000 FROM hunt_participants WHERE id = ?",
            [$participant['id']]
        ));
    }

    return [
        'token' => $participant['token'],
        'name' => $participant['name'],
        'started' => $participant['started_at'] !== null,
        'completed' => $completed,
        'scanned' => $scanned,
        'count' => count($scanned),
        'total' => count(huntPosters()),
        'next' => $completed ? null : $next,
        'total_ms' => $totalMs,
        'elapsed_ms' => $elapsedMs,
        'time_formatted' => formatMs($totalMs),
        'rank' => participantRank($db, $participant),
        'posters' => publicPosters(),
        'ui' => huntUi(),
        'resumed' => true,
    ];
}

function handleLeaderboard($db) {
    $rows = $db->query(
        "SELECT name, company, total_ms, completed_at FROM hunt_participants
         WHERE completed_at IS NOT NULL AND total_ms IS NOT NULL
           AND NOT (is_suspect = TRUE AND is_verified = FALSE)
         ORDER BY total_ms ASC, completed_at ASC
         LIMIT 10"
    );
    $board = [];
    $rank = 1;
    foreach ($rows as $row) {
        $board[] = [
            'rank' => $rank,
            'name' => $row['name'],
            'company' => $row['company'],
            'time_formatted' => formatMs(intval($row['total_ms'])),
        ];
        $rank++;
    }
    $stats = [
        'total_participants' => intval($db->scalar("SELECT COUNT(*) FROM hunt_participants")),
        'total_completed' => intval($db->scalar("SELECT COUNT(*) FROM hunt_participants WHERE completed_at IS NOT NULL")),
    ];
    Response::success(['leaderboard' => $board, 'stats' => $stats]);
}

/** First name only — the live ticker is a public display. */
function firstName($name) {
    $parts = preg_split('/\s+/', trim(strval($name)));
    return (is_array($parts) && $parts[0] !== '') ? $parts[0] : 'Hunter';
}

/**
 * Live activity feed for the big-screen leaderboard: recent scans, finishes
 * and joins (first names only, no phones), plus a "hunting right now" count.
 */
function handleActivity($db) {
    $labels = [];
    foreach (huntPosters() as $p) { $labels[$p['id']] = $p['label']; }
    $events = [];

    // ago_s is computed by MySQL (TIMESTAMPDIFF against its own NOW()) — the
    // same clock that wrote the rows. Mixing in PHP's time()/strtotime() here
    // shifts every event by the PHP↔MySQL timezone offset (looked like a
    // "fake" 6h-old feed on Hostinger).
    foreach ($db->query(
        "SELECT name, completed_at, total_ms,
                TIMESTAMPDIFF(SECOND, completed_at, NOW()) AS ago_s
         FROM hunt_participants
         WHERE completed_at IS NOT NULL AND total_ms IS NOT NULL
           AND NOT (is_suspect = TRUE AND is_verified = FALSE)
         ORDER BY completed_at DESC LIMIT 6") as $r) {
        $events[] = ['type' => 'finish', 'name' => firstName($r['name']),
                     'time' => formatMs(intval($r['total_ms'])), 'at' => $r['completed_at'],
                     'ago_s' => max(0, intval($r['ago_s']))];
    }
    foreach ($db->query(
        "SELECT s.scanned_at, s.poster_id, p.name,
                TIMESTAMPDIFF(SECOND, s.scanned_at, NOW()) AS ago_s
         FROM hunt_scans s
         JOIN hunt_participants p ON p.id = s.participant_id
         ORDER BY s.scanned_at DESC LIMIT 12") as $r) {
        $events[] = ['type' => 'scan', 'name' => firstName($r['name']),
                     'poster' => isset($labels[$r['poster_id']]) ? $labels[$r['poster_id']] : $r['poster_id'],
                     'at' => $r['scanned_at'],
                     'ago_s' => max(0, intval($r['ago_s']))];
    }
    foreach ($db->query(
        "SELECT name, registered_at,
                TIMESTAMPDIFF(SECOND, registered_at, NOW()) AS ago_s
         FROM hunt_participants
         ORDER BY registered_at DESC LIMIT 6") as $r) {
        $events[] = ['type' => 'join', 'name' => firstName($r['name']), 'at' => $r['registered_at'],
                     'ago_s' => max(0, intval($r['ago_s']))];
    }

    usort($events, function ($a, $b) { return strcmp($b['at'], $a['at']); });
    $events = array_slice($events, 0, 15);

    // Actively hunting: not completed, with a scan or registration in the last 15 min
    $huntingNow = intval($db->scalar(
        "SELECT COUNT(*) FROM hunt_participants p
         WHERE p.completed_at IS NULL
           AND (p.registered_at >= (NOW() - INTERVAL 15 MINUTE)
                OR EXISTS (SELECT 1 FROM hunt_scans s
                           WHERE s.participant_id = p.id
                             AND s.scanned_at >= (NOW() - INTERVAL 15 MINUTE)))"
    ));

    Response::success(['events' => $events, 'hunting_now' => $huntingNow]);
}

// ─── Admin actions ───────────────────────────────────────────────────

function adminRows($db) {
    $participants = $db->query(
        "SELECT id, name, phone, company, business_type, registered_at, started_at, completed_at, total_ms, is_verified, is_suspect
         FROM hunt_participants
         ORDER BY (completed_at IS NULL), total_ms ASC, completed_at ASC, registered_at ASC"
    );
    $scans = $db->query("SELECT participant_id, poster_id, scanned_at FROM hunt_scans");
    $scanMap = [];
    foreach ($scans as $s) {
        $scanMap[$s['participant_id']][$s['poster_id']] = $s['scanned_at'];
    }

    $rank = 1;
    $rows = [];
    foreach ($participants as $p) {
        $row = $p;
        $row['is_verified'] = intval($p['is_verified']);
        $row['is_suspect'] = intval($p['is_suspect']);
        $hidden = $row['is_suspect'] && !$row['is_verified'];   // off the public board
        $row['total_ms'] = $p['total_ms'] !== null ? intval($p['total_ms']) : null;
        $row['time_formatted'] = formatMs($row['total_ms']);
        $row['rank'] = ($p['completed_at'] !== null && !$hidden) ? $rank++ : null;
        $row['posters'] = [];
        foreach (huntPosterIds() as $pid) {
            $row['posters'][$pid] = isset($scanMap[$p['id']][$pid]) ? $scanMap[$p['id']][$pid] : null;
        }
        $row['scan_count'] = count(isset($scanMap[$p['id']]) ? $scanMap[$p['id']] : []);
        $rows[] = $row;
    }
    return $rows;
}

function handleAdminParticipants($db) {
    Response::success([
        'participants' => adminRows($db),
        'posters' => publicPosters(),
        'stats' => [
            'total_participants' => intval($db->scalar("SELECT COUNT(*) FROM hunt_participants")),
            'total_completed' => intval($db->scalar("SELECT COUNT(*) FROM hunt_participants WHERE completed_at IS NOT NULL")),
        ],
    ]);
}

/** Neutralize spreadsheet formula injection (=, +, -, @, tab, CR prefixes) */
function csvSafe($value) {
    $value = strval($value);
    return preg_match('/^[=+\-@\t\r]/', $value) ? "'" . $value : $value;
}

function handleAdminExport($db) {
    header('Content-Type: text/csv; charset=utf-8');
    header('Content-Disposition: attachment; filename="memehunt_participants.csv"');
    header('Cache-Control: no-cache');

    $out = fopen('php://output', 'w');
    $header = ['Rank', 'Name', 'Phone', 'Company', 'Business Type', 'Registered At', 'Started At', 'Completed At', 'Total Time', 'Verified', 'Suspect'];
    foreach (huntPosterIds() as $pid) { $header[] = $pid; }
    fputcsv($out, $header);

    foreach (adminRows($db) as $row) {
        $line = [
            $row['rank'] !== null ? $row['rank'] : '',
            csvSafe($row['name']), $row['phone'], csvSafe($row['company']), csvSafe($row['business_type']),
            $row['registered_at'], $row['started_at'], $row['completed_at'],
            $row['time_formatted'] !== null ? $row['time_formatted'] : '',
            $row['is_verified'] ? 'YES' : '',
            $row['is_suspect'] ? 'SUSPECT' : '',
        ];
        foreach (huntPosterIds() as $pid) {
            $line[] = $row['posters'][$pid] !== null ? $row['posters'][$pid] : '';
        }
        fputcsv($out, $line);
    }
    fclose($out);
    exit;
}

/** Mark a completed participant as manually verified (also clears the suspect flag). */
function handleAdminVerify($db) {
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input) Response::error('Invalid JSON body', 400);
    $id = intval($input['id'] ?? 0);
    $verified = !empty($input['verified']);
    if ($id <= 0) Response::error('Missing participant id', 400);

    if ($verified) {
        $db->execute("UPDATE hunt_participants SET is_verified = TRUE, is_suspect = FALSE WHERE id = ?", [$id]);
    } else {
        $db->execute("UPDATE hunt_participants SET is_verified = FALSE WHERE id = ?", [$id]);
        // Re-run the plausibility check so an implausible time goes back to
        // being hidden from the public leaderboard when verification is undone
        flagIfImplausible($db, $id);
    }
    Response::success(null, $verified ? 'Participant verified' : 'Verification removed');
}

/** Current dashboard-editable settings: poster labels/hints + anti-cheat floors. */
function handleAdminSettings($db) {
    $floors = huntFloors();
    $manifest = huntPosterList();
    Response::success([
        'posters' => huntPosters(),
        // What the server believes the AR build ships: 'custom' = a Unity
        // manifest is saved (poster_list holds it), 'builtin' = the hardcoded
        // defaults are live and poster_list is null.
        'poster_source' => $manifest === null ? 'builtin' : 'custom',
        'poster_list' => $manifest,
        'floors' => [
            'min_total_s' => intval($floors['total'] / 1000),
            'min_gap_s' => intval($floors['gap'] / 1000),
        ],
        'ui' => huntUi(),
        'branding' => huntBranding(),
    ]);
}

/** Save settings from the admin dashboard — live immediately, no rebuild. */
function handleAdminSaveSettings($db) {
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input) Response::error('Invalid JSON body', 400);

    // Poster list (the Unity manifest) is handled FIRST and validated before
    // anything is written: it decides which ids exist, so the label/hint
    // override map below must be filtered against the NEW list. array_key_exists
    // (not isset) so an explicit null still means "reset to built-in".
    if (array_key_exists('poster_list', $input)) {
        $raw = $input['poster_list'];
        $isClear = ($raw === null || $raw === '' || (is_array($raw) && count($raw) === 0));
        if ($isClear) {
            deleteHuntSetting($db, 'poster_list');
        } elseif (is_array($raw)) {
            $list = normalizePosterList($raw);
            if (!count($list)) {
                Response::error('No valid posters in the list — every entry needs an "id" of letters, digits, _ or - (max 64 chars)', 400);
            }
            saveHuntSetting($db, 'poster_list', $list);
            // Drop label/hint overrides for ids that no longer exist, otherwise a
            // reused id keeps its old admin-edited text and silently masks the
            // fresh hint from the new Unity manifest.
            huntPosters(true);
            $ov = huntSetting('posters');
            if (is_array($ov)) {
                $live = huntPosterIds();
                $keep = [];
                foreach ($ov as $k => $v) {
                    if (in_array(strval($k), $live, true)) { $keep[strval($k)] = $v; }
                }
                if (count($keep) !== count($ov)) { saveHuntSetting($db, 'posters', $keep); }
            }
        } else {
            Response::error('poster_list must be an array of {id, label, hint} objects', 400);
        }
        huntPosters(true);   // drop the per-request cache so the new ids apply below
    }
    if (isset($input['posters']) && is_array($input['posters'])) {
        $clean = [];
        foreach ($input['posters'] as $pid => $vals) {
            // strval: json_decode turns an all-digit JSON key into a PHP int, which
            // would fail the strict in_array and silently drop that poster's edits.
            if (!in_array(strval($pid), huntPosterIds(), true) || !is_array($vals)) { continue; }
            $clean[$pid] = [
                'label' => isset($vals['label']) ? trim(mb_substr($vals['label'], 0, 20, 'UTF-8')) : '',
                'hint' => isset($vals['hint']) ? trim(mb_substr($vals['hint'], 0, 300, 'UTF-8')) : '',
            ];
        }
        saveHuntSetting($db, 'posters', $clean);
    }
    if (isset($input['floors']) && is_array($input['floors'])) {
        saveHuntSetting($db, 'floors', [
            'min_total_s' => max(0, min(3600, intval($input['floors']['min_total_s'] ?? 60))),
            'min_gap_s' => max(0, min(600, intval($input['floors']['min_gap_s'] ?? 10))),
        ]);
    }
    if (isset($input['branding']) && is_array($input['branding'])) {
        $clean = [];
        foreach (['event_name', 'event_meta', 'brand_name', 'hunt_title', 'powered_by',
                  'primary_color', 'cta_headline', 'cta_text', 'cta_url'] as $k) {
            if (isset($input['branding'][$k]) && is_string($input['branding'][$k])) {
                $clean[$k] = trim(mb_substr($input['branding'][$k], 0, 200, 'UTF-8'));
            }
        }
        saveHuntSetting($db, 'branding', $clean);
    }
    if (isset($input['ui']) && is_array($input['ui'])) {
        // Up to 4 sponsor ads; https-only, junk entries are dropped
        $ads = [];
        if (isset($input['ui']['ads']) && is_array($input['ui']['ads'])) {
            foreach (array_slice($input['ui']['ads'], 0, 4) as $ad) {
                if (!is_array($ad)) continue;
                $img = trim(mb_substr($ad['image'] ?? '', 0, 300, 'UTF-8'));
                $lnk = trim(mb_substr($ad['link'] ?? '', 0, 300, 'UTF-8'));
                if ($img === '' || strpos($img, 'https://') !== 0) continue;
                if ($lnk !== '' && strpos($lnk, 'https://') !== 0) { $lnk = ''; }
                $ads[] = ['image' => $img, 'link' => $lnk];
            }
        }
        saveHuntSetting($db, 'ui', [
            'next_btn_delay_s' => max(0, min(60, intval($input['ui']['next_btn_delay_s'] ?? 9))),
            'sequential' => !empty($input['ui']['sequential']),
            'ads' => $ads,
        ]);
    }
    Response::success(null, 'Settings saved — live immediately');
}

/** Seed a finished player straight onto the leaderboard (demo/booth entries).
 *  Inserts a complete, pre-verified run finishing "now" with the given total
 *  time, plus evenly-spread scan rows so the admin table shows a normal 5/5.
 *  Auto-generated phones start with "00": no real device number normalizes to
 *  that, so seeded rows can't collide and are easy to spot for Remove later. */
function handleAdminAddPlayer($db) {
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input) Response::error('Invalid JSON body', 400);

    $name = trim(mb_substr($input['name'] ?? '', 0, 100, 'UTF-8'));
    if (mb_strlen($name, 'UTF-8') < 2) Response::error('Please enter a name', 400);
    $company = trim(mb_substr($input['company'] ?? '', 0, 150, 'UTF-8'));

    $totalMs = intval($input['total_ms'] ?? 0);
    if ($totalMs < 10000 || $totalMs > 86400000) {
        Response::error('Time must be between 10 seconds and 24 hours', 400);
    }

    $phone = normalizePhone($input['phone'] ?? '');
    if ($phone === '') {
        do {
            $phone = '00' . str_pad(strval(random_int(0, 99999999)), 8, '0', STR_PAD_LEFT);
        } while ($db->queryOne("SELECT id FROM hunt_participants WHERE phone = ?", [$phone]));
    } else {
        if (strlen($phone) < 8 || strlen($phone) > 15) Response::error('Please enter a valid phone number', 400);
        if ($db->queryOne("SELECT id FROM hunt_participants WHERE phone = ?", [$phone])) {
            Response::error('That phone number is already registered', 409);
        }
    }

    $token = bin2hex(random_bytes(16));
    try {
        $db->insert(
            "INSERT INTO hunt_participants
                (name, phone, company, business_type, token, consent,
                 started_at, completed_at, total_ms, is_verified, is_suspect)
             VALUES (?, ?, ?, '', ?, 1,
                 DATE_SUB(NOW(3), INTERVAL ? MICROSECOND), NOW(3), ?, TRUE, FALSE)",
            [$name, $phone, $company, $token, $totalMs * 1000, $totalMs]
        );
    } catch (Exception $e) {
        Response::error('Could not add player — phone may already be registered', 409);
    }
    $row = $db->queryOne("SELECT id FROM hunt_participants WHERE token = ?", [$token]);
    if (!$row) Response::error('Insert failed', 500);
    $pid = intval($row['id']);

    // First scan sits at started_at, last at completed_at (matching the real
    // timer semantics: first scan → fifth scan), the rest spread evenly.
    $posters = huntPosters();
    $n = count($posters);
    $i = 1;
    foreach ($posters as $p) {
        $offsetMs = ($n > 1) ? intval($totalMs * ($i - 1) / ($n - 1)) : $totalMs;
        try {
            $db->execute(
                "INSERT INTO hunt_scans (participant_id, poster_id, scanned_at)
                 VALUES (?, ?, DATE_SUB(NOW(3), INTERVAL ? MICROSECOND))",
                [$pid, strval($p['id']), ($totalMs - $offsetMs) * 1000]
            );
        } catch (Exception $e) {}
        $i++;
    }
    Response::success(null, 'Added ' . $name . ' (' . $phone . ') — on the leaderboard now');
}

/** Remove ONE participant — for staff/test runs that would take real players'
 *  prizes. Ranks are computed per request, so the leaderboard shifts up
 *  instantly; the freed phone number can register again as a fresh player. */
function handleAdminRemove($db) {
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input) Response::error('Invalid JSON body', 400);
    $id = intval($input['id'] ?? 0);
    if ($id <= 0) Response::error('Missing participant id', 400);

    $row = $db->queryOne("SELECT name FROM hunt_participants WHERE id = ?", [$id]);
    if (!$row) Response::error('Participant not found', 404);

    $db->execute("DELETE FROM hunt_participants WHERE id = ?", [$id]);   // hunt_scans cascades
    Response::success(null, 'Removed ' . $row['name'] . ' — ranks updated');
}

/** Pre-event reset: wipes ALL participants + scans (test data cleanup). */
function handleAdminReset($db) {
    $input = json_decode(file_get_contents('php://input'), true);
    if (!$input || ($input['confirm'] ?? '') !== 'RESET') {
        Response::error('Send {"confirm":"RESET"} to wipe all hunt data', 400);
    }
    $db->execute("DELETE FROM hunt_participants");   // hunt_scans cascades
    Response::success(null, 'All hunt data cleared');
}
