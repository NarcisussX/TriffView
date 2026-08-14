using System.IO;
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class PlanNameValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading space")]
    [InlineData("trailing space ")]
    [InlineData("ends with period.")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("has..dots")]
    [InlineData("..")]
    [InlineData("quote\"name")]
    public void RejectsInvalidNames(string name)
    {
        Assert.False(PlanNameValidator.TryValidate(name, out var error));
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    [InlineData("CON.txt")]
    [InlineData("CON.txt.bak")]
    public void RejectsReservedWindowsDeviceNames(string name)
    {
        Assert.False(PlanNameValidator.TryValidate(name, out _));
    }

    [Theory]
    [InlineData("Core Ship Skills")]
    [InlineData("Marauder V")]
    [InlineData("logi.alt plans")]
    public void AcceptsOrdinaryNames(string name)
    {
        Assert.True(PlanNameValidator.TryValidate(name, out var error));
        Assert.Equal("", error);
    }

    [Fact]
    public void RejectsNamesOverTheLengthLimit()
    {
        var name = new string('a', PlanNameValidator.MaxNameLength + 1);
        Assert.False(PlanNameValidator.TryValidate(name, out _));
    }

    [Fact]
    public void IsWithinAcceptsChildrenAndRejectsEscapes()
    {
        var root = Path.Combine(Path.GetTempPath(), "triffskills-tests", "plans");
        Assert.True(PlanNameValidator.IsWithin(Path.Combine(root, "plan.txt"), root));
        Assert.False(PlanNameValidator.IsWithin(root, root));
        Assert.False(PlanNameValidator.IsWithin(Path.Combine(root, "..", "escape.txt"), root));
        Assert.False(PlanNameValidator.IsWithin(root + "-sibling" + Path.DirectorySeparatorChar + "plan.txt", root));
    }

    [Fact]
    public void RejectsMalformedUnicodeWithoutThrowing()
    {
        Assert.False(PlanNameValidator.TryValidate("bad\uD800name", out _, out var error));
        Assert.Contains("Unicode", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizesUnicodeBeforeCollisionChecks()
    {
        Assert.True(PlanNameValidator.TryValidate("Cafe\u0301", out var normalized, out _));
        Assert.Equal("Café", normalized);
    }
}
