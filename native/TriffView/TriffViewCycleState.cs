namespace TriffView.Preview;

internal static class TriffViewCycleState
{
    public static int NextIndex(
        IReadOnlyList<nint> candidates,
        int direction,
        nint activeHandle,
        nint liveForeground,
        nint cursorHandle,
        bool rememberPosition = true)
    {
        if (candidates.Count == 0) return -1;

        var anchorIndex = IndexOf(candidates, activeHandle);
        if (anchorIndex < 0) anchorIndex = IndexOf(candidates, liveForeground);
        if (anchorIndex < 0 && rememberPosition) anchorIndex = IndexOf(candidates, cursorHandle);

        if (anchorIndex < 0)
        {
            return direction < 0 ? candidates.Count - 1 : 0;
        }

        var step = direction < 0 ? -1 : 1;
        return (anchorIndex + step + candidates.Count) % candidates.Count;
    }

    public static nint CursorAfterFailedActivation(IReadOnlyList<nint> candidates, nint previousCursor)
    {
        return IndexOf(candidates, previousCursor) >= 0 ? previousCursor : nint.Zero;
    }

    private static int IndexOf(IReadOnlyList<nint> candidates, nint handle)
    {
        if (handle == nint.Zero) return -1;
        for (var index = 0; index < candidates.Count; index++)
        {
            if (candidates[index] == handle) return index;
        }

        return -1;
    }
}
