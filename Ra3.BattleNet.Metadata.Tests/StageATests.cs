using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class StageATests
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
    public void Build_RepoSample_FlattensWithoutInclude_AndResolvesIds()
    {
        var src = RepoMetadataDir;
        var dst = Path.Combine(Path.GetTempPath(), $"stage-a-{Guid.NewGuid():N}");

        try
        {
            MetadataBuilder.Build(src, dst, schemaVersion: "1.0", contentRevision: "test-rev");

            var flatPath = Path.Combine(dst, "metadata.xml");
            File.Exists(flatPath).Should().BeTrue();

            var text = File.ReadAllText(flatPath);
            text.Should().NotContain("<Include");
            text.Should().NotContain("<Includes");
            text.Should().Contain("SchemaVersion");
            text.Should().Contain("ContentRevision");
            text.Should().NotMatchRegex(@"\$\{[^}]+\}");

            var loaded = MetadataBuilder.Load(flatPath);
            loaded.Get("SchemaVersion").Should().Be("1.0");
            loaded.Get("ContentRevision").Should().Be("test-rev");
            loaded.Applications().Should().Contain(a => a.Id == "RA3BattleNet");
            loaded.Mods().Should().Contain(m => m.Id == "Corona");

            // 发布 ID = {路径前缀}:{localId}
            var registries = loaded.GetAllElements("Manifest")
                .Where(m => !string.IsNullOrEmpty(m.Get("ID"))).ToList();
            var manifest = registries
                .First(m => MetadataFlattener.LocalId(m.Get("ID")) == "manifest-1.5.2.0");
            manifest.Get("ID")!.Should().Contain(":");
            manifest.Get("ID")!.Should().EndWith(":manifest-1.5.2.0");
            var source = manifest.Get("Source");
            source.Should().NotBeNullOrWhiteSpace();
            source!.Replace('\\', '/').Should().EndWith("manifests/1.5.2.0.xml");
            source.Should().NotContain("apps.xml");
            var manifestPath = Path.Combine(dst, source.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(manifestPath).Should().BeTrue();
            File.ReadAllText(manifestPath).Should().Contain("<File").And.Contain("NativeDll.dll");

            var coronaManifest = loaded.GetAllElements("Manifest")
                .First(m => MetadataFlattener.LocalId(m.Get("ID")!) == "manifest-3229");
            coronaManifest.Get("Source")!.Replace('\\', '/').Should().EndWith("manifests/3.229.xml");

            var md = loaded.GetAllElements("Markdown")
                .First(m => MetadataFlattener.LocalId(m.Get("ID")!) == "changelog-zh-1.5.2.0");
            md.Get("ID")!.Should().Contain("changelogs");
            var mdSource = md.Get("Source");
            mdSource!.Replace('\\', '/').Should().EndWith("changelogs/zh-1.5.2.0.md");
            File.Exists(Path.Combine(dst, mdSource.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();

            // 引用已改写为限定 ID
            var app = loaded.Applications().Single(a => a.Id == "RA3BattleNet");
            app.Packages[0].ManifestId.Should().Be(manifest.Get("ID"));
            var corona = loaded.Mods().Single(m => m.Id == "Corona");
            corona.Icon.Should().EndWith(":corona-icon-64px");
            corona.Icon.Should().Contain(":");
        }
        finally
        {
            if (Directory.Exists(dst))
                Directory.Delete(dst, recursive: true);
        }
    }

    [TestMethod]
    public void Build_MissingMarkdown_HardFails_AndCleansOutput()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"stage-a-bad-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        SeedSchemas(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Markdown ID="missing-md" Source="nope.md" Hash="${MD5::}"/>
</Metadata>
""");

            var act = () => MetadataBuilder.Build(src, dst, contentRevision: "x");
            act.Should().Throw<InvalidOperationException>();
            Directory.Exists(dst).Should().BeFalse("半残输出应被清理");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_BadIdReference_HardFails()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"stage-a-id-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        SeedSchemas(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "note.md"), "# hello sample markdown body\n");
            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Markdown ID="ok-md" Source="note.md" Hash="${MD5::}"/>
  <Application ID="App">
    <Version>1.0</Version>
    <Packages>
      <Package Version="1.0">
        <Manifest>no-such-manifest</Manifest>
      </Package>
    </Packages>
  </Application>
</Metadata>
""");

            var act = () => MetadataBuilder.Build(src, dst, contentRevision: "x");
            act.Should().Throw<InvalidOperationException>().WithMessage("*no-such-manifest*");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Flatten_CircularInclude_Throws()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"stage-a-circ-{Guid.NewGuid():N}");
        SeedSchemas(temp);
        try
        {
            File.WriteAllText(Path.Combine(temp, "a.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="b.xml" Type="public" />
  </Includes>
</Metadata>
""");
            File.WriteAllText(Path.Combine(temp, "b.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="a.xml" Type="public" />
  </Includes>
</Metadata>
""");
            File.WriteAllText(Path.Combine(temp, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="a.xml" Type="public" />
  </Includes>
</Metadata>
""");

            var act = () => MetadataBuilder.Build(temp, Path.Combine(temp, "out"), contentRevision: "x");
            act.Should().Throw<InvalidOperationException>().WithMessage("*循环引用*");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_LeftoverVariable_HardFails()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"stage-a-var-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        SeedSchemas(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Tags>
    <Commit>${ENV:THIS_ENV_SHOULD_NOT_EXIST_ZZZ}</Commit>
  </Tags>
</Metadata>
""");
            var act = () => MetadataBuilder.Build(src, Path.Combine(temp, "out"), contentRevision: "x");
            act.Should().Throw<InvalidOperationException>().WithMessage("*残留*");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Flatten_Document_HasNoIncludeNodes()
    {
        var entry = Path.Combine(RepoMetadataDir, "metadata.xml");
        var doc = MetadataFlattener.Flatten(entry, RepoMetadataDir, "1.0", "rev");
        doc.Descendants().Any(e => e.Name.LocalName is "Include" or "Includes").Should().BeFalse();
        doc.Root!.Attribute("SchemaVersion")!.Value.Should().Be("1.0");
        XDocument.Parse(doc.ToString()).Root!.Attribute("ContentRevision")!.Value.Should().Be("rev");
    }
}
