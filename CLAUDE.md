# ASF-Notifier

An [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) (ASF) plugin that sends
configurable notifications to a Matrix room. The design lives in `docs/SPEC.md`; the build
order lives in `docs/PLAN.md` and the GitHub issues.

## Layout

Mirrors JustArchiNET's `ASF-PluginTemplate`:

- `ArchiSteamFarm/`: git submodule pinned to the ASF release tag we build against (6.3.10.6).
- `ASFNotifier/`: the plugin project. Assembly and plugin name `ASFNotifier`.
- `ASFNotifier.Tests/`: MSTest unit tests (the framework ASF's own tests use).
- `ASFNotifier.slnx`: solution with ASF, the plugin and the tests.
- `Directory.Build.props` / `Directory.Packages.props`: import ASF's, so the target framework
  (`net10.0`) and package versions come from the submodule. The plugin `<Version>` lives here.

## Commands

Requires the .NET 10 SDK. Clone with submodules: `git clone --recursive` (or
`git submodule update --init` afterwards).

- Build: `dotnet build -c Release`
- Test: `dotnet test ASFNotifier.Tests -c Release`
- Package: `dotnet publish ASFNotifier -c Release -o out/plugin/ASFNotifier -p:UseAppHost=false`,
  then zip the folder's contents (not the folder) as `ASFNotifier.zip` (CI does this; see
  `.github/workflows/publish.yml`).
- Run: copy the published folder to ASF's `plugins/ASFNotifier/` and restart ASF.
- Trimmed-ASF check (CI job `trimmed-asf`): unzip an official OS-specific ASF release, then
  `dotnet run --project tools/TrimCheck -c Release -- <asf-dir> <published>/ASFNotifier.dll` and
  `tools/smoke/smoke.sh <asf-dir> <published-dir>` (bash, needs `curl` and `python3`; set `PYTHON=python` on Windows,
  where a win-x64 release works under Git Bash).

## Conventions

- Everything in the repo is English: code, comments, docs, issues, commit messages.
- ASF code style: tabs, `.editorconfig` copied from the template. ASF's analyzers apply and
  warnings are errors in Release; build with `-c Release` before pushing.
- ASF's OS-specific builds (the Docker image too) are trimmed: types and members ASF itself never uses, and
  reflection-based JSON serialization, are gone at run time. Use only what the trim check accepts; that is why the
  plugin has its own `Clock` instead of `TimeProvider` (issue #23).
- Plugin callbacks run on ASF's own flow and block it: never await network I/O inside a hook;
  enqueue and return.
- Never log, echo or commit the Matrix access token. Test fixtures use obviously fake tokens.
- Release tags are plain versions (`1.0.0.0`, no `v` prefix): ASF's plugin updater parses the
  tag with `new Version(tag)`.

## Agent skills

### Issue tracker

GitHub Issues on `easly1989/ASF-Notifier`. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context. See `docs/agents/domain.md`.

### Token tools

CBM: call `index_repository` once, then `search_graph` / `trace_path` before reading files.
