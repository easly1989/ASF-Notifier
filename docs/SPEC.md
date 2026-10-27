# ASF-Notifier: specification

Status: approved design (2026-10-27), facts verified against ASF `6.3.10.6`.
Build order: `docs/PLAN.md` and the GitHub issues it links.

## 1. Goals and non-goals

**Goals**

- Tell the operator, in a Matrix room, when an ArchiSteamFarm bot needs attention or did
  something worth knowing: ASF stopped a bot, a bot stayed disconnected, farming finished, ASF
  was updated.
- Configurable per event, globally and per bot, using ASF's own config files.
- Never slow ASF down: a hook only enqueues; delivery happens on a background queue.
- Never leak the Matrix access token: not in logs, not through ASF's IPC.
- Ship as a standard ASF plugin zip from GitHub Releases, compatible with ASF's plugin updater.

**Non-goals for v1**

- Generic ASF errors forwarded from the log (an NLog target): a separate ticket, marked later.
- Card drops, trades, gifts, redeems or any other event not in the table in section 6.
- Any channel other than Matrix. The channel abstraction exists so a second one can be added,
  but none is built.
- End-to-end encrypted rooms. The target room must accept unencrypted `m.room.message` events
  from the bot user.
- Room aliases. The config takes a room ID (`!id:server`).
- Commands other than `!notifytest`.

## 2. Platform and versions

| Item | Value | Source |
|---|---|---|
| ASF | `6.3.10.6` (stable, 2026-10-14) | GitHub release `JustArchiNET/ArchiSteamFarm@6.3.10.6`, tag commit `c156c7b` |
| Target framework | `net10.0` | ASF `Directory.Build.props` at the tag (`<TargetFramework>net10.0</TargetFramework>`) |
| SDK | .NET 10 | Template CI uses `DOTNET_SDK_VERSION: 10.0` |
| Test framework | MSTest (`MSTest` 4.4.1 from ASF's central packages) | `ArchiSteamFarm.Tests.csproj` at the tag; the plugin template has no tests |
| Deployment | ASF Docker image (`docker` variant) in Carlo's NAS stack `services` | |

The repository mirrors `JustArchiNET/ASF-PluginTemplate`: ASF as a git submodule pinned to the
release tag, `Directory.Build.props` and `Directory.Packages.props` importing ASF's, the plugin
project referencing `ArchiSteamFarm.csproj` with `ExcludeAssets="all" Private="false"`. The
template's submodule already points at commit `c156c7b` (= `6.3.10.6`).

## 3. Verified facts about ASF 6.3.10.6 that shape the design

1. **Hooks used.** `IPlugin` (`Name`, `Version`, `OnLoaded`), `IASF.OnASFInit(additionalConfigProperties)`,
   `IBotModules.OnBotInitModules(bot, additionalConfigProperties)`,
   `IBotConnection.OnBotDisconnected(bot, EResult)` / `OnBotLoggedOn(bot)`,
   `IBotCardsFarmerInfo.OnBotFarmingStarted/Stopped/Finished(bot, farmedSomething)`,
   `IBotCommand2.OnBotCommand(bot, access, message, args, steamID)`, `IGitHubPluginUpdates`
   (`RepositoryName`).
2. **Callbacks block ASF.** `PluginsCore` awaits every plugin in parallel
   (`Utilities.InParallel`) inside the bot's own flow; `OnBotDisconnected` runs before ASF
   decides whether to reconnect. Hooks must enqueue and return.
3. **`OnBotDisconnected` does not carry the failure reason when ASF stops a bot.** On a fatal
   login result, `HandleLoginResult` stores the `EResult` and then calls `Stop()`. `Stop()`
   sets `KeepRunning = false` and calls `Disconnect()`, which overwrites the stored result
   with `EResult.OK`. So the hook usually sees `(bot, OK)` with `KeepRunning == false`, exactly
   like a manual `stop`. The same happens for ASF stops after `LogonSessionReplaced` (twice in
   an hour) and failed Steam parental codes. The design in section 5.3 recovers both the
   "who stopped it" and the reason.
4. **A manual stop of a bot that is not connected fires no hook at all** (`Stop()` only calls
   `Disconnect()` when the Steam client is connected). No notification is possible there,
   which is fine: manual stop is off by default.
5. **Commands reach plugins only when ASF does not know them.** `stop`, `start` and every
   built-in command are handled before `PluginsCore.OnBotCommand`; the plugin cannot observe
   them. `!notifytest` is unknown to ASF, so it reaches the plugin.
6. **`IUpdateAware` never fires in Docker.** `BuildInfo.CanUpdate` is `false` for the `docker`
   variant, and `ASF.Update` returns before downloading. The update event is therefore
   detected at startup by comparing the running ASF version with the last one the plugin saw
   (section 5.5).
7. **No plugin shutdown hook.** No interface in 6.3.10.6 announces ASF shutdown. ASF exits
   through `Environment.Exit`, which runs `AppDomain.CurrentDomain.ProcessExit` handlers; the
   plugin flushes there (section 10.1).
8. **IPC exposes the plugin's `ASF.json` keys.** `GlobalConfig.AdditionalProperties` is
   `[JsonExtensionData][JsonInclude]`; `GET /Api/ASF` returns `ASFResponse.GlobalConfig`
   serialized with `Saving == false`, so every unknown `ASF.json` key, including an access
   token, is returned verbatim. ASF only hides its own secrets (`IPCPassword`, `LicenseID`,
   `WebProxyPassword`) through `ShouldSerialize*`. The same holds for bot configs through
   `GET /Api/Bot/{bots}`. See section 11.
9. **Plugin state can be persisted** in ASF's global database:
   `ASF.GlobalDatabase.LoadFromJsonStorage(key)` / `SaveToJsonStorage(key, value)` are public.
   `GlobalDatabase` is initialised before `ASF.Init()` calls `OnASFInit`.
10. **Packaging.** ASF loads a plugin from `plugins/<Name>/`. The template's publish workflow
    runs `dotnet publish <PluginName> -c Release` and zips the output as `<PluginName>.zip`.
    `IGitHubPluginUpdates` picks, in order, `{Name}-V{A}-{B}-{C}-{D}.zip` … `{Name}-V{A}.zip`,
    `{Name}.zip`, else the only `.zip` in the release. It parses the release tag with
    `new Version(tag)`, so tags are plain versions (`1.0.0.0`). On the stable update channel it
    reads `/releases/latest`, which skips pre-releases; the template publishes pre-releases, so
    our workflow publishes regular releases instead.

## 4. Architecture

```
 ASF hooks (thin adapter, ASF types end here)            core (no ASF types, unit-tested)
 ┌──────────────────────────────────────────┐   ┌────────────────────────────────────────────┐
 │ ASFNotifierPlugin                        │   │                                            │
 │  IASF.OnASFInit ── config ──────────────────▶│ NotifierSettings (global + per-bot)        │
 │  IBotModules.OnBotInitModules ── config ────▶│                                            │
 │  IBotConnection ─────▶ ConnectionWatcher ───▶│ Notification ─▶ Filter ─▶ RateLimiter ─┐   │
 │  IBotCardsFarmerInfo ─▶ FarmingSource ──────▶│                                        │   │
 │  OnASFInit ──────────▶ AsfVersionSource ────▶│                       SendQueue ◀──────┘   │
 │  IBotCommand2 (!notifytest) ────────────────▶│  (bounded channel, one background worker)  │
 │  NLog tap (bot error log) ─▶ StopClassifier  │                 │                          │
 └──────────────────────────────────────────┘   │        Formatter (plain + HTML)            │
                                                │                 │                          │
                                                │        INotificationChannel                │
                                                │          └─ MatrixChannel (HttpClient)     │
                                                └────────────────────────────────────────────┘
```

### 4.1 Plugin entry

One class, `ASFNotifierPlugin`, implements `IGitHubPluginUpdates` (`RepositoryName =
"easly1989/ASF-Notifier"`), `IASF`, `IBotModules`, `IBotConnection`, `IBotCardsFarmerInfo` and
`IBotCommand2`. It is the only place that touches ASF types. Every hook converts what it
needs (bot name, `KeepRunning`, `EResult` name) into plain values and hands them to the core.
Every hook body is wrapped so an exception is logged through `ASF.ArchiLogger` and never
propagates into ASF.

### 4.2 Event sources (one per ASF hook)

- **ConnectionWatcher** (`IBotConnection`): owns per-bot connection state and the
  disconnect timers. Emits `BotStoppedByAsf`, `BotStoppedManually`, `BotDisconnected`,
  `BotReconnected`. Section 5.
- **FarmingSource** (`IBotCardsFarmerInfo`): emits `FarmingFinished` (only when
  `farmedSomething`), `FarmingStarted`, `FarmingStopped`.
- **AsfVersionSource** (`IASF.OnASFInit`): emits `AsfUpdated` once per version change.
- **NotifyTestCommand** (`IBotCommand2`): builds a `Test` notification and bypasses filter and
  rate limit.

### 4.3 Event model

```csharp
enum NotificationKind { BotStoppedByAsf, BotStoppedManually, BotDisconnected, BotReconnected,
                        FarmingFinished, FarmingStarted, FarmingStopped, AsfUpdated,
                        Suppressed, Test }

sealed record Notification(
    NotificationKind Kind,
    string? BotName,                 // null for ASF-wide events
    DateTimeOffset OccurredAt,
    string? Reason = null,           // EResult name, e.g. "TwoFactorCodeMismatch"
    TimeSpan? Duration = null,       // disconnected-for / reconnected-after
    string? FromVersion = null, string? ToVersion = null,
    int? Count = null);              // Suppressed
```

The config key for each kind is its name (`"BotStoppedByAsf"`, …); `Suppressed` and `Test`
are not configurable.

### 4.4 Filter and per-bot overrides

`Filter.Allows(notification, settings)`:

1. Global `Enabled == false` → drop everything (the plugin still loads).
2. ASF-wide events (`AsfUpdated`) use the global `Events` map only.
3. Bot events: if the bot's `ASFNotifier.Enabled` is `false` → drop. Otherwise the bot's
   `Events` map wins key by key over the global map, which wins over the built-in defaults
   (section 6).

Per-bot settings are read in `OnBotInitModules`, which ASF calls at bot creation and again
whenever the bot config file changes, so edits apply without restarting ASF.

### 4.5 Rate limit and dedupe

Applied after the filter, before the queue:

- **Dedupe**: the same `(Kind, BotName)` is sent at most once every 10 minutes. Later ones in
  the window are dropped and counted as suppressed. Exception: `BotReconnected` is never
  deduped when it closes a `BotDisconnected` that was sent, so every "disconnected" message
  gets its "reconnected".
- **Hourly cap**: at most 20 messages in any rolling 60 minutes. Beyond that, messages are
  dropped and counted.
- **Summary**: when the cap frees a slot and the suppressed count is above zero, one
  `Suppressed` notification ("N notifications suppressed in the last hour") is sent first; it
  uses that slot and resets the count.
- `Test` bypasses both and does not consume a slot.

Values are constants in v1 (10 minutes, 20 per hour); they become config keys only if needed.
The clock is injected (`TimeProvider`) so tests control time.

### 4.6 Background send queue

- A bounded `System.Threading.Channels.Channel<Notification>` (capacity 100,
  `DropOldest`), one long-running consumer task started in `OnASFInit`.
- Hooks call `TryWrite` and return immediately; enqueue never awaits.
- The worker formats the message and calls the channel. Retries and backoff happen in the
  worker (section 10), so a slow homeserver delays notifications, never ASF.
- A notification older than 1 hour when its turn comes is dropped with a log line.

### 4.7 Channel abstraction

```csharp
interface INotificationChannel {
    string Name { get; }
    Task<SendResult> SendAsync(FormattedMessage message, CancellationToken ct);
}
sealed record FormattedMessage(string PlainText, string Html);
enum SendOutcome { Sent, RetryAfter, Fatal }   // SendResult carries outcome + delay + error text
```

`MatrixChannel` is the only implementation. It takes an `HttpClient` built by the plugin, so
tests inject a fake `HttpMessageHandler`.

**Matrix request.** `PUT {HomeserverUrl}/_matrix/client/v3/rooms/{urlencoded RoomId}/send/m.room.message/{txnId}`,
header `Authorization: Bearer <token>`, body:

```json
{ "msgtype": "m.notice", "body": "<plain>", "format": "org.matrix.custom.html", "formatted_body": "<html>" }
```

`m.notice` marks the sender as a bot, so other bots do not answer it. `txnId` is a GUID made
once per notification and reused on every retry, so the homeserver deduplicates a retried
send.

## 5. Event detection

### 5.1 Connection state per bot

`ConnectionWatcher` keeps, per bot name: `LoggedOnSinceConnect`, `DisconnectedAt`,
`DisconnectReason`, `DisconnectNotified`, `StopNotified`, and a pending timer.

- `OnBotLoggedOn(bot)`: cancel the timer. If `DisconnectNotified`, emit `BotReconnected` with
  `Duration = now - DisconnectedAt`. Reset the state.
- `OnBotDisconnected(bot, reason)`: record `DisconnectedAt` and the reason (only if it is not
  `OK`/`Invalid`); then section 5.2 or 5.3.

### 5.2 Disconnected for N minutes, then reconnected

If `bot.KeepRunning` is still `true`, ASF will reconnect. Start a timer for
`DisconnectedNotifyAfterMinutes` (default 5), only if none is pending, so the clock runs from
the first disconnect of an outage. When it fires:

- `bot.KeepRunning == false` → the bot was stopped meanwhile: go to section 5.3 (covers the
  race below).
- `bot.IsConnectedAndLoggedOn == false` → emit `BotDisconnected` (`Duration` = elapsed,
  `Reason` = last recorded reason) and set `DisconnectNotified`.
- otherwise nothing (a log-on callback was missed; reset).

A short outage that recovers within N minutes produces no message at all.

### 5.3 Stopped by ASF (login failed) versus stopped manually

The hook cannot tell the two apart (fact 3), so the plugin adds a narrow **NLog tap**: an
in-process NLog target, registered in `OnASFInit`, that keeps, per bot logger name, the time
and text of the latest `Error`-level entry and the latest entry written from
`HandleLoginResult()`. ASF prefixes every entry with the calling method name
(`"{method}() {message}"`), which is not localised, and the `EResult` appears in the message
as the enum name, which is not localised either. The tap stores only the last entry per bot
and never forwards log text anywhere.

When `OnBotDisconnected` sees `KeepRunning == false`, or the timer in 5.2 finds it so:

- an `Error` entry from that bot within the last 60 seconds → **`BotStoppedByAsf`**.
  `Reason` = the hook's `EResult` if it is not `OK`/`Invalid`, else the last `EResult` name
  found in a `HandleLoginResult()` / `OnLoggedOff()` entry, else none. "Login failed" is
  written when the reason came from `HandleLoginResult()` or is a login result.
- no such entry → **`BotStoppedManually`**.
- set `StopNotified`; a later timer or callback for the same stop emits nothing.

Race covered: if Steam drops the connection before ASF's `Stop()` runs, the hook sees
`KeepRunning == true` and a real reason; `Stop()` then finds the client disconnected and fires
nothing. A one-off re-check 15 seconds after every disconnect (in addition to the N-minute
timer) catches this and emits `BotStoppedByAsf` promptly.

Ticket 3 must confirm this classification on a real ASF instance (a bot with a wrong
password; a manual `stop`) and record the observed hook arguments in the PR.

### 5.4 Farming

`OnBotFarmingFinished(bot, farmedSomething)`: emit `FarmingFinished` only when
`farmedSomething` is `true`. `OnBotFarmingStarted` / `OnBotFarmingStopped` emit their kinds
(off by default).

### 5.5 ASF update

In `OnASFInit`, read the running ASF version (`typeof(ASF).Assembly.GetName().Version`) and
the stored one (`ASF.GlobalDatabase.LoadFromJsonStorage("ASFNotifier.LastAsfVersion")`).

- nothing stored → store the current version, emit nothing (first install).
- stored < current → emit `AsfUpdated(From, To)`, then store the current version.
- equal, or stored > current (a downgrade) → store; emit nothing for a downgrade except a log
  line.

This works for every variant, including Docker, where ASF updates by replacing the image.
`IUpdateAware` is not implemented: it never fires in Docker and would duplicate the startup
message elsewhere.

## 6. Events

| Event (config key) | Trigger | Default |
|---|---|---|
| `BotStoppedByAsf` | Login failed, or the bot was stopped by ASF | **on** |
| `BotDisconnected` | Still disconnected after 5 minutes (`DisconnectedNotifyAfterMinutes`) | **on** |
| `BotReconnected` | Logged on again after a `BotDisconnected` was sent | **on** |
| `FarmingFinished` | Farming finished with something farmed | **on** |
| `AsfUpdated` | ASF update (detected at startup) | **on** |
| `BotStoppedManually` | Manual stop | off |
| `FarmingStarted` | Farming started | off |
| `FarmingStopped` | Farming stopped | off |

Out of v1: generic errors through an NLog target (separate ticket, later), card drops.

## 7. Configuration

### 7.1 Global keys in `ASF.json`

ASF passes unknown top-level `ASF.json` keys to `OnASFInit`. The plugin reads one object key,
`ASFNotifier`:

```json
{
  "ASFNotifier": {
    "Enabled": true,
    "Matrix": {
      "HomeserverUrl": "https://matrix.example.org",
      "RoomId": "!AbCdEfGh:example.org",
      "AccessToken": null
    },
    "DisconnectedNotifyAfterMinutes": 5,
    "Events": {
      "BotStoppedManually": true,
      "FarmingFinished": false
    }
  }
}
```

| Key | Type | Default | Notes |
|---|---|---|---|
| `Enabled` | bool | `true` | `false` disables every notification. |
| `Matrix.HomeserverUrl` | string | none, required | Base URL, `https` expected; trailing slash ignored. |
| `Matrix.RoomId` | string | none, required | Room ID, not an alias. |
| `Matrix.AccessToken` | string | `null` | Prefer the env var (7.3). |
| `DisconnectedNotifyAfterMinutes` | int, 1–1440 | `5` | |
| `Events.<Kind>` | bool | table in section 6 | Unknown keys are logged as a warning and ignored. |

If the `ASFNotifier` key is absent, the plugin logs one info line ("not configured") and
stays idle. Missing `HomeserverUrl`, `RoomId` or token → one error line naming the missing
key (never its value) and the plugin stays idle; ASF keeps running.

### 7.2 Per-bot keys in `config/<bot>.json`

```json
{
  "SteamLogin": "…",
  "ASFNotifier": {
    "Enabled": true,
    "Events": {
      "FarmingFinished": false,
      "BotStoppedManually": true
    }
  }
}
```

| Key | Type | Default |
|---|---|---|
| `Enabled` | bool | `true` |
| `Events.<Kind>` | bool | inherits the global value |

Only bot events are honoured per bot; `AsfUpdated` there is ignored with a warning. The room
is global; there is no per-bot room.

### 7.3 Token from the environment

`ASF_NOTIFIER_MATRIX_ACCESS_TOKEN`, when set and non-empty, wins over
`Matrix.AccessToken`. In Carlo's deployment it goes in the `services` stack `.env` and is passed
to the ASF container through the compose `environment:` list. The plugin logs which source it
used ("token from environment" / "token from ASF.json"), never the value.

## 8. Message formats

All text is English. `body` is plain text; `formatted_body` is HTML with bot names, reasons
and versions HTML-escaped. Bot name = ASF bot name (the config file name).

| Kind | `body` | `formatted_body` |
|---|---|---|
| `BotStoppedByAsf` (login reason) | `🔴 exsly89 — login failed (TwoFactorCodeMismatch), ASF stopped the bot` | `🔴 <b>exsly89</b> — login failed (<code>TwoFactorCodeMismatch</code>), ASF stopped the bot` |
| `BotStoppedByAsf` (other reason) | `🔴 exsly89 — ASF stopped the bot (LogonSessionReplaced)` | `🔴 <b>exsly89</b> — ASF stopped the bot (<code>LogonSessionReplaced</code>)` |
| `BotStoppedByAsf` (no reason) | `🔴 exsly89 — ASF stopped the bot` | `🔴 <b>exsly89</b> — ASF stopped the bot` |
| `BotStoppedManually` | `⏹️ exsly89 — bot stopped` | `⏹️ <b>exsly89</b> — bot stopped` |
| `BotDisconnected` | `🟠 exsly89 — disconnected for 5 minutes (NoConnection)` | `🟠 <b>exsly89</b> — disconnected for 5 minutes (<code>NoConnection</code>)` |
| `BotReconnected` | `🟢 exsly89 — reconnected after 12 minutes` | `🟢 <b>exsly89</b> — reconnected after 12 minutes` |
| `FarmingFinished` | `✅ exsly89 — farming finished` | `✅ <b>exsly89</b> — farming finished` |
| `FarmingStarted` | `▶️ exsly89 — farming started` | `▶️ <b>exsly89</b> — farming started` |
| `FarmingStopped` | `⏸️ exsly89 — farming stopped` | `⏸️ <b>exsly89</b> — farming stopped` |
| `AsfUpdated` | `⬆️ ASF updated 6.3.10.6 → 6.3.11.7` | `⬆️ ASF updated <code>6.3.10.6</code> → <code>6.3.11.7</code>` |
| `Suppressed` | `⚠️ ASF-Notifier — 7 notifications suppressed in the last hour (rate limit)` | `⚠️ <b>ASF-Notifier</b> — 7 notifications suppressed in the last hour (rate limit)` |
| `Test` | `🧪 ASF-Notifier test from exsly89 — delivery works` | `🧪 <b>ASF-Notifier</b> test from <b>exsly89</b> — delivery works` |

Durations: whole minutes below 2 hours ("12 minutes", "1 minute"), then hours and minutes
("3 h 5 min"). A reason in parentheses is omitted when unknown.

## 9. `!notifytest` command

- Syntax: `!notifytest` (with ASF's `CommandPrefix`, `!` by default), or `notifytest` in the
  ASF console / IPC `POST /Api/Command`. Runs against the bot(s) ASF routes the command to.
- Access: `EAccess.Master` or higher. Lower access → return `null`, so ASF answers as for any
  unknown command and the command's existence is not revealed.
- Behaviour: builds a `Test` notification for the bot, bypasses the filter and rate limit,
  sends it directly through the channel (not the queue), waits up to 15 seconds, and replies:
  - `<bot> Test notification sent to Matrix.`
  - `<bot> Test notification failed: <short error>` (for example `401 M_UNKNOWN_TOKEN`,
    `403 M_FORBIDDEN`, `timeout`). The error never contains the token.
  - `<bot> ASF-Notifier is not configured: missing Matrix.RoomId.` when config is incomplete.

## 10. Error handling

| Situation | Behaviour |
|---|---|
| Hook throws | Caught in the adapter, logged with `ASF.ArchiLogger.LogGenericException`, never rethrown. |
| Homeserver unreachable, timeout, 5xx | Retry the same `txnId` with exponential backoff 2 s, 4 s, … capped at 5 min; give up when the notification is older than 1 hour. One warning line per outage, not per retry. |
| `429 M_LIMIT_EXCEEDED` | Wait `retry_after_ms` from the JSON body (fall back to the `Retry-After` header, then to the backoff), then retry the same `txnId`. Does not count as a failure. |
| `401 M_UNKNOWN_TOKEN` / `M_MISSING_TOKEN` | Fatal: log one error ("Matrix rejected the access token"), drop queued notifications, stop sending until the config is reloaded or ASF restarts. `!notifytest` still tries and reports the error. |
| `403 M_FORBIDDEN` (not in room / no permission) | Fatal, same as 401, message names the room ID. |
| Other 4xx | Drop that notification, log a warning with status and `errcode`. |
| Queue full | `DropOldest`; count drops and log once per minute at most. |
| ASF shutdown | See 10.1. |
| Config invalid | Plugin stays idle with one error line; ASF unaffected. |

### 10.1 Flushing on ASF shutdown

ASF has no plugin shutdown hook (fact 7). The plugin registers an
`AppDomain.CurrentDomain.ProcessExit` handler that: completes the queue writer, lets the
worker send what is queued, and waits at most 5 seconds in total; after that it abandons the
rest. No retries with backoff during the flush; a 429 or failure abandons that message. The
handler never throws. A clean shutdown with an empty queue returns at once.

## 11. Security

- **The token is never logged.** No log line, exception message, command reply or test
  output includes it. The `HttpClient` is not given a logging handler. A unit test asserts the
  token string never appears in any captured log or reply for every error path.
- **IPC exposure (verified, fact 8).** A token in `ASF.json` is returned by `GET /Api/ASF` and
  shown in ASF-ui's config editor to anyone who can reach IPC. Carlo's IPC has no
  `IPCPassword`, so it is reachable without credentials from every address in
  `KnownNetworks`.
  - Decision: the environment variable is the documented way to provide the token. When the
    token comes from `ASF.json`, the plugin logs a startup **warning** that the token is
    exposed through ASF's IPC and should move to `ASF_NOTIFIER_MATRIX_ACCESS_TOKEN`.
  - Redaction is rejected. The only handle is the dictionary ASF passes to `OnASFInit`, which
    is ASF's live internal `AdditionalProperties`; removing the key from it needs a downcast
    and makes the next config save through IPC (`POST /Api/ASF` merges from that dictionary)
    delete the token from `ASF.json` on disk.
- Per-bot keys hold no secrets.
- The Matrix user `@asf:<domain>` is a dedicated account with no admin rights, joined only to
  the alerts room. Rotating its token means changing the env var and restarting the container.
- HTTPS is expected for the homeserver; an `http://` URL logs a warning (allowed for a LAN
  homeserver).

## 12. Testing

- **Framework:** MSTest, as in ASF's own `ArchiSteamFarm.Tests` (the plugin template ships
  no tests). Project `ASFNotifier.Tests`.
- **What is unit-tested:** the core, which has no ASF types: settings parsing and merge
  (global, per bot, env var), filter, rate limiter and dedupe (with a fake `TimeProvider`),
  formatter (every row of section 8, HTML escaping), `ConnectionWatcher` state machine (fake
  time, fake bot state), stop classifier (fed synthetic log entries), the send queue
  (ordering, `DropOldest`, age limit, shutdown flush bound), and `MatrixChannel` against a
  **fake `HttpMessageHandler`** that records requests and replays canned responses (200, 429
  with `retry_after_ms`, 401, 403, 500, timeout).
- **What is not unit-tested:** the thin ASF adapter. It is verified by hand against a real ASF
  6.3.10.6 with a test bot and a test Matrix room; each ticket lists the manual check.
- **Shape rule:** ASF is referenced with `ExcludeAssets="all" Private="false"`, so ASF types
  are not loadable from the test host; keeping them out of the core is what makes the core
  testable.

## 13. CI and releases

Mirrors the template's workflows:

- `ci.yml`: on push, pull request and manual run; matrix `Debug`/`Release` ×
  `ubuntu-latest`/`windows-latest`/`macos-latest`; checkout with `submodules: recursive`,
  setup .NET 10, `dotnet build`, then `dotnet test ASFNotifier.Tests` (the one addition).
- `publish.yml`: `dotnet publish ASFNotifier -c Release -o out/plugin/ASFNotifier`, zip as
  `ASFNotifier.zip`, upload as an artifact on every run. On a tag push it creates a GitHub
  release with `ASFNotifier.zip` and `SHA512SUMS`. Deviation from the template: the release is
  a regular release (`prerelease: false`, `makeLatest: true`) so ASF's stable plugin-update
  channel can see it.
- `bump-asf-reference.yml`: the template's daily job that bumps the submodule's tracked ASF
  release in `.gitmodules` (reference only; the pinned commit moves by hand after testing).
- Tags are plain four-part versions equal to `<Version>` in `Directory.Build.props`
  (`1.0.0.0`).
- Optional GPG signing of `SHA512SUMS` stays as in the template (skipped without the secret).

## 14. Open points

- **Stop classification (section 5.3)** is designed from the source; ticket 3 verifies it on a
  real instance before it is relied on.
- The memory-file assumption that `OnBotDisconnected`'s `EResult` and `KeepRunning == false`
  are enough to detect "login failed, ASF stopped the bot" does not hold (fact 3); the NLog
  tap in section 5.3 is the replacement, and it is the only NLog use in v1.
