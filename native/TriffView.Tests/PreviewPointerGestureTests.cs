using System.Drawing;
using TriffView.Preview;
using Xunit;

namespace TriffView.Tests;

/// <summary>
/// Clicking a preview is how you switch to that client. Before this, any click whose cursor
/// moved more than four pixels in total was reclassified as a drag: the client did not switch
/// and the preview was quietly nudged instead. Users reported it as "if your mouse isn't
/// perfectly still, it won't take the command and switch".
/// </summary>
public class PreviewPointerGestureTests
{
    // Windows' default SM_CXDRAG / SM_CYDRAG.
    private static readonly Size DefaultDragSize = new(4, 4);

    [Fact]
    public void APressAndReleaseAtTheSamePointIsAClick()
    {
        Assert.True(PreviewPointerGesture.IsClick(new Point(500, 500), new Point(500, 500), DefaultDragSize));
    }

    [Theory]
    [InlineData(4, 0)]   // exactly at the limit on one axis
    [InlineData(0, 4)]
    [InlineData(4, 4)]   // at the limit on both — a diamond test would have rejected this
    [InlineData(3, 3)]
    [InlineData(-4, -4)] // tolerance is symmetric
    public void SmallMovementDuringAClickStillCounts(int dx, int dy)
    {
        var start = new Point(500, 500);
        var end = new Point(500 + dx, 500 + dy);

        Assert.True(PreviewPointerGesture.IsClick(start, end, DefaultDragSize));
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(0, 5)]
    [InlineData(-9, 2)]
    [InlineData(120, 80)]
    public void MovementBeyondTheDragSizeIsADrag(int dx, int dy)
    {
        var start = new Point(500, 500);
        var end = new Point(500 + dx, 500 + dy);

        Assert.False(PreviewPointerGesture.IsClick(start, end, DefaultDragSize));
    }

    [Fact]
    public void ACursorThatWandersAndReturnsIsStillAClick()
    {
        // The regression that matters. The old code latched into drag mode the moment peak
        // movement crossed the threshold and never reconsidered, so a click that wobbled 200px
        // away and came back to the exact starting pixel was swallowed. Judging net
        // displacement makes the wandering irrelevant.
        var start = new Point(500, 500);
        var end = new Point(500, 500);

        Assert.True(PreviewPointerGesture.IsClick(start, end, DefaultDragSize));
    }

    [Fact]
    public void PerAxisToleranceIsNotATotalBudget()
    {
        // 4 across and 4 down is within tolerance on each axis, but sums to 8. The previous
        // Manhattan test (|dx| + |dy| > 4) rejected it, which is why ordinary clicks failed.
        Assert.True(PreviewPointerGesture.IsClick(new Point(0, 0), new Point(4, 4), DefaultDragSize));
    }

    [Fact]
    public void ALargerSystemDragSizeIsHonored()
    {
        // Accessibility settings and some drivers raise the drag metrics; the check must follow
        // whatever the system reports rather than assume 4.
        var generous = new Size(16, 16);

        Assert.True(PreviewPointerGesture.IsClick(new Point(0, 0), new Point(15, 15), generous));
        Assert.False(PreviewPointerGesture.IsClick(new Point(0, 0), new Point(17, 0), generous));
    }

    [Fact]
    public void AZeroDragSizeStillTreatsAMotionlessPressAsAClick()
    {
        // Defensive: a zero or nonsensical metric must not make every click impossible.
        Assert.True(PreviewPointerGesture.IsClick(new Point(7, 9), new Point(7, 9), Size.Empty));
        Assert.False(PreviewPointerGesture.IsClick(new Point(7, 9), new Point(8, 9), Size.Empty));
    }

    [Fact]
    public void ANegativeDragSizeIsTreatedAsZeroRatherThanInverting()
    {
        Assert.True(PreviewPointerGesture.IsClick(new Point(0, 0), new Point(0, 0), new Size(-5, -5)));
        Assert.False(PreviewPointerGesture.IsClick(new Point(0, 0), new Point(1, 0), new Size(-5, -5)));
    }

    // With previews locked, a fast real click travels far more than SM_CXDRAG between press and
    // release, so net-displacement checks like IsClick reject it and the click does nothing —
    // users reported this as "clicking a preview to switch characters doesn't work" whenever
    // their mouse was in motion. Locked mode decides on release position instead.

    [Fact]
    public void ReleaseInsideThePressedFrameActivatesEvenAfterALongFastTravel()
    {
        var pressedFrame = new Rectangle(100, 100, 200, 150);

        // ~80px of net displacement in the time a real click takes at ordinary cursor speed —
        // far beyond the 4px drag metric, but the release is still over the pressed preview.
        var release = new Point(180, 180);

        Assert.True(PreviewPointerGesture.IsLockedReleaseActivation(pressedFrame, release));
    }

    [Fact]
    public void ReleaseJustOutsideThePressedFrameDoesNotActivate()
    {
        var pressedFrame = new Rectangle(100, 100, 200, 150);

        // One pixel past the right edge: the user dragged off the preview before releasing.
        var release = new Point(301, 150);

        Assert.False(PreviewPointerGesture.IsLockedReleaseActivation(pressedFrame, release));
    }

    [Fact]
    public void ReleaseExactlyOnTheFrameEdgeFollowsRectangleContains()
    {
        var pressedFrame = new Rectangle(100, 100, 200, 150);

        // Rectangle.Contains treats the right/bottom edge as exclusive (Left <= x < Right), so
        // the top-left corner is in and the bottom-right corner is out. Asserted here rather
        // than fought, per the pattern this file already follows for degenerate drag sizes.
        Assert.True(PreviewPointerGesture.IsLockedReleaseActivation(pressedFrame, new Point(100, 100)));
        Assert.False(PreviewPointerGesture.IsLockedReleaseActivation(pressedFrame, new Point(300, 250)));
    }

    [Fact]
    public void AMotionlessPressStillActivatesWhenLocked()
    {
        var pressedFrame = new Rectangle(100, 100, 200, 150);
        var release = new Point(150, 150);

        Assert.True(PreviewPointerGesture.IsLockedReleaseActivation(pressedFrame, release));
    }
}
