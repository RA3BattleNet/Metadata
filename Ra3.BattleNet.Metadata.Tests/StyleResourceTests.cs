using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class StyleResourceTests
{
    private static string RepoMetadataDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));

    private static void SeedSchemas(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var name in new[] { SchemaValidator.SourceSchemaFileName, SchemaValidator.PublishSchemaFileName })
            File.Copy(Path.Combine(RepoMetadataDir, name), Path.Combine(dir, name), overwrite: true);
    }

    [TestMethod]
    public void Build_MissingStyleImage_WithoutOptionalSections_Fails()
    {
        AssertStyleRefsRejected(packages: false, posts: false, logoRef: "missing-logo", backgroundRef: "missing-bg");
    }

    [TestMethod]
    public void Build_MissingStyleImage_WithPackagesOnly_Fails()
    {
        AssertStyleRefsRejected(packages: true, posts: false, logoRef: "missing-logo", backgroundRef: "missing-bg");
    }

    [TestMethod]
    public void Build_MissingStyleImage_WithPackagesAndPosts_Fails()
    {
        AssertStyleRefsRejected(packages: true, posts: true, logoRef: "missing-logo", backgroundRef: "missing-bg");
    }

    [TestMethod]
    public void Build_StyleRefToManifest_WithoutOptionalSections_Fails()
    {
        AssertStyleRefsRejected(packages: false, posts: false, logoRef: "leaf", backgroundRef: "leaf");
    }

    [TestMethod]
    public void Build_StyleRefToManifest_WithPackagesOnly_Fails()
    {
        AssertStyleRefsRejected(packages: true, posts: false, logoRef: "leaf", backgroundRef: "leaf");
    }

    [TestMethod]
    public void Build_StyleRefToManifest_WithPackagesAndPosts_Fails()
    {
        AssertStyleRefsRejected(packages: true, posts: true, logoRef: "leaf", backgroundRef: "leaf");
    }

    [TestMethod]
    public void Build_InheritedStyleImages_KeepQualifiedIdsAndValues()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"style-inherit-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        SeedSchemas(src);
        Directory.CreateDirectory(Path.Combine(src, "styles"));
        try
        {
            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="styles/theme.xml" />
  </Includes>
  <Mod ID="Demo" InheritFrom="Theme">
    <CurrentVersion>1</CurrentVersion>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
  </Mod>
</Metadata>
""");
            File.WriteAllText(Path.Combine(src, "styles", "theme.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Image ID="banner" Url="https://example.com/banner.png" />
  <Image ID="bg-a" Url="https://example.com/a.png" />
  <Image ID="bg-b" Url="https://example.com/b.png" />
  <Base ID="Theme" Kind="Mod">
    <Style>
      <Logo Width="400" Height="160" Position="TopLeft" OffsetX="24" OffsetY="16">banner</Logo>
      <Background Random="true">
        <Image>bg-a</Image>
        <Image>bg-b</Image>
        <Color Format="CSS">#112233</Color>
        <SecondaryColor Format="ARGB">#FFAABBCC</SecondaryColor>
      </Background>
    </Style>
  </Base>
</Metadata>
""");

            MetadataBuilder.Build(src, dst, contentRevision: "style-inherit");
            var loaded = MetadataBuilder.Load(Path.Combine(dst, "metadata.xml"));
            var style = loaded.Mods().Single(m => m.Id == "Demo").Raw.Find("Style")!;

            var logo = style.Find("Logo")!;
            logo.Value.Should().Be("styles/theme:banner");
            logo.Get("Width").Should().Be("400");
            logo.Get("Height").Should().Be("160");
            logo.Get("Position").Should().Be("TopLeft");
            logo.Get("OffsetX").Should().Be("24");
            logo.Get("OffsetY").Should().Be("16");

            var background = style.Find("Background")!;
            background.Get("Random").Should().Be("true");
            background.Children.Where(c => c.Name == "Image").Select(c => c.Value)
                .Should().Equal("styles/theme:bg-a", "styles/theme:bg-b");
            var color = background.Find("Color")!;
            color.Value.Should().Be("#112233");
            color.Get("Format").Should().Be("CSS");
            var secondary = background.Find("SecondaryColor")!;
            secondary.Value.Should().Be("#FFAABBCC");
            secondary.Get("Format").Should().Be("ARGB");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    private static void AssertStyleRefsRejected(bool packages, bool posts, string logoRef, string backgroundRef)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"style-ref-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        SeedSchemas(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "metadata.xml"), MetadataXml(packages, posts, logoRef, backgroundRef));
            File.WriteAllText(Path.Combine(src, "leaf.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Manifest ID="leaf">
    <File Hash="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA">
      <FileName>x.bin</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
    </File>
  </Manifest>
</Metadata>
""");

            var act = () => MetadataBuilder.Build(src, dst, contentRevision: "style-ref");
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Logo*")
                .WithMessage($"*{logoRef}*")
                .WithMessage("*Background Image*")
                .WithMessage($"*{backgroundRef}*");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    private static string MetadataXml(bool packages, bool posts, string logoRef, string backgroundRef)
    {
        var packageXml = packages
            ? """
    <Packages>
      <Package Version="1.0">
        <ReleaseDate>2026-01-01</ReleaseDate>
        <Manifest>leaf</Manifest>
      </Package>
    </Packages>
"""
            : "";
        var postXml = posts
            ? """
    <Posts>
      <Post DateTime="2026-01-01T00:00:00">
        <Titles>
          <Title Language="zh-CN">公告</Title>
        </Titles>
        <Contents>
          <Content Language="zh-CN">decoy</Content>
        </Contents>
      </Post>
    </Posts>
"""
            : "";

        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="leaf.xml" />
  </Includes>
  <Image ID="decoy" Url="https://example.com/decoy.png" />
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
    <Style>
      <Logo Width="400" Height="80">{logoRef}</Logo>
      <Background Random="false">
        <Image>{backgroundRef}</Image>
      </Background>
    </Style>
{packageXml}{postXml}  </Mod>
</Metadata>
""";
    }
}
