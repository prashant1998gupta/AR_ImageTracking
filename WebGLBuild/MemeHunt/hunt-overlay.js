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
    doneMaxMs: 15000
  };
  var CFG = {};
  var userCfg = window.HUNT_CONFIG || {};
  for (var k in DEFAULTS) { CFG[k] = (k in userCfg) ? userCfg[k] : DEFAULTS[k]; }

  var qs = new URLSearchParams(window.location.search);
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
    active: false         // token present + status ok
  };
  var FALLBACK_POSTERS = [
    { id: 'FIFA_Target', label: 'FIFA' },
    { id: 'One8Traget', label: 'One8' },
    { id: 'BookCover', label: 'Book' },
    { id: 'CultGym', label: 'Gym' },
    { id: 'Shoes', label: 'Shoes' }
  ];

  // ─── arAnalytics shim + wrap (must happen synchronously at load) ────
  if (!window.arAnalytics) {
    // Shim so Analytics.jslib always finds a target even if the analytics
    // script failed to load (offline dashboards etc.)
    window.arAnalytics = {
      track: function () {}, arSessionStart: function () {},
      arImageFound: function () {}, arImageLost: function () {},
      arCtaClick: function () {}
    };
  }
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

  function applyState(data, announce) {
    if (!data) { return; }
    if (data.posters && data.posters.length) { state.posters = data.posters; }
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
    // Freeze the tracking thumbnail BEFORE renderChips/updatePeek run, so a
    // live scan doesn't advance it — that happens only on "Next Clue" / ✕.
    if (announce && !data.duplicate && data.poster_id && !data.completed && data.next) {
      peekHold = true;
    }
    renderChips();

    if (state.completed) {
      if (announce && !data.duplicate && data.poster_id) {
        // 5th poster just scanned live: instant feedback, then let the final
        // meme play before the completion screen takes over
        hintCard('🎉 ' + data.total + '/' + data.total + ' completed', 'Challenge complete — enjoy the last meme! 🎬');
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
        hintCard('✓ ' + data.count + '/' + data.total + ' completed', 'Enjoy the meme! 🎬');
        setNextBtnVisible(false);
        clearTimeout(nextBtnTimer);
        nextBtnTimer = setTimeout(function () { setNextBtnVisible(true); }, CFG.nextBtnDelayMs);
      }
    }
  }

  // ─── UI ─────────────────────────────────────────────────────────────
  var root, chipsEl, timerEl, toastEl, toastTimer, hintEl, gateEl, doneEl;
  var peekEl, peekBackdrop, peekImg, peekLabel;
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
      '  #hunt-root { position:fixed; top:0; right:0; bottom:0; left:0; pointer-events:none; z-index:90; overflow:hidden; font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif; }' +
      '  #hunt-top { position:absolute; top:calc(10px + env(safe-area-inset-top)); left:0; right:0; display:none; justify-content:center; gap:10px; align-items:center; }' +
      '  #hunt-timer, #hunt-count { background:rgba(8,8,8,0.72); border:1px solid rgba(220,30,30,0.45); color:#fff; border-radius:20px; padding:6px 14px; font-size:12.5px; font-weight:700; letter-spacing:0.08em; font-variant-numeric:tabular-nums; }' +
      '  #hunt-count b { color:#ff5555; }' +
      '  #hunt-chips { position:absolute; bottom:calc(18px + env(safe-area-inset-bottom)); left:0; right:0; display:none; justify-content:center; gap:7px; padding:0 10px; flex-wrap:wrap; }' +
      '  .hunt-chip { background:rgba(8,8,8,0.72); border:1px solid rgba(255,255,255,0.18); color:rgba(255,255,255,0.55); border-radius:16px; padding:6px 11px; font-size:11px; font-weight:600; letter-spacing:0.06em; text-transform:uppercase; }' +
      '  .hunt-chip.done { border-color:rgba(95,208,106,0.7); color:#5fd06a; }' +
      '  #hunt-toast { position:absolute; top:calc(56px + env(safe-area-inset-top)); left:50%; transform:translateX(-50%); background:rgba(8,8,8,0.85); border:1px solid rgba(220,30,30,0.5); color:#fff; border-radius:12px; padding:10px 16px; font-size:12.5px; max-width:86vw; text-align:center; display:none; line-height:1.5; }' +
      // Hint = compact bottom sheet above the chips — never covers the AR view
      // left+right anchoring + margin:auto centers the sheet without transform-X,
      // so it can never be clipped at a screen edge; box-sizing keeps the padding
      // inside the width on every browser
      '  #hunt-hint { position:absolute; left:12px; right:12px; margin:0 auto; max-width:380px; box-sizing:border-box; bottom:calc(64px + env(safe-area-inset-bottom)); transform:translateY(16px); background:rgba(8,8,8,0.9); border:1px solid rgba(220,30,30,0.55); border-radius:14px; padding:13px 40px 13px 16px; text-align:left; display:none; opacity:0; transition:opacity 0.3s ease, transform 0.3s ease; pointer-events:auto; }' +
      '  #hunt-hint.show { opacity:1; transform:translateY(0); }' +
      '  #hunt-hint .hp { color:#ff5555; font-size:10px; font-weight:800; letter-spacing:0.2em; text-transform:uppercase; margin-bottom:5px; }' +
      '  #hunt-hint .ht { color:#fff; font-size:13px; line-height:1.55; }' +
      '  #hunt-hint button, #hunt-gate a, #hunt-gate button, #hunt-done a { pointer-events:auto; -webkit-tap-highlight-color:transparent; }' +
      '  #hunt-hint .hx { position:absolute; top:6px; right:6px; width:28px; height:28px; background:rgba(255,255,255,0.08); color:rgba(255,255,255,0.6); border:none; border-radius:50%; font-size:13px; line-height:28px; padding:0; }' +
      '  #hunt-hint .nxt { display:none; margin-top:11px; background:linear-gradient(135deg,#ff4444,#aa1111); color:#fff; border:none; border-radius:10px; font-size:11.5px; font-weight:700; letter-spacing:0.12em; text-transform:uppercase; padding:11px 24px; -webkit-tap-highlight-color:transparent; }' +
      '  #hunt-hint .nxt.on { display:inline-block; }' +
      '  #hunt-gate, #hunt-done { position:absolute; top:0; right:0; bottom:0; left:0; background:rgba(8,8,8,0.94); display:none; flex-direction:column; align-items:center; justify-content:center; text-align:center; padding:30px 24px; pointer-events:auto; z-index:30; }' +
      // Draggable "find this poster" preview thumbnail
      '  #hunt-peek { position:absolute; width:82px; background:rgba(8,8,8,0.88); border:1px solid rgba(220,30,30,0.55); border-radius:12px; overflow:hidden; display:none; pointer-events:auto; touch-action:none; z-index:5; box-shadow:0 4px 14px rgba(0,0,0,0.4); }' +
      '  #hunt-peek img { display:block; width:100%; height:82px; object-fit:cover; pointer-events:none; -webkit-user-drag:none; user-select:none; -webkit-user-select:none; }' +
      '  #hunt-peek .pk { font-size:8.5px; letter-spacing:0.1em; text-transform:uppercase; color:#ff6666; font-weight:800; text-align:center; padding:4px 4px 5px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }' +
      // plain width first = fallback for browsers without CSS min(); max-width
      // guarantees the enlarged card always fits inside the screen
      '  #hunt-peek.big { left:50% !important; top:50% !important; transform:translate(-50%,-50%); width:288px; width:min(80vw,320px); max-width:calc(100vw - 24px); z-index:20; }' +
      '  #hunt-peek.big img { height:auto; max-height:55vh; object-fit:contain; background:#000; }' +
      '  #hunt-peek.big .pk { font-size:11px; padding:10px; white-space:normal; }' +
      '  #hunt-peek-backdrop { position:absolute; top:0; right:0; bottom:0; left:0; background:rgba(0,0,0,0.65); display:none; pointer-events:auto; z-index:15; }' +
      '  .hunt-brand { letter-spacing:0.38em; font-weight:300; font-size:20px; text-transform:uppercase; color:#fff; }' +
      '  .hunt-brand b { color:#dc1e1e; font-weight:700; }' +
      '  .hunt-h { color:#fff; font-size:21px; font-weight:800; letter-spacing:0.06em; margin:16px 0 8px; }' +
      '  .hunt-p { color:rgba(255,255,255,0.55); font-size:13px; line-height:1.7; max-width:300px; }' +
      '  .hunt-btn { display:inline-block; margin-top:20px; background:linear-gradient(135deg,#ff4444,#aa1111); color:#fff; border:none; border-radius:12px; font-size:13px; font-weight:700; letter-spacing:0.12em; text-transform:uppercase; padding:15px 30px; text-decoration:none; }' +
      '  .hunt-skip { display:inline-block; margin-top:14px; color:rgba(255,255,255,0.35); font-size:11.5px; text-decoration:underline; background:none; border:none; }' +
      '  #hunt-done .big-time { color:#dc1e1e; font-size:46px; font-weight:100; margin:8px 0 2px; font-variant-numeric:tabular-nums; }' +
      '  #hunt-done .rank { color:rgba(255,255,255,0.7); font-size:14px; margin-bottom:6px; }' +
      '</style>' +
      '<div id="hunt-top"><div id="hunt-count"><b>0</b>/5</div><div id="hunt-timer">00:00</div></div>' +
      '<div id="hunt-chips"></div>' +
      '<div id="hunt-peek-backdrop"></div>' +
      '<div id="hunt-peek"><img id="hunt-peek-img" alt="Next poster"><div class="pk" id="hunt-peek-label"></div></div>' +
      '<div id="hunt-toast"></div>' +
      '<div id="hunt-hint"><button id="hunt-hint-ok" class="hx" aria-label="Close">✕</button><div class="hp" id="hunt-hint-p"></div><div class="ht" id="hunt-hint-t"></div><button id="hunt-hint-next" class="nxt">Next Clue ▸</button></div>' +
      '<div id="hunt-gate">' +
      '  <div class="hunt-brand">AR<b>RISE</b></div>' +
      '  <div class="hunt-h">AR Meme Hunt</div>' +
      '  <p class="hunt-p">Find 5 posters. Scan them all. Top 3 win prizes. Register first to join the challenge!</p>' +
      '  <a class="hunt-btn" id="hunt-gate-btn">Register To Play</a>' +
      '  <button class="hunt-skip" id="hunt-gate-skip">Continue without the hunt</button>' +
      '</div>' +
      '<div id="hunt-done">' +
      '  <div class="hunt-brand">AR<b>RISE</b></div>' +
      '  <div class="hunt-h">Challenge Complete!</div>' +
      '  <p class="hunt-p">Congratulations! You completed the Bharatiya Vyapar Mahotsav AR Meme Hunt.</p>' +
      '  <div class="big-time" id="hunt-done-time">--:--</div>' +
      '  <div class="rank" id="hunt-done-rank"></div>' +
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

    // Poster preview images ship in every build: PostProcessBuild injects
    // <imagetarget id src> tags pointing at targets/<file>
    document.querySelectorAll('imagetarget').forEach(function (t) {
      var tid = t.getAttribute('id');
      var src = t.getAttribute('src');
      if (tid && src) { posterImages[tid] = src; }
    });
    initPeekInteractions();

    document.getElementById('hunt-hint-ok').addEventListener('click', hideHint);
    nextBtnEl = document.getElementById('hunt-hint-next');
    nextBtnEl.addEventListener('click', advanceToClue);
    document.getElementById('hunt-gate-btn').setAttribute('href', CFG.landingUrl);
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
    document.getElementById('hunt-done-close').addEventListener('click', function () {
      doneEl.style.display = 'none';
    });
  }

  // ─── "Find this poster" preview: draggable thumb, tap to enlarge ────
  var peekPos = null;
  var peekDrag = null;
  var suppressPeekClick = false;

  function loadPeekPos() {
    try { return JSON.parse(localStorage.getItem('hunt_peek_pos') || 'null'); } catch (e) { return null; }
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
  function applyPeekPos() {
    if (!peekPos) { peekPos = loadPeekPos() || { x: 10, y: 104 }; }
    clampPeekPos();
    peekEl.style.left = peekPos.x + 'px';
    peekEl.style.top = peekPos.y + 'px';
  }
  function expandPeek() {
    peekBackdrop.style.display = 'block';
    peekEl.classList.add('big');
    peekLabel.textContent = 'Find this poster: ' + currentPeekLabel + ' — tap anywhere to close';
  }
  function collapsePeek() {
    peekBackdrop.style.display = 'none';
    peekEl.classList.remove('big');
    peekLabel.textContent = 'Find: ' + currentPeekLabel;
    applyPeekPos();
  }
  function initPeekInteractions() {
    peekEl.addEventListener('pointerdown', function (e) {
      if (peekEl.classList.contains('big')) { return; }
      peekDrag = { sx: e.clientX, sy: e.clientY, ox: peekPos ? peekPos.x : 10, oy: peekPos ? peekPos.y : 104, moved: false };
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
        try { localStorage.setItem('hunt_peek_pos', JSON.stringify(peekPos)); } catch (e) {}
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
    peekLabel.textContent = (peekEl.classList.contains('big') ? 'Find this poster: ' + nextP.label + ' — tap anywhere to close' : 'Find: ' + nextP.label);
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
  function hintCard(progressText, hint) {
    if (!hintEl) { return; }
    document.getElementById('hunt-hint-p').textContent = progressText;
    document.getElementById('hunt-hint-t').textContent = hint;
    hintEl.style.display = 'block';
    // next frame so the slide-up transition runs
    requestAnimationFrame(function () { hintEl.classList.add('show'); });
  }
  function setNextBtnVisible(on) {
    if (!nextBtnEl) { return; }
    if (on) { nextBtnEl.classList.add('on'); } else { nextBtnEl.classList.remove('on'); }
  }
  // "Next Clue" tapped: reveal the clue and let the tracking thumbnail advance
  function advanceToClue() {
    clearTimeout(nextBtnTimer);
    setNextBtnVisible(false);
    peekHold = false;
    if (pendingNext) {
      hintCard(pendingNext.count + '/' + pendingNext.total + ' completed', pendingNext.hint);
      pendingNext = null;
    }
    updatePeek();
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

  function showCompletion(data) {
    if (!doneEl) { return; }
    clearTimeout(nextBtnTimer);
    setNextBtnVisible(false);
    pendingNext = null;
    peekHold = false;
    hintEl.classList.remove('show');
    hintEl.style.display = 'none';
    if (peekEl) {
      peekEl.style.display = 'none';
      peekBackdrop.style.display = 'none';
      peekEl.classList.remove('big');
    }
    document.getElementById('hunt-done-time').textContent = data.time_formatted || '--:--';
    document.getElementById('hunt-done-rank').textContent = data.rank ? 'Leaderboard position: #' + data.rank : '';
    doneEl.style.display = 'flex';
  }

  function fmtTimer(ms) {
    var s = Math.max(0, Math.floor(ms / 1000));
    var mm = Math.floor(s / 60);
    var ss = s % 60;
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
