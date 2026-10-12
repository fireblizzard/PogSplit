import { DurableObject } from 'cloudflare:workers';
import { sha256Hex, sign } from './jwt.js';

const PUBLISH_INTERVAL_MS = 1100; // Twitch allows 1 Extension PubSub message per second per channel.
const HEARTBEAT_MS = 30_000;
const OFFLINE_AFTER_MS = 45_000;
const MAX_PUBSUB_BYTES = 4500; // Twitch's cap is 5 KB; leave room for the envelope.
const TYPES = ['snapshot', 'update', 'offline'];

/**
 * One instance per Twitch channel. Folds PogSplit's messages into the latest state and publishes
 * at most one PubSub message per 1.1 s, plus a heartbeat every 30 s and "offline" after 45 s of silence.
 */
export class ChannelState extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    ctx.blockConcurrencyWhile(async () => {
      const saved = await ctx.storage.get(['meta', 'latest']);
      this.meta = saved.get('meta') || {
        channelId: null,
        tokens: [], // SHA-256 of bridge tokens
        inSeq: 0, // last PogSplit seq accepted
        seq: 0, // last seq published
        online: false,
        lastPutAt: 0,
        lastPublishAt: 0,
        dirty: false, // something changed since the last publish
        snapshotDirty: false, // the run part changed since the last publish
        fx: [],
      };
      this.latest = saved.get('latest') || null; // the current snapshot, updates folded in
    });
  }

  async save() {
    await this.ctx.storage.put({ meta: this.meta, latest: this.latest });
  }

  async addToken(hash, channelId) {
    this.meta.channelId = channelId;
    this.meta.tokens = [...this.meta.tokens.filter((t) => t !== hash), hash].slice(-5);
    await this.save();
  }

  async revoke(token) {
    const hash = await sha256Hex(token);
    this.meta.tokens = this.meta.tokens.filter((t) => t !== hash);
    await this.save();
  }

  async revokeAll() {
    this.meta.tokens = [];
    await this.save();
    await this.ctx.storage.delete('history');
  }

  async putHistory(token, history, now) {
    if (!this.meta.tokens.includes(await sha256Hex(token))) return { status: 401, body: { error: 'unauthorized' } };
    if (history) await this.ctx.storage.put('history', { ...history, updatedAt: now });
    else await this.ctx.storage.delete('history');
    return { status: 204 };
  }

  async getHistory() {
    return (await this.ctx.storage.get('history')) || null;
  }

  async getState() {
    if (!this.latest) return null;
    if (!this.meta.online) return { v: 1, type: 'offline', seq: this.meta.seq };
    return { ...this.latest, seq: this.meta.seq };
  }

  async put(token, msg, sentAt, now) {
    if (!this.meta.tokens.includes(await sha256Hex(token))) return { status: 401, body: { error: 'unauthorized' } };
    if (msg.v !== 1 || !TYPES.includes(msg.type)) return { status: 400, body: { error: 'unsupported message' } };
    if (msg.type !== 'offline' && (!msg.timer || typeof msg.timer.time !== 'number')) return { status: 400, body: { error: 'timer required' } };
    if (msg.type === 'snapshot' && (!Array.isArray(msg.segments) || !Array.isArray(msg.comparisons))) return { status: 400, body: { error: 'snapshot incomplete' } };

    // Drop stale requests, but accept a restarted PogSplit (its seq starts again at 1 with a snapshot).
    const seq = Number(msg.seq) || 0;
    if (seq <= this.meta.inSeq && msg.type !== 'snapshot' && seq !== 1) return { status: 202, body: { stale: true } };
    this.meta.inSeq = seq;

    if (msg.timer) msg.timer.at = rebaseClock(msg.timer.at, sentAt, now);
    if (Array.isArray(msg.fx)) this.meta.fx.push(...msg.fx.slice(0, 20));
    this.meta.lastPutAt = now;

    if (msg.type === 'offline') {
      this.meta.online = false;
    } else {
      this.meta.online = true;
      this.fold(msg);
    }

    this.meta.dirty = true;
    await this.save();
    await this.schedule(now);
    return { status: 204 };
  }

  fold(msg) {
    if (msg.type === 'snapshot' || !this.latest) {
      const { seq, fx, ...rest } = msg;
      if (msg.type === 'snapshot') {
        this.latest = { ...rest, type: 'snapshot' };
        this.meta.snapshotDirty = true;
      }
      return;
    }

    if (msg.run !== this.latest.run) return; // an update for a run we have no snapshot of; PogSplit sends one next
    if (msg.splits) this.latest.splits = msg.splits;
    if (msg.timer) this.latest.timer = msg.timer;
    if (typeof msg.attempts === 'number') this.latest.attempts = msg.attempts;
    if (Array.isArray(msg.best)) msg.best.forEach((b, i) => this.latest.segments[i] && (this.latest.segments[i].best = b));
  }

  async schedule(now) {
    const m = this.meta;
    let next = Infinity;
    if (m.dirty) next = Math.max(now, m.lastPublishAt + PUBLISH_INTERVAL_MS);
    if (m.online) next = Math.min(next, m.lastPublishAt + HEARTBEAT_MS, m.lastPutAt + OFFLINE_AFTER_MS);
    if (next === Infinity) return;
    if (next <= now) {
      await this.alarm();
      return;
    }
    const current = await this.ctx.storage.getAlarm();
    if (current === null || current > next) await this.ctx.storage.setAlarm(next);
  }

  async alarm() {
    const now = Date.now();
    const m = this.meta;

    if (m.online && now - m.lastPutAt >= OFFLINE_AFTER_MS) {
      m.online = false;
      m.dirty = true;
    }

    const due = m.dirty ? now - m.lastPublishAt >= PUBLISH_INTERVAL_MS : m.online && now - m.lastPublishAt >= HEARTBEAT_MS;
    if (due && this.latest !== null) {
      const result = await this.publish(now);
      if (result.retryAt) {
        await this.save();
        await this.ctx.storage.setAlarm(result.retryAt);
        return;
      }
    } else if (due) {
      m.dirty = false;
    }

    await this.save();
    if (m.dirty || m.online) await this.schedule(Date.now());
  }

  outgoing(now) {
    const m = this.meta;
    const seq = m.seq + 1;
    if (!m.online) return { v: 1, type: 'offline', seq };

    const timer = extrapolate(this.latest.timer, now);
    const fx = m.fx.length ? m.fx : undefined;
    if (m.snapshotDirty) {
      const snapshot = { ...this.latest, timer, seq, fx };
      if (JSON.stringify(snapshot).length <= MAX_PUBSUB_BYTES) return snapshot;
    }

    // Updates and oversized snapshots: the overlay refetches /state when the run hash is new to it.
    return { v: 1, type: 'update', run: this.latest.run, seq, attempts: this.latest.attempts, splits: this.latest.splits, timer, fx };
  }

  async publish(now) {
    const m = this.meta;
    const message = this.outgoing(now);
    const env = this.env;
    const jwt = await sign(
      {
        exp: Math.floor(now / 1000) + 60,
        user_id: String(env.OWNER_ID),
        role: 'external',
        channel_id: String(m.channelId),
        pubsub_perms: { send: ['broadcast'] },
      },
      env.EXTENSION_SECRET,
    );

    let response;
    try {
      response = await fetch(`${env.TWITCH_API_BASE || 'https://api.twitch.tv'}/helix/extensions/pubsub`, {
        method: 'POST',
        headers: { Authorization: `Bearer ${jwt}`, 'Client-Id': env.EXTENSION_CLIENT_ID, 'Content-Type': 'application/json' },
        body: JSON.stringify({ target: ['broadcast'], broadcaster_id: String(m.channelId), is_global_broadcast: false, message: JSON.stringify(message) }),
      });
    } catch (e) {
      console.log(`pubsub network error: ${e}`);
      return { retryAt: now + 2000 };
    }

    if (response.status === 429) {
      const reset = Number(response.headers.get('Ratelimit-Reset')) * 1000;
      return { retryAt: Math.max(now + PUBLISH_INTERVAL_MS, reset || 0) };
    }

    m.lastPublishAt = now;
    if (!response.ok) {
      // A bad request will not get better by retrying; drop it and let the next change or heartbeat try again.
      console.log(`pubsub ${response.status}: ${await response.text()}`);
      if (response.status >= 500) return { retryAt: now + 2000 };
    }

    m.seq = message.seq;
    m.dirty = false;
    m.fx = [];
    m.snapshotDirty = false;
    return {};
  }
}

/** Moves PogSplit's timestamp onto this server's clock using the send time PogSplit stamped on the request. */
function rebaseClock(at, sentAt, now) {
  if (typeof at !== 'number' || !sentAt) return now;
  const age = sentAt - at;
  return age >= 0 && age < 60_000 ? now - age : now;
}

function extrapolate(timer, now) {
  if (!timer) return timer;
  if (timer.phase !== 'Running') return { ...timer, at: now };
  return { ...timer, time: timer.time + (now - timer.at), at: now };
}
