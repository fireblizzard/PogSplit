import { DurableObject } from 'cloudflare:workers';
import { randomHex, sha256Hex } from './jwt.js';

const CODE_TTL_MS = 10 * 60 * 1000;
const POLL_INTERVAL_S = 3;
// No 0/O, 1/I/L: the code is read off a screen and typed by hand.
const ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';

function userCode() {
  const bytes = crypto.getRandomValues(new Uint8Array(8));
  const chars = [...bytes].map((b) => ALPHABET[b % ALPHABET.length]).join('');
  return `${chars.slice(0, 4)}-${chars.slice(4)}`;
}

/** Device-code pairing between PogSplit and a channel. One global instance; it only holds codes for 10 minutes. */
export class Pairing extends DurableObject {
  async start() {
    await this.purge();
    const deviceCode = randomHex(32);
    let code;
    do code = userCode();
    while (await this.ctx.storage.get(`uc:${code}`));
    const expires = Date.now() + CODE_TTL_MS;
    await this.ctx.storage.put({ [`dc:${deviceCode}`]: { code, expires, pairing: null }, [`uc:${code}`]: deviceCode });
    if (!(await this.ctx.storage.getAlarm())) await this.ctx.storage.setAlarm(expires);
    return { device_code: deviceCode, user_code: code, interval: POLL_INTERVAL_S, expires_in: CODE_TTL_MS / 1000 };
  }

  async poll(deviceCode) {
    const entry = await this.ctx.storage.get(`dc:${deviceCode}`);
    if (!entry || entry.expires < Date.now()) return { status: 'expired' };
    if (!entry.pairing) return { status: 'pending' };
    await this.ctx.storage.delete([`dc:${deviceCode}`, `uc:${entry.code}`]);
    return { status: 'ok', pairing: entry.pairing };
  }

  async confirm(code, channelId, channelName) {
    code = code.trim().toUpperCase().replace(/[^A-Z0-9]/g, '');
    code = `${code.slice(0, 4)}-${code.slice(4)}`;
    const deviceCode = await this.ctx.storage.get(`uc:${code}`);
    const entry = deviceCode && (await this.ctx.storage.get(`dc:${deviceCode}`));
    if (!entry || entry.expires < Date.now() || entry.pairing) return false;

    const token = `${channelId}.${randomHex(32)}`;
    const channel = this.env.CHANNEL.get(this.env.CHANNEL.idFromName(channelId));
    await channel.addToken(await sha256Hex(token), channelId);
    entry.pairing = { token, channel_id: channelId, channel_name: channelName };
    await this.ctx.storage.put(`dc:${deviceCode}`, entry);
    return true;
  }

  async alarm() {
    await this.purge();
    const left = await this.ctx.storage.list({ prefix: 'dc:' });
    if (left.size > 0) await this.ctx.storage.setAlarm(Date.now() + CODE_TTL_MS);
  }

  async purge() {
    const now = Date.now();
    const old = [];
    for (const [k, v] of await this.ctx.storage.list({ prefix: 'dc:' })) {
      if (v.expires < now) old.push(k, `uc:${v.code}`);
    }
    if (old.length) await this.ctx.storage.delete(old);
  }
}
