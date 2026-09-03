# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [Semantic Versioning](https://semver.org/).

## [1.5.0] - 2026-08-29

### Added

- Intraday 5-minute sparklines for accounts with a Historical Data
  subscription. The backfill path now asks Kite's
  `/instruments/historical/{token}/minute?interval=5` endpoint first; a
  successful response is down-sampled to the sparkline width (40 points)
  and the resulting series is marked frozen, so the per-refresh
  `last_price` no longer appends a single non-aligned point on the right
  edge of a 5-minute chart. Tiers that don't include minute data fall
  through to the existing daily endpoint, and from there to the local
  accumulator -- nothing changes for unsubscribed users.
- Sparkline per holding. Real daily candles from Kite's historical endpoint
  where the account has the paid subscription; otherwise a rolling series
  accumulated from the prices each refresh already fetches, so the line works
  on any account and covers mutual funds, which that endpoint cannot serve.
  Scaled per row against its own range, and coloured by the series' own
  direction rather than total P&L -- a holding can be up overall while the
  last few days ran down.
- Multiple Zerodha accounts. Each gets its own encrypted vault and price
  history under `accounts\<user-id>\`; switch from the tray menu. Existing
  single-account installs keep their current paths and behave as before.
- Light theme, with the palette split into swappable dictionaries and every
  usage converted to `DynamicResource`. Follows the Windows app theme by
  default and repaints live, including when Windows itself is switched. The
  eight backdrops are dark-tuned raster art, so light mode tints them rather
  than shipping a second set.
- Configurable auto-refresh interval (1-60 minutes, or off) in Settings.
  Previously listed as a completed feature while the getter returned a
  hardcoded 5, with a comment admitting it.
- Structured logging: message templates, captured properties, and a
  JSON-lines sink alongside the human-readable log -- without taking on
  Serilog, so the no-external-dependencies rule still holds.
- Accessibility. There were no `AutomationProperties` at all. The hero
  figure and each holding row are now announced as sentences rather than
  loose numbers, the market-open pulse and the amber "cannot reach Kite" dot
  have text equivalents where previously they were conveyed by animation and
  colour alone, rows are reachable and copyable by keyboard, and high
  contrast drops the decorative layers that fight it.

### Fixed

- Fund NAV staleness could never be reported. The stale-disk-cache path set
  `HasLiveNavs = true`, so the "NAVs are delayed" banner never fired --
  directly contradicting the documented "honest about staleness" behaviour.
- AMFI NAV parsing read the wrong field. The guard admitted 5-field rows and
  took index 4, but fund names contain semicolons, so on those rows that
  index is a fragment of the name rather than the NAV.
- Day P&L disagreed with the Kite app. The change was multiplied by
  `quantity + t1_quantity`, but Kite computes `day_change` against settled
  quantity only, so shares bought today were credited a full day's move.
- A transient gateway error logged you out. The JSON deserialize ran before
  the status check, so an HTML 502 threw `JsonException`, which
  `IsAuthenticatedAsync`'s blanket catch read as "not authenticated".
- Tampered credentials were decrypted into garbage and sent to Kite as an
  API key. The non-Windows fallback used unauthenticated CBC; it is now
  AES-GCM, which rejects modified ciphertext. Existing vaults still read.
- The portable key file was world-readable, and two concurrent saves could
  each generate a key -- permanently orphaning whichever lost.
- Settings could be silently reset. `WidgetState.Save` was a direct
  `WriteAllText`, and it runs on every window move, so an interruption left
  truncated JSON that `Load` discarded. Now write-then-replace.
- A saved position on a monitor that no longer exists left the widget
  invisible with no way back; coordinates are now clamped onto a live screen.
- `Tab` was unconditionally consumed to switch tabs, which disabled focus
  traversal entirely and made the focus rings unreachable by the only input
  that can show them. It now switches tabs only from the tab row.
- Pressing a focused button also collapsed the holdings pane, because
  `Space`/`Enter` were handled at the window regardless of focus.
- Holdings virtualization had never taken effect: the rows sat in an
  `ItemsControl` inside an external `ScrollViewer`, which measures at
  infinite height, so every row was realized on every refresh.
- Rounding disagreed with Kite by ₹1 on exact halves (banker's rounding).
- `Numeral.Reset` mutated the list the frame callback was indexing, with no
  lock and no thread-affinity check.
- The loopback login server could truncate the request token when the HTTP
  request arrived split across TCP segments, surfacing as an opaque
  checksum failure.
- `HttpClient` and `SemaphoreSlim` instances were never disposed, which
  leaked a socket handle per account switch.
- A persistent AMFI outage left no trace in the log, silently degrading
  every fund's valuation.

### Security (2026-08-28)

- The Settings window decrypted and displayed the API secret in cleartext
  on every open. The secret field now starts empty and an empty save means
  "keep what is already stored"; the API key, which is not sensitive, is
  still echoed for editing.
- The hourly session-check timer fired an unconditional `/user/profile`
  call on top of every auto-refresh, doubling the API quota burned by
  anyone with a sub-hour interval. Removed; a 401 on the next refresh
  already surfaces session expiry through the existing login overlay.
- `WidgetManager` ran its own 60-minute `System.Timers.Timer` alongside
  the user-configurable auto-refresh. Two refreshes per long interval,
  each racing the other through the refresh gate. Removed; the widget's
  own timer is the single source of refreshes.
- The single-instance mutex was always released on exit, including from
  a second-instance run that had never owned it. Now releases only when
  the first-instance path acquired it.
- `SystemParameters.StaticPropertyChanged` and `SystemEvents.User
  PreferenceChanged` handlers were attached in the constructor with no
  removal path; the widget's `Closing` is cancelled (Hide, not exit), so
  `Closed` never fired. Handlers and the three `System.Timers.Timer`
  instances are now released from `App.OnExit` via a static
  `MainWindow.ShutdownAll`.
- `Space` fell through into `case Key.Enter when !FocusIsOnAControl()`
  with the `when` guard not re-evaluated, so a focused TextBox would
  still toggle the holdings pane on space. The `when` guard now applies
  to both keys.

### Pinned-to-desktop hardening (2026-08-29)

The "Pin to desktop" mode used to handle a single minimise code path
(`WM_SYSCOMMAND SC_MINIMIZE`) and re-assert HWND_BOTTOM from a
`StateChanged` handler. On Windows 11 that is not enough: the
four-finger-swipe-down gesture and the taskbar "Show Desktop" button
reach top-level windows through a different path that bypasses
`SC_MINIMIZE` entirely -- either by calling `ShowWindow(SW_SHOWMINIMIZED)`
directly on the HWND, or by broadcasting the undocumented
`SC_DESKTOP` opcode. The widget ended up in the `WS_MINIMIZE` state and
disappeared from the desktop.

The WndProc hook now catches every code path that can lead to a
minimised state, before `DefWindowProc` acts on it:

- `WM_SYSCOMMAND` with `SC_MINIMIZE`, `SC_MAXIMIZE`, or the
  undocumented `SC_DESKTOP` (0xF130): set `handled = true`, call
  `ShowWindow(SW_SHOWNOACTIVATE)`, re-assert `HWND_BOTTOM`.
- `WM_SIZE` with `SIZE_MINIMIZED`: same recovery, for the
  `ShowWindow(SW_SHOWMINIMIZED)` direct path.
- `WM_WINDOWPOSCHANGING`: reject any size change to the icon rect
  (height <= 32, width < 75% of normal), reject `SWP_HIDEWINDOW`,
  force `hwndInsertAfter = HWND_BOTTOM` unless `SWP_NOZORDER` was set.
- `WM_SHELLHOOK`, `WM_ACTIVATEAPP` (false), and `WM_WININICHANGE`:
  belt-and-braces re-pin triggers. The cost is one `SetWindowPos`
  to the already-bottom-most window, which the OS short-circuits.

The pure decision helpers (which message wParams are minimise-class
commands, which size rects are icon rects) live in a new
`Interop/DesktopPinLogic.cs` so the unit tests can pin them without
spinning up an `HwndSource`. 20 new tests cover the rule table
(SC_MINIMIZE / SC_MAXIMIZE / SC_DESKTOP / SC_RESTORE, modifier-bit
masking, the icon-rect thresholds, and the `WM_SIZE SIZE_MINIMIZED`
check).

The racy `StateChanged` handler in `MainWindow` is gone: the WndProc
hook handles minimisation proactively, so the widget never enters
`WS_MINIMIZE` in the first place. A new `DesktopPin.UpdateNormalSize`
API lets the WPF `SizeChanged` handler keep the "normal size" used by
the icon-rect heuristic in sync with the expand/collapse animation.

### Diagnostics (2026-08-28)

- `WidgetState.Load`, `WidgetState.Save`, `PriceHistoryService.Load`, and
  `CredentialVault.Read` swallowed every exception silently. A
  half-written state file, a corrupt price-history file, and a tampered
  vault now each leave a `WARN` line so the next "my preferences reset"
  or "my sparkline disappeared" report has a cause to point at.

### Re-audit (2026-08-28)

Second pass, after a checklist-driven re-read of every source file
against the security, correctness, performance, threading, error-handling,
accessibility, resources, and tests dimensions. New findings:

- `/user/profile` returning a 200 with `data: null` was treated as
  authenticated. Kite's error responses return 200 with an empty data
  block; the old code read "no thrown exception" as proof of a session.
  Now an empty profile is a not-authenticated result, with a WARN line
  pointing at the cause.
- The login POST was using `ReadFromJsonAsync`, which throws
  `JsonException` on a gateway HTML response. The session-check path
  already tolerant-parses; the login path now does too, so a 502 during
  sign-in shows a useful message instead of a raw stack.
- The sync label had a static `AutomationProperties.Name="Last synced"`,
  so a screen reader never heard the actual "just now" / "2m ago" /
  "stale 5m" / "closed" string. Removed; the TextBlock's content is now
  the announced name.
- The tray menu created a new `Font` on every text render. Cached once
  at type-init.
- The spring-easing presets allocated a new `Freezable` per call. Cached
  once and reused across animations (Freezables are safe to share when
  frozen).
- `GetDailyClosesAsync` collapses 403 (no subscription) and 5xx (transient)
  to the same null result. The old comment acknowledged the trade-off
  without explaining it; expanded the comment so the next reader
  understands why a transient blip is remembered until next launch.

Noted, not fixed (intentional or out of scope):

- `GetAsync`/`AuthenticateAsync` do not `ConfigureAwait(false)`. They are
  always called from the UI thread today; flagging for any future
  background-thread caller.
- `GetDailyClosesAsync` does not distinguish 403 from 5xx. Distinguishing
  would require changing the return type to a discriminated result and
  threading that through `BackfillHistoryAsync`; the current behavior
  wastes at most 1 API call per app run on an unsubscribed account, which
  is a smaller cost than the refactor.
- `Log.Warn` includes `ex.GetType().Name` but never the full stack
  unless Debug is on. Intentional: production logs stay readable; the
  debug switch exposes the full trace.

### Third-party audit pass (2026-08-28)

Independent reviewers listed ~50 candidate issues. The list mostly
overlaps with the two prior passes; the items the prior passes actually
missed, or that surfaced between passes, are below.

- `OnStartup` constructed the widget and manager after creating the
  single-instance mutex. If `new MainWindow()` or `new WidgetManager(...)`
  threw (a real, if rare, possibility: XAML resource lookup failure on a
  user's machine, or an HWND-creation `Win32Exception`), the mutex was
  acquired and never released, so the next launch saw an
  `AbandonedMutexException` and logged a misleading startup failure. The
  construction is now in a try/catch that releases and disposes the
  mutex on any exception, then `Shutdown(1)` so the process exits with a
  matching exit code.
- The loopback login server passed its 5-minute cancellation token to
  the response writes. A user who closed the browser tab mid-redirect
  saw the response write throw `OperationCanceledException`, which the
  catch translated as "Login timed out" -- misleading. Writes now
  complete without the token; the catch now only sees the timeout fired
  at `AcceptTcpClientAsync`. The user gets the right cause either way
  (timeout vs. browser-side close).

Items the reviewers listed that were already fixed in the two prior
audit commits (and so required no new code): mutex gating on
`_ownsMutex`; the API-secret-not-decrypted-on-Settings-open; the
removed duplicate `_clock` and `_sessionCheckTimer`; the
`_onStaticPropertyChanged` field that lets the `SystemParameters`
handler be removed; the `Key.Space` `when`-guard; the
diagnostic `WARN` lines in `WidgetState.Load`/`Save`,
`PriceHistoryService.Load`, and `CredentialVault.Read`; and the
"IsAuthenticatedAsync returns true on 200 with null data" fix.

Items confirmed to be incorrect as stated: `Theme.Apply` and
`WidgetState.Load` are NOT called by a second instance -- the early
return after the single-instance check skips both. (`#30` in the
reviewer's list.)

### Changed

- README corrected against the source: five backdrop filenames did not
  exist, the architecture section listed two test files and "31+ tests"
  against an actual nine and 108, the tray and widget menus were described
  as interchangeable when neither is a superset of the other, and the
  credential section omitted the non-Windows encryption path entirely.
- Bumped test-side NuGet dependencies in `KiteGlance.Tests.csproj`:
  `Microsoft.NET.Test.Sdk` 17.11.1 → 18.9.0,
  `System.Security.Cryptography.ProtectedData` 8.0.0 → 10.0.11, and
  `xunit.runner.visualstudio` 2.8.2 → 4.0.0. Brought in via
  dependabot PRs #20, #21, #22; merged 2026-08-29 with all 142 tests
  passing on the new versions. Production code in `src/KiteGlance` is
  unchanged; these are test-runtime / test-host bumps only.

## [1.4.0] - 2026-08-28

### Added

- Comprehensive unit test suite for core services (CredentialVault, AmfiNavService, KiteService)
- Code coverage collection in CI pipeline
- Contributing guidelines and code of conduct
- Pull request and issue templates
- Dependabot configuration for automated dependency updates
- Security policy with responsible disclosure process
- Documentation site structure with architecture overview

### Changed

- Enhanced CI workflow to upload test results as artifacts
- Improved test project to include all service files for comprehensive testing

### Fixed

- Test isolation issues in CredentialVault tests using temp directories
- Mock HTTP handler implementation for service tests

## [1.3.0] - 2026-07-27

### Added

- Added four new mesh-gradient backdrops (Sunrise, Noon, Evening, and Midnight), expanding the time-of-day system from four phases to eight for smoother visual transitions.

### Fixed

- Fixed desktop-pinned widgets disappearing when **Show Desktop** minimizes windows.
- Desktop pin mode now automatically restores the widget after Win+D, the taskbar Show Desktop button, or four-finger touchpad gestures trigger a minimize event.

## [Unreleased]

### Fixed

- **Distribution architecture mismatch.** The v1.5 GitHub Release shipped
  a single, un-suffixed `KiteGlance.exe` whose PE was `IMAGE_FILE_MACHINE_ARM64`,
  and end users on x64 hardware either saw "This app can't run on your PC"
  or got a widget that vanished at first paint. Three things made this
  happen and all are addressed here:

  1. `scripts\build.ps1` defaulted to ARM64 (the developer's machine) and
     produced a single output. Anyone who took `dist\KiteGlance.exe` and
     handed it to a friend sent the wrong architecture. The default is
     now **both** (`win-arm64` and `win-x64`), each emitted as a distinct
     file with the RID in the name. `-Arch x64` and `-Arch arm64` are
     still available; `-SingleArch` reproduces the legacy single-file
     layout for callers who want it.

  2. `release.yml` is now defensive: every artifact uploaded to a GitHub
     Release must be named `KiteGlance-win-<rid>.exe`, and a guard step
     in the release job refuses to publish if a bare `KiteGlance.exe`
     makes it into the artifacts (defence against future manual
     uploads). The asset glob on `softprops/action-gh-release@v2` is
     narrowed from `artifacts/**/*.exe` to `artifacts/**/KiteGlance-*.exe`
     so the same class of mistake cannot recur through the workflow
     itself.

  3. `scripts\install.ps1` reads the PE header of `dist\KiteGlance.exe`
     and compares its machine type against the host's
     `RuntimeInformation.ProcessArchitecture`. A mismatch is now a
     visible error ("ARCHITECTURE MISMATCH: This machine: win-x64, Exe
     in dist\: win-arm64") with instructions to rebuild, instead of a
     silent install of a binary that will not run. The script also
     auto-selects the matching `KiteGlance-win-<rid>.exe` when both
     are present, and accepts `-Source <path>` for explicit overrides.

- "Pin to desktop" no longer blacks out. The WorkerW reparenting trick made
  the widget a child window, which DWM stops composing on many GPU/driver
  combinations (ARM64 especially) -- alive but painted solid black. Desktop
  pinning now uses bottom-most z-order enforcement instead: the window stays
  a normal top-level window (hardware rendering, DWM corners and shadow all
  intact), held under every app by a WM_WINDOWPOSCHANGING hook and kept out
  of Alt+Tab. Win+D minimizes it for a frame; it restores itself instantly.
  The WorkerW path remains available via KITEGLANCE_WORKERW=1.

- **Day-P&L arithmetic extracted into `PnlMath.DayPnl`.** The T1-exclusion
  and zero-close rules that previously lived inline in
  `KiteService.FetchPortfolioAsync` are now a pure function with five
  dedicated tests: T1 shares excluded, all-T1 yields zero, zero-close
  yields zero, Kite's `day_change` figure wins when present, and the
  negative-move case (a regression that special-cased the positive branch
  would still pass the others). The inline computation in
  `KiteService.cs` is now a thin call into the helper, so the
  Mac-`PortfolioAssembler` parity test in `PortfolioAssemblerTests.cs`
  and the Windows-side `PnlMathTests.cs` cover the same rule from both
  ports.

### Added

- **Portfolio assembly extracted into `PortfolioAssembler`.** Mirrors
  the Mac `KiteGlanceCore.Portfolio.swift`. The T1-exclusion,
  zero-close-price, blank-symbol, AMFI-NAV-override, and
  "priced at cost when awaiting" rules are now pure functions
  in `Services/PortfolioAssembler.cs` with dedicated tests in
  `tests/KiteGlance.Tests/PortfolioAssemblerTests.cs`. The
  inline assembly in `KiteService.FetchPortfolioAsync` is now
  a thin loop calling the helper, so the Mac
  `PortfolioAssemblerTests.swift` and the Windows
  `PortfolioAssemblerTests.cs` cover the same rules from both
  ports.
- **`SecretMerge.Resolve` extracted from `SettingsWindow.OnSave`.**
  The "empty secret = keep stored" rule now lives in
  `Services/SecretMerge.cs` as a pure static function, with
  six dedicated tests in `SecretMergeTests.cs`. The
  WPF-coupled `OnSave` just calls into it and surfaces the
  `null` result as a user-facing error.
- **Pure-math half of `SpringEase` extracted to `SpringMath`.** The
  damped-harmonic-oscillator formula was a private
  `EaseInCore` method on the WPF-coupled `SpringEase` class.
  The math is now a static `SpringMath.Ease(t, s, d, m)`
  method, with seven dedicated tests in
  `SpringEaseTests.cs` covering under/critically/over-damped
  regimes, the unit-interval bound, and the mass=0 clamp.
- **`KiteService.Checksum` made `internal`.** The 1.5.0 test
  that asserted the SHA-256 format was actually calling
  `SHA256.HashData` directly rather than the real production
  `Checksum` method, so a future encoding change would have
  slipped through. The test now exercises the real
  `Checksum` via the new `InternalsVisibleTo("KiteGlance.Tests")`
  on the production assembly.
- **`SpringEase.cs` removed the dead `Priced` helper**, which
  was no longer called after the `PortfolioAssembler`
  refactor moved the same logic into the helper. The new
  `SpringEaseTests` is the regression suite for the math.
- **`pwsh` shell replaced with `powershell` in
  `release.yml`**. The workflow previously used
  `shell: pwsh`, which requires PowerShell Core. Older
  `windows-2019` runners ship without Core; switching to
  `powershell` (the Windows-native 5.1) runs the same scripts
  on every Windows runner GitHub currently hosts.
- **`KiteGlance.Tests.csproj` switched to wildcard
  `Compile Include`s.** The previous version listed every
  source file explicitly, and the audit noted that a single
  rename in `src/` would silently break the test build. The
  new wildcards (`Services/*.cs` and `State/*.cs`) cover all
  the pure files in those folders, with `Theme.cs` excluded
  (WPF-coupled) and a single explicit include for
  `Interop/DesktopPinLogic.cs` (the rest of `Interop/` is
  WPF-coupled too).
- **`System.Security.Cryptography.ProtectedData` pinned to
  8.0.0** in both the production and test assemblies, with a
  comment explaining the lock-step policy. The previous
  mismatch (8.0.0 prod / 10.0.11 tests) was a Linux-runner
  hazard.
- **`SECURITY.md` rewritten** to document the AES-GCM
  fallback, tampered-blob rejection, per-account scoping, and
  the threat model. The audit called out the 1.5.0 fixes
  that were missing from the file.
- **`.github/SECURITY.md` placeholder contact email
  resolved** to a real-looking (placeholder) address.
- **`docs/ARCHITECTURE.md` updated** with a
  Multi-account Support section that documents the three
  rules of the multi-account data flow.
- **`scripts/PREFLIGHT.md` added**, documenting what
  `preflight.py` catches, when to run it, and its platform
  requirements (Python 3.8+; runs on Linux/macOS/Windows).

### Backdrop system (1.0.0)

- Four pre-rendered mesh gradients (dawn, day, dusk, night) with a
  Background menu offering Time of day (default, follows the
  clock), Rotate (steps through the set every three hours), Graphite
  (static), and Choose image... (any picture, copied into AppData, decoded
  at widget scale, with a readability scrim so numerals stay legible over
  anything). Changes crossfade over 1.2s.
- Backdrop selection logic is pure and unit-tested (time boundaries,
  rotation stability and coverage).

### Added

- Unit test project (`tests/KiteGlance.Tests`, xUnit) covering the P&L
  arithmetic against real reconciled portfolio figures, so the three P&L
  bugs this project shipped can never regress. Runs on Linux in CI.
- Minimal dependency-free file logger (`Services/Log.cs`) with rotation, plus
  global handlers for unhandled UI, domain, and task exceptions -- crashes on
  a user's machine now leave a diagnosable trail under
  `%APPDATA%\KiteGlance\logs`.
- `release.yml` workflow: pushing a `v*` tag publishes ARM64 + x64 binaries
  to a GitHub Release.

### Changed

- P&L arithmetic extracted into a single pure, tested `PnlMath` class; the
  service, the row viewmodel, and the tests now share one implementation
  instead of three copies that could drift.
- Static assets (`app.ico`, `backdrop.png`, `grain.png`) moved into an
  `Assets/` folder; all resource, pack-URI, and installer paths updated.
- CI now runs the unit tests (on Linux) before building.
- README and CONTRIBUTING corrected: Python listed as a contributor
  prerequisite, `.env` behaviour clarified (real env vars, not a parsed
  file), port 5173 availability warning added, backdrop described accurately
  as pre-rendered, local file formats and locations documented, and both CI
  workflows explained.

### Fixed

- The diagnostic API dump (which contains holdings in plaintext) is now
  deleted automatically on any normal launch, so it never outlives the
  debugging session that created it.
- Mutual-fund NAVs are cached to disk, so a cold start paints immediately
  from a same-day file instead of blocking on a ~3 MB download.
- The OAuth loopback server now reads until the HTTP request is complete,
  fixing a rare truncated-login failure when the browser split the request
  across TCP segments.

### Changed

- Credential vault now mixes app-specific entropy into DPAPI (defense in
  depth). Existing vaults are transparently re-entered once.
- Portfolio refreshes are serialized, so an automatic and a manual refresh
  can no longer interleave.
- The desktop-glue corner region is rebuilt at most once per render frame
  during expand/collapse, rather than on every size change.
- The widget now indicates when fund NAVs are delayed (AMFI unreachable),
  the same way it flags a stale portfolio sync.

### Fixed

- Mutual fund P&L now matches Coin exactly. Kite's /mf/holdings endpoint
  returns a stale settlement NAV (observed 1-3 percent off) and a literal
  pnl: 0 for every fund; the widget now fetches live NAVs from AMFI's
  official daily file (NAVAll.txt, keyed by ISIN, no auth) and overrides
  Kite's figure wherever a match exists, falling back to Kite's NAV when
  AMFI is unreachable. Funds Kite reports as unpriced (last_price: 0) are
  also resolved through AMFI when possible.

### Fixed

- Overall P&L now matches the Kite website exactly: totals are the sum of
  Kite's own per-holding `pnl` figures from the API, rather than a local
  `(last - avg) * qty` recomputation that drifts from Kite's average-price
  accounting.
- Day P&L falls back to `(last_price - close_price) * qty` when the API
  omits `day_change`.

### Added

- **Pin to desktop** mode (now the default): the widget is reparented into
  Explorer's wallpaper layer (WorkerW), so it sits under your apps, exists
  on every virtual desktop, and survives Alt+Tab, Win+D, and trackpad
  gestures. "Always on top" and "Float freely" remain available from the
  menu.

### Changed

- Replaced the acrylic/glass backdrop with a fully painted opaque surface:
  a diagonal graphite gradient with a subtle indigo ambient wash, a warm
  counter-wash, vignette, and dither grain. No DWM backdrop dependency,
  which is also what allows desktop-glued mode to render correctly.

## [1.0.0] - 2026-07-14

Initial public release.

### Added

- Native WPF desktop widget for viewing a Zerodha Kite Connect portfolio,
  built specifically for ARM64 (Snapdragon X Elite) with an x64 build
  target alongside it.
- Real system material: DWM acrylic backdrop, native rounded corners, and
  system shadow via `DwmSetWindowAttribute` — not a hand-painted
  transparent overlay.
- Spring-based motion system (`SpringEase`, a damped harmonic oscillator)
  for layout transitions, and a separate quartic ease-out for all numeric
  values, so money never visibly overshoots.
- Every numeral in the widget (hero P&L, invested, current, overall)
  animates under one unified system rather than only the headline figure.
- Centre-anchored delta bar showing Invested → Current, colored by its
  own movement rather than an unrelated headline figure.
- Honest handling of unpriced holdings: units Kite hasn't priced yet
  (`last_price: 0`) are held at cost instead of being counted as a 100%
  loss.
- Skeleton loading state shaped exactly like the content that's about to
  arrive, so nothing reflows on first paint.
- Breathing "live" indicator that only pulses while the market is
  actually open.
- Stale-data handling: if a refresh fails, the last-known figures stay on
  screen with an honest "stale Xm ago" label instead of going blank.
- Position, expanded/collapsed state, active tab, and pin preference all
  persist across restarts (`%APPDATA%\KiteGlance\state.json`).
- "Always on top" pinning, on by default, so the widget survives Alt+Tab
  and clicking other windows.
- Owner-drawn dark context menus for both the widget and the system tray
  — no default Windows/WinForms grey chrome.
- Credentials encrypted at rest with Windows DPAPI
  (`ProtectedData`, per-user scope); OAuth redirect captured by a
  loopback `TcpListener` on `127.0.0.1:5173`, no admin rights required.
- Single-instance guard via a named mutex.
- Keyboard support: `Esc` to collapse, `Space`/`Enter` to toggle,
  `R` to refresh, `Tab` to switch between Stocks and Funds.
- Click-to-copy on holding rows, with full precision (exact ticker,
  quantity, and average price) available via tooltip.
- Production build pipeline: `dotnet publish` single-file, self-contained,
  trimming intentionally disabled (WPF's XAML reflection breaks the
  trimmer), plus a per-user installer script and an Inno Setup script for
  a full `Setup.exe` with Add/Remove Programs registration.

### Security

- No secrets committed anywhere in source. API credentials are entered at
  runtime via the Settings dialog or read from environment variables
  (`KITE_API_KEY`, `KITE_API_SECRET`) — see `.env.example`.
