#!/usr/bin/env node
// Local stand-in for the PogSplit EBS, for testing the Twitch Bridge component without Cloudflare or Twitch.
// Usage: node mock-ebs.js [port]   then set the component's Backend URL to http://localhost:<port>
// Pairing auto-approves after the first poll. Every message PogSplit sends is logged, and the latest state
// is served at GET /v1/channels/local/state like the real EBS would.
'use strict';
const http = require('http');
const crypto = require('crypto');

const port = Number(process.argv[2] || 8787);
const devices = new Map(); // device_code -> { userCode, polls }
const tokens = new Set();
let latest = null; // snapshot with updates folded in
let history = null; // run history, while the streamer shares it
let seq = 0;

function send(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*', Date: new Date().toUTCString() });
  res.end(body === undefined ? '' : JSON.stringify(body));
}

function readJson(req) {
  return new Promise((resolve, reject) => {
    let data = '';
    req.on('data', (c) => (data += c));
    req.on('end', () => {
      try { resolve(data ? JSON.parse(data) : {}); } catch (e) { reject(e); }
    });
  });
}

function bearer(req) {
  const h = req.headers.authorization || '';
  return h.startsWith('Bearer ') ? h.slice(7) : null;
}

function apply(msg) {
  if (msg.type === 'snapshot' || msg.type === 'offline' || !latest || latest.type === 'offline') {
    latest = { ...msg };
  } else if (msg.run === latest.run) {
    if (msg.splits) latest.splits = msg.splits;
    if (msg.timer) latest.timer = msg.timer;
    if (msg.best) msg.best.forEach((b, i) => latest.segments[i] && (latest.segments[i].best = b));
  }
  latest.seq = ++seq;
}

function describe(msg) {
  const t = msg.timer ? ` ${msg.timer.phase} ${(msg.timer.time / 1000).toFixed(2)}s split ${msg.timer.splitIndex}` : '';
  const fx = msg.fx ? ` fx=${msg.fx.map((f) => `${f.k}@${f.i}`).join(',')}` : '';
  const extra = msg.type === 'snapshot' ? ` ${msg.game} / ${msg.category}, ${msg.segments.length} segments, comparisons: ${msg.comparisons.join(', ')}` : '';
  return `#${msg.seq} ${msg.type} run=${msg.run || '-'}${t}${fx}${extra} (${Buffer.byteLength(JSON.stringify(msg))} bytes)`;
}

http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://localhost:${port}`);
  try {
    if (req.method === 'OPTIONS') return send(res, 204);

    if (req.method === 'POST' && url.pathname === '/v1/device') {
      const deviceCode = crypto.randomBytes(16).toString('hex');
      const userCode = crypto.randomBytes(4).toString('hex').toUpperCase().replace(/(.{4})/, '$1-');
      devices.set(deviceCode, { userCode, polls: 0 });
      console.log(`pairing code ${userCode} (auto-approved on next poll)`);
      return send(res, 200, { device_code: deviceCode, user_code: userCode, interval: 2, expires_in: 600 });
    }

    if (req.method === 'POST' && url.pathname === '/v1/device/token') {
      const { device_code: deviceCode } = await readJson(req);
      const device = devices.get(deviceCode);
      if (!device) return send(res, 410, { error: 'expired_token' });
      if (device.polls++ < 1) return send(res, 428, { error: 'authorization_pending' });
      devices.delete(deviceCode);
      const token = crypto.randomBytes(32).toString('base64url');
      tokens.add(token);
      console.log('paired');
      return send(res, 200, { token, channel_id: 'local', channel_name: 'local test channel' });
    }

    if (req.method === 'DELETE' && url.pathname === '/v1/device/token') {
      tokens.delete(bearer(req));
      console.log('pairing revoked');
      return send(res, 204);
    }

    if (req.method === 'PUT' && url.pathname === '/v1/state') {
      if (!tokens.has(bearer(req))) return send(res, 401, { error: 'unauthorized' });
      const msg = await readJson(req);
      if (msg.v !== 1 || !['snapshot', 'update', 'offline'].includes(msg.type)) return send(res, 400, { error: 'bad message' });
      console.log(describe(msg));
      apply(msg);
      return send(res, 204);
    }

    if ((req.method === 'PUT' || req.method === 'DELETE') && url.pathname === '/v1/history') {
      if (!tokens.has(bearer(req))) return send(res, 401, { error: 'unauthorized' });
      history = req.method === 'PUT' ? { ...(await readJson(req)), updatedAt: Date.now() } : null;
      console.log(history
        ? `history: ${history.attempts.length} attempts, ${history.finished.length} finished, ${history.golds.length} golds`
        : 'history no longer shared');
      return send(res, 204);
    }

    if (req.method === 'GET' && url.pathname === '/v1/channels/local/state') {
      return latest ? send(res, 200, latest) : send(res, 404, { error: 'no state yet' });
    }

    if (req.method === 'GET' && url.pathname === '/v1/channels/local/history') {
      return history ? send(res, 200, history) : send(res, 404, { error: 'history not shared' });
    }

    send(res, 404, { error: 'not found' });
  } catch (e) {
    send(res, 400, { error: String(e.message || e) });
  }
}).listen(port, () => console.log(`mock EBS on http://localhost:${port}`));
