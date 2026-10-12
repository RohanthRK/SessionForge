using SessionForge.Core.Security;

namespace SessionForge.Protocols;

public enum HostKeyDecision { Reject, AcceptOnce, AcceptAndSave }

public sealed record HostKeyInfo(
    string Host,
    int Port,
    string KeyType,
    byte[] KeyBlob,
    string FingerprintSha256,
    string FingerprintMd5,
    HostKeyStatus Status,
    KnownHostEntry? PreviousEntry);

public sealed record PromptField(string Label, bool Secret, string? DefaultValue = null);

/// <summary>Result of a credential prompt: the values and whether the user ticked "save in vault".</summary>
public sealed record PromptResult(string[] Values, bool Save);

/// <summary>Identifies which secret is being requested so the UI/vault can look it up or store it.</summary>
public sealed record SecretTarget(string VaultKey, string DisplayName, string? Username);

/// <summary>
/// UI callbacks used during connection setup. Implementations may block the calling (background) thread while
/// waiting for the user; they must never be invoked on the UI thread by protocol code.
/// </summary>
public interface IConnectionInteraction
{
    HostKeyDecision ConfirmHostKey(HostKeyInfo info);

    /// <summary>Returns a stored secret without prompting, or null.</summary>
    string? GetStoredSecret(SecretTarget target);

    /// <summary>Stores a secret after a successful login when the user asked to save it.</summary>
    void StoreSecret(SecretTarget target, string value);

    /// <summary>Asks the user for one or more values. Returns null if cancelled.</summary>
    PromptResult? Prompt(string title, string message, IReadOnlyList<PromptField> fields, bool offerSave);

    /// <summary>Shows a non-blocking informational message (e.g. server banner).</summary>
    void Notify(string message);
}
