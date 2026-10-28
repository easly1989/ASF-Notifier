# ASF-Notifier: specification

Status: approved design (2026-10-27), revised after review; facts verified against ASF
`6.3.10.6`. Build order: `docs/PLAN.md` and the GitHub issues it links.

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
- Any channel other than Matrix. `INotificationChannel` exists only as the test seam for
  `MatrixChannel`; there is no channel registry or plugin model.
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
| Analyzers | ASF's: `AnalysisMode` `AllEnabledByDefault`, `Nullable` enabled, `TreatWarningsAsErrors` in Release | ASF `Directory.Build.props`, inherited through the import |
| Primary deployment | The ASF Docker image (`docker` variant) | |

The repository mirrors `JustArchiNET/ASF-PluginTemplate`: ASF as a git submodule pinned to the
release tag, `Directory.Build.props` and `Directory.Packages.props` importing ASF's, the plugin
project referencing `ArchiSteamFarm.csproj` with `ExcludeAssets="all" Private="false"`. The
template's submodule already points at commit `c156c7b` (= `6.3.10.6`).

## 3. Verified facts about ASF 6.3.10.6 that shape the design

1. **Hooks used.** `IPlugin` (`Name`, `Version`, `OnLoaded`), `IASF.OnASFInit(additionalConfigProperties)`,
   `IBot.OnBotInit/OnBotDestroy(bot)`, `IBotModules.OnBotInitModules(bot, additionalConfigProperties)`,
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
   with `EResult.OK`. So the hook usually sees `(bot, OK)` with `KeepRunning == false`.
   - `Stop()` call sites that log an `Error` first (ASF giving up): `HandleLoginResult`,
     `OnLoggedOff` (`LogonSessionReplaced` twice in an hour), `OnLoggedOn` (Steam parental
     code), `OnConnected` (invalid login or password), `RequestInput` (required input missing,
     which is the normal case in headless Docker).
   - `Stop()` call sites with no error: the `stop` command and IPC, a bot config edit
     (`OnConfigChanged`), a bot rename, `ShutdownOnFarmingFinished` (`OnFarmingFinished`), and
     ASF shutdown, exit or restart.
   - `Bot.RequiredInput` is public; it stays set when ASF stops a bot that needed input.
   Section 5.3 uses these facts.
4. **A stop of a bot that is not connected fires no hook at all** (`Stop()` only calls
   `Disconnect()` when the Steam client is connected).
5. **Commands reach plugins only when ASF does not know them.** `stop`, `start` and every
   built-in command are handled before `PluginsCore.OnBotCommand`; the plugin cannot observe
   them. `!notifytest` is unknown to ASF, so it reaches the plugin.
6. **`IUpdateAware` never fires in Docker.** `BuildInfo.CanUpdate` is `false` for the `docker`
   variant, and `ASF.Update` returns before downloading. The update event is therefore
   detected at startup by comparing the running ASF version with the last one the plugin saw
   (section 5.5).
7. **No plugin shutdown hook.** No interface in 6.3.10.6 announces ASF shutdown. ASF exits
   through `Environment.Exit`, which runs `AppDomain.CurrentDomain.ProcessExit` handlers. The
   plugin flushes there (section 10.1) and learns of a signal-driven shutdown earlier through
   `PosixSignalRegistration` (SIGTERM, SIGINT) and `Console.CancelKeyPress` (section 5.3).
8. **IPC exposes the plugin's `ASF.json` keys.** `GlobalConfig.AdditionalProperties` is
   `[JsonExtensionData][JsonInclude]`; `GET /Api/ASF` returns `ASFResponse.GlobalConfig`
   serialized with `Saving == false`, so every unknown `ASF.json` key, including an access
   token, is returned verbatim. ASF only hides its own secrets (`IPCPassword`, `LicenseID`,
   `WebProxyPassword`) through `ShouldSerialize*`. The same holds for bot configs through
   `GET /Api/Bot/{bots}`. See section 11.
9. **Plugin state can be persisted** in ASF's global database:
   `ASF.GlobalDatabase.LoadFromJsonStorage(key)` / `SaveToJsonStorage(key, value)` are public;
   a missing key returns a `JsonElement` whose `ValueKind` is `Undefined`. `GlobalDatabase` is
   initialised before `ASF.Init()` calls `OnASFInit`.
10. **Packaging.** ASF loads a plugin from `plugins/<Name>/`. The template's publish workflow
    runs `dotnet publish <PluginName> -c Release` and zips the output folder's contents as
    `<PluginName>.zip`. `IGitHubPluginUpdates` picks, in order,
    `{Name}-V{A}-{B}-{C}-{D}.zip` … `{Name}-V{A}.zip`, `{Name}.zip`, else the only `.zip` in the
    release. It parses the release tag with `new Version(tag)`, so tags are plain versions
    (`1.0.0.0`). On the stable update channel it reads `/releases/latest`, which skips
    pre-releases; the template publishes pre-releases, so our workflow publishes regular
    releases instead.
11. **Plugin discovery.** `PluginsCore` builds a MEF `ConventionBuilder` with
    `ForTypesDerivedFrom<IPlugin>().Export<IPlugin>()`, so every `IPlugin` type is exported
    by convention. No `[Export]` attribute is needed; the template's plugin class has none.

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
 │  signals / ProcessExit ─▶ shutdown flag      │        Formatter (plain + HTML)            │
 └──────────────────────────────────────────┘   │                 │                          │
                                                │        MatrixChannel (HttpClient)          │
                                                └────────────────────────────────────────────┘
```

### 4.1 Plugin entry

One class, `ASFNotifierPlugin`, implements `IGitHubPluginUpdates` (`RepositoryName =
"easly1989/ASF-Notifier"`), `IASF`, `IBot`, `IBotModules`, `IBotConnection`,
`IBotCardsFarmerInfo` and `IBotCommand2`. It is the only place that touches ASF types. Every
hook converts what it needs (bot name, `KeepRunning`, `RequiredInput`, `EResult` name, access
level) into plain values and hands them to the core. Every hook body is wrapped so an
exception is logged through `ASF.ArchiLogger` and never propagates into ASF.
`OnBotDestroy` clears that bot's state and cancels its timers (config reload and rename
destroy and recreate the bot).

### 4.2 Event sources (one per ASF hook)

- **ConnectionWatcher** (`IBotConnection`): owns per-bot connection state and the
  disconnect timer. Emits `BotStoppedByAsf`, `BotStopped`, `BotDisconnected`,
  `BotReconnected`. Section 5.
- **FarmingSource** (`IBotCardsFarmerInfo`): emits `FarmingFinished` (only when
  `farmedSomething`), `FarmingStarted`, `FarmingStopped`.
- **AsfVersionSource** (`IASF.OnASFInit`): emits `AsfUpdated` once per version change.
- **NotifyTestCommand** (`IBotCommand2`): builds a `Test` notification and sends it directly.

### 4.3 Event model

```csharp
enum NotificationKind { BotStoppedByAsf, BotStopped, BotDisconnected, BotReconnected,
                        FarmingFinished, FarmingStarted, FarmingStopped, AsfUpdated,
                        Suppressed, Test }

sealed record Notification(
    NotificationKind Kind,
    string? BotName,                 // null for ASF-wide events
    DateTimeOffset OccurredAt,
    string? Reason = null,           // EResult name, e.g. "TwoFactorCodeMismatch"
    string? RequiredInput = null,    // e.g. "TwoFactorAuthentication"
    TimeSpan? Duration = null,       // disconnected-for / reconnected-after
    string? FromVersion = null, string? ToVersion = null,
    int? Count = null,               // Suppressed
    bool LoginFailed = false);       // BotStoppedByAsf: the reason came from the login result (section 8)
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

The filter and the global map land first (ticket #2); per-bot settings are added later
(ticket #7). They are read in `OnBotInitModules`, which ASF calls at bot creation and again
whenever the bot config file changes, so edits apply without restarting ASF.

### 4.5 Rate limit and dedupe

Applied after the filter, before the queue (`RateLimiter`):

- **Dedupe**: the same `(Kind, BotName)` is sent at most once every 10 minutes, measured from
  the last one that was sent (9:59 later is dropped, 10:00 later is sent). Later ones in
  the window are dropped and counted as suppressed. Exception: `BotReconnected` is never
  deduped when it closes a `BotDisconnected` that was sent, so every "disconnected" message
  gets its "reconnected". The limiter tracks this itself: a passed `BotDisconnected` opens the
  bot's outage, the next passed `BotReconnected`, `BotStopped` or `BotStoppedByAsf` closes it
  (a stop ends the outage, 5.1).
- **Hourly cap**: at most 20 messages in any rolling 60 minutes; a slot frees exactly 60
  minutes after it was taken. Beyond that, messages are dropped and counted. `BotStoppedByAsf`
  and `BotReconnected` are exempt from the cap (they are the messages that must never be
  lost) but still use a slot when there is one.
- **Summary**: there is no timer. When a notification passes and the suppressed count is
  above zero, one `Suppressed` notification ("N notifications suppressed") is enqueued just
  before it, uses a slot when one is free, and resets the count. The count runs from the
  previous summary and includes dedupe drops, so the text names no period. It never stops
  the message it precedes, so an hour can carry one summary above the cap. If no further
  notification ever passes, the summary is never sent.
- `Test` does not go through the rate limiter: `!notifytest` calls the channel directly
  (section 9).
- **Stops and the stop hold (5.3).** Stop notifications are rate-checked when the hold
  releases them, not when they are held: a stop the hold drops (shutdown, mass stop) never
  uses a dedupe window or a slot, and a released stop is checked once. Dedupe and the cap use
  the release time.
- **Accepted.** For every other kind the limiter's verdict is the emit's answer
  (section 5.2): a `BotDisconnected` dropped by dedupe or the cap is not "notified", so its
  outage sends no `BotReconnected`. A notification the queue refuses (it closed at shutdown)
  takes no slot, opens or closes no outage, starts no dedupe window and is not counted; a
  refused summary leaves the count for the next one.
- One lock; the summary and its message are enqueued under it, so nothing lands between
  them. The limiter never calls back into the watcher or the stop hold.

Values are constants in v1 (10 minutes, 20 per hour); they become config keys only if needed.
The clock is injected (`TimeProvider`) so tests control time. Windows and slots use its
monotonic timestamps (`GetTimestamp`), so a wall clock stepping back mutes nothing; wall time
only stamps the summary.

### 4.6 Background send queue

- A bounded `System.Threading.Channels.Channel<Notification>` (capacity 100,
  `DropOldest`), one long-running consumer task started in `OnASFInit`.
- Hooks call `TryWrite` and return immediately; enqueue never awaits.
- The worker formats the message and calls the channel. Retries and backoff happen in the
  worker (section 10), so a slow homeserver delays notifications, never ASF.
- The only bounds are the hourly cap (4.5) and the queue capacity; there is no age limit.

### 4.7 Channel

```csharp
interface INotificationChannel {              // test seam only
    Task<SendResult> SendAsync(FormattedMessage message, string transactionId, bool bypassDegraded, CancellationToken ct);
}
sealed record FormattedMessage(string PlainText, string Html);
enum SendOutcome { Sent, RetryAfter, Transient, Dropped }  // SendResult: outcome + delay + short error
```

`MatrixChannel` is the only implementation. It takes an `HttpClient` built by the plugin
(`Timeout` 10 seconds), so tests inject a fake `HttpMessageHandler`.

**Matrix request.** `PUT {HomeserverUrl}/_matrix/client/v3/rooms/{urlencoded RoomId}/send/m.room.message/{txnId}`,
header `Authorization: Bearer <token>`, body:

```json
{ "msgtype": "m.notice", "body": "<plain>", "format": "org.matrix.custom.html", "formatted_body": "<html>" }
```

`m.notice` marks the sender as a bot, so other bots do not answer it. `txnId` is a GUID made
once per notification and reused on every retry, so the homeserver deduplicates a retried
send.

**Degraded state.** `401` and `403` put the channel in `Degraded` (section 10). While
degraded, a send attempted less than 10 minutes after the last failed one returns `Dropped`
without a request; the first send after 10 minutes is tried normally. A success returns the
channel to `Healthy`. `!notifytest` passes `bypassDegraded: true` and always makes the
request.

## 5. Event detection

### 5.1 Connection state per bot

`ConnectionWatcher` keeps, per bot name: `DisconnectedAt`, `DisconnectReason`,
`DisconnectNotified`, and a pending timer. `OnBotDestroy` removes the entry and
cancels the timer.

- `OnBotLoggedOn(bot)`: cancel the timer. If `DisconnectNotified`, emit `BotReconnected` with
  `Duration = now - DisconnectedAt`. Reset the state.
- `OnBotDisconnected(bot, reason)`: record `DisconnectedAt` (only if not already set for this
  outage), the time of this disconnect as the latest one, and the reason (only if it is not
  `OK`/`Invalid`). If `KeepRunning` is `false`, go to 5.3; otherwise 5.2.
- A classified stop ends the outage (5.3 step 2): a stopped bot only disconnects again after it
  was started again, and a bot started again can be stopped again before it ever logs on (a
  headless bot that still needs input), which is a new stop. An outage that ends with a stop or
  `OnBotDestroy` sends no `BotReconnected`, even after a `BotDisconnected`: the stop message
  (or the bot's removal) closes it.
- Known limit: a bot stopped while not connected, after its `BotDisconnected` was sent, fires no
  hook (`Stop()` disconnects only a connected client, fact 4), and the outage's one timer has
  already fired. The outage stays open; a later `start` and log-on then sends
  `BotReconnected` with a duration that includes the stopped time. See section 14.

The state is one lock per watcher; the hooks and the timer callback run on arbitrary threads.
The timer callback reads the bot (`KeepRunning`, `IsConnectedAndLoggedOn`, `RequiredInput`)
outside the lock, then acts only if the outage it was started for is still the current one, and
emits while holding the lock: a log-on racing it waits, so its `BotReconnected` is always queued
after the `BotDisconnected`. Lock order: the watcher lock may be held while taking the tap's lock
and, through the emit, the filter, the stop hold, the rate limiter and the send queue; none of them calls back
into the watcher. An exception in the callback is logged through `ASF.ArchiLogger` and never
escapes the timer thread.

### 5.2 Disconnected for N minutes, then reconnected

If `bot.KeepRunning` is still `true`, ASF will reconnect. Start a timer for
`DisconnectedNotifyAfterMinutes` (default 5), only if the outage has none yet, so the clock runs
from the first disconnect of an outage. One timer per outage: once it has fired and sent
`BotDisconnected`, later disconnects of the same outage (every failed reconnect attempt is one)
start no other, so an outage is reported once however long it lasts. When it fires:

- `bot.KeepRunning == false` → the bot was stopped meanwhile (for example Steam dropped the
  connection before ASF's `Stop()` ran, so `Stop()` fired no hook): go to 5.3 with `T` = the
  latest recorded disconnect and no hook reason (`OK`), so the reason comes from the tap.
- `bot.IsConnectedAndLoggedOn == false` → emit `BotDisconnected` (`Duration` = elapsed since
  `DisconnectedAt`, `Reason` = last recorded reason). `DisconnectNotified` is set only if the
  emit accepted it: it passed the filter (section 4.4), dedupe and the hourly
  cap (4.5) and was queued. With `BotDisconnected` off, or the message dropped, the outage sends
  no `BotReconnected` either, and is not offered again.
- ASF no longer has the bot → nothing; the outage ends.
- otherwise nothing (a log-on callback was missed; reset).

A short outage that recovers within N minutes produces no message at all. There is no other
re-check: in the race above, the stop is reported when the timer fires, up to N minutes late.

### 5.3 Stopped by ASF versus stopped

The hook alone cannot tell these apart (fact 3). The plugin adds three inputs, and holds the
result briefly before it is sent.

**Shutdown flag.** Set by `PosixSignalRegistration.Create` for SIGTERM and SIGINT (the
handler does not cancel the signal), by `Console.CancelKeyPress` (not cancelled either) and by
`ProcessExit`. While it is set, no stop event is emitted: `docker stop` and an ASF shutdown
stop every bot and must not produce a message per bot.

**Stop hold.** ASF's `exit` and `restart` commands, the IPC exit and restart endpoints and the
restart after an auto-update raise no signal. They go through `Program.InitShutdownSequence`,
which calls `bot.Stop(true)` on every bot and then exits: about 1 second later for `exit`,
about 6 seconds later for `restart`. Without more, every bot would produce `⏹️`, and a bot with
`RequiredInput` set a false 🔴. So every `BotStoppedByAsf` / `BotStopped` that passes the
filter waits 10 seconds (`StopHold`) and is then:

- dropped if the shutdown flag was set meanwhile (the process is going down);
- dropped, with everything else held, if no bot is left running
  (`Bot.BotsReadOnly`, `KeepRunning`) and the held stops span more than one bot: every bot
  stopped together, which is what a shutdown looks like. Exempt from this rule (not from the
  shutdown flag) is a `BotStoppedByAsf` backed by a give-up `Error` within 60 seconds of `T`
  (`Notification.AsfGaveUp`): a shutdown's `Stop(true)` logs none, so two bots ASF gave up on
  at once (a Steam-side change) are both reported. A `BotStoppedByAsf` from `RequiredInput`
  alone, with no such error (a bot waiting for input when ASF exits), is still dropped;
- sent otherwise. One bot stopping is never a mass stop, so the only bot of a single-bot
  instance is still reported (its shutdown is caught by the flag instead).

The hold is preferred over reading `Program.ShutdownSequenceInitialized` by reflection: that
member is internal, may change in any ASF release, and would fail silently. The cost is a 10
second delay on stop messages, and that stopping every bot within 10 seconds (for example
`stop ASF`) sends no `BotStopped`.

**NLog tap.** An in-process NLog target that records, per bot logger name, the latest
`Error`-level entry written from one of ASF's give-up sites: `HandleLoginResult()`,
`OnLoggedOff()`, `OnLoggedOn()`, `OnConnected()`, `RequestInput()`. ASF writes every entry as
`"{method}() {message}"`; the method name is not localised. It also records the latest
`HandleLoginResult()` / `OnLoggedOff()` entry at any level that names an `EResult`, for the
reason (an entry without one, such as the final "Received InvalidPassword error code … times"
in a language that translates it, must not wipe the reason logged by the attempt before). It
stores only time, method and the parsed `EResult`, never forwards or re-logs log text.

- Registration: insert a rule from `Info` to `Fatal` for all loggers at the top of the current
  `LogManager.Configuration` (so a `final` rule in a custom `NLog.config` cannot hide entries
  from it), then call `LogManager.ReconfigExistingLoggers()` so loggers that already exist pick
  the target up. Each registration uses a new target instance, because the configuration being
  replaced closes the targets it held.
- Lifecycle: subscribe to `LogManager.ConfigurationChanged` and register the target again on
  the new configuration (ASF or the user can replace the NLog config at runtime). NLog 6.2.1
  raises that event inside its configuration lock (`LogFactory.Configuration`'s setter), so
  the handler only queues the re-registration on the thread pool; the queued work skips a
  configuration that is no longer the active one. Entries written in the few milliseconds in
  between are missed.
- `EResult` parsing: split the message into tokens on non-alphanumeric characters and take
  the first token for which `Enum.TryParse<EResult>(token, ignoreCase: false, out r)`
  succeeds, the token is not numeric, `Enum.IsDefined(r)` holds, and `r` is neither `OK` nor
  `Invalid`. `EResult` values are formatted by name, so this works in every ASF language.

**Classification**, run when a stop is detected (`KeepRunning == false` in
`OnBotDisconnected`, or the 5.2 timer finding it so), with `T` = the time of the disconnect
that revealed the stop: the hook's own time, or for the timer the latest recorded disconnect.
Not the first disconnect of the outage: ASF may retry a failing login for minutes (rate-limit
cooldowns) before it gives up, and the give-up error sits next to the last disconnect. Not the
time of classifying either.

1. Shutdown flag set → nothing.
2. Each stop is reported once: `Stop()` disconnects at most once per connection, so the first
   classified stop ends the bot's outage and forgets its tap entries; a later stop disconnect
   can only follow a new start. The outage's disconnect timer (5.2) is cancelled with it.
3. `bot.RequiredInput != None` → **`BotStoppedByAsf`** with `RequiredInput` set.
4. A tap `Error` entry for that bot whose time is within 60 seconds of `T` (before or after)
   → **`BotStoppedByAsf`**. `Reason` = the hook's `EResult` if it is not `OK`/`Invalid`,
   else the `EResult` parsed from that entry, else the one from the latest
   `HandleLoginResult()` / `OnLoggedOff()` entry within 60 seconds of `T`, else none.
5. Otherwise → **`BotStopped`** ("stopped without an ASF error"): the `stop` command, a config
   edit, a rename, `ShutdownOnFarmingFinished` (ASF `exit`/`restart` are dropped by the stop
   hold).

The tap entries of a bot are also forgotten when it logs on and when it is destroyed, so an
error from before a successful log-on never explains a later stop. Ticket #3 confirms the
classification on a real instance before it is relied on.

### 5.4 Farming

`OnBotFarmingFinished(bot, farmedSomething)`: emit `FarmingFinished` only when
`farmedSomething` is `true`. `OnBotFarmingStarted` / `OnBotFarmingStopped` emit their kinds
(off by default).

### 5.5 ASF update

In `OnASFInit`, read the running ASF version (`typeof(ASF).Assembly.GetName().Version`) and
the stored one (`ASF.GlobalDatabase.LoadFromJsonStorage("ASFNotifier.LastAsfVersion")`).

- nothing stored (`ValueKind == JsonValueKind.Undefined`) or an unparsable value → store the
  current version, emit nothing.
- stored < current → emit `AsfUpdated(From, To)`, then store the current version.
- equal → nothing. Stored > current (a downgrade) → log a line, store, emit nothing.
- a stored value that is not a JSON string counts as unparsable; a missing trailing part
  (`6.3.10`) equals `.0` (`6.3.10.0`). The message has no bot name; it goes through the
  same `Emit` as every other event, so `Events.AsfUpdated: false` disables it (the version is
  still stored).

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
| `BotStopped` | Stopped without an ASF error (manual stop, config edit, rename, `ShutdownOnFarmingFinished`) | off |
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
      "RoomId": "!AbCdEfGh:example.org"
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
| `Enabled` | bool | `true` | `false` disables every notification. |
| `Matrix.HomeserverUrl` | string | none, required | Base URL; trailing slash ignored; `http://` logs a warning. |
| `Matrix.RoomId` | string | none, required | Room ID, not an alias. |
| `Matrix.AccessToken` | string | `null` | Supported, but exposed through IPC; use the env var (7.3). |
| `DisconnectedNotifyAfterMinutes` | int, 1–1440 | `5` | Out of range → warning, `5`. |
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
      "BotStopped": true
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
`Matrix.AccessToken`. In Docker it goes in the stack's `.env` file and reaches the ASF
container through the compose `environment:` list. The plugin logs which source it used
("token from environment" / "token from ASF.json"), never the value. A token from `ASF.json`
also logs the IPC exposure warning from section 11.

## 8. Message formats

All text is English. `body` is plain text; `formatted_body` is HTML with bot names, reasons
and versions HTML-escaped. Bot name = ASF bot name (the config file name).

| Kind | `body` | `formatted_body` |
|---|---|---|
| `BotStoppedByAsf` (login reason) | `🔴 bot1 — login failed (TwoFactorCodeMismatch), ASF stopped the bot` | `🔴 <b>bot1</b> — login failed (<code>TwoFactorCodeMismatch</code>), ASF stopped the bot` |
| `BotStoppedByAsf` (needs input) | `🔴 bot1 — ASF stopped the bot, it needs TwoFactorAuthentication input` | `🔴 <b>bot1</b> — ASF stopped the bot, it needs <code>TwoFactorAuthentication</code> input` |
| `BotStoppedByAsf` (other reason) | `🔴 bot1 — ASF stopped the bot (LogonSessionReplaced)` | `🔴 <b>bot1</b> — ASF stopped the bot (<code>LogonSessionReplaced</code>)` |
| `BotStoppedByAsf` (no reason) | `🔴 bot1 — ASF stopped the bot` | `🔴 <b>bot1</b> — ASF stopped the bot` |
| `BotStopped` | `⏹️ bot1 — bot stopped` | `⏹️ <b>bot1</b> — bot stopped` |
| `BotDisconnected` | `🟠 bot1 — disconnected for 5 minutes (NoConnection)` | `🟠 <b>bot1</b> — disconnected for 5 minutes (<code>NoConnection</code>)` |
| `BotReconnected` | `🟢 bot1 — reconnected after 12 minutes` | `🟢 <b>bot1</b> — reconnected after 12 minutes` |
| `FarmingFinished` | `✅ bot1 — farming finished` | `✅ <b>bot1</b> — farming finished` |
| `FarmingStarted` | `▶️ bot1 — farming started` | `▶️ <b>bot1</b> — farming started` |
| `FarmingStopped` | `⏸️ bot1 — farming stopped` | `⏸️ <b>bot1</b> — farming stopped` |
| `AsfUpdated` | `⬆️ ASF updated 6.3.10.6 → 6.3.11.7` | `⬆️ ASF updated <code>6.3.10.6</code> → <code>6.3.11.7</code>` |
| `Suppressed` | `⚠️ ASF-Notifier — 7 notifications suppressed (rate limit)` | `⚠️ <b>ASF-Notifier</b> — 7 notifications suppressed (rate limit)` |
| `Test` | `🧪 ASF-Notifier test from bot1 — delivery works` | `🧪 <b>ASF-Notifier</b> test from <b>bot1</b> — delivery works` |

"Login failed" is used when the reason came from `HandleLoginResult()` or the hook. Durations:
whole minutes below 2 hours ("1 minute", "12 minutes"), then hours and minutes ("3 h 5 min",
"2 h 0 min"), rounded to the nearest minute so a timer firing a few milliseconds early still
reads as the configured threshold.
A reason in parentheses is omitted when unknown.
`Suppressed` uses the singular for one ("1 notification suppressed").

## 9. `!notifytest` command

- Syntax: `!notifytest` (with ASF's `CommandPrefix`, `!` by default), or `notifytest` in the
  ASF console / IPC `POST /Api/Command`. `args[0]` is compared case-insensitively
  (`StringComparison.OrdinalIgnoreCase`). Further arguments are ignored: the command acts on
  the bot that received it only.
- Access: `EAccess.Master` or higher. Lower access → return `null`, so ASF answers as for any
  unknown command and the command's existence is not revealed.
- Decision as a pure function: the adapter turns `EAccess` into one `isMasterOrHigher` bool
  and calls a core function with the command name, that bool, the bot's last test time, now,
  and whether config is complete. It returns one of: not-ours (`null`), denied (`null`),
  cooldown, not configured, send. Unit-tested without ASF types.
- Cooldown: 10 seconds per bot. Within it: `<bot> Please wait <n> s before the next test.`
- Send: builds a `Test` notification for the bot and sends it directly through the channel
  (not the queue, not the filter or rate limiter, bypassing the degraded state), waits up to
  15 seconds, and replies:
  - `<bot> Test notification sent to Matrix.`
  - `<bot> Test notification failed: <short error>` (for example `401 M_UNKNOWN_TOKEN`,
    `403 M_FORBIDDEN`, `timeout`). The error never contains the token.
  - `<bot> ASF-Notifier is not configured: missing Matrix.RoomId.` when config is incomplete.

## 10. Error handling

| Situation | Behaviour |
|---|---|
| Hook throws | Caught in the adapter, logged with `ASF.ArchiLogger.LogGenericException`, never rethrown. |
| Homeserver unreachable, timeout (`HttpClient.Timeout` 10 s), 5xx | `Transient`: the worker retries the same `txnId` with exponential backoff 2 s, 4 s, … capped at 5 min, until it succeeds or the channel turns degraded. New notifications wait in the queue (capacity 100, `DropOldest`). One warning when the outage starts, one info line when it ends. |
| `429 M_LIMIT_EXCEEDED` | `RetryAfter`: wait `retry_after_ms` from the JSON body (fall back to the `Retry-After` header, then to the backoff), capped at 60 seconds, then retry the same `txnId`. |
| `401 M_UNKNOWN_TOKEN` / `M_MISSING_TOKEN`, `403 M_FORBIDDEN` | Drop that notification and enter `Degraded` (section 4.7). While degraded, notifications are dropped; the first one at least 10 minutes after the last failure is tried again. Logged only on a state change: one error on entering `Degraded` (naming the status, `errcode` and, for 403, the room ID), one info line on returning to `Healthy`. |
| Other 4xx | Drop that notification, log a warning with status and `errcode`. |
| Queue full | `DropOldest`; count drops and log at most once per minute. |
| ASF shutdown | See 10.1. |
| Config invalid | Plugin stays idle with one error line; ASF unaffected. |

### 10.1 Flushing on ASF shutdown

ASF has no plugin shutdown hook (fact 7). The plugin's `ProcessExit` handler sets the shutdown
flag, completes the queue writer, lets the worker send what is queued, and waits at most 3
seconds in total; after that it abandons the rest. No backoff during the flush; a 429 or
failure abandons that message. The handler never throws. A clean shutdown with an empty queue
returns at once.

## 11. Security

- **The token is never logged.** No log line, exception message, command reply or test
  output includes it. The `HttpClient` is not given a logging handler. A unit test asserts the
  token string never appears in any captured log or reply for every error path.
- **IPC exposure (verified, fact 8).** A token in `ASF.json` is returned by `GET /Api/ASF` and
  shown in ASF-ui's config editor to anyone who can reach IPC. A deployment without
  `IPCPassword` serves it without credentials to every address in `KnownNetworks`.
  - Decision: the environment variable is the documented way to provide the token, and it is
    available from the first end-to-end ticket (#2), so the token never has to sit in
    `ASF.json`. When the token does come from `ASF.json`, the plugin logs a startup
    **warning** that it is exposed through ASF's IPC and should move to
    `ASF_NOTIFIER_MATRIX_ACCESS_TOKEN`.
  - Redaction is rejected. The only handle is the dictionary ASF passes to `OnASFInit`, which
    is ASF's live internal `AdditionalProperties`; removing the key from it needs a downcast
    and makes the next config save through IPC (`POST /Api/ASF` merges from that dictionary)
    delete the token from `ASF.json` on disk.
- Per-bot keys hold no secrets.
- The Matrix account is a dedicated bot user with no admin rights, joined only to the alerts
  room. Rotating its token means changing the env var and restarting the container.
- HTTPS is expected for the homeserver; an `http://` URL logs a warning (allowed for a LAN
  homeserver).

## 12. Testing

- **Framework:** MSTest, as in ASF's own `ArchiSteamFarm.Tests` (the plugin template ships
  no tests). Project `ASFNotifier.Tests`. ASF's analyzers apply to it too, with no
  suppressions beyond ASF's own (`internal` test classes, `[assembly: DiscoverInternals]`).
- **What is unit-tested:** the core, which has no ASF types: settings parsing and merge
  (global, per bot, env var), filter, rate limiter and dedupe (with a fake `TimeProvider`),
  formatter (every row of section 8, HTML escaping), `ConnectionWatcher` state machine (fake
  time, fake bot state), stop classifier (synthetic tap entries, shutdown flag, required
  input), the stop hold (one bot, every bot together, shutdown meanwhile), `EResult` token
  parsing, the `!notifytest` decision function, the send queue
  (ordering, `DropOldest`, flush bound), and `MatrixChannel` against a **fake
  `HttpMessageHandler`** that records requests and replays canned responses (200, 429 with
  `retry_after_ms`, 401, 403, 500, timeout, degraded and recovery).
- **NLog tap:** tested against a real NLog `LogFactory` with an in-memory configuration,
  including a configuration reload and a `final` rule. The tap takes the `LogFactory`; the
  plugin passes `LogManager.LogFactory`.
- **What is not unit-tested:** the thin ASF adapter and the signal registration. They are
  verified by hand against a real ASF 6.3.10.6 with a test bot and a test Matrix room; each
  ticket lists the manual check.
- **Shape rule:** ASF is referenced with `ExcludeAssets="all" Private="false"`; keeping ASF
  types out of the core keeps ASF out of the test host.

## 13. CI and releases

Based on the template's workflows, trimmed:

- `ci.yml`: on push, pull request and manual run; `ubuntu-latest`, `Release` only; checkout
  with `submodules: recursive`, setup .NET 10, `dotnet build -c Release` (analyzers as errors),
  then `dotnet test ASFNotifier.Tests -c Release`.
- `publish.yml`: on `ubuntu-latest` only, `dotnet publish ASFNotifier -c Release -o
  out/plugin/ASFNotifier`, zip the folder's contents as `ASFNotifier.zip`, upload one artifact
  on every run. On a tag push, a guard step first fails the job when the tag differs from
  `<Version>` in `Directory.Build.props`; then it creates a GitHub release with
  `ASFNotifier.zip` and `SHA512SUMS`. Deviation from the template: the release is a regular
  release (`prerelease: false`, `makeLatest: true`) so ASF's stable plugin-update channel can
  see it.
- `bump-asf-reference.yml`: the template's daily job that bumps the submodule's tracked ASF
  release in `.gitmodules` (reference only; the pinned commit moves by hand after testing).
- Tags are plain four-part versions equal to `<Version>` (`1.0.0.0`).
- Optional GPG signing of `SHA512SUMS` stays as in the template (skipped without the secret).

## 14. Open points

- **Stop classification (section 5.3)** is designed from the source; ticket #3 verifies it on
  a real instance before it is relied on.
- **A stop while not connected sends nothing.** `Bot.Stop()` disconnects only when
  `SteamClient.IsConnected` (fact 4), so `stop` during a reconnect wait or a rate-limit
  cooldown fires no hook. The pending disconnect timer (5.2) finds `KeepRunning == false` and
  reports the stop up to N minutes late. If the timer already fired for that outage, the stop
  is not reported, the outage stays open, and a later `start` and log-on sends
  `BotReconnected` with a duration that counts the stopped time (5.1). No hook fires between
  that `stop` and the next `start`: `Start()` and `Stop()` call no plugin interface, and
  `OnBotLoggedOn` cannot tell a restart from a reconnect. The only trace is ASF's own Info
  line from `Stop()`, which the NLog tap could record. Using it was rejected for v1:
  - checking it at log-on would suppress the `BotReconnected` and leave the `BotDisconnected`
    unanswered;
  - acting on it when it is logged needs a deferred callback from the tap, which races the
    hook's own classification of an ordinary stop and would need its own report-once rule.
- **Stops coinciding with a shutdown are not reported** (the stop hold above), including a
  genuine 🔴 in the last 10 seconds before ASF exits. When every bot stops together without a
  shutdown, `BotStopped` and input-only 🔴 are dropped; a 🔴 backed by a give-up error is
  still sent.
- **Bot config edit.** `OnConfigChanged` calls `Stop(true)` and then `InitStart()`, which starts
  the bot again in the background. The disconnect callback may therefore reach the hook with
  `KeepRunning` already `true` again, which is a plain disconnect (5.2), not a stop: no
  `BotStopped`, and nothing at all if the bot logs on within N minutes. Ticket #3's manual
  check 4 settles which one happens.
- The original assumption that `OnBotDisconnected`'s `EResult` and `KeepRunning == false` are
  enough to detect "login failed, ASF stopped the bot" does not hold (fact 3); the NLog tap
  in section 5.3 is the replacement, and it is the only NLog use in v1.
