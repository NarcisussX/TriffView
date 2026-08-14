namespace TriffView;

internal enum WebViewNavigationKind
{
    Internal,
    External,
    Rejected,
}

internal sealed class WebViewTrustPolicy
{
    private readonly Uri _applicationOrigin;
    private readonly Uri? _developmentOrigin;

    public WebViewTrustPolicy(string virtualHostName, string? developmentUrl)
    {
        _applicationOrigin = new Uri($"https://{virtualHostName}/");
        _developmentOrigin = string.IsNullOrWhiteSpace(developmentUrl) ? null : RequireHttpOrigin(developmentUrl);
    }

    public bool IsBridgeSource(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (SameOrigin(uri, _applicationOrigin) || _developmentOrigin is not null && SameOrigin(uri, _developmentOrigin));

    public WebViewNavigationKind ClassifyNavigation(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return WebViewNavigationKind.Rejected;
        }
        if (SameOrigin(uri, _applicationOrigin) || _developmentOrigin is not null && SameOrigin(uri, _developmentOrigin))
        {
            return WebViewNavigationKind.Internal;
        }
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
            ? WebViewNavigationKind.External
            : WebViewNavigationKind.Rejected;
    }

    private static Uri RequireHttpOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("TRIFFVIEW_DEV_URL must be an absolute HTTP or HTTPS URL without credentials.");
        }
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static bool SameOrigin(Uri candidate, Uri expected)
        => string.IsNullOrEmpty(candidate.UserInfo)
            && string.Equals(candidate.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase)
            && candidate.Port == expected.Port;
}
