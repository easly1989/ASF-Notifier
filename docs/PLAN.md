# ASF-Notifier: build plan

Tracer-bullet tickets from `docs/SPEC.md`. Each is a GitHub issue with acceptance criteria and
its verification; this file is the map. Blocking edges are native GitHub issue dependencies
and are also written as text in each issue. A ticket can start when every blocker is closed.

| # | Ticket | Blocked by |
|---|---|---|
| [#1](https://github.com/easly1989/ASF-Notifier/issues/1) | Scaffold: plugin project, tests, CI and release zip | none |
| [#2](https://github.com/easly1989/ASF-Notifier/issues/2) | Global settings, filter, Matrix channel and `!notifytest` (first end-to-end slice) | #1 |
| [#9](https://github.com/easly1989/ASF-Notifier/issues/9) | Background send queue and shutdown flush | #2 |
| [#3](https://github.com/easly1989/ASF-Notifier/issues/3) | Event: bot stopped by ASF / bot stopped | #9 |
| [#4](https://github.com/easly1989/ASF-Notifier/issues/4) | Event: disconnected after N minutes, then reconnected | #3 |
| [#5](https://github.com/easly1989/ASF-Notifier/issues/5) | Event: farming finished (and farming started/stopped) | #9 |
| [#6](https://github.com/easly1989/ASF-Notifier/issues/6) | Event: ASF update, detected at startup | #9 |
| [#7](https://github.com/easly1989/ASF-Notifier/issues/7) | Per-bot overrides from the bot config | #3, #4, #5 |
| [#8](https://github.com/easly1989/ASF-Notifier/issues/8) | Rate limit and dedupe with suppressed summary | #9 |
| [#10](https://github.com/easly1989/ASF-Notifier/issues/10) | README: install and configuration docs | #3–#9 |
| [#11](https://github.com/easly1989/ASF-Notifier/issues/11) | **Later:** forward generic ASF errors through an NLog target | #3, #8 |

Issue numbers are not build order: #9 was repurposed during review (it was the token ticket,
now folded into #2) and runs right after #2.

## Order

```
#1 ─▶ #2 ─▶ #9 ─┬─▶ #3 ─▶ #4 ─┐
                ├─▶ #5 ───────┼─▶ #7 ─┐
                ├─▶ #6 ───────────────┼─▶ #10
                └─▶ #8 ───────────────┘
                    (#3 + #8) ─▶ #11  (later)
```

- **Frontier after #9:** #3, #5, #6 and #8 can run in parallel. #3 and #4 share the
  connection hooks, so #4 waits for #3 instead of running beside it.
- **#5** carries the first manual check through the real queue.
- **#7** waits for the three bot-event tickets so its precedence rules are verified on all of
  them.
- **#10** comes last so the README is checked against the finished settings parser.
- **#11** is outside v1.

## Ticket conventions

- One PR per ticket, test-first, with `dotnet test` green in CI.
- Each ticket's manual check runs against a real ASF 6.3.10.6 with a test bot (a throwaway
  Steam account) and a test Matrix room, and the PR records the result. No real tokens, room
  IDs, hostnames or account names in PRs.
- If a ticket finds ASF behaving differently from `docs/SPEC.md`, the PR updates the spec.

## After v1

Deployment (the ASF container, the Matrix bot user and its token) happens outside this repo.
