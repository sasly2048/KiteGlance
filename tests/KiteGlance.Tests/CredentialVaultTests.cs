using System;
using System.IO;
using KiteGlance.Services;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// Tests for credential storage and DPAPI encryption.
/// These tests verify the vault's behavior without testing DPAPI itself
/// (which is a Windows OS component).
/// </summary>
public class CredentialVaultTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _originalAppData;

    public CredentialVaultTests()
    {
        // Isolate tests in a temp directory so they don't touch real credentials
        _testDir = Path.Combine(Path.GetTempPath(), $"KiteGlanceTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        
        // Redirect APPDATA for the duration of the test
        _originalAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Environment.SetEnvironmentVariable("APPDATA", _testDir, EnvironmentVariableTarget.Process);
    }

    [Fact]
    public void SaveCredentials_writes_encrypted_file()
    {
        var vault = new CredentialVault(_testDir);
        vault.SaveCredentials("test-api-key", "test-api-secret");

        var credFile = Path.Combine(_testDir, "KiteGlance", "vault.bin");
        Assert.True(File.Exists(credFile), "Encrypted credential file should exist");
        Assert.True(new FileInfo(credFile).Length > 0, "Encrypted file should not be empty");
    }

    [Fact]
    public void GetCredentials_returns_saved_values()
    {
        var vault = new CredentialVault(_testDir);
        vault.SaveCredentials("my-api-key", "my-api-secret");

        var (apiKey, apiSecret) = vault.GetCredentials();
        
        Assert.Equal("my-api-key", apiKey);
        Assert.Equal("my-api-secret", apiSecret);
    }

    [Fact]
    public void GetApiKey_convenience_method_works()
    {
        var vault = new CredentialVault(_testDir);
        vault.SaveCredentials("key-123", "secret-456");

        Assert.Equal("key-123", vault.GetApiKey());
    }

    [Fact]
    public void GetApiSecret_convenience_method_works()
    {
        var vault = new CredentialVault(_testDir);
        vault.SaveCredentials("key-123", "secret-456");

        Assert.Equal("secret-456", vault.GetApiSecret());
    }

    [Fact]
    public void GetCredentials_returns_null_when_no_credentials_exist()
    {
        var vault = new CredentialVault(_testDir);
        
        var (apiKey, apiSecret) = vault.GetCredentials();
        
        Assert.Null(apiKey);
        Assert.Null(apiSecret);
    }

    [Fact]
    public void Environment_variables_take_priority_over_disk()
    {
        try
        {
            Environment.SetEnvironmentVariable("KITE_API_KEY", "env-key", EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("KITE_API_SECRET", "env-secret", EnvironmentVariableTarget.Process);

            var vault = new CredentialVault(_testDir);
            vault.SaveCredentials("disk-key", "disk-secret");

            var (apiKey, apiSecret) = vault.GetCredentials();

            Assert.Equal("env-key", apiKey);
            Assert.Equal("env-secret", apiSecret);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KITE_API_KEY", null, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("KITE_API_SECRET", null, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public void SaveAccessToken_writes_token_file()
    {
        var vault = new CredentialVault(_testDir);
        vault.SaveAccessToken("test-access-token-123");

        var tokenFile = Path.Combine(_testDir, "KiteGlance", "token.bin");
        Assert.True(File.Exists(tokenFile), "Token file should exist");
    }

    [Fact]
    public void GetAccessToken_returns_saved_token()
    {
        var vault = new CredentialVault(_testDir);
        vault.SaveAccessToken("my-access-token");

        Assert.Equal("my-access-token", vault.GetAccessToken());
    }

    [Fact]
    public void ClearAccessToken_removes_token_file()
    {
        var vault = new CredentialVault(_testDir);
        vault.SaveAccessToken("temp-token");
        vault.ClearAccessToken();

        var tokenFile = Path.Combine(_testDir, "KiteGlance", "token.bin");
        Assert.False(File.Exists(tokenFile), "Token file should be deleted");
    }

    [Fact]
    public void ClearAll_removes_both_files()
    {
        var vault = new CredentialVault(_testDir);
        vault.SaveCredentials("key", "secret");
        vault.SaveAccessToken("token");
        
        vault.ClearAll();

        Assert.False(File.Exists(Path.Combine(_testDir, "KiteGlance", "vault.bin")));
        Assert.False(File.Exists(Path.Combine(_testDir, "KiteGlance", "token.bin")));
    }

    [Fact]
    public void GetCredentials_handles_corrupted_file_gracefully()
    {
        var vault = new CredentialVault(_testDir);
        var credFile = Path.Combine(_testDir, "KiteGlance", "vault.bin");
        
        // Write invalid encrypted data
        Directory.CreateDirectory(Path.GetDirectoryName(credFile)!);
        File.WriteAllBytes(credFile, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        var (apiKey, apiSecret) = vault.GetCredentials();
        
        Assert.Null(apiKey);
        Assert.Null(apiSecret);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }

        Environment.SetEnvironmentVariable("APPDATA", _originalAppData, EnvironmentVariableTarget.Process);
    }
}
