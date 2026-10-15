using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class ModCompatibilityTests
{
    private static string Mod(string id, string attribute = "", string rules = "", string packages = "") =>
        $"<Mod ID=\"{id}\" {attribute}><CurrentVersion>1</CurrentVersion><DisplayName Language=\"zh\">{id}</DisplayName><DisplayName Language=\"en\">{id}</DisplayName>{rules}<Packages>{packages}</Packages></Mod>";

    private sealed class Tree : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "compatibility-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(_root, "src");
        public string Output => Path.Combine(_root, "out");
        public Tree(string content)
        {
            Directory.CreateDirectory(Source);
            var metadata = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Metadata"));
            foreach (var schema in new[] { SchemaValidator.SourceSchemaFileName, SchemaValidator.PublishSchemaFileName })
                File.Copy(Path.Combine(metadata, schema), Path.Combine(Source, schema));
            File.WriteAllText(Path.Combine(Source, "metadata.xml"), "<Metadata>" + content + "</Metadata>");
        }
        public MetadataCatalog Build()
        {
            MetadataBuilder.Build(Source, Output, contentRevision: "compatibility-test");
            SchemaValidator.ValidateFile(Path.Combine(Output, "metadata.xml"), Path.Combine(Source, SchemaValidator.PublishSchemaFileName)).Should().BeEmpty();
            return MetadataBuilder.Load(Path.Combine(Output, "metadata.xml")).Catalog();
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void XmlBooleanWhitespace_IsNormalizedBeforeCompatibilityDecisions()
    {
        using var tree = new Tree("""
            <Mod ID="Other" IsAddon=" true "><CurrentVersion>1</CurrentVersion><DisplayName Language="zh">Other</DisplayName><DisplayName Language="en">Other</DisplayName></Mod>
            <Mod ID="Addon" IsAddon=" 1 "><CurrentVersion>1</CurrentVersion><DisplayName Language="zh">Addon</DisplayName><DisplayName Language="en">Addon</DisplayName><Compatibility><Addon Mod="Other" Compatible=" false "/></Compatibility></Mod>
            """);
        var mod = tree.Build().Mod("Addon")!;
        mod.IsAddon.Should().BeTrue();
        mod.LaunchCompatibility("1").Addons.Single().Compatible.Should().BeFalse();
    }

    [TestMethod]
    public void OldMetadata_HasNoAddonFlagOrCompatibilityRules()
    {
        using var tree = new Tree(Mod("Old"));
        var mod = tree.Build().Mod("old")!;
        mod.IsAddon.Should().BeFalse();
        mod.LaunchCompatibility("1").Main.Should().BeEmpty();
        mod.LaunchCompatibility("1").Addons.Should().BeEmpty();
    }

    [TestMethod]
    public void Build_PreservesIdsVersionsAndIndependentAddonFlag()
    {
        using var tree = new Tree(Mod("Main") + Mod("Other", "IsAddon=\"1\"") +
            Mod("Addon", "IsAddon=\"true\"", "<Compatibility><Main Mod=\"main\"><Version>1</Version><Version>2</Version></Main><Addon Mod=\"Other\" Compatible=\"false\"/></Compatibility>"));
        var mod = tree.Build().Mod("addon")!;
        mod.IsAddon.Should().BeTrue();
        mod.LaunchCompatibility("1").Main.Single().ModId.Should().Be("main", "实体 ID 不加资源前缀");
        mod.LaunchCompatibility("1").Main.Single().Versions.Should().Equal("1", "2");
        mod.LaunchCompatibility("1").Addons.Single().Compatible.Should().BeFalse();
    }

    [TestMethod]
    public void PackageRules_ReplaceRatherThanMerge_AndEmptyRulesClear()
    {
        using var tree = new Tree(Mod("Main") + Mod("Other", "IsAddon=\"true\"") +
            Mod("Addon", "IsAddon=\"true\"", "<Compatibility><Main Mod=\"Main\"/></Compatibility>",
                "<Package Version=\"1\"/><Package Version=\"2\"><Compatibility><Addon Mod=\"Other\"/></Compatibility></Package><Package Version=\"3\"><Compatibility/></Package>"));
        var mod = tree.Build().Mod("Addon")!;
        mod.LaunchCompatibility("1").Main.Should().ContainSingle();
        mod.LaunchCompatibility("2").Main.Should().BeEmpty();
        mod.LaunchCompatibility("2").Addons.Single().Compatible.Should().BeTrue();
        mod.LaunchCompatibility("3").Main.Should().BeEmpty();
        mod.LaunchCompatibility("3").Addons.Should().BeEmpty();
        mod.LaunchCompatibility("missing").Main.Should().ContainSingle();
    }

    [TestMethod]
    public void InheritedStyle_DoesNotDiscardChildCompatibilityOrLinks()
    {
        using var tree = new Tree("<Base ID=\"Common\" Kind=\"Mod\"><Style><Controls><PrimaryLabel><FontSize>12</FontSize></PrimaryLabel></Controls></Style></Base>" +
            Mod("Main") + Mod("Addon", "IsAddon=\"true\" InheritFrom=\"Common\"", "<Compatibility><Main Mod=\"Main\"/></Compatibility>"));
        var mod = tree.Build().Mod("Addon")!;
        mod.IsAddon.Should().BeTrue();
        mod.Raw.Find("Style").Should().NotBeNull();
        mod.LaunchCompatibility("1").Main.Should().ContainSingle();
    }

    [TestMethod]
    [DataRow("<Main Mod=\"Missing\"/>")]
    [DataRow("<Main Mod=\"Addon\"/>")]
    [DataRow("<Main Mod=\"Main\"/><Main Mod=\"main\"/>")]
    [DataRow("<Main Mod=\"Main\" Compatible=\"false\"/>")]
    [DataRow("<Addon Mod=\"Main\"/>")]
    [DataRow("<Addon Mod=\"Other\"/><Addon Mod=\"other\"/>")]
    [DataRow("<Main Mod=\"Main\"><Version> </Version></Main>")]
    [DataRow("<Main Mod=\"Main\"><Version>1</Version><Version>1</Version></Main>")]
    [DataRow("<Addon Mod=\"Other\" Compatible=\"false\"><Version>1</Version></Addon>")]
    public void Build_RejectsInvalidReferencesAndAmbiguousRules(string rules)
    {
        using var tree = new Tree(Mod("Main") + Mod("Other", "IsAddon=\"true\"") + Mod("Addon", "IsAddon=\"true\"", "<Compatibility>" + rules + "</Compatibility>"));
        Action build = () => tree.Build();
        build.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void BasePackageCompatibility_CannotBeInherited()
    {
        using var tree = new Tree("<Base ID=\"Common\" Kind=\"Mod\"><Packages><Package Version=\"1\"><Compatibility/></Package></Packages></Base>" + Mod("Addon", "IsAddon=\"true\" InheritFrom=\"Common\""));
        Action build = () => tree.Build();
        build.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void ReservedVanillaIdAndApplicationPackageCompatibility_AreRejected()
    {
        using var reserved = new Tree(Mod("vanilla"));
        Action first = () => reserved.Build();
        first.Should().Throw<InvalidOperationException>();
        using var application = new Tree("<Application ID=\"App\"><Version>1</Version><DisplayName Language=\"zh\">App</DisplayName><DisplayName Language=\"en\">App</DisplayName><Packages><Package Version=\"1\"><Compatibility/></Package></Packages></Application>");
        Action second = () => application.Build();
        second.Should().Throw<InvalidOperationException>();
    }
}
