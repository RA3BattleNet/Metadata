using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 核心构建：校验、变量替换、XML 数据展平、复制资源。纯 managed。
/// </summary>
public static class MetadataBuilder
{
    public const string DefaultSchemaVersion = "1.0";

    private static readonly Regex LeftoverVariablePattern = new(@"\$\{[^}]+\}", RegexOptions.Compiled);
    private static readonly Regex MountTokenPattern = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex GameVersionPattern = new(@"^\d+\.\d+$", RegexOptions.Compiled);

    /// <summary>本地配置文件必须是纯文件名：无路径分隔符、无通配/保留字符、无空格与控制字符、不是 . 或 ..</summary>
    private static readonly Regex ModSkudefLocalFilePattern = new(@"^(?!\.\.?$)[^\\/:*?""<>|\x00-\x1f ]+$", RegexOptions.Compiled);

    private const string DefaultHashAlgorithm = "CRC32C";

    /// <summary>各算法的十六进制哈希长度。</summary>
    private static readonly Dictionary<string, int> HashHexLengths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CRC32C"] = 8,
        ["MD5"] = 32,
        ["SHA256"] = 64,
    };

    /// <summary>
    /// 从本地源目录执行核心构建。
    /// </summary>
    /// <param name="convertImages">true 时在展平后调用 Imaging CLI 转 WebP，并由本管线改写 Source/Hash。</param>
    public static void Build(
        string sourceDir,
        string outputDir,
        string? schemaVersion = null,
        string? contentRevision = null,
        bool convertImages = false)
    {
        if (string.IsNullOrWhiteSpace(sourceDir))
            throw new ArgumentException("源目录不能为空", nameof(sourceDir));
        if (string.IsNullOrWhiteSpace(outputDir))
            throw new ArgumentException("输出目录不能为空", nameof(outputDir));

        var src = Path.GetFullPath(sourceDir);
        var dst = Path.GetFullPath(outputDir);
        var entry = Path.Combine(src, "metadata.xml");
        if (!File.Exists(entry))
            throw new FileNotFoundException($"找不到入口文件: {entry}");

        if (Directory.Exists(dst))
            Directory.Delete(dst, recursive: true);
        Directory.CreateDirectory(dst);

        try
        {
            var sourceSchema = SchemaValidator.FindSchema(src, SchemaValidator.SourceSchemaFileName)
                ?? throw new FileNotFoundException($"找不到源树 XSD: {SchemaValidator.SourceSchemaFileName}");
            SchemaValidator.EnsureDirectoryValid(src, sourceSchema, "源树");
            ValidateManifests(src);

            var revision = string.IsNullOrWhiteSpace(contentRevision)
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                : contentRevision;
            var version = string.IsNullOrWhiteSpace(schemaVersion) ? DefaultSchemaVersion : schemaVersion;

            var flattened = MetadataFlattener.Flatten(entry, src, version, revision);
            var flatPath = Path.Combine(dst, "metadata.xml");
            var resolver = new VariableResolver();

            // 发布面 = 展平 metadata.xml + 被引用资源 + _redirects；
            // 源 XML / XSD / Templates 不进 Output
            var leafPaths = CopyReferencedResources(flattened, src, dst, version, revision, resolver);
            CopyIfExists(Path.Combine(src, "_redirects"), Path.Combine(dst, "_redirects"));

            flattened.Save(flatPath);
            resolver.ReplaceInFile(flatPath);

            var leftover = FindLeftoverVariables([flatPath, .. leafPaths]);
            if (leftover.Count > 0)
                throw new InvalidOperationException("变量替换后仍有残留: " + string.Join("; ", leftover));

            // 展平后：按图调用 Imaging（只产 webp+hash），本管线写回 XML
            if (convertImages)
            {
                var n = ImagePostProcessor.ApplyWebP(dst);
                Console.WriteLine($"  Imaging: 转换 {n} 张图片并写回 Source/Hash");
            }

            var publishSchema = SchemaValidator.FindSchema(src, SchemaValidator.PublishSchemaFileName)
                ?? SchemaValidator.FindSchema(dst, SchemaValidator.PublishSchemaFileName)
                ?? throw new FileNotFoundException($"找不到发布 XSD: {SchemaValidator.PublishSchemaFileName}");
            SchemaValidator.EnsureValid(flatPath, publishSchema, "发布物");

            ValidateHard(flatPath, dst);
        }
        catch
        {
            if (Directory.Exists(dst))
            {
                try { Directory.Delete(dst, recursive: true); } catch { /* 尽力清理半残产物 */ }
            }
            throw;
        }
    }

    /// <summary>
    /// 从展平树收集被引用资源（Image/Markdown/Manifest 登记节点的 Source 文件；Url-only 图跳过），复制到 Output。
    /// 叶子清单不照抄源文件：它同样走一遍展平并解析变量（去掉源树专用的 Includes、限定 ID、替换 ${TIMESTAMP}），
    /// 否则发布物里会留下客户端解析器拒绝的 Includes 元素与未替换的变量。
    /// 返回写出的叶子清单路径。缺失文件不在此报错——由 ValidateHard 统一硬失败。
    /// </summary>
    private static List<string> CopyReferencedResources(
        XDocument flattened, string src, string dst, string schemaVersion, string contentRevision, VariableResolver resolver)
    {
        var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var leafPaths = new List<string>();

        foreach (var el in flattened.Root!.DescendantsAndSelf())
        {
            if (el.Name.LocalName is not ("Image" or "Markdown" or "Manifest"))
                continue;
            if (string.IsNullOrWhiteSpace(el.Attribute("ID")?.Value))
                continue; // 登记节点才有资源
            if (!string.IsNullOrWhiteSpace(el.Attribute("Url")?.Value))
                continue; // 外链不落盘
            var source = el.Attribute("Source")?.Value;
            if (string.IsNullOrWhiteSpace(source))
                continue;

            var rel = source.Replace('\\', '/');
            if (!copied.Add(rel))
                continue;

            var from = Path.GetFullPath(Path.Combine(src, rel));
            if (!File.Exists(from))
                continue; // 缺文件由 ValidateHard 报

            var target = Path.Combine(dst, rel);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (el.Name.LocalName == "Manifest")
            {
                MetadataFlattener.Flatten(from, src, schemaVersion, contentRevision).Save(target);
                resolver.ReplaceInFile(target);
                leafPaths.Add(target);
            }
            else
            {
                File.Copy(from, target, overwrite: true);
            }
        }

        return leafPaths;
    }

    private static void CopyIfExists(string from, string to)
    {
        if (!File.Exists(from)) return;
        var dir = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.Copy(from, to, overwrite: true);
    }

    /// <summary>
    /// 从本地路径（含 <c>file://</c> 形式）或 HTTP(S) URL 加载已展平的 metadata.xml。
    /// </summary>
    public static Metadata Load(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
            throw new ArgumentException("路径或 URL 不能为空", nameof(pathOrUrl));

        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                return LoadFromUrl(uri);
            }

            // MetadataResourceUri.Resolve 对本地入口返回 file:// 地址，这里还原成本地路径
            if (uri.IsFile)
            {
                return Metadata.LoadFromFile(uri.LocalPath);
            }
        }

        return Metadata.LoadFromFile(pathOrUrl);
    }

    /// <summary>单个 IP 的连接预算：这么久还没连上就换下一个，不在一棵树上吊死。</summary>
    private static readonly TimeSpan AddressConnectTimeout = TimeSpan.FromSeconds(8);

    /// <summary>一次尝试的总预算（含握手与正文）。</summary>
    private static readonly TimeSpan LoadUrlTimeout = TimeSpan.FromSeconds(60);

    /// <summary>URL 入口的总尝试次数（含第一次）。</summary>
    private const int LoadUrlAttempts = 8;

    /// <summary>两次尝试之间歇多久。握手失败几十毫秒就报出来，用不着指数退避。</summary>
    private static readonly TimeSpan LoadUrlRetryWait = TimeSpan.FromMilliseconds(600);

    /// <summary>连接地址的轮换计数，跨尝试累加，用来错开每次尝试的起点。</summary>
    private static int _addressCursor;

    /// <summary>
    /// 从 URL 加载已展平的 metadata.xml。CDN 边缘节点经常握手失败或压根不可达，
    /// 所以最多试 <see cref="LoadUrlAttempts"/> 次，每次换一个边缘 IP 的起点；
    /// 全部失败才把最后一次的异常抛出去。
    /// </summary>
    private static Metadata LoadFromUrl(Uri uri)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return FetchFromUrl(uri);
            }
            catch (Exception ex) when (IsRetryableLoadFailure(ex) && attempt < LoadUrlAttempts)
            {
                Thread.Sleep(LoadUrlRetryWait);
            }
        }
    }

    private static Metadata FetchFromUrl(Uri uri)
    {
        using var handler = CreateLoadHandler();
        using var client = new HttpClient(handler) { Timeout = LoadUrlTimeout };
        using var stream = client.GetStreamAsync(uri).GetAwaiter().GetResult();
        var temp = Path.Combine(Path.GetTempPath(), $"metadata-url-{Guid.NewGuid():N}.xml");
        try
        {
            using (var fs = File.Create(temp))
                stream.CopyTo(fs);
            return Metadata.LoadFromFile(temp);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// 什么才算"值得再试一次"：网络层的失败和超时都算。除了 <see cref="HttpRequestException"/> 和
    /// <see cref="TimeoutException"/>，还有两种得拆开看的 —— HttpClient 的超时是
    /// <see cref="TaskCanceledException"/>（里头裹着一个 <see cref="TimeoutException"/>）；
    /// 响应体读到一半被重置则是 <see cref="HttpIOException"/>（"响应提前结束"），它也是
    /// <see cref="IOException"/>，一块算上。本地落盘失败同样是 IOException，重试它没有意义，
    /// 但最多白烧几次几毫秒的失败，比漏掉一次真网络抖动划算。取消和 XML 解析错误一律不重试 ——
    /// 那不是网络在抖。
    /// </summary>
    private static bool IsRetryableLoadFailure(Exception ex) =>
        ex is HttpRequestException or IOException or TimeoutException
        or TaskCanceledException { InnerException: TimeoutException };

    /// <summary>
    /// 自己接管连接过程的 handler。
    ///
    /// 为什么不直接用 <see cref="HttpClient"/> 默认那套：它只在 TCP 连不上时才换地址，握手阶段被
    /// RST/EOF 会直接把异常抛出来，不会改连同域名的其它 IP。而 CDN 一个域名背后是一组边缘 IP，
    /// 可达性并不一致（实测同一个域名的两个 IP，一个通、一个 12 秒超时），不自己接管的话
    /// 每次重试都撞上同一个坏 IP，重试再多也白试。顺手把 IPv4 排到 IPv6 前面：国内不少网络
    /// 有 IPv6 地址但没有 IPv6 路由，先试 IPv6 等于白等一轮超时。
    /// </summary>
    private static SocketsHttpHandler CreateLoadHandler() => new()
    {
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            return await ConnectAsync(OrderAddresses(addresses), context.DnsEndPoint.Port, cancellationToken);
        },
    };

    /// <summary>
    /// IPv4 一律排在 IPv6 前面；轮换只在 IPv4 这一组内做。
    /// 国内不少网络有 IPv6 地址却没有 IPv6 路由，先试 IPv6 等于白等一轮超时，所以 IPv6 只能垫底。
    /// 而 IPv4 这一组内部要换着起点来，否则每次尝试都先撞同一个边缘 IP，重试再多也白试。
    /// </summary>
    private static IReadOnlyList<IPAddress> OrderAddresses(IPAddress[] addresses)
    {
        var ipv4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        var others = addresses.Where(a => a.AddressFamily != AddressFamily.InterNetwork).ToArray();

        if (ipv4.Length == 0)
        {
            return others;
        }

        var offset = Interlocked.Increment(ref _addressCursor) % ipv4.Length;
        return ipv4.Skip(offset).Concat(ipv4.Take(offset)).Concat(others).ToArray();
    }

    /// <summary>按顺序连各个地址，单个地址连这么久还没动静就换下一个；全都不通抛最后一个异常。</summary>
    private static async Task<Stream> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
    {
        if (addresses.Count == 0)
        {
            throw new HttpRequestException("DNS 没有返回任何地址");
        }

        Exception? failure = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(AddressConnectTimeout);
                await socket.ConnectAsync(address, port, budget.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                failure = ex;
            }
        }

        throw failure!;
    }

    private static List<string> FindLeftoverVariables(IEnumerable<string> xmlPaths)
    {
        return xmlPaths
            .SelectMany(path => LeftoverVariablePattern.Matches(File.ReadAllText(path)).Select(m => m.Value))
            .Distinct()
            .ToList();
    }

    private static void ValidateHard(string flatPath, string outputDir)
    {
        var metadata = Metadata.LoadFromFile(flatPath);
        var errors = new List<string>();

        // 仅「登记节点」（带 ID 属性）进入索引；Style 内 <Image>id</Image> 引用不算登记。
        // Logo / Background Image 只认图片登记，不认 Markdown / Manifest 等同名 ID。
        var idIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registryImages = metadata.GetAllElements("Image").Where(n => !string.IsNullOrWhiteSpace(n.Get("ID"))).ToList();
        var registryMarkdowns = metadata.GetAllElements("Markdown").Where(n => !string.IsNullOrWhiteSpace(n.Get("ID"))).ToList();
        var registryManifests = metadata.GetAllElements("Manifest").Where(n => !string.IsNullOrWhiteSpace(n.Get("ID"))).ToList();
        var imageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in registryImages.Concat(registryMarkdowns).Concat(registryManifests))
            idIndex.Add(node.Get("ID")!);
        foreach (var image in registryImages)
            imageIds.Add(image.Get("ID")!);

        foreach (var md in registryMarkdowns)
        {
            var id = md.Get("ID")!;
            var source = md.Get("Source");
            if (string.IsNullOrWhiteSpace(source))
            {
                errors.Add($"Markdown '{id}' 缺少 Source");
                continue;
            }
            var path = Path.Combine(outputDir, source.Replace('\\', '/'));
            if (!File.Exists(path))
                errors.Add($"Markdown 资源不存在: {source} (ID: {id})");
        }

        foreach (var image in registryImages)
        {
            var id = image.Get("ID")!;
            var url = image.Get("Url");
            var source = image.Get("Source");
            if (!string.IsNullOrWhiteSpace(url))
                continue;
            if (string.IsNullOrWhiteSpace(source))
            {
                errors.Add($"Image '{id}' 缺少 Source 或 Url");
                continue;
            }
            var path = Path.Combine(outputDir, source.Replace('\\', '/'));
            if (!File.Exists(path))
                errors.Add($"Image 资源不存在: {source} (ID: {id})");
        }

        foreach (var manifest in registryManifests)
        {
            var id = manifest.Get("ID")!;
            var source = manifest.Get("Source");
            if (!string.IsNullOrWhiteSpace(source))
            {
                var path = Path.Combine(outputDir, source.Replace('\\', '/'));
                if (!File.Exists(path))
                    errors.Add($"Manifest 资源不存在: {source} (ID: {id})");
            }
        }

        foreach (var app in metadata.GetAllElements("Application").Concat(metadata.GetAllElements("Mod")))
        {
            ValidateEntityDisplayNames(app, errors);
            ValidateIdRef(app.Find("Icon")?.Value, "Icon", idIndex, errors);
            var packages = app.Find("Packages");
            if (packages != null)
            {
                foreach (var package in packages.Children.Where(c => c.Name == "Package"))
                {
                    ValidateIdRef(package.Find("Manifest")?.Value, "Manifest", idIndex, errors);
                }
            }

            var posts = app.Find("Posts");
            if (posts != null)
            {
                foreach (var post in posts.Children.Where(c => c.Name == "Post"))
                {
                    // Post 封面只认图片登记：引用 Markdown / Manifest 或不存在都报错。
                    ValidateIdRef(post.Find("HeadImage")?.Value?.Trim(), "Post HeadImage", imageIds, errors);

                    var contents = post.Find("Contents");
                    if (contents == null) continue;
                    foreach (var content in contents.Children.Where(c => c.Name == "Content"))
                        ValidateIdRef(content.Value, "Post Content", idIndex, errors);
                }
            }

            var style = app.Find("Style");
            if (style == null) continue;
            var logo = style.Find("Logo");
            if (logo?.Value != null)
                ValidateIdRef(logo.Value.Trim(), "Logo", imageIds, errors);
            var background = style.Find("Background");
            if (background == null) continue;
            foreach (var img in background.Children.Where(c => c.Name == "Image"))
                ValidateIdRef(img.Value?.Trim(), "Background Image", imageIds, errors);
        }

        if (string.IsNullOrWhiteSpace(metadata.Get("SchemaVersion"))
            && metadata.Find("SchemaVersion") == null)
        {
            // 属性写在根上
            if (!metadata.Variables.ContainsKey("SchemaVersion"))
                errors.Add("缺少 SchemaVersion");
        }

        if (!metadata.Variables.ContainsKey("ContentRevision") && metadata.Find("ContentRevision") == null)
            errors.Add("缺少 ContentRevision");

        if (errors.Count > 0)
            throw new InvalidOperationException("核心构建校验失败:\n- " + string.Join("\n- ", errors));
    }

    /// <summary>
    /// 实体显示名硬校验：Mod 与 Application 都必须声明 <c>zh-CN</c> 与 <c>en-US</c> 两条显示名
    /// （完整语言标签精确匹配，比较不区分大小写，大小写变体如 <c>Zh-Cn</c> 也算），
    /// 完整语言标签不许重复（不区分大小写），语言与文本都不能为空白；允许再声明其它语言。
    /// 显示名属于实体自身，不从 Base 继承——校验针对合并后的节点。
    /// </summary>
    private static void ValidateEntityDisplayNames(Metadata entity, List<string> errors)
    {
        var name = $"{entity.Name} '{entity.Get("ID")}'";
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var displayName in entity.Children.Where(c => c.Name == "DisplayName"))
        {
            var language = displayName.Get("Language");
            if (string.IsNullOrWhiteSpace(language))
            {
                errors.Add($"{name}: DisplayName 缺少 Language");
            }
            else if (!languages.Add(language))
            {
                errors.Add($"{name}: DisplayName Language 重复: {language}");
            }

            if (string.IsNullOrWhiteSpace(displayName.Value))
                errors.Add($"{name}: DisplayName（{language ?? "?"}）文本不能为空");
        }

        if (!languages.Contains("zh-CN"))
            errors.Add($"{name}: 缺少 zh-CN DisplayName");
        if (!languages.Contains("en-US"))
            errors.Add($"{name}: 缺少 en-US DisplayName");
    }

    private static void ValidateIdRef(string? id, string kind, HashSet<string> idIndex, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!idIndex.Contains(id))
            errors.Add($"{kind} 引用未找到 ID: {id}");
    }

    /// <summary>
    /// 源树 Manifest 语义硬校验：仅当清单声明新格式（带 HashAlgorithm 属性，或任一 File 含 Sources）时执行；
    /// 旧清单不触发，保持原样可构建。File 表只存在于源树叶子清单，不进展平物，故在此阶段扫描源文件。
    /// </summary>
    private static void ValidateManifests(string sourceRoot)
    {
        var errors = new List<string>();
        foreach (var xmlPath in Directory.GetFiles(sourceRoot, "*.xml", SearchOption.AllDirectories))
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(xmlPath);
            }
            catch
            {
                continue; // 语法错误已由 XSD 校验报出
            }

            var rel = Path.GetRelativePath(sourceRoot, xmlPath).Replace('\\', '/');
            foreach (var manifest in doc.Root!.DescendantsAndSelf().Where(e => e.Name.LocalName == "Manifest"))
                ValidateManifestNode(manifest, rel, errors);
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("Manifest 校验失败:\n- " + string.Join("\n- ", errors));
    }

    private static void ValidateManifestNode(XElement manifest, string rel, List<string> errors)
    {
        var id = manifest.Attribute("ID")?.Value ?? string.Empty;
        var files = manifest.Elements().Where(e => e.Name.LocalName == "File").ToList();
        var algoAttr = manifest.Attribute("HashAlgorithm")?.Value;
        var skudef = manifest.Elements().FirstOrDefault(e => e.Name.LocalName == "Skudef");

        // 新格式触发条件：声明 HashAlgorithm、写了 Skudef，或任一 File 带 Sources
        var isNewFormat = algoAttr != null
            || skudef != null
            || files.Any(f => f.Elements().Any(e => e.Name.LocalName == "Sources"));
        if (!isNewFormat)
            return;

        var algo = string.IsNullOrWhiteSpace(algoAttr) ? DefaultHashAlgorithm : algoAttr.Trim().ToUpperInvariant();
        if (!HashHexLengths.ContainsKey(algo))
        {
            errors.Add($"{rel} (Manifest ID: {id}): HashAlgorithm 非法: {algoAttr}（仅支持 CRC32C/MD5/SHA256）");
            algo = DefaultHashAlgorithm;
        }
        var hashLen = HashHexLengths[algo];

        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var fileName = ChildValue(file, "FileName");
            var where = $"{rel} (Manifest ID: {id}) File \"{fileName}\"";

            var hash = file.Attribute("Hash")?.Value ?? string.Empty;
            if (hash.Length != hashLen)
                errors.Add($"{where}: Hash 长度应为 {hashLen}（{algo}），实际 {hash.Length}");
            else if (hash.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(hash[0])))
                errors.Add($"{where}: Hash 为占位值（全部同一字符）");

            var relativePath = ChildValue(file, "RelativePath");
            if (relativePath.StartsWith("..", StringComparison.Ordinal) || IsAbsolutePath(relativePath))
                errors.Add($"{where}: RelativePath 必须是相对路径: {relativePath}");

            if (!seenFiles.Add(fileName + "\u0000" + relativePath))
                errors.Add($"{where}: FileName + RelativePath 重复: {fileName} + {relativePath}");

            var sizeAttr = file.Attribute("Size");
            if (sizeAttr != null && (!long.TryParse(sizeAttr.Value, out var size) || size <= 0))
                errors.Add($"{where}: Size 必须是正整数: {sizeAttr.Value}");

            var downloadName = file.Attribute("DownloadName")?.Value;
            if (downloadName != null && (string.IsNullOrWhiteSpace(downloadName)
                || downloadName.Contains('/') || downloadName.Contains('\\')))
                errors.Add($"{where}: DownloadName 必须是不含路径分隔符的文件名: {downloadName}");

            var compression = file.Attribute("Compression")?.Value;
            if (compression != null)
            {
                if (string.IsNullOrWhiteSpace(downloadName))
                    errors.Add($"{where}: 声明 Compression 时必须写 DownloadName");
                else if (string.Equals(downloadName, fileName, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{where}: DownloadName 与 FileName 相同，解压源与安装名不能同名");
            }

            var sources = file.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Sources")?
                .Elements().Where(e => e.Name.LocalName == "Source").ToList()
                ?? new List<XElement>();
            if (sources.Count == 0)
                errors.Add($"{where}: 缺少 Source（新格式清单每个 File 至少一个来源）");

            foreach (var source in sources)
            {
                var type = (source.Attribute("Type")?.Value ?? string.Empty).Trim().ToUpperInvariant();
                var url = source.Attribute("Url")?.Value ?? string.Empty;
                if (type == "BT")
                {
                    if (!url.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                        errors.Add($"{where}: BT 地址必须以 .torrent 结尾: {url}");
                }
                else if (type == "HTTP")
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        errors.Add($"{where}: HTTP 地址必须是 http/https 绝对地址: {url}");
                }
                else
                {
                    errors.Add($"{where}: Source Type 非法: {type}");
                }
            }
            if (skudef != null)
            {
                if (file.Attribute("Mount") != null || file.Attribute("Language") != null || file.Attribute("Package") != null)
                    errors.Add($"{where}: 带 Skudef 的清单里 File 不能再写 Mount/Language/Package（加载条件写在 AddBig 上）");
            }
            else
            {
                ValidateMountRole(file, where, errors);
            }
        }

        if (skudef != null)
            ValidateSkudef(skudef, files, rel, id, errors);

        var dependencies = manifest.Elements().FirstOrDefault(e => e.Name.LocalName == "Dependencies");
        if (dependencies == null)
            return;

        var seenDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in dependencies.Elements().Where(e => e.Name.LocalName == "Dll"))
        {
            var name = dll.Attribute("Name")?.Value ?? string.Empty;
            var where = $"{rel} (Manifest ID: {id}) Dll \"{name}\"";
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"{rel} (Manifest ID: {id}): Dll Name 不能为空");
                continue;
            }

            if (!seenDlls.Add(name))
                errors.Add($"{where}: Dll Name 重复");

            var hash = dll.Attribute("Hash")?.Value ?? string.Empty;
            if (hash.Length != hashLen)
                errors.Add($"{where}: Hash 长度应为 {hashLen}（{algo}），实际 {hash.Length}");
        }
    }

    /// <summary>
    /// Skudef 语义硬校验：GameVersion 格式、FileName 唯一、AddBig/AddConfig 的属性与引用关系
    /// （每个 File 恰好被引用一次，不允许悬空或重复引用）。
    /// </summary>
    private static void ValidateSkudef(XElement skudef, List<XElement> files, string rel, string id, List<string> errors)
    {
        var where = $"{rel} (Manifest ID: {id}) Skudef";

        var gameVersion = skudef.Attribute("GameVersion")?.Value;
        if (!string.IsNullOrWhiteSpace(gameVersion) && !GameVersionPattern.IsMatch(gameVersion))
            errors.Add($"{where}: GameVersion 非法: {gameVersion}（形如 1.12）");

        var commands = skudef.Elements().ToList();
        if (commands.Count == 0)
            errors.Add($"{where}: 没有任何指令");

        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var fileName = ChildValue(file, "FileName");
            if (!fileNames.Add(fileName))
                errors.Add($"{where}: FileName 重复: {fileName}");
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            if (command.Name.LocalName == "AddBig")
            {
                var target = command.Attribute("File")?.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(target))
                {
                    errors.Add($"{where}: AddBig 缺少 File");
                    continue;
                }

                if (!fileNames.Contains(target))
                    errors.Add($"{where}: 引用了不存在的 FileName: {target}");
                if (!referenced.Add(target))
                    errors.Add($"{where}: FileName 被多条 AddBig 引用: {target}");
                ValidateAddBigCondition(command, $"{where} AddBig \"{target}\"", errors);
            }
            else
            {
                var target = command.Attribute("LocalFile")?.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(target))
                {
                    errors.Add($"{where}: AddConfig 缺少 LocalFile");
                    continue;
                }

                if (!ModSkudefLocalFilePattern.IsMatch(target))
                    errors.Add($"{where}: AddConfig LocalFile 必须是纯文件名: {target}");
            }
        }

        foreach (var fileName in fileNames)
        {
            if (!referenced.Contains(fileName))
                errors.Add($"{where}: File \"{fileName}\" 没有被 AddBig 引用（每个文件都要恰好引用一次）");
        }
    }

    /// <summary>AddBig 的加载条件：Language 与 Package 只能写一个，取值必须是 token。</summary>
    private static void ValidateAddBigCondition(XElement command, string where, List<string> errors)
    {
        var language = command.Attribute("Language")?.Value;
        var package = command.Attribute("Package")?.Value;
        if (language != null && package != null)
        {
            errors.Add($"{where}: 同时写了 Language 与 Package");
            return;
        }

        if (language != null && !MountTokenPattern.IsMatch(language))
            errors.Add($"{where}: Language 只能包含字母数字下划线连字符: {language}");
        if (package != null && !MountTokenPattern.IsMatch(package))
            errors.Add($"{where}: Package 只能包含字母数字下划线连字符: {package}");
    }

    /// <summary>Mount 缺省 base。language 必填 Language，optional 必填 Package，base 不能带这两个属性。</summary>
    private static void ValidateMountRole(XElement file, string where, List<string> errors)
    {
        var mountRaw = file.Attribute("Mount")?.Value;
        var mount = string.IsNullOrWhiteSpace(mountRaw) ? "base" : mountRaw.Trim().ToLowerInvariant();
        if (mount is not ("base" or "language" or "optional"))
        {
            errors.Add($"{where}: Mount 非法: {mountRaw}（仅支持 base/language/optional）");
            return;
        }

        var language = file.Attribute("Language")?.Value;
        var package = file.Attribute("Package")?.Value;
        if (mount == "base")
        {
            if (language != null)
                errors.Add($"{where}: Mount=\"base\" 不能带 Language");
            if (package != null)
                errors.Add($"{where}: Mount=\"base\" 不能带 Package");
            return;
        }

        if (mount == "language")
        {
            if (string.IsNullOrWhiteSpace(language))
                errors.Add($"{where}: Mount=\"language\" 必须写 Language");
            else if (!MountTokenPattern.IsMatch(language))
                errors.Add($"{where}: Language 只能是字母、数字、下划线或连字符: {language}");
            return;
        }

        if (string.IsNullOrWhiteSpace(package))
            errors.Add($"{where}: Mount=\"optional\" 必须写 Package");
        else if (!MountTokenPattern.IsMatch(package))
            errors.Add($"{where}: Package 只能是字母、数字、下划线或连字符: {package}");
    }

    private static string ChildValue(XElement element, string childName)
    {
        return element.Elements().FirstOrDefault(e => e.Name.LocalName == childName)?.Value ?? string.Empty;
    }

    /// <summary>盘符或 UNC 视为绝对路径；以 <c>/</c> 开头的清单内路径合法（相对安装根）。</summary>
    private static bool IsAbsolutePath(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.StartsWith("//", StringComparison.Ordinal))
            return true;
        return p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':';
    }
}
