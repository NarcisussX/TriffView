using System.Drawing;

namespace TriffView.Preview;

internal readonly record struct PreviewClientIdentity(nint Handle, uint ProcessId)
{
    public static PreviewClientIdentity From(EveClientWindow client) => new(client.Handle, client.ProcessId);
}

internal sealed class TriffViewPreviewPositionMemory
{
    private readonly Dictionary<PreviewClientIdentity, Rectangle> _positions = new();
    private string _profileId = "";
    private int _previewWidth;
    private int _previewHeight;
    private ScreenPixelInfo[] _monitorTopology = Array.Empty<ScreenPixelInfo>();
    private bool _hasContext;

    public bool MatchesContext(
        string profileId,
        int previewWidth,
        int previewHeight,
        IReadOnlyList<ScreenPixelInfo> monitorTopology)
    {
        return _hasContext
            && string.Equals(_profileId, profileId, StringComparison.OrdinalIgnoreCase)
            && _previewWidth == previewWidth
            && _previewHeight == previewHeight
            && _monitorTopology.SequenceEqual(monitorTopology);
    }

    public bool BeginContext(
        string profileId,
        int previewWidth,
        int previewHeight,
        IReadOnlyList<ScreenPixelInfo> monitorTopology)
    {
        var nextTopology = monitorTopology.ToArray();
        var changed = !MatchesContext(profileId, previewWidth, previewHeight, nextTopology);

        if (!changed) return false;

        _positions.Clear();
        _profileId = profileId;
        _previewWidth = previewWidth;
        _previewHeight = previewHeight;
        _monitorTopology = nextTopology;
        _hasContext = true;
        return true;
    }

    public void Remember(PreviewClientIdentity identity, Rectangle position)
    {
        _positions[identity] = position;
    }

    public bool TryGet(PreviewClientIdentity identity, out Rectangle position)
    {
        return _positions.TryGetValue(identity, out position);
    }

    public void PurgeExcept(IReadOnlySet<PreviewClientIdentity> liveIdentities)
    {
        foreach (var identity in _positions.Keys.Where(identity => !liveIdentities.Contains(identity)).ToArray())
        {
            _positions.Remove(identity);
        }
    }

    public static Rectangle Resolve(
        Rectangle? savedForCurrentKey,
        Rectangle? rememberedForClient,
        Rectangle? titleFallback,
        Rectangle defaultStack)
    {
        return savedForCurrentKey
            ?? rememberedForClient
            ?? titleFallback
            ?? defaultStack;
    }
}
