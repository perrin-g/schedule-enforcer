# Jellyfin.Plugin.ScheduleEnforcer

A Jellyfin server plugin that enforces Jellyfin's existing per-user **Access Schedules** against
**already-active** playback sessions. Native Access Schedules only block *new* logins outside the
allowed window — a session that was already playing when the window closes just keeps going. This
plugin closes that gap: on a 1-minute tick it warns the user a configurable number of minutes
before their window ends, then at the cutoff sends a final message, revokes the user's access
tokens (the real enforcement — a `Stop` alone leaves the client authenticated and free to resume),
and sends a `Stop` playstate command to any session that supports media control. Administrators are
never enforced, unconditionally. It only *reads* schedules; create and edit them in Jellyfin's own
Users → Access Schedule UI as before.

Configuration (enable/disable, warning lead time, message templates) lives under Dashboard →
Schedule Enforcer Settings.

## Build and test

```bash
dotnet build Jellyfin.Plugin.ScheduleEnforcer/Jellyfin.Plugin.ScheduleEnforcer.csproj
dotnet test  Jellyfin.Plugin.ScheduleEnforcer.Tests/Jellyfin.Plugin.ScheduleEnforcer.Tests.csproj
```

There is no solution file; pass the project path explicitly.

Jellyfin API shapes are verified by reflection against the *installed* `Jellyfin.Controller` /
`Jellyfin.Model` 10.11.* packages rather than trusted from docs or memory — that convention is why
several of the source comments cite specific confirmed signatures. Keep doing it.

## Package and deploy

`<Version>` in `Jellyfin.Plugin.ScheduleEnforcer.csproj` is the single source of truth for the
plugin version. After bumping it, run `scripts/sync-version.sh` to propagate it into `meta.json`
and `manifest.json` — don't hand-edit the version into those two files. The deploy commands below
read the version back out of `meta.json` via `jq` rather than hardcoding it, so they stay correct
as long as the sync script has been run.

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

Only the plugin's own DLL is copied — everything else it references ships with Jellyfin. The
`meta.json` is required: Jellyfin's plugin discovery is metadata-driven, not "any DLL in a folder".
It is committed here precisely so deploys stop generating one ad hoc.

To cut a new GitHub release (needed for `manifest.json`'s `sourceUrl`/`checksum` to stay real):

```bash
./scripts/sync-version.sh
dotnet publish -c Release Jellyfin.Plugin.ScheduleEnforcer/Jellyfin.Plugin.ScheduleEnforcer.csproj -o dist/ScheduleEnforcer
VERSION=$(jq -r .version meta.json)
zip -j "dist/ScheduleEnforcer_${VERSION}.zip" dist/ScheduleEnforcer/Jellyfin.Plugin.ScheduleEnforcer.dll
CHECKSUM=$(md5sum "dist/ScheduleEnforcer_${VERSION}.zip" | cut -d' ' -f1)
git tag "v${VERSION%.0}"  # trailing .0 build number dropped for the tag, matching arr-delete-sync's convention
git push origin "v${VERSION%.0}"
gh release create "v${VERSION%.0}" "dist/ScheduleEnforcer_${VERSION}.zip" --title "v${VERSION%.0}" --notes "..."
jq --arg url "https://github.com/perrin-g/schedule-enforcer/releases/download/v${VERSION%.0}/ScheduleEnforcer_${VERSION}.zip" \
   --arg sum "$CHECKSUM" \
   '.[0].versions[0].sourceUrl = $url | .[0].versions[0].checksum = $sum' manifest.json > manifest.json.tmp && mv manifest.json.tmp manifest.json
```

Sanity checks after a restart:

```bash
ssh <user>@<jellyfin-host> "docker logs jellyfin --since 2m | grep -i ScheduleEnforcer"
```

Expect a plugin-loaded line and `ScheduleEnforcer: resolved container timezone is Pacific/Auckland`.
If that reads `UTC`, stop — every cutoff will fire against the wrong clock; fix the container's
timezone first.

`manifest.json` is a plugin-*repository* manifest, pointing at the actual GitHub release —
`sourceUrl`/`checksum` are real and verified (the published zip's checksum matches what's in this
file). It isn't wired up as a Jellyfin repository URL anywhere yet (deploy is still manual
scp above), but it's no longer a placeholder either — see the release-cutting commands above for
keeping it in sync with future versions.

## Operational gotcha: a disabled plugin stays disabled

**If the plugin ever seems to stop working after a Jellyfin restart, check this first.**

When this plugin crashes during Jellyfin's second (host-side) DI-activation attempt on load,
Jellyfin persists a "disabled" flag for it and then **silently skips loading the assembly on every
subsequent restart** — including after the underlying bug is fixed and a corrected DLL is deployed.
Nothing in the fixed code runs, and the logs show only:

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

## Where the depth lives

- Design rationale, scope decisions, and the failure modes this is built around:
  `docs/superpowers/specs/2026-08-29-jellyfin-schedule-enforcer-design.md`
- Implementation plan, including the confirmed-API probe findings:
  `docs/superpowers/plans/2026-08-30-jellyfin-schedule-enforcer.md`
- Live staging verification results (what was actually confirmed against real Jellyfin, and what
  wasn't): `VERIFICATION.md`
