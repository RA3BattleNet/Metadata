using System.Text;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 把已展平的在线元数据导出成单个 Mod 的自包含作者源声明，是 <see cref="MetadataBuilder.LoadSource"/> 的反向操作。
///
/// 只抄这个 Mod 自己的节点和它直接引用的 <c>Image</c> / <c>Markdown</c> 登记，不夹带其他 Mod 或 Application。
/// 资源引用换成不含发布前缀的短 ID；登记节点的 <c>Source</c> 既可以是相对发布根的路径，也可以解析成绝对地址。
/// 作者样式、文章、外链与兼容性原样保留。
/// </summary>
public static class MetadataSourceExporter
{
    /// <summary>
    /// 整份模组声明：保留该 Mod 的全部版本与线上 <c>CurrentVersion</c>，不挑版本、不改当前版本。
    /// 每个版本都指向本目录的短清单叶子，资源引用与安装路径一致。此方法只做纯转换，不读写磁盘、不发网络请求。
    /// </summary>
    /// <param name="root">已展平的在线元数据根。</param>
    /// <param name="modId">要导出的 Mod ID（不区分大小写）。</param>
    /// <param name="publishedMetadataUrl">
    /// 发布物入口地址；非空时把登记 <c>Source</c> 解析成绝对地址，
    /// 为空时保留登记里的相对 <c>Source</c>，供本地导入直接使用。
    /// </param>
    public static string ExportWhole(Metadata root, string modId, string? publishedMetadataUrl = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);

        return Build(root, FindMod(root, modId), publishedMetadataUrl, selectedVersion: null);
    }

    /// <summary>
    /// 单版本声明：只保留该版本、把它设成 <c>CurrentVersion</c>，其余规则同 <see cref="ExportWhole"/>。
    /// 此方法只做纯转换，不读写磁盘、不发网络请求。
    /// </summary>
    /// <param name="root">已展平的在线元数据根。</param>
    /// <param name="modId">要导出的 Mod ID（不区分大小写）。</param>
    /// <param name="version">要保留的版本号。</param>
    /// <param name="publishedMetadataUrl">发布物入口地址；为空时保留相对 <c>Source</c>。</param>
    public static string ExportVersion(Metadata root, string modId, string version,
        string? publishedMetadataUrl = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        return Build(root, FindMod(root, modId), publishedMetadataUrl, version);
    }

    private static Metadata FindMod(Metadata root, string modId) =>
        root.Mods().FirstOrDefault(item => string.Equals(item.Id, modId, StringComparison.OrdinalIgnoreCase))?.Raw
        ?? throw new InvalidOperationException($"元数据里没有模组 {modId}");

    private static string Build(Metadata root, Metadata modRaw, string? publishedMetadataUrl, string? selectedVersion)
    {
        var localSource = string.IsNullOrWhiteSpace(publishedMetadataUrl);
        Func<string, string> resolveSource = localSource
            ? source => source
            : source => MetadataResourceUri.Resolve(publishedMetadataUrl!, source).AbsoluteUri;

        var mod = Copy(modRaw);
        if (selectedVersion is null)
        {
            NormalizePackages(mod);
        }
        else
        {
            KeepSelectedPackage(mod, selectedVersion);
            SetCurrentVersion(mod, selectedVersion);
        }

        var images = ImageIndex(root);
        var markdowns = MarkdownIndex(root);
        var imageIds = new ShortIdGenerator("image");
        var markdownIds = new ShortIdGenerator("markdown");
        var imageRegistrations = new List<XElement>();
        var markdownRegistrations = new List<XElement>();
        var mappedImages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mappedMarkdowns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? MapImage(string? oldId)
        {
            var id = oldId?.Trim();
            if (string.IsNullOrEmpty(id))
                return null;
            if (mappedImages.TryGetValue(id, out var mapped))
                return mapped;
            if (!images.TryGetValue(id, out var registration))
                return null;

            mapped = imageIds.Create(id);
            mappedImages[id] = mapped;
            imageRegistrations.Add(ImageRegistration(registration, mapped, resolveSource, localSource));
            return mapped;
        }

        string? MapMarkdown(string? oldId)
        {
            var id = oldId?.Trim();
            if (string.IsNullOrEmpty(id))
                return null;
            if (mappedMarkdowns.TryGetValue(id, out var mapped))
                return mapped;
            if (!markdowns.TryGetValue(id, out var registration))
                return null;

            mapped = markdownIds.Create(id);
            mappedMarkdowns[id] = mapped;
            markdownRegistrations.Add(MarkdownRegistration(registration, mapped, resolveSource));
            return mapped;
        }

        Rewrite(mod.Element("Icon"), MapImage);
        var style = mod.Element("Style");
        Rewrite(style?.Element("Logo"), MapImage);
        if (style?.Element("Background") is { } background)
        {
            foreach (var image in background.Elements("Image"))
                Rewrite(image, MapImage);
        }

        if (mod.Element("Posts") is { } posts)
        {
            foreach (var post in posts.Elements("Post"))
            {
                Rewrite(post.Element("HeadImage"), MapImage);
                if (post.Element("Contents") is { } contents)
                {
                    foreach (var content in contents.Elements("Content"))
                        Rewrite(content, MapMarkdown);
                }
            }
        }

        if (mod.Element("Links") is { } links)
        {
            foreach (var link in links.Elements("Link"))
                Rewrite(link.Element("Icon"), MapImage);
        }

        // 声明不写根属性：每次生成都一样，落盘时本来也会被去掉。
        var document = new XElement("Metadata",
            imageRegistrations,
            markdownRegistrations,
            mod);
        return document.ToString();
    }

    private static XElement Copy(Metadata node) => XElement.Parse(MetadataXml.Write(node));

    private static void KeepSelectedPackage(XElement mod, string version)
    {
        var packages = mod.Element("Packages")
            ?? throw new InvalidOperationException($"模组 {(string?)mod.Attribute("ID")} 没有 Packages");
        var selected = packages.Elements("Package")
            .FirstOrDefault(package => string.Equals((string?)package.Attribute("Version"), version, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"模组 {(string?)mod.Attribute("ID")} 没有版本 {version}");

        foreach (var package in packages.Elements("Package").Where(package => package != selected).ToList())
            package.Remove();

        var manifest = selected.Element("Manifest")
            ?? throw new InvalidOperationException($"模组 {(string?)mod.Attribute("ID")} 的版本 {version} 没有声明 Manifest");
        manifest.Value = "manifest-" + version;
    }

    /// <summary>整份声明里每个版本都指向本目录的短清单叶子；<c>CurrentVersion</c> 保持线上声明。</summary>
    private static void NormalizePackages(XElement mod)
    {
        foreach (var package in mod.Element("Packages")?.Elements("Package") ?? Enumerable.Empty<XElement>())
        {
            var version = (string?)package.Attribute("Version");
            if (string.IsNullOrWhiteSpace(version))
                continue;
            if (package.Element("Manifest") is { } manifest)
                manifest.Value = "manifest-" + version;
        }
    }

    private static void SetCurrentVersion(XElement mod, string version)
    {
        var current = mod.Element("CurrentVersion");
        if (current is null)
            mod.AddFirst(new XElement("CurrentVersion", version));
        else
            current.Value = version;
    }

    private static Dictionary<string, ImageEntry> ImageIndex(Metadata root)
    {
        var index = new Dictionary<string, ImageEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in root.Images())
        {
            if (!string.IsNullOrWhiteSpace(image.Id))
                index.TryAdd(image.Id, image);
        }

        return index;
    }

    private static Dictionary<string, MarkdownEntry> MarkdownIndex(Metadata root)
    {
        var index = new Dictionary<string, MarkdownEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var markdown in root.Markdowns())
        {
            if (!string.IsNullOrWhiteSpace(markdown.Id))
                index.TryAdd(markdown.Id, markdown);
        }

        return index;
    }

    private static XElement ImageRegistration(ImageEntry image, string newId, Func<string, string> resolveSource,
        bool localSource)
    {
        var registration = new XElement("Image", new XAttribute("ID", newId));
        var url = image.Url?.Trim();
        var source = image.Source?.Trim();
        if (!string.IsNullOrEmpty(url))
        {
            registration.SetAttributeValue("Url", url);
            // 带 Url 的节点在展平时不会改写 Source；解析成绝对地址反而会指错位置。
            if (!localSource)
                return registration;
        }

        if (string.IsNullOrEmpty(source))
        {
            if (!string.IsNullOrEmpty(url))
                return registration;
            throw new InvalidOperationException($"图片 {image.Id} 既没有 Source 也没有 Url");
        }

        registration.SetAttributeValue("Source", resolveSource(source));
        return registration;
    }

    private static XElement MarkdownRegistration(MarkdownEntry markdown, string newId, Func<string, string> resolveSource)
    {
        var source = markdown.Source?.Trim();
        if (string.IsNullOrEmpty(source))
            throw new InvalidOperationException($"Markdown {markdown.Id} 没有 Source");
        return new XElement("Markdown",
            new XAttribute("ID", newId),
            new XAttribute("Source", resolveSource(source)));
    }

    private static void Rewrite(XElement? node, Func<string?, string?> map)
    {
        if (node is null)
            return;
        var old = node.Value.Trim();
        if (old.Length == 0)
            return;
        if (map(old) is { } mapped)
            node.Value = mapped;
    }

    /// <summary>把发布物里的限定 ID 压成本地短 ID；同一类资源内保证唯一，跨版本保持不变。</summary>
    private sealed class ShortIdGenerator
    {
        private const int MaxSlugLength = 48;
        private readonly string _prefix;
        private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

        public ShortIdGenerator(string prefix) => _prefix = prefix;

        public string Create(string oldId)
        {
            var local = oldId;
            var separator = local.LastIndexOf(':');
            if (separator >= 0 && separator < local.Length - 1)
                local = local[(separator + 1)..];

            var slug = new StringBuilder(local.Length);
            foreach (var character in local)
            {
                if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
                    slug.Append(char.ToLowerInvariant(character));
                else if (character is '-' or '_' or '.')
                    slug.Append(character);
                else
                    slug.Append('-');
            }

            var trimmed = slug.ToString().Trim('-', '.', '_');
            if (trimmed.Length > MaxSlugLength)
                trimmed = trimmed[..MaxSlugLength].TrimEnd('-', '.', '_');
            if (trimmed.Length == 0)
                trimmed = "resource";

            var candidate = $"{_prefix}-{trimmed}";
            var unique = candidate;
            var suffix = 2;
            while (!_used.Add(unique))
                unique = $"{candidate}-{suffix++}";
            return unique;
        }
    }
}
