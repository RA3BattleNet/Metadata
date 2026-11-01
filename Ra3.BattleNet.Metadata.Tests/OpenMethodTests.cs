using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// MetadataBuilder.Open：统一读取入口，按入口内容自动区分作者源树（带 Include）与已展平发布物。
/// 源树走内存展平并限定短 ID；发布物直接解析；Load / LoadSource 的既有约定不变。
/// </summary>
[TestClass]
public class OpenMethodTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"open-method-{Guid.NewGuid():N}");
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
    public void Open_SourceTreeWithInclude_FlattensAndQualifiesShortIds()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Includes>
    <Include Source="base.xml" />
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
            WriteFile(root, "base.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Image ID="banner" Source="images/banner.png" />
</Metadata>
""");

            var loaded = MetadataBuilder.Open(Path.Combine(root, "metadata.xml"));

            loaded.GetAllElements("Include").Should().BeEmpty();
            loaded.GetAllElements("Includes").Should().BeEmpty();
            loaded.Get("ContentRevision").Should().Be("local-source", "源树没有发布修订");
            loaded.Get("SchemaVersion").Should().Be(MetadataSchema.Current);
            loaded.Images().Select(image => image.Id)
                .Should().BeEquivalentTo(new[] { "metadata:icon", "base:banner" },
                    "ID 前缀是文件相对源根、去 .xml 的路径");

            var mod = loaded.Mods().Single();
            mod.Id.Should().Be("Demo");
            mod.Icon.Should().Be("metadata:icon", "短名引用同步改写成限定 ID");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Open_PublishedDocumentWithoutInclude_ParsesAsIs()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", """
<Metadata SchemaVersion="1.0" ContentRevision="rev-1">
  <Image ID="corona:icon" Source="mods/corona/images/icon.png" />
  <Mod ID="Corona">
    <CurrentVersion>3.259</CurrentVersion>
    <Icon>corona:icon</Icon>
    <DisplayName Language="zh">日冕</DisplayName>
    <DisplayName Language="en">Corona</DisplayName>
  </Mod>
</Metadata>
""");

            var loaded = MetadataBuilder.Open(Path.Combine(root, "metadata.xml"));

            loaded.Get("ContentRevision").Should().Be("rev-1", "发布物不展平，根上的发布修订原样保留");
            loaded.Get("SchemaVersion").Should().Be("1.0");
            loaded.Images().Single().Id.Should().Be("corona:icon", "不展平就不加前缀");
            loaded.Mods().Single().Icon.Should().Be("corona:icon");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Open_ForceSourceTree_QualifiesIdsInDocumentWithoutInclude()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "mod.xml", """
<?xml version="1.0" encoding="UTF-8"?>
<Metadata>
  <Image ID="icon" Source="images/icon.png" />
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <Icon>icon</Icon>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
  </Mod>
</Metadata>
""");
            var entry = Path.Combine(root, "mod.xml");

            var published = MetadataBuilder.Open(entry);
            published.Images().Single().Id.Should().Be("icon", "默认按发布物解析，ID 原样");
            published.Get("ContentRevision").Should().BeNull();

            var sourceTree = MetadataBuilder.Open(entry, new MetadataSourceOptions { ForceSourceTree = true });
            sourceTree.Images().Single().Id.Should().Be("mod:icon", "强制源树模式，前缀来自入口文件相对源根、去 .xml 的路径");
            sourceTree.Get("ContentRevision").Should().Be("local-source");
            sourceTree.Mods().Single().Id.Should().Be("Demo", "实体 ID 不加前缀");
            sourceTree.Mods().Single().Icon.Should().Be("mod:icon");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Open_DoesNotChangeLoad_SourceTreeStillRejectedByLoad()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", """
<Metadata>
  <Includes>
    <Include Source="base.xml" />
  </Includes>
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
  </Mod>
</Metadata>
""");
            WriteFile(root, "base.xml", "<Metadata />");
            var entry = Path.Combine(root, "metadata.xml");

            var load = () => MetadataBuilder.Load(entry);
            load.Should().Throw<InvalidOperationException>().WithMessage("*Include*");

            var open = () => MetadataBuilder.Open(entry);
            open.Should().NotThrow("Open 是新增入口，Load 的发布物约定不变");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Open_ValidateReferences_ThrowsOnMissingReference_AndSkipsWhenDisabled()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", """
<Metadata SchemaVersion="1.0" ContentRevision="rev-1">
  <Image ID="known" Source="images/known.png" />
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <Icon>missing</Icon>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
  </Mod>
</Metadata>
""");
            var entry = Path.Combine(root, "metadata.xml");

            var relaxed = MetadataBuilder.Open(entry);
            relaxed.Mods().Single().Icon.Should().Be("missing", "默认不做引用校验，断引用原样保留");

            var strict = () => MetadataBuilder.Open(entry, new MetadataSourceOptions { ValidateReferences = true });
            strict.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*元数据引用校验失败*")
                .WithMessage("*Demo*")
                .WithMessage("*missing*");

            WriteFile(root, "metadata.xml", """
<Metadata SchemaVersion="1.0" ContentRevision="rev-1">
  <Image ID="known" Source="images/known.png" />
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <Icon>known</Icon>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
  </Mod>
</Metadata>
""");
            var fixedTree = MetadataBuilder.Open(entry, new MetadataSourceOptions { ValidateReferences = true });
            fixedTree.Mods().Single().Icon.Should().Be("known");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Open_CustomSource_InMemorySourceTree_FlattensWhatTheSourceReturns()
    {
        var source = new MemoryXmlSource();
        source.Add("/mem/root/metadata.xml", """
<Metadata>
  <Includes>
    <Include Source="base.xml" />
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
        source.Add("/mem/root/base.xml", """
<Metadata>
  <Image ID="banner" Source="images/banner.png" />
</Metadata>
""");

        var loaded = MetadataBuilder.Open(
            "/mem/root/metadata.xml", new MetadataSourceOptions { CustomSource = source });

        loaded.GetAllElements("Include").Should().BeEmpty();
        loaded.Get("ContentRevision").Should().Be("local-source");
        loaded.Images().Select(image => image.Id)
            .Should().BeEquivalentTo("metadata:icon", "base:banner");

        var mod = loaded.Mods().Single();
        mod.Id.Should().Be("Demo");
        mod.Icon.Should().Be("metadata:icon");
    }

    [TestMethod]
    public void Open_CustomSource_PublishedDocument_ParsesWithoutFlattening()
    {
        var source = new MemoryXmlSource();
        source.Add("/mem/published.xml", """
<Metadata SchemaVersion="1.0" ContentRevision="rev-9">
  <Image ID="corona:icon" Source="mods/corona/images/icon.png" />
  <Mod ID="Corona">
    <CurrentVersion>3.259</CurrentVersion>
    <Icon>corona:icon</Icon>
    <DisplayName Language="zh">日冕</DisplayName>
    <DisplayName Language="en">Corona</DisplayName>
  </Mod>
</Metadata>
""");

        var loaded = MetadataBuilder.Open(
            "/mem/published.xml", new MetadataSourceOptions { CustomSource = source });

        loaded.Get("ContentRevision").Should().Be("rev-9", "自定义源里的发布物直接解析，不套展平");
        loaded.Images().Single().Id.Should().Be("corona:icon");
        loaded.Mods().Single().Icon.Should().Be("corona:icon");
    }

    [TestMethod]
    public void Open_BlankPath_ThrowsArgumentException()
    {
        var empty = () => MetadataBuilder.Open("");
        empty.Should().Throw<ArgumentException>().WithParameterName("pathOrUrl");

        var whitespace = () => MetadataBuilder.Open("   ");
        whitespace.Should().Throw<ArgumentException>().WithParameterName("pathOrUrl");

        var missing = () => MetadataBuilder.Open(null!);
        missing.Should().Throw<ArgumentException>().WithParameterName("pathOrUrl");
    }

    [TestMethod]
    public void Open_MalformedEntry_PropagatesXmlException()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", "<Metadata><Mod>");

            var act = () => MetadataBuilder.Open(Path.Combine(root, "metadata.xml"));

            act.Should().Throw<XmlException>("入口探测阶段的 XML 解析失败不吞异常");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Open_ExplicitSourceRoot_AllowsIncludeOutsideEntryDirectory()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "shared/base.xml", """
<Metadata>
  <Image ID="banner" Source="images/banner.png" />
</Metadata>
""");
            WriteFile(root, "mods/mod.xml", """
<Metadata>
  <Includes>
    <Include Source="../shared/base.xml" />
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
            var entry = Path.Combine(root, "mods", "mod.xml");

            var byEntryDir = () => MetadataBuilder.Open(entry);
            byEntryDir.Should().Throw<InvalidOperationException>().WithMessage("*Include 越出源目录*");

            var loaded = MetadataBuilder.Open(entry, new MetadataSourceOptions { SourceRoot = root });
            loaded.Images().Select(image => image.Id)
                .Should().BeEquivalentTo("mods/mod:icon", "shared/base:banner");
            loaded.Mods().Single().Id.Should().Be("Demo");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    [TestMethod]
    public void Open_FileUri_ReadsLocalSourceTree()
    {
        var root = CreateTempDir();
        try
        {
            WriteFile(root, "metadata.xml", """
<Metadata>
  <Includes>
    <Include Source="base.xml" />
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
            WriteFile(root, "base.xml", """
<Metadata>
  <Image ID="banner" />
</Metadata>
""");
            var fileUri = new Uri(Path.Combine(root, "metadata.xml")).AbsoluteUri;
            fileUri.Should().StartWith("file://");

            var loaded = MetadataBuilder.Open(fileUri);

            loaded.GetAllElements("Includes").Should().BeEmpty();
            loaded.Images().Select(image => image.Id)
                .Should().BeEquivalentTo("metadata:icon", "base:banner");
            loaded.Mods().Single().Icon.Should().Be("metadata:icon");
            loaded.Get("ContentRevision").Should().Be("local-source");
        }
        finally
        {
            DeleteTemp(root);
        }
    }

    private const string RemoteRoot = "https://example.test/mods/";

    [TestMethod]
    public void Open_CustomSource_HttpSourceTree_FlattensRemoteIncludes()
    {
        var handler = new FakeHandler();
        handler.AddXml(RemoteRoot + "metadata.xml", """
<Metadata>
  <Includes>
    <Include Source="parts/base.xml" />
  </Includes>
  <Image ID="icon" />
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <Icon>icon</Icon>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
  </Mod>
</Metadata>
""");
        handler.AddXml(RemoteRoot + "parts/base.xml", """
<Metadata>
  <Image ID="banner" />
</Metadata>
""");
        var source = new HttpXmlSource(new Uri(RemoteRoot), handler);

        var loaded = MetadataBuilder.Open(
            RemoteRoot + "metadata.xml", new MetadataSourceOptions { CustomSource = source });

        loaded.GetAllElements("Include").Should().BeEmpty();
        loaded.Get("ContentRevision").Should().Be("local-source");
        loaded.Images().Select(image => image.Id)
            .Should().BeEquivalentTo("metadata:icon", "parts/base:banner");
        loaded.Mods().Single().Icon.Should().Be("metadata:icon");

        handler.Requests.Should().Contain(RemoteRoot + "metadata.xml");
        handler.Requests.Should().Contain(RemoteRoot + "parts/base.xml");
        handler.Requests.Should().OnlyContain(url => url.StartsWith(RemoteRoot, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Open_CustomSource_ForceSourceTree_QualifiesIdsWithoutInclude()
    {
        var source = new MemoryXmlSource();
        source.Add("/mem/root/mod.xml", """
<Metadata>
  <Image ID="icon" />
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <Icon>icon</Icon>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
  </Mod>
</Metadata>
""");

        var published = MetadataBuilder.Open(
            "/mem/root/mod.xml", new MetadataSourceOptions { CustomSource = source });
        published.Images().Single().Id.Should().Be("icon", "默认按发布物解析，ID 原样");
        published.Get("ContentRevision").Should().BeNull();

        var sourceTree = MetadataBuilder.Open("/mem/root/mod.xml", new MetadataSourceOptions
        {
            CustomSource = source,
            ForceSourceTree = true,
        });
        sourceTree.Images().Single().Id.Should().Be("mod:icon", "强制源树模式，前缀来自入口路径去 .xml");
        sourceTree.Mods().Single().Icon.Should().Be("mod:icon");
        sourceTree.Get("ContentRevision").Should().Be("local-source");
    }

    [TestMethod]
    public void Open_CustomSource_IsNotDisposedByOpen()
    {
        var source = new MemoryXmlSource();
        source.Add("/mem/published.xml", """
<Metadata SchemaVersion="1.0" ContentRevision="rev-1">
  <Image ID="known" />
  <Mod ID="Demo">
    <CurrentVersion>1.0</CurrentVersion>
    <Icon>known</Icon>
    <DisplayName Language="zh">演示</DisplayName>
    <DisplayName Language="en">Demo</DisplayName>
  </Mod>
</Metadata>
""");

        MetadataBuilder.Open("/mem/published.xml", new MetadataSourceOptions { CustomSource = source })
            .Mods().Single().Id.Should().Be("Demo");

        source.Disposed.Should().BeFalse("自定义源归调用方所有，Open 不能释放");
    }

    /// <summary>
    /// 只存抽象路径的内存源：不映射文件系统，路径按 <c>/</c> 分隔原样解释，
    /// 用来验证 Open 拿到自定义源时不需要任何本地文件。
    /// </summary>
    private sealed class MemoryXmlSource : IMetadataXmlSource, IDisposable
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        /// <summary>是否被释放过：Open 不得释放调用方注入的源。</summary>
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        public void Add(string path, string xml) => _files[path] = xml;

        public bool Exists(string path) => _files.ContainsKey(path);

        public XDocument LoadDocument(string path) =>
            _files.TryGetValue(path, out var xml)
                ? XDocument.Parse(xml)
                : throw new FileNotFoundException($"找不到文件: {path}");

        public string Combine(string basePath, string relativePath) =>
            $"{basePath.TrimEnd('/')}/{relativePath.Replace('\\', '/').TrimStart('/')}";

        public string? GetDirectoryName(string path)
        {
            var lastSlash = path.LastIndexOf('/');
            return lastSlash <= 0 ? null : path[..lastSlash];
        }

        public string GetRelativePath(string fromPath, string toPath)
        {
            var prefix = fromPath.TrimEnd('/') + "/";
            return toPath.StartsWith(prefix, StringComparison.Ordinal) ? toPath[prefix.Length..] : toPath;
        }

        public string GetFullPath(string path) => path;

        public bool IsPathRooted(string path) => path.StartsWith('/');
    }

    /// <summary>按绝对 URL 路由的内存报文 handler：未登记的 URL 一律 404，绝不发真实请求。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = new();

        public void AddXml(string url, string xml) =>
            _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));

        // HttpXmlSource 走同步 Send（IMetadataXmlSource 是同步接口），自定义 handler 必须覆盖这个重载
        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken) => Respond(request);

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            return _routes.TryGetValue(url, out var factory)
                ? factory()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
