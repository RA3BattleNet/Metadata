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

    /// <summary>十六进制哈希字符集；长度另按算法校验。</summary>
    private static readonly Regex HexHashPattern = new("^[0-9A-Fa-f]+$", RegexOptions.Compiled);

    private const string DefaultHashAlgorithm = "CRC32C";

    /// <summary>不做内容哈希校验的算法值：清单用 None 声明，File@Hash 必须省略或为空。</summary>
    private const string NoHashAlgorithm = "NONE";

    /// <summary>各算法的十六进制哈希长度。</summary>
    private static readonly Dictionary<string, int> HashHexLengths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CRC32C"] = 8,
        ["MD5"] = 32,
        ["SHA256"] = 64,
    };

    /// <summary>
    /// 从本地源目录执行核心构建。SchemaVersion 由作者写在源根 metadata.xml 上，构建不写任何版本或修订标记。
    /// </summary>
    /// <param name="convertImages">true 时在展平后调用 Imaging CLI 转 WebP，并由本管线改写 Source/Hash。</param>
    public static void Build(
        string sourceDir,
        string outputDir,
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
        MetadataExporter.ValidateOutput(src, dst);

        if (Directory.Exists(dst))
            Directory.Delete(dst, recursive: true);
        Directory.CreateDirectory(dst);

        try
        {
            var sourceSchema = SchemaValidator.FindSchema(src, SchemaValidator.SourceSchemaFileName)
                ?? throw new FileNotFoundException($"找不到源树 XSD: {SchemaValidator.SourceSchemaFileName}");
            SchemaValidator.EnsureDirectoryValid(src, sourceSchema, "源树");
            ValidateManifests(src);

            var input = MetadataReader.Read(entry, new MetadataSourceOptions
            {
                SourceRoot = src
            });
            if (string.IsNullOrWhiteSpace(input.Document.Root!.Attribute("SchemaVersion")?.Value))
                throw new InvalidOperationException("源根 metadata.xml 的根节点缺少 SchemaVersion 属性");
            ModCompatibility.Validate(input.Document.Root!);
            var flatPath = Path.Combine(dst, "metadata.xml");
            var xmlPaths = MetadataExporter.Export(input, dst);
            var resolver = new VariableResolver();
            CopyIfExists(Path.Combine(src, "_redirects"), Path.Combine(dst, "_redirects"));
            CopyIfExists(Path.Combine(src, "edgeone.json"), Path.Combine(dst, "edgeone.json"));
            CopyIfExists(Path.Combine(src, "editor.html"), Path.Combine(dst, "editor.html"));

            foreach (var path in xmlPaths)
            {
                var document = XDocument.Load(path);
                resolver.ReplaceInElement(document.Root!);
                document.Save(path);
            }

            var leftover = FindLeftoverVariables(xmlPaths);
            if (leftover.Count > 0)
                throw new InvalidOperationException("变量替换后仍有残留: " + string.Join("; ", leftover));

            // 展平后：按图调用 Imaging（只产 webp），本管线写回 XML
            if (convertImages)
            {
                var n = ImagePostProcessor.ApplyWebP(dst, xmlPaths.Select(path => Path.GetRelativePath(dst, path)));
                Console.WriteLine($"  Imaging: 转换 {n} 张图片并写回 Source");
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


    private static void CopyIfExists(string from, string to)
    {
        if (!File.Exists(from)) return;
        var dir = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.Copy(from, to, overwrite: true);
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
        var metadata = MetadataReader.Read(flatPath).Root;
        var errors = new List<string>();

        var registryImages = metadata.GetAllElements("Image").Where(n => !string.IsNullOrWhiteSpace(n.Get("ID"))).ToList();
        var registryMarkdowns = metadata.GetAllElements("Markdown").Where(n => !string.IsNullOrWhiteSpace(n.Get("ID"))).ToList();
        var registryManifests = metadata.GetAllElements("Manifest").Where(n => !string.IsNullOrWhiteSpace(n.Get("ID"))).ToList();

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

        errors.AddRange(MetadataValidator.FindMissingReferences(metadata));
        foreach (var entity in metadata.GetAllElements("Application").Concat(metadata.GetAllElements("Mod")))
            ValidateEntityDisplayNames(entity, errors);

        if (errors.Count > 0)
            throw new InvalidOperationException("核心构建校验失败:\n- " + string.Join("\n- ", errors));
    }

    /// <summary>
    /// 检查实体显示名：Mod 和 Application 必须写齐 zh 和 en 两个显示名（不区分大小写，像 Zh 也认）。
    /// 不能写重复的语言，语言和名字都不能留空。自己有就用自己的，不从 Base 继承。
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

        if (!languages.Contains("zh"))
            errors.Add($"{name}: 缺少 zh DisplayName");
        if (!languages.Contains("en"))
            errors.Add($"{name}: 缺少 en DisplayName");
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

        // 新格式触发条件也包含下载物指纹，不能借旧格式绕过校验
        var isNewFormat = algoAttr != null
            || skudef != null
            || files.Any(f => f.Attribute("RawHash") != null || f.Attribute("RawHashAlgorithm") != null
                || f.Elements().Any(e => e.Name.LocalName == "Sources"));
        if (!isNewFormat)
            return;

        var algo = string.IsNullOrWhiteSpace(algoAttr) ? DefaultHashAlgorithm : algoAttr.Trim().ToUpperInvariant();
        if (algo != NoHashAlgorithm && !HashHexLengths.ContainsKey(algo))
        {
            errors.Add($"{rel} (Manifest ID: {id}): HashAlgorithm 非法: {algoAttr}（仅支持 CRC32C/MD5/SHA256/None）");
            algo = DefaultHashAlgorithm;
        }
        var noHash = algo == NoHashAlgorithm;
        var hashLen = noHash ? 0 : HashHexLengths[algo];

        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var fileName = ChildValue(file, "FileName");
            var where = $"{rel} (Manifest ID: {id}) File \"{fileName}\"";

            var hash = file.Attribute("Hash")?.Value ?? string.Empty;
            if (noHash)
            {
                if (hash.Length != 0)
                    errors.Add($"{where}: HashAlgorithm=None 时不能写 File@Hash（实际 {hash.Length} 位）");
            }
            else if (hash.Length != hashLen)
            {
                errors.Add($"{where}: Hash 长度应为 {hashLen}（{algo}），实际 {hash.Length}");
            }
            else if (!HexHashPattern.IsMatch(hash))
            {
                errors.Add($"{where}: Hash 必须是十六进制字符串（{algo}）: {hash}");
            }
            else if (hash.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(hash[0])))
            {
                errors.Add($"{where}: Hash 为占位值（全部同一字符）");
            }

            try
            {
                ManifestRawFingerprint.Validate(file.Attribute("RawHash")?.Value, file.Attribute("RawHashAlgorithm")?.Value, algo);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"{where}: {ex.Message}");
            }

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

                // zip 解压产物是目录，目录无法计算清单哈希，因此只能搭配 HashAlgorithm=None
                if (string.Equals(compression.Trim(), "zip", StringComparison.OrdinalIgnoreCase) && !noHash)
                    errors.Add($"{where}: Compression=\"zip\" 必须搭配 HashAlgorithm=\"None\"（zip 解压产物是目录，无法计算目录哈希）");
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
            // None 清单不做内容哈希校验，Dll 哈希也不参与长度校验
            if (!noHash && hash.Length != hashLen)
            {
                errors.Add($"{where}: Hash 长度应为 {hashLen}（{algo}），实际 {hash.Length}");
            }
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
