using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

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
    public void Include_PublicType_VisibleToParent()
    {
        var filePath = Path.Combine(_testDataPath, "access-control-test.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        var publicElement = metadata.GetElementById("public-element");
        publicElement.Should().NotBeNull();
        publicElement!.Get("ID").Should().Be("public-element");
    }

    [TestMethod]
    public void Include_PrivateType_ExistsInTree()
    {
        var filePath = Path.Combine(_testDataPath, "access-control-test.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        metadata.GetElementById("private-element").Should().NotBeNull();
    }

    [TestMethod]
    public void Include_ParentElement_Accessible()
    {
        var filePath = Path.Combine(_testDataPath, "access-control-test.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        var parentElement = metadata.GetElementById("parent-element");
        parentElement.Should().NotBeNull();
        parentElement!.Get("ID").Should().Be("parent-element");
    }

    [TestMethod]
    public void Include_CircularReference_ThrowsException()
    {
        var filePath = Path.Combine(_testDataPath, "circular-a.xml");
        var act = () => Metadata.LoadFromFile(filePath);
        act.Should().Throw<InvalidOperationException>().WithMessage("*循环引用*");
    }

    [TestMethod]
    public void Include_HasParentReference()
    {
        var filePath = Path.Combine(_testDataPath, "access-control-test.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        var child = metadata.Children.FirstOrDefault();
        child.Should().NotBeNull();
        child!.Parent.Should().Be(metadata);
    }

    [TestMethod]
    public void Include_TypeAttribute_IsPreserved()
    {
        var filePath = Path.Combine(_testDataPath, "access-control-test.xml");
        var metadata = Metadata.LoadFromFile(filePath);
        metadata.Children.Any(c => c.IncludeType == "public").Should().BeTrue();
        metadata.Children.Any(c => c.IncludeType == "private").Should().BeTrue();
    }
}
