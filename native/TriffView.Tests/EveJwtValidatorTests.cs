using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using TriffView.Eve;
using Xunit;

namespace TriffView.Tests;

public class EveJwtValidatorTests : IDisposable
{
    private const string ClientId = "test-client";
    private static readonly HashSet<string> Scopes = new(StringComparer.Ordinal) { "scope.read", "scope.queue" };
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    public EveJwtValidatorTests() => _key = new RsaSecurityKey(_rsa) { KeyId = "current" };
    public void Dispose() => _rsa.Dispose();

    [Fact]
    public async Task ValidRs256TokenProducesVerifiedEveIdentity()
    {
        var validator = Validator(new StaticKeys(_key));
        var identity = await validator.ValidateAsync(Token(), CancellationToken.None);
        Assert.Equal(123456789, identity.CharacterId);
        Assert.Equal("Pilot", identity.CharacterName);
        Assert.Equal("owner-123456", identity.OwnerHash);
        Assert.True(Scopes.SetEquals(identity.Scopes));
    }

    [Fact]
    public async Task RejectsSignatureAlgorithmIssuerAudienceLifetimeAndRequiredClaims()
    {
        var validator = Validator(new StaticKeys(_key));
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() => validator.ValidateAsync(Token(issuer: "https://attacker.invalid"), CancellationToken.None));
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() => validator.ValidateAsync(Token(audiences: [ClientId]), CancellationToken.None));
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() => validator.ValidateAsync(Token(notBefore: Now.AddMinutes(-10), expires: Now.AddMinutes(-3)), CancellationToken.None));
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() => validator.ValidateAsync(Token(notBefore: Now.AddMinutes(3)), CancellationToken.None));
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() => validator.ValidateAsync(Token(includeNotBefore: false), CancellationToken.None));
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() => validator.ValidateAsync(Token(subject: "CHARACTER:OTHER:123456789"), CancellationToken.None));
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() => validator.ValidateAsync(Token(owner: ""), CancellationToken.None));
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() => validator.ValidateAsync(Token(azp: "different-client"), CancellationToken.None));
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() => validator.ValidateAsync(Token(scopes: ["scope.read"]), CancellationToken.None));

        var hmac = new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), SecurityAlgorithms.HmacSha256);
        await Assert.ThrowsAsync<SecurityTokenInvalidAlgorithmException>(() => validator.ValidateAsync(Token(credentials: hmac), CancellationToken.None));
    }

    [Fact]
    public async Task TamperedSignatureIsRejected()
    {
        var token = Token();
        var parts = token.Split('.');
        parts[1] = parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A');
        await Assert.ThrowsAnyAsync<SecurityTokenException>(() => Validator(new StaticKeys(_key)).ValidateAsync(string.Join('.', parts), CancellationToken.None));
    }

    [Fact]
    public async Task UnknownKidForcesExactlyOneKeyRefresh()
    {
        using var otherRsa = RSA.Create(2048);
        var source = new RotatingKeys(new RsaSecurityKey(otherRsa) { KeyId = "old" }, _key);
        var identity = await Validator(source).ValidateAsync(Token(), CancellationToken.None);
        Assert.Equal(123456789, identity.CharacterId);
        Assert.Equal(1, source.ForcedRefreshes);
    }

    private EveJwtValidator Validator(IEveSigningKeySource source) => new(ClientId, Scopes, source, new FixedTimeProvider(Now));

    private string Token(
        string issuer = "https://login.eveonline.com",
        string[]? audiences = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires = null,
        bool includeNotBefore = true,
        string subject = "CHARACTER:EVE:123456789",
        string owner = "owner-123456",
        string azp = ClientId,
        string[]? scopes = null,
        SigningCredentials? credentials = null)
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("name", "Pilot"),
            new("owner", owner),
            new("azp", azp),
        };
        var payload = new JwtPayload(issuer, null, claims, includeNotBefore ? (notBefore ?? Now.AddMinutes(-1)).UtcDateTime : null, (expires ?? Now.AddMinutes(20)).UtcDateTime);
        payload["aud"] = audiences ?? ["EVE Online", ClientId];
        payload["scp"] = scopes ?? Scopes.ToArray();
        var jwt = new JwtSecurityToken(new JwtHeader(credentials ?? new SigningCredentials(_key, SecurityAlgorithms.RsaSha256)), payload);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
