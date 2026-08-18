using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TriffView.EveSettings;

/// <summary>
/// How a single universe/names batch request came back. ESI rejects the entire batch with
/// 404 when it contains even one id it cannot resolve, so "invalid" says nothing about which
/// id was bad, and a transient failure (timeout, network error, 5xx, error-limited) says
/// nothing about validity at all.
/// </summary>
internal enum NameBatchOutcomeKind
{
    Resolved,
    InvalidIds,
    Transient,
}

internal readonly record struct NameBatchOutcome(NameBatchOutcomeKind Kind, IReadOnlyDictionary<long, string>? Names = null)
{
    public static NameBatchOutcome Resolved(IReadOnlyDictionary<long, string> names) => new(NameBatchOutcomeKind.Resolved, names);
    public static readonly NameBatchOutcome InvalidIds = new(NameBatchOutcomeKind.InvalidIds);
    public static readonly NameBatchOutcome Transient = new(NameBatchOutcomeKind.Transient);
}

internal delegate Task<NameBatchOutcome> ResolveNameBatchAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken);

/// <summary>
/// Resolves character names against ESI's universe/names endpoint in batches, working around
/// its all-or-nothing rejection of a batch containing an invalid id. A rejected batch is
/// bisected until the bad ids are isolated; only ids proven bad in isolation are added to
/// <paramref name="knownInvalid"/> and excluded from all future calls with the same set. A
/// transient failure never bisects and never poisons the cache, since it says nothing about
/// which ids (if any) are actually bad.
/// </summary>
internal static class CharacterNameBatchResolver
{
    /// <summary>
    /// ESI returns 404 both for the invalid-ids rejection this class exists to work around and
    /// for an unrelated route/gateway failure (e.g. a dropped or renamed endpoint), which
    /// returns a plain-text "page not found" body rather than JSON. Only the former identifies
    /// bad ids; the latter must never bisect or poison the negative cache, or a routing change
    /// would permanently blacklist every character id in a batch. Match loosely on the shape
    /// (a JSON object with a non-empty "error" string) rather than the exact wording, since CCP
    /// could reword the message without changing the shape of the response.
    /// </summary>
    public static bool IsInvalidIdsBody(string body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject obj
                && obj["error"] is JsonValue value
                && value.TryGetValue<string>(out var error)
                && !string.IsNullOrWhiteSpace(error);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns a raw universe/names HTTP response into a <see cref="NameBatchOutcome"/>: this is
    /// the whole decision the resolver depends on, pulled out so it can be driven by fixed
    /// status/body pairs in tests rather than only through a live (or faked) HTTP call.
    /// </summary>
    public static NameBatchOutcome ClassifyResponse(HttpStatusCode statusCode, string body)
    {
        if (statusCode == HttpStatusCode.NotFound)
        {
            return IsInvalidIdsBody(body) ? NameBatchOutcome.InvalidIds : NameBatchOutcome.Transient;
        }
        if ((int)statusCode is < 200 or > 299) return NameBatchOutcome.Transient;

        try
        {
            if (JsonNode.Parse(body) is not JsonArray array) return NameBatchOutcome.Transient;

            var names = new Dictionary<long, string>();
            foreach (var node in array.OfType<JsonObject>())
            {
                var id = node["id"]?.GetValue<long>() ?? 0;
                var name = node["name"]?.GetValue<string>()?.Trim() ?? "";
                if (id > 0 && !string.IsNullOrWhiteSpace(name)) names[id] = name;
            }
            return NameBatchOutcome.Resolved(names);
        }
        catch (JsonException)
        {
            return NameBatchOutcome.Transient;
        }
    }

    public static async Task<IReadOnlyDictionary<long, string>> ResolveAsync(
        IReadOnlyList<long> ids,
        ISet<long> knownInvalid,
        ResolveNameBatchAsync resolveBatch,
        CancellationToken cancellationToken = default)
    {
        var names = new Dictionary<long, string>();
        var candidates = ids.Where(id => !knownInvalid.Contains(id)).Distinct().ToArray();
        if (candidates.Length == 0) return names;

        await ResolveBatchAsync(candidates, knownInvalid, resolveBatch, names, cancellationToken);
        return names;
    }

    private static async Task ResolveBatchAsync(
        IReadOnlyList<long> ids,
        ISet<long> knownInvalid,
        ResolveNameBatchAsync resolveBatch,
        Dictionary<long, string> names,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0 || cancellationToken.IsCancellationRequested) return;

        var outcome = await resolveBatch(ids, cancellationToken);
        switch (outcome.Kind)
        {
            case NameBatchOutcomeKind.Resolved:
                if (outcome.Names != null)
                {
                    foreach (var (id, name) in outcome.Names) names[id] = name;
                }
                return;

            case NameBatchOutcomeKind.Transient:
                // Leave these ids unresolved; a later refresh will simply try again.
                return;

            case NameBatchOutcomeKind.InvalidIds when ids.Count == 1:
                knownInvalid.Add(ids[0]);
                return;

            case NameBatchOutcomeKind.InvalidIds:
                var half = ids.Count / 2;
                await ResolveBatchAsync(ids.Take(half).ToArray(), knownInvalid, resolveBatch, names, cancellationToken);
                await ResolveBatchAsync(ids.Skip(half).ToArray(), knownInvalid, resolveBatch, names, cancellationToken);
                return;
        }
    }
}
