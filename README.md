# ASF-Notifier

An [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) (ASF) plugin that sends
notifications to a [Matrix](https://matrix.org) room: a bot stopped, a bot stayed disconnected,
farming finished, ASF was updated. Each event can be switched on or off globally and per bot,
using ASF's own config files.

- A hook only enqueues; delivery runs on a background queue, so a slow homeserver never slows ASF down.
- The Matrix access token is never logged.
- Matrix only, unencrypted rooms only, room ID only (no aliases).

## Requirements

- ArchiSteamFarm 6.3.10.6 or newer. The plugin is built against 6.3.10.6; the .NET 10 runtime is
  the one ASF ships with, so there is nothing else to install.
- A Matrix homeserver, a dedicated bot user and an unencrypted room (see [Matrix setup](#matrix-setup)).

## Install

1. Download `ASFNotifier.zip` from the [Releases](https://github.com/easly1989/ASF-Notifier/releases) page.
2. Extract the **contents** of the zip (not a wrapping folder) into `plugins/ASFNotifier/` inside
   your ASF folder, so that `plugins/ASFNotifier/ASFNotifier.dll` exists. With the ASF Docker
   image, put it in the mounted `plugins` directory.
3. Configure the plugin ([below](#configuration)) and restart ASF.

On start, ASF logs `ASFNotifier <version> loaded`.

### Automatic updates (optional)

The plugin supports ASF's plugin updater. Add its name to `PluginsUpdateList` in `ASF.json`
(`PluginsUpdateMode` defaults to `Whitelist`, which uses that list):

```json
{
  "PluginsUpdateList": ["ASFNotifier"]
}
```

Release tags are plain versions (`1.0.0.0`, no `v` prefix), as the updater requires. ASF's Docker
image never self-updates; there you update by replacing the plugin files or the image.

## Matrix setup

1. **Create a dedicated bot user** on your homeserver (for example `@asf-notifier:example.org`).
   Give it no admin rights and join it only to the alerts room.
2. **Create the room** and **invite** the bot user, then accept the invite as that user. The room
   must be **unencrypted**: the plugin sends plain `m.room.message` events and cannot encrypt.
3. **Get an access token** for the bot user. Log in as the bot user with any Matrix client or with
   the login API (`POST /_matrix/client/v3/login`) and copy the token it returns. A homeserver
   admin can also mint a token for a user (for example Synapse's admin API). Treat it like a
   password; rotating it means changing the setting and restarting ASF.
4. **Find the room ID**, not the alias. In most clients it is under Room settings > Advanced, and it
   looks like `!abc123:example.org`. An alias such as `#alerts:example.org` is rejected.

## Configuration

The plugin reads one object, `ASFNotifier`, from `ASF.json` (global) and, optionally, from
`config/<bot>.json` (per bot). With no `ASFNotifier` block the plugin logs one line and stays idle.

### Global: `ASF.json`

```json
{
  "ASFNotifier": {
    "Enabled": true,
    "Matrix": {
      "HomeserverUrl": "https://matrix.example.org",
      "RoomId": "!abc123:example.org"
    },
    "DisconnectedNotifyAfterMinutes": 5,
    "Events": {
      "BotStopped": true,
      "FarmingFinished": false
    }
  }
}
```

| Key | Type | Default | Notes |
|---|---|---|---|
| `Enabled` | bool | `true` | `false` turns every notification off; the plugin still loads. |
| `Matrix.HomeserverUrl` | string | none, required | `http` or `https` base URL, no credentials, query or fragment. A trailing slash is ignored. `http://` logs a warning, because the token then travels unencrypted. |
| `Matrix.RoomId` | string | none, required | A room ID starting with `!`. Aliases are rejected. |
| `Matrix.AccessToken` | string | none | Works, but is exposed through ASF's IPC. Use the environment variable instead ([below](#access-token)). |
| `DisconnectedNotifyAfterMinutes` | int, 1 to 1440 | `5` | How long a bot stays disconnected before `BotDisconnected` is sent. Out of range logs a warning and uses `5`. |
| `Events.<Event>` | bool | per the [event table](#events) | Overrides the default for one event. Unknown names and non-boolean values log a warning and are ignored. |

A wrong type for `Enabled` logs a warning and keeps `true`. If `HomeserverUrl`, `RoomId` or the
token is missing or invalid, the plugin logs one error naming the key (never its value), stays
idle, and ASF keeps running.

### Per bot: `config/bot1.json`

```json
{
  "SteamLogin": "...",
  "ASFNotifier": {
    "Enabled": true,
    "Events": {
      "FarmingFinished": false,
      "BotStopped": true
    }
  }
}
```

| Key | Type | Default | Notes |
|---|---|---|---|
| `Enabled` | bool | `true` | `false` silences this bot entirely. |
| `Events.<Event>` | bool | the global value | Wins over the global `Events` for this bot only. |

- Precedence, first match wins: global `Enabled`, bot `Enabled`, bot `Events`, global `Events`,
  built-in default.
- `AsfUpdated` is not a per-bot event: it is ignored in a bot config, with a warning. Unknown keys
  log a warning too. The room is global; there is no per-bot room.
- ASF reads the per-bot block again whenever the bot config changes, so an edit applies on bot
  config reload, with no ASF restart.
- A bot that turns `BotDisconnected` off gets no `BotReconnected` either.

### Access token

Set the token in the environment, not in `ASF.json`:

```
ASF_NOTIFIER_MATRIX_ACCESS_TOKEN=<token>
```

When set and non-empty it wins over `Matrix.AccessToken`. The plugin logs which source it used,
never the value.

Docker Compose, with the token in the stack's `.env` file (kept out of version control):

```yaml
services:
  asf:
    image: justarchinet/archisteamfarm:released
    environment:
      - ASF_NOTIFIER_MATRIX_ACCESS_TOKEN=${ASF_NOTIFIER_MATRIX_ACCESS_TOKEN}
    volumes:
      - ./config:/app/config
      - ./plugins:/app/plugins
```

> **Warning: IPC exposure.** ASF's IPC returns every `ASF.json` key, including unknown ones.
> A token placed in `ASF.json` is served verbatim by `GET /Api/ASF` and shown in ASF-ui to anyone
> who can reach IPC, and without an `IPCPassword` that is every address in `KnownNetworks`. If the
> token comes from `ASF.json` the plugin logs a warning at start. If you move it to the environment,
> delete it from `ASF.json`: it is then ignored but still served.

## Events

| Event (config key) | Sent when | Default |
|---|---|---|
| `BotStoppedByAsf` | ASF stopped the bot: login failed, or it needs input (2FA code, password) | on |
| `BotDisconnected` | A bot is still disconnected after `DisconnectedNotifyAfterMinutes` | on |
| `BotReconnected` | A bot logs on again after a `BotDisconnected` was sent | on |
| `FarmingFinished` | Farming finished and something was farmed | on |
| `AsfUpdated` | ASF is a newer version than at the last start | on |
| `BotStopped` | A bot stopped without an ASF error: `stop` command, config edit, rename, `ShutdownOnFarmingFinished` | off |
| `FarmingStarted` | Farming started | off |
| `FarmingStopped` | Farming stopped | off |

### Message examples

Each message is a Matrix `m.notice` with a plain and an HTML body.

| Event | Message |
|---|---|
| `BotStoppedByAsf`, login failed | `🔴 bot1 — login failed (TwoFactorCodeMismatch), ASF stopped the bot` |
| `BotStoppedByAsf`, needs input | `🔴 bot1 — ASF stopped the bot, it needs TwoFactorAuthentication input` |
| `BotStoppedByAsf`, other reason | `🔴 bot1 — ASF stopped the bot (LogonSessionReplaced)` |
| `BotStoppedByAsf`, no reason | `🔴 bot1 — ASF stopped the bot` |
| `BotStopped` | `⏹️ bot1 — bot stopped` |
| `BotDisconnected` | `🟠 bot1 — disconnected for 5 minutes (NoConnection)` |
| `BotReconnected` | `🟢 bot1 — reconnected after 12 minutes` |
| `FarmingFinished` | `✅ bot1 — farming finished` |
| `FarmingStarted` | `▶️ bot1 — farming started` |
| `FarmingStopped` | `⏸️ bot1 — farming stopped` |
| `AsfUpdated` | `⬆️ ASF updated 6.3.10.6 → 6.3.11.7` |
| rate-limit summary | `⚠️ ASF-Notifier — 7 notifications suppressed (rate limit)` (`1 notification suppressed` for one) |
| `!notifytest` | `🧪 ASF-Notifier test from bot1 — delivery works` |

Durations read `1 minute`, `12 minutes`, then `3 h 5 min` from two hours. A reason in
parentheses is left out when it is unknown.

### Behaviour worth knowing

- **Stop messages wait about 10 seconds.** `BotStoppedByAsf` and `BotStopped` are held for 10 s
  and dropped if ASF exits or restarts meanwhile, or if every bot stopped together (what `exit`,
  `restart` and `docker stop` look like). A stop backed by an ASF give-up error is still sent.
  The only bot of a single-bot instance is still reported.
- **Dedupe.** The same event for the same bot is sent at most once every 10 minutes; repeats are
  counted as suppressed. A `BotReconnected` that closes a sent `BotDisconnected` is never deduped.
- **Hourly cap.** At most 20 messages in any rolling 60 minutes. `BotStoppedByAsf` and
  `BotReconnected` are exempt from the cap. When something passes after drops, one
  `N notifications suppressed (rate limit)` message is sent just before it; if nothing else ever
  passes, the summary is never sent.
- **A short outage is silent.** A bot that reconnects within `DisconnectedNotifyAfterMinutes`
  produces no message.
- **`AsfUpdated` is detected at startup**, by comparing the running ASF version with the one the
  plugin saw last (stored in ASF's global database). It works in Docker, where ASF updates by
  replacing the image. The first start only stores the version and sends nothing.
- **The queue holds 100 messages** and drops the oldest when full. On a clean ASF exit the plugin
  tries to flush the queue for up to 3 seconds.

### Known limits

- A bot stopped while it is not connected (during a reconnect wait) fires no ASF hook, so the stop
  is reported up to `DisconnectedNotifyAfterMinutes` late, or not at all if `BotDisconnected` was
  already sent for that outage. A later start and log-on then sends `BotReconnected` with a duration
  that includes the stopped time.
- Stops in the last 10 seconds before ASF exits are not reported.
- After a bot config edit ASF restarts the bot; if it logs on within the threshold, no
  `BotStopped` and no disconnect message is sent.
- Only the events above. No card drops, trades or generic ASF errors, and no channel other than
  Matrix.

## `!notifytest`

Sends a test message to the room and replies with the result, to check the setup end to end.

- Send `!notifytest` to a bot (with ASF's `CommandPrefix`, `!` by default), or `notifytest` in the
  ASF console or IPC command box.
- Needs **Master** access or higher. Lower access gets ASF's usual unknown-command answer.
- 10 second cooldown per bot: `<bot1> Please wait 7 s before the next test.`
- Bypasses the event filter, rate limit and degraded state, and waits up to 15 s for the answer.

Replies:

- `<bot1> Test notification sent to Matrix.`
- `<bot1> Test notification failed: 401 M_UNKNOWN_TOKEN` (also `403 M_FORBIDDEN`, `timeout`).
- `<bot1> ASF-Notifier is not configured: missing Matrix.RoomId.`

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Log: `ASFNotifier is not configured in ASF.json, the plugin stays idle` | No `ASFNotifier` object in `ASF.json`. Add it ([above](#global-asfjson)). |
| Log: `ASFNotifier stays idle: missing ...` or `invalid ...` | The error names the key: `Matrix.HomeserverUrl`, `Matrix.RoomId`, or the token (`Matrix.AccessToken` or the environment variable). A room alias, a URL with credentials or a token with whitespace is invalid. `!notifytest` shows the same reason. |
| Log: `Matrix rejected the credentials or permissions (401 ...)` or `(403 ...)` | The plugin is **degraded**. 401: the token is wrong or revoked, so make a new one. 403: the bot user is not joined to the room, or may not post (the log names the room ID). While degraded, notifications are dropped and one send is tried every 10 minutes; a success restores delivery. `!notifytest` always tries. |
| `429 M_LIMIT_EXCEEDED` | The homeserver rate-limits the bot user. The plugin waits the time the server asks (at most 60 s) and retries the same message. If it repeats, ask the admin to relax the limit for that user. |
| `Test notification failed: timeout` or `network error` | The homeserver is unreachable from ASF. Check the URL and the container's network. Other failures retry with backoff (2 s doubling to 5 min). |
| Nothing arrives, no error | Check the event is on ([table](#events)) and not turned off for that bot, that the room is unencrypted, and that the notification was not suppressed by dedupe or the cap. |

## Building

See `CLAUDE.md` for the build, test and packaging commands.

## Licence

Apache-2.0. See [LICENSE](LICENSE).
