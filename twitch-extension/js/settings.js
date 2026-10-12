// Per-viewer overlay preferences, kept in this viewer's browser only.
(function (root) {
  'use strict';

  var KEY = 'pogsplit.viewer.v1';
  var DEFAULTS = {
    open: true,           // panel shown or collapsed to the launcher button
    comparison: null,     // comparison name; null follows the streamer's current one
    rows: 8,              // splits shown at once; 0 = all
    showSplits: true,
    showDeltas: true,
    showSegTimes: false,
    showTimer: true,
    showSoB: true,
    showBPT: true,
    showPrevSeg: true,
    showTimeSave: false,
    showPredicted: false,
    position: 'tr',       // tl | tr | bl | br | custom (dragged to posX/posY)
    posX: 0,              // custom position: left edge as a fraction of the player width
    posY: 0,              // custom position: top edge as a fraction of the player height
    scale: 1,             // panel size, 0.6 to 2.5; text and spacing scale with it
    deltaDecimals: 1,
    timeDecimals: 2,
  };

  // Storage can throw (blocked third-party data, private windows); the overlay must still work.
  function load() {
    var saved = {};
    try { saved = JSON.parse(localStorage.getItem(KEY) || '{}') || {}; } catch (e) { saved = {}; }
    var out = {};
    for (var k in DEFAULTS) out[k] = k in saved && typeof saved[k] === typeof DEFAULTS[k] ? saved[k] : DEFAULTS[k];
    if (typeof saved.comparison === 'string') out.comparison = saved.comparison;
    return out;
  }

  function save(prefs) {
    try { localStorage.setItem(KEY, JSON.stringify(prefs)); } catch (e) { /* not persisted this time */ }
  }

  function reset() {
    try { localStorage.removeItem(KEY); } catch (e) { /* ignore */ }
    return load();
  }

  root.PogSettings = { load: load, save: save, reset: reset, DEFAULTS: DEFAULTS };
})(self);
