using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TriffView.Eve;

internal sealed record EsiResponse<T>(HttpStatusCode StatusCode, T? Value, string Error, string Method, string Path)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and <= 299;

    public void ThrowIfFailed()
    {
        if (!IsSuccess) throw new EsiException(StatusCode, $"{Method} {Path} returned {(int)StatusCode}: {Error}");
    }
}

internal sealed class EsiException(HttpStatusCode statusCode, string message) : InvalidOperationException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

internal sealed class EsiClient
{
    private static readonly Uri BaseUri = new("https://esi.evetech.net/");
    private const int MaxAttempts = 3;
    public const int MaxErrorBodyBytes = 8_192;
    public const int MaxSuccessBodyBytes = 4 * 1024 * 1024;
    public const string CompatibilityDate = "2026-08-12";

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;
    private readonly string _userAgent;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public EsiClient(
        HttpClient http,
        JsonSerializerOptions json,
        string userAgent,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _json = json;
        _userAgent = userAgent;
        _delay = delay ?? Task.Delay;
    }

    public async Task<EsiResponse<T>> SendAsync<T>(
        HttpMethod method,
        string versionedPath,
        string? accessToken,
        object? body = null,
        CancellationToken cancellationToken = default)
    {
        var uri = ValidateAndBuildUri(versionedPath);
        var bodyJson = body == null ? null : JsonSerializer.Serialize(body, _json);
        Exception? lastNetworkError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.UserAgent.ParseAdd(_userAgent);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("X-Compatibility-Date", CompatibilityDate);
                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }
                if (bodyJson != null)
                {
                    request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                }

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var text = await ReadLimitedBodyAsync(
                    response.Content,
                    response.IsSuccessStatusCode ? MaxSuccessBodyBytes : MaxErrorBodyBytes,
                    rejectOversize: response.IsSuccessStatusCode,
                    cancellationToken);
                var error = response.IsSuccessStatusCode ? string.Empty : ReadError(text);
                if (!response.IsSuccessStatusCode)
                {
                    error = AppendRateLimit(error, response);
                }

                if (!response.IsSuccessStatusCode && ShouldRetry(method, versionedPath, response.StatusCode, attempt))
                {
                    await _delay(RetryDelay(response, attempt), cancellationToken);
                    continue;
                }

                T? value = default;
                if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(text) && typeof(T) != typeof(object))
                {
                    value = JsonSerializer.Deserialize<T>(text, _json)
                        ?? throw new InvalidDataException("ESI returned an empty JSON value.");
                }

                return new EsiResponse<T>(response.StatusCode, value, error, method.Method, versionedPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsTransientNetworkFailure(exception))
            {
                lastNetworkError = exception;
                if (attempt < MaxAttempts)
                {
                    await _delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
                    continue;
                }
            }
        }

        return new EsiResponse<T>(
            HttpStatusCode.ServiceUnavailable,
            default,
            Sanitize(lastNetworkError?.Message ?? "ESI request failed after bounded retries."),
            method.Method,
            versionedPath);
    }

    internal static Uri ValidateAndBuildUri(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !path.StartsWith("/v", StringComparison.Ordinal)
            || Uri.TryCreate(path, UriKind.Absolute, out _)
            || path.Contains("\\", StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("ESI requests must use an internally constructed, versioned relative path.", nameof(path));
        }

        return new Uri(BaseUri, path.TrimStart('/'));
    }

    internal static string ReadError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "No response body.";
        try
        {
            var node = JsonNode.Parse(text)?.AsObject();
            var remote = node?["error"]?.GetValue<string>();
            return Sanitize(string.IsNullOrWhiteSpace(remote) ? text : remote);
        }
        catch
        {
            return Sanitize(text);
        }
    }

    private static bool ShouldRetry(HttpMethod method, string path, HttpStatusCode status, int attempt)
    {
        if (attempt >= MaxAttempts) return false;
        var code = (int)status;
        if (code is not (408 or 420 or 429 or 500 or 502 or 503 or 504)) return false;
        if (method == HttpMethod.Get || method == HttpMethod.Put || method == HttpMethod.Delete) return true;
        return method == HttpMethod.Post && path.StartsWith("/v3/universe/ids/", StringComparison.Ordinal);
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delta;
        }
        if (TryHeaderSeconds(response, "X-Esi-Error-Limit-Reset", out var reset))
        {
            return TimeSpan.FromSeconds(Math.Min(reset, 30));
        }
        return TimeSpan.FromMilliseconds(650 * attempt);
    }

    private static string AppendRateLimit(string error, HttpResponseMessage response)
    {
        var values = new List<string>();
        foreach (var name in new[] { "X-Esi-Error-Limit-Remain", "X-Esi-Error-Limit-Reset", "Retry-After" })
        {
            if (response.Headers.TryGetValues(name, out var headerValues))
            {
                values.Add($"{name}={Sanitize(string.Join(",", headerValues))}");
            }
        }
        return values.Count == 0 ? error : Sanitize($"{error} ({string.Join("; ", values)})");
    }

    private static bool TryHeaderSeconds(HttpResponseMessage response, string name, out int seconds)
    {
        seconds = 0;
        return response.Headers.TryGetValues(name, out var values)
            && int.TryParse(values.FirstOrDefault(), out seconds)
            && seconds > 0;
    }

    private static async Task<string> ReadLimitedBodyAsync(
        HttpContent content,
        int maximumBytes,
        bool rejectOversize,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[maximumBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        if (total > maximumBytes && rejectOversize) throw new InvalidDataException($"ESI response exceeded the {maximumBytes:N0}-byte limit.");
        var length = Math.Min(total, maximumBytes);
        return Encoding.UTF8.GetString(buffer, 0, length) + (total > maximumBytes ? "... [truncated]" : string.Empty);
    }

    private static string Sanitize(string value)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(character => character is '\r' or '\n' or '\t' || !char.IsControl(character))
            .Take(2_048)
            .ToArray())
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return cleaned.Length == 0 ? "Remote service returned an unreadable error." : cleaned;
    }

    private static bool IsTransientNetworkFailure(Exception exception)
        => exception is HttpRequestException or TaskCanceledException or SocketException or IOException;
}
