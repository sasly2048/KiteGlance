namespace KiteGlance.Services;

/// <summary>
/// The Settings save rule: a blank API-secret field on Save means
/// "keep the stored secret", never "wipe it". Extracted from the
/// WPF-coupled <c>SettingsWindow.OnSave</c> path so the test target
/// can exercise it without WPF. Mirrors the Mac
/// <c>KiteGlanceCore.SecretMerge</c> in <c>SecretMerge.swift</c> --
/// the two implementations should produce identical results for
/// the same inputs.
/// </summary>
public static class SecretMerge
{
    /// <summary>
    /// The resolved secret to write. <c>null</c> means "do not save
    /// -- the user has not entered a secret and there is nothing to
    /// keep", which the UI surfaces as "The API secret is required".
    /// </summary>
    /// <param name="newSecret">The verbatim value the user typed in
    /// the secret field. Whitespace-only is treated as empty.</param>
    /// <param name="existing">The currently-stored secret, or
    /// <c>null</c> if the vault has nothing.</param>
    public static string? Resolve(string? newSecret, string? existing)
    {
        if (!string.IsNullOrWhiteSpace(newSecret))
        {
            return newSecret!.Trim();
        }
        return existing;
    }
}
