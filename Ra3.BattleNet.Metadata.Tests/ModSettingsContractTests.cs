using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 通用模组设置契约的可见行为：定义/绑定解析、快照往返、跨节点硬校验、
/// 继承保序合并、以及真实数据里 Corona 8 项 / ArmorRush 4 项的投影与真实哈希。
/// </summary>
[TestClass]
public class ModSettingsContractTests
{
    private static string RepoMetadataDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));

    [TestMethod]
    public void ParseDefinitions_ReadsBooleanChoiceAndLanguageFamilies()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Boolean ID="hd-shadow" Default="false">
    <DisplayName Language="zh">高清阴影</DisplayName>
    <DisplayName Language="en">High-Res Shadows</DisplayName>
    <Description Language="zh">启用更高分辨率的实时地面阴影</Description>
    <Description Language="en">Enable higher resolution shadows</Description>
  </Boolean>
  <Choice ID="mod-language" Default="chs">
    <DisplayName Language="zh">模组语言</DisplayName>
    <Option Value="chs">
      <DisplayName Language="zh">简体中文</DisplayName>
      <DisplayName Language="en">Simplified Chinese</DisplayName>
    </Option>
    <Option Value="en">
      <DisplayName Language="zh">英语 (English)</DisplayName>
      <DisplayName Language="en">English</DisplayName>
    </Option>
  </Choice>
</Settings>
""");

        definitions.Select(d => d.Id).Should().Equal("hd-shadow", "mod-language");

        var boolean = definitions[0];
        boolean.Kind.Should().Be(ModSettingKind.Boolean);
        boolean.Default.Should().Be("false");
        boolean.DisplayNames.Select(t => t.Language).Should().Equal("zh", "en");
        boolean.Descriptions.Should().HaveCount(2);
        boolean.Options.Should().BeEmpty();

        var choice = definitions[1];
        choice.Kind.Should().Be(ModSettingKind.Choice);
        choice.Default.Should().Be("chs");
        choice.Options.Select(o => o.Value).Should().Equal("chs", "en");
        choice.Options[1].DisplayNames[0].Text.Should().Be("英语 (English)");
    }

    [TestMethod]
    public void ParseManifest_ReadsDllIdProtocolCustomDataAndInjection()
    {
        var entry = ParseManifest("""
<Manifest ID="m" HashAlgorithm="CRC32C">
  <Dependencies>
    <Dll ID="hook" Name="Hook.dll" Hash="7E77F4E5" Protocol="easyhook">
      <CustomData Encoding="utf8">
        <RuntimeValue Name="log-file" />
      </CustomData>
    </Dll>
  </Dependencies>
  <Injection>
    <InjectDll Ref="hook" />
  </Injection>
</Manifest>
""");

        entry.Dependencies.Should().HaveCount(1);
        entry.Dependencies[0].Id.Should().Be("hook");
        entry.Dependencies[0].Protocol.Should().Be("easyhook");
        entry.Dependencies[0].CustomData.Should().Be(new ManifestDllCustomData("utf8", "log-file"));
        entry.Injection.Should().NotBeNull();
        entry.Injection!.Should().HaveCount(1);
        entry.Injection[0].Inject.Should().BeTrue();
        entry.Injection[0].Ref.Should().Be("hook");
    }

    [TestMethod]
    public void ParseManifest_LegacyWithoutInjection_LeavesInjectionNull()
    {
        var entry = ParseManifest("""
<Manifest ID="legacy" HashAlgorithm="CRC32C">
  <Dependencies>
    <Dll Name="Lyi.dll" Hash="A4A7924F" />
  </Dependencies>
</Manifest>
""");

        entry.Injection.Should().BeNull("未声明 <Injection> 的旧清单沿用旧执行链");
        entry.Dependencies[0].Id.Should().BeNull();
        entry.Settings.Should().BeEmpty();
    }

    [TestMethod]
    public void Snapshot_SerializeParse_RoundTrips()
    {
        var dst = Path.Combine(Path.GetTempPath(), $"settings-snapshot-{Guid.NewGuid():N}");
        try
        {
            MetadataBuilder.Build(RepoMetadataDir, dst, schemaVersion: "1.0", contentRevision: "snapshot");
            var root = Metadata.LoadFromFile(Path.Combine(dst, "metadata.xml"));

            var corona = root.Mods().Single(m => m.Id == "Corona");
            var manifest = Metadata.LoadFromFile(
                Path.Combine(dst, "mods", "corona", "manifests", "3.258.xml")).Find("Manifest")!.ToManifestEntry();
            var snapshot = new ModSettingsSnapshot(corona.Settings, manifest.Settings);

            var xml = ModSettingsContract.SerializeSnapshot(snapshot);

            var parsed = ModSettingsContract.ParseSnapshot(xml);
            parsed.Definitions.Should().BeEquivalentTo(snapshot.Definitions, o => o.WithStrictOrdering());
            parsed.Bindings.Should().BeEquivalentTo(snapshot.Bindings, o => o.WithStrictOrdering());

            ModSettingsContract.SerializeSnapshot(parsed).Should().Be(xml);
        }
        finally
        {
            if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
        }
    }

    [TestMethod]
    public void Validate_ChoiceDefaultOutsideOptions_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Choice ID="mode" Default="z">
    <Option Value="a" />
    <Option Value="b" />
  </Choice>
</Settings>
""");
        var manifest = ParseManifest("""<Manifest ID="m"><Injection /></Manifest>""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Default 不在 Option*");
    }

    [TestMethod]
    public void Validate_BooleanDefaultNotLiteral_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Boolean ID="flag" Default="maybe" />
</Settings>
""");
        var manifest = ParseManifest("""<Manifest ID="m"><Injection /></Manifest>""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Default 必须是 true 或 false*");
    }

    [TestMethod]
    public void Validate_DanglingSettingRef_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Boolean ID="flag" Default="false" />
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Injection />
  <Settings>
    <SettingRef Ref="missing"><MountPackage Name="pkg" /></SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*不存在的设置定义*");
    }

    [TestMethod]
    public void Validate_DuplicateSettingRef_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Boolean ID="flag" Default="false" />
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Injection />
  <Settings>
    <SettingRef Ref="flag"><MountPackage Name="pkg" /></SettingRef>
    <SettingRef Ref="flag"><MountPackage Name="pkg" /></SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*SettingRef 重复*");
    }

    [TestMethod]
    public void Validate_BooleanWithCase_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Boolean ID="flag" Default="false" />
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Injection />
  <Settings>
    <SettingRef Ref="flag">
      <Case Value="true"><MountPackage Name="pkg" /></Case>
    </SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Boolean 设置不能声明 Case*");
    }

    [TestMethod]
    public void Validate_CaseValueOutsideOptions_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Choice ID="mode" Default="normal">
    <Option Value="normal" />
    <Option Value="crates" />
  </Choice>
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Injection />
  <Skudef GameVersion="1.12">
    <AddBig File="a.big" Package="crates" />
  </Skudef>
  <Settings>
    <SettingRef Ref="mode">
      <Case Value="bogus"><MountPackage Name="crates" /></Case>
    </SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Case 值不在 Option*");
    }

    [TestMethod]
    public void Validate_MountPackageNotInSkudef_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Boolean ID="flag" Default="false" />
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Injection />
  <Skudef GameVersion="1.12">
    <AddBig File="a.big" />
  </Skudef>
  <Settings>
    <SettingRef Ref="flag"><MountPackage Name="not-mounted" /></SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Skudef 未声明的 Package*");
    }

    [TestMethod]
    public void Validate_NewDllIdWithoutInjectionMarker_Fails()
    {
        var definitions = Array.Empty<ModSettingDefinition>();
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Dependencies>
    <Dll ID="hook" Name="Hook.dll" Hash="7E77F4E5" Protocol="easyhook" />
  </Dependencies>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*必须显式声明 <Injection>*");
    }

    [TestMethod]
    public void Validate_MountLanguageWithoutInjectionMarker_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Choice ID="mode" Default="x">
    <Option Value="x" />
  </Choice>
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Settings>
    <SettingRef Ref="mode"><MountLanguage /></SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*必须显式声明 <Injection>*");
    }

    [TestMethod]
    public void Validate_MountPackageWithoutInjectionMarker_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Boolean ID="flag" Default="false" />
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Skudef GameVersion="1.12">
    <AddBig File="a.big" Package="pkg" />
  </Skudef>
  <Settings>
    <SettingRef Ref="flag"><MountPackage Name="pkg" /></SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*必须显式声明 <Injection>*");
    }

    [TestMethod]
    public void Validate_SettingsWithEmptyInjectionMarker_Passes()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Boolean ID="flag" Default="false" />
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Injection />
  <Skudef GameVersion="1.12">
    <AddBig File="a.big" Package="pkg" />
  </Skudef>
  <Settings>
    <SettingRef Ref="flag"><MountPackage Name="pkg" /></SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Validate_UnknownProtocol_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Boolean ID="flag" Default="false" />
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Dependencies>
    <Dll ID="hook" Name="Hook.dll" Hash="7E77F4E5" Protocol="magic" />
  </Dependencies>
  <Injection>
    <InjectDll Ref="hook" />
  </Injection>
  <Settings>
    <SettingRef Ref="flag"><InjectDll Ref="hook" /></SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*未知 Protocol*");
    }

    [TestMethod]
    public void Validate_UnknownAdapter_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Boolean ID="flag" Default="false" />
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Dependencies>
    <Dll ID="lua-bridge" Name="RA3LuaBridge.dll" Hash="0D49B9A0" Protocol="lyi-create-process" />
  </Dependencies>
  <Injection>
    <InjectDll Ref="lua-bridge" />
  </Injection>
  <Settings>
    <SettingRef Ref="flag"><ConfigureLuaBridge Ref="lua-bridge" Adapter="nope" /></SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*未知 Adapter*");
    }

    [TestMethod]
    public void Validate_ConfigureLuaBridgeTargetNotInjected_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Boolean ID="flag" Default="false" />
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Dependencies>
    <Dll ID="lua-bridge" Name="RA3LuaBridge.dll" Hash="0D49B9A0" Protocol="lyi-create-process" />
  </Dependencies>
  <Injection />
  <Settings>
    <SettingRef Ref="flag"><ConfigureLuaBridge Ref="lua-bridge" Adapter="audio-fix" /></SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*未处于注入动作集合*");
    }

    [TestMethod]
    public void Inheritance_MergeSettings_KeepsParentPositionAndAppendsNew()
    {
        var root = XElement.Parse("""
            <Metadata>
              <Base ID="Parent" Kind="Mod">
                <Settings>
                  <Boolean ID="a" Default="false" />
                  <Boolean ID="b" Default="false" />
                </Settings>
              </Base>
              <Mod ID="Child" InheritFrom="Parent">
                <Settings>
                  <Boolean ID="b" Default="true" />
                  <Choice ID="c" Default="x">
                    <Option Value="x" />
                  </Choice>
                </Settings>
              </Mod>
            </Metadata>
            """);

        MetadataInheritance.Resolve(root);

        var settings = root.Element("Mod")!.Element("Settings")!;
        settings.Elements().Select(e => e.Attribute("ID")!.Value).Should().Equal("a", "b", "c");
        settings.Elements().Single(e => e.Attribute("ID")!.Value == "b").Attribute("Default")!.Value.Should().Be("true");
        settings.Elements().Single(e => e.Attribute("ID")!.Value == "a").Attribute("Default")!.Value.Should().Be("false");
    }

    [TestMethod]
    public void Inheritance_MergeSettings_TypeConflictFails()
    {
        var root = XElement.Parse("""
            <Metadata>
              <Base ID="Parent" Kind="Mod">
                <Settings>
                  <Boolean ID="a" Default="false" />
                </Settings>
              </Base>
              <Mod ID="Child" InheritFrom="Parent">
                <Settings>
                  <Choice ID="a" Default="x">
                    <Option Value="x" />
                  </Choice>
                </Settings>
              </Mod>
            </Metadata>
            """);

        var act = () => MetadataInheritance.Resolve(root);
        act.Should().Throw<InvalidOperationException>().WithMessage("*类型不一致*");
    }

    [TestMethod]
    public void Validate_BooleanWithMountLanguage_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Boolean ID="flag" Default="false" />
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Injection />
  <Settings>
    <SettingRef Ref="flag"><MountLanguage /></SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Boolean 设置不能使用 MountLanguage*");
    }

    [TestMethod]
    public void Validate_ChoiceDirectMountPackage_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Choice ID="mode" Default="normal">
    <Option Value="normal" />
  </Choice>
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Injection />
  <Skudef GameVersion="1.12">
    <AddBig File="a.big" Package="pkg" />
  </Skudef>
  <Settings>
    <SettingRef Ref="mode"><MountPackage Name="pkg" /></SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*必须写在 Case 中*");
    }

    [TestMethod]
    public void Validate_CaseWithMountLanguage_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Choice ID="mode" Default="normal">
    <Option Value="normal" />
  </Choice>
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Injection />
  <Settings>
    <SettingRef Ref="mode">
      <Case Value="normal"><MountLanguage /></Case>
    </SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Case 不能声明 MountLanguage*");
    }

    [TestMethod]
    public void Validate_MultipleMountLanguage_Fails()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Choice ID="a" Default="x">
    <Option Value="x" />
  </Choice>
  <Choice ID="b" Default="y">
    <Option Value="y" />
  </Choice>
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Injection />
  <Settings>
    <SettingRef Ref="a"><MountLanguage /></SettingRef>
    <SettingRef Ref="b"><MountLanguage /></SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*MountLanguage 只能声明一次*");
    }

    [TestMethod]
    public void Validate_ChoiceCaseConfigureLuaBridge_Passes()
    {
        var definitions = ParseDefinitions("""
<Settings>
  <Choice ID="mode" Default="normal">
    <Option Value="normal" />
    <Option Value="crates" />
  </Choice>
</Settings>
""");
        var manifest = ParseManifest("""
<Manifest ID="m">
  <Dependencies>
    <Dll ID="lua-bridge" Name="RA3LuaBridge.dll" Hash="0D49B9A0" Protocol="lyi-create-process" />
  </Dependencies>
  <Injection>
    <InjectDll Ref="lua-bridge" />
  </Injection>
  <Settings>
    <SettingRef Ref="mode">
      <Case Value="crates"><ConfigureLuaBridge Ref="lua-bridge" Adapter="audio-fix" /></Case>
    </SettingRef>
  </Settings>
</Manifest>
""");

        var act = () => ModSettingsContract.Validate(definitions, manifest);
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Validate_UnknownSettingKind_Fails()
    {
        var definition = new ModSettingDefinition("flag", (ModSettingKind)99, "v", [], [], []);
        var manifest = ParseManifest("""<Manifest ID="m"><Injection /></Manifest>""");

        var act = () => ModSettingsContract.Validate(new[] { definition }, manifest);
        act.Should().Throw<InvalidOperationException>().WithMessage("*未知设置类型*");
    }

    [TestMethod]
    public void ParseDefinitions_UnknownNode_Throws()
    {
        var act = () => ParseDefinitions("""<Settings><Bogus ID="x" Default="false" /></Settings>""");
        act.Should().Throw<InvalidOperationException>().WithMessage("*未知节点*");
    }

    [TestMethod]
    public void ParseManifest_UnknownAction_Throws()
    {
        var act = () => ParseManifest("""
<Manifest ID="m">
  <Injection />
  <Settings>
    <SettingRef Ref="b"><BogusAction /></SettingRef>
  </Settings>
</Manifest>
""");
        act.Should().Throw<InvalidOperationException>().WithMessage("*未知设置动作节点*");
    }

    private static IReadOnlyList<ModSettingDefinition> ParseDefinitions(string settingsXml)
    {
        var xml = $"""
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Mod ID="T">
{settingsXml}
  </Mod>
</Metadata>
""";
        var node = LoadInline(xml).Find("Mod")!.Find("Settings")
            ?? throw new InvalidOperationException("测试输入缺少 Settings");
        return ModSettingsContract.ParseDefinitions(node);
    }

    private static ManifestEntry ParseManifest(string manifestXml)
    {
        var xml = $"""
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
{manifestXml}
</Metadata>
""";
        return LoadInline(xml).Find("Manifest")!.ToManifestEntry();
    }

    private static Metadata LoadInline(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-inline-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(path, xml);
            return Metadata.LoadFromFile(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
