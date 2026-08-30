# Jellyfin.Plugin.ScheduleEnforcer

Jellyfin's built-in per-user **Access Schedules** only block *new* logins outside the allowed
window — a session already playing when the window closes just keeps going. This plugin closes
that gap: on a 1-minute tick it warns a user before their window ends, then at cutoff sends a
final message, revokes their access tokens (the real enforcement — `Stop` alone leaves the
client authenticated and free to resume), and sends `Stop` to any session that supports media
control. Administrators are never enforced.

It only *reads* schedules — create and edit them in Jellyfin's own Users → Access Schedule page.

## Using it

1. Set a user's Access Schedule as normal, under Dashboard → Users → *user* → Access Schedule.
2. Configure this plugin under Dashboard → Schedule Enforcer Settings: enable/disable, how many
   minutes before the window ends to warn (`{minutes}` in the message template is replaced with
   the real countdown), and the warning/final message text.
3. What the enforced user sees: a warning message at the configured lead time, then a final
   message at cutoff, then playback stops and they're logged out. Logging back in is blocked by
   Jellyfin's own Access Schedule until their next window opens.

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
gh release create "v${VERSION%.0}" "dist/ScheduleEnforcer_${VERSION}.zip" --title "v${VERSION%.0}" --notes "..."
jq --arg url "https://github.com/perrin-g/schedule-enforcer/releases/download/v${VERSION%.0}/ScheduleEnforcer_${VERSION}.zip" \
   --arg sum "$CHECKSUM" \
   '.[0].versions[0].sourceUrl = $url | .[0].versions[0].checksum = $sum' manifest.json > manifest.json.tmp && mv manifest.json.tmp manifest.json
```

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

## More depth

`VERIFICATION.md` in this repo has the live-staging verification results — what was actually
confirmed against real Jellyfin, and what wasn't (notably: the real-client `Stop`-command path
was never exercised live, only in unit tests). The full design spec and implementation plan live
outside this repo, in the author's private notes.
