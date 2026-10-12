// Harness for dev.html: hosts video_overlay.html in a fake player and can inject messages.
(function () {
  'use strict';
  var frame = document.getElementById('overlay');
  var speed = document.getElementById('speed');
  var width = document.getElementById('width');
  var controls = document.getElementById('controls');
  var json = document.getElementById('json');
  var status = document.getElementById('send-status');
  var player = document.querySelector('.player');

  function post(data) {
    data.pogsplit = true;
    frame.contentWindow.postMessage(data, location.origin);
  }

  function load() {
    frame.src = 'video_overlay.html?dev=1&mock=1&speed=' + speed.value;
  }

  frame.addEventListener('load', function () { post({ controls: controls.checked }); });
  speed.addEventListener('change', load);
  document.getElementById('reload').addEventListener('click', load);
  width.addEventListener('change', function () { player.style.width = width.value + 'px'; });
  controls.addEventListener('change', function () {
    document.getElementById('fake-controls').style.opacity = controls.checked ? 1 : 0;
    post({ controls: controls.checked });
  });

  document.getElementById('load-sample').addEventListener('click', function () {
    fetch('mock/sample-snapshot.json').then(function (r) { return r.text(); }).then(function (t) {
      json.value = t;
      status.textContent = '';
    }).catch(function () { status.textContent = 'Serve this folder over http to load the sample.'; });
  });

  document.getElementById('send').addEventListener('click', function () {
    var msg;
    try { msg = JSON.parse(json.value); } catch (e) { status.textContent = 'Not valid JSON: ' + e.message; return; }
    var bytes = new Blob([JSON.stringify(msg)]).size;
    post({ msg: msg });
    status.textContent = 'Sent (' + bytes + ' bytes' + (bytes > 5120 ? ', over the 5 KB PubSub limit!' : '') + ').';
  });

  player.style.width = width.value + 'px';
  load();
})();
