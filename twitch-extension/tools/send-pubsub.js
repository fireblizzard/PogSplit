// Sends one schema message to a channel's overlay through Twitch Extension PubSub,
// the same path PogSplit's bridge will use. No dependencies.
//
//   TWITCH_CLIENT_ID=... TWITCH_EXT_SECRET=... TWITCH_OWNER_ID=... TWITCH_CHANNEL_ID=... \
//     node tools/send-pubsub.js mock/sample-snapshot.json
//
// TWITCH_EXT_SECRET is the base64 extension secret from the Extensions console
// (Extension Settings > Extension Secrets). Keep it out of the repo.
'use strict';
var crypto = require('crypto');
var fs = require('fs');

var env = process.env;
['TWITCH_CLIENT_ID', 'TWITCH_EXT_SECRET', 'TWITCH_OWNER_ID', 'TWITCH_CHANNEL_ID'].forEach(function (k) {
  if (!env[k]) { console.error('Missing ' + k); process.exit(1); }
});
var file = process.argv[2] || 'mock/sample-snapshot.json';
var message = JSON.stringify(JSON.parse(fs.readFileSync(file, 'utf8')));
if (Buffer.byteLength(message) > 5120) { console.error('Message is over the 5 KB PubSub limit'); process.exit(1); }

function b64url(buf) { return Buffer.from(buf).toString('base64').replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_'); }
var header = b64url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
var payload = b64url(JSON.stringify({
  exp: Math.floor(Date.now() / 1000) + 60,
  user_id: env.TWITCH_OWNER_ID,
  role: 'external',
  channel_id: env.TWITCH_CHANNEL_ID,
  pubsub_perms: { send: ['broadcast'] },
}));
var sig = b64url(crypto.createHmac('sha256', Buffer.from(env.TWITCH_EXT_SECRET, 'base64')).update(header + '.' + payload).digest());

fetch('https://api.twitch.tv/helix/extensions/pubsub', {
  method: 'POST',
  headers: {
    'Authorization': 'Bearer ' + header + '.' + payload + '.' + sig,
    'Client-Id': env.TWITCH_CLIENT_ID,
    'Content-Type': 'application/json',
  },
  body: JSON.stringify({
    target: ['broadcast'],
    broadcaster_id: env.TWITCH_CHANNEL_ID,
    is_global_broadcast: false,
    message: message,
  }),
}).then(function (r) {
  return r.text().then(function (t) { console.log(r.status === 204 ? 'Sent.' : r.status + ' ' + t); });
});
