using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class InheritanceTests
{
    private static string RepoMetadataDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));

    private static void SeedSchemas(string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var name in new[] { SchemaValidator.SourceSchemaFileName, SchemaValidator.PublishSchemaFileName })
        {
            File.Copy(Path.Combine(RepoMetadataDir, name), Path.Combine(targetDir, name), overwrite: true);
        }
    }

    [TestMethod]
    public void Merge_Controls_ChildOverridesOneField()
    {
        var root = XElement.Parse("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Controls>
                    <Label>
                      <FontSize>14</FontSize>
                      <ForegroundBrush>#FFFFFFFF</ForegroundBrush>
                    </Label>
                  </Controls>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Controls>
                    <Label>
                      <FontSize>16</FontSize>
                    </Label>
                  </Controls>
                </Style>
              </Mod>
            </Metadata>
            """);

        MetadataInheritance.Resolve(root);

        root.Elements("Base").Should().BeEmpty();
        var mod = root.Element("Mod")!;
        mod.Attribute("InheritFrom").Should().BeNull();
        mod.Element("Style")!.Element("Controls")!.Element("Label")!.Element("FontSize")!.Value.Should().Be("16");
        mod.Element("Style")!.Element("Controls")!.Element("Label")!.Element("ForegroundBrush")!.Value
            .Should().Be("#FFFFFFFF");
    }

    [TestMethod]
    public void Merge_Background_ChildReplacesList()
    {
        var root = XElement.Parse("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Background Random="true">
                    <Image>base-a</Image>
                    <Image>base-b</Image>
                  </Background>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Background Random="false">
                    <Image>child-only</Image>
                  </Background>
                </Style>
              </Mod>
            </Metadata>
            """);

        MetadataInheritance.Resolve(root);

        var bg = root.Element("Mod")!.Element("Style")!.Element("Background")!;
        bg.Attribute("Random")!.Value.Should().Be("false");
        bg.Elements("Image").Select(e => e.Value).Should().Equal("child-only");
    }

    [TestMethod]
    public void Merge_Packages_ChildReplacesAll()
    {
        var root = XElement.Parse("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Packages>
                  <Package Version="0.1">
                    <Manifest>old</Manifest>
                  </Package>
                </Packages>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Packages>
                  <Package Version="2.0">
                    <Manifest>new</Manifest>
                  </Package>
                </Packages>
              </Mod>
            </Metadata>
            """);

        MetadataInheritance.Resolve(root);

        var packages = root.Element("Mod")!.Element("Packages")!.Elements("Package").ToList();
        packages.Should().HaveCount(1);
        packages[0].Attribute("Version")!.Value.Should().Be("2.0");
        packages[0].Element("Manifest")!.Value.Should().Be("new");
    }

    [TestMethod]
    public void Fail_MissingBase()
    {
        var root = XElement.Parse("""
            <Metadata>
              <Mod ID="Child" InheritFrom="Missing">
                <CurrentVersion>1</CurrentVersion>
              </Mod>
            </Metadata>
            """);

        var act = () => MetadataInheritance.Resolve(root);
        act.Should().Throw<InvalidOperationException>().WithMessage("*找不到 Base*");
    }

    [TestMethod]
    public void Fail_KindMismatch()
    {
        var root = XElement.Parse("""
            <Metadata>
              <Base ID="AppBase" Kind="Application">
                <Version>1.0</Version>
              </Base>
              <Mod ID="Child" InheritFrom="AppBase">
                <CurrentVersion>1</CurrentVersion>
              </Mod>
            </Metadata>
            """);

        var act = () => MetadataInheritance.Resolve(root);
        act.Should().Throw<InvalidOperationException>().WithMessage("*不能继承*");
    }

    [TestMethod]
    public void Fail_BaseIdConflictsWithMod()
    {
        var root = XElement.Parse("""
            <Metadata>
              <Base ID="Same" Kind="Mod">
                <CurrentVersion>0</CurrentVersion>
              </Base>
              <Mod ID="Same">
                <CurrentVersion>1</CurrentVersion>
              </Mod>
            </Metadata>
            """);

        var act = () => MetadataInheritance.Resolve(root);
        act.Should().Throw<InvalidOperationException>().WithMessage("*冲突*");
    }

    [TestMethod]
    public void Flatten_Publish_NoBaseNoInheritFrom()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"inh-flat-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        Directory.CreateDirectory(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="base.xml" />
  </Includes>
  <Mod ID="Demo" InheritFrom="StandardMod">
    <CurrentVersion>9</CurrentVersion>
    <Icon>icon-a</Icon>
  </Mod>
  <Image ID="icon-a" Url="https://example.com/a.png" />
</Metadata>
""");
            File.WriteAllText(Path.Combine(src, "base.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Base ID="StandardMod" Kind="Mod">
    <Style>
      <Controls>
        <Label>
          <FontSize>14</FontSize>
          <ForegroundBrush>#FFFFFFFF</ForegroundBrush>
        </Label>
      </Controls>
    </Style>
  </Base>
</Metadata>
""");

            var doc = MetadataFlattener.Flatten(
                Path.Combine(src, "metadata.xml"),
                src,
                "1.0",
                "rev");

            var text = doc.ToString();
            text.Should().NotContain("<Base");
            text.Should().NotContain("InheritFrom");
            text.Should().NotContain("<Include");

            var mod = doc.Root!.Element("Mod")!;
            mod.Attribute("ID")!.Value.Should().Be("Demo");
            mod.Element("CurrentVersion")!.Value.Should().Be("9");
            mod.Element("Style")!.Element("Controls")!.Element("Label")!.Element("FontSize")!.Value
                .Should().Be("14");
            // Icon 经本文件作用域限定
            mod.Element("Icon")!.Value.Should().Contain("icon-a");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_WithPrivateBase_SucceedsAndPublishClean()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"inh-build-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        try
        {
            SeedSchemas(src);
            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="base.xml" />
    <Include Source="manifest-1.xml" />
  </Includes>
  <Image ID="icon-a" Url="https://example.com/a.png" />
  <Mod ID="Demo" InheritFrom="StandardMod">
    <CurrentVersion>3.0</CurrentVersion>
    <Icon>icon-a</Icon>
    <Packages>
      <Package Version="3.0">
        <ReleaseDate>2026-01-01</ReleaseDate>
        <Manifest>manifest-1</Manifest>
      </Package>
    </Packages>
  </Mod>
</Metadata>
""");
            File.WriteAllText(Path.Combine(src, "base.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Base ID="StandardMod" Kind="Mod">
    <Style>
      <Controls>
        <LaunchButton>
          <BorderBrush>#FF000000</BorderBrush>
        </LaunchButton>
      </Controls>
    </Style>
  </Base>
</Metadata>
""");
            File.WriteAllText(Path.Combine(src, "manifest-1.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Manifest ID="manifest-1">
    <File Hash="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA">
      <FileName>x.bin</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
    </File>
  </Manifest>
</Metadata>
""");

            MetadataBuilder.Build(src, dst, schemaVersion: "1.0", contentRevision: "inh-test");

            var flat = File.ReadAllText(Path.Combine(dst, "metadata.xml"));
            flat.Should().NotContain("<Base");
            flat.Should().NotContain("InheritFrom");
            flat.Should().Contain("Demo");
            flat.Should().Contain("BorderBrush");

            var loaded = MetadataBuilder.Load(Path.Combine(dst, "metadata.xml"));
            var mod = loaded.Mods().Single(m => m.Id == "Demo");
            mod.Version.Should().Be("3.0");
            mod.Icon.Should().Contain("icon-a");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }
}
