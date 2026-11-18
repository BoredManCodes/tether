# Tether for Windows: design

Status: draft for approval, 2026-11-18

## Intent

The child now has a Windows laptop. Tether covers the phone and a TV. The laptop needs the same parent view: what the child searches and types, what is on the screen, which sites and apps they use, and a way to block them. Everything shows in the existing dashboard under one login.

The child uses a standard (non-admin) Windows account. The parent holds the only admin account. That is the whole protection model; see Constraints.

## Goals

1. Watch on-screen and typed text for the parent's keywords. On a hit, take a screenshot and raise the same email alert the phone raises.
2. Live screen view from the dashboard, about one frame a second, started and stopped by the parent.
3. Domain blocking using the existing category lists, custom blocks and per-device ad block switch. Log the domains the laptop looks up.
4. App list: installed programs, daily foreground time per program, and parent rules (block, daily limit).
5. Bedtime schedule and remote lock, using the existing schedule and `LOCK` / `UNLOCK` commands.
6. Tamper and offline alerts, so the parent hears when the client is stopped.

## Non-goals

- No image or video classification. "Adult content" is caught by keywords and the domain list only.
- No uninstall rule, no AppLocker, no Safe Mode defence, no firmware or BIOS lock. Standard account plus admin password is the barrier.
- No macOS or Linux client. No multi-user laptops; one child account.
- No page content capture beyond the keyword snippet already sent by the phone (about 400 characters in the alert).
- No keystroke upload. Typed text is matched on the laptop and discarded.

## Anti-goals

- A detection storm. If Defender quarantines the client on first run, the project failed. Signing and an install script that adds a Defender exclusion are part of done.
- Silent expansion of what is collected. The README already tells parents to tell the child the device is monitored; the Windows client shows a tray icon for the same reason and the install doc repeats the notice.

## Constraints

- Server stays as it is: Node 20, Fastify, SQLite, static dashboard. Only small additive changes.
- Windows 10 21H2 or later and Windows 11. Edition unknown, so nothing may need Enterprise features (AppLocker rules, Group Policy only features beyond registry keys).
- Services run in session 0 and cannot see the child's desktop. Screen, UI Automation and keyboard access must run in the child's own session.
- Windows cannot capture the lock screen or UAC prompts from a user session (secure desktop), and DRM video returns black. Accepted.
- Without a standard account the client is trivially removable. The README must say so, as it does for Android without Device Owner.

## Approaches considered

1. **.NET 8 Windows service plus tray helper (chosen).** First-class access to UI Automation, keyboard hooks, Windows.Graphics.Capture, SCM and registry. One signable installer.
2. Node/Electron. Reuses the server stack, but native hooks need addons, idle memory is high and UI Automation access is weak. Rejected.
3. Rust. Smallest binary and hardest to kill, but UI Automation bindings are rough and the toolchain is new to the project. Rejected.

## Design

### Server changes (additive)

- `POST /api/device/enroll`: accept `kind: 'pc'` (today anything other than `tv` is stored as `phone`). `devices.kind` already exists.
- `public/app.js`: add a PC tab set next to `TV_TABS`. Show Overview, Alerts, Search, Screen time, Schedule, Apps, Sites, Settings. Hide Messages, Calls, Notifications, Photos, Places, TV. Show the live screen card for `pc` (the card is currently built by `liveScreenCard()`; the builder confirms where it mounts).
- `COMMAND_TYPES`: no new types. `LOCK`, `UNLOCK`, `SCREEN_START`, `SCREEN_STOP`, `SCREENSHOT` are reused. Other types sent to a PC are marked failed.
- `devices.accessibility_active` doubles as "helper running". The tamper alert text is phone-worded ("Accessibility service was turned off"); make the message depend on `kind`. The builder decides whether that is a new column or a message lookup.
- Package field: for a PC the `package` columns (`installed_apps`, `usage`, `app_rules`, `dns_log`) hold the lowercase executable file name, for example `chrome.exe`. `app_name` holds the file description. No schema change.

### Client layout

One solution, `windows/`:

- `Tether.Service`: Windows service, runs as LocalSystem, auto-start, recovery set to restart. Owns the server connection, token, sync loop, command long-poll, DNS proxy, adapter DNS settings, browser DoH policy, tamper watching.
- `Tether.Helper`: runs in the child's session, started at logon by a scheduled task whose trigger and principal are the configured child account only (standard rights, interactive token, no stored password; the helper also exits if the signed-in user is not the child). Tray icon with a "monitored by parent" tooltip. Owns screen capture, UI Automation text, keyboard buffer, foreground tracking, blocked-app overlay, lock. Talks to the service over a named pipe restricted to SYSTEM and the logged-on user.
- `Tether.Shared`: models, API client, keyword matcher.
- `Tether.Setup`: MSI or signed installer run by the parent from the admin account. Prompts for server URL and parent login, enrols with `kind: 'pc'`, stores the token in a file ACL'd to SYSTEM and Administrators.

### Keyword watch (goal 1)

Port the rules from `KeywordWatch.kt` unchanged:

- Keywords pulled from `GET /api/device/keywords`, refreshed on the sync tick. Plain words match at a word start only ("sex" does not fire on "Essex"); regex entries run as given.
- Source one: UI Automation. The helper subscribes to focus-change and text-changed events and reads the Text and Name of the foreground window's elements (bounded tree walk, rate limited), all browsers included. Chrome and Edge turn on their accessibility tree once a UIA client attaches; Firefox exposes it by default.
- Source two: a low-level keyboard hook feeds a short rolling word buffer. The buffer is matched and cleared on pause; nothing is stored. Password fields found via UIA `IsPassword` are skipped, and the buffer is cleared on window change.
- On a hit: screenshot at once, upload as `kind=screenshot` stamped with the hit time via `POST /api/device/media`, then `POST /api/device/screen-hit` with `words`, `package` (exe), `app_name`, `snippet`, `ts`. Cooldown 20 seconds between shots; the same word in the same app repeats only after 10 minutes. The server attaches the nearest screenshot to the email as it does today.

### Live screen (goal 2)

On `SCREEN_START` the helper captures the primary monitor with GDI (`Graphics.CopyFromScreen`), chosen over Windows.Graphics.Capture for no consent border and no WinRT dependency, scales to 1280 pixels wide, encodes JPEG at quality 60 and posts it to `POST /api/device/screen` every second until `SCREEN_STOP`. `SCREENSHOT` posts one frame. The dashboard card is unchanged. A hard stop after 30 minutes with no stop command avoids a forgotten stream.

### Domain blocking and DNS log (goal 3)

- The service runs a local DNS stub on 127.0.0.1 and ::1, port 53, and sets every active adapter's IPv4 and IPv6 DNS to it (re-applied every minute and on network change).
- Blocklist: `GET /api/device/blocklist` (hosts text, versioned by `X-Blocklist-Version`, checked via `/api/device/adblock`). Blocked names answer NXDOMAIN; the subdomain match rule is the same as `AdBlock.kt`.
- Everything else forwards to the adapter's original DNS servers, captured at install, or 1.1.1.1 if none.
- Every lookup is logged and uploaded in the `dns` array of `POST /api/device/sync`. `package` is null in v1 (Windows does not attribute DNS to a process without ETW tracing); the dashboard already tolerates a missing app.
- Browser bypass: the installer writes policy registry keys that disable DNS over HTTPS in Chrome and Edge (`DnsOverHttpsMode = off`) and Firefox (`DNSOverHTTPS` policy, `Enabled: false, Locked: true`). A Windows Firewall rule blocks outbound TCP 853 (DNS over TLS). Apps with hard-coded DoH are not covered; the README states this next to the existing Android limit.

### App list, usage and rules (goal 4)

- Installed list: HKLM and HKCU `Uninstall` keys plus Start Menu shortcuts, resolved to executables. Sent as `apps` in the sync body.
- Usage: the helper samples the foreground window's executable every 5 seconds and adds to a per-day total, sent as `usage`.
- Rules: `GET /api/device/apprules`. For `blocked`, the helper watches process starts (WMI event, 2 second poll fallback), ends the process and shows a full-screen "blocked by parent" message for 5 seconds. For `daily_limit_min`, the same path fires once today's foreground total passes the limit. The `uninstall` flag is ignored.
- Parent bypass codes (`/api/device/bypass`) are out of scope for v1.

### Schedule and lock (goal 5)

`GET /api/device/schedule` gives days and minute windows. Inside a window, or after `LOCK`, the helper calls `LockWorkStation` and re-locks within 10 seconds of any sign-in until the window ends or `UNLOCK` arrives. A locked laptop still syncs.

### Tamper and offline (goal 6)

The server's offline watchdog already covers a dead service or no network. The service additionally reports `helper_running`, DNS settings intact and policy keys intact in `status`; a true to false change raises a `tamper` alert. The helper restarts on exit through the scheduled task and the service starts it on demand.

### Distribution

`windows/` builds a self-contained single-file signed binary and an installer. The server serves it at `/download/tether-setup.exe`, as it does the APK at `/download/tether.apk`. The README gets a Windows section.

## Left to the builder

Named pipe wire format, the exact UI Automation walk and rate limits, installer technology (MSI via WiX or a signed `.exe` bootstrap), how the helper is relaunched after fast user switching, and the test approach. Where the choice changes what the parent sees, say so in the plan.

## Open items

- Code-signing: a certificate costs money and needs identity vetting. Fallback is a self-signed cert trusted on the one laptop plus a Defender exclusion written by the installer. The builder implements the fallback first; real signing is a later swap.
- Windows edition on the laptop is unchecked. Nothing in this design requires Pro, but the builder verifies on the real machine.
