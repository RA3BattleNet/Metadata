using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class SchemaTests
{
    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [TestMethod]
    public void Build_SampleTree_PassesSourceAndPublishSchema()
    {
        var src = Path.Combine(RepoRoot, "Metadata");
        var dst = Path.Combine(Path.GetTempPath(), $"schema-ok-{Guid.NewGuid():N}");
        try
        {
            MetadataBuilder.Build(src, dst, contentRevision: "schema-ok");
            var flat = Path.Combine(dst, "metadata.xml");
            File.Exists(flat).Should().BeTrue();

            var publishSchema = Path.Combine(src, SchemaValidator.PublishSchemaFileName);
            SchemaValidator.ValidateFile(flat, publishSchema).Should().BeEmpty();

            var loaded = MetadataBuilder.Load(flat);
            loaded.Applications().Should().Contain(a => a.Id == "RA3BattleNet");
            loaded.Mods().Should().Contain(m => m.Id == "Corona");
            loaded.Get("SchemaVersion").Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            if (Directory.Exists(dst))
                Directory.Delete(dst, recursive: true);
        }
    }

    [TestMethod]
    public void Build_ApplicationMissingId_FailsSourceSchema()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"schema-bad-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        var dst = Path.Combine(temp, "out");
        Directory.CreateDirectory(src);
        try
        {
            // 最小可拷贝 schema
            File.Copy(
                Path.Combine(RepoRoot, "Metadata", SchemaValidator.SourceSchemaFileName),
                Path.Combine(src, SchemaValidator.SourceSchemaFileName));
            File.Copy(
                Path.Combine(RepoRoot, "Metadata", SchemaValidator.PublishSchemaFileName),
                Path.Combine(src, SchemaValidator.PublishSchemaFileName));

            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Application>
    <Version>1.0</Version>
  </Application>
</Metadata>
""");

            var act = () => MetadataBuilder.Build(src, dst, contentRevision: "x");
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*XSD*");
            Directory.Exists(dst).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void PublishSchema_RejectsManifestWithoutSource()
    {
        var schema = Path.Combine(RepoRoot, "Metadata", SchemaValidator.PublishSchemaFileName);
        var temp = Path.Combine(Path.GetTempPath(), $"pub-bad-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(temp, """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata SchemaVersion="1.0" ContentRevision="x">
  <Manifest ID="m1" />
</Metadata>
""");
            var errors = SchemaValidator.ValidateFile(temp, schema);
            errors.Should().NotBeEmpty();
            string.Join(" ", errors).Should().Contain("Source");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    [TestMethod]
    public void ConsumerParse_CatalogResolvesIdsAndResources()
    {
        var src = Path.Combine(RepoRoot, "Metadata");
        var dst = Path.Combine(Path.GetTempPath(), $"parse-{Guid.NewGuid():N}");
        try
        {
            MetadataBuilder.Build(src, dst, contentRevision: "parse");
            var doc = MetadataBuilder.Load(Path.Combine(dst, "metadata.xml"));

            var app = doc.Catalog().Application("RA3BattleNet");
            app.Should().NotBeNull();
            app!.Version.Should().Be("1.5.2.0");
            app.Packages.Should().NotBeEmpty();
            var manifestId = app.Packages[0].ManifestId;
            manifestId.Should().NotBeNullOrWhiteSpace();

            var reg = doc.GetAllElements("Manifest").First(m => m.Get("ID") == manifestId);
            var rel = reg.Get("Source")!;
            rel.Replace('\\', '/').Should().Contain("manifests/");
            File.Exists(Path.Combine(dst, rel.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dst))
                Directory.Delete(dst, recursive: true);
        }
    }
}
