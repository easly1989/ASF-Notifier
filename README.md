# ASF-Notifier

An [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) (ASF) plugin that sends
notifications to a [Matrix](https://matrix.org) room: a bot stopped, a bot stayed disconnected,
farming finished, ASF was updated. Each event can be switched on or off globally and per bot,
using ASF's own config files.

- One plugin and one room serve **every bot** in the ASF instance.
- A hook only enqueues; delivery runs on a background queue, so a slow homeserver never slows ASF down.
- The Matrix access token is never logged.
- Matrix only, unencrypted rooms only, room ID only (no aliases). Other chat apps are reached
  through Matrix, see [Other chat apps](#other-chat-apps-telegram-discord-whatsapp-signal-slack-).

## Contents

- [Quick start](#quick-start)
- [Requirements](#requirements)
- [Install](#install)
- [Matrix setup](#matrix-setup)
- [Configuration](#configuration)
- [Check that it works](#check-that-it-works)
- [Events](#events)
- [Other ASF bots](#other-asf-bots)
- [Other chat apps (Telegram, Discord, WhatsApp, Signal, Slack, ...)](#other-chat-apps-telegram-discord-whatsapp-signal-slack-)
- [Updates](#updates)
- [Troubleshooting](#troubleshooting)
- [Building](#building)
- [Licence](#licence)

## Quick start

1. Download `ASFNotifier.zip` from [Releases](https://github.com/easly1989/ASF-Notifier/releases) and extract
   its **contents** into `plugins/ASFNotifier/` (Docker: the mounted `plugins` directory).
2. On your Matrix homeserver, create a bot user, an **unencrypted** room, invite the bot and join it.
   Note the bot's access token and the room ID (`!abc123:example.org`).
3. Add this to `ASF.json`:

   ```json
   {
     "ASFNotifier": {
       "Matrix": {
         "HomeserverUrl": "https://matrix.example.org",
         "RoomId": "!abc123:example.org"
       }
     }
   }
   ```

4. Put the token in the environment of the ASF process (not in `ASF.json`):

   ```
   ASF_NOTIFIER_MATRIX_ACCESS_TOKEN=<token>
   ```

5. Restart ASF. The log shows `ASFNotifier <version> loaded`. Send `!notifytest` to a bot as its
   Master or Owner: a test message appears in the room.

Everything below explains each step and the options.

## Requirements

- ArchiSteamFarm 6.3.10.6 or newer. The plugin is built against 6.3.10.6; the .NET 10 runtime is
  the one ASF ships with, so there is nothing else to install.
- A Matrix homeserver, a dedicated bot user and an unencrypted room (see [Matrix setup](#matrix-setup)).

## Install

The plugin is a normal ASF plugin: a folder under ASF's `plugins` directory.

### From the release zip

1. Download `ASFNotifier.zip` from the [Releases](https://github.com/easly1989/ASF-Notifier/releases) page.
2. Extract the **contents** of the zip (not a wrapping folder) into `plugins/ASFNotifier/` inside
   your ASF folder, so that `plugins/ASFNotifier/ASFNotifier.dll` exists.
3. Configure the plugin ([below](#configuration)) and restart ASF.

### With the ASF Docker image

Mount a `plugins` directory into the container and put the extracted files in
`plugins/ASFNotifier/` on the host. The [Docker Compose example](#access-token) below shows the mounts.
Keep the directory writable by the container user if you want [automatic updates](#updates).

On start, ASF logs `ASFNotifier <version> loaded`. ASF also prints its usual warning that custom
plugins are loaded; that is expected.

## Matrix setup

1. **Create a dedicated bot user** on your homeserver (for example `@asf-notifier:example.org`).
   Give it no admin rights and join it only to the alerts room.
2. **Create the room** and **invite** the bot user, then accept the invite as that user. The room
   must be **unencrypted**: the plugin sends plain `m.room.message` events and cannot encrypt.
3. **Get an access token** for the bot user. Log in as the bot user with any Matrix client or with
   the login API (`POST /_matrix/client/v3/login`) and copy the token it returns. A homeserver
   admin can also mint a token for a user (for example
   [Synapse's admin API](https://matrix-org.github.io/synapse/latest/admin_api/user_admin_api.html)).
   Treat it like a password; rotating it means changing the setting and restarting ASF.
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

Optional. A bot without this block inherits the global settings.

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
    image: justarchi/archisteamfarm:released
    restart: unless-stopped
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

## Check that it works

1. Restart ASF and look for `ASFNotifier <version> loaded` in the log. An
   `ASFNotifier stays idle: ...` line names the setting that is missing or invalid
   (see [Troubleshooting](#troubleshooting)).
2. Run the test command. It sends a test message to the room and replies with the result, so you
   see the whole path (token, room, network) work end to end.

   - Send `!notifytest` to a bot (with ASF's `CommandPrefix`, `!` by default), or `notifytest` in the
     ASF console or IPC command box.
   - Needs **Master** access or higher. Lower access gets ASF's usual unknown-command answer.
   - 10 second cooldown per bot: `<bot1> Please wait 7 s before the next test.`
   - Bypasses the event filter, rate limit and degraded state, and waits up to 15 s for the answer.

   Replies:

   - `<bot1> Test notification sent to Matrix.`
   - `<bot1> Test notification failed: 401 M_UNKNOWN_TOKEN` (also `403 M_FORBIDDEN`, `timeout`).
   - `<bot1> ASF-Notifier is not configured: missing Matrix.RoomId.`

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

## Other ASF bots

You do not install anything per bot. The plugin is loaded once by ASF and watches **every bot** the
instance runs, including bots you add later. All of them post to the **one room** set in `ASF.json`;
each message starts with the bot's name (its config file name), so you can tell them apart.

So for "all my bots", the [Quick start](#quick-start) is already the whole setup. To choose per bot,
add an `ASFNotifier` block to that bot's `config/<bot>.json` (see [Per bot](#per-bot-configbot1json)):

- Silence one bot, keep the rest:

  ```json
  { "ASFNotifier": { "Enabled": false } }
  ```

- Change which events one bot reports (here `bot2` also reports manual stops and mutes farming):

  ```json
  { "ASFNotifier": { "Events": { "BotStopped": true, "FarmingFinished": false } } }
  ```

- Notify only some bots: set the event defaults you do not want to `false` in the global `Events`
  (or `"Enabled": false` in the per-bot block of the others), then turn them on in the per-bot
  `Events` of the bots you want. A per-bot value always wins over the global one.

Limits: there is no per-bot room, and two ASF instances need one plugin install each (they can post
to the same room, but their bot names are not prefixed with the instance).

## Other chat apps (Telegram, Discord, WhatsApp, Signal, Slack, ...)

The plugin speaks **Matrix only**. It cannot post to Telegram, Discord and the like directly. There are
three ways to get the same alerts elsewhere, most practical first.

### 1. Bridge the alerts room to the other network

A Matrix bridge copies messages between a Matrix room and a chat on another network. Bridge the
alerts room, and what the bot user posts is relayed to the other side. The
[mautrix](https://docs.mau.fi/bridges/) family covers the common networks:

| Network | Bridge docs |
|---|---|
| Telegram | [mautrix-telegram](https://docs.mau.fi/bridges/go/telegram/index.html) |
| Discord | [mautrix-discord](https://docs.mau.fi/bridges/go/discord/index.html) |
| WhatsApp | [mautrix-whatsapp](https://docs.mau.fi/bridges/go/whatsapp/index.html) |
| Signal | [mautrix-signal](https://docs.mau.fi/bridges/go/signal/index.html) |
| Slack | [mautrix-slack](https://docs.mau.fi/bridges/go/slack/index.html) |

What you need:

1. **A bridge running next to your homeserver.** It is a separate service you install and register with
   the homeserver (an appservice registration). Follow the bridge's own setup guide linked above.
2. **The alerts room bridged to a chat on the other network.** Create or pick the target chat there
   (a Telegram group, a Discord channel, ...) and link it to the room with the bridge's commands.
3. **A way to relay messages from users who have no account on the other network.** The bot user is a
   plain Matrix user. Either use the bridge's *relay mode*
   ([relay-mode docs](https://docs.mau.fi/bridges/general/relay-mode.html)), where the bridge posts as
   itself with the sender's name, or log the bot user in to the other network as a puppet. Relay mode
   is the simpler fit for a notification bot.

Notes:

- This is **not tested by this project**. The plugin only ever posts to the Matrix room; what happens
  after that is up to your bridge and its version.
- The plugin sends `m.notice` messages with an HTML body. Some bridges treat notices differently from
  ordinary messages (for example, skip them or bridge them as plain text), and the HTML formatting may
  be reduced to plain text on the other side. Check your bridge's settings if nothing arrives.
- The bridge needs the room to stay **unencrypted**, as the plugin does.
- Keep the same bot user and token for the plugin; the bridge does not need the token.

### 2. Read the room in any Matrix client

No bridge needed: install a Matrix client on your phone or desktop, such as
[Element](https://element.io) or [FluffyChat](https://fluffychat.im) (more in the
[client list](https://matrix.org/ecosystem/clients/)), sign in with your own Matrix account, join the
alerts room (invite your own account from a client signed in as the bot user, or from any room
admin), and turn on push notifications for it. The bot user stays the sender.

### 3. Add a native channel (for developers)

There is **no channel registry or plugin model**. `INotificationChannel` exists only as a test seam
for `MatrixChannel`, and `docs/SPEC.md` lists "any channel other than Matrix" as a v1 non-goal. A new
channel (for example a Discord webhook, the [Telegram Bot API](https://core.telegram.org/bots/api#sendmessage)
or [ntfy](https://ntfy.sh/docs/publish/)) is a code change. This is where it would go, from the code:

| Piece | File | What it does |
|---|---|---|
| Event model | `ASFNotifier/Notification.cs` | `Notification`, `NotificationKind` and `FormattedMessage(PlainText, Html)`. |
| Formatter | `ASFNotifier/Formatter.cs` | Turns a `Notification` into the plain and HTML text. Channel independent. |
| Channel contract | `ASFNotifier/INotificationChannel.cs` | `SendAsync(message, transactionId, bypassDegraded, ct)` returns a `SendResult` with `Sent`, `RetryAfter`, `Transient` or `Dropped`. |
| Matrix channel | `ASFNotifier/MatrixChannel.cs` | The only implementation: the HTTP `PUT`, error mapping (401/403 degraded, 429 retry, 5xx transient). |
| Queue | `ASFNotifier/SendQueue.cs` | One bounded queue and one worker. Formats once, retries the same transaction ID, backs off, flushes on exit. Takes **one** `INotificationChannel`. |
| Wiring | `ASFNotifier/ASFNotifier.cs` (`OnASFInit`) | Builds `MatrixChannel` and `SendQueue` directly; `!notifytest` gets the same channel. |
| Settings | `ASFNotifier/NotifierSettings.cs` | Parses the `Matrix` section and the token from the environment. |

The smallest shape that would work:

1. **Implement `INotificationChannel`** for the new service (`SendAsync` maps the service's answers to
   `SendResult`: success is `Sent`, rate limiting is `RetryAfter`, network errors are `Transient`, a bad
   credential or a rejected message is `Dropped`).
2. **Add a settings section** (for example `Discord.WebhookUrl`) next to `Matrix` in `NotifierSettings`,
   with the secret read from an environment variable, never required in `ASF.json`.
3. **Construct it in `OnASFInit`** and give it **its own `SendQueue`**, next to the Matrix one, and
   enqueue each accepted notification to every queue. A single shared queue with a combined channel
   would let a slow or failing service hold up the others, and the retry state (transaction ID,
   degraded state) is per channel.
4. Decide whether `!notifytest` should also test the new channel.

What a new channel must respect (see `CLAUDE.md`):

- **Never block ASF.** Hooks only enqueue and return; no network I/O inside a hook. All sending
  happens on the queue's worker.
- **Never log, echo or commit a secret.** That covers the token, a webhook URL (it is a credential) and
  exception text that may contain the URL. Build the `HttpClient` without a logging handler, as
  `MatrixChannel.CreateHttpClient` does, and return short errors such as `timeout`.
- **Stay inside ASF's trimmed runtime.** ASF's OS-specific builds (the Docker image too) drop code ASF
  does not use. Do not use `JsonContent.Create` or any reflection-based JSON (write the body with
  `Utf8JsonWriter`, as `MatrixChannel` does), and prefer the plugin's own `Clock` over `TimeProvider`.
  Run the trim check (`tools/TrimCheck`, see `CLAUDE.md`) and the smoke test; issue #23 explains why.
- **Add tests.** MSTest, with a fake `HttpMessageHandler` as in `ASFNotifier.Tests/MatrixChannelTests.cs`,
  covering success, rate limit, transient error and rejection.
- Document the new settings in this README and in `docs/SPEC.md`.

If you want this, open an issue on the repository first so the design (one queue per channel, how it
shows in settings) is agreed before code is written.

## Updates

ASF can update the plugin for you, on every ASF variant, **the Docker image included**. This is opt-in:
by default no plugin is updated.

### Automatic updates

The plugin supports ASF's plugin updater. Enable it for this plugin in `ASF.json`:

```json
{
  "PluginsUpdateList": ["ASFNotifier"]
}
```

For it to run, all of these must hold:

| Setting (`ASF.json`) | Needed value | Default |
|---|---|---|
| `PluginsUpdateMode` | `Whitelist` (the list names the plugins to update) | `Whitelist` |
| `PluginsUpdateList` | contains `ASFNotifier` | empty, so nothing is updated |
| `UpdateChannel` | `Stable` (or `PreRelease`), not `None` | `Stable` |
| `UpdatePeriod` | greater than `0` (hours between checks) | `24` |

How it behaves:

- **When ASF checks.** At ASF start, then every `UpdatePeriod` hours, and when you send the
  `update` command (Owner access). The `updateplugins` command checks plugins only.
- **What it downloads.** The plugin looks at the latest GitHub release of this repository (pre-releases
  are skipped on the `Stable` channel), reads the release tag as a version (so tags are plain versions
  such as `1.0.0.0`, no `v` prefix), and downloads `ASFNotifier.zip` from it if that version is newer
  than the installed one. Nothing is downloaded when the installed version is the same or newer.
- **What happens after.** ASF replaces the plugin files in `plugins/ASFNotifier/` and then **restarts
  itself** (or exits, see below). The new version is loaded on the next start.
- **In Docker.** The Docker image starts ASF with `--no-restart`, so ASF **exits** instead of
  restarting itself. Run the container with a restart policy (`restart: unless-stopped` in the
  example above, or `--restart unless-stopped`) so Docker starts it again with the new plugin.
  Without one the container stays stopped after an update. The `plugins` mount must be writable.
  Docker only prevents ASF from updating **itself** (that is done by replacing the image); it does
  not stop plugin updates.
- **Releases are checked before they ship.** CI runs every release zip against ASF's trimmed
  OS-specific builds (issue #23) and a smoke test, and a red check blocks the release. This is no
  guarantee, but a broken build does not reach the updater.

### Turn automatic updates off

Any one of these is enough:

- Remove `ASFNotifier` from `PluginsUpdateList` (with `PluginsUpdateMode` `Whitelist`). ASF then logs
  that updates are disabled for the plugin at start.
- Set `UpdatePeriod` to `0`. ASF still checks, but only logs that a new version is available and
  downloads nothing. The `update` and `updateplugins` commands still download.
- Set `UpdateChannel` to `None`. This also stops ASF's own updates.
- In `Blacklist` mode, list `ASFNotifier` in `PluginsUpdateList`.

### Update by hand

1. Download the new `ASFNotifier.zip` from [Releases](https://github.com/easly1989/ASF-Notifier/releases).
2. Replace the contents of `plugins/ASFNotifier/` with the contents of the zip (delete the old files
   first so nothing stale remains).
3. Restart ASF (Docker: restart the container).

Or ask ASF to do it once, even with automatic updates off or `UpdatePeriod` at `0`: send the
`updateplugins Stable ASFNotifier` command (Owner access; the first argument is the channel). Writing
the channel as `Stable!` forces a re-install even when the version is not newer.

### Hold or pin a version

ASF has no setting for "stay on version X" and always updates to the latest release. To stay on a
version, keep `ASFNotifier` out of the update list (or turn updates off as above) and install the
zip of that release by hand. An update never goes backwards: the installed version is replaced only
by a newer one.

Release notes are on the [Releases](https://github.com/easly1989/ASF-Notifier/releases) page.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Log: `ASFNotifier is not configured in ASF.json, the plugin stays idle` | No `ASFNotifier` object in `ASF.json`. Add it ([above](#global-asfjson)). |
| Log: `ASFNotifier stays idle: missing ...` or `invalid ...` | The error names the key: `Matrix.HomeserverUrl`, `Matrix.RoomId`, or the token (`Matrix.AccessToken` or the environment variable). A room alias, a URL with credentials or a token with whitespace is invalid. `!notifytest` shows the same reason. |
| Log: `Matrix rejected the credentials or permissions (401 ...)` or `(403 ...)` | The plugin is **degraded**. 401: the token is wrong or revoked, so make a new one. 403: the bot user is not joined to the room, or may not post (the log names the room ID). While degraded, notifications are dropped and one send is tried every 10 minutes; a success restores delivery. `!notifytest` always tries. |
| `429 M_LIMIT_EXCEEDED` | The homeserver rate-limits the bot user. The plugin waits the time the server asks (at most 60 s) and retries the same message. If it repeats, ask the admin to relax the limit for that user. |
| `Test notification failed: timeout` or `network error` | The homeserver is unreachable from ASF. Check the URL and the container's network. Other failures retry with backoff (2 s doubling to 5 min). |
| Nothing arrives, no error | Check the event is on ([table](#events)) and not turned off for that bot, that the room is unencrypted, and that the notification was not suppressed by dedupe or the cap. |
| Nothing arrives on Telegram, Discord, ... | The plugin only posts to Matrix. Check the message is in the Matrix room first, then the bridge ([bridges](#1-bridge-the-alerts-room-to-the-other-network)). |
| The plugin did not update | Check [the four settings](#automatic-updates) and ASF's log, which names the plugin when it checks or skips it. In Docker, also check the container has a restart policy and a writable `plugins` mount. |

## Building

See `CLAUDE.md` for the build, test and packaging commands. CI checks the plugin against ASF's trimmed
OS-specific builds (issue #23). The trim check is necessary but not sufficient: it proves every referenced
type and member exists, not that the code runs. The smoke test covers only the `!notifytest` happy path on linux-x64.
The design is in [`docs/SPEC.md`](docs/SPEC.md).

## Licence

Apache-2.0. See [LICENSE](LICENSE).
