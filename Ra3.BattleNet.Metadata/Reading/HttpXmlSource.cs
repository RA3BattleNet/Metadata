using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// <see cref="IMetadataXmlSource"/> 的 HTTP 只读实现：路径一律是绝对 URL，请求走内部 HttpClient，
/// 构造后无全局状态。读取全部流式进行，不先整包下载；XML 正文按 URL 缓存，同一个文件不发两次 GET。
/// 不跟随重定向：展平器的越界检查只看即将发出的 URL，跟随 3xx 会把外站正文当源树内容读进来。
/// </summary>
public sealed class HttpXmlSource : IMetadataXmlSource, IDisposable
{
    /// <summary>单个 XML 请求的超时。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    // 与 MetadataParser 口径一致：禁 DTD、不解析外部实体
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };

    private readonly Uri _baseUri;
    private readonly HttpClient _client;
    private readonly Dictionary<string, byte[]> _documents = new(StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="baseUri"/> 视为源根目录，缺少尾部 / 时补齐，相对路径按它解析；
    /// <paramref name="handler"/> 为空时用默认处理器（关闭自动重定向）。
    /// </summary>
    public HttpXmlSource(Uri baseUri, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri)
            throw new ArgumentException("baseUri 必须是绝对 URL", nameof(baseUri));
        if (!IsHttpScheme(baseUri))
            throw new ArgumentException("baseUri 只能是 http/https URL", nameof(baseUri));

        var root = baseUri.AbsoluteUri;
        _baseUri = new Uri(root.EndsWith("/", StringComparison.Ordinal) ? root : root + "/");
        _client = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = RequestTimeout;
    }

    /// <summary>释放内部 HttpClient；注入的 handler 归调用方所有，不随本实例释放。</summary>
    public void Dispose() => _client.Dispose();

    /// <inheritdoc />
    public bool Exists(string path)
    {
        var full = GetFullPath(path);
        using var response = Send(full);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        if (!response.IsSuccessStatusCode)
            throw Failed(full, response);

        // 只缓存 XML 正文；图片等资源只判存在，不把整包读进内存
        if (LooksLikeXml(full, response))
            _documents[full] = ReadBody(response.Content);
        return true;
    }

    /// <inheritdoc />
    public XDocument LoadDocument(string path)
    {
        var full = GetFullPath(path);
        if (_documents.TryGetValue(full, out var body))
        {
            using var cached = new MemoryStream(body, writable: false);
            return Parse(cached);
        }

        using var response = Send(full);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException($"找不到文件: {full}");
        if (!response.IsSuccessStatusCode)
            throw Failed(full, response);

        // 直接从响应流解析，不先落内存副本
        using var stream = response.Content.ReadAsStream();
        return Parse(stream);
    }

    /// <inheritdoc />
    public string Combine(string basePath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(relativePath);

        var baseDir = GetFullPath(basePath);
        if (!baseDir.EndsWith("/", StringComparison.Ordinal))
            baseDir += "/";

        // 作者可能写 \ 分隔的路径；先统一成 / 再交给 Uri 解析
        var relative = relativePath.Replace('\\', '/');
        return Normalize(new Uri(new Uri(baseDir), relative), relative);
    }

    /// <inheritdoc />
    // 文件 URL 返回所在目录，目录 URL 返回上一级；站点根没有父目录
    public string? GetDirectoryName(string path)
    {
        var full = GetFullPath(path);
        if (new Uri(full).AbsolutePath == "/")
            return null;

        var parent = new Uri(new Uri(full), full.EndsWith("/", StringComparison.Ordinal) ? ".." : ".");
        var absolute = parent.AbsoluteUri;
        return absolute.EndsWith("/", StringComparison.Ordinal) ? absolute : absolute + "/";
    }

    /// <inheritdoc />
    // 同源地址使用相对路径；跨源地址保留绝对 URL。
    public string GetRelativePath(string fromPath, string toPath)
    {
        var from = GetFullPath(fromPath);
        var to = GetFullPath(toPath);

        var fromUri = new Uri(from);
        var toUri = new Uri(to);
        if (!string.Equals(fromUri.Scheme, toUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fromUri.Host, toUri.Host, StringComparison.OrdinalIgnoreCase)
            || fromUri.Port != toUri.Port)
            return to;

        var dir = from.EndsWith("/", StringComparison.Ordinal) ? from : from + "/";
        return new Uri(dir).MakeRelativeUri(toUri).ToString();
    }

    /// <inheritdoc />
    public string GetFullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var relative = path.Replace('\\', '/');
        return Normalize(new Uri(_baseUri, relative), relative);
    }

    /// <inheritdoc />
    public bool IsPathRooted(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) && IsHttpScheme(uri);
    }

    private static bool IsHttpScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    // 规范化：Uri 负责折叠 . / ..、统一 scheme/host 大小写与转义；这里补目录尾斜杠并拒绝非 http/https
    private static string Normalize(Uri resolved, string input)
    {
        if (!IsHttpScheme(resolved))
            throw new InvalidOperationException($"只支持 http/https URL: {input}");

        var absolute = resolved.AbsoluteUri;
        return input.EndsWith("/", StringComparison.Ordinal) && !absolute.EndsWith("/", StringComparison.Ordinal)
            ? absolute + "/"
            : absolute;
    }

    // 有 Content-Type 就看它含不含 xml；没有 Content-Type 时按 .xml 扩展名兜底
    private static bool LooksLikeXml(string url, HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return string.IsNullOrEmpty(mediaType)
            ? url.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            : mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase);
    }

    private static XDocument Parse(Stream stream)
    {
        using var reader = XmlReader.Create(stream, ReaderSettings);
        return XDocument.Load(reader);
    }

    private static byte[] ReadBody(HttpContent content)
    {
        using var stream = content.ReadAsStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private HttpResponseMessage Send(string fullUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, fullUrl);
        var response = _client.Send(request, HttpCompletionOption.ResponseHeadersRead);

        // 不读 Location、不跟随：越界检查只覆盖即将发出的 URL，跟随重定向等于绕开源根约束
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var status = (int)response.StatusCode;
            var reason = response.ReasonPhrase;
            response.Dispose();
            throw new InvalidOperationException($"GET {fullUrl} 返回重定向 {status} {reason}");
        }

        return response;
    }

    private static HttpRequestException Failed(string full, HttpResponseMessage response) =>
        new($"GET {full} 返回 {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
}
