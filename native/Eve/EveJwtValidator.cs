using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;

namespace TriffView.Eve;

internal sealed partial class EveJwtValidator
{
    internal static readonly HashSet<string> AcceptedIssuers = new(StringComparer.Ordinal)
    {
        "https://login.eveonline.com",
        "https://login.eveonline.com/",
        "login.eveonline.com",
    };

    private static readonly string[] AllowedAlgorithms = [SecurityAlgorithms.RsaSha256];
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);
    private readonly string _clientId;
    private readonly IReadOnlySet<string> _requiredScopes;
    private readonly IEveSigningKeySource _keys;

    public EveJwtValidator(string clientId, IReadOnlySet<string> requiredScopes, IEveSigningKeySource keys)
    {
        _clientId = clientId;
        _requiredScopes = requiredScopes;
        _keys = keys;
    }

    public async Task<EveIdentity> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 32 * 1024)
        {
            throw new SecurityTokenException("EVE SSO returned an invalid access token.");
        }

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        JwtSecurityToken unvalidated;
        try
        {
            unvalidated = handler.ReadJwtToken(token);
        }
        catch (Exception exception) when (exception is ArgumentException or SecurityTokenException)
        {
            throw new SecurityTokenException("EVE SSO returned an unreadable access token.", exception);
        }

        if (!AllowedAlgorithms.Contains(unvalidated.Header.Alg, StringComparer.Ordinal))
        {
            throw new SecurityTokenInvalidAlgorithmException("EVE SSO access token used an unexpected signing algorithm.");
        }

        var keys = await _keys.GetSigningKeysAsync(forceRefresh: false, cancellationToken);
        var refreshed = false;
        if (string.IsNullOrWhiteSpace(unvalidated.Header.Kid) || keys.All(key => key.KeyId != unvalidated.Header.Kid))
        {
            keys = await _keys.GetSigningKeysAsync(forceRefresh: true, cancellationToken);
            refreshed = true;
        }

        ClaimsPrincipal principal;
        SecurityToken validated;
        try
        {
            principal = handler.ValidateToken(token, Parameters(keys), out validated);
        }
        catch (SecurityTokenSignatureKeyNotFoundException) when (!refreshed)
        {
            keys = await _keys.GetSigningKeysAsync(forceRefresh: true, cancellationToken);
            principal = handler.ValidateToken(token, Parameters(keys), out validated);
        }

        var jwt = validated as JwtSecurityToken
            ?? throw new SecurityTokenException("EVE SSO access token was not a JWT.");
        var subject = principal.FindFirst("sub")?.Value ?? string.Empty;
        var match = CharacterSubject().Match(subject);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var characterId) || characterId <= 0)
        {
            throw new SecurityTokenException("EVE SSO access token had an invalid character subject.");
        }

        var name = (principal.FindFirst("name")?.Value ?? string.Empty).Trim();
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl))
        {
            throw new SecurityTokenException("EVE SSO access token had an invalid character name.");
        }

        var ownerClaim = principal.FindFirst("owner");
        string? owner = null;
        if (ownerClaim is not null)
        {
            owner = ownerClaim.Value.Trim();
            if (owner.Length is < 8 or > 256 || owner.Any(char.IsControl))
            {
                throw new SecurityTokenException("EVE SSO access token had an invalid owner claim.");
            }
        }

        var authorizedParty = principal.FindFirst("azp");
        if (authorizedParty is not null && !string.Equals(authorizedParty.Value, _clientId, StringComparison.Ordinal))
        {
            throw new SecurityTokenException("EVE SSO access token was authorized to a different client.");
        }

        var scopes = ReadScopes(jwt.Payload);
        var missing = _requiredScopes.Where(scope => !scopes.Contains(scope)).OrderBy(scope => scope).ToArray();
        if (missing.Length > 0)
        {
            throw new SecurityTokenException($"EVE SSO access token is missing required scopes: {string.Join(", ", missing)}.");
        }

        return new EveIdentity(characterId, name, owner, scopes);
    }

    private TokenValidationParameters Parameters(IEnumerable<SecurityKey> keys) => new()
    {
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKeys = keys,
        ValidAlgorithms = AllowedAlgorithms,
        ValidateIssuer = true,
        ValidIssuers = AcceptedIssuers,
        ValidateAudience = true,
        AudienceValidator = (audiences, _, _) =>
        {
            var values = audiences?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            return values.Contains("EVE Online") && values.Contains(_clientId);
        },
        RequireAudience = true,
        RequireExpirationTime = true,
        ValidateLifetime = true,
        ClockSkew = ClockSkew,
        NameClaimType = "name",
    };

    private static IReadOnlySet<string> ReadScopes(JwtPayload payload)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        if (!payload.TryGetValue("scp", out var raw) || raw is null) return scopes;
        switch (raw)
        {
            case string text:
                foreach (var scope in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) scopes.Add(scope);
                break;
            case JsonElement element when element.ValueKind == JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } scope) scopes.Add(scope);
                }
                break;
            case IEnumerable<object> items:
                foreach (var item in items)
                {
                    if (item is string scope && !string.IsNullOrWhiteSpace(scope)) scopes.Add(scope);
                }
                break;
        }
        return scopes;
    }

    [GeneratedRegex("^CHARACTER:EVE:([1-9][0-9]{0,18})$", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterSubject();
}
