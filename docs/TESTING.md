# Testing Strategy

## Overview

Kite Glance uses xUnit for unit testing with a focus on:
- **Cross-platform test execution** (tests run on Linux CI)
- **No WPF dependencies** in test project
- **Regression tests** for known bugs
- **High coverage** on core business logic

## Test Project Structure

```
tests/KiteGlance.Tests/
├── PnlMathTests.cs          # P&L arithmetic regression tests
├── CredentialVaultTests.cs   # Credential storage & encryption tests
├── AmfiNavServiceTests.cs    # NAV parsing & caching tests
├── KiteServiceTests.cs       # Portfolio calculation tests
└── BackdropServiceTests.cs   # Backdrop selection logic tests
```

## Running Tests

### Local Development

```bash
# Run all tests
dotnet test tests/KiteGlance.Tests/KiteGlance.Tests.csproj

# Run with code coverage
dotnet test -c Release --collect:"XPlat Code Coverage"

# Run specific test class
dotnet test --filter "FullyQualifiedName~PnlMathTests"

# Run with verbose output
dotnet test --logger "console;verbosity=detailed"
```

### CI Pipeline

Tests run automatically on:
- Every push to `main` branch
- Every pull request
- Linux runners (ubuntu-latest)

See `.github/workflows/build.yml` for configuration.

## Test Categories

### Unit Tests

Pure logic tests with no external dependencies:

```csharp
[Fact]
public void Pnl_ignores_zero_from_kite_and_computes_from_nav()
{
    var qty = 1749.91m / 47.019013m;
    var pnl = PnlMath.Pnl(qty, 47.019013m, 44.0707m, apiPnl: 0m, awaitingPrice: false);
    
    Assert.True(pnl < -100m);
    Assert.Equal(-109.72m, pnl, precision: 0);
}
```

### Integration-style Tests

Tests that verify component interaction (still mock external services):

```csharp
[Fact]
public void GetCredentials_returns_saved_values()
{
    var vault = new CredentialVault();
    vault.SaveCredentials("my-api-key", "my-api-secret");
    
    var (apiKey, apiSecret) = vault.GetCredentials();
    
    Assert.Equal("my-api-key", apiKey);
    Assert.Equal("my-api-secret", apiSecret);
}
```

## Writing New Tests

### Naming Convention

Use `Class_Method_Scenario_ExpectedResult` pattern:

```csharp
[Fact]
public void Parse_handles_na_nav_values() { }

[Fact]
public void GetCredentials_returns_null_when_no_credentials_exist() { }

[Theory]
[InlineData("Flexi", 2109.971684, 2241.1970, 200.45, 12.46)]
public void Each_fund_matches_coin_within_a_rupee(...) { }
```

### Test Isolation

- Use temp directories for file-based tests
- Mock HTTP calls with `MockHttpMessageHandler`
- Clean up resources in `Dispose()` method
- Never rely on global state between tests

Example:

```csharp
public class CredentialVaultTests : IDisposable
{
    private readonly string _testDir;
    
    public CredentialVaultTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"KiteGlanceTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        Environment.SetEnvironmentVariable("APPDATA", _testDir, EnvironmentVariableTarget.Process);
    }
    
    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
        Environment.SetEnvironmentVariable("APPDATA", null, EnvironmentVariableTarget.Process);
    }
}
```

## Code Coverage

Target coverage levels:
- **Core Services**: >80%
- **P&L Math**: 100% (critical path)
- **UI Code**: Best effort (WPF coupling makes isolation difficult)

Generate coverage report:

```bash
dotnet test -c Release --collect:"XPlat Code Coverage"
# Results in: tests/KiteGlance.Tests/TestResults/*/coverage.cobertura.xml
```

## Regression Tests

Key bugs that have regression tests:

| Bug | Test File | Description |
|-----|-----------|-------------|
| MF PnL showing 0 | `PnlMathTests.cs` | Kite's `pnl: 0` not trusted as real zero |
| Current value drift | `PnlMathTests.cs` | `current = invested + pnl` consistency |
| Unpriced holdings | `PnlMathTests.cs` | `last_price: 0` held at cost, not shown as loss |

## Continuous Integration

CI runs:
1. Pre-flight checks (Python script validation)
2. Unit tests on Linux
3. Build on Windows (ARM64 and x64)

Test results are uploaded as artifacts for 7 days.

## Troubleshooting

### Tests failing on Linux but pass on Windows

Check for:
- Windows-specific paths (`C:\\`, `%APPDATA%`)
- Case-sensitive file operations
- DPAPI usage (mocked in tests)

### Flaky tests

Common causes:
- Timing-dependent assertions
- Shared static state
- File system race conditions

Fix by:
- Using deterministic time sources
- Isolating test state
- Proper async/await patterns
