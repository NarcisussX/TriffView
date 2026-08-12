using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class SkillPlanParserTests
{
    [Theory]
    [InlineData("Navigation I", 1)]
    [InlineData("Navigation II", 2)]
    [InlineData("Navigation III", 3)]
    [InlineData("Navigation IV", 4)]
    [InlineData("Navigation V", 5)]
    [InlineData("Navigation iv", 4)] // Roman levels are case-insensitive
    public void ParsesRomanLevels(string line, int expected)
    {
        var plan = SkillPlanParser.Parse("p", line);
        var requirement = Assert.Single(plan.Requirements);
        Assert.Equal("Navigation", requirement.SkillName);
        Assert.Equal(expected, requirement.Level);
    }

    [Theory]
    [InlineData("Gunnery 1", 1)]
    [InlineData("Gunnery 5", 5)]
    public void ParsesNumericLevelsOneThroughFive(string line, int expected)
    {
        var plan = SkillPlanParser.Parse("p", line);
        Assert.Equal(expected, Assert.Single(plan.Requirements).Level);
    }

    [Theory]
    [InlineData("Gunnery 0")]
    [InlineData("Gunnery 6")]
    [InlineData("Gunnery 50")]
    [InlineData("Gunnery -1")]
    [InlineData("Gunnery")]
    public void RejectsLevelsOutsideOneThroughFive(string line)
    {
        Assert.Empty(SkillPlanParser.Parse("p", line).Requirements);
    }

    // Escapes rather than literal characters: a raw tab or non-breaking space in the
    // source is invisible in review and easy to "fix" into a plain space by accident.
    [Theory]
    [InlineData("Survey\tIV")]
    [InlineData("Survey\u00A0IV")]
    [InlineData("Survey \u00A0 IV")]
    public void SplitsOnNonAsciiWhitespace(string line)
    {
        var requirement = Assert.Single(SkillPlanParser.Parse("p", line).Requirements);
        Assert.Equal("Survey", requirement.SkillName);
        Assert.Equal(4, requirement.Level);
    }

    [Fact]
    public void TrimsColumnAlignedWhitespaceFromName()
    {
        var plan = SkillPlanParser.Parse("p", "Survey    IV");
        Assert.Equal("Survey", Assert.Single(plan.Requirements).SkillName);
    }

    [Fact]
    public void MergesDuplicatesCaseInsensitivelyKeepingHighestLevel()
    {
        var plan = SkillPlanParser.Parse("p", "Survey 3\nsurvey 4\nSURVEY 2");
        var requirement = Assert.Single(plan.Requirements);
        Assert.Equal("Survey", requirement.SkillName); // first-seen casing wins
        Assert.Equal(4, requirement.Level);
    }

    [Fact]
    public void SkipsBlankAndCommentLines()
    {
        var contents = "# a comment ending in a digit 3\n\n   \nNavigation V\n# another\r\n";
        var plan = SkillPlanParser.Parse("p", contents);
        Assert.Equal("Navigation", Assert.Single(plan.Requirements).SkillName);
    }

    [Fact]
    public void HandlesCrlfContent()
    {
        var plan = SkillPlanParser.Parse("p", "Navigation V\r\nGunnery 3\r\n");
        Assert.Equal(2, plan.Requirements.Count);
        Assert.Equal("Navigation", plan.Requirements[0].SkillName);
        Assert.Equal("Gunnery", plan.Requirements[1].SkillName);
    }

    [Fact]
    public void AllInvalidLinesYieldZeroRequirements()
    {
        var plan = SkillPlanParser.Parse("p", "# only a comment\nnot a requirement\nGunnery 9");
        Assert.Empty(plan.Requirements);
    }
}
