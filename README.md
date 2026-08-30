# Jellyfin.Plugin.ScheduleEnforcer

A Jellyfin plugin that enforces existing per-user **Access Schedules** against already-active
playback sessions. Jellyfin's built-in Access Schedules only block *new* logins outside the
allowed window — a session already playing when the window closes just keeps going. This plugin
closes that gap.

## Features

- **Warns before cutoff** — a configurable number of minutes before a user's Access Schedule
  window ends, sends an on-screen message (`{minutes}` in the template is replaced with the real
  countdown).
- **Actually enforces the cutoff** — at the window's end, revokes the user's access tokens (the
  real enforcement — a `Stop` command alone leaves the client authenticated and free to resume
  seconds later) and sends `Stop` to any session that supports media control.
- **Works on paused sessions too** — enforcement isn't gated on whether the client currently
  reports something playing, so a session paused right at the cutoff still gets revoked.
- **Admins are never enforced**, unconditionally.
- **Read-only against schedules** — it doesn't create or manage Access Schedules itself; set
  those up as normal under Dashboard → Users → *user* → Access Schedule, and this plugin enforces
  whatever's configured there.
- **Admin alerts** — if a session won't actually stop after repeated attempts, logs an error and
  writes an entry to Jellyfin's Activity Log so it's visible from the dashboard, not just the
  server log.

Configuration (enable/disable, warning lead time, message templates) lives under Dashboard →
Schedule Enforcer Settings.

## How it works

On a 1-minute scheduled task tick, for every non-administrator user with an Access Schedule
configured:

1. If they have an active session and are within the warning window, send the warning message
   once.
2. If they're past their window's end (or no window currently covers "now" at all), send a final
   message, revoke their access tokens, and — if the session supports it — send a `Stop` command.
   Revocation happens unconditionally on every tick past cutoff, independent of whether the
   client is controllable or the session reports paused, because it's the one action that
   doesn't depend on anything the client reports.
3. Revoking the token means the next login attempt is itself blocked by Jellyfin's own native
   Access Schedule, until the user's next allowed window opens.

## Build and test

```bash
dotnet build Jellyfin.Plugin.ScheduleEnforcer/Jellyfin.Plugin.ScheduleEnforcer.csproj
dotnet test  Jellyfin.Plugin.ScheduleEnforcer.Tests/Jellyfin.Plugin.ScheduleEnforcer.Tests.csproj
```

`<Version>` in the csproj is the single source of truth for the plugin version; run
`scripts/sync-version.sh` after bumping it to propagate the value into `meta.json` and
`manifest.json`.

## Installing

Add this repository under Dashboard → Plugins → Repositories:

```
https://raw.githubusercontent.com/perrin-g/schedule-enforcer/master/manifest.json
```

Schedule Enforcer will then appear in the Catalog to install like any other plugin.
