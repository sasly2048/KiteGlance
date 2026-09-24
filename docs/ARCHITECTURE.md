# Architecture Overview

> Last revised against Kite Glance **1.5.0**. Earlier versions did
> not have multi-account support, the bottom-most z-order pin, or the
> 1.5.0 audit pass.

## System Design

Kite Glance follows a lightweight, single-process architecture
optimized for Windows desktop integration. The process owns exactly
one widget window, one tray icon, and one loopback OAuth listener.

```
┌─────────────────────────────────────────────────────────────┐
│                      Kite Glance Widget                      │
├─────────────────────────────────────────────────────────────┤
│  UI Layer (WPF + WinForms)                                   │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐       │
│  │ MainWindow   │  │ SettingsWin  │  │ WidgetMgr    │       │
│  │ (XAML)       │  │ (XAML)       │  │ (Tray)       │       │
│  └──────────────┘  └──────────────┘  └──────────────┘       │
├─────────────────────────────────────────────────────────────┤
│  ViewModels                                                  │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ PortfolioViewModel (HoldingViewModel, Money)         │   │
│  └──────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────┤
│  Services Layer                                              │
│  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌──────────┐ │
│  │ KiteSvc    │ │ AMFI Svc   │ │ CredVault  │ │ Backdrop │ │
│  │ (HTTP+    │ │ (HTTP+     │ │ (DPAPI /   │ │          │ │
│  │ semaphore)│ │  cache)    │ │  AES-GCM)  │ │          │ │
│  └────────────┘ └────────────┘ └────────────┘ └──────────┘ │
│  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌──────────┐ │
│  │ LoginSvr   │ │ PriceHist  │ │ Theme      │ │ Log      │ │
│  │ (loopback) │ │ (frozen)   │ │ (light/    │ │ (rotating│ │
│  │            │ │            │ │  dark)     │ │  + JSONL)│ │
│  └────────────┘ └────────────┘ └────────────┘ └──────────┘ │
├─────────────────────────────────────────────────────────────┤
│  State Management                                            │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ WidgetState (persisted settings, position, accounts) │   │
│  └──────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────┤
│  Windows Interop                                             │
│  ┌────────────┐  ┌────────────┐  ┌────────────┐             │
│  │ DWM APIs   │  │ DesktopPin │  │ Win32      │             │
│  │ (acrylic)  │  │ (bottom-   │  │ hooks for  │             │
│  │            │  │  most z)   │  │ tray       │             │
│  └────────────┘  └────────────┘  └────────────┘             │
└─────────────────────────────────────────────────────────────┘
                          │
         ┌────────────────┼────────────────┐
         ▼                ▼                ▼
   ┌──────────┐    ┌──────────┐    ┌──────────┐
   │ Kite API │    │ AMFI NAV │    │ Vault    │
   │ (HTTPS)  │    │ (HTTPS)  │    │ (DPAPI / │
   │          │    │          │    │  AES-GCM)│
   └──────────┘    └──────────┘    └──────────┘
```

## Key Components

### Services

- **KiteService**: OAuth + portfolio fetch. Async/await with a
  `SemaphoreSlim` so two refreshes never overlap. The login path uses
  a tolerant-parse for non-2xx bodies so an HTML 502 from a proxy
  surfaces as `Login failed (502)` rather than a JSON `DecodingError`.
- **AmfiNavService**: Fetches live mutual fund NAVs from AMFI to
  override Kite's stale settlement figure. Caches the last good
  response and serves it with a stale warning when AMFI is
  unreachable. The Kite P&L is dropped with the NAV override — a
  P&L computed against a stale NAV cannot annotate a live one.
- **CredentialVault**: DPAPI on Windows, AES-GCM with a per-user
  key file as a portable fallback for non-Windows. Per-account
  scoping so switching accounts does not leak credentials across
  users. Tampered or truncated blobs are rejected rather than
  silently read.
- **BackdropService**: Pure clock logic — picks one of eight
  pre-rendered time-of-day images, or rotates through them.
- **LoginServer**: Loopback `TcpListener` on `127.0.0.1:5173` that
  captures Kite's OAuth redirect. Cancellation token is only passed
  to accept/read, not to writes — finishing a write after
  cancellation is a real bug.
- **PriceHistoryService**: Rolling price series per holding. Frozen
  after `seedIntraday` so an account without historical subscription
  keeps the local series.
- **Theme**: Light/dark/system. The `Dark.xaml` and `Light.xaml`
  resource dictionaries define the same key set — `ThemeTests`
  enforces this.
- **Log**: `os`-style logger writing to a rotating `kiteglance.log`
  and an opt-in `kiteglance.jsonl` with `{Name}`-template rendering
  for structured queries.
- **WidgetManager** (Windows-only equivalent on Mac is the status
  bar item): tray icon with "Show widget", "Refresh", "Pin mode",
  "Background", "Account", "Appearance", "Open at Login" (1.5.0),
  "Settings", "Quit".

### State Management

`WidgetState` persists:
- Window position and size
- Expanded/collapsed state
- Active tab (Stocks/Funds)
- Pin mode preference (Desktop / Always on Top / Float Freely)
- Backdrop selection
- Theme mode (System / Dark / Light)
- Refresh interval (clamped to 1...60 minutes, 0 = off)
- Custom backdrop path
- Account list (per-account id and label, with the vault as the
  source of truth for which ones are actually usable)

### Windows Integration

- **Desktop Pinning**: Bottom-most z-order is achieved through a
  `WM_WINDOWPOSCHANGING` WndProc hook (`DesktopPin.cs` /
  `DesktopPinLogic.cs`). The hook matches against the SC_MINIMIZE,
  SC_MAXIMIZE and `DESKTOP` opcode flags, and only injects a
  HWND_BOTTOM request when the existing z-order is greater than
  bottom. Earlier versions reparented into the WorkerW; the
  bottom-most approach is the 1.5.0 fix and is more robust across
  virtual desktops.
- **DWM Effects**: `WindowMaterial.cs` applies acrylic via
  `DwmSetWindowAttribute` on the main widget and the settings
  window. `UseImmersiveDarkMode` is set for the title bar.
- **Tray Icon**: WinForms `NotifyIcon` for system-tray presence,
  with an owner-drawn `TrayTheme.cs` so the icon and check marks
  match the active theme.
- **Single Instance**: Named `Mutex` (not `Mutex(false, name)`,
  which is a different thing). The owning flag is held so an
  exception in `OnStartup` does not release a mutex the process
  does not own.
- **System events**: `SystemParameters.StaticPropertyChanged` and
  `SystemEvents.UserPreferenceChanged` are subscribed to live-sync
  to the OS flipping to dark, and to the user enabling
  high-contrast mode. Both subscriptions are released on shutdown
  (`MainWindow.ShutdownAll`).
- **High contrast**: `SystemParameters.HighContrast` is watched and
  forces a palette reload.

## Data Flow

1. **Boot**: `App.OnStartup` acquires the named mutex →
   `WidgetState.Load` → services initialise (Credentials loaded
   lazily on first use, not in `Init`) → widget window created →
   status menu built → `MainWindow.Bootstrap` runs.
2. **Refresh**: `MainWindow.RefreshAsync` acquires
   `KiteService._refreshGate` → fetches `/portfolio/holdings` and
   `/mf/holdings` (in that order) → `KiteService` overrides MF
   NAVs from `AmfiNavService` (stale-warning if AMFI lacks the
   ISIN) → `MainWindow.Render` rebuilds the visual tree.
3. **Settings change**: A picker writes through `WidgetState`
   immediately; the visual tree and the OS theme apply without
   a restart. Credentials re-load via `KiteService.ReloadCredentials`.

## Threading Model

- **UI operations**: Dispatcher thread (WPF).
- **API calls**: async/await with `SemaphoreSlim` for serialization.
- **File I/O**: async where possible; critical paths use synchronous
  I/O inside a try/catch with a clear error in the UI.
- **Tray / shell hooks**: STA thread, marshalled back to the
  Dispatcher for any state mutation.

## Error Handling

- **Network failures**: portfolio is held at the last good value
  and the live dot turns amber. The sync label reads
  `stale · Xm ago`.
- **Auth failures** (TokenException, 401 with no error_type): the
  stored token is cleared, the user is prompted to re-auth.
  `PermissionException` is a *subscription* failure, not an auth
  failure, and prompts the wrong thing if conflated.
- **Parse errors**: logged and the affected holding is dropped, not
  the whole portfolio.
- **Unhandled exceptions**: global handlers log to the file and
  prevent crash; the user sees a toast.

## Recent audits

The 1.5.0 release incorporated three rounds of audit (in-house,
in-house re-audit, third-party). The full list of fixes is in
`CHANGELOG.md`. Notable items that affect this document:

- The Pin-to-Desktop implementation changed from WorkerW
  reparenting to bottom-most z-order, which is why
  `DesktopPin.cs` and `DesktopPinLogic.cs` exist in two parts
  (the WndProc hook and the matching rule set).
- The credential vault is now AES-GCM with a per-user key file
  as a portable fallback, in addition to DPAPI.
- Structured logging (`kiteglance.jsonl`) was added; opt-in
  via the `KITEGLANCE_STRUCTURED_LOG=1` env var.
- "Open at Login" is now in the status menu and the Settings
  window.
- `Key.Space` and `Key.Enter` are now only consumed by the
  widget when the focus is not on an editable control.
- `Key.Tab` is now only consumed when the focus is on the tab
  row itself, not on a text field.
- The hourly session-check timer that doubled every API call
  without changing the outcome was removed.

## Multi-account support

A Kite user can have more than one Kite Connect app (e.g. a personal
account and a trading-bot account). KiteGlance supports switching
between them, with each account's credentials kept in its own vault
folder. Mirrors the Mac `docs/MULTI_ACCOUNT.md` -- the two
implementations should produce identical user-visible behaviour.

### The three rules

1. **A listed account is a label, not a credential.** A user who
   has signed in once and then deleted the DPAPI entry for that
   account (or never had it -- e.g. by editing the JSON by hand)
   should not see the account as "available" in the status menu.
   The vault probe is the only source of truth.
2. **Switching accounts clears in-memory credentials, then
   reloads.** `KiteService.UseAccount(...)` rebinds a new
   `CredentialVault` (one per account) and zeroes the in-memory
   `AccessToken` *before* reloading.
3. **Per-account vault items use a stable suffix.** Each account
   has its own folder at
   `%APPDATA%\KiteGlance\accounts\<accountId>\`, with the same
   `vault.bin` / `token.bin` files. There is no `pending` folder
   staging dance -- the folder is the per-account namespace.

### Why the probe is a parameter, not a stored property

`WidgetState` is the persisted state, and a closure is neither
encodable nor cross-actor-sendable. The vault probe is passed
into the resolver at the call site, which is what makes the
account-resolution logic unit-testable in
`WidgetStateTests.cs`.

### What this protects against

The classic regression in this area is the "labelled without
credentials" account: a user has signed in, the menu shows their
account, then the vault entry is removed. Without the probe check,
the menu still shows the account, the app builds a login URL with
`api_key=` empty, and Kite returns an error page -- with the
account still listed, looking signed in. The probe check collapses
this to "no usable account, show the connect overlay".
