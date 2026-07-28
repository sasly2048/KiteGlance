using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static System.Security.Cryptography.ProtectedData;

namespace KiteGlance.Services;

/// <summary>Credentials encrypted at rest with Windows DPAPI (per-user).</summary>
public class CredentialVault
{
    private readonly string _dir;
    private readonly string _credPath;
    private readonly string _tokenPath;

    public CredentialVault()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KiteGlance");

        Directory.CreateDirectory(_dir);

        _credPath = Path.Combine(_dir, "vault.bin");
        _tokenPath = Path.Combine(_dir, "token.bin");
    }

    // -- Credentials -----------------------------------------------

    public void SaveCredentials(string apiKey, string apiSecret)
    {
        var json = JsonSerializer.Serialize(new Creds
        {
            ApiKey = apiKey,
            ApiSecret = apiSecret,
            SavedAt = DateTime.UtcNow
        });

        Write(_credPath, json);
    }

    public (string? ApiKey, string? ApiSecret) GetCredentials()
    {
        // Environment variables take priority. This is what lets a developer
        // running from source keep credentials in a local .env / user-level
        // env var instead of typing them into the Settings dialog each time --
        // see .env.example. Nothing here is ever hardcoded or committed.
        var envKey = Environment.GetEnvironmentVariable("KITE_API_KEY");
        var envSecret = Environment.GetEnvironmentVariable("KITE_API_SECRET");

        if (!string.IsNullOrWhiteSpace(envKey) && !string.IsNullOrWhiteSpace(envSecret))
            return (envKey, envSecret);

        var json = Read(_credPath);
        if (json is null) return (null, null);

        try
        {
            var c = JsonSerializer.Deserialize<Creds>(json);
            return (c?.ApiKey, c?.ApiSecret);
        }
        catch
        {
            return (null, null);
        }
    }

    public string? GetApiKey() => GetCredentials().ApiKey;
    public string? GetApiSecret() => GetCredentials().ApiSecret;

    // -- Access token (rotates daily) ------------------------------

    public void SaveAccessToken(string token) => Write(_tokenPath, token);
    public string? GetAccessToken() => Read(_tokenPath);

    public void ClearAccessToken()
    {
        try { if (File.Exists(_tokenPath)) File.Delete(_tokenPath); }
        catch { /* ignore */ }
    }

    public void ClearAll()
    {
        ClearAccessToken();
        try { if (File.Exists(_credPath)) File.Delete(_credPath); }
        catch { /* ignore */ }
    }

    // -- DPAPI -----------------------------------------------------

    // App-specific secondary entropy mixed into DPAPI. This is defense in
    // depth, not a secret: DPAPI already scopes the blob to the Windows user,
    // and this constant only means another app running AS that same user must
    // also know this value to Unprotect. It raises the bar a little; it is not
    // a substitute for OS-level user isolation.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("KiteGlance.v1.dpapi.entropy");

    private static void Write(string path, string plaintext)
    {
        var blob = Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);

        File.WriteAllBytes(path, blob);
    }

    private static string? Read(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var blob = File.ReadAllBytes(path);
            var clear = Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch
        {
            // Includes blobs written by a pre-entropy build: treat as absent,
            // the user re-enters credentials once. Acceptable one-time cost.
            return null;
        }
    }

    private class Creds
    {
        public string? ApiKey { get; set; }
        public string? ApiSecret { get; set; }
        public DateTime SavedAt { get; set; }
    }
}
