using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// LoadSource：本地作者源文件在内存里展平 Include、合并继承、限定短 ID，不写盘、不替换 ENV。
/// </summary>
[TestClass]
public class SourceLoadTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"source-load-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteBytes(string root, string relative, byte[] content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static void DeleteTemp(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private static byte[] TinyPng() =>
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [TestMethod]
    public void LoadSource_ExpandsIncludesInheritanceAndShortIds()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "mod.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="base.xml" />
  </Includes>
  <Image ID="icon" Source="images/icon.png" />
  <Mod ID="Demo" InheritFrom="StandardMod">
    <CurrentVersion>2.0</CurrentVersion>
    <Icon>icon</Icon>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
  </Mod>
</Metadata>
""");
            WriteFile(root, "base.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Base ID="StandardMod" Kind="Mod">
    <Style>
      <Controls>
        <PrimaryLabel>
          <FontSize>14</FontSize>
        </PrimaryLabel>
      </Controls>
    </Style>
  </Base>
</Metadata>
""");
            WriteBytes(root, "images/icon.png", TinyPng());

            var loaded = MetadataBuilder.LoadSource(Path.Combine(root, "mod.xml"));

            loaded.GetAllElements("Include").Should().BeEmpty();
            loaded.GetAllElements("Includes").Should().BeEmpty();
            loaded.GetAllElements("Base").Should().BeEmpty();

            var mod = loaded.Mods().Single();
            mod.Id.Should().Be("Demo");
            mod.Raw.Get("InheritFrom").Should().BeNull();
            mod.Icon.Should().Be("mod:icon");
            mod.Raw.Find("Style:Controls:PrimaryLabel:FontSize")!.Value.Should().Be("14");

            var image = loaded.Images().Single(i => i.Id == "mod:icon");
            image.Source.Should().Be("images/icon.png");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void LoadSource_CoronaStyleDirectory_ResolvesNestedResourcesAndManifestStub()
    {
        var root = CreateTempDir();
        try
        {
            var corona = Path.Combine(root, "corona");
            WriteFile(corona, "corona.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="manifests/manifests.xml" />
    <Include Source="posts/posts.xml" />
  </Includes>
  <Image ID="icon" Source="images/icon.png" />
  <Mod ID="Corona">
    <CurrentVersion>3.258</CurrentVersion>
    <Icon>icon</Icon>
    <DisplayName Language="zh">日冕</DisplayName>
    <DisplayName Language="en">Corona</DisplayName>
    <Packages>
      <Package Version="3.258">
        <Manifest>manifest-3258</Manifest>
      </Package>
    </Packages>
    <Posts>
      <Post DateTime="2026-07-12T00:00:00+08:00">
        <Titles>
          <Title Language="zh">更新</Title>
          <Title Language="en">Update</Title>
        </Titles>
        <HeadImage>head</HeadImage>
        <Contents>
          <Content Language="zh">news</Content>
          <Content Language="en">news</Content>
        </Contents>
      </Post>
    </Posts>
  </Mod>
</Metadata>
""");
            WriteFile(corona, "manifests/manifests.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="3.258.xml" />
  </Includes>
</Metadata>
""");
            WriteFile(corona, "manifests/3.258.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Manifest ID="manifest-3258" HashAlgorithm="CRC32C">
    <File Hash="3507CD54">
      <FileName>corona.lyi</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
    </File>
  </Manifest>
</Metadata>
""");
            WriteFile(corona, "posts/posts.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Image ID="head" Source="../images/head.jpg" />
  <Markdown ID="news" Source="news.md" />
</Metadata>
""");
            WriteBytes(corona, "images/icon.png", TinyPng());
            WriteBytes(corona, "images/head.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            WriteFile(corona, "posts/news.md", "# news");

            var loaded = MetadataBuilder.LoadSource(Path.Combine(corona, "corona.xml"));

            var mod = loaded.Mods().Single();
            mod.Icon.Should().Be("corona:icon");
            mod.Raw.Find("Posts:Post:HeadImage")!.Value.Should().Be("posts/posts:head");
            mod.Raw.Find("Posts:Post:Contents:Content")!.Value.Should().Be("posts/posts:news");

            loaded.Images().Single(i => i.Id == "corona:icon").Source.Should().Be("images/icon.png");
            loaded.Images().Single(i => i.Id == "posts/posts:head").Source.Should().Be("images/head.jpg");
            loaded.Markdowns().Single(m => m.Id == "posts/posts:news").Source.Should().Be("posts/news.md");

            // Include 的叶子清单只以登记 stub 形式留在大树里，Source 相对入口目录。
            loaded.ManifestRegistration("manifests/3.258:manifest-3258").Should().NotBeNull();
            mod.ManifestSource().Should().Be("manifests/3.258.xml");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void LoadSource_ManualLegacyStub_DiffersByPrefixButSharesLocalId()
    {
        var root = CreateTempDir();
        try
        {
            var modDir = Path.Combine(root, "mod");
            WriteFile(modDir, "mod.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Mod ID="Legacy">
    <CurrentVersion>1.0</CurrentVersion>
    <DisplayName Language="zh">旧写法</DisplayName>
    <DisplayName Language="en">Legacy</DisplayName>
    <Packages>
      <Package Version="1.0">
        <Manifest>manifest-1</Manifest>
      </Package>
    </Packages>
  </Mod>
  <Manifest ID="manifest-1" Source="manifests/1.0.xml" />
</Metadata>
""");
            WriteFile(modDir, "manifests/1.0.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Manifest ID="manifest-1" HashAlgorithm="CRC32C">
    <File Hash="3507CD54">
      <FileName>legacy.lyi</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
    </File>
  </Manifest>
</Metadata>
""");

            var loaded = MetadataBuilder.LoadSource(Path.Combine(modDir, "mod.xml"));

            var stub = loaded.GetAllElements("Manifest")
                .Single(m => m.Get("Source") == "manifests/1.0.xml");
            stub.Get("ID").Should().Be("mod:manifest-1");
            loaded.Mods().Single().ManifestSource().Should().Be("manifests/1.0.xml");

            // 叶子按同一源根加载时前缀来自文件路径，与手写 stub 的入口前缀不同；LocalId 相同。
            var leaf = MetadataBuilder.LoadSource(
                Path.Combine(modDir, "manifests", "1.0.xml"), modDir);
            var leafId = leaf.GetAllElements("Manifest").Single().Get("ID")!;
            leafId.Should().Be("manifests/1.0:manifest-1");
            leafId.Should().NotBe(stub.Get("ID"));
            MetadataFlattener.LocalId(leafId).Should().Be(MetadataFlattener.LocalId(stub.Get("ID")));
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void LoadSource_GeneratedSourceInclude_StubAndLeafIdsMatchExactly()
    {
        var root = CreateTempDir();
        try
        {
            var modDir = Path.Combine(root, "mod");
            WriteFile(modDir, "mod.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="manifests/1.0.xml" />
  </Includes>
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
    <Packages>
      <Package Version="1.0">
        <Manifest>manifest-1</Manifest>
      </Package>
    </Packages>
  </Mod>
</Metadata>
""");
            WriteFile(modDir, "manifests/1.0.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Manifest ID="manifest-1" HashAlgorithm="CRC32C">
    <File Hash="3507CD54">
      <FileName>generated.lyi</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
    </File>
  </Manifest>
</Metadata>
""");

            var loaded = MetadataBuilder.LoadSource(Path.Combine(modDir, "mod.xml"));

            var stub = loaded.GetAllElements("Manifest")
                .Single(m => m.Get("Source") == "manifests/1.0.xml");
            stub.Get("ID").Should().Be("manifests/1.0:manifest-1");
            loaded.Mods().Single().ManifestSource().Should().Be("manifests/1.0.xml");

            var leaf = MetadataBuilder.LoadSource(
                Path.Combine(modDir, "manifests", "1.0.xml"), modDir);
            leaf.GetAllElements("Manifest").Single().Get("ID").Should().Be(stub.Get("ID"));
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Parser_LoadXDocument_ParsesWrappedSavedManifestInMemory()
    {
        var savedManifest = new System.Xml.Linq.XElement("Manifest",
            new System.Xml.Linq.XAttribute("ID", "manifest-3258"),
            new System.Xml.Linq.XElement("File",
                new System.Xml.Linq.XAttribute("Hash", "3507CD54"),
                new System.Xml.Linq.XElement("FileName", "corona.lyi")));
        var document = new System.Xml.Linq.XDocument(
            new System.Xml.Linq.XElement("Metadata", new System.Xml.Linq.XElement(savedManifest)));

        var loaded = MetadataParser.Load(document);

        loaded.Name.Should().Be("Metadata");
        var manifest = loaded.Find("Manifest")!;
        manifest.Get("ID").Should().Be("manifest-3258");
        manifest.Find("File:FileName")!.Value.Should().Be("corona.lyi");
    }

    [TestMethod]
    public void Parser_LoadXDocument_RejectsSourceTreeAndNonMetadataRoot()
    {
        var withInclude = System.Xml.Linq.XDocument.Parse(
            "<Metadata><Includes><Include Source=\"mod.xml\" /></Includes></Metadata>");
        var includeAct = () => MetadataParser.Load(withInclude);
        includeAct.Should().Throw<InvalidOperationException>().WithMessage("*Include*");

        var badRoot = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("Manifest"));
        var rootAct = () => MetadataParser.Load(badRoot);
        rootAct.Should().Throw<System.Xml.XmlException>().WithMessage("*Metadata*");
    }

    [TestMethod]
    public void LoadSource_CyclicIncludes_Throws()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "entry.xml", """
<Metadata>
  <Includes>
    <Include Source="a.xml" />
  </Includes>
</Metadata>
""");
            WriteFile(root, "a.xml", """
<Metadata>
  <Includes>
    <Include Source="b.xml" />
  </Includes>
</Metadata>
""");
            WriteFile(root, "b.xml", """
<Metadata>
  <Includes>
    <Include Source="a.xml" />
  </Includes>
</Metadata>
""");

            var act = () => MetadataBuilder.LoadSource(Path.Combine(root, "entry.xml"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*循环引用*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void LoadSource_IncludeOutsideSourceRoot_RejectedBeforeReadingTarget()
    {
        var root = CreateTempDir();
        try
        {
            var source = Path.Combine(root, "source");
            WriteFile(source, "entry.xml", """
<Metadata>
  <Includes>
    <Include Source="../outside.xml" />
  </Includes>
</Metadata>
""");
            // 越界目标即使是无效 XML，也必须先被路径收敛拒绝，而不是读进来再报根节点错误。
            WriteFile(root, "outside.xml", "<NotMetadata />");

            var act = () => MetadataBuilder.LoadSource(Path.Combine(source, "entry.xml"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*Include 越出源目录*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void LoadSource_ResourceOutsideSourceRoot_RejectedBeforeProbingTarget()
    {
        var root = CreateTempDir();
        try
        {
            var source = Path.Combine(root, "source");
            WriteFile(source, "entry.xml", """
<Metadata>
  <Image ID="outside" Source="../outside.png" />
</Metadata>
""");
            WriteBytes(root, "outside.png", TinyPng());

            var act = () => MetadataBuilder.LoadSource(Path.Combine(source, "entry.xml"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*资源 Source 越出源目录*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void LoadSource_DoesNotWriteOrMutateAuthorFiles()
    {
        var root = CreateTempDir();
        try
        {
            var mod = Path.Combine(root, "mod");
            WriteFile(mod, "mod.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Tags>
    <Commit>${TIMESTAMP}</Commit>
    <Flavor>${ENV:SOURCE_LOAD_TEST_MISSING}</Flavor>
  </Tags>
  <Includes>
    <Include Source="posts/posts.xml" />
  </Includes>
  <Image ID="icon" Source="images/icon.png" />
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <Icon>icon</Icon>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
  </Mod>
</Metadata>
""");
            WriteFile(mod, "posts/posts.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Markdown ID="news" Source="news.md" />
</Metadata>
""");
            WriteFile(mod, "posts/news.md", "# news");
            WriteBytes(mod, "images/icon.png", TinyPng());

            var before = Snapshot(mod);
            var loaded = MetadataBuilder.LoadSource(Path.Combine(mod, "mod.xml"));
            var after = Snapshot(mod);

            // 作者文件原样保留，也没有生成任何新文件。
            after.Keys.Should().BeEquivalentTo(before.Keys);
            foreach (var (relative, bytes) in before)
                after[relative].Should().Equal(bytes);

            // 构建期变量不在本地读取时解析。
            loaded.Find("Tags:Commit")!.Value.Should().Be("${TIMESTAMP}");
            loaded.Find("Tags:Flavor")!.Value.Should().Be("${ENV:SOURCE_LOAD_TEST_MISSING}");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void LoadSource_DoesNotRequireOtherModsReferencedByCompatibility()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "mod.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Mod ID="AronaEva" IsAddon="true">
    <CurrentVersion>1.0</CurrentVersion>
    <DisplayName Language="zh">语音包</DisplayName>
    <DisplayName Language="en">Voice Pack</DisplayName>
    <Compatibility>
      <Main Mod="OtherMod" />
    </Compatibility>
  </Mod>
</Metadata>
""");

            var loaded = MetadataBuilder.LoadSource(Path.Combine(root, "mod.xml"));

            var mod = loaded.Mods().Single();
            mod.Id.Should().Be("AronaEva");
            mod.Raw.Find("Compatibility:Main")!.Get("Mod").Should().Be("OtherMod");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Load_StillRejectsSourceTree_WhileLoadSourceAcceptsIt()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "entry.xml", """
<Metadata>
  <Includes>
    <Include Source="mod.xml" />
  </Includes>
</Metadata>
""");
            WriteFile(root, "mod.xml", """
<Metadata>
  <Mod ID="Demo">
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
  </Mod>
</Metadata>
""");

            var entry = Path.Combine(root, "entry.xml");
            MetadataBuilder.LoadSource(entry).Mods().Should().ContainSingle(m => m.Id == "Demo");

            var act = () => Metadata.LoadFromFile(entry);
            act.Should().Throw<InvalidOperationException>().WithMessage("*Include*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    private static SortedDictionary<string, byte[]> Snapshot(string root)
    {
        var snapshot = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            snapshot[Path.GetRelativePath(root, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        return snapshot;
    }
}
