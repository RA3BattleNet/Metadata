using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public class IdScopeTests
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
    public void QualifyId_UsesPathPrefixColonLocal()
    {
        MetadataFlattener.QualifyId("mods/corona/corona", "icon").Should().Be("mods/corona/corona:icon");
        MetadataFlattener.LocalId("mods/corona/corona:icon").Should().Be("icon");
        MetadataFlattener.PathPrefix(
                Path.Combine(RepoMetadataDir, "mods", "corona", "corona.xml"),
                RepoMetadataDir)
            .Replace('\\', '/').Should().Be("mods/corona/corona");
    }

    [TestMethod]
    public void QualifyId_WithManualPrefix_Throws()
    {
        var act = () => MetadataFlattener.QualifyId("mods/corona/corona", "mods/corona/corona:icon");
        act.Should().Throw<InvalidOperationException>().WithMessage("*含 ':'*");
    }

    [TestMethod]
    public void Build_DuplicateEntityId_HardFailsWithBothSources()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"id-dup-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        SeedSchemas(src);
        Directory.CreateDirectory(Path.Combine(src, "a"));
        Directory.CreateDirectory(Path.Combine(src, "b"));
        try
        {
            File.WriteAllText(Path.Combine(src, "a", "mod.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Mod ID="Same" />
</Metadata>
""");
            File.WriteAllText(Path.Combine(src, "b", "mod.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Mod ID="Same" />
</Metadata>
""");
            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="a/mod.xml" />
    <Include Source="b/mod.xml" />
  </Includes>
</Metadata>
""");

            var act = () => MetadataBuilder.Build(src, Path.Combine(temp, "out"), contentRevision: "x");
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*实体 ID 重复*")
                .WithMessage("*a/mod.xml*")
                .WithMessage("*b/mod.xml*");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_DuplicateEntityIdAcrossKinds_HardFails()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"id-dup-kind-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        SeedSchemas(src);
        Directory.CreateDirectory(Path.Combine(src, "a"));
        Directory.CreateDirectory(Path.Combine(src, "b"));
        try
        {
            File.WriteAllText(Path.Combine(src, "a", "mod.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Mod ID="Corona" />
</Metadata>
""");
            File.WriteAllText(Path.Combine(src, "b", "app.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Application ID="Corona" />
</Metadata>
""");
            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="a/mod.xml" />
    <Include Source="b/app.xml" />
  </Includes>
</Metadata>
""");

            var act = () => MetadataBuilder.Build(src, Path.Combine(temp, "out"), contentRevision: "x");
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*实体 ID 重复*")
                .WithMessage("*Corona*");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_ManualQualifiedRegistrationId_HardFails()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"id-manual-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        SeedSchemas(src);
        try
        {
            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Image ID="mods/corona/corona:icon" Source="icon.png" />
</Metadata>
""");

            var act = () => MetadataBuilder.Build(src, Path.Combine(temp, "out"), contentRevision: "x");
            act.Should().Throw<InvalidOperationException>().WithMessage("*含 ':'*");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [TestMethod]
    public void Build_SameLocalIdInDifferentMods_BothSurviveAsQualified()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"id-scope-{Guid.NewGuid():N}");
        var src = Path.Combine(temp, "src");
        SeedSchemas(src);
        Directory.CreateDirectory(Path.Combine(src, "a"));
        Directory.CreateDirectory(Path.Combine(src, "b"));
        try
        {
            File.WriteAllText(Path.Combine(src, "a", "mod.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Image ID="shared-icon" Source="icon.png" />
  <Mod ID="ModA">
    <Icon>shared-icon</Icon>
  </Mod>
</Metadata>
""");
            File.WriteAllText(Path.Combine(src, "b", "mod.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Image ID="shared-icon" Source="icon.png" />
  <Mod ID="ModB">
    <Icon>shared-icon</Icon>
  </Mod>
</Metadata>
""");
            // minimal png
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
            File.WriteAllBytes(Path.Combine(src, "a", "icon.png"), png);
            File.WriteAllBytes(Path.Combine(src, "b", "icon.png"), png);

            File.WriteAllText(Path.Combine(src, "metadata.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="a/mod.xml" />
    <Include Source="b/mod.xml" />
  </Includes>
</Metadata>
""");

            var dst = Path.Combine(temp, "out");
            MetadataBuilder.Build(src, dst, contentRevision: "scope");
            var doc = MetadataBuilder.Load(Path.Combine(dst, "metadata.xml"));

            var images = doc.GetAllElements("Image").Where(i => MetadataFlattener.LocalId(i.Get("ID")!) == "shared-icon").ToList();
            images.Should().HaveCount(2);
            images.Select(i => i.Get("ID")).Should().OnlyHaveUniqueItems();

            var modA = doc.Mods().Single(m => m.Id == "ModA");
            var modB = doc.Mods().Single(m => m.Id == "ModB");
            modA.Icon.Should().Be("a/mod:shared-icon");
            modB.Icon.Should().Be("b/mod:shared-icon");
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }
}
