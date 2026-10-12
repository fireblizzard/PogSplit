// Pure run-state model: applies feed messages and derives everything the overlay shows.
// No DOM access, so it runs in node for tests (see test/model.test.js).
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.PogModel = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  function createState() {
    return {
      ready: false,
      offline: false,
      run: null,  // content hash of the run; a change means the snapshot is stale
      seq: null,
      game: '',
      category: '',
      attempts: 0,
      timingMethod: 'RealTime',
      comparisons: [],
      segments: [],
      splits: [],
      timer: { phase: 'NotRunning', time: 0, splitIndex: -1 },
      receivedAt: 0,
    };
  }

  function isNum(x) { return typeof x === 'number' && isFinite(x); }
  function orNull(x) { return isNum(x) ? x : null; }

  // Returns true when the message changed the state. serverNow (optional) is the EBS clock
  // right now; with it and timer.at, the timer is corrected for delivery delay.
  function apply(state, msg, now, serverNow) {
    if (msg && msg.v === 1 && msg.type === 'offline') {
      // Freeze the timer where it was; the run resumes with the next snapshot.
      state.timer = { phase: state.timer.phase === 'Running' ? 'Paused' : state.timer.phase,
                      time: currentTime(state, now), splitIndex: state.timer.splitIndex };
      state.receivedAt = now;
      state.offline = true;
      return true;
    }
    if (!msg || msg.v !== 1 || !msg.timer) return false;
    if (msg.type === 'snapshot') {
      if (!Array.isArray(msg.comparisons) || !Array.isArray(msg.segments)) return false;
      state.game = msg.game || '';
      state.category = msg.category || '';
      state.attempts = msg.attempts | 0;
      state.timingMethod = msg.timingMethod === 'GameTime' ? 'GameTime' : 'RealTime';
      state.comparisons = msg.comparisons.map(String);
      state.segments = msg.segments.map(function (s) {
        return {
          name: String(s.name || ''),
          best: orNull(s.best),
          cmp: state.comparisons.map(function (_, c) { return orNull(s.cmp && s.cmp[c]); }),
        };
      });
      state.splits = (msg.splits || []).map(orNull);
      state.ready = true;
    } else if (msg.type === 'update') {
      if (!state.ready) return false; // an update means nothing without a snapshot
      if (Array.isArray(msg.splits)) state.splits = msg.splits.map(orNull);
      if (Array.isArray(msg.best)) {
        msg.best.forEach(function (b, i) { if (state.segments[i]) state.segments[i].best = orNull(b); });
      }
      if (isNum(msg.attempts)) state.attempts = msg.attempts;
    } else {
      return false;
    }
    var t = msg.timer;
    var time = isNum(t.time) ? t.time : 0;
    if (t.phase === 'Running' && isNum(t.at) && isNum(serverNow)) time += Math.max(0, serverNow - t.at);
    state.offline = false;
    if (typeof msg.run === 'string') state.run = msg.run;
    if (isNum(msg.seq)) state.seq = msg.seq;
    state.timer = {
      phase: t.phase || 'NotRunning',
      time: time,
      splitIndex: isNum(t.splitIndex) ? t.splitIndex : state.splits.length,
    };
    // Extrapolate from local receive time, so streamer/viewer clock skew doesn't matter.
    state.receivedAt = now;
    return true;
  }

  function currentTime(state, now) {
    var t = state.timer;
    return t.phase === 'Running' ? t.time + Math.max(0, now - state.receivedAt) : t.time;
  }

  function lastNonNull(arr, before) {
    for (var i = Math.min(before, arr.length) - 1; i >= 0; i--) if (arr[i] != null) return { i: i, v: arr[i] };
    return { i: -1, v: 0 };
  }

  // Delta colors follow LiveSplit's defaults.
  function deltaColor(delta, prevDelta) {
    var gaining = prevDelta != null && delta < prevDelta;
    if (delta < 0) return gaining || prevDelta == null ? 'ahead-gaining' : 'ahead-losing';
    return gaining ? 'behind-gaining' : 'behind-losing';
  }

  function sum(list) {
    var total = 0;
    for (var i = 0; i < list.length; i++) { if (list[i] == null) return null; total += list[i]; }
    return total;
  }

  // Builds the view for one comparison. cmpIndex indexes state.comparisons.
  function compute(state, cmpIndex, now) {
    var segs = state.segments;
    var n = segs.length;
    var phase = state.timer.phase;
    var active = phase === 'Running' || phase === 'Paused';
    var idx = phase === 'NotRunning' ? -1 : Math.min(state.timer.splitIndex, n);
    var t = currentTime(state, now);
    var cmpOf = function (i) { return i >= 0 && i < n ? segs[i].cmp[cmpIndex] : null; };

    var rows = [];
    var lastDelta = null; // latest delta among completed splits
    var lastDoneSplit = 0;
    for (var i = 0; i < n; i++) {
      var seg = segs[i];
      var cmpTime = cmpOf(i);
      var row = { index: i, name: seg.name, cmpTime: cmpTime, best: seg.best, status: 'future',
                  time: cmpTime, delta: null, segTime: null, color: null };
      var done = idx >= 0 && i < idx && i < state.splits.length;
      if (done) {
        var split = state.splits[i];
        if (split == null) {
          row.status = 'skipped';
          row.time = null;
        } else {
          var prev = lastNonNull(state.splits, i);
          var contiguous = prev.i === i - 1;
          row.status = 'done';
          row.time = split;
          row.segTime = contiguous ? split - prev.v : null;
          if (cmpTime != null) {
            row.delta = split - cmpTime;
            row.color = deltaColor(row.delta, lastDelta);
            lastDelta = row.delta;
          }
          if (row.segTime != null && seg.best != null && row.segTime <= seg.best) row.color = 'gold';
          lastDoneSplit = split;
        }
      } else if (i === idx && active) {
        var liveSeg = t - lastNonNull(state.splits, i).v;
        row.status = 'current';
        row.segTime = liveSeg;
        if (cmpTime != null) {
          var liveDelta = t - cmpTime;
          var slowerThanGold = seg.best != null && liveSeg > seg.best;
          if (liveDelta > 0 || (slowerThanGold && lastDelta != null && liveDelta > lastDelta)) {
            row.delta = liveDelta;
            row.color = deltaColor(liveDelta, lastDelta);
            row.live = true;
          }
        }
      }
      rows.push(row);
    }

    var bests = segs.map(function (s) { return s.best; });
    var sumOfBest = sum(bests);
    var bestPossible = sumOfBest;
    var finalCmp = cmpOf(n - 1);
    if (phase === 'Ended') {
      bestPossible = lastNonNull(state.splits, n).v;
    } else if (active && idx >= 0 && idx < n) {
      var base = lastNonNull(state.splits, idx).v;
      var curBest = bests[idx];
      var rest = sum(bests.slice(idx + 1));
      bestPossible = curBest == null || rest == null ? null
        : base + Math.max(t - base, curBest) + rest;
    }

    // Previous segment: last completed segment vs. that segment in the comparison.
    var prevSegment = null;
    if (idx > 0) {
      var p = idx - 1;
      var pr = rows[p];
      var cmpSeg = cmpOf(p) != null && (p === 0 ? 0 : cmpOf(p - 1)) != null
        ? cmpOf(p) - (p === 0 ? 0 : cmpOf(p - 1)) : null;
      if (pr && pr.segTime != null && cmpSeg != null) {
        prevSegment = { delta: pr.segTime - cmpSeg, gold: pr.color === 'gold' };
      }
    }

    var timeSave = null;
    if (active && idx >= 0 && idx < n) {
      var curCmpSeg = cmpOf(idx) != null && (idx === 0 ? 0 : cmpOf(idx - 1)) != null
        ? cmpOf(idx) - (idx === 0 ? 0 : cmpOf(idx - 1)) : null;
      if (curCmpSeg != null && bests[idx] != null) timeSave = Math.max(0, curCmpSeg - bests[idx]);
    }

    var predicted = finalCmp;
    if (phase === 'Ended') predicted = lastNonNull(state.splits, n).v;
    else if (finalCmp != null && lastDelta != null) predicted = finalCmp + lastDelta;

    return {
      ready: state.ready,
      phase: phase,
      splitIndex: idx,
      time: t,
      rows: rows,
      lastDoneSplit: lastDoneSplit,
      lastDelta: lastDelta,
      sumOfBest: sumOfBest,
      bestPossible: bestPossible,
      prevSegment: prevSegment,
      possibleTimeSave: timeSave,
      comparisonFinal: finalCmp,
      predicted: predicted,
    };
  }

  function pad(n, w) { n = String(n); while (n.length < w) n = '0' + n; return n; }

  // 1:02:03.4 / 2:03.4 / 3.4; digits = decimals (0-2).
  function formatTime(ms, digits) {
    if (ms == null) return '-';
    if (digits == null) digits = 1;
    var neg = ms < 0;
    ms = Math.abs(ms);
    var unit = Math.pow(10, 3 - digits);
    var total = Math.floor(ms / unit);
    var frac = total % Math.pow(10, digits);
    var s = Math.floor(ms / 1000);
    var h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, sec = s % 60;
    var out = h > 0 ? h + ':' + pad(m, 2) + ':' + pad(sec, 2)
      : m > 0 ? m + ':' + pad(sec, 2) : String(sec);
    if (digits > 0) out += '.' + pad(frac, digits);
    return (neg ? '−' : '') + out;
  }

  // +1.2 / −12.3 / +1:02; digits = decimals under a minute (default 1), dropped above like LiveSplit.
  function formatDelta(ms, digits) {
    if (ms == null) return '';
    if (digits == null) digits = 1;
    var sign = ms < 0 ? '−' : '+';
    var abs = Math.abs(ms);
    return sign + formatTime(abs, abs < 60000 ? digits : 0);
  }

  return {
    createState: createState,
    apply: apply,
    currentTime: currentTime,
    compute: compute,
    formatTime: formatTime,
    formatDelta: formatDelta,
  };
});
