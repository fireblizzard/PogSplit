// Broadcaster config view: pairs a PogSplit install with this channel through the EBS.
(function () {
  'use strict';
  var EBS = window.POGSPLIT_EBS || '';
  var twitch = window.Twitch && window.Twitch.ext ? window.Twitch.ext : null;
  var token = null;

  var form = document.getElementById('pair');
  var code = document.getElementById('code');
  var connect = document.getElementById('connect');
  var disconnect = document.getElementById('disconnect');
  var status = document.getElementById('status');

  function show(text, kind) {
    status.textContent = text;
    status.className = 'status ' + (kind || '');
  }

  function busy(on) {
    connect.disabled = disconnect.disabled = on || !token || !EBS;
  }

  // Codes are shown as XXXX-XXXX; accept them typed with or without the dash, any case.
  function normalize(value) {
    var raw = value.toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 8);
    return raw.length > 4 ? raw.slice(0, 4) + '-' + raw.slice(4) : raw;
  }

  code.addEventListener('input', function () {
    var pos = code.value.length === code.selectionStart;
    code.value = normalize(code.value);
    if (pos) code.selectionStart = code.selectionEnd = code.value.length;
  });

  function call(method, path, body) {
    return fetch(EBS + path, {
      method: method,
      headers: Object.assign({ Authorization: 'Bearer ' + token },
        body ? { 'Content-Type': 'application/json' } : {}),
      body: body ? JSON.stringify(body) : undefined,
    });
  }

  form.addEventListener('submit', function (e) {
    e.preventDefault();
    var userCode = normalize(code.value);
    if (userCode.length !== 9) { show('Enter the 8-character code PogSplit shows.', 'error'); return; }
    busy(true);
    show('Connecting…');
    call('POST', '/v1/device/confirm', { user_code: userCode }).then(function (r) {
      if (r.status === 204) { show('Connected. PogSplit will start sending your splits.', 'ok'); code.value = ''; }
      else if (r.status === 404) show('That code didn’t work. Check the code in PogSplit; codes expire after a few minutes.', 'error');
      else if (r.status === 401) show('Only the channel’s broadcaster can connect PogSplit.', 'error');
      else show('Something went wrong (' + r.status + '). Try again.', 'error');
    }).catch(function () {
      show('Couldn’t reach the PogSplit service. Try again.', 'error');
    }).then(function () { busy(false); });
  });

  disconnect.addEventListener('click', function () {
    busy(true);
    show('Disconnecting…');
    call('DELETE', '/v1/channel/tokens').then(function (r) {
      if (r.status === 204) show('Disconnected. Viewers will see PogSplit as offline.', 'ok');
      else if (r.status === 401) show('Only the channel’s broadcaster can disconnect PogSplit.', 'error');
      else show('Something went wrong (' + r.status + '). Try again.', 'error');
    }).catch(function () {
      show('Couldn’t reach the PogSplit service. Try again.', 'error');
    }).then(function () { busy(false); });
  });

  if (!EBS) show('The PogSplit service isn’t set up in this build yet (POGSPLIT_EBS in js/ebs.js).', 'error');

  if (twitch) {
    twitch.onAuthorized(function (auth) {
      token = auth.token;
      busy(false);
    });
    twitch.onContext(function (ctx) {
      if (ctx.theme) document.documentElement.setAttribute('data-theme', ctx.theme);
    });
  } else if (EBS) {
    show('Open this page from the extension’s settings on Twitch to connect.', 'error');
  }
  busy(false);
})();
