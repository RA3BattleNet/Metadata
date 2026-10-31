using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 解析本地作者声明里的版本清单：登记节点既可能是内联清单，也可能只是指向叶子的 stub。
///
/// <see cref="MetadataBuilder.LoadSource"/> 把 Include 进来的独立清单留成登记 stub（ID 已限定、
/// <c>Source</c> 相对源根）；手写 <c>&lt;Manifest ID Source&gt;</c> 的 stub 带入口文件前缀，
/// 单独加载叶子时前缀又来自叶子自身路径。两处前缀可以不同，因此按
/// <see cref="MetadataFlattener.LocalId"/> 对齐局部名后再取节点。
/// </summary>
public static class MetadataSourceManifest
{
    /// <summary>
    /// 取版本包声明的清单节点。登记节点已内联清单（带 <c>Skudef</c> 或 <c>File</c> 子节点）时直接返回；
    /// 只有 stub 时按 <c>Source</c> 在声明目录内读取叶子，并按局部名对齐清单 ID。
    /// </summary>
    /// <param name="root">已展平的本地作者声明根。</param>
    /// <param name="declarationPath">声明文件路径；叶子清单与相对 <c>Source</c> 都相对它所在的目录。</param>
    /// <param name="package">版本包节点（声明里的 <c>Package</c>）。</param>
    /// <returns>清单节点；版本包没有声明 <c>Manifest</c> 时返回 null。</returns>
    public static Metadata? Resolve(Metadata root, string declarationPath, Metadata package)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(declarationPath);
        ArgumentNullException.ThrowIfNull(package);

        var id = package.Find("Manifest")?.Value;
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var registration = root.ManifestRegistration(id)
            ?? throw new InvalidOperationException($"模组声明找不到文件清单：{id}");
        if (registration.Find("Skudef") is not null || registration.Children.Any(child => child.Name == "File"))
            return registration;

        var source = registration.Get("Source")
            ?? throw new InvalidOperationException($"模组文件清单缺少 Source：{id}");
        var folder = Path.GetDirectoryName(Path.GetFullPath(declarationPath))!;
        var leaf = MetadataResourceUri.Resolve(declarationPath, source);
        if (!leaf.IsFile)
            throw new InvalidOperationException("本地模组文件清单必须在模组目录内");

        var document = MetadataBuilder.LoadSource(leaf.LocalPath, folder);
        var localId = MetadataFlattener.LocalId(id);
        return document.GetAllElements("Manifest").SingleOrDefault(manifest =>
                string.Equals(MetadataFlattener.LocalId(manifest.Get("ID")), localId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"文件清单 ID 与模组声明不一致：{id}");
    }
}
