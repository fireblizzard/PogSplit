// PogSplit Extension Backend Service (Cloudflare Worker).
// PogSplit PUTs run state here; one Durable Object per channel throttles it onto Twitch Extension PubSub.
// The API is described in components/PogSplit.TwitchBridge/README.md and ebs/README.md.
import { verify } from './jwt.js';

export { ChannelState } from './channel.js';
export { Pairing } from './pairing.js';

const MAX_HISTORY_BYTES = 256 * 1024;

const CORS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, OPTIONS',
  'Access-Control-Allow-Headers': 'Authorization, Content-Type, X-Sent-At',
  'Access-Control-Max-Age': '86400',
};

export function json(status, body) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store', ...CORS },
  });
}

function bearer(request) {
  const h = request.headers.get('Authorization') || '';
  return h.startsWith('Bearer ') ? h.slice(7).trim() : null;
}

// Bridge tokens look like "<channel id>.<random>", so each request goes straight to its channel's object.
function channelOfToken(token) {
  const m = /^(\d+)\.[0-9a-f]{64}$/.exec(token || '');
  return m ? m[1] : null;
}

async function body(request) {
  try {
    return await request.json();
  } catch {
    return null;
  }
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const { pathname } = url;
    const method = request.method;

    if (method === 'OPTIONS') return new Response(null, { status: 204, headers: CORS });

    const pairing = () => env.PAIRING.get(env.PAIRING.idFromName('global'));
    const channel = (id) => env.CHANNEL.get(env.CHANNEL.idFromName(id));

    // PogSplit starts pairing and gets a code to show the streamer.
    if (method === 'POST' && pathname === '/v1/device') {
      return json(200, await pairing().start());
    }

    // PogSplit polls until the streamer has entered the code.
    if (method === 'POST' && pathname === '/v1/device/token') {
      const b = await body(request);
      if (!b || typeof b.device_code !== 'string') return json(400, { error: 'device_code required' });
      const r = await pairing().poll(b.device_code);
      if (r.status === 'pending') return json(428, { error: 'authorization_pending' });
      if (r.status === 'expired') return json(410, { error: 'expired_token' });
      return json(200, r.pairing);
    }

    // The extension's config page confirms a code for the broadcaster's own channel.
    if (method === 'POST' && pathname === '/v1/device/confirm') {
      const claims = await verify(bearer(request), env.EXTENSION_SECRET);
      if (!claims || claims.role !== 'broadcaster' || !claims.channel_id) return json(401, { error: 'broadcaster token required' });
      const b = await body(request);
      if (!b || typeof b.user_code !== 'string') return json(400, { error: 'user_code required' });
      const name = typeof b.channel_name === 'string' ? b.channel_name.slice(0, 64) : null;
      const ok = await pairing().confirm(b.user_code, String(claims.channel_id), name);
      return ok ? json(204) : json(404, { error: 'unknown or expired code' });
    }

    // PogSplit (Disconnect) or the config page revokes a bridge token.
    if (method === 'DELETE' && pathname === '/v1/device/token') {
      const token = bearer(request);
      const id = channelOfToken(token);
      if (id) await channel(id).revoke(token);
      return json(204);
    }

    // The config page revokes every bridge token of its channel.
    if (method === 'DELETE' && pathname === '/v1/channel/tokens') {
      const claims = await verify(bearer(request), env.EXTENSION_SECRET);
      if (!claims || claims.role !== 'broadcaster' || !claims.channel_id) return json(401, { error: 'broadcaster token required' });
      await channel(String(claims.channel_id)).revokeAll();
      return json(204);
    }

    // PogSplit sends a snapshot, update or offline message.
    if (method === 'PUT' && pathname === '/v1/state') {
      const token = bearer(request);
      const id = channelOfToken(token);
      if (!id) return json(401, { error: 'unauthorized' });
      const msg = await body(request);
      if (!msg) return json(400, { error: 'invalid JSON' });
      const sentAt = Number(request.headers.get('X-Sent-At')) || null;
      const r = await channel(id).put(token, msg, sentAt, Date.now());
      return json(r.status, r.body);
    }

    // PogSplit stores the run history (PUT) or stops sharing it (DELETE). Too big for PubSub, so the overlay fetches it.
    if ((method === 'PUT' || method === 'DELETE') && pathname === '/v1/history') {
      const token = bearer(request);
      const id = channelOfToken(token);
      if (!id) return json(401, { error: 'unauthorized' });
      let history = null;
      if (method === 'PUT') {
        if (Number(request.headers.get('Content-Length')) > MAX_HISTORY_BYTES) return json(413, { error: 'history too large' });
        history = await body(request);
        if (!history || history.v !== 1 || !Array.isArray(history.attempts)) return json(400, { error: 'invalid history' });
      }
      const r = await channel(id).putHistory(token, history, Date.now());
      return json(r.status, r.body);
    }

    // The overlay fetches the latest state on load, on a seq gap, or when the run hash changes,
    // and the history when a viewer opens a history tab.
    const m = /^\/v1\/channels\/(\d+)\/(state|history)$/.exec(pathname);
    if (method === 'GET' && m) {
      const claims = await verify(bearer(request), env.EXTENSION_SECRET);
      if (!claims || String(claims.channel_id) !== m[1]) return json(401, { error: 'extension token for this channel required' });
      if (m[2] === 'history') {
        const history = await channel(m[1]).getHistory();
        return history ? json(200, history) : json(404, { error: 'history not shared' });
      }
      const state = await channel(m[1]).getState();
      return state ? json(200, state) : json(404, { error: 'no state yet' });
    }

    return json(404, { error: 'not found' });
  },
};
