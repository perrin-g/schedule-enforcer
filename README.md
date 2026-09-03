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
- **Kills the already-open stream, not just the token** — a real TCP reset on the connection
  itself, not just a revoked token. See [Active stream kill](#active-stream-kill) below.
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

## Active stream kill

Token revoke and `Stop` (see [How it works](#how-it-works) above) block *starting* new playback,
but neither reliably stops a stream that's already open at cutoff — Transcode/HLS plays through
already-buffered segments, DirectPlay's data channel carries no auth token at all so revoke has
zero effect on it, and `Stop` is advisory, not enforced.

To close that gap, the plugin also hooks Jellyfin's ASP.NET Core request pipeline directly (via
`IStartupFilter`, precedent: [jellyfin-plugin-dedupe-continue-watching](https://github.com/SloMR/jellyfin-plugin-dedupe-continue-watching))
to map each play session to its user, then abort and reject any further requests for that session
the moment its user is enforced — real TCP resets (`HttpContext.Abort()`), not advisory messages,
covering both Transcode/HLS and DirectPlay. Confirmed live 2026-09-03 against a real DirectPlay
session: stream killed, an unrelated admin's Transcode session unaffected, reconnect rejected,
restart blocked by the still-closed schedule window.

## Known limitations

- **Warning/final messages don't render on every client** — rendering the `DisplayMessage`
  command is up to each client, and support varies. As of this writing, Swiftfin (iOS) doesn't
  yet display it ([tracked here](https://github.com/jellyfin/Swiftfin/blob/master/Shared/Services/UserSession/UserSessionManager%2BSocketCommands.swift)),
  though this could change in a future Swiftfin release. Enforcement itself (revoke, stream kill)
  doesn't depend on this and works the same either way — both confirmed on Swiftfin. Official
  clients (Web, Android TV) render the message fine today.
- **The 1-minute task tick can run closer to every 2 minutes in practice** — observed live
  2026-09-03, source of the delay is Jellyfin's own scheduler, not this plugin. Adds up to another
  minute of delay before a warning or cutoff fires.

## Development & Contributing

The plugin is written in C# / .NET 9 and compiled against Jellyfin 10.11.x APIs. `dotnet build`
and `dotnet test` work as expected; `scripts/sync-version.sh` keeps the csproj's `<Version>` in
sync with `meta.json`/`manifest.json` after a bump. See the repository for the full build/deploy
and release process.

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
   (or build it yourself — see [Development & Contributing](#development--contributing) above).
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

The [Active stream kill](#active-stream-kill) middleware's use of `IPluginServiceRegistrator`
+ `IStartupFilter` to hook Jellyfin's ASP.NET Core request pipeline directly was confirmed viable
by a real precedent:
[SloMR/jellyfin-plugin-dedupe-continue-watching](https://github.com/SloMR/jellyfin-plugin-dedupe-continue-watching),
which demonstrated the same DI + middleware registration approach for an unrelated feature
(deduplicating the Continue Watching row).

---

**Built with Claude Code** | [GitHub](https://github.com/perrin-g/schedule-enforcer)
