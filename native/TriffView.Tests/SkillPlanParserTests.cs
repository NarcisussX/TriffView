using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class SkillPlanParserTests
{
    [Theory]
    [InlineData("Navigation I", 1)]
    [InlineData("Navigation iv", 4)]
    [InlineData("Navigation 5", 5)]
    [InlineData("Survey\tIII", 3)]
    [InlineData("Survey\u00A0II", 2)]
    public void ParsesSupportedNativeLines(string line, int expected)
    {
        var result = SkillPlanParser.Parse("p", line);
        Assert.True(result.IsValid);
        var requirement = Assert.Single(result.Plan!.Requirements);
        Assert.Equal(expected, requirement.Level);
    }

    [Theory]
    [InlineData("Gunnery")]
    [InlineData("Gunnery 0")]
    [InlineData("Gunnery VI")]
    [InlineData("Gunnery -1")]
    [InlineData("just words here")]
    public void EveryMalformedNonCommentLineRejectsThePlan(string line)
    {
        var result = SkillPlanParser.Parse("p", $"Navigation V\n{line}\n");
        Assert.False(result.IsValid);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Line == 2);
    }

    [Fact]
    public void MergesDuplicatesAtHighestLevelAndKeepsFirstCasing()
    {
        var result = SkillPlanParser.Parse("p", "Survey 3\nsurvey IV\nSURVEY 2");
        var requirement = Assert.Single(result.Plan!.Requirements);
        Assert.Equal("Survey", requirement.SkillName);
        Assert.Equal(4, requirement.Level);
    }

    [Fact]
    public void CommentsBlankLinesAndCrlfAreAccepted()
    {
        var result = SkillPlanParser.Parse("p", "# comment\r\n\r\nNavigation V\r\nGunnery 3\r\n");
        Assert.True(result.IsValid);
        Assert.Equal(2, result.Plan!.Requirements.Count);
    }

    [Fact]
    public void EmptyOrCommentOnlyPlanIsRejected()
    {
        var result = SkillPlanParser.Parse("p", "# no requirements\n");
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Line == 0);
    }

    [Fact]
    public void EnforcesContentLineAndRequirementLimits()
    {
        Assert.False(SkillPlanParser.Parse("p", new string('x', SkillPlanParser.MaxContentCharacters + 1)).IsValid);
        Assert.False(SkillPlanParser.Parse("p", string.Join('\n', Enumerable.Repeat("#", SkillPlanParser.MaxLines + 1))).IsValid);
        Assert.False(SkillPlanParser.Parse("p", new string('x', SkillPlanParser.MaxLineCharacters + 1) + " I").IsValid);
        var tooMany = string.Join('\n', Enumerable.Range(1, SkillPlanParser.MaxRequirements + 1).Select(index => $"Skill {index} I"));
        Assert.False(SkillPlanParser.Parse("p", tooMany).IsValid);
    }

    [Fact]
    public void RejectsMalformedUnicodeSkillNames()
    {
        var result = SkillPlanParser.Parse("p", "Bad\uD800Skill I");
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Unicode", StringComparison.Ordinal));
    }
}
