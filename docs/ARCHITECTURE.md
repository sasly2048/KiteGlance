# Architecture Overview

## System Design

Kite Glance follows a lightweight, single-process architecture optimized for Windows desktop integration.

```
┌─────────────────────────────────────────────────────────────┐
│                      Kite Glance Widget                      │
├─────────────────────────────────────────────────────────────┤
│  UI Layer (WPF)                                              │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐       │
│  │ MainWindow   │  │ SettingsWin  │  │ Tray Icon    │       │
│  └──────────────┘  └──────────────┘  └──────────────┘       │
├─────────────────────────────────────────────────────────────┤
│  ViewModels                                                  │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ PortfolioViewModel                                   │   │
│  └──────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────┤
│  Services Layer                                              │
│  ┌────────────┐ ┌────────────┐ ┌────────────┐ ┌──────────┐ │
│  │ KiteSvc    │ │ AMFI Svc   │ │ Cred Vault │ │ Backdrop │ │
│  └────────────┘ └────────────┘ └────────────┘ └──────────┘ │
├─────────────────────────────────────────────────────────────┤
│  State Management                                            │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ WidgetState (persisted settings & position)          │   │
│  └──────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────┤
│  Windows Interop                                             │
│  ┌────────────┐  ┌────────────┐                             │
│  │ DWM APIs   │  │ Win32 Hooks│                             │
│  └────────────┘  └────────────┘                             │
└─────────────────────────────────────────────────────────────┘
                          │
         ┌────────────────┼────────────────┐
         ▼                ▼                ▼
   ┌──────────┐    ┌──────────┐    ┌──────────┐
   │ Kite API │    │ AMFI NAV │    │ DPAPI    │
   │ (HTTPS)  │    │ (HTTPS)  │    │ Storage  │
   └──────────┘    └──────────┘    └──────────┘
```

## Key Components

### Services

- **KiteService**: Handles OAuth authentication and portfolio data fetching from Kite Connect API
- **AmfiNavService**: Fetches live mutual fund NAVs from AMFI to override stale Kite data
- **CredentialVault**: Encrypts/decrypts credentials using Windows DPAPI
- **BackdropService**: Manages time-based backdrop selection and transitions
- **Log**: Minimal file-based logger with rotation

### State Management

WidgetState persists:
- Window position and size
- Expanded/collapsed state
- Active tab (Stocks/Funds)
- Pin mode preference
- Backdrop selection
- Last sync timestamp

### Windows Integration

- **Desktop Pinning**: Uses `WM_WINDOWPOSCHANGING` hook to maintain bottom-most z-order
- **DWM Effects**: Applies acrylic/dark mode via `DwmSetWindowAttribute`
- **Tray Icon**: WinForms NotifyIcon for system tray presence
- **Single Instance**: Named mutex prevents multiple instances

## Data Flow

1. **Boot**: Load persisted state → Initialize services → Start hourly timer
2. **Refresh**: Acquire semaphore → Fetch from Kite API → Override MF NAVs from AMFI → Update ViewModel → Release semaphore
3. **Settings Change**: Validate → Persist to state → Apply immediately

## Threading Model

- UI operations: Dispatcher thread (WPF)
- API calls: Async/await with SemaphoreSlim for serialization
- File I/O: Async where possible, synchronous with timeout for critical paths

## Error Handling

- Network failures: Graceful degradation with cached data
- Auth failures: Clear token, prompt re-authentication
- Parse errors: Log and skip affected holdings
- Unhandled exceptions: Global handlers log to file, prevent crash
