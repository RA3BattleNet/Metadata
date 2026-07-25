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

    /// <summary>
    /// 从本地源目录执行核心构建（不含 Imaging/WebP）。
    /// </summary>
    /// <param name="sourceDir">含 metadata.xml 的源目录。</param>
    /// <param name="outputDir">输出目录。</param>
    /// <param name="schemaVersion">契约版本，默认 1.0。</param>
    /// <param name="contentRevision">内容修订号；为空则用 UTC 时间戳。</param>
    public static void Build(string sourceDir, string outputDir, string? schemaVersion = null, string? contentRevision = null)
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
            CopyAll(src, dst);

            var revision = string.IsNullOrWhiteSpace(contentRevision)
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                : contentRevision;
            var version = string.IsNullOrWhiteSpace(schemaVersion) ? DefaultSchemaVersion : schemaVersion;

            var flattened = MetadataFlattener.Flatten(entry, src, version, revision);
            var flatPath = Path.Combine(dst, "metadata.xml");
            flattened.Save(flatPath);

            var resolver = new VariableResolver();
            resolver.ReplaceInFile(flatPath);

            var leftover = FindLeftoverVariables(flatPath);
            if (leftover.Count > 0)
                throw new InvalidOperationException("变量替换后仍有残留: " + string.Join("; ", leftover));

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

    private static void CopyAll(string src, string dst)
    {
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static List<string> FindLeftoverVariables(string xmlPath)
    {
        var text = File.ReadAllText(xmlPath);
        return LeftoverVariablePattern.Matches(text).Select(m => m.Value).Distinct().ToList();
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
}
