// Overlay wiring: feed (Twitch PubSub or mock) -> model -> DOM, plus viewer controls.
(function () {
  'use strict';

  var M = window.PogModel;
  var params = new URLSearchParams(location.search);
  var twitch = window.Twitch && window.Twitch.ext ? window.Twitch.ext : null;

  // ?mock=1 forces demo data, ?mock=0 disables it. By default the demo runs until
  // the first real message from the streamer's PogSplit arrives.
  var mockParam = params.get('mock');
  var speed = Math.max(0.1, Number(params.get('speed')) || 1);

  var state = M.createState();
  var prefs = window.PogSettings.load();
  var source = 'waiting'; // waiting | mock | live
  var mock = null;
  var controlsVisible = true;
  var settingsOpen = false;
  var activeView = 'live'; // live | attempts | finished | golds

  // Run history for the history tabs. Not on PubSub: fetched from the EBS when a tab opens.
  var history = { data: null, available: false, fetchedAt: 0, stale: true };

  var $ = function (id) { return document.getElementById(id); };
  var el = {
    root: $('root'), launcher: $('launcher'), panel: $('panel'), head: $('head'), resize: $('resize'),
    tabs: $('tabs'), history: $('history'), cmp: $('cmp'),
    game: $('game'), category: $('category'), gear: $('gear'), close: $('close'),
    cmpPrev: $('cmp-prev'), cmpNext: $('cmp-next'), cmpName: $('cmp-name'),
    rows: $('rows'), timer: $('timer'), stats: $('stats'),
    settings: $('settings'), resetPrefs: $('reset-prefs'), source: $('source'),
  };

  function esc(s) {
    return String(s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  // ---- feed --------------------------------------------------------------

  // Real messages are held for the broadcaster's HLS latency so splits appear
  // when the viewer's video shows them, not seconds earlier.
  var hlsDelayMs = 0;
  var clockOffset = null; // EBS clock minus ours, once known from an HTTP Date header

  function onLive(msg) {
    if (!msg || msg.v !== 1) return;
    setTimeout(function () { onMessage(msg, 'live'); }, hlsDelayMs);
  }

  function serverNow() {
    return clockOffset == null ? undefined : Date.now() + clockOffset - hlsDelayMs;
  }

  function onMessage(msg, from) {
    if (from === 'live' && source !== 'live') {
      source = 'live';
      if (mock) { mock.stop(); mock = null; }
      state = M.createState(); // drop demo data before applying the real run
      history = { data: null, available: false, fetchedAt: 0, stale: true };
      fetchHistory();
    }
    // A snapshot follows every reset and new splits file, which is when the bridge uploads a new history.
    if (from === 'live' && msg.type === 'snapshot') scheduleHistoryFetch();
    if (from === 'mock' && source === 'live') return;
    if (from === 'live') {
      var gap = state.seq != null && msg.seq != null && msg.seq !== state.seq + 1 && msg.type !== 'snapshot';
      var staleRun = msg.type === 'update' && msg.run && state.run && msg.run !== state.run;
      if (gap || staleRun || (msg.type === 'update' && !state.ready)) fetchState();
    }
    if (M.apply(state, msg, performance.now(), from === 'live' ? serverNow() : undefined) && from === 'mock') source = 'mock';
    render();
  }

  // ---- EBS (bridge backend) ------------------------------------------------
  // Snapshots too big for PubSub come from the EBS. Empty until the bridge is deployed.
  var EBS_BASE = window.POGSPLIT_EBS || '';
  var authToken = null;
  var channelId = null;
  var fetching = false;

  function fetchState() {
    if (!EBS_BASE || !authToken || fetching) return;
    fetching = true;
    fetch(EBS_BASE + '/v1/channels/' + encodeURIComponent(channelId) + '/state', {
      headers: { Authorization: 'Bearer ' + authToken },
    }).then(function (r) {
      var date = Date.parse(r.headers.get('Date') || '');
      if (!isNaN(date)) clockOffset = date - Date.now(); // 1 s resolution is fine for a split timer
      return r.ok ? r.json() : null;
    }).then(function (msg) {
      if (msg) onLive(msg);
    }).catch(function () { /* PubSub snapshots/heartbeats still arrive */ })
      .then(function () { fetching = false; });
  }

  function fetchHistory() {
    if (mock) return; // the demo has its own history until the streamer's PogSplit is live
    if (!EBS_BASE || !authToken || history.loading) return;
    history.loading = true;
    fetch(EBS_BASE + '/v1/channels/' + encodeURIComponent(channelId) + '/history', {
      headers: { Authorization: 'Bearer ' + authToken },
    }).then(function (r) {
      if (r.status === 404) return null; // the streamer doesn't share it
      if (!r.ok) throw new Error('history ' + r.status);
      return r.json();
    }).then(function (data) {
      history.data = data && data.v === 1 ? data : null;
      history.available = !!history.data;
      history.fetchedAt = Date.now();
      history.stale = false;
    }).catch(function () { /* keep what we had; the next tab switch retries */ })
      .then(function () { history.loading = false; render(); });
  }

  var historyTimer = null;
  function scheduleHistoryFetch() {
    history.stale = true;
    clearTimeout(historyTimer);
    // The bridge uploads right after the reset; give it a moment.
    historyTimer = setTimeout(fetchHistory, 6000);
  }

  function startMock() {
    history = { data: window.PogMockFeed.createMockHistory(), available: true, fetchedAt: Date.now(), stale: false };
    mock = window.PogMockFeed.createMockFeed({
      speed: speed,
      onMessage: function (msg) { onMessage(msg, 'mock'); },
    });
    mock.start();
  }

  if (twitch) {
    twitch.onAuthorized(function (auth) {
      authToken = auth.token;
      channelId = auth.channelId;
      fetchState();
      fetchHistory();
    });
    twitch.onContext(function (ctx) {
      if (typeof ctx.hlsLatencyBroadcaster === 'number') hlsDelayMs = Math.max(0, ctx.hlsLatencyBroadcaster * 1000);
      if ('arePlayerControlsVisible' in ctx) {
        controlsVisible = ctx.arePlayerControlsVisible !== false;
        applyChrome();
      }
    });
    // The bridge sends to the "broadcast" PubSub target; payload is a JSON string.
    twitch.listen('broadcast', function (target, contentType, body) {
      var msg;
      try { msg = JSON.parse(body); } catch (e) { return; }
      onLive(msg);
    });
  }
  if (mockParam !== '0') startMock();

  // Local harness only (dev.html): lets the page push schema messages as if they came from PubSub.
  if (params.get('dev') === '1') {
    window.addEventListener('message', function (e) {
      if (e.origin !== location.origin || !e.data || e.data.pogsplit !== true) return;
      if (e.data.controls != null) { controlsVisible = !!e.data.controls; applyChrome(); }
      if (e.data.msg) onMessage(e.data.msg, 'live');
    });
  }

  // ---- viewer controls ---------------------------------------------------

  function setPref(key, value) {
    prefs[key] = value;
    window.PogSettings.save(prefs);
    applyChrome();
    render();
  }

  function comparisonIndex() {
    var i = prefs.comparison == null ? -1 : state.comparisons.indexOf(prefs.comparison);
    return i === -1 ? 0 : i; // index 0 is the streamer's current comparison
  }

  function cycleComparison(step) {
    var n = state.comparisons.length;
    if (!n) return;
    var next = (comparisonIndex() + step + n) % n;
    setPref('comparison', next === 0 ? null : state.comparisons[next]);
  }

  el.launcher.addEventListener('click', function () { setPref('open', !prefs.open); });
  el.close.addEventListener('click', function () { setPref('open', false); });
  el.gear.addEventListener('click', function () {
    settingsOpen = !settingsOpen;
    applyChrome();
  });
  el.tabs.addEventListener('click', function (e) {
    var tab = e.target.closest('[data-view]');
    if (!tab) return;
    activeView = tab.getAttribute('data-view');
    if (activeView !== 'live' && (history.stale || Date.now() - history.fetchedAt > 60000)) fetchHistory();
    render();
  });
  el.cmpPrev.addEventListener('click', function () { cycleComparison(-1); });
  el.cmpNext.addEventListener('click', function () { cycleComparison(1); });
  el.resetPrefs.addEventListener('click', function () {
    prefs = window.PogSettings.reset();
    syncSettingsForm();
    applyChrome();
    render();
  });

  function onSettingInput(e) {
    var input = e.target;
    var key = input.getAttribute('data-key');
    if (!key) return;
    var value = input.type === 'checkbox' ? input.checked
      : typeof window.PogSettings.DEFAULTS[key] === 'number' ? Number(input.value) : input.value;
    setPref(key, value);
  }
  el.settings.addEventListener('change', onSettingInput);
  el.settings.addEventListener('input', function (e) { if (e.target.type === 'range') onSettingInput(e); });

  // ---- move and resize -----------------------------------------------------
  // Dragging switches to a free position, kept as fractions of the player so it survives player size changes.

  var GAP = 16;
  var MIN_SCALE = 0.6, MAX_SCALE = 2.5;

  function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }

  // Pins the panel where it is now, so a drag or resize starting from a corner doesn't jump.
  function toCustom() {
    if (prefs.position === 'custom') return;
    var before = el.panel.getBoundingClientRect();
    var rootRect = el.root.getBoundingClientRect();
    prefs.position = 'custom';
    prefs.posX = rootRect.left / innerWidth;
    prefs.posY = rootRect.top / innerHeight;
    applyChrome();
    // In the bottom corners the launcher sits under the panel; keep the panel itself in place.
    var after = el.panel.getBoundingClientRect();
    prefs.posX += (before.left - after.left) / innerWidth;
    prefs.posY += (before.top - after.top) / innerHeight;
    applyChrome();
  }

  function startDrag(e, onMove) {
    if (e.button !== 0) return;
    e.preventDefault();
    toCustom();
    var startX = e.clientX, startY = e.clientY;
    var target = e.currentTarget;
    target.setPointerCapture(e.pointerId);
    el.root.classList.add('dragging');
    function move(ev) { onMove(ev.clientX - startX, ev.clientY - startY); applyChrome(); }
    function up() {
      target.removeEventListener('pointermove', move);
      target.removeEventListener('pointerup', up);
      target.removeEventListener('pointercancel', up);
      el.root.classList.remove('dragging');
      window.PogSettings.save(prefs);
      syncSettingsForm();
    }
    target.addEventListener('pointermove', move);
    target.addEventListener('pointerup', up);
    target.addEventListener('pointercancel', up);
  }

  el.head.addEventListener('pointerdown', function (e) {
    if (e.target.closest('button')) return;
    var x0, y0;
    startDrag(e, function (dx, dy) {
      if (x0 == null) { x0 = prefs.posX * innerWidth; y0 = prefs.posY * innerHeight; }
      prefs.posX = (x0 + dx) / innerWidth;
      prefs.posY = (y0 + dy) / innerHeight;
    });
  });

  el.resize.addEventListener('pointerdown', function (e) {
    var w0, s0;
    startDrag(e, function (dx, dy) {
      if (w0 == null) { w0 = el.panel.getBoundingClientRect().width; s0 = prefs.scale; }
      // Follow whichever way the pointer moved further, so dragging down also grows the panel.
      var grow = Math.abs(dx) >= Math.abs(dy) ? dx : dy * (w0 / Math.max(1, el.panel.getBoundingClientRect().height));
      prefs.scale = Math.round(clamp(s0 * (w0 + grow) / w0, MIN_SCALE, MAX_SCALE) * 100) / 100;
    });
  });

  window.addEventListener('resize', applyChrome);

  function syncSettingsForm() {
    var inputs = el.settings.querySelectorAll('[data-key]');
    for (var i = 0; i < inputs.length; i++) {
      var input = inputs[i];
      var v = prefs[input.getAttribute('data-key')];
      if (input.type === 'checkbox') input.checked = !!v;
      else input.value = String(v);
    }
  }

  // Layout-level state: open/closed, position, size, settings drawer, player-controls fade.
  function applyChrome() {
    var scale = clamp(Number(prefs.scale) || 1, MIN_SCALE, MAX_SCALE);
    var custom = prefs.position === 'custom';
    el.root.className = 'pos-' + prefs.position + (controlsVisible ? ' controls' : '') +
      (el.root.classList.contains('dragging') ? ' dragging' : '');
    el.root.style.setProperty('--scale', scale);
    el.panel.hidden = !prefs.open;

    // Sizes are set before scaling, so divide the room on screen by the scale.
    el.panel.style.maxWidth = Math.max(120, (innerWidth - 2 * GAP) / scale) + 'px';
    if (custom) {
      // Keep it fully on screen; a panel taller than the player stays at the top and scrolls.
      var w = el.root.offsetWidth * scale;
      var h = (el.launcher.offsetHeight + 6 + (prefs.open ? el.panel.scrollHeight + 2 : 0)) * scale;
      var left = clamp(prefs.posX * innerWidth, 0, Math.max(0, innerWidth - w));
      var top = clamp(prefs.posY * innerHeight, 0, Math.max(0, innerHeight - GAP - h));
      el.root.style.left = left + 'px';
      el.root.style.top = top + 'px';
      el.root.style.maxHeight = Math.max(80, (innerHeight - top - GAP) / scale) + 'px';
    } else {
      el.root.style.left = el.root.style.top = '';
      var bottom = prefs.position === 'bl' || prefs.position === 'br';
      el.root.style.maxHeight = Math.max(80, (innerHeight - GAP - (bottom ? 72 : GAP)) / scale) + 'px';
    }
    el.launcher.setAttribute('aria-expanded', String(prefs.open));
    el.settings.hidden = !settingsOpen;
    el.gear.setAttribute('aria-expanded', String(settingsOpen));
  }

  // ---- rendering -----------------------------------------------------------

  // Which rows to show: a window ending just past the current split, with the final split pinned.
  function visibleRows(rows, current, limit) {
    if (!limit || limit >= rows.length) return rows;
    var last = rows.length - 1;
    var focus = current < 0 ? 0 : Math.min(current, last);
    var end = Math.min(last - 1, Math.max(focus + 1, limit - 2));
    var start = Math.max(0, end - (limit - 2));
    return rows.slice(start, end + 1).concat([rows[last]]);
  }

  function rowHtml(r, last, pinned) {
    var cls = 'row ' + r.status + (pinned ? ' pinned' : '');
    var timeText = r.status === 'skipped' ? '-' : M.formatTime(r.time, prefs.timeDecimals);
    var delta = prefs.showDeltas && r.delta != null
      ? '<span class="delta c-' + r.color + (r.live ? ' live' : '') + '">' + M.formatDelta(r.delta, prefs.deltaDecimals) + '</span>'
      : '<span class="delta"></span>';
    var seg = prefs.showSegTimes
      ? '<span class="seg' + (r.color === 'gold' ? ' c-gold' : '') + '">' +
        (r.segTime != null ? M.formatTime(r.segTime, prefs.timeDecimals) : '') + '</span>'
      : '';
    return '<li class="' + cls + '"><span class="name">' + esc(r.name) + '</span>' +
      delta + seg + '<span class="time">' + timeText + '</span></li>';
  }

  function stat(label, value, cls) {
    return '<div class="stat"><dt>' + label + '</dt><dd class="' + (cls || '') + '">' + value + '</dd></div>';
  }

  // ---- history tabs ----------------------------------------------------------

  function when(ms) {
    if (ms == null) return '';
    var d = new Date(ms);
    var today = new Date().toDateString() === d.toDateString();
    var time = d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
    return today ? time : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) + ' ' + time;
  }

  function segName(h, i) {
    var names = h.segments || [];
    return names[i] != null ? names[i] : (state.segments[i] ? state.segments[i].name : 'Split ' + (i + 1));
  }

  function emptyText(text) { return '<p class="h-empty">' + text + '</p>'; }

  function attemptsHtml(h) {
    var n = (h.segments || []).length;
    if (!h.attempts || !h.attempts.length) return emptyText('No attempts saved yet.');
    return '<ol class="h-list">' + h.attempts.map(function (a) {
      var done = a.time != null;
      var main = done ? 'Finished'
        : n && a.reached < n ? 'Reset in ' + esc(segName(h, a.reached)) : 'Reset';
      var right = done ? M.formatTime(a.time, prefs.timeDecimals) : (n ? a.reached + '/' + n : '');
      return '<li class="h-row' + (done ? ' done' : '') + '"><span class="h-id">#' + a.id + '</span>' +
        '<span class="h-main">' + main + '</span><span class="h-time">' + right + '</span>' +
        '<span class="h-date">' + when(a.started) + '</span></li>';
    }).join('') + '</ol>';
  }

  function finishedHtml(h) {
    if (!h.finished || !h.finished.length) return emptyText('No finished runs yet.');
    var fastest = Math.min.apply(null, h.finished.map(function (r) { return r.time; }));
    return h.finished.map(function (r) {
      var prev = 0;
      var rows = (r.splits || []).map(function (t, i) {
        var seg = t != null && prev != null ? t - prev : null;
        if (t != null) prev = t;
        return '<li class="h-split"><span class="name">' + esc(segName(h, i)) + '</span>' +
          '<span class="seg">' + (seg != null ? M.formatTime(seg, prefs.timeDecimals) : '') + '</span>' +
          '<span class="time">' + M.formatTime(t, prefs.timeDecimals) + '</span></li>';
      }).join('');
      return '<details class="h-run"><summary><span class="h-id">#' + r.id + '</span>' +
        '<span class="h-main' + (r.time === fastest ? ' c-gold' : '') + '">' + M.formatTime(r.time, prefs.timeDecimals) + '</span>' +
        '<span class="h-date">' + when(r.ended || r.started) + '</span></summary><ol class="h-splits">' + rows + '</ol></details>';
    }).join('');
  }

  function goldsHtml(h) {
    if (!h.golds || !h.golds.length) return emptyText('No golds yet.');
    return '<ol class="h-list">' + h.golds.map(function (g) {
      var gain = g.prev != null
        ? '<span class="h-gain c-gold">' + M.formatDelta(g.time - g.prev, prefs.deltaDecimals) + '</span>'
        : '<span class="h-gain"></span>';
      return '<li class="h-row gold"><span class="h-main">' + esc(segName(h, g.seg)) + '</span>' + gain +
        '<span class="h-time c-gold">' + M.formatTime(g.time, prefs.timeDecimals) + '</span>' +
        '<span class="h-date">' + when(g.date) + '</span></li>';
    }).join('') + '</ol>';
  }

  var historyKey = null; // what is on screen, so the 50 ms timer repaint leaves open runs and scrolling alone

  function renderHistory() {
    var h = history.data;
    var key = activeView + '|' + history.fetchedAt + '|' + prefs.timeDecimals + '|' + prefs.deltaDecimals + '|' + !!history.loading;
    if (key === historyKey) return;
    historyKey = key;
    if (!h) {
      el.history.innerHTML = emptyText(history.loading ? 'Loading\u2026' : 'History is not available.');
      return;
    }
    el.history.innerHTML = activeView === 'attempts' ? attemptsHtml(h) : activeView === 'finished' ? finishedHtml(h) : goldsHtml(h);
    el.history.scrollTop = 0;
  }

  function render() {
    var ready = state.ready;
    el.game.textContent = ready ? state.game : 'Waiting for PogSplit…';
    el.category.textContent = ready
      ? state.category + (state.attempts ? ' · #' + state.attempts : '') : '';

    if (!history.available && activeView !== 'live') activeView = 'live';
    el.tabs.hidden = !history.available;
    var tabs = el.tabs.querySelectorAll('[data-view]');
    for (var ti = 0; ti < tabs.length; ti++) {
      tabs[ti].setAttribute('aria-pressed', String(tabs[ti].getAttribute('data-view') === activeView));
    }
    var live = activeView === 'live';
    el.history.hidden = live;
    el.cmp.hidden = !live;
    if (!live) {
      el.rows.hidden = el.timer.hidden = el.stats.hidden = true;
      renderHistory();
      el.source.textContent = history.data && history.data.updatedAt ? 'History updated ' + when(history.data.updatedAt) : '';
      el.source.className = 'source';
      return;
    }
    historyKey = null;
    el.cmpName.textContent = ready ? state.comparisons[comparisonIndex()] || '' : '';
    el.cmpPrev.disabled = el.cmpNext.disabled = state.comparisons.length < 2;

    el.source.textContent = state.offline ? 'Streamer’s PogSplit is offline'
      : source === 'live' ? 'Live from PogSplit'
      : source === 'mock' ? 'Demo data · no live run yet' : 'Waiting for the streamer’s PogSplit';
    el.source.className = 'source ' + (state.offline ? 'offline' : source);

    if (!ready) {
      el.rows.innerHTML = '';
      el.timer.textContent = '';
      el.stats.innerHTML = '';
      return;
    }

    var view = M.compute(state, comparisonIndex(), performance.now());

    el.rows.hidden = !prefs.showSplits;
    if (prefs.showSplits) {
      var shown = visibleRows(view.rows, view.splitIndex, prefs.rows);
      var lastIndex = view.rows.length - 1;
      el.rows.innerHTML = shown.map(function (r, i) {
        var pinned = i === shown.length - 1 && i > 0 && r.index === lastIndex && shown[i - 1].index !== lastIndex - 1;
        return rowHtml(r, lastIndex, pinned);
      }).join('');
      el.rows.classList.toggle('with-seg', prefs.showSegTimes);
      el.panel.classList.toggle('wide', prefs.showSegTimes);
      // Columns as wide as their longest text, so extra decimals don't get cut off.
      var td = prefs.timeDecimals, dd = prefs.deltaDecimals;
      var longest = function (f) { return shown.reduce(function (n, r) { return Math.max(n, f(r).length); }, 0); };
      el.rows.style.setProperty('--time-w', Math.max(4, longest(function (r) { return M.formatTime(r.time, td); })) + 'ch');
      el.rows.style.setProperty('--delta-w', Math.max(4, longest(function (r) { return M.formatDelta(r.delta, dd); }), 5 + dd) + 'ch');
      el.rows.style.setProperty('--seg-w', Math.max(4, longest(function (r) { return M.formatTime(r.segTime, td); })) + 'ch');
    }

    el.timer.hidden = !prefs.showTimer;
    if (prefs.showTimer) {
      var tcls = 'timer ' + view.phase.toLowerCase();
      var cur = view.rows[view.splitIndex];
      if (view.phase === 'Ended') {
        var fin = view.rows[view.rows.length - 1];
        tcls += fin && fin.delta != null ? (fin.delta < 0 ? ' c-ahead-gaining' : ' c-behind-losing') : '';
      } else if (cur && cur.delta != null) {
        tcls += ' c-' + cur.color;
      } else if (view.lastDelta != null) {
        tcls += view.lastDelta < 0 ? ' c-ahead-gaining' : ' c-behind-losing';
      }
      el.timer.className = tcls;
      el.timer.textContent = M.formatTime(view.time, prefs.timeDecimals);
    }

    var s = '';
    var td2 = prefs.timeDecimals;
    if (prefs.showPrevSeg) {
      var p = view.prevSegment;
      s += stat('Previous Segment', p ? M.formatDelta(p.delta, prefs.deltaDecimals) : '-',
        p ? (p.gold ? 'c-gold' : p.delta < 0 ? 'c-ahead-gaining' : 'c-behind-losing') : '');
    }
    if (prefs.showSoB) s += stat('Sum of Best', M.formatTime(view.sumOfBest, td2));
    if (prefs.showBPT) s += stat('Best Possible Time', M.formatTime(view.bestPossible, td2));
    if (prefs.showTimeSave) s += stat('Possible Time Save', M.formatTime(view.possibleTimeSave, td2));
    if (prefs.showPredicted) s += stat('Predicted Time', M.formatTime(view.predicted, td2));
    el.stats.innerHTML = s;
    el.stats.hidden = !s;
  }

  syncSettingsForm();
  applyChrome();
  render();
  // The running timer (hundredths) and live deltas need a steady repaint.
  setInterval(function () { if (state.timer.phase === 'Running') render(); }, 50);
})();
