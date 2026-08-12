/*
 * AR Meme Hunt overlay — Bharatiya Vyapar Mahotsav 2026 (ARRISE / Rionick Studios)
 *
 * Injected into the iTracker WebGL template AFTER ar-analytics.js and BEFORE the
 * Unity loader. It intercepts image-found events by wrapping window.arAnalytics
 * (which Analytics.jslib calls from ImageTracker on every tracking acquisition),
 * so NO Unity/C# changes are required.
 *
 * Participant identity: token arrives via ?hunt_token=... (from the landing page,
 * cross-origin safe) and is persisted to localStorage.
 *
 * Config override: define window.HUNT_CONFIG = {...} before this script, or use
 * ?hunt_api=<base> for testing against a different backend.
 */
(function () {
  'use strict';

  // ─── Config ─────────────────────────────────────────────────────────
  var DEFAULTS = {
    apiBase: 'https://dashboard.rionick.com',
    landingUrl: 'https://dashboard.rionick.com/hunt/',
    leaderboardUrl: 'https://dashboard.rionick.com/hunt/leaderboard.html',
    requireRegistration: true,
    totalPosters: 5,
    // The "Next Clue" button appears this long after a successful scan —
    // long enough for the meme to play (memes are 4-8s per the campaign spec).
    // The clue and the tracking thumbnail only advance when it is tapped.
    nextBtnDelayMs: 9000,
    // Completion-screen reveal: waits for the final meme's FIRST LOOP.
    // Fallbacks: tracking lost (looked away) or doneMaxMs. Never earlier
    // than doneMinMs after the scan (tracking-jitter protection).
    doneMinMs: 3000,
    doneMaxMs: 15000,
    // Sponsor interstitials (dashboard-managed; [] = disabled). Up to 4
    // {image, link} entries that rotate on each "Next Clue" tap — never before
    // the first scan, never on the completion screen. Instantly closeable so
    // hunt times stay fair.
    ads: []
  };
  var CFG = {};
  var userCfg = window.HUNT_CONFIG || {};
  for (var k in DEFAULTS) { CFG[k] = (k in userCfg) ? userCfg[k] : DEFAULTS[k]; }

  var qs = new URLSearchParams(window.location.search);

  // ─── Analytics safety net (installed BEFORE the opt-in gate) ────────
  // Analytics.jslib calls window.arAnalytics from Unity on every tracking
  // acquisition. If ar-analytics.js failed to load, that call would throw —
  // so the shim goes in for EVERY build, hunt or not.
  if (!window.arAnalytics) {
    window.arAnalytics = {
      track: function () {}, arSessionStart: function () {},
      arImageFound: function () {}, arImageLost: function () {},
      arCtaClick: function () {}
    };
  }

  // ─── OPT-IN GATE ────────────────────────────────────────────────────
  // The Meme Hunt is a CAMPAIGN, not a feature of every AR build. The page
  // must ask for it explicitly:
  //     <script>window.HUNT_CONFIG = { enabled: true };</script>
  // Without that, this file does NOTHING — no HUD, no registration gate, no
  // network, no localStorage — so a visiting-card / poster / product build
  // shows only its own AR content. Use ?hunt=1 to force it on for testing.
  //
  // Enablement is deliberately NOT inferred from a stored hunt_token: every
  // build on the same domain shares one localStorage, so a token left over
  // from testing the hunt would light the HUD up on every other campaign —
  // which is exactly the bug this gate exists to kill.
  if (userCfg.enabled !== true && qs.get('hunt') !== '1') { return; }

  // ?hunt_api override is for local testing only — honoring it in production
  // would let a crafted link exfiltrate participant tokens to another origin.
  var isDevHost = /^(localhost|127\.0\.0\.1|192\.168\.|10\.)/.test(window.location.hostname);
  if (qs.get('hunt_api') && (isDevHost || CFG.allowApiOverride)) {
    CFG.apiBase = qs.get('hunt_api').replace(/\/+$/, '');
  }
  var API = CFG.apiBase.replace(/\/+$/, '') + '/api/controllers/hunt.php';

  var TOKEN_KEY = 'hunt_token';
  var PENDING_KEY = 'hunt_pending_scans';

  // ─── Token intake (URL param wins, then localStorage) ───────────────
  // urlToken stays in memory as a fallback: in private browsing / in-app
  // browsers localStorage.setItem can throw and the token must survive.
  var urlToken = qs.get('hunt_token') || '';
  if (urlToken) {
    try { localStorage.setItem(TOKEN_KEY, urlToken); } catch (e) {}
    try {
      qs.delete('hunt_token');
      var clean = window.location.pathname + (qs.toString() ? '?' + qs.toString() : '') + window.location.hash;
      window.history.replaceState(null, '', clean);
    } catch (e) {}
  }
  function getToken() {
    try { return localStorage.getItem(TOKEN_KEY) || urlToken; } catch (e) { return urlToken; }
  }

  // ─── State ──────────────────────────────────────────────────────────
  var state = {
    posters: [],          // [{id,label}] from server (fallback: built-in)
    scanned: {},          // id -> true
    count: 0,
    total: CFG.totalPosters,
    started: false,
    completed: false,
    timerBase: null,      // Date.now() - elapsed_ms
    active: false,        // token present + status ok
    nextHint: null,       // latest {id,label,hint} — clue stays recoverable
    name: ''              // participant name (for the victory card)
  };
  // Offline/timeout fallback ONLY — boot()'s .catch path uses these when the status
  // call fails or hits the 8s timeout (routine on venue Wi-Fi). They MUST match the
  // ids the current build actually ships, or a phone on bad Wi-Fi silently records
  // nothing: onImageFound() drops any id not in this list before it is ever sent.
  // Keep in step with Tools ▸ Meme Hunt ▸ 3. Copy Poster List JSON.
  var FALLBACK_POSTERS = [
    { id: 'Target1', label: 'Target 1' },
    { id: 'Target2', label: 'Target 2' },
    { id: 'Target3', label: 'Target 3' },
    { id: 'Target4', label: 'Target 4' },
    { id: 'Target5', label: 'Target 5' }
  ];

  // ─── arAnalytics wrap (must happen synchronously at load) ───────────
  // The shim itself is installed above, before the opt-in gate.
  var origFound = window.arAnalytics.arImageFound.bind(window.arAnalytics);
  window.arAnalytics.arImageFound = function (id) {
    try { onImageFound(String(id)); } catch (e) { console.error('[Hunt]', e); }
    return origFound(id);
  };
  var origLost = window.arAnalytics.arImageLost.bind(window.arAnalytics);
  window.arAnalytics.arImageLost = function (id) {
    try { onImageLost(String(id)); } catch (e) { console.error('[Hunt]', e); }
    return origLost(id);
  };

  // ─── Networking ─────────────────────────────────────────────────────
  // 8s timeout: on congested venue Wi-Fi a HUNG request must not freeze the
  // hunt state machine — timeouts fall through to the offline/queue paths.
  function api(action, method, body) {
    var controller = typeof AbortController !== 'undefined' ? new AbortController() : null;
    var timeoutId = controller ? setTimeout(function () { controller.abort(); }, 8000) : null;
    return fetch(API + '?action=' + action, {
      method: method || 'GET',
      headers: { 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : undefined,
      signal: controller ? controller.signal : undefined
    }).then(function (r) {
      if (timeoutId) { clearTimeout(timeoutId); }
      return r.json().then(function (j) { j.__status = r.status; return j; });
    }, function (err) {
      if (timeoutId) { clearTimeout(timeoutId); }
      throw err;
    });
  }

  function loadPending() {
    try { return JSON.parse(localStorage.getItem(PENDING_KEY) || '[]'); } catch (e) { return []; }
  }
  function savePending(arr) {
    try { localStorage.setItem(PENDING_KEY, JSON.stringify(arr)); } catch (e) {}
  }
  function queueScan(posterId) {
    var pending = loadPending();
    if (pending.indexOf(posterId) === -1) { pending.push(posterId); savePending(pending); }
  }
  function dropPending(posterId) {
    savePending(loadPending().filter(function (p) { return p !== posterId; }));
  }
  function flushPending() {
    var pending = loadPending();
    if (!pending.length || !getToken()) { return; }
    var posterId = pending[0];
    api('scan', 'POST', { token: getToken(), poster_id: posterId }).then(function (res) {
      if (res && res.success) {
        dropPending(posterId);
        applyState(res.data, false);
        if (loadPending().length) { flushPending(); }
      } else if (res && res.success === false) {
        // Definitive server rejection (bad poster id, invalid token, ...):
        // retrying forever would wedge a fake green chip and block completion.
        dropPending(posterId);
        delete state.scanned[posterId];
        state.count = Object.keys(state.scanned).length;
        renderChips();
        toast((res.message || 'Scan rejected') + ' — please scan ' + labelOf(posterId) + ' again', 3500);
      }
      // No-success-field responses (proxy error pages etc.): keep retrying.
    }).catch(function () { /* network failure — retry on next interval */ });
  }
  setInterval(flushPending, 5000);

  // ─── Scan handling ──────────────────────────────────────────────────
  var inFlight = {};
  var earlyFound = [];      // found-events that arrived before boot() settled
  var bootSettled = false;

  function onImageFound(id) {
    if (state.completed) { return; }
    if (!state.active) {
      // A target can be tracked before the status fetch settles (fast Unity
      // boot + slow network). Buffer it — boot() replays these.
      if (getToken() && !bootSettled && earlyFound.indexOf(id) === -1) {
        earlyFound.push(id);
      }
      return;
    }
    var posters = state.posters.length ? state.posters : FALLBACK_POSTERS;
    var known = posters.some(function (p) { return p.id === id; });
    if (!known) { return; }
    if (state.scanned[id]) {
      toast('✓ ' + labelOf(id) + ' already scanned — find the next poster!', 2600);
      return;
    }
    if (inFlight[id]) { return; }
    inFlight[id] = true;
    toast('Scanning ' + labelOf(id) + '…', 1400);
    api('scan', 'POST', { token: getToken(), poster_id: id }).then(function (res) {
      inFlight[id] = false;
      if (res && res.success) {
        applyState(res.data, true);
      } else if (res && res.success === false) {
        // Definitive rejection — do NOT tick the chip
        toast(res.message || 'Scan rejected — please try again', 3000);
      } else {
        // Unexpected body (e.g. DB-error JSON without a success field): treat
        // as transient — queue like a network failure
        queueAndTick(id);
      }
    }).catch(function () {
      inFlight[id] = false;
      queueAndTick(id);
    });
  }

  // Offline / transient failure: queue and retry — the server timestamp will be
  // the arrival time, which keeps the competition fair.
  function queueAndTick(id) {
    queueScan(id);
    state.scanned[id] = true;   // optimistic tick so the HUD moves on
    state.count = Object.keys(state.scanned).length;
    renderChips();
    toast('✓ ' + labelOf(id) + ' saved — syncing when network returns', 3200);
  }

  function replayEarlyFound() {
    bootSettled = true;
    var buffered = earlyFound.slice();
    earlyFound = [];
    buffered.forEach(function (id) { onImageFound(id); });
  }

  // ─── Deferred reveal: let the meme play its first loop ──────────────
  // After a successful scan the hint (or completion screen) waits for the
  // meme video to finish its FIRST loop. Fallbacks: tracking lost (looked
  // away) or maxMs. Never earlier than minMs after the scan.

  // Track the most recent content <video> Unity plays. Unity's video element
  // may be detached from the DOM, so intercept play() — the same technique the
  // template's iOS sound gate uses (patches compose: it wraps ours later).
  var latestContentVideo = null;
  (function () {
    try {
      var origPlay = HTMLMediaElement.prototype.play;
      HTMLMediaElement.prototype.play = function () {
        try {
          if (this.tagName === 'VIDEO' && this.id !== 'webcam-video') {
            latestContentVideo = this;
          }
        } catch (e) {}
        return origPlay.apply(this, arguments);
      };
    } catch (e) {}
  })();

  function findContentVideo() {
    if (latestContentVideo && !latestContentVideo.paused) { return latestContentVideo; }
    var vids = document.querySelectorAll('video');
    for (var i = 0; i < vids.length; i++) {
      if (vids[i].id !== 'webcam-video' && !vids[i].paused && vids[i].currentTime > 0) {
        return vids[i];
      }
    }
    return null;
  }

  var pendingReveal = null;   // {fn, posterId, minAt, timerId, pollId, videoEl, onTime, onEnded}

  function detachVideoWatch(reveal) {
    if (reveal.pollId) { clearInterval(reveal.pollId); reveal.pollId = null; }
    if (reveal.videoEl) {
      reveal.videoEl.removeEventListener('timeupdate', reveal.onTime);
      reveal.videoEl.removeEventListener('ended', reveal.onEnded);
      reveal.videoEl = null;
    }
  }
  function cancelReveal() {
    if (pendingReveal) {
      clearTimeout(pendingReveal.timerId);
      detachVideoWatch(pendingReveal);
      pendingReveal = null;
    }
  }
  function fireReveal() {
    if (!pendingReveal) { return; }
    var fn = pendingReveal.fn;
    cancelReveal();
    fn();
  }
  // Move the reveal up to "now or minAt, whichever is later"
  function revealSoon(reveal) {
    if (pendingReveal !== reveal) { return; }
    var wait = Math.max(0, reveal.minAt - Date.now());
    clearTimeout(reveal.timerId);
    reveal.timerId = setTimeout(fireReveal, wait);
    detachVideoWatch(reveal);
  }
  // Primary trigger: the poster's video wraps back to the start (loop done)
  // or ends. The video may start a moment after the scan (buffering), so poll
  // briefly until it is playing before attaching listeners.
  function watchVideoFirstLoop(reveal) {
    var attempts = 0;
    reveal.pollId = setInterval(function () {
      var v = findContentVideo();
      if (!v) {
        if (++attempts > 12) { clearInterval(reveal.pollId); reveal.pollId = null; }
        return;
      }
      clearInterval(reveal.pollId);
      reveal.pollId = null;
      reveal.videoEl = v;
      var lastT = v.currentTime;
      reveal.onTime = function () {
        if (v.currentTime + 0.5 < lastT) { revealSoon(reveal); }   // wrapped -> first loop complete
        else { lastT = Math.max(lastT, v.currentTime); }
      };
      reveal.onEnded = function () { revealSoon(reveal); };
      v.addEventListener('timeupdate', reveal.onTime);
      v.addEventListener('ended', reveal.onEnded);
    }, 400);
  }
  function scheduleReveal(posterId, minMs, maxMs, fn) {
    cancelReveal();
    pendingReveal = { fn: fn, posterId: posterId, minAt: Date.now() + minMs };
    pendingReveal.timerId = setTimeout(fireReveal, maxMs);
    watchVideoFirstLoop(pendingReveal);
  }
  function onImageLost(id) {
    if (pendingReveal && pendingReveal.posterId === id) { revealSoon(pendingReveal); }
  }

  function labelOf(id) {
    for (var i = 0; i < state.posters.length; i++) {
      if (state.posters[i].id === id) { return state.posters[i].label; }
    }
    return id;
  }

  // ─── ID mismatch detection ──────────────────────────────────────────
  // The poster ids exist in TWO places that must agree: the ids baked into this
  // build (PostProcessBuild writes them as <imagetarget id='…'> tags) and the
  // server's list. When they disagree the failure is SILENT — the camera tracks
  // the poster and the meme plays, but the scan is rejected and the chip never
  // ticks. So compare them once and say so loudly in the console.
  var mismatchChecked = false;
  function checkPosterIds(serverPosters) {
    if (mismatchChecked || !serverPosters || !serverPosters.length) { return; }
    mismatchChecked = true;
    var inBuild = [];
    try {
      document.querySelectorAll('imagetarget').forEach(function (t) {
        var id = t.getAttribute('id');
        if (id) { inBuild.push(id); }
      });
    } catch (e) { return; }
    if (!inBuild.length) { return; }   // nothing to compare against

    var missing = serverPosters
      .map(function (p) { return p.id; })
      .filter(function (id) { return inBuild.indexOf(id) === -1; });
    if (missing.length) {
      console.error(
        '[Hunt] POSTER ID MISMATCH — the server expects ids this AR build does not have: ' +
        missing.join(', ') + '\n' +
        '       ids in this build: ' + inBuild.join(', ') + '\n' +
        '       Those posters can NEVER be scanned (the meme plays, the scan is rejected).\n' +
        '       Fix: Unity ▸ Tools ▸ Meme Hunt ▸ 3. Copy Poster List JSON, then paste it into\n' +
        '       hunt/admin.html ▸ Settings ▸ Poster list.');
    }
  }

  function applyState(data, announce) {
    if (!data) { return; }
    if (data.posters && data.posters.length) { state.posters = data.posters; checkPosterIds(data.posters); }
    state.total = data.total || state.total;
    state.scanned = {};
    (data.scanned || []).forEach(function (id) { state.scanned[id] = true; });
    // keep optimistic (queued) ticks
    loadPending().forEach(function (id) { state.scanned[id] = true; });
    state.count = Object.keys(state.scanned).length;
    state.started = !!data.started;
    state.completed = !!data.completed;
    if (typeof data.elapsed_ms === 'number') {
      state.timerBase = Date.now() - data.elapsed_ms;
    }
    if (data.name) { state.name = data.name; }
    if (data.next) { state.nextHint = data.next; }
    if (data.completed) { state.nextHint = null; }
    // Server-tunable UI settings (hunt/admin.html → Settings) override defaults
    if (data.ui) {
      if (data.ui.branding) { applyBranding(data.ui.branding); }
      if (typeof data.ui.next_btn_delay_s === 'number') {
        CFG.nextBtnDelayMs = Math.max(0, data.ui.next_btn_delay_s) * 1000;
      }
      if (Object.prototype.toString.call(data.ui.ads) === '[object Array]') {
        CFG.ads = data.ui.ads;
      } else if (typeof data.ui.ad_image_url === 'string' && data.ui.ad_image_url) {
        // older backend: single-ad fields
        CFG.ads = [{ image: data.ui.ad_image_url, link: data.ui.ad_link_url || '' }];
      }
      preloadAds();
    }
    // Freeze the tracking thumbnail BEFORE renderChips/updatePeek run, so a
    // live scan doesn't advance it — that happens only on "Next Clue" / ✕.
    if (announce && !data.duplicate && data.poster_id && !data.completed && data.next) {
      peekHold = true;
      // The frozen thumb shows the poster just FOUND — relabel accordingly
      if (peekLabel) { peekLabel.textContent = peekCaption(peekEl && peekEl.classList.contains('big')); }
    }
    renderChips();

    if (state.completed) {
      if (announce && !data.duplicate && data.poster_id) {
        // 5th poster just scanned live: instant feedback, then let the final
        // meme play before the completion screen takes over. Kill any pending
        // Next-Clue state from the previous poster (fast back-to-back scans).
        clearTimeout(nextBtnTimer);
        setNextBtnVisible(false);
        pendingNext = null;
        hintCard('🎉 ' + data.total + '/' + data.total, 'Challenge complete — enjoy the last meme! 🎬', true);
        scheduleReveal(data.poster_id, CFG.doneMinMs, CFG.doneMaxMs, function () {
          showCompletion(data);
        });
      } else {
        showCompletion(data);
      }
      return;
    }
    if (announce) {
      if (data.duplicate) {
        toast('✓ Already counted — next poster!', 2600);
      } else if (data.next) {
        // Enjoy phase: the card celebrates the scan while the meme plays and
        // the tracking thumbnail stays frozen on the poster just scanned.
        // After nextBtnDelayMs a "Next Clue" button appears — only tapping it
        // (or ✕) reveals the clue and advances the thumbnail. Nothing changes
        // on its own.
        pendingNext = { count: data.count, total: data.total, hint: data.next.hint };
        hintCard('✓ ' + data.count + '/' + data.total, 'Enjoy the meme! 🎬', true);
        setNextBtnVisible(false);
        clearTimeout(nextBtnTimer);
        nextBtnTimer = setTimeout(function () { setNextBtnVisible(true); }, CFG.nextBtnDelayMs);
      }
    }
  }

  // ─── Branding (white-label; admin → ui.branding overrides live) ─────
  var BR = {
    eventName: 'Bharatiya Vyapar Mahotsav 2026',
    brandName: 'AR|RISE',        // part after | renders in the accent color
    huntTitle: 'AR Meme Hunt',
    poweredBy: 'ARRISE / RIONICK STUDIOS',
    primary: '#dc1e1e', accent: '#ff4444', dark: '#aa1111', light: '#ff5555'
  };
  function hexRgb(h) {
    var m = /^#([0-9a-f]{6})$/i.exec(String(h || ''));
    if (!m) { return null; }
    var n = parseInt(m[1], 16);
    return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
  }
  function mixHex(hex, target, t) {
    var a = hexRgb(hex), b = hexRgb(target);
    if (!a || !b) { return hex; }
    function ch(x, y) { return Math.round(x + (y - x) * t); }
    return '#' + ((1 << 24) + (ch(a.r, b.r) << 16) + (ch(a.g, b.g) << 8) + ch(a.b, b.b)).toString(16).slice(1);
  }
  function brandHtml(name) {
    var esc = function (s) { return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); };
    var i = String(name).indexOf('|');
    if (i === -1) { return '<b>' + esc(String(name)) + '</b>'; }
    return esc(name.slice(0, i)) + '<b>' + esc(name.slice(i + 1)) + '</b>';
  }
  function applyBranding(b) {
    if (!b || !root) { return; }
    if (b.event_name) { BR.eventName = b.event_name; }
    if (b.brand_name) { BR.brandName = b.brand_name; }
    if (b.hunt_title) { BR.huntTitle = b.hunt_title; }
    if (b.powered_by) { BR.poweredBy = b.powered_by; }
    if (b.primary_color && hexRgb(b.primary_color)) {
      BR.primary = b.primary_color;
      BR.accent = mixHex(BR.primary, '#ffffff', 0.22);
      BR.dark = mixHex(BR.primary, '#000000', 0.3);
      BR.light = mixHex(BR.primary, '#ffffff', 0.35);
    }
    var rgb = hexRgb(BR.primary);
    root.style.setProperty('--hb', BR.primary);
    root.style.setProperty('--hbr', rgb.r + ',' + rgb.g + ',' + rgb.b);
    root.style.setProperty('--hba', BR.accent);
    root.style.setProperty('--hbd', BR.dark);
    root.style.setProperty('--hbl', BR.light);
    document.getElementById('hunt-gate-brand').innerHTML = brandHtml(BR.brandName);
    document.getElementById('hunt-done-brand').innerHTML = brandHtml(BR.brandName);
    document.getElementById('hunt-gate-title').textContent = BR.huntTitle;
    document.getElementById('hunt-done-msg').textContent =
      'Congratulations! You completed the ' + BR.eventName + ' ' + BR.huntTitle + '.';
  }

  // ─── UI ─────────────────────────────────────────────────────────────
  var root, chipsEl, timerEl, toastEl, toastTimer, hintEl, gateEl, doneEl;
  var peekEl, peekBackdrop, peekImg, peekLabel, peekHintEl;
  var adEl, adImgEl, adPreloaded = {}, adShownCount = 0, currentAdLink = '';
  var posterImages = {};       // target id -> image URL (from <imagetarget> tags)
  var currentPeekLabel = '';
  var nextBtnEl, nextBtnTimer = null;
  var pendingNext = null;      // {count,total,hint} waiting behind "Next Clue"
  var peekHold = false;        // true = thumbnail frozen until Next Clue / ✕

  function buildUI() {
    root = document.createElement('div');
    root.id = 'hunt-root';
    root.innerHTML =
      '<style>' +
      // z-index 90: above the AR canvases, but BELOW the template dialogs
      // (.ctaDiv 99: error/sound-unlock) and the boot loader (999), so template
      // errors are never hidden behind hunt overlays. top/right/bottom/left
      // instead of inset: iOS Safari < 14.5 does not support inset.
      // overflow:hidden — no overlay element may ever paint outside the screen box
      // Brand color vars (--hb primary, --hbr its r,g,b triplet, --hba bright,
      // --hbd dark, --hbl light text) — overridden live by applyBranding()
      '  #hunt-root { --hb:#dc1e1e; --hbr:220,30,30; --hba:#ff4444; --hbd:#aa1111; --hbl:#ff5555; position:fixed; top:0; right:0; bottom:0; left:0; pointer-events:none; z-index:90; overflow:hidden; font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif; }' +
      '  #hunt-top { position:absolute; top:calc(10px + env(safe-area-inset-top)); left:0; right:0; display:none; justify-content:center; gap:10px; align-items:center; }' +
      '  #hunt-timer, #hunt-count { background:rgba(8,8,8,0.72); border:1px solid rgba(var(--hbr),0.45); color:#fff; border-radius:20px; padding:6px 14px; font-size:12.5px; font-weight:700; letter-spacing:0.08em; font-variant-numeric:tabular-nums; }' +
      '  #hunt-count b { color:var(--hbl); }' +
      '  #hunt-chips { position:absolute; bottom:calc(18px + env(safe-area-inset-bottom)); left:0; right:0; display:none; justify-content:center; gap:7px; padding:0 10px; flex-wrap:wrap; }' +
      '  .hunt-chip { background:rgba(8,8,8,0.72); border:1px solid rgba(255,255,255,0.18); color:rgba(255,255,255,0.55); border-radius:16px; padding:6px 11px; font-size:11px; font-weight:600; letter-spacing:0.06em; text-transform:uppercase; }' +
      '  .hunt-chip.done { border-color:rgba(95,208,106,0.7); color:#5fd06a; }' +
      '  #hunt-toast { position:absolute; top:calc(56px + env(safe-area-inset-top)); left:50%; transform:translateX(-50%); background:rgba(8,8,8,0.85); border:1px solid rgba(var(--hbr),0.5); color:#fff; border-radius:12px; padding:10px 16px; font-size:12.5px; max-width:86vw; text-align:center; display:none; line-height:1.5; }' +
      // Hint = compact bottom sheet above the chips — never covers the AR view
      // left+right anchoring + margin:auto centers the sheet without transform-X,
      // so it can never be clipped at a screen edge; box-sizing keeps the padding
      // inside the width on every browser
      '  #hunt-hint { position:absolute; left:12px; right:12px; margin:0 auto; max-width:380px; box-sizing:border-box; bottom:calc(64px + env(safe-area-inset-bottom)); transform:translateY(16px); background:rgba(8,8,8,0.9); border:1px solid rgba(var(--hbr),0.55); border-radius:14px; padding:13px 40px 13px 16px; text-align:left; display:none; opacity:0; transition:opacity 0.3s ease, transform 0.3s ease; pointer-events:auto; }' +
      '  #hunt-hint.show { opacity:1; transform:translateY(0); }' +
      '  #hunt-hint .hp { color:var(--hbl); font-size:10px; font-weight:800; letter-spacing:0.2em; text-transform:uppercase; margin-bottom:5px; }' +
      '  #hunt-hint .ht { color:#fff; font-size:13px; line-height:1.55; }' +
      '  #hunt-hint button, #hunt-gate a, #hunt-gate button, #hunt-done a { pointer-events:auto; -webkit-tap-highlight-color:transparent; }' +
      '  #hunt-hint .hx { position:absolute; top:6px; right:6px; width:28px; height:28px; background:rgba(255,255,255,0.08); color:rgba(255,255,255,0.6); border:none; border-radius:50%; font-size:13px; line-height:28px; padding:0; }' +
      '  #hunt-hint .nxt { display:none; margin-top:11px; background:linear-gradient(135deg,var(--hba),var(--hbd)); color:#fff; border:none; border-radius:10px; font-size:11.5px; font-weight:700; letter-spacing:0.12em; text-transform:uppercase; padding:11px 24px; -webkit-tap-highlight-color:transparent; }' +
      '  #hunt-hint .nxt.on { display:inline-block; }' +
      // Compact single-row variant for the enjoy phase — half the height:
      // progress + text on the left, the Next Clue pill on the right
      '  #hunt-hint.compact { display:flex; align-items:center; gap:10px; padding:9px 14px; }' +
      '  #hunt-hint.compact .hp { margin:0; white-space:nowrap; }' +
      '  #hunt-hint.compact .ht { flex:1; font-size:12px; min-width:0; }' +
      '  #hunt-hint.compact .nxt { margin:0; padding:9px 13px; font-size:10px; letter-spacing:0.08em; white-space:nowrap; }' +
      // No ✕ on the enjoy-phase card — the only way forward is Next Clue,
      // so a mis-tap can never skip the clue
      '  #hunt-hint.compact .hx { display:none; }' +
      '  #hunt-gate, #hunt-done { position:absolute; top:0; right:0; bottom:0; left:0; background:rgba(8,8,8,0.94); display:none; flex-direction:column; align-items:center; justify-content:center; text-align:center; padding:30px 24px; pointer-events:auto; z-index:30; }' +
      // Completion confetti — content stays above it via relative positioning
      '  #hunt-done > * { position:relative; }' +
      '  #hunt-confetti { position:absolute; top:0; right:0; bottom:0; left:0; overflow:hidden; pointer-events:none; }' +
      '  .hunt-cf { position:absolute; top:-14px; width:8px; height:13px; border-radius:2px; opacity:0; animation-name:huntCfFall; animation-timing-function:linear; animation-iteration-count:2; }' +
      '  @keyframes huntCfFall { 0% { opacity:1; transform:translateY(-14px) rotate(0deg); } 85% { opacity:1; } 100% { opacity:0; transform:translateY(102vh) rotate(680deg); } }' +
      // Draggable "find this poster" preview thumbnail
      '  #hunt-peek { position:absolute; width:82px; background:rgba(8,8,8,0.88); border:1px solid rgba(var(--hbr),0.55); border-radius:12px; overflow:hidden; display:none; pointer-events:auto; touch-action:none; z-index:5; box-shadow:0 4px 14px rgba(0,0,0,0.4); }' +
      '  #hunt-peek img { display:block; width:100%; height:82px; object-fit:cover; pointer-events:none; -webkit-user-drag:none; user-select:none; -webkit-user-select:none; }' +
      '  #hunt-peek .pk { font-size:8.5px; letter-spacing:0.1em; text-transform:uppercase; color:var(--hbl); font-weight:800; text-align:center; padding:4px 4px 5px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }' +
      // plain width first = fallback for browsers without CSS min(); max-width
      // guarantees the enlarged card always fits inside the screen
      '  #hunt-peek.big { left:50% !important; top:50% !important; transform:translate(-50%,-50%); width:288px; width:min(80vw,320px); max-width:calc(100vw - 24px); z-index:20; }' +
      '  #hunt-peek.big img { height:auto; max-height:55vh; object-fit:contain; background:#000; }' +
      '  #hunt-peek.big .pk { font-size:11px; padding:10px 10px 4px; white-space:normal; }' +
      // Clue text inside the enlarged thumbnail — the clue is always
      // recoverable here even if the hint card was closed by mistake
      '  #hunt-peek .pkhint { display:none; }' +
      '  #hunt-peek.big .pkhint { display:block; color:rgba(255,255,255,0.75); font-size:12.5px; line-height:1.55; text-align:center; padding:0 14px 13px; }' +
      '  #hunt-peek-backdrop { position:absolute; top:0; right:0; bottom:0; left:0; background:rgba(0,0,0,0.65); display:none; pointer-events:auto; z-index:15; }' +
      // Sponsor interstitial — above hunt UI (z 40) but below the template
      // dialogs (.ctaDiv 99 lives outside #hunt-root), instantly closeable
      '  #hunt-spot { position:absolute; top:0; right:0; bottom:0; left:0; background:rgba(8,8,8,0.92); display:none; flex-direction:column; align-items:center; justify-content:center; padding:24px; pointer-events:auto; z-index:40; }' +
      '  #hunt-spot .splbl { color:rgba(255,255,255,0.35); font-size:9px; letter-spacing:0.3em; text-transform:uppercase; margin-bottom:12px; }' +
      '  #hunt-spot img { max-width:88vw; max-height:64vh; border-radius:14px; border:1px solid rgba(255,255,255,0.15); -webkit-user-drag:none; }' +
      '  #hunt-spot .sptap { color:rgba(255,255,255,0.3); font-size:10.5px; margin-top:12px; }' +
      '  #hunt-spot-close { position:absolute; top:calc(14px + env(safe-area-inset-top)); right:14px; width:38px; height:38px; border-radius:50%; background:rgba(255,255,255,0.1); color:#fff; border:1px solid rgba(255,255,255,0.3); font-size:15px; line-height:36px; padding:0; -webkit-tap-highlight-color:transparent; }' +
      '  .hunt-brand { letter-spacing:0.38em; font-weight:300; font-size:20px; text-transform:uppercase; color:#fff; }' +
      '  .hunt-brand b { color:var(--hb); font-weight:700; }' +
      '  .hunt-h { color:#fff; font-size:21px; font-weight:800; letter-spacing:0.06em; margin:16px 0 8px; }' +
      '  .hunt-p { color:rgba(255,255,255,0.55); font-size:13px; line-height:1.7; max-width:300px; }' +
      '  .hunt-btn { display:inline-block; margin-top:20px; background:linear-gradient(135deg,var(--hba),var(--hbd)); color:#fff; border:none; border-radius:12px; font-size:13px; font-weight:700; letter-spacing:0.12em; text-transform:uppercase; padding:15px 30px; text-decoration:none; }' +
      '  .hunt-resume { display:inline-block; margin-top:18px; color:rgba(255,255,255,0.75); font-size:13px; font-weight:600; text-decoration:underline; text-underline-offset:3px; pointer-events:auto; -webkit-tap-highlight-color:transparent; }' +
      '  .hunt-skip { display:inline-block; margin-top:14px; color:rgba(255,255,255,0.35); font-size:11.5px; text-decoration:underline; background:none; border:none; }' +
      '  #hunt-done .big-time { color:var(--hb); font-size:42px; font-weight:100; margin:6px 0 0; font-variant-numeric:tabular-nums; }' +
      '  #hunt-done .rank { color:rgba(255,255,255,0.7); font-size:14px; margin-bottom:2px; }' +
      // Victory card preview + share (WhatsApp viral loop)
      '  #hunt-vc-preview { display:none; width:min(44vw,180px); border-radius:12px; border:1px solid rgba(255,255,255,0.25); margin:10px 0 0; box-shadow:0 8px 28px rgba(0,0,0,0.55); pointer-events:auto; }' +
      '  #hunt-done-lb { background:rgba(255,255,255,0.12); }' +
      '  #hunt-done { overflow-y:auto; }' +
      '</style>' +
      '<div id="hunt-top"><div id="hunt-count"><b>0</b>/5</div><div id="hunt-timer">00:00</div></div>' +
      '<div id="hunt-chips"></div>' +
      '<div id="hunt-peek-backdrop"></div>' +
      '<div id="hunt-peek"><img id="hunt-peek-img" alt="Next poster"><div class="pk" id="hunt-peek-label"></div><div class="pkhint" id="hunt-peek-hint"></div></div>' +
      '<div id="hunt-spot"><button id="hunt-spot-close" aria-label="Close">✕</button><div class="splbl">Sponsored</div><img id="hunt-spot-img" alt="Sponsor"><div class="sptap" id="hunt-spot-tap"></div></div>' +
      '<div id="hunt-toast"></div>' +
      '<div id="hunt-hint"><button id="hunt-hint-ok" class="hx" aria-label="Close">✕</button><div class="hp" id="hunt-hint-p"></div><div class="ht" id="hunt-hint-t"></div><button id="hunt-hint-next" class="nxt">Next Clue ▸</button></div>' +
      '<div id="hunt-gate">' +
      '  <div class="hunt-brand" id="hunt-gate-brand">AR<b>RISE</b></div>' +
      '  <div class="hunt-h" id="hunt-gate-title">AR Meme Hunt</div>' +
      '  <p class="hunt-p">Find 5 posters. Scan them all. Top 3 win prizes. Register first to join the challenge!</p>' +
      '  <a class="hunt-btn" id="hunt-gate-btn">Register To Play</a>' +
      '  <a class="hunt-resume" id="hunt-gate-resume">Already registered? Resume your hunt →</a>' +
      '  <button class="hunt-skip" id="hunt-gate-skip">Continue without the hunt</button>' +
      '</div>' +
      '<div id="hunt-done">' +
      '  <div class="hunt-brand" id="hunt-done-brand">AR<b>RISE</b></div>' +
      '  <div class="hunt-h">Challenge Complete!</div>' +
      '  <p class="hunt-p" id="hunt-done-msg">Congratulations! You completed the Bharatiya Vyapar Mahotsav AR Meme Hunt.</p>' +
      '  <div class="big-time" id="hunt-done-time">--:--</div>' +
      '  <div class="rank" id="hunt-done-rank"></div>' +
      '  <img id="hunt-vc-preview" alt="My victory card">' +
      '  <a class="hunt-btn" id="hunt-vc-share">📲 Share Victory Card</a>' +
      '  <a class="hunt-btn" id="hunt-done-lb">View Leaderboard</a>' +
      '  <button class="hunt-skip" id="hunt-done-close">Keep exploring the AR experience</button>' +
      '</div>';
    document.body.appendChild(root);

    chipsEl = document.getElementById('hunt-chips');
    timerEl = document.getElementById('hunt-timer');
    toastEl = document.getElementById('hunt-toast');
    hintEl = document.getElementById('hunt-hint');
    gateEl = document.getElementById('hunt-gate');
    doneEl = document.getElementById('hunt-done');
    peekEl = document.getElementById('hunt-peek');
    peekBackdrop = document.getElementById('hunt-peek-backdrop');
    peekImg = document.getElementById('hunt-peek-img');
    peekLabel = document.getElementById('hunt-peek-label');
    peekHintEl = document.getElementById('hunt-peek-hint');

    // Poster preview images ship in every build: PostProcessBuild injects
    // <imagetarget id src> tags pointing at targets/<file>
    document.querySelectorAll('imagetarget').forEach(function (t) {
      var tid = t.getAttribute('id');
      var src = t.getAttribute('src');
      if (tid && src) { posterImages[tid] = src; }
    });
    initPeekInteractions();

    document.getElementById('hunt-hint-ok').addEventListener('click', hideHint);
    adEl = document.getElementById('hunt-spot');
    adImgEl = document.getElementById('hunt-spot-img');
    document.getElementById('hunt-spot-close').addEventListener('click', hideAd);
    adEl.addEventListener('click', hideAd);   // tap anywhere outside the image closes
    adImgEl.addEventListener('click', function (e) {
      e.stopPropagation();
      if (currentAdLink) { window.open(currentAdLink, '_blank'); }
    });
    nextBtnEl = document.getElementById('hunt-hint-next');
    nextBtnEl.addEventListener('click', advanceToClue);
    document.getElementById('hunt-gate-btn').setAttribute('href', CFG.landingUrl);
    // #resume deep-link: the landing page opens its resume form directly
    document.getElementById('hunt-gate-resume').setAttribute('href', CFG.landingUrl + '#resume');
    document.getElementById('hunt-gate-skip').addEventListener('click', function () {
      gateEl.style.display = 'none';
    });
    // Carry the participant token to the leaderboard (different domain, so
    // localStorage doesn't transfer) — enables its "Your Rank" card
    var lbUrl = CFG.leaderboardUrl;
    if (getToken()) {
      lbUrl += (lbUrl.indexOf('?') === -1 ? '?' : '&') + 'hunt_token=' + encodeURIComponent(getToken());
    }
    document.getElementById('hunt-done-lb').setAttribute('href', lbUrl);
    document.getElementById('hunt-vc-share').addEventListener('click', shareVictoryCard);
    document.getElementById('hunt-vc-preview').addEventListener('click', shareVictoryCard);
    document.getElementById('hunt-done-close').addEventListener('click', function () {
      doneEl.style.display = 'none';
    });
  }

  // ─── "Find this poster" preview: draggable thumb, tap to enlarge ────
  var peekPos = null;
  var peekDrag = null;
  var suppressPeekClick = false;

  // v2 key: resets positions saved before the corner-default change
  var PEEK_POS_KEY = 'hunt_peek_pos2';
  function loadPeekPos() {
    try { return JSON.parse(localStorage.getItem(PEEK_POS_KEY) || 'null'); } catch (e) { return null; }
  }
  function clampPeekPos() {
    var w = peekEl.offsetWidth || 82;
    var h = peekEl.offsetHeight || 104;
    // No keep-out zones — the participant may park the thumbnail anywhere on
    // the screen. Only stop it leaving the viewport entirely (it would become
    // unreachable: the overlay root clips at the screen edges).
    peekPos.x = Math.min(Math.max(0, peekPos.x), Math.max(0, window.innerWidth - w));
    peekPos.y = Math.min(Math.max(0, peekPos.y), Math.max(0, window.innerHeight - h));
  }
  // Default: tucked into the top-left corner, on the same line as the HUD
  // pills (y accounts for the notch/safe-area, measured in initPeekInteractions)
  var PEEK_DEFAULT = { x: 8, y: 18 };
  function applyPeekPos() {
    if (!peekPos) { peekPos = loadPeekPos() || { x: PEEK_DEFAULT.x, y: PEEK_DEFAULT.y }; }
    clampPeekPos();
    peekEl.style.left = peekPos.x + 'px';
    peekEl.style.top = peekPos.y + 'px';
  }
  // Caption for the thumbnail — accounts for the enjoy-phase hold, where the
  // thumb still shows the poster that was just FOUND (not the next target)
  function peekCaption(big) {
    if (peekHold) {
      return big ? '✓ ' + currentPeekLabel + ' found! Tap Next Clue for your next target'
                 : '✓ ' + currentPeekLabel + ' found!';
    }
    return big ? 'Find this poster: ' + currentPeekLabel + ' — tap anywhere to close'
               : 'Find: ' + currentPeekLabel;
  }
  // Clue text shown inside the enlarged thumbnail (recovery path when the
  // hint card was closed by mistake). Hidden during the enjoy-phase hold —
  // the caption there already points to the Next Clue button.
  function refreshPeekHint() {
    if (!peekHintEl) { return; }
    var show = !peekHold && state.nextHint && state.nextHint.hint;
    peekHintEl.textContent = show ? state.nextHint.hint : '';
  }
  function expandPeek() {
    peekBackdrop.style.display = 'block';
    peekEl.classList.add('big');
    peekLabel.textContent = peekCaption(true);
    refreshPeekHint();
  }
  function collapsePeek() {
    peekBackdrop.style.display = 'none';
    peekEl.classList.remove('big');
    peekLabel.textContent = peekCaption(false);
    applyPeekPos();
  }
  function initPeekInteractions() {
    // Measure the safe-area inset so the corner default clears the notch
    try {
      var probe = document.createElement('div');
      probe.style.cssText = 'position:fixed;top:env(safe-area-inset-top);left:0;width:0;height:0;visibility:hidden;pointer-events:none;';
      document.body.appendChild(probe);
      var safeTop = probe.getBoundingClientRect().top || 0;
      document.body.removeChild(probe);
      PEEK_DEFAULT.y = Math.max(10, safeTop + 10);
    } catch (e) {}
    peekEl.addEventListener('pointerdown', function (e) {
      if (peekEl.classList.contains('big')) { return; }
      peekDrag = { sx: e.clientX, sy: e.clientY, ox: peekPos ? peekPos.x : PEEK_DEFAULT.x, oy: peekPos ? peekPos.y : PEEK_DEFAULT.y, moved: false };
      try { peekEl.setPointerCapture(e.pointerId); } catch (err) {}
      e.preventDefault();
    });
    peekEl.addEventListener('pointermove', function (e) {
      if (!peekDrag) { return; }
      var dx = e.clientX - peekDrag.sx;
      var dy = e.clientY - peekDrag.sy;
      if (!peekDrag.moved && Math.abs(dx) + Math.abs(dy) > 8) { peekDrag.moved = true; }
      if (peekDrag.moved) {
        peekPos = { x: peekDrag.ox + dx, y: peekDrag.oy + dy };
        applyPeekPos();
      }
    });
    peekEl.addEventListener('pointerup', function () {
      if (!peekDrag) { return; }
      if (peekDrag.moved) {
        suppressPeekClick = true;
        try { localStorage.setItem(PEEK_POS_KEY, JSON.stringify(peekPos)); } catch (e) {}
      }
      peekDrag = null;
    });
    peekEl.addEventListener('pointercancel', function () { peekDrag = null; });
    peekEl.addEventListener('click', function () {
      if (suppressPeekClick) { suppressPeekClick = false; return; }
      if (peekEl.classList.contains('big')) { collapsePeek(); } else { expandPeek(); }
    });
    peekBackdrop.addEventListener('click', collapsePeek);
    window.addEventListener('resize', function () {
      if (peekEl && peekEl.style.display !== 'none' && !peekEl.classList.contains('big')) { applyPeekPos(); }
    });
  }
  function updatePeek() {
    if (!peekEl) { return; }
    var posters = state.posters.length ? state.posters : FALLBACK_POSTERS;
    var nextP = null;
    for (var i = 0; i < posters.length; i++) {
      if (!state.scanned[posters[i].id]) { nextP = posters[i]; break; }
    }
    if (!state.active || state.completed || !nextP || !posterImages[nextP.id]) {
      peekEl.style.display = 'none';
      peekBackdrop.style.display = 'none';
      peekEl.classList.remove('big');
      return;
    }
    // Enjoy phase: keep showing the poster just scanned — the thumbnail only
    // advances when the participant taps "Next Clue" (or closes the card).
    if (peekHold) { return; }
    currentPeekLabel = nextP.label;
    if (peekImg.getAttribute('src') !== posterImages[nextP.id]) {
      peekImg.src = posterImages[nextP.id];
    }
    peekLabel.textContent = peekCaption(peekEl.classList.contains('big'));
    refreshPeekHint();
    peekEl.style.display = 'block';
    if (!peekEl.classList.contains('big')) { applyPeekPos(); }
  }

  function renderChips() {
    if (!chipsEl) { return; }
    var posters = state.posters.length ? state.posters : FALLBACK_POSTERS;
    var html = '';
    posters.forEach(function (p) {
      var done = !!state.scanned[p.id];
      html += '<div class="hunt-chip' + (done ? ' done' : '') + '">' + (done ? '✓ ' : '') + p.label + '</div>';
    });
    chipsEl.innerHTML = html;
    document.getElementById('hunt-count').innerHTML = '<b>' + state.count + '</b>/' + (state.total || posters.length);
    updatePeek();
  }

  function toast(msg, ms) {
    if (!toastEl) { return; }
    toastEl.textContent = msg;
    toastEl.style.display = 'block';
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.style.display = 'none'; }, ms || 2500);
  }

  // The card never auto-hides: it stays until the participant taps ✕, the next
  // scan replaces its content, or the completion screen takes over.
  function hintCard(progressText, hint, compact) {
    if (!hintEl) { return; }
    document.getElementById('hunt-hint-p').textContent = progressText;
    document.getElementById('hunt-hint-t').textContent = hint;
    if (compact) { hintEl.classList.add('compact'); } else { hintEl.classList.remove('compact'); }
    hintEl.style.display = compact ? 'flex' : 'block';
    // next frame so the slide-up transition runs
    requestAnimationFrame(function () { hintEl.classList.add('show'); });
  }
  function setNextBtnVisible(on) {
    if (!nextBtnEl) { return; }
    if (on) { nextBtnEl.classList.add('on'); } else { nextBtnEl.classList.remove('on'); }
  }
  // ─── Sponsor interstitials (dashboard-managed, rotate per clue) ─────
  function preloadAds() {
    (CFG.ads || []).forEach(function (ad) {
      if (ad && ad.image && !adPreloaded[ad.image]) {
        adPreloaded[ad.image] = true;
        var im = new Image();
        im.src = ad.image;   // warm the cache so the interstitial is instant
      }
    });
  }
  function maybeShowAd() {
    if (!adEl || !CFG.ads || !CFG.ads.length) { return; }
    var ad = CFG.ads[adShownCount % CFG.ads.length];
    adShownCount++;
    if (!ad || !ad.image) { return; }
    currentAdLink = ad.link || '';
    if (adImgEl.getAttribute('src') !== ad.image) { adImgEl.src = ad.image; }
    document.getElementById('hunt-spot-tap').textContent = currentAdLink ? 'Tap the image to learn more · tap anywhere else to continue' : 'Tap anywhere to continue';
    adEl.style.display = 'flex';
  }
  function hideAd() {
    if (adEl) { adEl.style.display = 'none'; }
  }

  // "Next Clue" tapped: reveal the clue and let the tracking thumbnail advance.
  // The sponsor interstitial (if configured) sits on top; closing it uncovers
  // the clue card + advanced thumbnail already in place underneath.
  function advanceToClue() {
    clearTimeout(nextBtnTimer);
    setNextBtnVisible(false);
    peekHold = false;
    if (pendingNext) {
      hintCard(pendingNext.count + '/' + pendingNext.total + ' completed', pendingNext.hint);
      pendingNext = null;
    }
    updatePeek();
    maybeShowAd();
  }
  function hideHint() {
    if (!hintEl) { return; }
    // Closing the card also ends the enjoy phase: drop the unseen clue and
    // let the tracking thumbnail advance so the participant is never stuck.
    clearTimeout(nextBtnTimer);
    setNextBtnVisible(false);
    pendingNext = null;
    if (peekHold) { peekHold = false; updatePeek(); }
    hintEl.classList.remove('show');
    setTimeout(function () {
      if (!hintEl.classList.contains('show')) { hintEl.style.display = 'none'; }
    }, 320);
  }

  // ─── Victory card: canvas-drawn shareable completion image ──────────
  // Pure shapes + text (no external images), so the canvas is never CORS-
  // tainted and toBlob always works.
  var vcCanvas = null, vcFile = null, vcText = '';

  function vcRoundRect(ctx, x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
  }
  function vcFitFont(ctx, text, weight, startPx, minPx, maxWidth, family) {
    var px = startPx;
    while (px > minPx) {
      ctx.font = weight + ' ' + px + 'px ' + family;
      if (ctx.measureText(text).width <= maxWidth) { break; }
      px -= 4;
    }
    return px;
  }

  function buildVictoryCard(data) {
    var FAM = '-apple-system, Roboto, "Segoe UI", sans-serif';
    var W = 1080, H = 1350;
    var c = document.createElement('canvas');
    c.width = W; c.height = H;
    var ctx = c.getContext('2d');
    // Canvas can't read CSS vars — build rgba strings from the branding colors
    var pr = hexRgb(BR.primary) || { r: 220, g: 30, b: 30 };
    var PT = pr.r + ',' + pr.g + ',' + pr.b;
    var lr = hexRgb(BR.light) || { r: 255, g: 80, b: 80 };
    var LT = lr.r + ',' + lr.g + ',' + lr.b;

    // Background + brand-color glow
    ctx.fillStyle = '#0a0a0a';
    ctx.fillRect(0, 0, W, H);
    var glow = ctx.createRadialGradient(W / 2, 340, 60, W / 2, 340, 700);
    glow.addColorStop(0, 'rgba(' + PT + ',0.16)');
    glow.addColorStop(1, 'rgba(' + PT + ',0)');
    ctx.fillStyle = glow;
    ctx.fillRect(0, 0, W, H);

    // Confetti
    var colors = [BR.accent, '#ffd700', '#ffffff', '#5fd06a', BR.primary, '#ff9f43'];
    for (var i = 0; i < 40; i++) {
      ctx.save();
      ctx.translate(60 + Math.random() * (W - 120), 60 + Math.random() * 620);
      ctx.rotate(Math.random() * Math.PI);
      ctx.fillStyle = colors[i % colors.length];
      ctx.globalAlpha = 0.5 + Math.random() * 0.5;
      ctx.fillRect(-5, -8, 10, 16);
      ctx.restore();
    }
    ctx.globalAlpha = 1;

    // Frame + HUD corner brackets
    ctx.strokeStyle = 'rgba(' + PT + ',0.45)';
    ctx.lineWidth = 3;
    vcRoundRect(ctx, 34, 34, W - 68, H - 68, 26);
    ctx.stroke();
    ctx.strokeStyle = 'rgba(' + LT + ',0.9)';
    ctx.lineWidth = 6;
    [[60, 60, 1, 1], [W - 60, 60, -1, 1], [60, H - 60, 1, -1], [W - 60, H - 60, -1, -1]].forEach(function (k) {
      ctx.beginPath();
      ctx.moveTo(k[0] + 44 * k[2], k[1]);
      ctx.lineTo(k[0], k[1]);
      ctx.lineTo(k[0], k[1] + 44 * k[3]);
      ctx.stroke();
    });

    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';

    // Brand: [PREFIX] SUFFIX — from BR.brandName ("AR|RISE" style)
    var bi = String(BR.brandName).indexOf('|');
    var bPre = bi === -1 ? String(BR.brandName) : BR.brandName.slice(0, bi);
    var bSuf = bi === -1 ? '' : BR.brandName.slice(bi + 1);
    ctx.font = '900 56px ' + FAM;
    var arW = ctx.measureText(bPre).width + 36;
    var riseW = bSuf ? ctx.measureText(bSuf).width : 0;
    var bx = W / 2 - (arW + (bSuf ? 14 : 0) + riseW) / 2;
    ctx.fillStyle = BR.primary;
    vcRoundRect(ctx, bx, 118, arW, 74, 8);
    ctx.fill();
    ctx.fillStyle = '#ffffff';
    ctx.fillText(bPre, bx + arW / 2, 158);
    if (bSuf) { ctx.fillText(bSuf, bx + arW + 14 + riseW / 2, 158); }

    ctx.fillStyle = 'rgba(255,255,255,0.5)';
    ctx.font = '600 27px ' + FAM;
    ctx.fillText((BR.huntTitle + ' · ' + BR.eventName).toUpperCase(), W / 2, 240);

    // Trophy + headline
    ctx.font = '150px ' + FAM;
    ctx.fillText('🏆', W / 2, 380);
    ctx.fillStyle = '#ffffff';
    ctx.font = '900 62px ' + FAM;
    ctx.fillText('CHALLENGE COMPLETE!', W / 2, 510);

    // Name (auto-fit)
    var name = (data.name || state.name || 'Meme Hunter').toUpperCase();
    ctx.fillStyle = '#ffd700';
    vcFitFont(ctx, name, '800', 68, 34, 920, FAM);
    ctx.fillText(name, W / 2, 600);

    // Time
    ctx.fillStyle = BR.accent;
    ctx.font = '800 175px ' + FAM;
    ctx.fillText(data.time_formatted || '--:--', W / 2, 745);
    ctx.fillStyle = 'rgba(255,255,255,0.45)';
    ctx.font = '600 26px ' + FAM;
    ctx.fillText('HUNT TIME · ALL 5 MEMES FOUND', W / 2, 850);

    // Rank pill
    if (data.rank) {
      var rankTxt = 'RANK  #' + data.rank;
      ctx.font = '800 46px ' + FAM;
      var rw = ctx.measureText(rankTxt).width + 90;
      var gold = data.rank <= 3;
      ctx.strokeStyle = gold ? '#ffd700' : 'rgba(' + LT + ',0.9)';
      ctx.lineWidth = 4;
      vcRoundRect(ctx, W / 2 - rw / 2, 895, rw, 88, 44);
      ctx.stroke();
      ctx.fillStyle = gold ? '#ffd700' : '#ffffff';
      ctx.fillText(rankTxt, W / 2, 941);
    }

    // Poster chips row
    var posters = state.posters.length ? state.posters : FALLBACK_POSTERS;
    ctx.font = '700 26px ' + FAM;
    var pad = 34, gap = 14, chipH = 56;
    var widths = posters.map(function (p) { return ctx.measureText('✓ ' + p.label.toUpperCase()).width + pad * 2; });
    var totalW = widths.reduce(function (a, b) { return a + b; }, 0) + gap * (posters.length - 1);
    var cx = W / 2 - totalW / 2;
    posters.forEach(function (p, idx) {
      ctx.strokeStyle = 'rgba(95,208,106,0.8)';
      ctx.lineWidth = 3;
      vcRoundRect(ctx, cx, 1030, widths[idx], chipH, 28);
      ctx.stroke();
      ctx.fillStyle = '#5fd06a';
      ctx.fillText('✓ ' + p.label.toUpperCase(), cx + widths[idx] / 2, 1059);
      cx += widths[idx] + gap;
    });

    // Footer
    var div = ctx.createLinearGradient(200, 0, W - 200, 0);
    div.addColorStop(0, 'rgba(' + PT + ',0)');
    div.addColorStop(0.5, 'rgba(' + PT + ',0.8)');
    div.addColorStop(1, 'rgba(' + PT + ',0)');
    ctx.fillStyle = div;
    ctx.fillRect(200, 1130, W - 400, 3);

    ctx.fillStyle = '#ffffff';
    ctx.font = '800 40px ' + FAM;
    ctx.fillText('CAN YOU BEAT MY TIME?', W / 2, 1190);
    ctx.fillStyle = BR.light;
    ctx.font = '700 32px ' + FAM;
    ctx.fillText(CFG.landingUrl.replace(/^https:\/\//, '').replace(/\/$/, ''), W / 2, 1243);
    ctx.fillStyle = 'rgba(255,255,255,0.35)';
    ctx.font = '600 22px ' + FAM;
    ctx.fillText(('POWERED BY ' + BR.poweredBy).toUpperCase(), W / 2, 1295);

    return c;
  }

  function renderVictoryCard(data) {
    vcCanvas = buildVictoryCard(data);
    vcText = 'I completed the ' + BR.huntTitle + ' at ' + BR.eventName + ' in ' +
      (data.time_formatted || '') + (data.rank ? ' — Rank #' + data.rank : '') +
      '! 🏆 Can you beat my time? 👉 ' + CFG.landingUrl;

    var preview = document.getElementById('hunt-vc-preview');
    preview.src = vcCanvas.toDataURL('image/png');
    preview.style.display = 'block';

    vcFile = null;
    vcCanvas.toBlob(function (blob) {
      // Pre-built File keeps navigator.share inside the tap's user-activation
      try { vcFile = new File([blob], 'meme-hunt-victory.png', { type: 'image/png' }); } catch (e) {}
    }, 'image/png');
  }

  function vcDownload() {
    try {
      var a = document.createElement('a');
      a.href = vcCanvas.toDataURL('image/png');
      a.download = 'meme-hunt-victory.png';
      document.body.appendChild(a);
      a.click();
      a.remove();
      toast('Card saved — share it on WhatsApp! 📲', 3500);
    } catch (e) {}
  }

  function shareVictoryCard() {
    if (!vcCanvas) { return; }
    try {
      if (vcFile && navigator.canShare && navigator.canShare({ files: [vcFile] }) && navigator.share) {
        navigator.share({ files: [vcFile], title: 'AR Meme Hunt', text: vcText }).catch(function () {});
        return;
      }
    } catch (e) {}
    vcDownload();
  }

  function showCompletion(data) {
    if (!doneEl) { return; }
    clearTimeout(nextBtnTimer);
    setNextBtnVisible(false);
    pendingNext = null;
    peekHold = false;
    hideAd();   // never show a sponsor over the completion celebration
    hintEl.classList.remove('show');
    hintEl.style.display = 'none';
    if (peekEl) {
      peekEl.style.display = 'none';
      peekBackdrop.style.display = 'none';
      peekEl.classList.remove('big');
    }
    document.getElementById('hunt-done-time').textContent = data.time_formatted || '--:--';
    document.getElementById('hunt-done-rank').textContent = data.rank ? 'Leaderboard position: #' + data.rank : '';
    try { renderVictoryCard(data); } catch (e) { console.error('[Hunt] victory card:', e); }
    doneEl.style.display = 'flex';
    spawnConfetti();
  }

  function spawnConfetti() {
    var old = document.getElementById('hunt-confetti');
    if (old && old.parentNode) { old.parentNode.removeChild(old); }
    var c = document.createElement('div');
    c.id = 'hunt-confetti';
    var colors = [BR.accent, '#ffd700', '#ffffff', '#5fd06a', BR.primary, '#ff9f43'];
    for (var i = 0; i < 44; i++) {
      var p = document.createElement('div');
      p.className = 'hunt-cf';
      p.style.left = (Math.random() * 100) + '%';
      p.style.background = colors[i % colors.length];
      p.style.animationDuration = (2.4 + Math.random() * 1.8) + 's';
      p.style.animationDelay = (Math.random() * 1.6) + 's';
      if (i % 3 === 0) { p.style.width = '6px'; p.style.height = '9px'; }
      c.appendChild(p);
    }
    doneEl.insertBefore(c, doneEl.firstChild);
    setTimeout(function () { if (c.parentNode) { c.parentNode.removeChild(c); } }, 9500);
  }

  function fmtTimer(ms) {
    var s = Math.max(0, Math.floor(ms / 1000));
    var ss = s % 60;
    // Over an hour: H:MM:SS — "507:37" as minutes reads like a broken clock
    if (s >= 3600) {
      var mmH = Math.floor((s % 3600) / 60);
      return Math.floor(s / 3600) + ':' + (mmH < 10 ? '0' : '') + mmH + ':' + (ss < 10 ? '0' : '') + ss;
    }
    var mm = Math.floor(s / 60);
    return (mm < 10 ? '0' : '') + mm + ':' + (ss < 10 ? '0' : '') + ss;
  }
  setInterval(function () {
    if (timerEl && state.active && state.started && !state.completed && state.timerBase) {
      timerEl.textContent = fmtTimer(Date.now() - state.timerBase);
    }
  }, 500);

  // ─── Boot ───────────────────────────────────────────────────────────
  function boot() {
    buildUI();
    var token = getToken();
    if (!token) {
      bootSettled = true;
      if (CFG.requireRegistration) { gateEl.style.display = 'flex'; }
      // Brand the registration gate too (no participant state to piggyback on)
      api('config').then(function (res) {
        if (res && res.success && res.data && res.data.ui) { applyBranding(res.data.ui.branding); }
      }).catch(function () {});
      return;
    }
    api('status&token=' + encodeURIComponent(token)).then(function (res) {
      if (!res || !res.success) {
        bootSettled = true;
        if (CFG.requireRegistration) { gateEl.style.display = 'flex'; }
        return;
      }
      state.active = true;
      applyState(res.data, false);
      document.getElementById('hunt-top').style.display = 'flex';
      chipsEl.style.display = 'flex';
      if (res.data.completed) {
        showCompletion(res.data);
      } else if (res.data.next && res.data.count === 0) {
        hintCard('Find your first poster — your timer starts at the first scan!', res.data.next.hint);
      } else if (res.data.next) {
        hintCard(res.data.count + '/' + res.data.total + ' completed', res.data.next.hint);
      }
      replayEarlyFound();
      flushPending();
    }).catch(function () {
      // Backend unreachable or timed out: activate optimistically so scans queue
      state.active = true;
      state.posters = FALLBACK_POSTERS;
      state.started = true;
      renderChips();
      document.getElementById('hunt-top').style.display = 'flex';
      chipsEl.style.display = 'flex';
      toast('Offline — your scans will sync automatically', 3000);
      replayEarlyFound();
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
