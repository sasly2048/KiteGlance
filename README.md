<h1 align="center"> Kite Glance </h1>
<p align="center">
  <picture>
    <source
      media="(prefers-color-scheme: dark)"
      srcset="https://github.com/user-attachments/assets/b01833fd-a224-4864-9d54-f6fc5ace0c8c">
    <source
      media="(prefers-color-scheme: light)"
      srcset="https://github.com/user-attachments/assets/d95edb28-73cd-473a-89fc-7034bdfe562c">
    <img
      src="https://github.com/user-attachments/assets/b01833fd-a224-4864-9d54-f6fc5ace0c8c"
      alt="Kite Glance banner"
      >
  </picture>
</p>

<p align="center">
  <strong>A native Windows desktop widget for your Zerodha portfolio.</strong><br>
  Live P&amp;L, per-holding breakdown, and day change — glued to your desktop the way a widget should be.
</p>

<p align="center">
  Built with <strong>WPF</strong> on <strong>.NET 8</strong>, self-contained (no runtime to install), dependency-light, and optimized for <strong>Windows 11</strong>, <strong>ARM64 (Snapdragon X Elite)</strong>, and <strong>x64</strong>.
</p>

<div align="center">
   
![Build](https://github.com/sasly2048/KiteGlance/actions/workflows/build.yml/badge.svg)
![Last Commit](https://img.shields.io/github/last-commit/sasly2048/KiteGlance)
![License](https://img.shields.io/badge/License-MIT-informational.svg)
![Made with Love](https://img.shields.io/badge/Made%20with-%E2%9D%A4-red)
![Platform](https://img.shields.io/badge/Platform-Windows%2011-0078D4?logo=windows11)
![Release](https://img.shields.io/github/v/release/sasly2048/KiteGlance)
![Runtime](https://img.shields.io/badge/Runtime-.NET%208-blueviolet)
</div>
<div align="center">

  [![Ko-fi](https://img.shields.io/badge/Support-Ko--fi-FF5E5B?logo=ko-fi&logoColor=white)](https://ko-fi.com/sasly204800)

</div>

> **Not affiliated with Zerodha.** This is an independent, open-source client for the public Kite Connect API. See the [Disclaimer](#disclaimer).

<img width="2832" height="1600" alt="Kite Glance desktop widget showing portfolio P&L" src="https://github.com/user-attachments/assets/a90ce370-9ccb-4a19-b3f0-9e495e841eec" />

---

## Overview

Kite Glance sits on your desktop and keeps a quiet, always-current view of your holdings. It reads your portfolio through the official Kite Connect API, encrypts your credentials locally with Windows DPAPI, and talks to nothing else — there is no backend, no telemetry, and no third-party server in the loop.

It was built to feel like a first-party part of Windows rather than a browser tab: real DWM material, spring-based motion, a rendered backdrop, and state that persists across restarts.

## Key Features

- **Live portfolio P&L** — overall and per-holding, split into Stocks and Funds tabs.
- **Accurate mutual-fund NAVs** — Kite's holdings endpoint returns a stale settlement NAV for funds; Kite Glance overrides it with the official live NAV from [AMFI](https://www.amfiindia.com), so the numbers match what Coin shows.
- **Pin to desktop** — bottom-most z-order enforcement keeps the widget beneath every application while remaining in DWM's normal composition path for full hardware acceleration. The widget automatically restores itself when Show Desktop (Win+D, taskbar button, or touchpad gestures) issues a minimize command, ensuring desktop-pinned widgets never disappear.
- **Backgrounds** — eight pre-rendered mesh-gradient backdrops spanning the full day (dawn, sunrise, day, noon, dusk, evening, night, and midnight). They automatically follow the time of day, can rotate through the full cycle, or stay fixed on a chosen backdrop. A custom image is also supported, and all transitions crossfade smoothly.
- **Native material** — DWM corners, dark frame, and shadow via `DwmSetWindowAttribute`.
- **Considered motion** — a spring easing system for layout, a separate quartic ease for numbers (money never overshoots), a skeleton loading state, and a "live" indicator that only pulses while the market is open.
- **Honest about staleness** — if a sync fails or live NAVs are unavailable, the widget says so rather than showing numbers that quietly disagree.
- **Secure by construction** — credentials encrypted at rest with Windows DPAPI (per-user scope, app-specific entropy); OAuth captured on a loopback socket with no admin rights.
- **Sparklines** — each holding shows where its price has been. With a Kite historical-data subscription the line is drawn from real daily candles; without one it fills in from prices seen on each refresh, so it works on any account and covers mutual funds, which the historical API does not.
- **Multiple accounts** — each Zerodha login gets its own encrypted vault and its own price history; switch between them from the tray menu.
- **Light and dark** — follows the Windows app theme by default, or pin it to either from Settings. Switching repaints live, without a restart.
- **Keyboard-friendly** — expand/collapse, refresh, and tab-switch all have shortcuts; arrow keys move between holdings and Enter copies one; focus rings appear for keyboard users.
- **Screen-reader support** — the hero figure, each holding row, and the market-open indicator are all announced as sentences rather than loose numbers. High-contrast mode drops the decorative layers that would otherwise fight it.
- **Persistent** — remembers position, expanded/collapsed state, active tab, pin mode, backdrop, theme, refresh cadence, and which account you were looking at.

## Technology Stack

| Layer                | Choice                                                                                                                                        |
| -------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| UI framework         | WPF (`net8.0-windows`)                                                                                                                        |
| Language             | C# 12                                                                                                                                         |
| Rendering / material | DWM interop (`DwmSetWindowAttribute`), pre-rendered mesh-gradient PNG backdrop                                                                |
| Tray + desktop glue  | Win32 / WinForms `NotifyIcon`; bottom-most z-order via a `WM_WINDOWPOSCHANGING` hook (legacy WorkerW reparenting available behind an env var) |
| Credential storage   | Windows DPAPI (`System.Security.Cryptography.ProtectedData`); AES-GCM on non-Windows                                                          |
| Market data          | Kite Connect v3 REST API, AMFI NAVAll.txt                                                                                                     |
| Auth                 | Kite Connect OAuth via loopback `TcpListener`                                                                                                 |
| Packaging            | `dotnet publish` single-file self-contained; Inno Setup installer                                                                             |
| Testing              | xUnit (pure `net8.0`, no WPF dependency)                                                                                                      |
| Logging              | Minimal built-in rotating file logger (no external framework)                                                                                 |
| CI                   | GitHub Actions (ARM64 + x64 matrix)                                                                                                           |

There are **no external UI or HTTP libraries** — only the .NET base class library.

## Requirements

- **Windows 11** (22H2 or newer recommended — see below)
- **ARM64 or x64** CPU
- A **Zerodha account** with **Kite Connect API access** ([developers.kite.trade](https://developers.kite.trade))

The acrylic backdrop uses Windows 11 22H2+ APIs. On older builds the widget falls back to a solid dark surface automatically — this is intentional, not a bug.

## Installation

### Option A — download a pre-built release

1. Go to the [Releases](https://github.com/sasly2048/kite-glance/releases) page.
2. Download the `KiteGlance.exe` that matches **your CPU** — see the
   table below. Both files are self-contained (no .NET install needed) and
   start with a double-click.
3. Run it. On first launch it will ask for your Kite Connect API credentials
   (see [Configuration](#configuration)).

To install it properly (Start Menu shortcut, autostart, Add/Remove Programs
entry), run the installer script from a checkout, or use the Inno Setup
`Setup.exe` if one is attached to the release.

> **Which file do I download?**
>
> | Your CPU                                  | Download                                  |
> | ----------------------------------------- | ----------------------------------------- |
> | Snapdragon X Elite / any Windows-on-ARM   | `KiteGlance-win-arm64.exe`                |
> | Intel / AMD 64-bit (every "normal" PC)    | `KiteGlance-win-x64.exe`                  |
> | 32-bit Windows (x86)                      | **Not supported** — see [Troubleshooting](#the-latest-release-isnt-working-on-other-pcs) |

### Option B — build from source

See [Building from Source](#building-from-source).

## Building from Source

**Prerequisites:**

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows (to build and run the app)
- [Python 3](https://www.python.org/downloads/) (only if you intend to run the pre-flight validator before contributing — see [CONTRIBUTING](CONTRIBUTING.md))

```powershell
git clone https://github.com/sasly2048/kite-glance.git
cd kite-glance

# Run and iterate
cd src/KiteGlance
dotnet run
```

To produce a distributable single-file executable:

```powershell
# From the repo root
.\scripts\build.ps1              # ARM64 by default
.\scripts\build.ps1 -Arch x64    # or x64
```

Output lands in `src/KiteGlance/dist/KiteGlance.exe` — one self-contained file, no runtime required on the target machine.

To install it locally (per-user, with Start Menu + Desktop shortcuts and autostart):

```powershell
.\scripts\install.ps1
.\scripts\install.ps1 -Uninstall   # to remove
```

Before opening a pull request, run the pre-flight validator — it catches XAML/resource errors that `dotnet build` cannot (see [CONTRIBUTING](CONTRIBUTING.md)):

```powershell
python scripts/preflight.py
```

And run the unit tests, which cover the P&L arithmetic:

```powershell
dotnet test tests/KiteGlance.Tests
```

The test project is plain `net8.0` (no WPF), so it also runs on Linux/macOS and in CI.

## Configuration

You need a Kite Connect app to get an API key and secret:

1. Go to [developers.kite.trade](https://developers.kite.trade) and create an app.
2. Set the app's **Redirect URL** to **exactly**:
   ```
   http://127.0.0.1:5173/callback
   ```
   > **Keep port 5173 free during login.** Kite Glance briefly listens on `127.0.0.1:5173` to catch the OAuth redirect. Port 5173 is also the default for common dev servers (Vite, for one) — if something is already bound to it when you sign in, the login will fail to complete. Stop any such server for the few seconds the browser round-trip takes.
3. Note your **API key** and **API secret**.

Provide them to Kite Glance in **either** of two ways:

- **In-app (simplest):** launch the widget and enter them in the Settings dialog. They are encrypted with DPAPI and stored under `%APPDATA%\KiteGlance\vault.bin`.
- **Environment variables:** useful when running from source. See below.

Kite access tokens expire once daily (around 7:30 AM IST, per Kite's rules), so you'll sign in through your browser once each day.

### Environment Variables

| Variable               | Purpose                                                                                                                                                                                                        |
| ---------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `KITE_API_KEY`         | Your Kite Connect API key                                                                                                                                                                                      |
| `KITE_API_SECRET`      | Your Kite Connect API secret                                                                                                                                                                                   |
| `KITEGLANCE_DEBUG`     | Set to `1` to dump raw API responses to `%APPDATA%\KiteGlance\api-dump.json` and raise log verbosity to Debug. **The dump contains your holdings in plaintext**; it is auto-deleted on the next normal launch. |
| `KITEGLANCE_PUBLISHER` | Optional. Publisher name shown in Add/Remove Programs when using `install.ps1`.                                                                                                                                |

If both `KITE_API_KEY` and `KITE_API_SECRET` are set, they take priority over the stored vault — handy for development.

> **Note on `.env`:** the app reads real process environment variables; it does **not** parse a `.env` file at runtime. The included [`.env.example`](.env.example) is a template for your own reference — copy it to `.env`, fill in your values, and load it into your shell before launching. For example, in PowerShell:
>
> ```powershell
> # From the repo root, load your .env into the current shell
> Get-Content .env | Where-Object { $_ -match '=' } | ForEach-Object {
>     $name, $value = $_ -split '=', 2
>     Set-Item "env:$($name.Trim())" $value.Trim()
> }
> ```
>
> `.env` is git-ignored and will never be committed. Because the values must be present in the environment of whatever shell launches the app, set them **before** `dotnet run` — and note that `dotnet run` executes from `src/KiteGlance/`, so a `.env` sitting at the repo root is not read automatically.

## Usage

Launch the widget; it lives on your desktop and in the system tray.

- **Click the header** or press **Space / Enter** to expand the holdings list.
- **Tab** moves focus. With focus on the Stocks or Funds tab it switches between them; everywhere else it traverses normally, which is what makes the focus rings reachable.
- **R** refreshes now (throttled to once a minute).
- **Esc** collapses the list.
- **Click a holding row** to copy its ticker; hover for exact quantity and average price. By keyboard, arrow keys move between rows and **Enter** copies.
- **Right-click the tray icon** to switch pin modes, switch or add an account, toggle autostart, refresh, or quit.
- **The widget's menu button** covers pin modes, background choice, settings, refresh, hide, and quit. Background selection lives here rather than in the tray menu; account switching is the reverse.
- **Settings** holds your API credentials, the auto-refresh cadence (every 1–60 minutes, or off for manual-only), and the appearance mode. Refresh only runs while the market is open, so a short interval costs nothing overnight. Both take effect immediately — neither needs a restart.

The widget refreshes automatically during market hours (Mon–Fri, 09:15–15:30 IST).

### Pin modes

- **Pin to desktop** (default) — bottom-most z-order beneath every application, hidden from Alt+Tab and Task View. If Show Desktop (Win+D, taskbar button, or four-finger touchpad gesture) minimizes the window, Kite Glance immediately restores itself so the desktop widget remains visible.
- **Always on top** — floats above every window; useful while actively trading.
- **Float freely** — an ordinary window.

### Backgrounds

The menu's **Background** submenu offers:

- **Time of day (default)** — automatically transitions through eight phases: Sunrise, Morning, Late Morning, Noon, Afternoon, Sunset, Evening, and Midnight, following the system clock.
- **Rotate** — cycles through all eight backdrops automatically.
- **Graphite** — locks the widget to the neutral noon backdrop (or whichever backdrop is designated as Graphite).
- **Choose image…** — pick any picture; it's copied locally, decoded at the widget's size, and given a readability scrim so the numbers stay legible over anything.

Switching backgrounds crossfades rather than cutting.

## Project Architecture

```
src/KiteGlance/
├── KiteGlance.csproj           Project file: net8.0-windows, WPF + WinForms
├── App.xaml(.cs)              Design system, styles, resources; single-instance
│                              guard and global crash logging
├── MainWindow.xaml(.cs)       The widget: layout, motion, backdrop, state orchestration
├── SettingsWindow.xaml(.cs)   Credential entry, refresh cadence, appearance
├── WidgetManager.cs           Tray icon, pin-mode menu, accounts, autostart
├── TrayTheme.cs               Owner-drawn tray menu (light and dark)
├── Themes/
│   ├── Dark.xaml              Dark palette
│   └── Light.xaml             Light palette (same keys, swapped at runtime)
├── Assets/
│   ├── app.ico                 Application + tray icon
│   ├── backdrop-dawn.png
│   ├── backdrop-sunrise.png
│   ├── backdrop-day.png
│   ├── backdrop-noon.png
│   ├── backdrop-dusk.png
│   ├── backdrop-evening.png
│   ├── backdrop-night.png
│   ├── backdrop-midnight.png
│   └── grain.png               Dither/grain overlay tile
├── Motion/
│   ├── SpringEase.cs          Damped-harmonic-oscillator easing for layout
│   └── Numeral.cs             Unified number-tweening (quartic ease, no overshoot)
├── Interop/
│   ├── WindowMaterial.cs      DWM corners / shadow
│   └── DesktopPin.cs          Bottom-most desktop pinning (see Pin modes, below)
├── Services/
│   ├── KiteService.cs         Kite Connect v3 client + portfolio assembly
│   ├── PnlMath.cs             Pure P&L arithmetic (unit-tested in isolation)
│   ├── BackdropService.cs     Pure backdrop-selection logic (time-of-day / rotation)
│   ├── AmfiNavService.cs      Live mutual-fund NAVs from AMFI, cached to disk daily
│   ├── PriceHistoryService.cs Rolling per-symbol price series behind the sparklines
│   ├── CredentialVault.cs     Encrypted credential + token storage, per account
│   ├── LoginServer.cs         Loopback OAuth redirect capture (port 5173)
│   ├── Theme.cs               Runtime palette swap (System / Dark / Light)
│   └── Log.cs                 Dependency-free structured logger (text + JSON lines)
├── State/WidgetState.cs       Persisted position / tab / pin / backdrop / theme /
│                              accounts / refresh interval (JSON)
└── ViewModels/PortfolioViewModel.cs

tests/KiteGlance.Tests/         142 tests, plain net8.0, no WPF
├── KiteGlance.Tests.csproj    xUnit project; links the files under test
├── PnlMathTests.cs            P&L regression tests vs. real Coin figures
├── BackdropServiceTests.cs    Time-of-day / rotation boundary tests
├── KiteServiceTests.cs        Portfolio assembly, day-change, stale pricing
├── AmfiNavServiceTests.cs     NAV parsing, semicolon-in-name rows, staleness
├── CredentialVaultTests.cs    AES-GCM round-trip, tamper detection, accounts
├── PriceHistoryServiceTests.cs Series cap, eviction, corrupt-file recovery, frozen/intraday
├── WidgetStateTests.cs        Atomic save, off-screen clamping, account model
├── ThemeTests.cs              Dark and Light define identical keys
├── LogTests.cs                Message templates, property capture, redaction
└── DesktopPinLogicTests.cs    Pin-to-desktop WndProc rules (SC_MINIMIZE / SC_DESKTOP / icon rect)

scripts/
├── build.ps1                  Single-file self-contained publish
├── install.ps1                Per-user install / uninstall
├── setup.iss                  Inno Setup installer definition
└── preflight.py               Static checks CI and contributors run

.github/workflows/
├── build.yml                  Pre-flight, tests, and matrix build on every push/PR
└── release.yml                Publish ARM64 + x64 binaries on a v* tag push
```

**Data flow:** `KiteService` fetches `/portfolio/holdings` and `/mf/holdings`, overlays live NAVs from `AmfiNavService`, and produces a `PortfolioData` the window renders. All P&L flows through `PnlMath` — the single, unit-tested implementation — so a zero from the API is never treated as a real zero, and current value can never contradict P&L.

**Local files** (all under `%APPDATA%\KiteGlance\`): `vault.bin` (encrypted credentials), `token.bin` (encrypted access token), `state.json` (window position, active tab, pin mode, backdrop, theme, refresh interval, known accounts — plain JSON, no secrets), `history.json` (recent prices per holding, for the sparklines), `amfi-nav.txt` (cached daily NAVs), `custom-backdrop.*` (a user-chosen background image, if set), `logs/kiteglance.log` and `logs/kiteglance.jsonl` (rotating logs), and `api-dump.json` (only when `KITEGLANCE_DEBUG=1`, auto-deleted otherwise). With more than one account configured, the per-account files live under `accounts\<user-id>\`.

## Continuous Integration

Two GitHub Actions workflows run this project:

- **`build.yml`** — on every push and pull request to `main` (touching `src/`, `tests/`, or `scripts/`). It runs pre-flight, then the unit tests on Linux, then a Debug build and a self-contained Release publish for **both** `win-arm64` and `win-x64`, uploading each `KiteGlance.exe` as a build artifact. This is the badge at the top of this README.
- **`release.yml`** — on pushing a version tag (`v*`, e.g. `v1.0.0`). It re-runs pre-flight, publishes both architectures, and attaches the two executables to an automatically-created [GitHub Release](https://github.com/sasly2048/KiteGlance/releases) with generated notes.

To cut a release:

```bash
git tag v1.0.0
git push origin v1.0.0
```

> **Note:** CI produces the raw self-contained `KiteGlance.exe` for each architecture — the recommended distribution format (no runtime needed, nothing to install). The Inno Setup installer (`scripts/setup.iss`) is provided for building a traditional `Setup.exe` locally if you prefer a Start-Menu/Add-Remove-Programs experience; it is **not** run by CI, since the raw exe already covers both architectures cleanly. Build it locally with the [Inno Setup compiler](https://jrsoftware.org/isinfo.php) if you want a packaged installer.

## Roadmap

### Completed
- [x] ~~Auto-refresh portfolio data~~ (Implemented)
- [x] ~~Configurable refresh interval~~ (Settings → Auto-refresh: 1–60 minutes, or off)
- [x] ~~Session expiry handling~~ (Auto-detection every 1 hour)
- [x] ~~Unit test coverage expansion~~ (142 tests across 10 files)
- [x] ~~Documentation improvements~~ (Security model, troubleshooting guides)
- [x] ~~CI/CD enhancements~~ (Code coverage, cross-platform fixes)
- [x] ~~Multi-account support~~ (Per-account vaults; switch from the tray menu)
- [x] ~~Sparkline charts for holdings~~ (Kite historical API when subscribed, locally
      accumulated price history otherwise, so it works without the paid add-on)
- [x] ~~Advanced accessibility features~~ (Screen-reader names throughout, text
      equivalents for the market-open pulse, keyboard-reachable rows, high-contrast mode)
- [x] ~~Light theme variant~~ (Settings → Appearance: Match Windows / Dark / Light)
- [x] ~~Lazy loading for large portfolios~~ (Virtualized holdings list)
- [x] ~~Structured logging~~ (Message templates and JSON-lines output, without
      taking on Serilog — the no-dependencies rule stands)
- [x] ~~Pin to desktop survives Win+D and four-finger swipe-down~~ (Implemented
      2026-08-29 — the WndProc hook now catches every minimise code path:
      `WM_SYSCOMMAND` with `SC_MINIMIZE` / `SC_MAXIMIZE` / the undocumented
      `SC_DESKTOP` (0xF130), `WM_SIZE` with `SIZE_MINIMIZED`, the icon-rect
      detection in `WM_WINDOWPOSCHANGING`, and belt-and-braces
      `WM_SHELLHOOK` / `WM_ACTIVATEAPP` / `WM_WININICHANGE` re-pins. The
      widget now stays on the desktop instead of disappearing.)

### Future Considerations
- [ ] Light-mode backdrop art (light mode currently tints the dark-tuned images
      rather than shipping a second set)
- [x] ~~Intraday sparkline resolution for users with a historical-data subscription~~
      (Implemented — Kite's `/instruments/historical/{token}/minute?interval=5`
      endpoint is fetched on first backfill and down-sampled to the sparkline
      width; the resulting series is frozen so per-refresh `last_price` doesn't
      put a single non-aligned point on the right edge. Users on a tier that
      doesn't include minute data fall through to the daily endpoint and then
      to the local accumulator, as before.)

Suggestions and contributions are welcome — see [CONTRIBUTING](CONTRIBUTING.md).

## Troubleshooting

### "The latest release isn't working on other PCs"

The single-file exe Windows refuses to run is almost always an
architecture mismatch. Kite Glance is built per-CPU — there are two
distinct binaries, and one will not run on the other's hardware.

- On **Snapdragon X Elite** and other Windows-on-ARM machines: download
  `KiteGlance-win-arm64.exe`.
- On **Intel / AMD** PCs (which includes virtually every "normal" desktop
  and laptop in 2026): download `KiteGlance-win-x64.exe`.
- On **32-bit Windows (x86)**: there is no x86 build and there cannot be,
  because WPF's 32-bit story on Windows 11 is gone. Use a 64-bit
  machine, or open an issue describing the actual hardware constraint.

How to tell which one Windows tried to run: right-click the file in
Explorer → **Properties** → **Compatibility**. If the section reads
"This app can't run on your PC", you have the wrong architecture.
Re-download the one your machine needs.

A release with a single, unnamed `KiteGlance.exe` and no `-win-x64` /
`-win-arm64` suffix is a packaging bug — the binary in that file is one
architecture or the other, but the user has no way to tell which from
the filename alone. The release workflow (`release.yml`) is configured
to attach two separate files; if a tagged release ends up with only one,
that's the workflow being bypassed (most often by a manual upload
through the GitHub web UI) and should be re-cut from a clean tag.

### "I downloaded the right exe but double-clicking does nothing"

Open `%APPDATA%\KiteGlance\logs\kiteglance.log` in any text editor.
Three global exception handlers (`DispatcherUnhandledException`,
`AppDomain.UnhandledException`,
`TaskScheduler.UnobservedTaskException` in `App.xaml.cs`) write every
unhandled error to that file before the process exits, so a
silent failure on a user's machine is never actually silent. The most
common entries:

- `Startup failed; releasing mutex and exiting` — `new MainWindow()` or
  `new WidgetManager(...)` threw. The follow-on lines name the type
  (typically `Win32Exception` from DWM or a XAML resource lookup
  failure). Send the file to the issue tracker.
- `Unhandled domain exception` / `Unobserved task exception` — a
  background refresh path crashed. The widget marks itself stale and
  keeps the last-known figures on screen; it does not crash the UI.
- No log file at all — Windows refused to load the PE at all. That is
  the architecture mismatch from the section above; the process never
  reaches `OnStartup` to write a log.

### "Install.ps1 says ARCHITECTURE MISMATCH"

The repo's `dist\` contains only one exe and it's the wrong one for
your CPU. Re-run the build:

```powershell
.\scripts\build.ps1           # builds both arm64 and x64
.\scripts\install.ps1         # installs the right one for this machine
```

### 32-bit Windows is unsupported

.NET 8 itself runs on x86, but this project is `net8.0-windows` with
WPF + WinForms, and there is no WPF runtime for x86 on Windows 11.
There is no build target for `win-x86` and adding one is not planned.

---

## Security Considerations

- **Credentials never leave your machine.** API key, secret, and access token are encrypted at rest with Windows DPAPI (per-user scope + app-specific entropy) under `%APPDATA%\KiteGlance`. On non-Windows builds the fallback is AES-GCM, which is authenticated: tampered ciphertext is rejected rather than decrypted into garbage that would then be sent to Kite. Each account gets its own vault under `accounts\<user-id>\`.
- **No backend, no telemetry.** The app talks only to `api.kite.trade` and `amfiindia.com`. There is no analytics.
- **OAuth is loopback-only.** The redirect is captured by a `TcpListener` bound to `127.0.0.1:5173`; no admin rights or URL reservations are required, and the listener closes immediately after capture.
- **Nothing sensitive is committed.** No credentials appear anywhere in this repository; `.env` and `*.bin` are git-ignored.

For the full policy and how to report a vulnerability, see [SECURITY.md](SECURITY.md).

## Contributing

Pull requests are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) first — it covers the dev setup, the ASCII-only source rule, the WinForms/WPF aliasing gotcha, and the pre-flight check to run before submitting.

## License

Released under the [MIT License](LICENSE).

## Disclaimer

Kite Glance is an independent, community-built tool. It is **not affiliated with, endorsed by, or supported by Zerodha or AMFI.** "Zerodha", "Kite", and "Coin" are trademarks of their respective owners.

This software is provided "as is", without warranty of any kind. It is a read-only viewer for your own portfolio and places no trades. Market data may be delayed or inaccurate; **do not rely on it for trading decisions.** Always verify figures against the official Kite and Coin apps. You are responsible for complying with Zerodha's API terms of use.
