using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

namespace TriffView.Eve;

internal interface IEveSigningKeySource
{
    Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken cancellationToken);
}

internal sealed class EveSigningKeySource : IEveSigningKeySource
{
    internal static readonly Uri MetadataUri = new("https://login.eveonline.com/.well-known/oauth-authorization-server");
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<SecurityKey> _keys = Array.Empty<SecurityKey>();
    private DateTimeOffset _expiresUtc;
    private int _refreshVersion;

    public EveSigningKeySource(HttpClient http, TimeProvider? time = null)
    {
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && _keys.Count > 0 && _expiresUtc > _time.GetUtcNow()) return _keys;
        var observedVersion = _refreshVersion;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _keys.Count > 0 && _expiresUtc > _time.GetUtcNow()) return _keys;
            if (forceRefresh && _keys.Count > 0 && _refreshVersion != observedVersion) return _keys;

            using var metadataResponse = await _http.GetAsync(MetadataUri, cancellationToken);
            metadataResponse.EnsureSuccessStatusCode();
            var metadataText = await ReadBoundedAsync(metadataResponse.Content, 64 * 1024, cancellationToken);
            var metadata = JsonNode.Parse(metadataText)?.AsObject()
                ?? throw new InvalidDataException("EVE SSO metadata was not a JSON object.");

            var issuer = metadata["issuer"]?.GetValue<string>() ?? string.Empty;
            if (!EveJwtValidator.AcceptedIssuers.Contains(issuer))
            {
                throw new InvalidDataException("EVE SSO metadata returned an unexpected issuer.");
            }

            var jwksText = metadata["jwks_uri"]?.GetValue<string>() ?? string.Empty;
            if (!Uri.TryCreate(jwksText, UriKind.Absolute, out var jwksUri)
                || jwksUri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(jwksUri.Host, "login.eveonline.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("EVE SSO metadata returned an unexpected JWKS address.");
            }

            using var jwksResponse = await _http.GetAsync(jwksUri, cancellationToken);
            jwksResponse.EnsureSuccessStatusCode();
            var jwks = new JsonWebKeySet(await ReadBoundedAsync(jwksResponse.Content, 256 * 1024, cancellationToken));
            var keys = jwks.GetSigningKeys().Where(key => !string.IsNullOrWhiteSpace(key.KeyId)).ToArray();
            if (keys.Length == 0) throw new InvalidDataException("EVE SSO returned no usable signing keys.");

            _keys = keys;
            _expiresUtc = _time.GetUtcNow().Add(CacheDuration);
            _refreshVersion++;
            return _keys;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[limit + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        if (total > limit) throw new InvalidDataException("EVE SSO response exceeded the configured limit.");
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
