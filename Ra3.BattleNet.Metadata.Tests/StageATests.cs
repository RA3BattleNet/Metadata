using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class StageATests
{
    [TestMethod]
    public void Build_RepoSample_FlattensWithoutInclude_AndResolvesIds()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var src = Path.Combine(repoRoot, "Metadata");
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

            var manifest = loaded.GetAllElements("Manifest").First(m => m.Get("ID") == "manifest-1.5.2.0");
            var source = manifest.Get("Source");
            source.Should().NotBeNullOrWhiteSpace();
            File.Exists(Path.Combine(dst, source!.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();

            var md = loaded.GetAllElements("Markdown").First(m => m.Get("ID") == "changelog-zh-1.5.2.0");
            var mdSource = md.Get("Source");
            File.Exists(Path.Combine(dst, mdSource!.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();
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
        Directory.CreateDirectory(src);
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
        Directory.CreateDirectory(src);
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
        Directory.CreateDirectory(temp);
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
        Directory.CreateDirectory(src);
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
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var entry = Path.Combine(repoRoot, "Metadata", "metadata.xml");
        var doc = MetadataFlattener.Flatten(entry, Path.Combine(repoRoot, "Metadata"), "1.0", "rev");
        doc.Descendants().Any(e => e.Name.LocalName is "Include" or "Includes").Should().BeFalse();
        doc.Root!.Attribute("SchemaVersion")!.Value.Should().Be("1.0");
        XDocument.Parse(doc.ToString()).Root!.Attribute("ContentRevision")!.Value.Should().Be("rev");
    }
}
