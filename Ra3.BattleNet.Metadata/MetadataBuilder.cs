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
    /// 从本地路径或 HTTP(S) URL 加载已展平的 metadata.xml。
    /// </summary>
    public static Metadata Load(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
            throw new ArgumentException("路径或 URL 不能为空", nameof(pathOrUrl));

        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return LoadFromUrl(uri);
        }

        return Metadata.LoadFromFile(pathOrUrl);
    }

    private static Metadata LoadFromUrl(Uri uri)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
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

        // 仅「登记节点」（带 ID 属性）进入索引；Style 内 <Image>id</Image> 引用不算登记
        var idIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registryImages = metadata.GetAllElements("Image").Where(n => !string.IsNullOrWhiteSpace(n.Get("ID"))).ToList();
        var registryMarkdowns = metadata.GetAllElements("Markdown").Where(n => !string.IsNullOrWhiteSpace(n.Get("ID"))).ToList();
        var registryManifests = metadata.GetAllElements("Manifest").Where(n => !string.IsNullOrWhiteSpace(n.Get("ID"))).ToList();

        foreach (var node in registryImages.Concat(registryMarkdowns).Concat(registryManifests))
            idIndex.Add(node.Get("ID")!);

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
            ValidateIdRef(app.Find("Icon")?.Value, "Icon", idIndex, errors);
            var packages = app.Find("Packages");
            if (packages == null) continue;
            foreach (var package in packages.Children.Where(c => c.Name == "Package"))
            {
                ValidateIdRef(package.Find("Manifest")?.Value, "Manifest", idIndex, errors);
                var changelogs = package.Find("Changelogs");
                if (changelogs == null) continue;
                foreach (var cl in changelogs.Children.Where(c => c.Name == "Changelog"))
                    ValidateIdRef(cl.Value, "Changelog", idIndex, errors);
            }

            var posts = app.Find("Posts");
            if (posts == null) continue;
            foreach (var post in posts.Children.Where(c => c.Name == "Post"))
            {
                var contents = post.Find("Contents");
                if (contents == null) continue;
                foreach (var content in contents.Children.Where(c => c.Name == "Content"))
                    ValidateIdRef(content.Value, "Post Content", idIndex, errors);
            }

            var style = app.Find("Style");
            if (style == null) continue;
            var logo = style.Find("Logo");
            if (logo?.Value != null)
                ValidateIdRef(logo.Value.Trim(), "Logo", idIndex, errors);
            var background = style.Find("Background");
            if (background != null)
            {
                foreach (var img in background.Children.Where(c => c.Name == "Image"))
                    ValidateIdRef(img.Value?.Trim(), "Background Image", idIndex, errors);
            }
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
