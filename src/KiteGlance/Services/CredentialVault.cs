using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KiteGlance.Services;

/// <summary>
/// Credentials encrypted at rest. Uses Windows DPAPI (per-user) on Windows,
/// and an AES-256 fallback (key stored alongside the encrypted files) on
/// other platforms so the vault also works in cross-platform CI/dev
/// environments.
/// </summary>
public class CredentialVault
{
    private readonly string _dir;
    private readonly string _credPath;
    private readonly string _tokenPath;
    private readonly string _keyPath;

            public CredentialVault(string? baseDirectory = null)
            {
        _dir = Path.Combine(
                            baseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KiteGlance");

        Directory.CreateDirectory(_dir);

        _credPath = Path.Combine(_dir, "vault.bin");
        _tokenPath = Path.Combine(_dir, "token.bin");
        _keyPath = Path.Combine(_dir, "vault.key");
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

    // -- DPAPI / cross-platform fallback ----------------------------

    // App-specific secondary entropy mixed into DPAPI. This is defense in
    // depth, not a secret: DPAPI already scopes the blob to the Windows user,
    // and this constant only means another app running AS that same user must
    // also know this value to Unprotect. It raises the bar a little; it is not
    // a substitute for OS-level user isolation.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("KiteGlance.v1.dpapi.entropy");

    private void Write(string path, string plaintext)
    {
        byte[] blob = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser)
            : ProtectPortable(Encoding.UTF8.GetBytes(plaintext));

        File.WriteAllBytes(path, blob);
    }

    private string? Read(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var blob = File.ReadAllBytes(path);
            var clear = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser)
                : UnprotectPortable(blob);
            return Encoding.UTF8.GetString(clear);
        }
        catch
        {
            // Includes blobs written by a pre-entropy build: treat as absent,
            // the user re-enters credentials once. Acceptable one-time cost.
            return null;
        }
    }

    // Cross-platform fallback used on non-Windows systems where DPAPI is
    // unavailable (e.g. Linux CI runners). Not as strong as DPAPI's OS-level
    // user isolation, but keeps the vault functional everywhere. The key is
    // generated once per machine/user profile and stored alongside the
    // encrypted files.
    private byte[] ProtectPortable(byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = GetOrCreatePortableKey();
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

        var result = new byte[aes.IV.Length + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipher, 0, result, aes.IV.Length, cipher.Length);
        return result;
    }

    private byte[] UnprotectPortable(byte[] blob)
    {
        using var aes = Aes.Create();
        aes.Key = GetOrCreatePortableKey();

        var ivLength = aes.IV.Length;
        var iv = new byte[ivLength];
        Buffer.BlockCopy(blob, 0, iv, 0, ivLength);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(blob, ivLength, blob.Length - ivLength);
    }

    private byte[] GetOrCreatePortableKey()
    {
        if (File.Exists(_keyPath))
        {
            return Convert.FromBase64String(File.ReadAllText(_keyPath).Trim());
        }

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.GenerateKey();

        File.WriteAllText(_keyPath, Convert.ToBase64String(aes.Key));
        return aes.Key;
    }

    private class Creds
    {
        public string? ApiKey { get; set; }
        public string? ApiSecret { get; set; }
        public DateTime SavedAt { get; set; }
    }
}
