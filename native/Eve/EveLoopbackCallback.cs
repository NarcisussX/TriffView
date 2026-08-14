using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TriffView.Eve;

internal sealed record OAuthCallback(string? Code, string? Error);

internal static class EveLoopbackCallback
{
    private static readonly TimeSpan CandidateReadTimeout = TimeSpan.FromSeconds(10);

    public static async Task<OAuthCallback> WaitAsync(
        TcpListener listener,
        Uri redirect,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            using var candidateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            candidateCts.CancelAfter(CandidateReadTimeout);
            using var abort = candidateCts.Token.Register(client.Dispose);
            try
            {
                var stream = client.GetStream();
                var request = await ParseRequestAsync(stream, redirect, candidateCts.Token);
                if (request is null) continue;
                if (!request.Value.Query.TryGetValue("state", out var returnedState)
                    || !FixedTimeEquals(expectedState, returnedState))
                {
                    await WriteReplyAsync(stream, success: false, candidateCts.Token);
                    continue;
                }

                var error = request.Value.Query.TryGetValue("error", out var errorValue) ? SafeOAuthCode(errorValue) : null;
                var code = request.Value.Query.TryGetValue("code", out var codeValue) ? codeValue : null;
                await WriteReplyAsync(stream, string.IsNullOrWhiteSpace(error) && !string.IsNullOrWhiteSpace(code), candidateCts.Token);
                return new OAuthCallback(code, error);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is IOException or InvalidDataException or UriFormatException)
            {
            }
        }
    }

    internal static async Task<(string Method, string Path, Dictionary<string, string> Query)?> ParseRequestAsync(
        Stream stream,
        Uri redirect,
        CancellationToken cancellationToken)
    {
        var requestLine = await ReadAsciiLineAsync(stream, 8_192, cancellationToken) ?? string.Empty;
        var pieces = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length != 3 || pieces[0] != "GET" || pieces[2] != "HTTP/1.1" || !pieces[1].StartsWith('/')) return null;
        var queryIndex = pieces[1].IndexOf('?');
        var query = ParseQuery(queryIndex < 0 ? string.Empty : pieces[1][queryIndex..]);

        var headerBytes = 0;
        string? host = null;
        while (true)
        {
            var line = await ReadAsciiLineAsync(stream, 8_192, cancellationToken);
            if (line is null) return null;
            if (line.Length == 0) break;
            headerBytes += line.Length + 2;
            if (headerBytes > 32 * 1024) throw new InvalidDataException("Local callback headers were too large.");

            var separator = line.IndexOf(':');
            if (separator <= 0) return null;
            var name = line[..separator].Trim();
            if (!string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (host is not null) throw new InvalidDataException("Local callback contained duplicate Host headers.");
            host = line[(separator + 1)..].Trim();
        }

        if (!string.Equals(host, redirect.Authority, StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(redirect, pieces[1], out var callback)
            || callback.Scheme != redirect.Scheme
            || callback.Port != redirect.Port
            || !string.Equals(callback.Host, redirect.Host, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(callback.AbsolutePath, redirect.AbsolutePath, StringComparison.Ordinal))
        {
            return null;
        }

        return (pieces[0], callback.AbsolutePath, query);
    }

    internal static string SafeOAuthCode(string? value)
    {
        var safe = new string((value ?? string.Empty).Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(64).ToArray());
        return safe.Length == 0 ? "oauth_error" : safe;
    }

    private static async Task<string?> ReadAsciiLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var bytes = new byte[maxBytes];
        var one = new byte[1];
        var count = 0;
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0) return count == 0 ? null : Encoding.ASCII.GetString(bytes, 0, count);
            if (one[0] == (byte)'\n')
            {
                if (count > 0 && bytes[count - 1] == (byte)'\r') count--;
                return Encoding.ASCII.GetString(bytes, 0, count);
            }
            if (one[0] is 0 or > 127) throw new InvalidDataException("Local callback contained non-ASCII request data.");
            if (count == maxBytes) throw new InvalidDataException("Local callback line exceeded its configured limit.");
            bytes[count++] = one[0];
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            EnsureValidPercentEncoding(pieces[0]);
            if (pieces.Length == 2) EnsureValidPercentEncoding(pieces[1]);
            var key = Uri.UnescapeDataString(pieces[0].Replace("+", " ", StringComparison.Ordinal));
            var value = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1].Replace("+", " ", StringComparison.Ordinal)) : string.Empty;
            if (key.Length > 128 || value.Length > 8_192) throw new InvalidDataException("Local callback query exceeded its configured limit.");
            if (!result.TryAdd(key, value)) throw new InvalidDataException("Local callback query contained a duplicate key.");
        }
        return result;
    }

    private static void EnsureValidPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
            {
                throw new InvalidDataException("Local callback query contained invalid percent encoding.");
            }
            index += 2;
        }
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static async Task WriteReplyAsync(Stream stream, bool success, CancellationToken cancellationToken)
    {
        var message = success
            ? "Authentication complete. You can close this tab and return to TriffView."
            : "Authentication was not accepted. You can close this tab and return to TriffView.";
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>TriffView</title></head><body><p>{message}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        try
        {
            await stream.WriteAsync(headers, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
        }
    }
}
