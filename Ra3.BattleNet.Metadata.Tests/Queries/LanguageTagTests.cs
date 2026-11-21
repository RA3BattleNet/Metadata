using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class LanguageTagTests
{
    [TestMethod]
    [DataRow("zh", "zh")]
    [DataRow("ZH", "zh")]
    [DataRow("zh", "zh-CN")]
    [DataRow("zh", "zh-TW")]
    [DataRow("zh", " ZH-CN ")]
    [DataRow("zh-CN", "zh-CN")]
    [DataRow("ZH-cn", "zh-CN")]
    [DataRow("en", "en")]
    [DataRow("en", "en-US")]
    [DataRow("en-US", "en-US")]
    public void Matches_CanonicalStoredTagAgainstPlainOrRegionalQuery(string candidate, string query)
    {
        LanguageTag.Matches(candidate, query).Should().BeTrue();
    }

    [TestMethod]
    [DataRow("zh-CN", "zh")]
    [DataRow("en-US", "en")]
    [DataRow("zh-TW", "zh-CN")]
    [DataRow("en", "zh")]
    [DataRow("zh", "en-US")]
    [DataRow("english", "en")]
    [DataRow("chinese", "zh")]
    [DataRow("zhCN", "zh")]
    [DataRow("", "zh")]
    [DataRow("zh", "")]
    [DataRow(null, "zh")]
    [DataRow("zh", null)]
    public void Matches_RejectsReverseDirectionOtherFamilyAndBlank(string? candidate, string? query)
    {
        LanguageTag.Matches(candidate, query).Should().BeFalse();
    }

    [TestMethod]
    public void Select_TakesFirstMatchingEntry()
    {
        var items = new[] { ("zh-CN", "区域"), ("zh", "规范"), ("en", "English") };

        LanguageTag.Select(items, item => item.Item1, "zh")!.Item2.Should().Be("规范");
        LanguageTag.Select(items, item => item.Item1, "zh-CN")!.Item2.Should().Be("区域");
        LanguageTag.Select(items, item => item.Item1, "en")!.Item2.Should().Be("English");
    }

    [TestMethod]
    public void Select_ReturnsDefaultWhenNothingMatches()
    {
        var items = new[] { ("zh", "中文"), ("en", "English") };

        LanguageTag.Select(items, item => item.Item1, "ja-JP").Item2.Should().BeNull();
        LanguageTag.Select(Array.Empty<(string, string)>(), item => item.Item1, "zh").Item2.Should().BeNull();
        LanguageTag.Select<(string, string)>(null, item => item.Item1, "zh").Item2.Should().BeNull();
    }
}
