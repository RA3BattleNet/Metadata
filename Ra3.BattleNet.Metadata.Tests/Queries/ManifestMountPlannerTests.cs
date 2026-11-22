using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 挂载计划：清单写了 Skudef 就按声明顺序与条件产出，没写就按旧 File@Mount 角色产出。
/// </summary>
[TestClass]
public class ManifestMountPlannerTests
{
    private static readonly ManifestMountFile[] Files =
    [
        new("Disabler.big", "base", null, null),
        new("HighResShadow.big", "base", null, null),
        new("Cor_ENG_3.250.big", "language", "en", null),
        new("corona_3.258.lyi", "base", null, null),
    ];

    private static IReadOnlyList<ManifestMountCommand> Build(
        ManifestSkudefEntry? skudef,
        string language = "chs",
        string[]? packages = null,
        Func<string, bool>? localConfigExists = null,
        IReadOnlyList<ManifestMountFile>? files = null)
    {
        return ManifestMountPlanner.Build(
            skudef,
            files ?? Files,
            language,
            packages ?? [],
            localConfigExists ?? (_ => false));
    }

    private static ManifestSkudefEntry Declared(params ManifestSkudefCommand[] commands) => new("1.12", commands);

    [TestMethod]
    public void Declared_KeepsDocumentOrder_AndDropsUnmatchedConditions()
    {
        var skudef = Declared(
            new ManifestSkudefCommand(SkudefCommandKind.Big, "Disabler.big", false, null, "hd-shadow"),
            new ManifestSkudefCommand(SkudefCommandKind.Big, "Cor_ENG_3.250.big", false, "en", null),
            new ManifestSkudefCommand(SkudefCommandKind.Big, "HighResShadow.big", false, null, null),
            new ManifestSkudefCommand(SkudefCommandKind.Big, "corona_3.258.lyi", false, null, null));

        Build(skudef).Select(c => c.Target)
            .Should().Equal("HighResShadow.big", "corona_3.258.lyi");

        Build(skudef, language: "en", packages: ["hd-shadow"]).Select(c => c.Target)
            .Should().Equal("Disabler.big", "Cor_ENG_3.250.big", "HighResShadow.big", "corona_3.258.lyi");
    }

    [TestMethod]
    public void Declared_AddConfig_DependsOnLocalFileAndOptional()
    {
        var optional = Declared(new ManifestSkudefCommand(SkudefCommandKind.Config, "CustomConfig.txt", true, null, null));
        Build(optional, localConfigExists: _ => false, files: []).Should().BeEmpty();
        Build(optional, localConfigExists: name => name == "CustomConfig.txt", files: [])
            .Should().Equal(new ManifestMountCommand(ManifestMountKind.Config, "CustomConfig.txt"));

        var required = Declared(new ManifestSkudefCommand(SkudefCommandKind.Config, "Missing.txt", false, null, null));
        var act = () => Build(required, localConfigExists: _ => false, files: []);
        act.Should().Throw<InvalidOperationException>().WithMessage("找不到本地配置：Missing.txt");
    }

    [TestMethod]
    public void Declared_AddConfigWithPath_Throws()
    {
        var skudef = Declared(new ManifestSkudefCommand(SkudefCommandKind.Config, "sub/Config.txt", false, null, null));

        var act = () => Build(skudef, files: []);

        act.Should().Throw<InvalidOperationException>().WithMessage("本地配置名非法：sub/Config.txt");
    }

    [TestMethod]
    public void Declared_DanglingDuplicateAndUnreferenced_Throw()
    {
        var dangling = Declared(new ManifestSkudefCommand(SkudefCommandKind.Big, "missing.big", false, null, null));
        var act = () => Build(dangling);
        act.Should().Throw<InvalidOperationException>().WithMessage("*引用了清单里没有的文件*");

        var duplicate = Declared(
            new ManifestSkudefCommand(SkudefCommandKind.Big, "corona_3.258.lyi", false, null, null),
            new ManifestSkudefCommand(SkudefCommandKind.Big, "corona_3.258.lyi", false, null, null));
        act = () => Build(duplicate, files: [new ManifestMountFile("corona_3.258.lyi")]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*重复引用*");

        var partial = Declared(new ManifestSkudefCommand(SkudefCommandKind.Big, "corona_3.258.lyi", false, null, null));
        act = () => Build(partial, files: [new ManifestMountFile("corona_3.258.lyi"), new ManifestMountFile("extra.big")]);
        act.Should().Throw<InvalidOperationException>().WithMessage("清单缺少 add-big：extra.big");
    }

    [TestMethod]
    public void Declared_DuplicateFileName_Throws()
    {
        var skudef = Declared(new ManifestSkudefCommand(SkudefCommandKind.Big, "corona_3.258.lyi", false, null, null));
        var files = new[] { new ManifestMountFile("corona_3.258.lyi"), new ManifestMountFile("corona_3.258.lyi") };

        var act = () => Build(skudef, files: files);

        act.Should().Throw<InvalidOperationException>().WithMessage("清单里 FileName 重复：corona_3.258.lyi");
    }

    [TestMethod]
    public void Legacy_UsesMountRolesInFileOrder()
    {
        Build(null).Select(c => c.Target).Should().Equal("Disabler.big", "HighResShadow.big", "corona_3.258.lyi");

        Build(null, language: "en", packages: ["hd-shadow"]).Select(c => c.Target)
            .Should().Equal("Disabler.big", "HighResShadow.big", "Cor_ENG_3.250.big", "corona_3.258.lyi");

        IReadOnlyList<ManifestMountFile> optionalOnly =
            [new("Mod.big", "optional", null, "hd-shadow")];
        Build(null, files: optionalOnly).Should().BeEmpty("没开开关就不挂");
        Build(null, packages: ["HD-Shadow"], files: optionalOnly).Should().HaveCount(1, "开关名大小写不敏感");
    }

    [TestMethod]
    public void Legacy_IllegalMountRole_Throws()
    {
        IReadOnlyList<ManifestMountFile> files = [new("Mod.big", "patch")];

        var act = () => Build(null, files: files);

        act.Should().Throw<InvalidOperationException>().WithMessage("Mod.big 的挂载角色非法: patch");
    }
}
