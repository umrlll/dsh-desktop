using System;

namespace DSHDesktop;

/// <summary>
/// Defines the trust boundary for content displayed inside the privileged desktop WebView.
/// The policy is deliberately independent of WebView2 so it can be unit-tested without a
/// Windows UI host.
/// </summary>
internal static class WebViewPolicy
{
    internal enum NavigationDecision
    {
        AllowInApp,
        OpenExternal,
        Block,
    }

    /// <summary>
    /// Accept only an exact IPv4 loopback HTTP(S) endpoint emitted by the owned Harness child.
    /// Names such as localhost are intentionally rejected because their resolution is mutable.
    /// </summary>
    internal static bool TryCreateTrustedOrigin(string? bootstrapUrl, out Uri? trustedOrigin)
    {
        trustedOrigin = null;
        if (!Uri.TryCreate(bootstrapUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.IsDefaultPort || uri.Port is < 1 or > 65535) return false;

        trustedOrigin = new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri;
        return true;
    }

    internal static NavigationDecision ClassifyNavigation(
        string? targetUrl,
        Uri? trustedOrigin,
        bool isMainFrame = true)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target))
            return NavigationDecision.Block;

        // Before an owned Harness origin is established there is no trusted browsing context and
        // therefore no navigation (including an external hand-off) may be initiated by WebView.
        if (trustedOrigin == null)
            return NavigationDecision.Block;

        if (IsTrusted(target, trustedOrigin))
            return NavigationDecision.AllowInApp;

        // Only a top-level ordinary web link may leave the app, and it always goes through the
        // system browser. Subframe navigation never gets to widen the main window's trust boundary.
        if (isMainFrame && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps))
            return NavigationDecision.OpenExternal;

        return NavigationDecision.Block;
    }

    internal static bool IsTrusted(string? targetUrl, Uri? trustedOrigin) =>
        Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) && IsTrusted(target, trustedOrigin);

    internal static bool IsTrustedDownloadSource(string? sourceUrl, Uri? trustedOrigin)
    {
        if (IsTrusted(sourceUrl, trustedOrigin)) return true;
        if (string.IsNullOrWhiteSpace(sourceUrl)
            || !sourceUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            return false;

        return Uri.TryCreate(sourceUrl["blob:".Length..], UriKind.Absolute, out var embeddedOrigin)
            && IsTrusted(embeddedOrigin, trustedOrigin);
    }

    private static bool IsTrusted(Uri target, Uri? trustedOrigin)
    {
        if (trustedOrigin == null) return false;
        return string.Equals(target.Scheme, trustedOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.Host, trustedOrigin.Host, StringComparison.OrdinalIgnoreCase)
            && target.Port == trustedOrigin.Port;
    }
}
