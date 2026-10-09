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
                    <PrimaryLabel>
                      <FontSize>14</FontSize>
                      <Color>#FFFFFF</Color>
                    </PrimaryLabel>
                  </Controls>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Controls>
                    <PrimaryLabel>
                      <FontSize>16</FontSize>
                    </PrimaryLabel>
                  </Controls>
                </Style>
              </Mod>
            </Metadata>
            """);

        MetadataInheritance.Resolve(root);

        root.Elements("Base").Should().BeEmpty();
        var mod = root.Element("Mod")!;
        mod.Attribute("InheritFrom").Should().BeNull();
        var label = mod.Element("Style")!.Element("Controls")!.Element("PrimaryLabel")!;
        label.Element("FontSize")!.Value.Should().Be("16");
        ShouldBeColor(label, "Color", "#FFFFFF");
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
    public void Merge_ApplicationTransferAd_KeepsSchemaOrderBeforePackages()
    {
        var root = XElement.Parse("""
            <Metadata>
              <Base ID="StandardApp" Kind="Application">
                <Version>1.0</Version>
              </Base>
              <Application ID="Child" InheritFrom="StandardApp">
                <Version>2.0</Version>
                <TransferAd>true</TransferAd>
                <Packages>
                  <Package Version="2.0">
                    <Manifest>m</Manifest>
                  </Package>
                </Packages>
              </Application>
            </Metadata>
            """);

        MetadataInheritance.Resolve(root);

        var app = root.Element("Application")!;
        app.Attribute("InheritFrom").Should().BeNull();
        // 合并后必须仍按发布 schema 顺序：Version、TransferAd、Packages
        app.Elements().Select(e => e.Name.LocalName).Should().Equal("Version", "TransferAd", "Packages");
        app.Element("Version")!.Value.Should().Be("2.0");
        app.Element("TransferAd")!.Value.Should().Be("true");
    }

    [TestMethod]
    public void Merge_DisplayNames_ChildKeepsAllAndNeverInheritsBase()
    {
        var root = XElement.Parse("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <DisplayName Language="en">Base Name</DisplayName>
                <DisplayName Language="ja-JP">ベース</DisplayName>
                <Style>
                  <Controls><PrimaryLabel><FontSize>14</FontSize></PrimaryLabel></Controls>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <CurrentVersion>1</CurrentVersion>
                <DisplayName Language="zh">子名</DisplayName>
                <DisplayName Language="en">Child Name</DisplayName>
              </Mod>
            </Metadata>
            """);

        MetadataInheritance.Resolve(root);

        var mod = root.Element("Mod")!;
        // 显示名属于实体自身：保留子实体的全部节点（顺序不变），绝不从 Base 继承
        mod.Elements("DisplayName")
            .Select(e => (Lang: e.Attribute("Language")!.Value, Text: e.Value))
            .Should().Equal(("zh", "子名"), ("en", "Child Name"));
        mod.Elements().Select(e => e.Name.LocalName)
            .Should().Equal("CurrentVersion", "DisplayName", "DisplayName", "Style");
        mod.Element("Style").Should().NotBeNull("样式仍从 Base 继承");
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
        <PrimaryLabel>
          <FontSize>14</FontSize>
          <Color>#FFFFFF</Color>
        </PrimaryLabel>
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

            doc.Descendants("Base").Should().BeEmpty();
            doc.Descendants("Include").Should().BeEmpty();
            doc.Descendants("Includes").Should().BeEmpty();
            doc.Descendants().Attributes("InheritFrom").Should().BeEmpty();

            var mod = doc.Root!.Element("Mod")!;
            mod.Attribute("ID")!.Value.Should().Be("Demo");
            mod.Element("CurrentVersion")!.Value.Should().Be("9");
            var label = mod.Element("Style")!.Element("Controls")!.Element("PrimaryLabel")!;
            label.Element("FontSize")!.Value.Should().Be("14");
            ShouldBeColor(label, "Color", "#FFFFFF");
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
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
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
        <PrimaryButton>
          <BorderColor>#000000</BorderColor>
        </PrimaryButton>
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

            var loaded = MetadataBuilder.Load(Path.Combine(dst, "metadata.xml"));
            loaded.GetAllElements("Base").Should().BeEmpty();
            loaded.GetAllElements("Include").Should().BeEmpty();
            loaded.GetAllElements("Includes").Should().BeEmpty();
            loaded.Get("InheritFrom").Should().BeNull();
            loaded.GetAllElements("Mod").Concat(loaded.GetAllElements("Application"))
                .Should().OnlyContain(node => node.Get("InheritFrom") == null);

            var mod = loaded.Mods().Single(m => m.Id == "Demo");
            mod.Version.Should().Be("3.0");
            mod.Icon.Should().Contain("icon-a");
            var border = mod.Raw.Find("Style:Controls:PrimaryButton:BorderColor");
            border.Should().NotBeNull();
            border!.Value.Should().Be("#000000");
            border.Get("Format").Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Merge_Buttons_PartialStateInheritsByName()
    {
        var mod = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Controls>
                    <PrimaryButton>
                      <BorderColor Format="ARGB">#FF010203</BorderColor>
                      <BorderWidth>2</BorderWidth>
                      <BackgroundColor>#112233</BackgroundColor>
                      <Color>#AABBCC</Color>
                      <FontSize>16</FontSize>
                      <FontWeight>600</FontWeight>
                      <Hover>
                        <BorderColor>#445566</BorderColor>
                        <BorderWidth>3</BorderWidth>
                        <BackgroundColor Format="ARGB">#80AABBCC</BackgroundColor>
                        <FontSize>17</FontSize>
                        <FontWeight>700</FontWeight>
                      </Hover>
                      <Active>
                        <BorderColor>#101112</BorderColor>
                        <BorderWidth>4</BorderWidth>
                        <BackgroundColor>#202122</BackgroundColor>
                        <Color>#303132</Color>
                        <FontSize>18</FontSize>
                        <FontWeight>800</FontWeight>
                      </Active>
                    </PrimaryButton>
                    <SecondaryButton>
                      <BorderColor>#556677</BorderColor>
                      <BorderWidth>5</BorderWidth>
                      <BackgroundColor Format="ARGB">#FF998877</BackgroundColor>
                      <Color>#121212</Color>
                      <FontSize>14</FontSize>
                      <FontWeight>400</FontWeight>
                      <Hover>
                        <BackgroundColor>#AAAAAA</BackgroundColor>
                        <BorderColor Format="ARGB">#CC010101</BorderColor>
                        <BorderWidth>6</BorderWidth>
                        <Color>#BBBBBB</Color>
                        <FontSize>15</FontSize>
                        <FontWeight>500</FontWeight>
                      </Hover>
                      <Active>
                        <BorderColor>#CCCCCC</BorderColor>
                        <BorderWidth>7</BorderWidth>
                        <Color>#EEEEEE</Color>
                        <FontSize>13</FontSize>
                        <FontWeight>300</FontWeight>
                      </Active>
                    </SecondaryButton>
                  </Controls>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Controls>
                    <SecondaryButton>
                      <FontWeight>900</FontWeight>
                      <Active>
                        <FontSize>19</FontSize>
                      </Active>
                      <Hover>
                        <BorderColor Format="CSS">#80FFFFFF</BorderColor>
                      </Hover>
                    </SecondaryButton>
                    <PrimaryButton>
                      <Active>
                        <BorderWidth>1</BorderWidth>
                      </Active>
                      <Hover>
                        <FontWeight>500</FontWeight>
                        <BackgroundColor Format="CSS">#FFD633</BackgroundColor>
                      </Hover>
                      <FontSize>20</FontSize>
                    </PrimaryButton>
                  </Controls>
                </Style>
              </Mod>
            </Metadata>
            """);

        var primary = ControlOf(mod, "PrimaryButton");
        primary.Element("FontSize")!.Value.Should().Be("20");
        ShouldBeColor(primary, "Color", "#AABBCC");
        ShouldBeColor(primary, "BorderColor", "#FF010203", "ARGB");
        var primaryHover = primary.Element("Hover")!;
        ShouldHaveFields(primaryHover, "BorderColor", "BorderWidth", "BackgroundColor", "FontSize", "FontWeight");
        ShouldBeColor(primaryHover, "BackgroundColor", "#FFD633", "CSS");
        ShouldBeColor(primaryHover, "BorderColor", "#445566");
        primaryHover.Element("FontWeight")!.Value.Should().Be("500");
        primaryHover.Element("FontSize")!.Value.Should().Be("17");
        primaryHover.Element("Color").Should().BeNull();
        var primaryActive = primary.Element("Active")!;
        primaryActive.Element("BorderWidth")!.Value.Should().Be("1");
        ShouldBeColor(primaryActive, "BackgroundColor", "#202122");
        ShouldBeColor(primaryActive, "Color", "#303132");
        primaryActive.Element("FontWeight")!.Value.Should().Be("800");
        primaryActive.Element("FontSize")!.Value.Should().Be("18");

        var secondary = ControlOf(mod, "SecondaryButton");
        secondary.Element("FontWeight")!.Value.Should().Be("900");
        secondary.Element("FontSize")!.Value.Should().Be("14");
        ShouldBeColor(secondary, "BackgroundColor", "#FF998877", "ARGB");
        var secondaryHover = secondary.Element("Hover")!;
        ShouldBeColor(secondaryHover, "BorderColor", "#80FFFFFF", "CSS");
        ShouldBeColor(secondaryHover, "BackgroundColor", "#AAAAAA");
        ShouldBeColor(secondaryHover, "Color", "#BBBBBB");
        secondaryHover.Element("FontWeight")!.Value.Should().Be("500");
        secondaryHover.Element("FontSize")!.Value.Should().Be("15");
        var secondaryActive = secondary.Element("Active")!;
        secondaryActive.Element("FontSize")!.Value.Should().Be("19");
        ShouldBeColor(secondaryActive, "Color", "#EEEEEE");
        ShouldBeColor(secondaryActive, "BorderColor", "#CCCCCC");
        secondaryActive.Element("FontWeight")!.Value.Should().Be("300");
        secondaryActive.Element("BackgroundColor").Should().BeNull();
    }

    [TestMethod]
    public void Merge_Buttons_ColorFormatReplacedAtomically()
    {
        var mod = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Controls>
                    <PrimaryButton>
                      <Color Format="ARGB">#FF112233</Color>
                      <BorderColor Format="ARGB">#80000000</BorderColor>
                      <Hover>
                        <BackgroundColor Format="ARGB">#80AABBCC</BackgroundColor>
                      </Hover>
                      <Active>
                        <BorderColor Format="ARGB">#FF00FF00</BorderColor>
                      </Active>
                    </PrimaryButton>
                    <SecondaryButton>
                      <Color Format="ARGB">#FF445566</Color>
                      <BorderColor Format="ARGB">#FF0000FF</BorderColor>
                      <Hover>
                        <Color Format="ARGB">#CC778899</Color>
                      </Hover>
                      <Active>
                        <BackgroundColor Format="ARGB">#FFABCDEF</BackgroundColor>
                      </Active>
                    </SecondaryButton>
                  </Controls>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Controls>
                    <SecondaryButton>
                      <Color>#010203</Color>
                      <Active>
                        <BackgroundColor>#FEDCBA</BackgroundColor>
                      </Active>
                      <Hover>
                        <Color>#AABBCCDD</Color>
                      </Hover>
                    </SecondaryButton>
                    <PrimaryButton>
                      <Hover>
                        <BackgroundColor>#11223344</BackgroundColor>
                      </Hover>
                      <Color>#ABCDEF</Color>
                      <Active>
                        <BorderColor>#00FF00</BorderColor>
                      </Active>
                    </PrimaryButton>
                  </Controls>
                </Style>
              </Mod>
            </Metadata>
            """);

        var primary = ControlOf(mod, "PrimaryButton");
        ShouldBeColor(primary, "Color", "#ABCDEF");
        ShouldBeColor(primary, "BorderColor", "#80000000", "ARGB");
        ShouldBeColor(primary.Element("Hover")!, "BackgroundColor", "#11223344");
        ShouldBeColor(primary.Element("Active")!, "BorderColor", "#00FF00");

        var secondary = ControlOf(mod, "SecondaryButton");
        ShouldBeColor(secondary, "Color", "#010203");
        ShouldBeColor(secondary, "BorderColor", "#FF0000FF", "ARGB");
        ShouldBeColor(secondary.Element("Hover")!, "Color", "#AABBCCDD");
        ShouldBeColor(secondary.Element("Active")!, "BackgroundColor", "#FEDCBA");
    }

    [TestMethod]
    public void Merge_Buttons_DoesNotMaterializeStateDefaults()
    {
        var mod = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Controls>
                    <PrimaryButton>
                      <BackgroundColor>#112233</BackgroundColor>
                      <FontSize>16</FontSize>
                      <Color Format="ARGB">#FF010203</Color>
                      <Active>
                        <BorderColor>#445566</BorderColor>
                        <BorderWidth>2</BorderWidth>
                      </Active>
                    </PrimaryButton>
                    <SecondaryButton>
                      <BackgroundColor>#445566</BackgroundColor>
                      <FontSize>14</FontSize>
                      <Hover>
                        <BackgroundColor>#778899</BackgroundColor>
                        <FontSize>13</FontSize>
                      </Hover>
                    </SecondaryButton>
                  </Controls>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Controls>
                    <SecondaryButton>
                      <FontSize>15</FontSize>
                    </SecondaryButton>
                    <PrimaryButton>
                      <Hover>
                        <BorderWidth>1</BorderWidth>
                      </Hover>
                    </PrimaryButton>
                  </Controls>
                </Style>
              </Mod>
            </Metadata>
            """);

        var primary = ControlOf(mod, "PrimaryButton");
        primary.Element("FontSize")!.Value.Should().Be("16");
        ShouldBeColor(primary, "BackgroundColor", "#112233");
        ShouldBeColor(primary, "Color", "#FF010203", "ARGB");
        var primaryHover = primary.Element("Hover")!;
        ShouldHaveFields(primaryHover, "BorderWidth");
        primaryHover.Element("BorderWidth")!.Value.Should().Be("1");
        primaryHover.Element("BackgroundColor").Should().BeNull();
        primaryHover.Element("Color").Should().BeNull();
        primaryHover.Element("FontSize").Should().BeNull();
        var primaryActive = primary.Element("Active")!;
        ShouldHaveFields(primaryActive, "BorderColor", "BorderWidth");
        ShouldBeColor(primaryActive, "BorderColor", "#445566");
        primaryActive.Element("BorderWidth")!.Value.Should().Be("2");

        var secondary = ControlOf(mod, "SecondaryButton");
        secondary.Element("FontSize")!.Value.Should().Be("15");
        ShouldBeColor(secondary, "BackgroundColor", "#445566");
        secondary.Element("Active").Should().BeNull();
        var secondaryHover = secondary.Element("Hover")!;
        ShouldHaveFields(secondaryHover, "BackgroundColor", "FontSize");
        ShouldBeColor(secondaryHover, "BackgroundColor", "#778899");
        secondaryHover.Element("FontSize")!.Value.Should().Be("13");
        secondaryHover.Element("BorderWidth").Should().BeNull();
    }

    [TestMethod]
    public void Merge_Buttons_EmptyBagsPreserveParent()
    {
        var partial = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Logo Width="40" Height="20" OffsetY="0">logo-id</Logo>
                  <Controls>
                    <PrimaryLabel>
                      <FontSize>14</FontSize>
                      <Color>#FFFFFF</Color>
                    </PrimaryLabel>
                    <PrimaryButton>
                      <BackgroundColor>#111111</BackgroundColor>
                      <Hover>
                        <BackgroundColor Format="ARGB">#FF222222</BackgroundColor>
                        <BorderWidth>3</BorderWidth>
                      </Hover>
                      <Active>
                        <BorderColor>#333333</BorderColor>
                      </Active>
                    </PrimaryButton>
                    <SecondaryButton>
                      <Color>#444444</Color>
                      <Hover>
                        <FontSize>12</FontSize>
                      </Hover>
                      <Active>
                        <BackgroundColor>#555555</BackgroundColor>
                        <BorderWidth>0</BorderWidth>
                      </Active>
                    </SecondaryButton>
                  </Controls>
                  <Background Random="true">
                    <Image>parent-bg</Image>
                  </Background>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Controls>
                    <PrimaryButton>
                      <FontSize>21</FontSize>
                      <Hover/>
                    </PrimaryButton>
                    <SecondaryButton>
                      <Active></Active>
                    </SecondaryButton>
                  </Controls>
                </Style>
              </Mod>
            </Metadata>
            """);

        partial.Element("Style")!.Element("Logo")!.Attribute("Height")!.Value.Should().Be("20");
        partial.Element("Style")!.Element("Background")!.Elements("Image").Select(e => e.Value)
            .Should().Equal("parent-bg");
        var primary = ControlOf(partial, "PrimaryButton");
        primary.Element("FontSize")!.Value.Should().Be("21");
        ShouldBeColor(primary, "BackgroundColor", "#111111");
        ShouldBeColor(primary.Element("Hover")!, "BackgroundColor", "#FF222222", "ARGB");
        primary.Element("Hover")!.Element("BorderWidth")!.Value.Should().Be("3");
        ShouldBeColor(primary.Element("Active")!, "BorderColor", "#333333");
        var secondary = ControlOf(partial, "SecondaryButton");
        ShouldBeColor(secondary, "Color", "#444444");
        secondary.Element("Hover")!.Element("FontSize")!.Value.Should().Be("12");
        ShouldBeColor(secondary.Element("Active")!, "BackgroundColor", "#555555");
        secondary.Element("Active")!.Element("BorderWidth")!.Value.Should().Be("0");
        ControlOf(partial, "PrimaryLabel").Element("FontSize")!.Value.Should().Be("14");

        var emptyControls = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Logo Width="40" Height="20" OffsetY="0">logo-id</Logo>
                  <Controls>
                    <PrimaryButton>
                      <Hover>
                        <BackgroundColor Format="ARGB">#FF222222</BackgroundColor>
                      </Hover>
                    </PrimaryButton>
                    <SecondaryButton>
                      <Active>
                        <BorderWidth>0</BorderWidth>
                      </Active>
                    </SecondaryButton>
                  </Controls>
                  <Background Random="true">
                    <Image>parent-bg</Image>
                  </Background>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Controls></Controls>
                </Style>
              </Mod>
            </Metadata>
            """);
        ShouldBeColor(ControlOf(emptyControls, "PrimaryButton").Element("Hover")!, "BackgroundColor", "#FF222222", "ARGB");
        ControlOf(emptyControls, "SecondaryButton").Element("Active")!.Element("BorderWidth")!.Value.Should().Be("0");
        emptyControls.Element("Style")!.Element("Logo")!.Value.Should().Be("logo-id");
        emptyControls.Element("Style")!.Element("Background")!.Attribute("Random")!.Value.Should().Be("true");
    }

    [TestMethod]
    public void Merge_Buttons_ExplicitZeroPreserved()
    {
        var mod = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Controls>
                    <PrimaryButton>
                      <BorderWidth>2</BorderWidth>
                      <Hover>
                        <BorderWidth>4</BorderWidth>
                        <BackgroundColor>#111111</BackgroundColor>
                      </Hover>
                      <Active>
                        <BorderWidth>5</BorderWidth>
                        <BorderColor>#222222</BorderColor>
                      </Active>
                    </PrimaryButton>
                    <SecondaryButton>
                      <BorderWidth>0</BorderWidth>
                      <FontSize>14</FontSize>
                      <Hover>
                        <BorderWidth>0</BorderWidth>
                        <FontSize>12</FontSize>
                      </Hover>
                      <Active>
                        <BorderWidth>0</BorderWidth>
                      </Active>
                    </SecondaryButton>
                  </Controls>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Controls>
                    <SecondaryButton>
                      <FontSize>15</FontSize>
                      <Hover>
                        <Color>#010203</Color>
                      </Hover>
                    </SecondaryButton>
                    <PrimaryButton>
                      <BorderWidth>0</BorderWidth>
                      <Active>
                        <BorderWidth>0</BorderWidth>
                      </Active>
                      <Hover>
                        <Color>#ABCDEF</Color>
                        <BorderWidth>0</BorderWidth>
                      </Hover>
                    </PrimaryButton>
                  </Controls>
                </Style>
              </Mod>
            </Metadata>
            """);

        var primary = ControlOf(mod, "PrimaryButton");
        primary.Element("BorderWidth")!.Value.Should().Be("0");
        var primaryHover = primary.Element("Hover")!;
        primaryHover.Element("BorderWidth")!.Value.Should().Be("0");
        ShouldBeColor(primaryHover, "BackgroundColor", "#111111");
        ShouldBeColor(primaryHover, "Color", "#ABCDEF");
        var primaryActive = primary.Element("Active")!;
        primaryActive.Element("BorderWidth")!.Value.Should().Be("0");
        ShouldBeColor(primaryActive, "BorderColor", "#222222");

        var secondary = ControlOf(mod, "SecondaryButton");
        secondary.Element("BorderWidth")!.Value.Should().Be("0");
        secondary.Element("FontSize")!.Value.Should().Be("15");
        var secondaryHover = secondary.Element("Hover")!;
        secondaryHover.Element("BorderWidth")!.Value.Should().Be("0");
        secondaryHover.Element("FontSize")!.Value.Should().Be("12");
        ShouldBeColor(secondaryHover, "Color", "#010203");
        secondary.Element("Active")!.Element("BorderWidth")!.Value.Should().Be("0");
    }

    [TestMethod]
    public void Merge_Controls_MergesByNameRegardlessOfOrder()
    {
        var mod = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Controls>
                    <PrimaryLabel>
                      <FontSize>14</FontSize>
                      <Color>#111111</Color>
                    </PrimaryLabel>
                    <SecondaryLabel>
                      <FontSize>12</FontSize>
                      <Color>#222222</Color>
                    </SecondaryLabel>
                    <PrimaryButton>
                      <BorderColor>#AAAAAA</BorderColor>
                      <FontSize>16</FontSize>
                      <BackgroundColor>#BBBBBB</BackgroundColor>
                    </PrimaryButton>
                    <SecondaryButton>
                      <FontSize>13</FontSize>
                      <Color>#444444</Color>
                    </SecondaryButton>
                  </Controls>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Controls>
                    <SecondaryButton>
                      <Color>#555555</Color>
                    </SecondaryButton>
                    <PrimaryLabel>
                      <FontSize>18</FontSize>
                    </PrimaryLabel>
                    <SecondaryLabel>
                      <Color>#666666</Color>
                    </SecondaryLabel>
                    <PrimaryButton>
                      <BackgroundColor>#CCCCCC</BackgroundColor>
                      <BorderColor>#DDDDDD</BorderColor>
                    </PrimaryButton>
                  </Controls>
                </Style>
              </Mod>
            </Metadata>
            """);

        var primaryLabel = ControlOf(mod, "PrimaryLabel");
        primaryLabel.Element("FontSize")!.Value.Should().Be("18");
        ShouldBeColor(primaryLabel, "Color", "#111111");
        var secondaryLabel = ControlOf(mod, "SecondaryLabel");
        secondaryLabel.Element("FontSize")!.Value.Should().Be("12");
        ShouldBeColor(secondaryLabel, "Color", "#666666");
        var primary = ControlOf(mod, "PrimaryButton");
        ShouldBeColor(primary, "BorderColor", "#DDDDDD");
        ShouldBeColor(primary, "BackgroundColor", "#CCCCCC");
        primary.Element("FontSize")!.Value.Should().Be("16");
        var secondary = ControlOf(mod, "SecondaryButton");
        ShouldBeColor(secondary, "Color", "#555555");
        secondary.Element("FontSize")!.Value.Should().Be("13");
    }

    [TestMethod]
    public void Merge_Logo_ScalarReplacementDoesNotSynthesizeHeight()
    {
        var mod = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Logo Width="400" Height="160" Position="TopLeft" OffsetX="24" OffsetY="0">base-logo</Logo>
                  <Controls>
                    <PrimaryButton>
                      <FontSize>16</FontSize>
                    </PrimaryButton>
                  </Controls>
                  <Background Random="true">
                    <Image>base-a</Image>
                    <Image>base-b</Image>
                    <Color Format="ARGB">#FF112233</Color>
                  </Background>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Logo Width="120">child-logo</Logo>
                </Style>
              </Mod>
            </Metadata>
            """);

        mod.Element("Style")!.Elements().Select(e => e.Name.LocalName)
            .Should().Equal("Logo", "Controls", "Background");
        var logo = mod.Element("Style")!.Element("Logo")!;
        logo.Value.Should().Be("child-logo");
        logo.Attribute("Width")!.Value.Should().Be("120");
        logo.Attribute("Height").Should().BeNull();
        logo.Attribute("Position").Should().BeNull();
        logo.Attribute("OffsetX").Should().BeNull();
        logo.Attribute("OffsetY").Should().BeNull();
        ControlOf(mod, "PrimaryButton").Element("FontSize")!.Value.Should().Be("16");
        var background = mod.Element("Style")!.Element("Background")!;
        background.Attribute("Random")!.Value.Should().Be("true");
        background.Elements("Image").Select(e => e.Value).Should().Equal("base-a", "base-b");
        ShouldBeColor(background, "Color", "#FF112233", "ARGB");
    }

    [TestMethod]
    public void Merge_Logo_EmptyReplacesAndOmittedInherits()
    {
        var replaced = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Logo Width="400" Height="160" Position="Center" OffsetX="8" OffsetY="0">base-logo</Logo>
                  <Background Random="false">
                    <Image>base-a</Image>
                    <SecondaryColor>#445566</SecondaryColor>
                  </Background>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Logo/>
                </Style>
              </Mod>
            </Metadata>
            """);
        var emptyLogo = replaced.Element("Style")!.Element("Logo")!;
        emptyLogo.Value.Should().BeEmpty();
        emptyLogo.HasAttributes.Should().BeFalse();
        var keptBackground = replaced.Element("Style")!.Element("Background")!;
        keptBackground.Attribute("Random")!.Value.Should().Be("false");
        keptBackground.Elements("Image").Select(e => e.Value).Should().Equal("base-a");
        ShouldBeColor(keptBackground, "SecondaryColor", "#445566");

        var inherited = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Logo Width="400" Height="160" Position="Center" OffsetX="8" OffsetY="0">base-logo</Logo>
                  <Background Random="false">
                    <Image>base-a</Image>
                    <SecondaryColor>#445566</SecondaryColor>
                  </Background>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Background>
                    <Color>#ABCDEF</Color>
                  </Background>
                </Style>
              </Mod>
            </Metadata>
            """);
        var logo = inherited.Element("Style")!.Element("Logo")!;
        logo.Value.Should().Be("base-logo");
        logo.Attribute("Width")!.Value.Should().Be("400");
        logo.Attribute("Height")!.Value.Should().Be("160");
        logo.Attribute("Position")!.Value.Should().Be("Center");
        logo.Attribute("OffsetX")!.Value.Should().Be("8");
        logo.Attribute("OffsetY")!.Value.Should().Be("0");
        var background = inherited.Element("Style")!.Element("Background")!;
        background.Elements("Image").Should().BeEmpty();
        background.Attribute("Random").Should().BeNull();
        background.Element("SecondaryColor").Should().BeNull();
        ShouldBeColor(background, "Color", "#ABCDEF");
    }

    [TestMethod]
    public void Merge_Background_ScalarOrEmptyReplacesCategory()
    {
        var colorOnly = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Logo Width="10" Height="20">keep-logo</Logo>
                  <Background Random="true">
                    <Image>base-a</Image>
                    <Image>base-b</Image>
                    <Color Format="ARGB">#FF112233</Color>
                    <SecondaryColor>#445566</SecondaryColor>
                  </Background>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Background>
                    <Color>#ABCDEF</Color>
                  </Background>
                </Style>
              </Mod>
            </Metadata>
            """);
        colorOnly.Element("Style")!.Elements().Select(e => e.Name.LocalName)
            .Should().Equal("Logo", "Background");
        colorOnly.Element("Style")!.Element("Logo")!.Attribute("Height")!.Value.Should().Be("20");
        var colorBackground = colorOnly.Element("Style")!.Element("Background")!;
        colorBackground.Elements("Image").Should().BeEmpty();
        colorBackground.Attribute("Random").Should().BeNull();
        colorBackground.Element("SecondaryColor").Should().BeNull();
        ShouldBeColor(colorBackground, "Color", "#ABCDEF");

        var randomOnly = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Logo Width="10" Height="20">keep-logo</Logo>
                  <Background Random="true">
                    <Image>base-a</Image>
                    <Image>base-b</Image>
                    <Color Format="ARGB">#FF112233</Color>
                    <SecondaryColor>#445566</SecondaryColor>
                  </Background>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Background Random="false"/>
                </Style>
              </Mod>
            </Metadata>
            """);
        var randomBackground = randomOnly.Element("Style")!.Element("Background")!;
        randomBackground.Attribute("Random")!.Value.Should().Be("false");
        randomBackground.HasElements.Should().BeFalse();
        randomOnly.Element("Style")!.Element("Logo")!.Value.Should().Be("keep-logo");

        var empty = ResolveMod("""
            <Metadata>
              <Base ID="StandardMod" Kind="Mod">
                <Style>
                  <Logo Width="10" Height="20">keep-logo</Logo>
                  <Background Random="true">
                    <Image>base-a</Image>
                    <Color Format="ARGB">#FF112233</Color>
                  </Background>
                </Style>
              </Base>
              <Mod ID="Child" InheritFrom="StandardMod">
                <Style>
                  <Background></Background>
                </Style>
              </Mod>
            </Metadata>
            """);
        var emptyBackground = empty.Element("Style")!.Element("Background")!;
        emptyBackground.HasAttributes.Should().BeFalse();
        emptyBackground.HasElements.Should().BeFalse();
        empty.Element("Style")!.Element("Logo")!.Attribute("Height")!.Value.Should().Be("20");
    }

    private static XElement ResolveMod(string xml)
    {
        var root = XElement.Parse(xml);
        MetadataInheritance.Resolve(root);
        root.Elements("Base").Should().BeEmpty();
        var mod = root.Element("Mod");
        mod.Should().NotBeNull();
        mod!.Attribute("InheritFrom").Should().BeNull();
        return mod;
    }

    private static XElement ControlOf(XElement mod, string name) =>
        mod.Element("Style")!.Element("Controls")!.Element(name)!;

    private static void ShouldBeColor(XElement parent, string name, string value, string? format = null)
    {
        var color = parent.Element(name);
        color.Should().NotBeNull();
        color!.Value.Should().Be(value);
        if (format == null)
            color.Attribute("Format").Should().BeNull();
        else
            color.Attribute("Format")!.Value.Should().Be(format);
    }

    private static void ShouldHaveFields(XElement parent, params string[] names) =>
        parent.Elements().Select(element => element.Name.LocalName).Should().BeEquivalentTo(names);

}
