using KiteGlance.Services;
using Xunit;

namespace KiteGlance.Tests;

/// <summary>
/// Tests for the Settings "empty secret = keep existing" rule. The
/// rule itself lives in <see cref="SecretMerge.Resolve"/>; the
/// WPF-coupled <c>SettingsWindow.OnSave</c> just calls into it and
/// surfaces the <c>null</c> result as a user-facing error.
/// </summary>
public class SecretMergeTests
{
    /// A non-empty new secret always wins. The whole point of the
    /// "empty = keep" rule is that an explicitly-typed secret is a
    /// deliberate change, not a no-op.
    [Fact]
    public void NonEmptyNewSecret_wins()
    {
        var resolved = SecretMerge.Resolve("newsecret", existing: "old");
        Assert.Equal("newsecret", resolved);
    }

    /// The regression the rule was written for. A blank field on
    /// Save with an existing secret must come back as the existing
    /// secret, not an empty string -- which would wipe the stored
    /// credential.
    [Fact]
    public void EmptyNewSecret_keeps_existing()
    {
        var resolved = SecretMerge.Resolve("", existing: "old");
        Assert.Equal("old", resolved);
    }

    /// Whitespace is treated as empty: typing a single space into
    /// the secret field is not a credential update, and the user
    /// is not trying to set their secret to a space.
    [Fact]
    public void WhitespaceOnlyNewSecret_treated_as_empty()
    {
        var resolved = SecretMerge.Resolve("   ", existing: "old");
        Assert.Equal("old", resolved);
    }

    /// First-time setup: there is no existing secret, and the
    /// field is empty. The result must be <c>null</c> so the UI can
    /// show "The API secret is required" instead of saving an
    /// empty string.
    [Fact]
    public void EmptyNewSecret_no_existing_returns_null()
    {
        var resolved = SecretMerge.Resolve("", existing: null);
        Assert.Null(resolved);
    }

    /// First-time setup with a real secret typed in: the result
    /// is the typed value (trimmed).
    [Fact]
    public void NonEmptyNewSecret_no_existing_returns_it_trimmed()
    {
        var resolved = SecretMerge.Resolve("  typed  ", existing: null);
        Assert.Equal("typed", resolved);
    }

    /// A <c>null</c> typed value (which the WPF PasswordBox can
    /// deliver in some edge cases -- e.g. when the binding is
    /// momentarily cleared) is treated like an empty value.
    [Fact]
    public void NullNewSecret_keeps_existing()
    {
        var resolved = SecretMerge.Resolve(null, existing: "old");
        Assert.Equal("old", resolved);
    }
}
