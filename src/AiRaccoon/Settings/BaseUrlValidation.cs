namespace AiRaccoon.Settings;

/// <summary>
///     What "usable" means for a model-migration base-url (ADR-0107 PC.1): shared by the CLI's own
///     pre-check and the endpoint's, so the two can never disagree about which base-url is refused.
/// </summary>
internal static class BaseUrlValidation
{
    /// <summary>An absolute URI an HTTP client can actually dial — http(s) only, and not e.g. a scheme-less "host:port".</summary>
    public static bool IsUsableHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
