using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using TriffView.Eve;

namespace TriffView.Tests;

public class EveJwtValidatorTests : IDisposable
{
    private const string ClientId = "test-client";
    private static readonly HashSet<string> Scopes = new(StringComparer.Ordinal) { "scope.read", "scope.queue" };
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    public EveJwtValidatorTests() => _key = new RsaSecurityKey(_rsa) { KeyId = "current" };
    public void Dispose() => _rsa.Dispose();

    [Theory]
    [InlineData("https://login.eveonline.com")]
    [InlineData("https://login.eveonline.com/")]
    [InlineData("login.eveonline.com")]
    public async Task ValidTokenWithoutNbfUsesAcceptedIssuer(string issuer)
    {
        var identity = await Validator(new StaticKeys(_key)).ValidateAsync(Token(issuer: issuer, includeNotBefore: false), CancellationToken.None);
        Assert.Equal(123456789, identity.CharacterId);
        Assert.Equal("Pilot", identity.CharacterName);
        Assert.Equal("owner-123456", identity.OwnerHash);
        Assert.True(Scopes.SetEquals(identity.Scopes));
    }

    [Fact]
    public async Task FutureNbfIsRejected()
        => await Reject(Token(notBefore: DateTimeOffset.UtcNow.AddMinutes(5)));

    [Fact]
    public async Task ExpiredTokenIsRejected()
        => await Reject(Token(expires: DateTimeOffset.UtcNow.AddMinutes(-5)));

    [Fact]
    public async Task MissingExpirationIsRejected()
        => await Reject(Token(includeExpiration: false));

    [Fact]
    public async Task WrongIssuerIsRejected()
        => await Reject(Token(issuer: "https://attacker.invalid"));

    [Fact]
    public async Task MissingEveAudienceIsRejected()
        => await Reject(Token(audiences: [ClientId]));

    [Fact]
    public async Task MissingClientAudienceIsRejected()
        => await Reject(Token(audiences: ["EVE Online"]));

    [Fact]
    public async Task WrongAlgorithmIsRejected()
    {
        var hmac = new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), SecurityAlgorithms.HmacSha256);
        await Assert.ThrowsAsync<SecurityTokenInvalidAlgorithmException>(
            () => Validator(new StaticKeys(_key)).ValidateAsync(Token(credentials: hmac), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidSignatureIsRejected()
    {
        var parts = Token().Split('.');
        parts[1] = parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A');
        await Reject(string.Join('.', parts));
    }

    [Fact]
    public async Task MissingRequiredScopeIsRejected()
        => await Reject(Token(scopes: ["scope.read"]));

    [Fact]
    public async Task AzpIsOptionalAndValidatedWhenPresent()
    {
        await Validator(new StaticKeys(_key)).ValidateAsync(Token(includeAzp: false), CancellationToken.None);
        await Validator(new StaticKeys(_key)).ValidateAsync(Token(azp: ClientId), CancellationToken.None);
        await Reject(Token(azp: "different-client"));
    }

    [Fact]
    public async Task OwnerIsOptionalAndValidatedWhenPresent()
    {
        var identity = await Validator(new StaticKeys(_key)).ValidateAsync(Token(includeOwner: false), CancellationToken.None);
        Assert.Null(identity.OwnerHash);
        await Reject(Token(owner: "bad"));
    }

    [Fact]
    public async Task CharacterSubjectAndNameAreRequired()
    {
        await Reject(Token(subject: "CHARACTER:OTHER:123456789"));
        await Reject(Token(includeName: false));
        await Reject(Token(name: " \t "));
    }

    [Fact]
    public async Task SigningKeyRolloverRefreshesOnce()
    {
        using var oldRsa = RSA.Create(2048);
        var source = new RotatingKeys(new RsaSecurityKey(oldRsa) { KeyId = "old" }, _key);
        var identity = await Validator(source).ValidateAsync(Token(), CancellationToken.None);
        Assert.Equal(123456789, identity.CharacterId);
        Assert.Equal(1, source.ForcedRefreshes);
    }

    private Task Reject(string token)
        => Assert.ThrowsAnyAsync<SecurityTokenException>(() => Validator(new StaticKeys(_key)).ValidateAsync(token, CancellationToken.None));

    private EveJwtValidator Validator(IEveSigningKeySource source) => new(ClientId, Scopes, source);

    private string Token(
        string issuer = "https://login.eveonline.com",
        string[]? audiences = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires = null,
        bool includeNotBefore = true,
        bool includeExpiration = true,
        string subject = "CHARACTER:EVE:123456789",
        string name = "Pilot",
        bool includeName = true,
        string owner = "owner-123456",
        bool includeOwner = true,
        string azp = ClientId,
        bool includeAzp = true,
        string[]? scopes = null,
        SigningCredentials? credentials = null)
    {
        var payload = new JwtPayload
        {
            ["iss"] = issuer,
            ["aud"] = audiences ?? ["EVE Online", ClientId],
            ["sub"] = subject,
            ["scp"] = scopes ?? Scopes.ToArray(),
        };
        if (includeName) payload["name"] = name;
        if (includeOwner) payload["owner"] = owner;
        if (includeAzp) payload["azp"] = azp;
        if (includeNotBefore) payload["nbf"] = (notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-1)).ToUnixTimeSeconds();
        if (includeExpiration) payload["exp"] = (expires ?? DateTimeOffset.UtcNow.AddMinutes(20)).ToUnixTimeSeconds();
        var header = new JwtHeader(credentials ?? new SigningCredentials(_key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private sealed class StaticKeys(params SecurityKey[] keys) : IEveSigningKeySource
    {
        public Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SecurityKey>>(keys);
    }

    private sealed class RotatingKeys(SecurityKey first, SecurityKey refreshed) : IEveSigningKeySource
    {
        public int ForcedRefreshes { get; private set; }

        public Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            if (forceRefresh) ForcedRefreshes++;
            return Task.FromResult<IReadOnlyList<SecurityKey>>([forceRefresh ? refreshed : first]);
        }
    }
}
