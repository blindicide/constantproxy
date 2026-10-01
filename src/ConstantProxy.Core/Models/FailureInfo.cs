namespace ConstantProxy.Core.Models;

/// <summary>Broad failure categories (SPEC §77). <see cref="Unknown"/> is always a legitimate answer.</summary>
public enum FailureCategory
{
    Unknown,
    LocalConfiguration,
    Authentication,
    HostVerification,
    Network,
    RemoteConnection,
    Forwarding,
}

/// <summary>
/// A classified failure. <see cref="Code"/> is a stable key (also usable as a localization key suffix);
/// <see cref="Message"/> is an English fallback; <see cref="Details"/> carries technical text.
/// </summary>
public sealed record FailureInfo(
    FailureCategory Category,
    string Code,
    string Message,
    bool Retryable,
    string? Details = null);
