using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DSHDesktop.Core;

/// <summary>
/// The signed, small release document that must be verified before a runtime payload is
/// downloaded. The runtime manifest remains the post-download file-integrity proof; this record
/// binds that future payload to an issuer, a channel, and a bounded validity period.
/// </summary>
public sealed record RuntimeReleaseDescriptor(
    int SchemaVersion,
    string KeyId,
    string Channel,
    string RuntimeId,
    string DesktopVersion,
    string DshVersion,
    string NodeVersion,
    string PnpmVersion,
    string Platform,
    string Architecture,
    int ProfileSchema,
    string PayloadUrl,
    string PayloadSha256,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record SignedRuntimeRelease(
    RuntimeReleaseDescriptor Descriptor,
    string Signature);

public enum RuntimeReleaseVerificationStatus
{
    Verified,
    InvalidMetadata,
    UnknownKey,
    Expired,
    InvalidSignature,
}

public sealed record RuntimeReleaseVerification(
    RuntimeReleaseVerificationStatus Status,
    string? Error = null)
{
    public bool IsVerified => Status == RuntimeReleaseVerificationStatus.Verified;
}

/// <summary>
/// Verifies ECDSA P-256 release metadata before any update payload is trusted. This class makes
/// no network requests: callers choose the delivery transport, then must pass its exact response
/// through this verifier and <see cref="VerifyPayload"/> before staging a runtime slot.
/// </summary>
public static class RuntimeReleaseTrust
{
    private static readonly Regex ExactVersion = new(
        @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KeyId = new(
        @"^[0-9A-Za-z][0-9A-Za-z._-]{0,79}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Sha256 = new(
        @"^[0-9a-f]{64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Produces the canonical UTF-8 bytes to be signed by the release pipeline. Every field is
    /// base64-encoded per line, making delimiters and embedded Unicode unambiguous.
    /// </summary>
    public static byte[] CreateSigningPayload(RuntimeReleaseDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var fields = new[]
        {
            "dsh-desktop-runtime-release/v1",
            descriptor.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            descriptor.KeyId,
            descriptor.Channel,
            descriptor.RuntimeId,
            descriptor.DesktopVersion,
            descriptor.DshVersion,
            descriptor.NodeVersion,
            descriptor.PnpmVersion,
            descriptor.Platform,
            descriptor.Architecture,
            descriptor.ProfileSchema.ToString(CultureInfo.InvariantCulture),
            descriptor.PayloadUrl,
            descriptor.PayloadSha256,
            descriptor.IssuedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            descriptor.ExpiresAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        };
        return Encoding.UTF8.GetBytes(string.Join("\n", fields.Select(EncodeField)) + "\n");
    }

    public static RuntimeReleaseVerification Verify(
        SignedRuntimeRelease release,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);
        var metadataError = ValidateMetadata(release.Descriptor);
        if (metadataError != null)
            return new RuntimeReleaseVerification(RuntimeReleaseVerificationStatus.InvalidMetadata, metadataError);

        if ((now ?? DateTimeOffset.UtcNow) >= release.Descriptor.ExpiresAtUtc)
            return new RuntimeReleaseVerification(RuntimeReleaseVerificationStatus.Expired, "更新元数据已过期。");

        if (!trustedPublicKeys.TryGetValue(release.Descriptor.KeyId, out var publicKeyPem)
            || string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return new RuntimeReleaseVerification(
                RuntimeReleaseVerificationStatus.UnknownKey,
                "更新元数据使用了未受信任的签名密钥。");
        }

        try
        {
            var signature = Convert.FromBase64String(release.Signature);
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.VerifyData(CreateSigningPayload(release.Descriptor), signature, HashAlgorithmName.SHA256)
                ? new RuntimeReleaseVerification(RuntimeReleaseVerificationStatus.Verified)
                : new RuntimeReleaseVerification(RuntimeReleaseVerificationStatus.InvalidSignature, "更新元数据签名无效。");
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return new RuntimeReleaseVerification(
                RuntimeReleaseVerificationStatus.InvalidSignature,
                "无法验证更新元数据签名：" + ex.Message);
        }
    }

    /// <summary>
    /// Checks the exact downloaded payload bytes against the hash authenticated by a verified
    /// release descriptor. This method deliberately does not seek or close the supplied stream.
    /// </summary>
    public static bool VerifyPayload(Stream payload, RuntimeReleaseDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!Sha256.IsMatch(descriptor.PayloadSha256)) return false;
        using var sha256 = SHA256.Create();
        var actual = sha256.ComputeHash(payload);
        var expected = Convert.FromHexString(descriptor.PayloadSha256);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string? ValidateMetadata(RuntimeReleaseDescriptor descriptor)
    {
        if (descriptor.SchemaVersion != 1) return "release schemaVersion 必须为 1。";
        if (!KeyId.IsMatch(descriptor.KeyId ?? string.Empty)) return "签名 keyId 无效。";
        if (descriptor.Channel is not ("stable" or "beta")) return "release channel 必须为 stable 或 beta。";
        if (!RuntimeSlotManager.IsSafeRuntimeId(descriptor.RuntimeId)) return "release runtimeId 不安全。";
        if (!ExactVersion.IsMatch(descriptor.DesktopVersion ?? string.Empty)
            || !ExactVersion.IsMatch(descriptor.DshVersion ?? string.Empty)
            || !ExactVersion.IsMatch(descriptor.NodeVersion ?? string.Empty)
            || !ExactVersion.IsMatch(descriptor.PnpmVersion ?? string.Empty))
        {
            return "release 的 Desktop/DSH/Node/pnpm 版本必须精确锁定。";
        }
        if (descriptor.Platform is not ("win32" or "darwin" or "linux")
            || descriptor.Architecture is not ("x64" or "arm64"))
        {
            return "release 平台或架构无效。";
        }
        if (descriptor.ProfileSchema < 1) return "release profileSchema 必须大于等于 1。";
        if (!Uri.TryCreate(descriptor.PayloadUrl, UriKind.Absolute, out var payload)
            || !string.Equals(payload.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "release payloadUrl 必须为 HTTPS 绝对地址。";
        }
        if (!Sha256.IsMatch(descriptor.PayloadSha256 ?? string.Empty))
            return "release payloadSha256 必须是小写 SHA-256 十六进制值。";
        if (descriptor.ExpiresAtUtc <= descriptor.IssuedAtUtc)
            return "release 过期时间必须晚于签发时间。";
        if (descriptor.ExpiresAtUtc - descriptor.IssuedAtUtc > TimeSpan.FromDays(31))
            return "release 有效期不能超过 31 天。";
        return null;
    }

    private static string EncodeField(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
}
