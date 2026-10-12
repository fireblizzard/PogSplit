// Mock PogSplit feed: simulates attempts of a run and emits v1 schema messages
// (see schema/run-state.schema.json), exactly as the real bridge will via PubSub.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.PogMockFeed = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  // [name, PB segment, gold segment, average segment] in seconds.
  var RUN = {
    game: 'Super Mario 64',
    category: '16 Star',
    segments: [
      ['Bob-omb Battlefield', 70.4, 66.1, 74.9],
      ['Whomp’s Fortress', 95.2, 90.3, 101.7],
      ['Cool, Cool Mountain', 60.8, 57.2, 65.3],
      ['Bowser in the Dark World', 110.5, 104.0, 118.2],
      ['Lethal Lava Land', 85.1, 80.6, 92.4],
      ['Shifting Sand Land', 75.9, 70.2, 82.0],
      ['Hazy Maze Cave', 120.3, 112.8, 130.6],
      ['Dire, Dire Docks', 65.0, 61.4, 70.1],
      ['Bowser in the Fire Sea', 140.7, 131.5, 151.9],
      ['BLJs', 25.6, 22.3, 30.4],
      ['Bowser in the Sky', 145.2, 138.4, 156.8],
    ],
  };

  function cumulative(list) {
    var t = 0;
    return list.map(function (x) { t += x; return Math.round(t); });
  }

  function createMockFeed(opts) {
    var speed = opts.speed || 1;
    var emit = opts.onMessage;
    var rand = opts.random || Math.random;
    var clock = opts.now || function () { return Date.now(); };

    var segs = RUN.segments.map(function (s) {
      return { name: s[0], pb: s[1] * 1000, best: s[2] * 1000, avg: s[3] * 1000 };
    });
    var attempts = 412;
    var splits = [];
    var phase = 'NotRunning';
    var startedAt = 0; // in simulated ms
    var pausedTime = 0;
    var plan = []; // planned segment durations for this attempt
    var timers = [];

    // Simulated time runs `speed` times faster than the wall clock.
    var epoch = clock();
    function simNow() { return (clock() - epoch) * speed; }
    function runTime() {
      if (phase === 'Running') return Math.round(simNow() - startedAt);
      return pausedTime;
    }

    function comparisons() {
      return [
        { name: 'Personal Best', seg: segs.map(function (s) { return s.pb; }) },
        { name: 'Best Segments', seg: segs.map(function (s) { return s.best; }) },
        { name: 'Average Segments', seg: segs.map(function (s) { return s.avg; }) },
      ];
    }

    function timer() {
      return { phase: phase, time: runTime(), splitIndex: phase === 'NotRunning' ? -1 : splits.length };
    }

    function snapshot() {
      var cmps = comparisons();
      var cum = cmps.map(function (c) { return cumulative(c.seg); });
      return {
        v: 1,
        type: 'snapshot',
        game: RUN.game,
        category: RUN.category,
        attempts: attempts,
        timingMethod: 'RealTime',
        comparisons: cmps.map(function (c) { return c.name; }),
        segments: segs.map(function (s, i) {
          return { name: s.name, best: Math.round(s.best), cmp: cum.map(function (c) { return c[i]; }) };
        }),
        splits: splits.slice(),
        timer: timer(),
      };
    }

    function send(msg) { emit(JSON.parse(JSON.stringify(msg))); }
    function update(extra) {
      var msg = { v: 1, type: 'update', splits: splits.slice(), attempts: attempts, timer: timer() };
      for (var k in extra) msg[k] = extra[k];
      send(msg);
    }

    // Each segment lands between gold and slightly worse than average; ~12% chance of a gold.
    function planSegment(s) {
      if (rand() < 0.12) return s.best * (0.97 + rand() * 0.03);
      return s.best + (s.avg - s.best) * (0.2 + rand() * 1.1);
    }

    function later(fn, simMs) { timers.push(setTimeout(fn, simMs / speed)); }
    function clearTimers() { timers.forEach(clearTimeout); timers = []; }

    function start() {
      clearTimers();
      attempts++;
      splits = [];
      plan = segs.map(planSegment);
      phase = 'Running';
      startedAt = simNow();
      send(snapshot());
      scheduleNext();
    }

    function scheduleNext() {
      var i = splits.length;
      // 4% chance per segment of a reset, the speedrunner's way of life.
      if (rand() < 0.04 && i > 0) {
        later(reset, plan[i] * (0.3 + rand() * 0.5));
        return;
      }
      var due = startedAt + plan.slice(0, i + 1).reduce(function (a, b) { return a + b; }, 0);
      later(split, Math.max(0, due - simNow()));
    }

    function split() {
      if (phase !== 'Running') return;
      var i = splits.length;
      var t = runTime();
      splits.push(t);
      var segTime = t - (i ? splits[i - 1] : 0);
      var extra = {};
      if (segTime < segs[i].best) {
        segs[i].best = segTime;
        extra.best = segs.map(function (s) { return Math.round(s.best); });
      }
      if (splits.length === segs.length) {
        phase = 'Ended';
        pausedTime = t;
        var pb = cumulative(segs.map(function (s) { return s.pb; }));
        if (t < pb[pb.length - 1]) {
          // New PB: the comparison itself changes, so resend the whole run.
          splits.forEach(function (st, j) { segs[j].pb = st - (j ? splits[j - 1] : 0); });
          send(snapshot());
        } else {
          update(extra);
        }
        later(function () { reset(); }, 12000);
        return;
      }
      update(extra);
      scheduleNext();
    }

    function reset() {
      clearTimers();
      phase = 'NotRunning';
      splits = [];
      pausedTime = 0;
      update({});
      later(start, 6000);
    }

    var heartbeat = null;
    return {
      start: function () {
        start();
        // The real bridge should resend periodically so late joiners and drifted clocks recover.
        heartbeat = setInterval(function () { send(snapshot()); }, 15000);
      },
      stop: function () { clearTimers(); clearInterval(heartbeat); },
      snapshot: snapshot,
    };
  }

  // Demo run history in the bridge's history format, for the overlay's history tabs.
  function createMockHistory(opts) {
    opts = opts || {};
    var rand = opts.random || Math.random;
    var now = opts.now || Date.now();
    var segs = RUN.segments;
    var n = segs.length;
    var best = segs.map(function (s) { return s[2] * 1000 * 1.06; });
    var attempts = [], finished = [], golds = [];
    var t0 = now - 60 * 60 * 1000 * 6;

    for (var id = 380; id <= 412; id++) {
      var reach = rand() < 0.3 ? n : Math.floor(rand() * n);
      var splits = [], total = 0;
      for (var i = 0; i < reach; i++) {
        var seg = segs[i][2] * 1000 * (1 + rand() * 0.12);
        if (seg < best[i]) {
          golds.push({ seg: i, id: id, time: Math.round(seg), prev: Math.round(best[i]) });
          best[i] = seg;
        }
        total += seg;
        splits.push(Math.round(total));
      }
      var started = t0 + (id - 380) * 9 * 60 * 1000;
      var ended = started + total + 2000;
      golds.forEach(function (g) { if (g.id === id) g.date = ended; });
      var done = reach === n;
      attempts.push({ id: id, started: started, ended: ended, time: done ? splits[n - 1] : undefined, reached: reach, last: splits[reach - 1] });
      if (done) finished.push({ id: id, started: started, ended: ended, time: splits[n - 1], splits: splits });
    }

    var newest = function (a, b) { return b.id - a.id || b.seg - a.seg; };
    return {
      v: 1,
      timingMethod: 'RealTime',
      segments: segs.map(function (s) { return s[0]; }),
      attempts: attempts.reverse(),
      finished: finished.reverse().slice(0, 10),
      golds: golds.sort(newest).slice(0, 50),
      updatedAt: now,
    };
  }

  return { createMockFeed: createMockFeed, createMockHistory: createMockHistory, RUN: RUN };
});
