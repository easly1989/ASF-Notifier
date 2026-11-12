# Changelog

## 1.2.0.0

- Settings can be kept in `config/plugins/ASFNotifier.json` (ASF-PluginConfig storage standard v1, `formatVersion: 1`). A scope in the file replaces the legacy `ASFNotifier` key of `ASF.json` or of the bot config whole; errors log and fall back to the legacy keys, and a legacy key that is ignored logs a warning.
- The plugin embeds a JSON Schema (`config.schema.json`) so a settings editor can build a form.
- The token and the command users stay in the environment; the environment token wins.

Earlier versions are listed on the [releases page](https://github.com/easly1989/ASF-Notifier/releases).
