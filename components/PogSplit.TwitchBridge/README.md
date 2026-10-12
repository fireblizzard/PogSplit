# PogSplit Twitch Bridge

An invisible layout component that sends your run to the PogSplit Twitch extension. Viewers can then show or hide splits, stats and comparisons on the video overlay.

## Use it

1. Add **Other → PogSplit Twitch Bridge** to your layout.
2. Open its settings and click **Connect to Twitch**. A code like `9A73-5914` appears.
3. On Twitch, open the PogSplit extension's configuration (Creator Dashboard → Extensions) and enter the code.

The connection is saved for your Windows user in `%AppData%\PogSplit\twitch-bridge.dat`, encrypted with DPAPI. It is never written into the layout file, so you can share layouts safely. The bridge only runs while it is in the active layout.

### Settings

- **Keep the overlay timer running while game time is paused** (on by default). Some auto splitters, like BunnySplit for BXT, keep game time paused all the time and only set it at splits. With this on, the overlay keeps counting anyway, and the bridge sends a correction whenever game time is set to a value the overlay didn't expect. Turn it off to send phase `Paused` instead, so the overlay stops during loads.
- **Share run history with viewers** (on by default). Uploads past attempts, finished runs and golds for the overlay's history tabs. Turning it off deletes the uploaded history and hides those tabs.
- **Backend URL.** Where the bridge sends your run. Leave it empty to use the deployed backend at `https://pogsplit-ebs.pogsplit-ebs.workers.dev`.

## What it sends

Messages follow `twitch-extension/schema/run-state.schema.json` (v1):

| When | Message |
| --- | --- |
| Connect, timer start, reset, split edits, comparison switched or renamed, timing method change, another splits file opened | `snapshot` (the run definition and the current attempt) |
| Split, undo, skip, pause, resume, game time pausing for loads, timer drift over 500 ms | `update` (splits so far and timer), sent 250 ms after the change |
| Every 15 s while running or paused, every 30 s otherwise | `update` heartbeat |
| Layout closed or PogSplit exits | `offline` |

If PogSplit stops without saying goodbye (a crash, or the PC losing internet), the backend marks the channel offline after 45 s without a heartbeat.

Each message can carry effect hints (`fx`) for golds, PBs, undos and resets. The overlay doesn't use them yet.

Sending happens on a background thread, so a slow connection never freezes PogSplit. Only the newest message is kept: unsent messages are merged together, a waiting snapshot is never replaced by an update, and failed sends are retried after 1 s, then longer each time, up to 30 s.

## Backend (EBS) API

These are the requests the component makes, plus the ones the overlay uses to read what it sent. `tools/mock-ebs.js` is a local copy of the backend for testing without Cloudflare or Twitch: run `node tools/mock-ebs.js`, then set the Backend URL to `http://localhost:8787`. It approves pairing automatically, logs every message, and serves the latest state at `/v1/channels/local/state`.

| Request | Response |
| --- | --- |
| `POST /v1/device` (empty JSON body) | `200 {device_code, user_code, interval, expires_in}` |
| `POST /v1/device/token {device_code}` | `428` while waiting for the streamer, `410` or `400` when expired or refused, `200 {token, channel_id, channel_name}` once confirmed |
| `DELETE /v1/device/token` with `Authorization: Bearer <token>` | Revokes the token |
| `PUT /v1/state` with `Authorization: Bearer <token>` and a message body | `2xx` accepted. `401` or `403` means the token was revoked, and the component stops sending until you connect again. |
| `PUT /v1/history` with `Authorization: Bearer <token>` and a history body (up to 256 KB) | `204` stored. `DELETE /v1/history` removes it when the streamer stops sharing. |
| `GET /v1/channels/<id>/state` with the viewer's Twitch extension token (used by the overlay) | `200` the latest snapshot, `404` before PogSplit has sent anything |
| `GET /v1/channels/<id>/history` with the viewer's Twitch extension token (used by the overlay) | `200` the history, `404` when the streamer doesn't share it |

The `seq` PogSplit sends is its own send order. The backend ignores requests older than the last one it accepted, gives every message it publishes its own `seq`, and converts `timer.at` to its own clock so viewers' timers line up.

### Run history

With **Share run history with viewers** on, the component uploads the splits file's history when it connects, after every reset, and when you open another splits file or change timing method. It uploads at most once every 5 s and tries again 30 s after a failure. The overlay's Attempts, Finished and Golds tabs show it, and they are hidden when sharing is off.

The body is `{v: 1, timingMethod, segments: [names], attempts, finished, golds}`, newest first:

- `attempts` (last 50): `{id, started, ended, time?, reached, last?}`. `time` is the final time of a finished run, and `reached` is the number of splits completed.
- `finished` (last 10 finished runs): `{id, started, ended, time, splits: [ms or null]}`.
- `golds` (last 50): `{seg, id, time, prev?, date?}`, one for every time a segment beat its best so far. A segment time right after a skipped split is left out, because it covers two segments.

Times are in milliseconds in the current timing method. Dates are Unix time in milliseconds.
