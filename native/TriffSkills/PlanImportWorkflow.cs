using System.Collections.Concurrent;
using System.Text;

namespace TriffView.TriffSkills;

internal sealed record PlanPreviewResult(
    string RequestId,
    long Revision,
    SkillPlan? Plan,
    IReadOnlyList<PlanDiagnostic> Diagnostics);

internal sealed record PlanImportCommitResult(
    bool Success,
    bool Collision,
    bool Expired,
    string Name,
    string Error);

internal sealed class PlanImportWorkflow
{
    private const int MaxBridgePayloadBytes = 600 * 1024;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);
    private readonly string _plansDirectory;
    private readonly TimeProvider _time;
    private readonly Func<IEnumerable<string>, CancellationToken, Task<Dictionary<string, string>>> _resolveNames;
    private readonly ConcurrentDictionary<string, PendingPreview> _pending = new(StringComparer.Ordinal);

    public PlanImportWorkflow(
        string plansDirectory,
        TimeProvider time,
        Func<IEnumerable<string>, CancellationToken, Task<Dictionary<string, string>>> resolveNames)
    {
        _plansDirectory = plansDirectory;
        _time = time;
        _resolveNames = resolveNames;
    }

    public async Task<PlanPreviewResult> PreviewAsync(
        string requestId,
        long revision,
        string name,
        string contents,
        CancellationToken cancellationToken)
    {
        if (contents.Length > SkillPlanParser.MaxContentCharacters)
        {
            return Invalid(requestId, revision, $"Plan exceeds {SkillPlanParser.MaxContentCharacters:N0} characters.");
        }
        if (Encoding.UTF8.GetByteCount(contents) > MaxBridgePayloadBytes)
        {
            return Invalid(requestId, revision, "Plan payload exceeds the bridge-size limit.");
        }
        if (!PlanNameValidator.TryValidate(name, out var normalizedName, out var nameError))
        {
            return Invalid(requestId, revision, nameError);
        }

        var parsed = SkillPlanParser.Parse(normalizedName, contents);
        if (!parsed.IsValid || parsed.Plan is null)
        {
            return new PlanPreviewResult(requestId, revision, null, parsed.Diagnostics);
        }

        var failures = await _resolveNames(parsed.Plan.Requirements.Select(requirement => requirement.SkillName), cancellationToken);
        if (failures.Count > 0)
        {
            return new PlanPreviewResult(
                requestId,
                revision,
                null,
                failures.Select(pair => new PlanDiagnostic(0, $"{pair.Key}: {pair.Value}")).ToArray());
        }

        Prune();
        _pending[requestId] = new PendingPreview(revision, normalizedName, contents, parsed.Plan, _time.GetUtcNow().Add(PreviewLifetime));
        return new PlanPreviewResult(requestId, revision, parsed.Plan, []);
    }

    public PlanImportCommitResult Commit(string requestId, long revision, bool replace)
    {
        Prune();
        if (!_pending.TryGetValue(requestId, out var preview) || preview.Revision != revision)
        {
            return new PlanImportCommitResult(false, false, true, string.Empty, "Validated preview expired or no longer matches the current input.");
        }

        var result = PlanStore.CommitValidated(
            _plansDirectory,
            preview.Name,
            preview.Contents,
            preview.Plan,
            replace);
        if (result.Success) _pending.TryRemove(requestId, out _);
        return new PlanImportCommitResult(result.Success, result.Collision, false, result.Name, result.Error);
    }

    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var key in _pending.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
        {
            _pending.TryRemove(key, out _);
        }
        while (_pending.Count >= 5) _pending.TryRemove(_pending.First().Key, out _);
    }

    private static PlanPreviewResult Invalid(string requestId, long revision, string message)
        => new(requestId, revision, null, [new PlanDiagnostic(0, message)]);

    private sealed record PendingPreview(
        long Revision,
        string Name,
        string Contents,
        SkillPlan Plan,
        DateTimeOffset ExpiresUtc);
}
