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
    totalPosters: 5
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
    renderChips();

    if (state.completed) {
      showCompletion(data);
      return;
    }
    if (announce) {
      if (data.duplicate) {
        toast('✓ Already counted — next poster!', 2600);
      } else if (data.next) {
        hintCard(data.count + '/' + data.total + ' completed', data.next.hint);
      }
    }
  }

  // ─── UI ─────────────────────────────────────────────────────────────
  var root, chipsEl, timerEl, toastEl, toastTimer, hintEl, gateEl, doneEl;

  function buildUI() {
    root = document.createElement('div');
    root.id = 'hunt-root';
    root.innerHTML =
      '<style>' +
      // z-index 90: above the AR canvases, but BELOW the template dialogs
      // (.ctaDiv 99: error/sound-unlock) and the boot loader (999), so template
      // errors are never hidden behind hunt overlays. top/right/bottom/left
      // instead of inset: iOS Safari < 14.5 does not support inset.
      '  #hunt-root { position:fixed; top:0; right:0; bottom:0; left:0; pointer-events:none; z-index:90; font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif; }' +
      '  #hunt-top { position:absolute; top:calc(10px + env(safe-area-inset-top)); left:0; right:0; display:none; justify-content:center; gap:10px; align-items:center; }' +
      '  #hunt-timer, #hunt-count { background:rgba(8,8,8,0.72); border:1px solid rgba(220,30,30,0.45); color:#fff; border-radius:20px; padding:6px 14px; font-size:12.5px; font-weight:700; letter-spacing:0.08em; font-variant-numeric:tabular-nums; }' +
      '  #hunt-count b { color:#ff5555; }' +
      '  #hunt-chips { position:absolute; bottom:calc(18px + env(safe-area-inset-bottom)); left:0; right:0; display:none; justify-content:center; gap:7px; padding:0 10px; flex-wrap:wrap; }' +
      '  .hunt-chip { background:rgba(8,8,8,0.72); border:1px solid rgba(255,255,255,0.18); color:rgba(255,255,255,0.55); border-radius:16px; padding:6px 11px; font-size:11px; font-weight:600; letter-spacing:0.06em; text-transform:uppercase; }' +
      '  .hunt-chip.done { border-color:rgba(95,208,106,0.7); color:#5fd06a; }' +
      '  #hunt-toast { position:absolute; top:calc(56px + env(safe-area-inset-top)); left:50%; transform:translateX(-50%); background:rgba(8,8,8,0.85); border:1px solid rgba(220,30,30,0.5); color:#fff; border-radius:12px; padding:10px 16px; font-size:12.5px; max-width:86vw; text-align:center; display:none; line-height:1.5; }' +
      '  #hunt-hint { position:absolute; left:50%; top:50%; transform:translate(-50%,-50%); width:min(86vw,340px); background:rgba(8,8,8,0.93); border:1px solid rgba(220,30,30,0.55); border-radius:16px; padding:22px 20px; text-align:center; display:none; pointer-events:auto; }' +
      '  #hunt-hint .hp { color:#ff5555; font-size:11px; font-weight:800; letter-spacing:0.22em; text-transform:uppercase; margin-bottom:10px; }' +
      '  #hunt-hint .ht { color:#fff; font-size:14.5px; line-height:1.65; }' +
      '  #hunt-hint button, #hunt-gate a, #hunt-gate button, #hunt-done a { pointer-events:auto; -webkit-tap-highlight-color:transparent; }' +
      '  #hunt-hint button { margin-top:16px; background:linear-gradient(135deg,#ff4444,#aa1111); color:#fff; border:none; border-radius:10px; font-size:12px; font-weight:700; letter-spacing:0.1em; text-transform:uppercase; padding:12px 24px; }' +
      '  #hunt-gate, #hunt-done { position:absolute; top:0; right:0; bottom:0; left:0; background:rgba(8,8,8,0.94); display:none; flex-direction:column; align-items:center; justify-content:center; text-align:center; padding:30px 24px; pointer-events:auto; }' +
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
      '<div id="hunt-toast"></div>' +
      '<div id="hunt-hint"><div class="hp" id="hunt-hint-p"></div><div class="ht" id="hunt-hint-t"></div><button id="hunt-hint-ok">Got It</button></div>' +
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

    document.getElementById('hunt-hint-ok').addEventListener('click', function () {
      hintEl.style.display = 'none';
    });
    document.getElementById('hunt-gate-btn').setAttribute('href', CFG.landingUrl);
    document.getElementById('hunt-gate-skip').addEventListener('click', function () {
      gateEl.style.display = 'none';
    });
    document.getElementById('hunt-done-lb').setAttribute('href', CFG.leaderboardUrl);
    document.getElementById('hunt-done-close').addEventListener('click', function () {
      doneEl.style.display = 'none';
    });
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
  }

  function toast(msg, ms) {
    if (!toastEl) { return; }
    toastEl.textContent = msg;
    toastEl.style.display = 'block';
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { toastEl.style.display = 'none'; }, ms || 2500);
  }

  function hintCard(progressText, hint) {
    if (!hintEl) { return; }
    document.getElementById('hunt-hint-p').textContent = progressText;
    document.getElementById('hunt-hint-t').textContent = hint;
    hintEl.style.display = 'block';
  }

  function showCompletion(data) {
    if (!doneEl) { return; }
    hintEl.style.display = 'none';
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
