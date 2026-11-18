# Tether for Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows service plus tray helper that reports keywords, screenshots, live screen, DNS, apps and usage to the existing Tether server, and enforces app, DNS and bedtime rules on the child's laptop.

**Architecture:** `windows/` .NET 8 solution. `Tether.Service` (LocalSystem) owns the server link, DNS proxy and tamper checks. `Tether.Helper` (child's session) owns screen, UI Automation text, keyboard buffer, foreground tracking, app blocking and lock. They talk over a named pipe. `Tether.Shared` holds the pure logic so it is unit-tested.

**Tech Stack:** .NET 8 (`net8.0` Shared, `net8.0-windows` the rest), WinForms (tray, overlay, screen capture via `Graphics.CopyFromScreen`), WPF reference for `System.Windows.Automation`, xUnit. Server: existing Node 20 Fastify, `node --test`.

**Spec:** `docs/superpowers/specs/2026-11-18-windows-client-design.md`

## Global Constraints

- Server stays Node 20, Fastify, SQLite; changes are additive only.
- Windows 10 21H2 or later and Windows 11; nothing may need Enterprise features.
- Device `kind` is `'pc'`. For a PC the `package` field in `installed_apps`, `usage`, `app_rules`, `dns_log` is the lowercase exe file name (`chrome.exe`); `app_name` is the file description.
- Keyword rules identical to `KeywordWatch.kt`: word-start match, open end; 20 s between shots; same word in same app repeats after 10 min.
- Live screen: primary monitor, 1280 px wide, JPEG quality 60, 1 frame per second, auto-stop after 30 minutes.
- Typed text is matched on the laptop and discarded; nothing keystroke-level is uploaded or stored.
- Only the child's Windows account is monitored; the parent's admin session never is.
- Commit messages: short, no AI attribution lines, no real names.
- **Spec deviation (my call):** screen capture uses GDI `Graphics.CopyFromScreen` instead of Windows.Graphics.Capture. Same output at 1 fps, no WinRT interop. The spec is amended in Task 8.

## Review Focus

1. Service crash or stop while adapter DNS points at 127.0.0.1: internet dies. Service recovery restarts it within seconds; uninstall restores the original DNS. (Task 6, 8)
2. Helper task fires on logon for every account: it must exit unless the user is the configured child, so the parent's session is never watched. (Task 3, 8)
3. UI Automation on large or stale trees (a browser with many tabs, elements disposed mid-walk): bounded walk, exceptions swallowed, no hang. (Task 4)
4. Sleep and resume, and network changes: adapter DNS and the command long-poll re-establish themselves. (Task 6)
5. Local-time rollover and DST for schedule windows and per-day usage totals. (Task 5)

---

### Task 1: Server accepts PC devices

**Files:**
- Modify: `server/src/index.js` (enroll handler, line ~500), `server/src/alerts.js` (`checkTamper`), `server/public/app.js` (`TV_TABS`/`tabsFor`, `VIEWS.overview`)
- Create: `server/test/tamper.test.js`

**Interfaces:**
- Produces: `POST /api/device/enroll` stores `kind: 'pc'` when sent. `tamperMessages(kind: string): Record<'admin_active'|'accessibility_active'|'notif_listener_active', string>` exported from `alerts.js`; `checkTamper` uses `device.kind`. Dashboard `tabsFor(d)` returns the PC tab set for `d.kind === 'pc'`.

- [ ] **Step 1: Write failing test** `tamperMessages('pc').accessibility_active` equals `'Tether helper was stopped'`, `.admin_active` equals `'Tether service was stopped'`; `tamperMessages('phone').admin_active` is unchanged from today's `'Device Owner / admin was turned off'`.
- [ ] **Step 2:** `cd server && npm test` fails (export missing).
- [ ] **Step 3:** Implement `tamperMessages`; make `checkTamper` read `device.kind` (the sync handler already passes the pre-sync device row).
- [ ] **Step 4:** Enroll: `kind === 'tv' ? 'tv' : kind === 'pc' ? 'pc' : 'phone'`.
- [ ] **Step 5:** Dashboard: `PC_TABS = ['overview','alerts','search','usage','apprules','sites','schedule','settings']`. In `VIEWS.overview`, for `pc` skip the locations fetch and show `liveScreenCard()` plus Lock/Unlock buttons; verify the other listed tabs render with empty phone-only data.
- [ ] **Step 6:** `npm test` passes; run the server locally, enrol with `curl` using `kind: "pc"`, open the dashboard and confirm the tab set.
- [ ] **Step 7:** Commit `Server: accept PC devices, PC dashboard tabs`.

### Task 2: Solution scaffold and Shared logic

**Files:**
- Create: `windows/Tether.sln`, `windows/src/Tether.Shared/*.cs`, `windows/tests/Tether.Tests/*.cs`, `windows/.gitignore`
- Shared files: `KeywordMatcher.cs`, `HitThrottle.cs`, `BlockList.cs`, `ScheduleWindow.cs`, `AppRuleEvaluator.cs`, `TetherApi.cs`, `PipeMessages.cs`, `Config.cs`

**Interfaces (all produced; later tasks consume):**
- `KeywordMatcher(IEnumerable<string> plain, IEnumerable<string> regexes)`; `string? Match(string text)` returns the matched word or null.
- `HitThrottle(Func<long> nowMs)`; `bool ShouldShoot()` (20 s), `bool ShouldReport(string word, string app)` (10 min).
- `BlockList`: `void Load(string text)` (parse the format `AdBlock.kt` reads), `bool IsBlocked(string domain)` matches the domain or any parent.
- `ScheduleWindow`: `record ScheduleWindow(int Days, int StartMin, int EndMin)`; `static bool IsInside(IReadOnlyList<ScheduleWindow> w, DateTime local)` with the same day-bit and midnight-wrap semantics as `ScheduleManager.kt`.
- `AppRuleEvaluator.ShouldBlock(AppRule rule, long todayForegroundMs): bool`; `record AppRule(string Package, bool Blocked, int? DailyLimitMin)`.
- `TetherApi(HttpClient http, Uri baseUrl, Func<string?> token)` with `EnrollAsync(email, password, name)`, `GetKeywordsAsync()`, `GetAppRulesAsync()`, `GetScheduleAsync()`, `GetBlocklistAsync()` returning `(string Version, string Text)`, `GetAdblockVersionAsync()`, `PostSyncAsync(SyncBody)`, `PostScreenAsync(byte[])`, `PostMediaAsync(byte[], string kind, long ts)`, `PostScreenHitAsync(ScreenHit)`, `GetCommandsAsync(CancellationToken)` (long poll), `CommandDoneAsync(long id, bool ok)`. Wire shapes copied from `server/src/index.js`.
- `PipeMessages`: newline-delimited JSON; `record HelperToService(string Type, ...)` types `usage`, `hit`, `status`; `record ServiceToHelper(string Type, ...)` types `keywords`, `rules`, `schedule`, `command`.

- [ ] **Step 1:** Scaffold with `dotnet new sln`, class libs and xunit project; `dotnet build` passes.
- [ ] **Step 2: Failing tests** (one file per class):
  - Matcher: `"sex"` does not match `"Essex"`, matches `"sexual"` and `"SEX tape"`; regex `"s[3e]x"` matches `"s3x"`; empty list matches nothing.
  - Throttle: a second `ShouldShoot()` at +19 999 ms is false, true at +20 000; `ShouldReport("x","chrome.exe")` repeats false at +9 min, true at +10 min, and a different app reports immediately.
  - BlockList: `Load` then `IsBlocked("a.b.example.com")` is true when `example.com` is listed; `IsBlocked("notexample.com")` is false.
  - Schedule: a window 22:00 to 06:00 on a Friday-only day bit is inside at Fri 23:00 and at Sat 05:00, outside at Sat 07:00; spring-forward and fall-back days give the same answer as wall-clock minutes.
  - Rules: `Blocked` always blocks; limit 60 blocks at `60*60_000` ms, not at `60*60_000 - 1`.
- [ ] **Step 3:** `dotnet test` fails; implement each class; `dotnet test` passes.
- [ ] **Step 4:** `TetherApi` has no unit test beyond a fake `HttpMessageHandler` check that `PostScreenHitAsync` sends `words, package, app_name, snippet, ts` as JSON with the bearer token; add it.
- [ ] **Step 5:** Commit `Windows: solution scaffold and shared logic`.

### Task 3: Helper shell, screen capture and live view

**Files:**
- Create: `windows/src/Tether.Helper/{Program.cs,Tray.cs,ScreenCapture.cs,ScreenStreamer.cs,PipeClient.cs}`, `windows/tests/Tether.Tests/ScreenCaptureTests.cs`

**Interfaces:**
- Consumes: `PipeMessages`, `Config` (child username, pipe name).
- Produces: `ScreenCapture.CaptureJpeg(int maxWidth = 1280, long quality = 60): byte[]`; `ScreenStreamer(Func<byte[], Task> sink)` with `Start()`, `Stop()`, `CaptureOnce()`, auto-stop at 30 min. Helper `Program` exits immediately when `Environment.UserName` differs from `Config.ChildUser`. Tray icon tooltip: `Tether: this laptop is monitored by your parent`.

- [ ] **Step 1: Failing test** `CaptureJpeg` returns bytes starting `FF D8`, width exactly 1280 on a 1920-wide primary, and the streamer's injected clock stops it after 30 minutes with no stop command.
- [ ] **Step 2:** Implement; handle DPI scaling (`SetProcessDPIAware`/per-monitor manifest) so the capture covers the full primary screen.
- [ ] **Step 3:** Helper maps `command` pipe messages `SCREEN_START`, `SCREEN_STOP`, `SCREENSHOT` to the streamer; frames go out as `HelperToService` type `frame` with base64 bytes (service posts to `/api/device/screen`).
- [ ] **Step 4:** Run helper with a fake pipe server and confirm a JPEG frame arrives each second; confirm it exits as a different user name.
- [ ] **Step 5:** Commit `Windows helper: tray, screen capture, live stream`.

### Task 4: Keyword watch

**Files:**
- Create: `windows/src/Tether.Helper/{KeywordWatch.cs,UiaTextReader.cs,KeyboardBuffer.cs}`, `windows/tests/Tether.Tests/KeyboardBufferTests.cs`

**Interfaces:**
- Consumes: `KeywordMatcher`, `HitThrottle`, `ScreenCapture`, pipe `keywords` messages.
- Produces: `KeywordWatch(IPipeSink sink)` with `UpdateKeywords(plain, regexes)`; emits pipe `hit` carrying `words, package, app_name, snippet, ts` and a `shot` frame (JPEG) stamped `ts`. `KeyboardBuffer`: `void Push(char c)`, `void Reset()`, `string Snapshot()` holding the last 64 characters; cleared on window change, on 3 s idle, and never persisted.

- [ ] **Step 1: Failing tests** for `KeyboardBuffer`: keeps only the last 64 chars; `Reset()` empties it; backspace removes the last char.
- [ ] **Step 2:** Implement `UiaTextReader.Read(IntPtr hwnd): string` walking the foreground window's elements with a 200 ms budget and 500-element cap, skipping `IsPassword`, catching `ElementNotAvailableException` and any COM error, returning what was collected.
- [ ] **Step 3:** Subscribe to focus-change and `TextChanged` events plus a 2 s foreground poll; low-level `WH_KEYBOARD_LL` hook pushes printable keys into the buffer and checks on each word break. On a hit: respect `HitThrottle`, capture, emit.
- [ ] **Step 4: Manual check** on this machine with the server running: add keyword `zebra` on the dashboard; type it in Notepad, and open a page containing it in Edge. Alert with screenshot arrives in both cases; typing it in a password field does not alert.
- [ ] **Step 5:** Commit `Windows helper: keyword watch`.

### Task 5: Usage, app rules and bedtime lock

**Files:**
- Create: `windows/src/Tether.Helper/{ForegroundTracker.cs,AppEnforcer.cs,BlockedOverlay.cs,ScheduleLocker.cs}`, `windows/tests/Tether.Tests/ForegroundTrackerTests.cs`

**Interfaces:**
- Consumes: `AppRuleEvaluator`, `ScheduleWindow`, pipe `rules`, `schedule`, `command` (`LOCK`, `UNLOCK`).
- Produces: `ForegroundTracker(Func<DateTime> localNow)` with `Tick(string exe, string description, TimeSpan elapsed)` and `Drain(): IReadOnlyList<UsageRow>`; `record UsageRow(string Day, string Package, string AppName, long ForegroundMs)`. Day key is local `yyyy-MM-dd`.

- [ ] **Step 1: Failing tests:** ticks around local midnight split into two day keys; a DST day still totals wall-clock time; `Drain()` returns cumulative per-day totals for the same package, not deltas.
- [ ] **Step 2:** Implement tracker (5 s sample of foreground exe via `GetForegroundWindow` and `QueryFullProcessImageName`), reporting rows to the service every 60 s.
- [ ] **Step 3:** `AppEnforcer`: on process start (WMI `Win32_ProcessStartTrace`, 2 s poll fallback) end the process when `AppRuleEvaluator.ShouldBlock`; show `BlockedOverlay` for 5 s with `Blocked by your parent`.
- [ ] **Step 4:** `ScheduleLocker`: while inside a window or after `LOCK`, call `LockWorkStation()`, and again within 10 s of any unlock until the window ends or `UNLOCK`.
- [ ] **Step 5: Manual check:** rule-block `notepad.exe` from the dashboard; it closes on launch with the overlay. A window covering now locks the screen.
- [ ] **Step 6:** Commit `Windows helper: usage, app rules, bedtime lock`.

### Task 6: Service, sync and DNS

**Files:**
- Create: `windows/src/Tether.Service/{Program.cs,SyncWorker.cs,CommandWorker.cs,PipeHost.cs,DnsProxy.cs,DnsPacket.cs,AdapterDns.cs,BrowserPolicy.cs,InstalledApps.cs,TamperStatus.cs}`, `windows/tests/Tether.Tests/DnsPacketTests.cs`

**Interfaces:**
- Consumes: `TetherApi`, `BlockList`, pipe messages.
- Produces: `DnsPacket.ParseQuestion(byte[] query): (string Name, ushort Type)?`; `DnsPacket.BuildNxDomain(byte[] query): byte[]`; `DnsProxy(BlockList list, IPEndPoint[] upstream, Action<string> onLookup)` listening on `127.0.0.1:53` and `[::1]:53` over UDP and TCP; `AdapterDns.Apply(IPAddress[] servers)`, `AdapterDns.Restore()`; `BrowserPolicy.Apply()` writing `DnsOverHttpsMode=off` for Chrome and Edge and the Firefox `DNSOverHTTPS` policy `Enabled:false, Locked:true`, plus a firewall rule blocking outbound TCP 853.

- [ ] **Step 1: Failing tests:** `ParseQuestion` on a captured `example.com A` packet returns `("example.com", 1)`; `BuildNxDomain` keeps the query id, sets QR and RCODE 3; malformed or truncated bytes return null, not an exception.
- [ ] **Step 2:** Implement `DnsPacket`, then `DnsProxy`: blocked names get NXDOMAIN, others forward to the upstream captured at install (fallback `1.1.1.1`) with a 3 s timeout; a failed upstream returns SERVFAIL and the proxy keeps running. Every lookup calls `onLookup`.
- [ ] **Step 3:** `SyncWorker` every 5 min and on command wake: `PostSyncAsync` with `status` (`admin_active` = service running, `accessibility_active` = helper pipe connected, DNS and policy intact), `apps`, `usage` (from helper), `dns` (buffered lookups). Refresh keywords, rules, schedule, blocklist (when `GetAdblockVersionAsync` changes) and forward them to the helper.
- [ ] **Step 4:** `CommandWorker` long-polls; `SCREEN_START/STOP/SCREENSHOT/LOCK/UNLOCK` go to the helper, anything else is `CommandDoneAsync(id, false)`. Re-apply adapter DNS every 60 s and on `NetworkChange.NetworkAddressChanged` and `SystemEvents.PowerModeChanged` resume. Service recovery: restart on first, second and third failure.
- [ ] **Step 5:** `dotnet test` passes. Manual: run the service console build as admin; `nslookup` a listed domain returns NXDOMAIN and an unlisted one resolves; the dashboard Sites tab shows both lookups; kill the service and confirm the recovery restart brings DNS back.
- [ ] **Step 6:** Commit `Windows service: sync, commands, DNS proxy`.

### Task 7: Installer

**Files:**
- Create: `windows/src/Tether.Setup/{Program.cs,Installer.cs}`, `windows/build.ps1`

**Interfaces:**
- Produces: `tether-setup.exe install --server <url> --email <e> --child <username>` (prompts for the password; never on the command line) and `uninstall`.

- [ ] **Step 1:** `install` as admin: copy binaries to `%ProgramFiles%\Tether`, enrol (`kind: 'pc'`, name = machine name), save the token to `%ProgramData%\Tether\config.json` ACL'd to SYSTEM and Administrators only, record original adapter DNS, register the service with recovery actions, create the logon scheduled task for `--child` only, apply browser policy and the firewall rule, add the Defender exclusion for the install folder, start everything.
- [ ] **Step 2:** `uninstall` restores adapter DNS first, then removes the task, service, policies, firewall rule, exclusion and files.
- [ ] **Step 3:** `build.ps1` publishes self-contained single-file builds and self-signs them with a local cert, exporting the cert for the child laptop's trust store.
- [ ] **Step 4: Manual check** on this machine with a test standard account: install, log in as that account, confirm tray icon, a keyword hit, live screen and a blocked app; log in as the admin account and confirm the helper does not run; uninstall and confirm normal internet.
- [ ] **Step 5:** Commit `Windows: installer and build script`.

### Task 8: Docs and download route

**Files:**
- Modify: `server/src/index.js` (add `/download/tether-setup.exe` beside the APK route), `README.md`, `docs/superpowers/specs/2026-11-18-windows-client-design.md`

- [ ] **Step 1:** Route serves `server/data/tether-setup.exe` if present, 404 otherwise, same as the APK route.
- [ ] **Step 2:** README Windows section: install steps, the standard-account requirement, the monitored notice for the child, limits (secure desktop and DRM black, apps with hard-coded DoH, a local admin can remove it).
- [ ] **Step 3:** Amend the spec: screen capture via GDI, and the helper restricted to the configured child account.
- [ ] **Step 4:** Commit `Windows: docs and download route`.

## Self-review

- Spec coverage: goals 1 to 6 map to Tasks 4, 3, 6, 5 and 6, 5, 6; server changes Task 1; distribution Tasks 7 and 8.
- Types: `HitThrottle`, `KeywordMatcher`, `ScheduleWindow`, `AppRuleEvaluator`, `UsageRow` named identically across tasks.
- One spec gap fixed here: the logon task must be limited to the child account (Review Focus 2), which the spec's "all-users logon trigger" missed.
