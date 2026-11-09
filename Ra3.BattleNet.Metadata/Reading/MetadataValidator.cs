namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 元数据引用完整性校验：登记节点（带 ID 的 Image / Markdown / Manifest）是唯一合法引用目标。
/// 规则与 MetadataBuilder.ValidateHard 的引用部分一致：
/// Icon / Package.Manifest / Post.Content 查登记全集；
/// Post.HeadImage / Style.Logo / Style.Background.Image 只查图片登记。
/// 只负责断引用，不负责字段缺失、资源文件是否存在等其它校验。
/// </summary>
public static class MetadataValidator
{
    /// <summary>
    /// 找出全部断引用，每条格式为 <c>{实体/节点} (ID): {字段} -> {引用值}</c>。
    /// ID 比较用 OrdinalIgnoreCase，引用值先 Trim，空值跳过。
    /// </summary>
    public static IReadOnlyList<string> FindMissingReferences(Metadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var registrations = metadata.GetAllElements("Image")
            .Concat(metadata.GetAllElements("Markdown"))
            .Concat(metadata.GetAllElements("Manifest"))
            .Where(node => !string.IsNullOrWhiteSpace(node.Get("ID")));
        var allIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var registration in registrations)
        {
            var id = registration.Get("ID")!;
            if (!allIds.Add(id))
                missing.Add($"登记 ID 重复: {id}");
            if (registration.Name == "Image")
                imageIds.Add(id);
        }
        foreach (var entity in metadata.GetAllElements("Application").Concat(metadata.GetAllElements("Mod")))
        {
            var entityId = entity.Get("ID");
            var owner = string.IsNullOrWhiteSpace(entityId) ? entity.Name : $"{entity.Name} ({entityId})";

            AddIfMissing(entity.Find("Icon")?.Value, "Icon", allIds, owner, missing);

            var packages = entity.Find("Packages");
            if (packages != null)
            {
                foreach (var package in packages.Children.Where(c => c.Name == "Package"))
                    AddIfMissing(package.Find("Manifest")?.Value, "Manifest", allIds, owner, missing);
            }

            var posts = entity.Find("Posts");
            if (posts != null)
            {
                foreach (var post in posts.Children.Where(c => c.Name == "Post"))
                {
                    // Post 封面只认图片登记：引用 Markdown / Manifest 或不存在都算断引用。
                    AddIfMissing(post.Find("HeadImage")?.Value, "Post HeadImage", imageIds, owner, missing);

                    var contents = post.Find("Contents");
                    if (contents == null)
                        continue;

                    foreach (var content in contents.Children.Where(c => c.Name == "Content"))
                        AddIfMissing(content.Value, "Post Content", allIds, owner, missing);
                }
            }

            var links = entity.Find("Links");
            if (links is not null)
            {
                foreach (var link in links.Children.Where(child => child.Name == "Link"))
                    AddIfMissing(link.Find("Icon")?.Value, "Link Icon", imageIds, owner, missing);
            }

            var style = entity.Find("Style");
            if (style == null)
                continue;

            var logo = style.Find("Logo");
            if (logo?.Value != null)
                AddIfMissing(logo.Value, "Logo", imageIds, owner, missing);

            var background = style.Find("Background");
            if (background == null)
                continue;

            foreach (var image in background.Children.Where(c => c.Name == "Image"))
                AddIfMissing(image.Value, "Background Image", imageIds, owner, missing);
        }

        return missing;
    }

    /// <summary>
    /// 存在断引用时抛 <see cref="InvalidOperationException"/>，消息首行固定，之后每条断引用一行。
    /// </summary>
    public static void EnsureReferences(Metadata metadata)
    {
        var missing = FindMissingReferences(metadata);
        if (missing.Count == 0)
            return;

        throw new InvalidOperationException("元数据引用校验失败:\n- " + string.Join("\n- ", missing));
    }

    private static void AddIfMissing(string? reference, string field, HashSet<string> registry, string owner, List<string> missing)
    {
        var value = reference?.Trim();
        if (string.IsNullOrEmpty(value))
            return;

        if (!registry.Contains(value))
            missing.Add($"{owner}: {field} -> {value}");
    }
}
