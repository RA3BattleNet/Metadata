namespace Ra3.BattleNet.Metadata;

/// <summary>
/// Options for the unified reader. Reading never performs build-time substitutions.
/// </summary>
public sealed class MetadataSourceOptions
{
    /// <summary>
    /// <c>Include</c> 与相对资源路径的允许根目录；缺省时用入口文件所在目录。
    /// 显式指定后入口、<c>Include</c> 与资源路径都必须落在该目录内。
    /// </summary>
    public string? SourceRoot { get; init; }

    /// <summary>读取完成后是否做登记引用完整性校验：断引用抛 <see cref="InvalidOperationException"/>。</summary>
    public bool ValidateReferences { get; init; }

    /// <summary>Qualify unmarked documents even without composition constructs.</summary>
    public bool ForceSourceTree { get; init; }

    /// <summary>
    /// 自定义只读源；非空时入口路径交给该源解释，不再当成本地路径或 URL。
    /// 未注入时本地路径用 <see cref="PhysicalFileXmlSource"/>，HTTP(S) 入口用 <see cref="HttpXmlSource"/>。
    /// </summary>
    public IMetadataXmlSource? CustomSource { get; init; }

    /// <summary>Resolve external manifest bodies and retain included manifest bodies.</summary>
    public bool InlineManifests { get; init; }

    internal bool LegacySourcePaths { get; init; }
}
