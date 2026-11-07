using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 路径收敛：Include 与资源 Source 里的 .. / 绝对路径 / UNC 越出源根必须在读取目标前被拒绝，
/// 留在源根内的规范化路径不能被误伤。
/// </summary>
[TestClass]
public class PathSecurityTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"path-security-{Guid.NewGuid():N}");
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

    [TestMethod]
    public void Include_ParentTraversal_EscapingRoot_Rejected()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "outside.xml", "<Metadata />");
            WriteFile(root, "source/entry.xml", """
<Metadata>
  <Includes>
    <Include Source="../outside.xml" />
  </Includes>
</Metadata>
""");

            var act = () => MetadataBuilder.LoadSource(Path.Combine(root, "source", "entry.xml"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*Include 越出源目录*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Include_NestedParentTraversal_EscapingRoot_Rejected()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "outside.xml", "<Metadata />");
            WriteFile(root, "source/entry.xml", """
<Metadata>
  <Includes>
    <Include Source="sub/../../outside.xml" />
  </Includes>
</Metadata>
""");

            var act = () => MetadataBuilder.LoadSource(Path.Combine(root, "source", "entry.xml"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*Include 越出源目录*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Include_AbsolutePathOutsideRoot_Rejected()
    {
        var root = CreateTempDir();
        try
        {
            var outside = Path.Combine(root, "outside.xml");
            WriteFile(root, "outside.xml", "<Metadata />");
            WriteFile(root, "source/entry.xml", $"""
<Metadata>
  <Includes>
    <Include Source="{outside}" />
  </Includes>
</Metadata>
""");

            var act = () => MetadataBuilder.LoadSource(Path.Combine(root, "source", "entry.xml"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*Include 越出源目录*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Include_UncPath_Rejected()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "source/entry.xml", """
<Metadata>
  <Includes>
    <Include Source="\\server\share\outside.xml" />
  </Includes>
</Metadata>
""");

            var act = () => MetadataBuilder.LoadSource(Path.Combine(root, "source", "entry.xml"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*Include 越出源目录*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Include_DotDotStayingInsideRoot_Allowed()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "source/entry.xml", """
<Metadata>
  <Includes>
    <Include Source="sub/../sub/mod.xml" />
  </Includes>
</Metadata>
""");
            WriteFile(root, "source/sub/mod.xml", """
<Metadata>
  <Mod ID="Demo">
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
  </Mod>
</Metadata>
""");

            var loaded = MetadataBuilder.LoadSource(Path.Combine(root, "source", "entry.xml"));

            loaded.Mods().Should().ContainSingle(m => m.Id == "Demo");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Resource_ParentTraversal_EscapingRoot_Rejected()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "outside.png", "png");
            WriteFile(root, "source/entry.xml", """
<Metadata>
  <Image ID="outside" Source="../outside.png" />
</Metadata>
""");

            var act = () => MetadataBuilder.LoadSource(Path.Combine(root, "source", "entry.xml"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*资源 Source 越出源目录*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Resource_NestedParentTraversal_EscapingRoot_Rejected()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "outside.png", "png");
            WriteFile(root, "source/entry.xml", """
<Metadata>
  <Image ID="outside" Source="assets/../../outside.png" />
</Metadata>
""");

            var act = () => MetadataBuilder.LoadSource(Path.Combine(root, "source", "entry.xml"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*资源 Source 越出源目录*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Resource_AbsolutePathOutsideRoot_Rejected()
    {
        var root = CreateTempDir();
        try
        {
            var outside = Path.Combine(root, "outside.png");
            WriteFile(root, "outside.png", "png");
            WriteFile(root, "source/entry.xml", $"""
<Metadata>
  <Image ID="outside" Source="{outside}" />
</Metadata>
""");

            var act = () => MetadataBuilder.LoadSource(Path.Combine(root, "source", "entry.xml"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*资源 Source 越出源目录*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Resource_UncPath_Rejected()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "source/entry.xml", """
<Metadata>
  <Image ID="outside" Source="\\server\share\outside.png" />
</Metadata>
""");

            var act = () => MetadataBuilder.LoadSource(Path.Combine(root, "source", "entry.xml"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*资源 Source 越出源目录*");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Resource_DotDotStayingInsideRoot_Allowed()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "source/entry.xml", """
<Metadata>
  <Includes>
    <Include Source="sub/posts.xml" />
  </Includes>
  <Image ID="local" Source="images/../images/pic.png" />
</Metadata>
""");
            WriteFile(root, "source/sub/posts.xml", """
<Metadata>
  <Image ID="pic" Source="../images/pic.png" />
</Metadata>
""");
            WriteFile(root, "source/images/pic.png", "png");

            var loaded = MetadataBuilder.LoadSource(Path.Combine(root, "source", "entry.xml"));

            loaded.Images().Single(i => i.Id == "entry:local").Source.Should().Be("images/pic.png");
            loaded.Images().Single(i => i.Id == "sub/posts:pic").Source.Should().Be("images/pic.png");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Resource_UrlNodeWithEscapingSource_NotPathChecked()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "outside.png", "png");
            WriteFile(root, "source/entry.xml", """
<Metadata>
  <Image ID="remote" Source="../outside.png" Url="https://example.com/pic.png" />
</Metadata>
""");

            // 外链图片不落盘，Source 不参与路径解析，越界不应误伤。
            var loaded = MetadataBuilder.LoadSource(Path.Combine(root, "source", "entry.xml"));

            var image = loaded.Images().Single(i => i.Id == "entry:remote");
            image.Source.Should().Be("../outside.png");
            image.Url.Should().Be("https://example.com/pic.png");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Entry_OutsideExplicitSourceRoot_Rejected()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "other/entry.xml", "<NotValidXml");

            var act = () => MetadataBuilder.LoadSource(
                Path.Combine(root, "other", "entry.xml"),
                Path.Combine(root, "source"));

            act.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            DeleteTemp(root);
        }
    }
}
