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
    // Blob layout: [magic 'K','G','1'][12-byte nonce][ciphertext][16-byte tag].
    // The magic distinguishes an authenticated GCM blob from the older
    // unauthenticated CBC format, which is still readable so an existing vault
    // survives the upgrade.
    private static readonly byte[] GcmMagic = { (byte)'K', (byte)'G', (byte)'1' };
    private const int GcmNonceSize = 12;
    private const int GcmTagSize = 16;

    private byte[] ProtectPortable(byte[] plaintext)
    {
        var key = GetOrCreatePortableKey();

        var nonce = RandomNumberGenerator.GetBytes(GcmNonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[GcmTagSize];

        using (var gcm = new AesGcm(key, GcmTagSize))
        {
            gcm.Encrypt(nonce, plaintext, cipher, tag);
        }

        var result = new byte[GcmMagic.Length + nonce.Length + cipher.Length + tag.Length];
        var offset = 0;
        Buffer.BlockCopy(GcmMagic, 0, result, offset, GcmMagic.Length); offset += GcmMagic.Length;
        Buffer.BlockCopy(nonce, 0, result, offset, nonce.Length); offset += nonce.Length;
        Buffer.BlockCopy(cipher, 0, result, offset, cipher.Length); offset += cipher.Length;
        Buffer.BlockCopy(tag, 0, result, offset, tag.Length);
        return result;
    }

    private byte[] UnprotectPortable(byte[] blob)
    {
        if (!HasGcmMagic(blob))
        {
            // Pre-GCM vault written by an older build. Read it so the user is
            // not silently logged out; the next save re-writes it as GCM.
            return UnprotectLegacyCbc(blob);
        }

        var minimum = GcmMagic.Length + GcmNonceSize + GcmTagSize;
        if (blob.Length < minimum)
            throw new CryptographicException("Vault blob is truncated.");

        var nonce = new byte[GcmNonceSize];
        var cipherLength = blob.Length - minimum;
        var cipher = new byte[cipherLength];
        var tag = new byte[GcmTagSize];

        var offset = GcmMagic.Length;
        Buffer.BlockCopy(blob, offset, nonce, 0, GcmNonceSize); offset += GcmNonceSize;
        Buffer.BlockCopy(blob, offset, cipher, 0, cipherLength); offset += cipherLength;
        Buffer.BlockCopy(blob, offset, tag, 0, GcmTagSize);

        var plaintext = new byte[cipherLength];
        using var gcm = new AesGcm(GetOrCreatePortableKey(), GcmTagSize);

        // Throws CryptographicException if the tag does not verify. Unlike the
        // CBC version this cannot return attacker-chosen garbage that then gets
        // sent to Kite as an API key.
        gcm.Decrypt(nonce, cipher, tag, plaintext);
        return plaintext;
    }

    private static bool HasGcmMagic(byte[] blob)
    {
        if (blob.Length < GcmMagic.Length) return false;
        for (var i = 0; i < GcmMagic.Length; i++)
        {
            if (blob[i] != GcmMagic[i]) return false;
        }
        return true;
    }

    /// <summary>Reads a blob written before the move to AES-GCM.</summary>
    private byte[] UnprotectLegacyCbc(byte[] blob)
    {
        using var aes = Aes.Create();
        aes.Key = GetOrCreatePortableKey();

        var ivLength = aes.BlockSize / 8;
        if (blob.Length <= ivLength)
            throw new CryptographicException("Legacy vault blob is truncated.");

        var iv = new byte[ivLength];
        Buffer.BlockCopy(blob, 0, iv, 0, ivLength);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(blob, ivLength, blob.Length - ivLength);
    }

    // Guards key creation across processes. Two concurrent saves used to race
    // File.Exists, both generate a key, and both write -- the loser's blob then
    // decrypted with a key that no longer existed on disk, permanently losing
    // the stored credentials.
    private static readonly object KeyGate = new();

    private byte[] GetOrCreatePortableKey()
    {
        lock (KeyGate)
        {
            if (File.Exists(_keyPath))
            {
                return Convert.FromBase64String(File.ReadAllText(_keyPath).Trim());
            }

            var key = RandomNumberGenerator.GetBytes(32);   // AES-256
            File.WriteAllText(_keyPath, Convert.ToBase64String(key));
            RestrictToCurrentUser(_keyPath);
            return key;
        }
    }

    /// <summary>
    /// Removes group/other access from the key file. On Windows the vault is
    /// DPAPI-protected and this path is unused; on Linux/macOS the key sits
    /// beside the ciphertext, so mode 600 is the only thing separating the two.
    /// </summary>
    private static void RestrictToCurrentUser(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Best-effort: a filesystem that cannot represent the mode (a
            // mounted share) must not stop the app from starting.
        }
    }

    private class Creds
    {
        public string? ApiKey { get; set; }
        public string? ApiSecret { get; set; }
        public DateTime SavedAt { get; set; }
    }
}
