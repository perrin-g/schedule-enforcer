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
- **Kills the already-open stream, not just the token** — token revoke alone doesn't stop bytes
  already in flight: transcoded/HLS segments already buffered play through regardless, and
  DirectPlay's data channel carries no auth token at all, so revoke has *zero* effect on it. At
  the same cutoff moment the token is revoked, this plugin also aborts the live connection
  (Kestrel `HttpContext.Abort()`, a real TCP reset) and rejects any reconnect attempt using the
  same play session — for both Transcode/HLS and DirectPlay. See
  [Guaranteed stream kill](#guaranteed-stream-kill) below.
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

## Guaranteed stream kill

Token revoke and `Stop` (see [How it works](#how-it-works) above) correctly block *starting* new
playback, but neither one reliably stops a stream that's already open at cutoff:

- **Transcode/HLS** streams fetch data as a sequence of short-lived, separately-authenticated
  segment requests, so revoke *eventually* blocks the next segment — but not instantly, and a
  client with several segments already buffered plays through them regardless.
- **DirectPlay** is worse: its data requests (`?static=true&...`) carry **no auth token at all**.
  They're authorized purely by `playSessionId` matching an open playback session, entirely
  decoupled from the user's access token — so revoking the token has *zero* effect on an
  in-progress DirectPlay stream.
- `Stop` is a WebSocket message asking the client to stop. It's advisory, not enforced — nothing
  obliges a client to honor it, or to honor it promptly.

To close this gap, the plugin also registers two ASP.NET Core middleware components directly into
Jellyfin's own request pipeline (via `IStartupFilter`, the same mechanism demonstrated by
[jellyfin-plugin-dedupe-continue-watching](https://github.com/SloMR/jellyfin-plugin-dedupe-continue-watching)
— see [Credits](#credits)):

1. One intercepts `POST`/`GET /Items/{id}/PlaybackInfo` — the point in Jellyfin's request
   lifecycle where a real, authenticated `UserId` and a freshly-minted `PlaySessionId` are both
   present together — and records the `playSessionId → UserId` mapping.
2. The other sits in front of the `/videos/` and `/audio/` streaming routes. On every matching
   request it checks the `playSessionId` query parameter against that mapping and, if the owning
   user has been killed, aborts the connection outright (`HttpContext.Abort()` — a real TCP reset,
   not a request) — and keeps rejecting every subsequent request with that same `playSessionId`,
   not just whatever was in flight at the moment of the kill, so a client can't just silently
   reconnect.

At the same tick `ScheduleEnforcerTask` revokes a user's tokens, it also calls this kill switch —
so a session already playing at cutoff is aborted the same way, for both Transcode/HLS and
DirectPlay, instead of playing on regardless.

**Status:** implemented and code-reviewed (unit tests cover the pure state-tracking registry;
the middleware itself is deliberately not unit tested — real `HttpContext`/ASP.NET pipeline
behavior isn't meaningfully mockable, so it's verified against a live server instead). Not yet
deployed or live-verified as of this writing.

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

### Via the Catalog (recommended)

Add this repository under Dashboard → Plugins → Repositories:

```
https://raw.githubusercontent.com/perrin-g/schedule-enforcer/master/manifest.json
```

Schedule Enforcer will then appear in the Catalog to install like any other plugin.

### Manual install

Use this if the server has no outbound access to GitHub, or you want to pin a specific release.

1. Download `ScheduleEnforcer_<version>.zip` from [Releases](https://github.com/perrin-g/schedule-enforcer/releases)
   (or build it yourself — see [Build, test, deploy](#build-test-deploy) above).
2. Unzip it into its own versioned folder under Jellyfin's plugins directory:
   ```bash
   mkdir -p <jellyfin-plugins-dir>/ScheduleEnforcer_<version>
   unzip ScheduleEnforcer_<version>.zip -d <jellyfin-plugins-dir>/ScheduleEnforcer_<version>
   ```
   The folder must contain `Jellyfin.Plugin.ScheduleEnforcer.dll` and `meta.json` directly (not
   nested inside another subfolder) — Jellyfin's plugin discovery is metadata-driven, reading
   `meta.json` from each top-level folder under the plugins directory, not just scanning for DLLs.
3. Restart Jellyfin (`docker restart jellyfin`, or the equivalent for your install).
4. Confirm it loaded: Dashboard → Plugins should list Schedule Enforcer, or check the logs:
   ```bash
   docker logs jellyfin --since 2m | grep -i ScheduleEnforcer
   ```
   Expect a plugin-loaded line. If nothing appears, see
   [If the plugin seems to stop working after a restart](#if-the-plugin-seems-to-stop-working-after-a-restart)
   below — the same silent-disable behavior can also affect a first install if the DLL is
   incompatible with the running Jellyfin version.

Upgrading later is the same steps with the new version's zip in its own new
`ScheduleEnforcer_<version>` folder — Jellyfin does not require the old version's folder to be
removed first, but it's fine to delete it once the new version is confirmed working.

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

The [Guaranteed stream kill](#guaranteed-stream-kill) middleware's use of `IPluginServiceRegistrator`
+ `IStartupFilter` to hook Jellyfin's ASP.NET Core request pipeline directly was confirmed viable
by a real precedent:
[SloMR/jellyfin-plugin-dedupe-continue-watching](https://github.com/SloMR/jellyfin-plugin-dedupe-continue-watching),
which demonstrated the same DI + middleware registration approach for an unrelated feature
(deduplicating the Continue Watching row).

---

**Built with Claude Code** | [GitHub](https://github.com/perrin-g/schedule-enforcer)
