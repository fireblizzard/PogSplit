// Run with: node test/model.test.js
'use strict';
var assert = require('assert');
var M = require('../js/model.js');

function snap(extra) {
  var msg = {
    v: 1, type: 'snapshot', game: 'G', category: 'C', attempts: 3, timingMethod: 'RealTime',
    comparisons: ['Personal Best', 'Best Segments'],
    segments: [
      { name: 'A', best: 9000, cmp: [10000, 9000] },
      { name: 'B', best: 18000, cmp: [30000, 27000] },
      { name: 'C', best: 9000, cmp: [40000, 36000] },
    ],
    splits: [], timer: { phase: 'Running', time: 0, splitIndex: 0 },
  };
  for (var k in extra) msg[k] = extra[k];
  return msg;
}

var tests = {
  'update before snapshot is ignored': function () {
    var s = M.createState();
    assert.strictEqual(M.apply(s, { v: 1, type: 'update', timer: { phase: 'Running', time: 1 } }, 0), false);
    assert.strictEqual(s.ready, false);
  },
  'timer extrapolates only while running': function () {
    var s = M.createState();
    M.apply(s, snap({ timer: { phase: 'Running', time: 5000, splitIndex: 0 } }), 1000);
    assert.strictEqual(M.currentTime(s, 3500), 7500);
    M.apply(s, { v: 1, type: 'update', timer: { phase: 'Paused', time: 6000, splitIndex: 0 } }, 4000);
    assert.strictEqual(M.currentTime(s, 9000), 6000);
  },
  'deltas, colors and gold per comparison': function () {
    var s = M.createState();
    // A: 9500 (0.5s ahead of PB, not gold); B: 25500 (segment 16000 < best 18000 -> gold)
    M.apply(s, snap({ splits: [9500, 25500], timer: { phase: 'Running', time: 26000, splitIndex: 2 } }), 0);
    var pb = M.compute(s, 0, 0);
    assert.strictEqual(pb.rows[0].delta, -500);
    assert.strictEqual(pb.rows[0].color, 'ahead-gaining');
    assert.strictEqual(pb.rows[1].delta, -4500);
    assert.strictEqual(pb.rows[1].segTime, 16000);
    assert.strictEqual(pb.rows[1].color, 'gold');
    assert.strictEqual(pb.rows[2].status, 'current');
    assert.strictEqual(pb.prevSegment.delta, 16000 - 20000);
    var bs = M.compute(s, 1, 0);
    assert.strictEqual(bs.rows[0].delta, 500);
    assert.strictEqual(bs.rows[0].color, 'behind-losing');
  },
  'sum of best and best possible time': function () {
    var s = M.createState();
    M.apply(s, snap({ splits: [9500], timer: { phase: 'Running', time: 12000, splitIndex: 1 } }), 0);
    var v = M.compute(s, 0, 0);
    assert.strictEqual(v.sumOfBest, 36000);
    // 9500 done + max(2500 elapsed, 18000 gold) + 9000 remaining
    assert.strictEqual(v.bestPossible, 36500);
    assert.strictEqual(v.possibleTimeSave, 20000 - 18000);
    assert.strictEqual(v.predicted, 40000 - 500);
  },
  'live delta appears once behind': function () {
    var s = M.createState();
    M.apply(s, snap({ splits: [], timer: { phase: 'Running', time: 9000, splitIndex: 0 } }), 0);
    assert.strictEqual(M.compute(s, 0, 0).rows[0].delta, null);
    assert.strictEqual(M.compute(s, 0, 2000).rows[0].delta, 1000);
  },
  'skipped split leaves the next segment time unknown': function () {
    var s = M.createState();
    M.apply(s, snap({ splits: [null, 26000], timer: { phase: 'Running', time: 27000, splitIndex: 2 } }), 0);
    var v = M.compute(s, 0, 0);
    assert.strictEqual(v.rows[0].status, 'skipped');
    assert.strictEqual(v.rows[1].segTime, null);
    assert.strictEqual(v.rows[1].delta, -4000);
  },
  'timer.at corrects for delivery delay when the server clock is known': function () {
    var s = M.createState();
    M.apply(s, snap({ timer: { phase: 'Running', time: 5000, at: 100000, splitIndex: 0 } }), 0, 101500);
    assert.strictEqual(M.currentTime(s, 0), 6500);
    M.apply(s, snap({ timer: { phase: 'Running', time: 5000, at: 100000, splitIndex: 0 } }), 0);
    assert.strictEqual(M.currentTime(s, 0), 5000);
  },
  'offline freezes the timer and run/seq are tracked': function () {
    var s = M.createState();
    M.apply(s, snap({ run: 'abc', seq: 4, timer: { phase: 'Running', time: 5000, splitIndex: 0 } }), 0);
    assert.strictEqual(s.run, 'abc');
    assert.strictEqual(s.seq, 4);
    M.apply(s, { v: 1, type: 'offline' }, 2000);
    assert.strictEqual(s.offline, true);
    assert.strictEqual(M.currentTime(s, 9000), 7000);
    M.apply(s, { v: 1, type: 'update', seq: 5, timer: { phase: 'Running', time: 8000, splitIndex: 0 } }, 9000);
    assert.strictEqual(s.offline, false);
  },
  'formatting': function () {
    assert.strictEqual(M.formatTime(3723450, 2), '1:02:03.45');
    assert.strictEqual(M.formatTime(83400, 1), '1:23.4');
    assert.strictEqual(M.formatTime(5000, 0), '5');
    assert.strictEqual(M.formatDelta(-1234), '−1.2');
    assert.strictEqual(M.formatDelta(62000), '+1:02');
    assert.strictEqual(M.formatDelta(-1234, 2), '−1.23');
    assert.strictEqual(M.formatDelta(1234, 0), '+1');
    assert.strictEqual(M.formatDelta(62000, 2), '+1:02');
  },
};

var failed = 0;
Object.keys(tests).forEach(function (name) {
  try { tests[name](); console.log('ok   ' + name); }
  catch (e) { failed++; console.log('FAIL ' + name + '\n     ' + e.message); }
});
process.exit(failed ? 1 : 0);
