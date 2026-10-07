namespace WebViewHostBridge;

/// <summary>
/// Identifies the desktop shell as the browsing agent.
///
/// <para>The host appends a marker to the WebView user agent once the browser is initialized:</para>
/// <code>
/// webView.CoreWebView2.Settings.UserAgent += " " + HostUserAgent.Marker("MyApp-Shell");
/// </code>
///
/// <para>Unlike a query-string flag, the user agent rides on every request — it survives in-app
/// navigation, reloads and login redirects. The server checks it with
/// <see cref="IsHost"/> against the <c>User-Agent</c> header.</para>
/// </summary>
public static class HostUserAgent
{
    /// <summary>Builds the <c>product/version</c> token to append to the user agent.</summary>
    /// <param name="product">Product token without spaces or slashes, e.g. <c>MyApp-Shell</c>.</param>
    /// <param name="version">Token version; matching ignores it.</param>
    public static string Marker(string product, string version = "1.0")
    {
        ValidateToken(product, nameof(product));
        ValidateToken(version, nameof(version));
        return $"{product}/{version}";
    }

    /// <summary>True when <paramref name="userAgent"/> carries the marker of <paramref name="product"/> (any version).</summary>
    public static bool IsHost(string? userAgent, string product)
    {
        ValidateToken(product, nameof(product));
        if (string.IsNullOrEmpty(userAgent))
            return false;

        var prefix = product + "/";
        return userAgent
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateToken(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Token must not be empty.", paramName);
        if (value.Any(c => char.IsWhiteSpace(c) || c == '/'))
            throw new ArgumentException("Token must not contain whitespace or '/'.", paramName);
    }
}
