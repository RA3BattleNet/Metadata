using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ra3.BattleNet.Metadata.Cache;

namespace Ra3.BattleNet.Metadata.Tests;

[TestClass]
public sealed class MetadataCompositionCacheTests
{
    [TestMethod]
    public async Task Root304_RevalidatesChild_AndPersistsLastCompleteGraph()
    {
        using var server = new Server();
        server.Xml("metadata.xml", "<Metadata SchemaVersion=\"1.0\"><Include Source=\"child.xml\" /></Metadata>", "root");
        server.Xml("child.xml", Child("one"), "one");
        var cache = NewDirectory();
        using var client = Client(cache);
        var first = await client.RefreshRootAsync(server.Root);
        first.Status.Should().Be(MetadataFreshness.Fresh);
        server.Xml("child.xml", Child("two"), "two");
        var second = await client.RefreshRootAsync(server.Root);
        second.Status.Should().Be(MetadataFreshness.Fresh);
        second.Document!.Mods().Single().Id.Should().Be("two");
        server.Requests.Should().Contain(item => item.Path == "metadata.xml" && item.Conditional == "\"root\"");
        server.Requests.Should().Contain(item => item.Path == "child.xml" && item.Conditional == "\"one\"");
        File.ReadAllText(Path.Combine(cache, "metadata.xml")).Should().Contain("Include");
        var count = server.Requests.Count;
        using (var reopened = Client(cache))
        {
            var offline = await reopened.OpenSnapshotAsync(server.Root);
            offline.Status.Should().Be(MetadataFreshness.Stale);
            offline.Document!.Mods().Single().Id.Should().Be("two");
        }
        server.Requests.Count.Should().Be(count);
        server.Fail("child.xml");
        var failed = await client.RefreshRootAsync(server.Root);
        failed.Status.Should().Be(MetadataFreshness.Stale);
        failed.Document.Should().BeSameAs(second.Document);
        using var cold = Client(NewDirectory());
        (await cold.RefreshRootAsync(server.Root)).Status.Should().Be(MetadataFreshness.Unavailable);
        using var last = Client(cache);
        (await last.OpenSnapshotAsync(server.Root)).Document!.Mods().Single().Id.Should().Be("two");
    }

    [TestMethod]
    public async Task ColdOffline_NeverRequestsNetwork_AndCorruptCompositionIsRejected()
    {
        using var server = new Server();
        var cache = NewDirectory();
        using var client = Client(cache);
        (await client.OpenSnapshotAsync(server.Root)).Status.Should().Be(MetadataFreshness.Unavailable);
        server.Requests.Should().BeEmpty();
        server.Xml("metadata.xml", "<Metadata SchemaVersion=\"1.0\" IdFormat=\"legacy\" SourceBase=\"document\"><Mod ID=\"good\" /></Metadata>", "root");
        (await client.RefreshRootAsync(server.Root)).Status.Should().Be(MetadataFreshness.Fresh);
        var record = Directory.EnumerateFiles(Path.Combine(cache, ".compositions"), "*.xml").Single();
        File.WriteAllText(record, "<Composition");
        var count = server.Requests.Count;
        using var reopened = Client(cache);
        (await reopened.OpenSnapshotAsync(server.Root)).Status.Should().Be(MetadataFreshness.Unavailable);
        server.Requests.Count.Should().Be(count);
    }

    [TestMethod]
    public async Task Packages_InlineAndExternal_PreserveSnapshotIdentityAndImageAddress()
    {
        using var server = new Server();
        server.Xml("metadata.xml", """
            <Metadata SchemaVersion="1.0" IdFormat="legacy" SourceBase="document">
              <Mod ID="M"><CurrentVersion>1</CurrentVersion><Packages>
                <Package Version="1"><Manifest>inline</Manifest></Package>
                <Package Version="2"><Manifest>external</Manifest></Package>
              </Packages></Mod>
              <Manifest ID="inline" HashAlgorithm="None"><File><FileName>inline.bin</FileName></File></Manifest>
              <Manifest ID="external" Source="nested/leaf.xml" />
              <Image ID="icon" Source="nested/icon.png" />
            </Metadata>
            """, "root");
        server.Xml("nested/leaf.xml", "<Metadata><Manifest ID=\"external\" HashAlgorithm=\"None\"><File><FileName>external.bin</FileName></File></Manifest></Metadata>", "leaf");
        server.Bytes("nested/icon.png", [1, 2, 3], "image/png");
        using var client = Client(NewDirectory());
        var snapshot = await client.RefreshRootAsync(server.Root);
        var packages = snapshot.Document!.Mods().Single().Packages.ToList();
        var inline = await client.GetPackageAsync(snapshot, packages[0]);
        inline.Status.Should().Be(MetadataFreshness.Fresh);
        inline.SourceUri.Should().BeNull();
        inline.Entry!.Files.Single().FileName.Should().Be("inline.bin");
        server.Requests.Should().HaveCount(1);
        (await client.GetCachedPackageAsync(snapshot, packages[1])).Status.Should().Be(MetadataFreshness.Unavailable);
        var external = await client.GetPackageAsync(snapshot, packages[1]);
        external.Entry!.Files.Single().FileName.Should().Be("external.bin");
        external.SourceUri!.AbsoluteUri.Should().Be(new Uri(new Uri(server.Root), "nested/leaf.xml").AbsoluteUri);
        var stale = await client.OpenSnapshotAsync(server.Root);
        (await client.RefreshPackageAsync(stale, packages[0])).Status.Should().Be(MetadataFreshness.Stale);
        var forged = packages[0] with { Raw = new Metadata() };
        (await client.GetPackageAsync(snapshot, forged)).Status.Should().Be(MetadataFreshness.Unavailable);
        var summary = await client.PreloadLeavesAsync(snapshot);
        summary.Total.Should().Be(2);
        summary.Failed.Should().Be(0);
        (await client.GetImageAsync(snapshot, "icon")).Bytes.Should().Equal(1, 2, 3);
        server.Requests.Should().Contain(item => item.Path == "nested/icon.png");
    }

    [TestMethod]
    public async Task LocalRoot_RemoteInclude_UsesTransferAndSupportsColdOfflineReopen()
    {
        using var server = new Server();
        server.Xml("child.xml", Child("remote"), "child");
        var source = NewDirectory();
        var path = Path.Combine(source, "metadata.xml");
        File.WriteAllText(path, $"<Metadata SchemaVersion=\"1.0\"><Include Source=\"{new Uri(new Uri(server.Root), "child.xml")}\" /></Metadata>");
        var cache = NewDirectory();
        using (var client = Client(cache))
        {
            var fresh = await client.RefreshRootAsync(path);
            fresh.Status.Should().Be(MetadataFreshness.Fresh);
            fresh.Document!.Mods().Single().Id.Should().Be("remote");
        }
        var count = server.Requests.Count;
        using var reopened = Client(cache);
        (await reopened.OpenSnapshotAsync(path)).Status.Should().Be(MetadataFreshness.Stale);
        server.Requests.Count.Should().Be(count);
    }

    [TestMethod]
    public void SourceManifest_PrefersExactIdentity_AndRejectsAmbiguousLegacyLocalId()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "metadata.xml");
        File.WriteAllText(path, """
            <Metadata IdFormat="legacy" SourceBase="document">
              <Mod ID="M"><Packages><Package Version="1"><Manifest>wanted:item</Manifest></Package></Packages></Mod>
              <Manifest ID="wanted:item" Source="leaf.xml" />
            </Metadata>
            """);
        File.WriteAllText(Path.Combine(directory, "leaf.xml"), """
            <Metadata IdFormat="legacy" SourceBase="document">
              <Manifest ID="wanted:item" HashAlgorithm="None"><File><FileName>exact.bin</FileName></File></Manifest>
              <Manifest ID="other:item" HashAlgorithm="None"><File><FileName>other.bin</FileName></File></Manifest>
            </Metadata>
            """);
        var root = MetadataReader.Read(path).Root;
        var package = root.Mods().Single().Packages.Single();
        MetadataSourceManifest.Resolve(root, path, package.Raw)!.ToManifestEntry().Files.Single().FileName.Should().Be("exact.bin");
        File.WriteAllText(Path.Combine(directory, "leaf.xml"), """
            <Metadata IdFormat="legacy" SourceBase="document">
              <Manifest ID="first:item" HashAlgorithm="None" />
              <Manifest ID="second:item" HashAlgorithm="None" />
            </Metadata>
            """);
        var resolve = () => MetadataSourceManifest.Resolve(root, path, package.Raw);
        resolve.Should().Throw<InvalidOperationException>().WithMessage("*Ambiguous*");
    }

    [TestMethod]
    public void SourceManifest_ComposedExternalLeaf_ResolvesIncludedBody()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "metadata.xml");
        File.WriteAllText(path, """
            <Metadata IdFormat="legacy" SourceBase="document">
              <Mod ID="M"><Packages><Package Version="1"><Manifest>item</Manifest></Package></Packages></Mod>
              <Manifest ID="item" Source="leaf.xml" />
            </Metadata>
            """);
        File.WriteAllText(Path.Combine(directory, "leaf.xml"), "<Metadata><Include Source=\"body.xml\" /></Metadata>");
        File.WriteAllText(Path.Combine(directory, "body.xml"), "<Metadata><Manifest ID=\"item\" HashAlgorithm=\"None\"><File><FileName>composed.bin</FileName></File></Manifest></Metadata>");
        var root = MetadataReader.Read(path).Root;
        var package = root.Mods().Single().Packages.Single();
        var manifest = MetadataSourceManifest.Resolve(root, path, package.Raw)!;
        manifest.ToManifestEntry().Files.Single().FileName.Should().Be("composed.bin");
    }

    [TestMethod]
    public async Task IncludeRedirect_IsRejectedWithoutRequestingRedirectTarget()
    {
        using var server = new Server();
        server.Xml("metadata.xml", "<Metadata SchemaVersion=\"1.0\"><Include Source=\"child.xml\" /></Metadata>", "root");
        server.Redirect("child.xml", new Uri(new Uri(server.Root), "outside.xml").AbsoluteUri);
        server.Xml("outside.xml", Child("outside"), "outside");
        using var client = Client(NewDirectory());
        (await client.RefreshRootAsync(server.Root)).Status.Should().Be(MetadataFreshness.Unavailable);
        server.Requests.Should().NotContain(request => request.Path == "outside.xml");
    }

    [TestMethod]
    public async Task FailedFlatToCompositionMigration_PreservesColdOfflineRoot()
    {
        using var server = new Server();
        server.Xml("metadata.xml", "<Metadata SchemaVersion=\"1.0\"><Mod ID=\"previous\" /></Metadata>", "flat");
        var cache = NewDirectory();
        using (var client = Client(cache))
        {
            (await client.RefreshRootAsync(server.Root)).Status.Should().Be(MetadataFreshness.Fresh);
            server.Xml("metadata.xml", "<Metadata SchemaVersion=\"1.0\"><Include Source=\"metadata.xml/child.xml\" /></Metadata>", "tree");
            server.Xml("metadata.xml/child.xml", Child("next"), "child");
            var failed = await client.RefreshRootAsync(server.Root);
            failed.Status.Should().Be(MetadataFreshness.Stale);
            failed.Document!.Mods().Single().Id.Should().Be("previous");
        }
        using var reopened = Client(cache);
        var offline = await reopened.OpenSnapshotAsync(server.Root);
        offline.Status.Should().Be(MetadataFreshness.Stale);
        offline.Document!.Mods().Single().Id.Should().Be("previous");
    }

    [TestMethod]
    public async Task CorruptFlatCache_DoesNotBlockPublishingValidComposition()
    {
        using var server = new Server();
        server.Xml("metadata.xml", "<Metadata SchemaVersion=\"1.0\"><Mod ID=\"previous\" /></Metadata>", "flat");
        var cache = NewDirectory();
        using (var client = Client(cache))
            (await client.RefreshRootAsync(server.Root)).Status.Should().Be(MetadataFreshness.Fresh);
        File.WriteAllText(Path.Combine(cache, "metadata.xml"), "<Metadata><broken");
        server.Xml("metadata.xml", "<Metadata SchemaVersion=\"1.0\"><Include Source=\"child.xml\" /></Metadata>", "tree");
        server.Xml("child.xml", Child("recovered"), "child");
        using (var client = Client(cache))
        {
            var repaired = await client.RefreshRootAsync(server.Root);
            repaired.Status.Should().Be(MetadataFreshness.Fresh);
            repaired.Document!.Mods().Single().Id.Should().Be("recovered");
        }
        using var reopened = Client(cache);
        (await reopened.OpenSnapshotAsync(server.Root)).Document!.Mods().Single().Id.Should().Be("recovered");
    }

    [TestMethod]
    public async Task CachedLocalPackage_DecodesPathIds_AndExpandsLocalIncludesWithoutNetwork()
    {
        using var server = new Server();
        var directory = NewDirectory();
        var path = Path.Combine(directory, "metadata.xml");
        var leaf = Path.Combine(directory, "leaf.xml");
        File.WriteAllText(path, """
            <Metadata SchemaVersion="1.0" IdFormat="path" SourceBase="document">
              <Mod ID="M"><Packages><Package Version="1"><Manifest>scope/item</Manifest></Package></Packages></Mod>
              <Manifest ID="scope/item" Source="leaf.xml" />
            </Metadata>
            """);
        var body = "<Metadata IdFormat=\"path\" SourceBase=\"document\"><Manifest ID=\"scope/item\" HashAlgorithm=\"None\"><File><FileName>local.bin</FileName></File></Manifest></Metadata>";
        File.WriteAllText(leaf, body);
        using var client = new MetadataClient("", TimeSpan.FromSeconds(5), enableDiskCache: false);
        var snapshot = await client.RefreshRootAsync(path);
        var package = snapshot.Document!.Mods().Single().Packages.Single();
        var flat = await client.GetCachedPackageAsync(snapshot, package);
        flat.Status.Should().Be(MetadataFreshness.Stale);
        flat.Entry!.Id.Should().Be("scope:item");
        File.WriteAllText(leaf, "<Metadata IdFormat=\"legacy\" SourceBase=\"document\"><Include Source=\"body.xml\" /></Metadata>");
        File.WriteAllText(Path.Combine(directory, "body.xml"), body);
        (await client.GetCachedPackageAsync(snapshot, package)).Entry!.Files.Single().FileName.Should().Be("local.bin");
        File.WriteAllText(leaf, $"<Metadata><Include Source=\"{new Uri(new Uri(server.Root), "remote.xml")}\" /></Metadata>");
        (await client.GetCachedPackageAsync(snapshot, package)).Status.Should().Be(MetadataFreshness.Unavailable);
        server.Requests.Should().BeEmpty();
    }

    private static string Child(string id) => $"<Metadata><Mod ID=\"{id}\" /></Metadata>";
    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "metadata-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static MetadataClient Client(string cache) => new(cache, TimeSpan.FromSeconds(5));

    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, (byte[] Body, string Type, string? ETag, int Status)> _responses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _redirects = new(StringComparer.Ordinal);
        private readonly Task _loop;
        public string Root { get; }
        public List<(string Path, string? Conditional)> Requests { get; } = [];

        public Server()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            var prefix = $"http://127.0.0.1:{port}/";
            Root = prefix + "metadata.xml";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            _loop = RunAsync();
        }
        public void Xml(string path, string body, string etag) =>
            _responses[path] = (Encoding.UTF8.GetBytes(body), "application/xml", "\"" + etag + "\"", 200);
        public void Bytes(string path, byte[] body, string type) => _responses[path] = (body, type, null, 200);
        public void Fail(string path) => _responses[path] = ([], "text/plain", null, 503);
        public void Redirect(string path, string destination) => _redirects[path] = destination;
        private async Task RunAsync()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    var path = context.Request.Url!.AbsolutePath.TrimStart('/');
                    var conditional = context.Request.Headers["If-None-Match"];
                    Requests.Add((path, conditional));
                    if (_redirects.TryGetValue(path, out var destination))
                    {
                        context.Response.StatusCode = 302;
                        context.Response.RedirectLocation = destination;
                        context.Response.Close();
                        continue;
                    }
                    var response = _responses.TryGetValue(path, out var registered)
                        ? registered : (Body: Array.Empty<byte>(), Type: "text/plain", ETag: (string?)null, Status: 404);
                    context.Response.StatusCode = response.Status == 200 && response.ETag is not null && conditional == response.ETag ? 304 : response.Status;
                    if (response.ETag is not null)
                        context.Response.Headers["ETag"] = response.ETag;
                    context.Response.ContentType = response.Type;
                    if (context.Response.StatusCode == 200)
                    {
                        context.Response.ContentLength64 = response.Body.Length;
                        await context.Response.OutputStream.WriteAsync(response.Body);
                    }
                    context.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        }
        public void Dispose()
        {
            _listener.Close();
            _loop.GetAwaiter().GetResult();
        }
    }
}
