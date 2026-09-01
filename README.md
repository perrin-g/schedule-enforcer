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

## Build, test, deploy

```bash
dotnet build Jellyfin.Plugin.ScheduleEnforcer/Jellyfin.Plugin.ScheduleEnforcer.csproj
dotnet test  Jellyfin.Plugin.ScheduleEnforcer.Tests/Jellyfin.Plugin.ScheduleEnforcer.Tests.csproj
```

`<Version>` in the csproj is the single source of truth for the plugin version. After bumping
it, run `scripts/sync-version.sh` to propagate it into `meta.json`/`manifest.json` — the deploy
commands below read it back out via `jq` rather than hardcoding it.

```bash
./scripts/sync-version.sh
dotnet build -c Release Jellyfin.Plugin.ScheduleEnforcer/Jellyfin.Plugin.ScheduleEnforcer.csproj

VERSION=$(jq -r .version meta.json)
ssh <user>@<jellyfin-host> "mkdir -p <jellyfin-plugins-dir>/ScheduleEnforcer_${VERSION}"
scp Jellyfin.Plugin.ScheduleEnforcer/bin/Release/net9.0/Jellyfin.Plugin.ScheduleEnforcer.dll \
    meta.json \
    <user>@<jellyfin-host>:<jellyfin-plugins-dir>/ScheduleEnforcer_${VERSION}/
ssh <user>@<jellyfin-host> "docker restart jellyfin"
```

Only the DLL is copied — everything else it references ships with Jellyfin. `meta.json` is
required (Jellyfin's plugin discovery is metadata-driven, not "any DLL in a folder") and is
committed here so deploys stop generating one ad hoc.

Sanity check after a restart:

```bash
ssh <user>@<jellyfin-host> "docker logs jellyfin --since 2m | grep -i ScheduleEnforcer"
```

Expect a plugin-loaded line and `ScheduleEnforcer: resolved container timezone is
Pacific/Auckland`. If that reads `UTC`, stop — every cutoff will fire against the wrong clock.

### Cutting a release

`manifest.json` is a real, working plugin-repository manifest (`sourceUrl`/`checksum` point at
an actual GitHub release) and is registered in Jellyfin under Dashboard → Plugins → Repositories,
so Jellyfin's own Catalog can see version updates once they're released this way — the scp deploy
above is still the actual install mechanism for now, this just keeps the manifest truthful.

```bash
./scripts/sync-version.sh
dotnet publish -c Release Jellyfin.Plugin.ScheduleEnforcer/Jellyfin.Plugin.ScheduleEnforcer.csproj -o dist/ScheduleEnforcer
VERSION=$(jq -r .version meta.json)
zip -j "dist/ScheduleEnforcer_${VERSION}.zip" dist/ScheduleEnforcer/Jellyfin.Plugin.ScheduleEnforcer.dll
CHECKSUM=$(md5sum "dist/ScheduleEnforcer_${VERSION}.zip" | cut -d' ' -f1)
git tag "v${VERSION%.0}" && git push origin "v${VERSION%.0}"
gh release create "v${VERSION%.0}" "dist/ScheduleEnforcer_${VERSION}.zip" --title "v${VERSION%.0}" --notes "...

---
Built with Claude Code"
jq --arg url "https://github.com/perrin-g/schedule-enforcer/releases/download/v${VERSION%.0}/ScheduleEnforcer_${VERSION}.zip" \
   --arg sum "$CHECKSUM" \
   '.[0].versions[0].sourceUrl = $url | .[0].versions[0].checksum = $sum' manifest.json > manifest.json.tmp && mv manifest.json.tmp manifest.json
```

## Installing

Add this repository under Dashboard → Plugins → Repositories:

```
https://raw.githubusercontent.com/perrin-g/schedule-enforcer/master/manifest.json
```

Schedule Enforcer will then appear in the Catalog to install like any other plugin.

## If the plugin seems to stop working after a restart

Check this first. If it crashes during Jellyfin's second (host-side) DI-activation attempt on
load, Jellyfin persists a "disabled" flag and **silently skips loading the assembly on every
subsequent restart** — even after the bug is fixed and a corrected DLL is deployed. The logs show
only:

```
Skipping disabled plugin ... of Schedule Enforcer
```

Recovery is an explicit re-enable, via Dashboard → Plugins, or:

```bash
VERSION=$(jq -r .version meta.json)
ssh <user>@<jellyfin-host> "API_KEY=\$(sqlite3 -readonly <jellyfin-db-path> 'SELECT AccessToken FROM ApiKeys WHERE Name=\"<an-existing-key-name>\"') && curl -s -X POST 'http://localhost:8096/Plugins/5c90bb47-9d60-4b70-9265-3f2d025fcdd8/${VERSION}/Enable' -H \"X-Emby-Token: \$API_KEY\""
ssh <user>@<jellyfin-host> "docker restart jellyfin"
```

A deploy that looks like it did nothing is far more likely to be this than a code regression.

## Credits

An in-progress fix for the gap where an already-playing session outlives a revoked token (see
`ScheduleEnforcerTask`'s `RevokeUserTokens` call above) uses `IPluginServiceRegistrator` +
`IStartupFilter` to hook Jellyfin's ASP.NET Core request pipeline directly. That pattern was
confirmed viable by a real precedent:
[SloMR/jellyfin-plugin-dedupe-continue-watching](https://github.com/SloMR/jellyfin-plugin-dedupe-continue-watching),
which demonstrated the same DI + middleware registration approach for an unrelated feature
(deduplicating the Continue Watching row).

---

**Built with Claude Code** | [GitHub](https://github.com/perrin-g/schedule-enforcer)
