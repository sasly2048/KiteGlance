# Contributing to Kite Glance

Thank you for your interest in contributing! This document provides guidelines and setup instructions.

## Development Setup

### Prerequisites

- **.NET 8 SDK** (latest patch version)
- **Windows 11** (22H2 or newer recommended)
- **Visual Studio 2022** or **VS Code** with C# extension
- **Git**

### Clone and Build

```bash
git clone https://github.com/sasly2048/KiteGlance.git
cd KiteGlance

# Restore dependencies
dotnet restore

# Run tests
dotnet test tests/KiteGlance.Tests/KiteGlance.Tests.csproj

# Build the application
dotnet build src/KiteGlance/KiteGlance.csproj

# Run (Windows only)
dotnet run --project src/KiteGlance/KiteGlance.csproj
```

### Environment Variables (Optional)

Create a `.env` file or set environment variables for local development:

```bash
KITE_API_KEY=your_api_key
KITE_API_SECRET=your_api_secret
KITEGLANCE_DEBUG=1  # Enable debug logging
```

See `.env.example` for reference.

## Project Structure

```
KiteGlance/
├── src/KiteGlance/          # Main WPF application
│   ├── Services/            # Business logic (KiteService, AmfiNavService, etc.)
│   ├── ViewModels/          # MVVM view models
│   ├── Interop/             # Windows API interop
│   ├── Motion/              # Animation helpers
│   └── State/               # Application state management
├── tests/KiteGlance.Tests/  # Unit tests (cross-platform)
├── scripts/                 # Build and installer scripts
└── .github/workflows/       # CI/CD pipelines
```

## Coding Standards

### C# Conventions

- Use **C# 12** features where appropriate
- **Nullable reference types** enabled (`#nullable enable`)
- **Implicit usings** for cleaner code
- **Expression-bodied members** for simple methods
- **Pattern matching** over traditional conditionals

### Documentation

- All public APIs must have XML documentation comments
- Include `<summary>`, `<param>`, and `<returns>` tags
- Document exceptions that may be thrown
- Provide usage examples for complex APIs

Example:
```csharp
/// <summary>
/// Fetches live mutual fund NAVs from AMFI.
/// </summary>
/// <param name="isin">The ISIN code of the fund.</param>
/// <returns>The current NAV, or null if unavailable.</returns>
/// <exception cref="ArgumentException">Thrown when ISIN is invalid.</exception>
public async Task<decimal?> GetNavAsync(string isin);
```

### Testing

- Write unit tests for all new business logic
- Tests must run on Linux (no WPF dependencies in test project)
- Use descriptive test names: `Method_Scenario_ExpectedResult`
- Include regression tests for bug fixes
- Aim for >80% code coverage on core services

Run tests before committing:
```bash
dotnet test -c Release --collect:"XPlat Code Coverage"
```

### Security Guidelines

- Never log credentials, tokens, or personal data
- Use DPAPI for credential storage (already implemented)
- Validate all external inputs
- Keep dependencies updated (check `dotnet list package --outdated`)

## Pull Request Process

1. **Fork** the repository
2. **Create a branch** for your feature:
   ```bash
   git checkout -b feature/your-feature-name
   ```
3. **Make changes** following coding standards
4. **Write/update tests** as needed
5. **Run all tests** and ensure they pass
6. **Update documentation** if API changes
7. **Submit PR** with clear description

### PR Checklist

- [ ] Code follows project conventions
- [ ] All tests pass
- [ ] New code has test coverage
- [ ] XML documentation added/updated
- [ ] No sensitive data logged or committed
- [ ] CHANGELOG.md updated (if applicable)

## Architecture Decisions

### Why No Dependency Injection Framework?

The app is intentionally lightweight. Manual DI in `App.xaml.cs` keeps the binary small and avoids external dependencies for a single-window application.

### Why WPF Instead of MAUI?

WPF provides mature DWM integration for desktop widget behavior (bottom-most pinning, acrylic effects). MAUI's cross-platform abstraction doesn't support these Windows-specific features well.

### Why Self-Contained Publish?

Single-file self-contained builds ensure users don't need to install .NET separately. The ~70MB binary size is acceptable for the convenience.

## Common Tasks

### Adding a New Service

1. Create class in `src/KiteGlance/Services/`
2. Add XML documentation
3. Write unit tests in `tests/KiteGlance.Tests/`
4. Register in `MainWindow.xaml.cs` or `App.xaml.cs`

### Updating Dependencies

```bash
# Check for outdated packages
dotnet list package --outdated

# Update a specific package
dotnet add package PackageName --version X.Y.Z

# Run tests after update
dotnet test
```

### Debugging

Set `KITEGLANCE_DEBUG=1` to enable:
- Detailed log output to `%APPDATA%\KiteGlance\logs\kiteglance.log`
- API response dumps to `%APPDATA%\KiteGlance\api-dump.json`

## Reporting Issues

When filing bugs, include:
- Windows version (Win + R → `winver`)
- App version (from Settings or About dialog)
- Steps to reproduce
- Expected vs actual behavior
- Log file contents (if applicable)

## License

By contributing, you agree that your contributions are licensed under the MIT License (same as the project).

## Questions?

Open an issue for discussion, or reach out via the contact information in the README.
