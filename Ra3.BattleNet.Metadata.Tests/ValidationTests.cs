using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// MetadataValidator：Mod / Application 引用字段与登记节点（Image / Markdown / Manifest）的断引用校验。
/// 用临时源树 + MetadataBuilder.LoadSource 构造数据，覆盖 Include、短名与跨文件前缀 ID。
/// </summary>
[TestClass]
public class ValidationTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"metadata-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void DeleteTemp(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private static Metadata LoadTree(string root) =>
        MetadataBuilder.LoadSource(Path.Combine(root, "metadata.xml"));

    [TestMethod]
    public void EnsureReferences_AllReferenceFieldsResolve_DoesNotThrow()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="images/res.xml" />
    <Include Source="news/news.xml" />
    <Include Source="manifests/1.0.xml" />
  </Includes>
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <Icon>icon</Icon>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
    <Packages>
      <Package Version="1.0">
        <Manifest>manifest-1</Manifest>
      </Package>
    </Packages>
    <Posts>
      <Post DateTime="2026-07-12T00:00:00+08:00">
        <HeadImage>head</HeadImage>
        <Contents>
          <Content Language="zh">news</Content>
          <Content Language="en">news</Content>
        </Contents>
      </Post>
    </Posts>
    <Style>
      <Logo>logo</Logo>
      <Background>
        <Image>bg</Image>
      </Background>
    </Style>
  </Mod>
</Metadata>
""");
            WriteFile(root, "images/res.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Image ID="icon" Source="icon.png" />
  <Image ID="head" Source="head.jpg" />
  <Image ID="logo" Source="logo.png" />
  <Image ID="bg" Source="bg.png" />
</Metadata>
""");
            WriteFile(root, "news/news.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Markdown ID="news" Source="news.md" />
</Metadata>
""");
            WriteFile(root, "manifests/1.0.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Manifest ID="manifest-1" HashAlgorithm="CRC32C">
    <File Hash="3507CD54">
      <FileName>demo.lyi</FileName>
      <RelativePath>/</RelativePath>
      <KindOf>MOD;</KindOf>
    </File>
  </Manifest>
</Metadata>
""");

            var loaded = LoadTree(root);

            // 短名引用在展平时已限定为跨文件前缀 ID，校验器比对的是限定后的 ID。
            loaded.Find("Mod:Icon")!.Value.Should().Be("images/res:icon");
            loaded.Find("Mod:Packages:Package:Manifest")!.Value.Should().Be("manifests/1.0:manifest-1");

            MetadataValidator.FindMissingReferences(loaded).Should().BeEmpty();

            var act = () => MetadataValidator.EnsureReferences(loaded);
            act.Should().NotThrow();
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void FindMissingReferences_CrossFilePrefixedId_Resolves()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", """
<Metadata>
  <Includes>
    <Include Source="shared/resources.xml" />
  </Includes>
  <Mod ID="Demo">
    <Icon>icon</Icon>
  </Mod>
</Metadata>
""");
            WriteFile(root, "shared/resources.xml", """
<Metadata>
  <Image ID="icon" Source="icon.png" />
</Metadata>
""");

            var loaded = LoadTree(root);

            // 登记在 Include 文件里：ID 带该文件路径前缀，入口里的短名引用被改写过去。
            loaded.Find("Mod:Icon")!.Value.Should().Be("shared/resources:icon");
            MetadataValidator.FindMissingReferences(loaded).Should().BeEmpty();
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void FindMissingReferences_CaseOnlyDifference_Resolves()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", """
<Metadata>
  <Includes>
    <Include Source="res.xml" />
  </Includes>
  <Mod ID="Demo">
    <Icon>RES:ICON</Icon>
  </Mod>
</Metadata>
""");
            WriteFile(root, "res.xml", """
<Metadata>
  <Image ID="icon" Source="icon.png" />
</Metadata>
""");

            var loaded = LoadTree(root);

            // 展平按 Ordinal 改写，纯大小写不同的引用保持原样；校验按 OrdinalIgnoreCase 认命中。
            loaded.Find("Mod:Icon")!.Value.Should().Be("RES:ICON");
            MetadataValidator.FindMissingReferences(loaded).Should().BeEmpty();
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void EnsureReferences_MissingIcons_ReportsBothInvalidReferencesAndThrows()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", """
<Metadata>
  <Mod ID="Demo">
    <Icon>ghost-icon</Icon>
  </Mod>
  <Application ID="App">
    <Icon>ghost-app-icon</Icon>
  </Application>
</Metadata>
""");

            var loaded = LoadTree(root);

            var missing = MetadataValidator.FindMissingReferences(loaded);
            missing.Should().HaveCount(2);
            missing.Should().Contain(item => item.Contains("ghost-app-icon", StringComparison.Ordinal));
            missing.Should().Contain(item => item.Contains("ghost-icon", StringComparison.Ordinal));

            var act = () => MetadataValidator.EnsureReferences(loaded);
            act.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void FindMissingReferences_MultipleBrokenFields_ReportsEachWithEntityId()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", """
<Metadata>
  <Mod ID="Demo">
    <Icon>ghost-icon</Icon>
    <Packages>
      <Package Version="1.0">
        <Manifest>ghost-manifest</Manifest>
      </Package>
    </Packages>
    <Posts>
      <Post DateTime="2026-07-12T00:00:00+08:00">
        <HeadImage>ghost-head</HeadImage>
        <Contents>
          <Content Language="zh">ghost-content</Content>
        </Contents>
      </Post>
    </Posts>
    <Style>
      <Logo>ghost-logo</Logo>
      <Background>
        <Image>ghost-background</Image>
      </Background>
    </Style>
  </Mod>
</Metadata>
""");

            var loaded = LoadTree(root);

            MetadataValidator.FindMissingReferences(loaded).Should().Equal(
                "Mod (Demo): Icon -> ghost-icon",
                "Mod (Demo): Manifest -> ghost-manifest",
                "Mod (Demo): Post HeadImage -> ghost-head",
                "Mod (Demo): Post Content -> ghost-content",
                "Mod (Demo): Logo -> ghost-logo",
                "Mod (Demo): Background Image -> ghost-background");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void EnsureReferences_HeadImageReferencingMarkdown_IsReported()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", """
<Metadata>
  <Includes>
    <Include Source="news/news.xml" />
  </Includes>
  <Mod ID="Demo">
    <Icon>news</Icon>
    <Posts>
      <Post DateTime="2026-07-12T00:00:00+08:00">
        <HeadImage>news</HeadImage>
      </Post>
    </Posts>
  </Mod>
</Metadata>
""");
            WriteFile(root, "news/news.xml", """
<Metadata>
  <Markdown ID="news" Source="news.md" />
</Metadata>
""");

            var loaded = LoadTree(root);

            // Icon 查登记全集，可以引 Markdown；HeadImage 只认 Image 登记，同一个 ID 必须报错。
            loaded.Find("Mod:Icon")!.Value.Should().Be("news/news:news");
            loaded.Find("Mod:Posts:Post:HeadImage")!.Value.Should().Be("news/news:news");

            MetadataValidator.FindMissingReferences(loaded)
                .Should().Equal("Mod (Demo): Post HeadImage -> news/news:news");

            var act = () => MetadataValidator.EnsureReferences(loaded);
            var ex = act.Should().Throw<InvalidOperationException>().Which;
            ex.Message.Should().Contain("Post HeadImage").And.Contain("news/news:news");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void EnsureReferences_PackageManifestWithoutRegistration_IsReported()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", """
<Metadata>
  <Mod ID="Demo">
    <Packages>
      <Package Version="1.0">
        <Manifest>ghost-manifest</Manifest>
      </Package>
    </Packages>
  </Mod>
</Metadata>
""");

            var loaded = LoadTree(root);

            MetadataValidator.FindMissingReferences(loaded)
                .Should().Equal("Mod (Demo): Manifest -> ghost-manifest");

            var act = () => MetadataValidator.EnsureReferences(loaded);
            var ex = act.Should().Throw<InvalidOperationException>().Which;
            ex.Message.Should().Contain("Manifest").And.Contain("ghost-manifest");
        }
        finally
        {
            DeleteTemp(root);
        }
    }
}
