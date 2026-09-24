# Security Policy

## Reporting a vulnerability

If you find a security issue in Kite Glance, please **do not open a public
issue**. Instead:

1. Go to the repository's **Security** tab on GitHub.
2. Click **Report a vulnerability** to open a private advisory.

You should get an initial response within a few days. Please include:

- A description of the issue and its potential impact
- Steps to reproduce (a minimal repro helps a lot)
- The version / commit you tested against

## Supported versions

Only the latest released version is supported with security fixes. Please
update before reporting. The CHANGELOG has the chronological list of
every fix; security-relevant entries are listed under the
"Security-relevant changes" section of the latest release.

## What this app does with your data

Understanding the data flow is the fastest way to reason about risk here:

- **Credentials** (Kite Connect API key and secret) are encrypted at rest
  with **Windows DPAPI**, scoped to your Windows user account
  (`ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`). They are
  stored in `%APPDATA%\KiteGlance\vault.bin` and cannot be decrypted by
  another user account on the same machine, or by copying the file to a
  different machine.
- **AES-GCM portable fallback** is used when DPAPI is not available
  (e.g. on the Linux CI test runner). A per-user key file at
  `%APPDATA%\KiteGlance\key.bin` derives the AES key; the same vault
  blob is then encrypted with AES-256-GCM. The Linux path is a
  test-only path; the production Windows binary does not exercise it.
- **Tampered or truncated blobs are rejected**, not silently read.
  `CredentialVault.Load` verifies the GCM tag and the size before
  attempting any decryption. A blob that has been edited by hand
  surfaces as "vault unreadable" in the log, not as a phantom
  credential.
- **Per-account scoping**. Each account has its own vault under
  `%APPDATA%\KiteGlance\accounts\<accountId>\`, keyed by the Kite user
  id. Switching accounts (`WidgetState.ActiveAccountId`) rebinds the
  vault; the previous account's credentials stay on disk under their
  own folder and cannot be read by the new account.
- **Access tokens** (which Kite Connect rotates daily) are stored the
  same way, with a separate `token.bin` so the secret can be
  replaced without touching the token and vice versa. The Settings
  UI deliberately does not echo the secret back when opened; an
  empty Save means "keep the stored secret" (the
  `SecretMerge.Resolve` rule).
- **Network access** is limited to `api.kite.trade` (portfolio data) and
  the Kite login flow in your default browser. The app makes no other
  outbound calls, and there is no analytics or telemetry.
- **The OAuth redirect** is captured by a loopback `TcpListener` bound
  to `127.0.0.1:5173`. It only accepts connections from `localhost`
  and shuts down immediately after capturing the request token. The
  cancellation token is only passed to accept/read, not to writes --
  finishing a write after cancellation is a real bug.
- **Structured logging** (`kiteglance.jsonl`) is opt-in via
  `KITEGLANCE_STRUCTURED_LOG=1`. The default install does not
  produce the JSONL file. When enabled, it does not contain tokens
  or P&L values, only the message template and structured
  properties.
- **Nothing is ever sent to a third-party server**. There is no
  backend for this project -- it talks directly to Kite Connect's
  API from your machine.

## Reporting credential exposure

If you believe your Kite API key or access token has been exposed:

1. Regenerate your API secret at
   [developers.kite.trade](https://developers.kite.trade).
2. Revoke the affected access token from your
   [Kite account settings](https://kite.zerodha.com).
3. Clear the local vault: delete `%APPDATA%\KiteGlance\vault.bin`,
   `%APPDATA%\KiteGlance\key.bin`, `%APPDATA%\KiteGlance\token.bin`,
   and the entire `%APPDATA%\KiteGlance\accounts\` directory, then
   re-enter fresh credentials.

## Threat model

This is a single-user, single-machine desktop app. The threat model
is:

- **In scope**: another local user on the same machine reading your
  vault; a copy of the vault file moving to a different Windows
  user account on the same machine; a copy of the vault file
  moving to a different machine entirely; a malicious browser
  redirect to `127.0.0.1:5173` while the login flow is open; a
  tampered vault blob.
- **Out of scope**: a compromised machine at the kernel level
  (DPAPI is the OS-level boundary); a keylogger; a phishing
  attempt against the user; a malicious Kite Connect app that
  legitimately holds the user's API credentials.

## Scope

This policy covers the Kite Glance application code in this
repository. It does not cover Zerodha's Kite Connect API or
infrastructure -- for issues there, contact Zerodha directly.
