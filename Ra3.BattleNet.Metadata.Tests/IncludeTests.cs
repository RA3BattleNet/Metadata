using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// Load 侧拒绝源树文件（含 Include/Module）。Include 展平语义由构建侧覆盖（StageATests）。
/// </summary>
[TestClass]
public class IncludeTests
{
    private string _testDataPath = null!;

    [TestInitialize]
    public void Init()
    {
        _testDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");
    }

    [TestMethod]
    public void LoadFromFile_WithInclude_Throws()
    {
        var filePath = Path.Combine(_testDataPath, "source-with-include.xml");
        var act = () => Metadata.LoadFromFile(filePath);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Include*");
    }

    [TestMethod]
    public void LoadFromFile_WithIncludesContainer_Throws()
    {
        var filePath = Path.Combine(_testDataPath, "source-with-includes-container.xml");
        var act = () => Metadata.LoadFromFile(filePath);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Includes*");
    }
}
