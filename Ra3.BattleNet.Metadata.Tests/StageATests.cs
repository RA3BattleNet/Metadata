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
                .First(m => MetadataFlattener.LocalId(m.Get("ID")) == "manifest-1.9.9.11");
            manifest.Get("ID")!.Should().Contain(":");
            manifest.Get("ID")!.Should().EndWith(":manifest-1.9.9.11");
            var source = manifest.Get("Source");
            source.Should().NotBeNullOrWhiteSpace();
            source!.Replace('\\', '/').Should().EndWith("manifests/1.9.9.11.xml");
            source.Should().NotContain("apps.xml");
            var manifestPath = Path.Combine(dst, source.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(manifestPath).Should().BeTrue();
            File.ReadAllText(manifestPath).Should().Contain("<File").And.Contain("Ra3BattleNet_Setup_1.9.9.11.exe");

            var coronaManifest = loaded.GetAllElements("Manifest")
                .First(m => MetadataFlattener.LocalId(m.Get("ID")!) == "manifest-3258");
            coronaManifest.Get("Source")!.Replace('\\', '/').Should().EndWith("manifests/3.258.xml");

            // 引用已改写为限定 ID
            var app = loaded.Applications().Single(a => a.Id == "RA3BattleNet");
            var appXml = Path.Combine(src, "apps", "ra3battlenet", "ra3battlenet.xml");
            var appDoc = XDocument.Load(appXml);
            var localContentId = appDoc.Descendants("Application")
                .Single(a => a.Attribute("ID")!.Value == "RA3BattleNet")
                .Element("Posts")!
                .Elements("Post").Elements("Contents").Elements("Content")
                .Single(c => string.Equals((string?)c.Attribute("Language"), "zh-CN", StringComparison.OrdinalIgnoreCase))
                .Value.Trim();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>();
            pending.Enqueue(appXml);
            XElement? registration = null;
            string? registrationFile = null;
            while (pending.Count > 0)
            {
                var file = Path.GetFullPath(pending.Dequeue());
                if (!seen.Add(file))
                    continue;
                var doc = XDocument.Load(file);
                var match = doc.Descendants("Markdown")
                    .SingleOrDefault(m => string.Equals((string?)m.Attribute("ID"), localContentId, StringComparison.Ordinal));
                if (match != null)
                {
                    registration.Should().BeNull("同一入口的 Include 闭包里这条 Content 只能对应一条 Markdown");
                    registration = match;
                    registrationFile = file;
                }
                foreach (var include in doc.Descendants("Include"))
                {
                    var relative = include.Attribute("Source")?.Value;
                    if (!string.IsNullOrWhiteSpace(relative))
                        pending.Enqueue(Path.Combine(Path.GetDirectoryName(file)!, relative));
                }
            }
            registration.Should().NotBeNull();
            var sourceBody = File.ReadAllText(Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(registrationFile!)!,
                registration!.Attribute("Source")!.Value)));
            var publishedContentId = app.Raw.Find("Posts")!
                .GetAllElements("Content")
                .Single(c => string.Equals(c.Get("Language"), "zh-CN", StringComparison.OrdinalIgnoreCase))
                .Value!;
            MetadataFlattener.LocalId(publishedContentId).Should().Be(localContentId);
            var markdown = loaded.Markdowns().Single(m => m.Id == publishedContentId);
            File.ReadAllText(Path.Combine(dst, markdown.Source!.Replace('/', Path.DirectorySeparatorChar)))
                .Should().Be(sourceBody);

            app.Packages[0].ManifestId.Should().Be(manifest.Get("ID"));
            var corona = loaded.Mods().Single(m => m.Id == "Corona");
            var icon = loaded.Images().Single(image => image.Id == corona.Icon);
            var sourceDoc = XDocument.Load(Path.Combine(src, "mods", "corona", "corona.xml"));
            var localIconId = sourceDoc.Descendants("Mod").Single().Element("Icon")!.Value;
            icon.Id.Should().Be(MetadataFlattener.QualifyId("mods/corona/corona", localIconId));
        }
        finally
        {
            if (Directory.Exists(dst))
                Directory.Delete(dst, recursive: true);
        }
    }

    [TestMethod]
    public void Build_RepoSample_TransferAdFlattensForDeclaringAppOnly()
    {
        var src = RepoMetadataDir;
        var dst = Path.Combine(Path.GetTempPath(), $"stage-transfer-ad-{Guid.NewGuid():N}");

        try
        {
            MetadataBuilder.Build(src, dst, schemaVersion: "1.0", contentRevision: "transfer-ad");

            var doc = XDocument.Load(Path.Combine(dst, "metadata.xml"));
            var apps = doc.Root!.Elements("Application").ToList();

            var ra3 = apps.Single(a => (string?)a.Attribute("ID") == "RA3BattleNet");
            ra3.Element("Version")!.Value.Should().Be("1.9.9.11");
            ra3.Element("TransferAd").Should().NotBeNull();
            ra3.Element("TransferAd")!.Value.Should().Be("true");

            var content = apps.Single(a => (string?)a.Attribute("ID") == "content");
            content.Element("TransferAd").Should().BeNull("未声明 TransferAd 的应用保持缺失兼容");

            // 展平物继续满足发布 XSD
            SchemaValidator
                .ValidateFile(Path.Combine(dst, "metadata.xml"),
                              Path.Combine(src, SchemaValidator.PublishSchemaFileName))
                .Should().BeEmpty();
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
    <DisplayName Language="zh-CN">应用</DisplayName>
    <DisplayName Language="en-US">App</DisplayName>
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
    <Include Source="b.xml" />
  </Includes>
</Metadata>
""");
            File.WriteAllText(Path.Combine(temp, "b.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="a.xml" />
  </Includes>
</Metadata>
""");
            File.WriteAllText(Path.Combine(temp, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="a.xml" />
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
