# Changelog

All notable changes to this project are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

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

### Changed

- README corrected against the source: five backdrop filenames did not
  exist, the architecture section listed two test files and "31+ tests"
  against an actual nine and 108, the tray and widget menus were described
  as interchangeable when neither is a superset of the other, and the
  credential section omitted the non-Windows encryption path entirely.

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

- "Pin to desktop" no longer blacks out. The WorkerW reparenting trick made
  the widget a child window, which DWM stops composing on many GPU/driver
  combinations (ARM64 especially) -- alive but painted solid black. Desktop
  pinning now uses bottom-most z-order enforcement instead: the window stays
  a normal top-level window (hardware rendering, DWM corners and shadow all
  intact), held under every app by a WM_WINDOWPOSCHANGING hook and kept out
  of Alt+Tab. Win+D minimizes it for a frame; it restores itself instantly.
  The WorkerW path remains available via KITEGLANCE_WORKERW=1.

### Added

- Backdrop system: four pre-rendered mesh gradients (dawn, day, dusk,
  night) with a Background menu offering Time of day (default, follows the
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
