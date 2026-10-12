// Writes mock/sample-snapshot.json: a deterministic mid-run snapshot from the mock feed.
// Run with: node test/make-sample.js
'use strict';
var fs = require('fs');
var path = require('path');
var Feed = require('../js/mock-feed.js');

var seed = 7;
function random() { seed = (seed * 16807) % 2147483647; return (seed - 1) / 2147483646; }

var t = 0;
var feed = Feed.createMockFeed({ speed: 1, now: function () { return t; }, random: random, onMessage: function () {} });
var snap = feed.snapshot();
// Fake an attempt five splits in.
var cum = 0;
snap.splits = snap.segments.slice(0, 5).map(function (s, i) {
  cum += Math.round((s.cmp[0] - (i ? snap.segments[i - 1].cmp[0] : 0)) * (0.97 + random() * 0.06));
  return cum;
});
snap.attempts = 413;
snap.timer = { phase: 'Running', time: cum + 31200, splitIndex: 5 };
var out = JSON.stringify(snap, null, 2);
fs.writeFileSync(path.join(__dirname, '..', 'mock', 'sample-snapshot.json'), out + '\n');
console.log('wrote mock/sample-snapshot.json (' + Buffer.byteLength(JSON.stringify(snap)) + ' bytes minified)');
