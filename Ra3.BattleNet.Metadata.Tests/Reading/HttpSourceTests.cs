using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// HttpXmlSource：URL 路径语义、GET 状态分支、重定向拒绝与 XML 正文缓存，
/// 以及统一读取器的远端组合、路径边界和不预取资源行为。
/// 全部请求由内存 handler 应答，测试不发真实网络请求。
/// </summary>
[TestClass]
public class HttpSourceTests
{
    private const string Root = "https://example.test/mods/";

    private static HttpXmlSource Source(FakeHandler handler) => new(new Uri(Root), handler);

    [TestMethod]
    public void GetFullPath_ResolvesRelativeAndNormalizesUrl()
    {
        using var source = new HttpXmlSource(new Uri(Root));

        source.GetFullPath("metadata.xml").Should().Be(Root + "metadata.xml");
        source.GetFullPath("parts/./more/../base.xml").Should().Be(Root + "parts/base.xml");
        source.GetFullPath("images/").Should().Be(Root + "images/");
        source.GetFullPath(Root).Should().Be(Root);
        source.GetFullPath("https://Example.TEST/Mods/../Mods/base.xml")
            .Should().Be("https://example.test/Mods/base.xml");
    }

    [TestMethod]
    public void Combine_MergesIntoDirectoryAndNormalizesBackSlash()
    {
        using var source = Source(new FakeHandler());

        source.Combine(Root + "parts/", "more/leaf.xml").Should().Be(Root + "parts/more/leaf.xml");
        source.Combine(Root + "parts/", @"more\leaf.xml").Should().Be(Root + "parts/more/leaf.xml");
        source.Combine("https://example.test/mods/parts", "more/leaf.xml")
            .Should().Be(Root + "parts/more/leaf.xml");
        source.Combine(Root + "parts/", "https://other.test/x.xml").Should().Be("https://other.test/x.xml");
    }

    [TestMethod]
    public void GetDirectoryName_ReturnsParentDirectoryWithTrailingSlash()
    {
        using var source = Source(new FakeHandler());

        source.GetDirectoryName(Root + "parts/base.xml").Should().Be(Root + "parts/");
        source.GetDirectoryName(Root + "metadata.xml").Should().Be(Root);
        source.GetDirectoryName(Root + "parts/").Should().Be(Root);
        source.GetDirectoryName("https://example.test/metadata.xml").Should().Be("https://example.test/");
        source.GetDirectoryName("https://example.test/").Should().BeNull();
    }

    [TestMethod]
    public void GetRelativePath_SameOriginUnderFrom_ReturnsRelative()
    {
        using var source = Source(new FakeHandler());

        source.GetRelativePath(Root, Root + "parts/base.xml").Should().Be("parts/base.xml");
        source.GetRelativePath("https://example.test/mods", Root + "images/logo.png")
            .Should().Be("images/logo.png");
    }

    [TestMethod]
    public void GetRelativePath_CrossOriginStaysAbsoluteAndSameOriginCanTraverseParent()
    {
        using var source = Source(new FakeHandler());

        source.GetRelativePath(Root, "https://other.test/x.xml").Should().Be("https://other.test/x.xml");
        source.GetRelativePath(Root, "https://example.test/outside.xml")
            .Should().Be("../outside.xml");
        source.GetRelativePath(Root, "http://example.test/mods/x.xml")
            .Should().Be("http://example.test/mods/x.xml");
    }

    [TestMethod]
    [DataRow("part%2520name.xml", "part%2520name/release")]
    [DataRow("part%2Fname.xml", "part%2Fname/release")]
    public void Read_EscapedDocumentPathsPreserveIdentityAndResourceAddresses(string leaf, string id)
    {
        var handler = new FakeHandler();
        handler.AddXml(Root + "metadata.xml", $"<Metadata><Include Source=\"{leaf}\"/></Metadata>");
        handler.AddXml(Root + leaf, "<Metadata><Manifest ID=\"release\" HashAlgorithm=\"CRC32C\"/><Image ID=\"icon\" Source=\"images/icon%2520%2Fname.png\"/></Metadata>");
        using var source = Source(handler);
        source.GetRelativePath(Root, Root + leaf).Should().Be(leaf);
        var read = MetadataReader.Read(Root + "metadata.xml", new MetadataSourceOptions
            { CustomSource = source, SourceRoot = Root });
        read.Root.GetAllElements("Manifest").Single().Get("ID").Should().Be(id);
        read.Root.Images().Single().Source.Should().Be("images/icon%2520%2Fname.png");
        new Uri(new Uri(Root), read.Root.Images().Single().Source!).AbsoluteUri
            .Should().Be(Root + "images/icon%2520%2Fname.png");
        handler.Requests.Should().BeEquivalentTo(Root + "metadata.xml", Root + leaf);
    }

    [TestMethod]
    public void IsPathRooted_OnlyAbsoluteHttpUrls()
    {
        using var source = Source(new FakeHandler());

        source.IsPathRooted(Root + "metadata.xml").Should().BeTrue();
        source.IsPathRooted("metadata.xml").Should().BeFalse();
        source.IsPathRooted("parts/base.xml").Should().BeFalse();
    }

    [TestMethod]
    public void Exists_OkIsTrue_NotFoundIsFalse()
    {
        var handler = new FakeHandler();
        handler.AddContent(Root + "images/logo.png", [0x89, 0x50, 0x4E, 0x47], "image/png");
        using var source = Source(handler);

        source.Exists(Root + "images/logo.png").Should().BeTrue();
        source.Exists(Root + "images/missing.png").Should().BeFalse();
        handler.Requests.Should().Equal(Root + "images/logo.png", Root + "images/missing.png");
    }

    [TestMethod]
    public void Exists_OtherStatus_Throws()
    {
        var handler = new FakeHandler();
        handler.AddStatus(Root + "images/boom.png", HttpStatusCode.InternalServerError);
        using var source = Source(handler);

        var act = () => source.Exists(Root + "images/boom.png");

        act.Should().Throw<HttpRequestException>();
    }

    [TestMethod]
    public void Get_RedirectResponse_ThrowsAndDoesNotFollow()
    {
        var handler = new FakeHandler();
        handler.AddRedirect(Root + "redirected.xml", HttpStatusCode.Redirect, "https://evil.test/stolen.xml");
        using var source = Source(handler);

        var act = () => source.LoadDocument(Root + "redirected.xml");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*302*")
            .WithMessage("*" + Root + "redirected.xml*");
        handler.Requests.Should().Equal(Root + "redirected.xml");
    }

    [TestMethod]
    public void ExistsThenLoadDocument_XmlBody_ReusesCachedResponse()
    {
        var handler = new FakeHandler();
        handler.AddXml(Root + "metadata.xml", """<Metadata><Tags /></Metadata>""");
        using var source = Source(handler);

        source.Exists(Root + "metadata.xml").Should().BeTrue();
        source.LoadDocument(Root + "metadata.xml").Root!.Name.LocalName.Should().Be("Metadata");

        handler.Requests.Should().Equal(Root + "metadata.xml");
    }

    [TestMethod]
    public void Exists_XmlWithoutContentType_IdentifiedByExtension()
    {
        var handler = new FakeHandler();
        handler.AddContent(Root + "metadata.xml", Encoding.UTF8.GetBytes("<Metadata />"));
        using var source = Source(handler);

        source.Exists(Root + "metadata.xml").Should().BeTrue();
        source.LoadDocument(Root + "metadata.xml").Root!.Name.LocalName.Should().Be("Metadata");

        handler.Requests.Should().Equal(Root + "metadata.xml");
    }

    [TestMethod]
    public void Exists_ImageResponse_DoesNotCacheBody()
    {
        var handler = new FakeHandler();
        handler.AddContent(Root + "images/logo.png", [0x89, 0x50, 0x4E, 0x47], "image/png");
        handler.AddXml(Root + "images/logo.png", "<Metadata />");
        using var source = Source(handler);

        source.Exists(Root + "images/logo.png").Should().BeTrue();
        // 图片正文没进缓存，LoadDocument 必须重新请求（这一次拿到的是 XML）
        source.LoadDocument(Root + "images/logo.png").Root!.Name.LocalName.Should().Be("Metadata");

        handler.Requests.Should().HaveCount(2);
    }

    [TestMethod]
    public void LoadDocument_ParsesXml_MissingThrowsFileNotFound()
    {
        var handler = new FakeHandler();
        handler.AddXml(Root + "metadata.xml", """<Metadata><Mod ID="Demo" /></Metadata>""");
        using var source = Source(handler);

        source.LoadDocument(Root + "metadata.xml").Root!.Name.LocalName.Should().Be("Metadata");

        var act = () => source.LoadDocument(Root + "missing.xml");
        act.Should().Throw<FileNotFoundException>().WithMessage("*missing.xml*");
    }

    [TestMethod]
    public void LoadDocument_ProhibitsDtd()
    {
        var handler = new FakeHandler();
        handler.AddXml(Root + "evil.xml", """
<?xml version="1.0"?>
<!DOCTYPE Metadata [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
<Metadata>&xxe;</Metadata>
""");
        using var source = Source(handler);

        var act = () => source.LoadDocument(Root + "evil.xml");

        act.Should().Throw<System.Xml.XmlException>();
    }

    [TestMethod]
    public void Read_RemoteTree_ExpandsIncludesWithoutFetchingResources()
    {
        var handler = new FakeHandler();
        handler.AddXml(Root + "metadata.xml", """
<Metadata>
  <Includes>
    <Include Source="parts/base.xml" />
  </Includes>
</Metadata>
""");
        handler.AddXml(Root + "parts/base.xml", """
<Metadata>
  <Includes>
    <Include Source="more/leaf.xml" />
  </Includes>
  <Image ID="logo" Source="../images/logo.png" />
</Metadata>
""");
        handler.AddXml(Root + "parts/more/leaf.xml", """
<Metadata>
  <Image ID="leaf" Source="images/leaf.png" />
</Metadata>
""");
        handler.AddContent(Root + "images/logo.png", [0x89, 0x50, 0x4E, 0x47], "image/png");
        handler.AddContent(Root + "parts/more/images/leaf.png", [0x89, 0x50, 0x4E, 0x47], "image/png");

        using var source = Source(handler);
        var doc = MetadataReader.Read(Root + "metadata.xml", new MetadataSourceOptions
            { SourceRoot = Root, CustomSource = source }).Document;

        doc.Root!.Attribute("SchemaVersion").Should().BeNull();
        doc.Descendants().Should().NotContain(e =>
            e.Name.LocalName == "Include" || e.Name.LocalName == "Includes");

        var images = doc.Descendants("Image").ToList();
        images.Should().HaveCount(2);

        var logo = images.Single(i => i.Attribute("ID")!.Value.EndsWith("/logo", StringComparison.Ordinal));
        logo.Attribute("ID")!.Value.Should().Be("parts/base/logo");
        logo.Attribute("Source")!.Value.Should().Be("images/logo.png");

        var leaf = images.Single(i => i.Attribute("ID")!.Value.EndsWith("/leaf", StringComparison.Ordinal));
        leaf.Attribute("ID")!.Value.Should().Be("parts/more/leaf/leaf");
        leaf.Attribute("Source")!.Value.Should().Be("parts/more/images/leaf.png");

        // 递归 Include 真的发了请求，且全部落在源根下（没有任何真实外部请求）
        handler.Requests.Should().Contain(Root + "metadata.xml");
        handler.Requests.Should().Contain(Root + "parts/base.xml");
        handler.Requests.Should().Contain(Root + "parts/more/leaf.xml");
        handler.Requests.Should().BeEquivalentTo(Root + "metadata.xml", Root + "parts/base.xml", Root + "parts/more/leaf.xml");
    }

    [TestMethod]
    public void Read_IncludeEscapingSourceRoot_RejectedBeforeFetching()
    {
        var handler = new FakeHandler();
        handler.AddXml(Root + "metadata.xml", """
<Metadata>
  <Includes>
    <Include Source="../../../etc/x.xml" />
  </Includes>
</Metadata>
""");
        using var source = Source(handler);

        var act = () => MetadataReader.Read(Root + "metadata.xml", new MetadataSourceOptions
            { SourceRoot = Root, CustomSource = source });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Include 越出源目录*");
        handler.Requests.Should().OnlyContain(url => url == Root + "metadata.xml");
    }

    [TestMethod]
    public void Read_IncludeCrossHostAbsoluteUrl_RejectedBeforeFetching()
    {
        var handler = new FakeHandler();
        handler.AddXml(Root + "metadata.xml", """
<Metadata>
  <Includes>
    <Include Source="https://other.test/x.xml" />
  </Includes>
</Metadata>
""");
        using var source = Source(handler);

        var act = () => MetadataReader.Read(Root + "metadata.xml", new MetadataSourceOptions
            { SourceRoot = Root, CustomSource = source });

        act.Should().Throw<InvalidOperationException>().WithMessage("*Include 越出源目录*");
        handler.Requests.Should().OnlyContain(url => url == Root + "metadata.xml");
    }

    /// <summary>按绝对 URL 路由的内存报文 handler：未登记的 URL 一律 404，绝不发真实请求。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _routes =
            new(StringComparer.Ordinal);

        public List<string> Requests { get; } = new();

        public void AddXml(string url, string xml) =>
            Add(url, () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });

        public void AddContent(string url, byte[] content, string? mediaType = null) =>
            Add(url, () =>
            {
                var body = new ByteArrayContent(content);
                if (mediaType is not null)
                    body.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
            });

        public void AddStatus(string url, HttpStatusCode statusCode) =>
            Add(url, () => new HttpResponseMessage(statusCode));

        public void AddRedirect(string url, HttpStatusCode statusCode, string location) =>
            Add(url, () =>
            {
                var response = new HttpResponseMessage(statusCode);
                response.Headers.Location = new Uri(location);
                return response;
            });

        private void Add(string url, Func<HttpResponseMessage> response)
        {
            if (!_routes.TryGetValue(url, out var queue))
                _routes[url] = queue = new Queue<Func<HttpResponseMessage>>();
            queue.Enqueue(response);
        }

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
            if (!_routes.TryGetValue(url, out var queue))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            // 同一 URL 多次请求：队列只有一条时复用最后一条（每次都造新响应实例）
            return queue.Count > 1 ? queue.Dequeue()() : queue.Peek()();
        }
    }
}
