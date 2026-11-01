namespace Ra3.BattleNet.Metadata;

/// <summary>
/// <see cref="MetadataBuilder.Open"/> 的读取选项。全部为 init 属性，默认值即最短路径：
/// 本地文档有 <c>Include</c> 才当源树展平，不注入自定义源，不做引用校验。
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

    /// <summary>入口没有 <c>Include</c> 时也按源树展平并限定短 ID（本地、HTTP 与自定义源一律生效）；发布物读取不要打开。</summary>
    public bool ForceSourceTree { get; init; }

    /// <summary>
    /// 自定义只读源；非空时入口路径交给该源解释，不再当成本地路径或 URL。
    /// 未注入时本地路径用 <see cref="PhysicalFileXmlSource"/>，HTTP(S) 入口用 <see cref="HttpXmlSource"/>。
    /// </summary>
    public IMetadataXmlSource? CustomSource { get; init; }
}
