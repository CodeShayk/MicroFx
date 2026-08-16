namespace MicroFx.Configuration;

/// <summary>
/// Decides whether a configuration value must be withheld from diagnostics output.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately over-inclusive. A redacted value that was harmless costs an operator one lookup
/// elsewhere; a leaked credential costs a rotation and an incident review. When the classification
/// is uncertain, this redacts.
/// </para>
/// <para>
/// This is defence in depth, not the primary control. The primary control is that secrets are held
/// in a secret store and diagnostics endpoints are bound to the management port.
/// </para>
/// </remarks>
public static class SecretRedactor
{
    /// <summary>The text substituted for a withheld value.</summary>
    public const string Redacted = "[redacted]";

    private static readonly string[] SensitiveFragments =
    [
        "password", "passwd", "pwd",
        "secret", "token", "credential", "apikey", "api_key",
        "connectionstring", "connection_string",
        "privatekey", "private_key", "certificate", "pfx",
        "signingkey", "signing_key", "clientsecret", "client_secret",
        "accesskey", "access_key", "sas", "authorization", "auth",
        "salt", "seed", "cookie", "sessionkey", "session_key",
    ];

    /// <summary>Whether a configuration key names a value that must not be displayed.</summary>
    public static bool IsSensitive(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        foreach (var fragment in SensitiveFragments)
        {
            if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the value if it is safe to display, otherwise <see cref="Redacted"/>.
    /// </summary>
    public static string? Redact(string key, string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (IsSensitive(key))
        {
            return Redacted;
        }

        // A value that looks like a connection string carries embedded credentials regardless of
        // what its key is called, so shape is checked as well as name.
        return LooksLikeConnectionString(value) ? Redacted : value;
    }

    private static bool LooksLikeConnectionString(string value) =>
        value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("pwd=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("accountkey=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("sharedaccesskey=", StringComparison.OrdinalIgnoreCase);
}
