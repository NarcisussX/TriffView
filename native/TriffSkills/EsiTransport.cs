using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TriffView.TriffSkills;

// TriffSkills' own copy of Fleet Manager's ESI transport and retry policy
// (TriffFleetsController.SendEsiAsync), kept private so this proof of concept adds
// files without modifying Fleet Manager. If TriffSkills is accepted, unifying the two
// transports into a shared class is a natural follow-up.
internal static class EsiTransport
{
    public const string EsiBaseUrl = "https://esi.evetech.net/latest";

    private const int EsiTransientMaxAttempts = 3;
    private const int EsiTransientBaseDelayMs = 650;

    public static async Task<EsiResponse<T>> SendAsync<T>(
        HttpClient http,
        JsonSerializerOptions jsonOptions,
        string userAgent,
        HttpMethod method,
        string path,
        string? token,
        object? body = null)
    {
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"{EsiBaseUrl}{path}";
        var bodyJson = body == null ? null : JsonSerializer.Serialize(body, jsonOptions);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= EsiTransientMaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.UserAgent.ParseAdd(userAgent);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                if (bodyJson != null)
                {
                    request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                }

                using var response = await http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                var error = response.IsSuccessStatusCode ? "" : ReadError(text);
                var esiRemain = HeaderValue(response, "X-Esi-Error-Limit-Remain");
                var esiReset = HeaderValue(response, "X-Esi-Error-Limit-Reset");
                var retryAfter = HeaderValue(response, "Retry-After");
                if (!string.IsNullOrWhiteSpace(esiRemain) || !string.IsNullOrWhiteSpace(esiReset) || !string.IsNullOrWhiteSpace(retryAfter))
                {
                    error = $"{error} ESI error limit remain={esiRemain}, reset={esiReset}, retry-after={retryAfter}".Trim();
                }

                if (!response.IsSuccessStatusCode && ShouldRetryEsi(method, path, response.StatusCode, attempt))
                {
                    await Task.Delay(RetryDelay(attempt, retryAfter));
                    continue;
                }

                T? value = default;
                if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(text) && typeof(T) != typeof(object))
                {
                    value = JsonSerializer.Deserialize<T>(text, jsonOptions);
                }

                return new EsiResponse<T>(response.StatusCode, value, error, method.Method, path);
            }
            catch (Exception ex) when (IsTransientNetworkException(ex) && attempt < EsiTransientMaxAttempts)
            {
                lastException = ex;
                await Task.Delay(RetryDelay(attempt, ""));
            }
            catch (Exception ex) when (IsTransientNetworkException(ex))
            {
                lastException = ex;
                break;
            }
        }

        return new EsiResponse<T>(
            HttpStatusCode.ServiceUnavailable,
            default,
            lastException?.Message ?? "ESI request failed after transient retries.",
            method.Method,
            path
        );
    }

    private static bool ShouldRetryEsi(HttpMethod method, string path, HttpStatusCode statusCode, int attempt)
    {
        if (attempt >= EsiTransientMaxAttempts) return false;

        var status = (int)statusCode;
        var transient = status is 408 or 420 or 429 or 500 or 502 or 503 or 504;
        if (!transient) return false;

        if (method == HttpMethod.Get || method == HttpMethod.Put) return true;
        return method == HttpMethod.Post && path.StartsWith("/universe/ids/", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan RetryDelay(int attempt, string retryAfter)
    {
        if (int.TryParse(retryAfter, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, 8));
        }

        return TimeSpan.FromMilliseconds(EsiTransientBaseDelayMs * attempt);
    }

    private static bool IsTransientNetworkException(Exception ex)
    {
        return ex is HttpRequestException or TaskCanceledException or SocketException;
    }

    private static string HeaderValue(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";
    }

    public static string ReadError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "No response body.";
        try
        {
            var node = JsonNode.Parse(text)?.AsObject();
            return node?["error"]?.GetValue<string>() ?? text;
        }
        catch
        {
            return text;
        }
    }
}

internal sealed record EsiResponse<T>(HttpStatusCode StatusCode, T? Value, string Error, string Method, string Path)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and <= 299;

    public void ThrowIfFailed()
    {
        if (!IsSuccess)
        {
            throw new InvalidOperationException($"{Method} {Path} returned {(int)StatusCode}: {Error}");
        }
    }
}
