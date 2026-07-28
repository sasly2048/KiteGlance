# Changelog

All notable changes to Kite Glance will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

## [1.0.0] - 2025-07-28

### Added
- Initial release
- Live portfolio P&L tracking (stocks and mutual funds)
- AMFI NAV integration for accurate mutual fund valuations
- Desktop widget with bottom-most pinning
- Eight time-based mesh gradient backdrops
- Custom backdrop image support
- DWM dark mode and rounded corners
- Secure credential storage with Windows DPAPI
- OAuth authentication via loopback server
- System tray integration
- Keyboard shortcuts for common actions
- Persistent state (position, settings, preferences)
- ARM64 and x64 native builds
- Self-contained single-file executable
- Minimal rotating file logger
- Unit tests for P&L math (regression tests for known bugs)

### Security
- Credentials encrypted at rest with per-user DPAPI
- App-specific entropy for additional protection
- Environment variable override for development
- No sensitive data logged
- HTTPS-only API communication

[Unreleased]: https://github.com/sasly2048/KiteGlance/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/sasly2048/KiteGlance/releases/tag/v1.0.0
