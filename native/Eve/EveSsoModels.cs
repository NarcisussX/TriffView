using System.Net;

namespace TriffView.Eve;

internal sealed record EveSsoOptions(
    string ClientId,
    string RedirectUri,
    IReadOnlySet<string> RequiredScopes,
    string UserAgent);

internal sealed record EveIdentity(
    long CharacterId,
    string CharacterName,
    string? OwnerHash,
    IReadOnlySet<string> Scopes);

internal sealed record EveValidatedToken(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    EveIdentity Identity);

internal sealed class OAuthTokenException : InvalidOperationException
{
    public OAuthTokenException(HttpStatusCode statusCode, string errorCode, string message) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }
    public string ErrorCode { get; }
    public bool IsInvalidGrant => string.Equals(ErrorCode, "invalid_grant", StringComparison.Ordinal);
    public bool IsDefinitiveAuthorizationFailure => ErrorCode is "invalid_grant" or "identity_mismatch" or "owner_changed";
}

internal interface IEveSsoClient
{
    Task<EveValidatedToken> AuthorizeAsync(TimeSpan timeout, CancellationToken cancellationToken);
    Task<EveValidatedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}
