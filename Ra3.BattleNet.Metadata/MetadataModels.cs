namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 通用元数据树节点（适合反序列化后继续加工）。
/// </summary>
/// <param name="Name">节点名（即 XML 标签名）。</param>
/// <param name="Value">叶子节点文本值；若包含子节点则通常为 <c>null</c> 或空字符串。</param>
/// <param name="Attributes">节点属性字典（如 <c>ID</c>、<c>Source</c>）。</param>
/// <param name="Children">子节点集合。</param>
public sealed record MetadataNode(
    string Name,
    string? Value,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<MetadataNode> Children);

/// <summary>
/// 业务实体快照，便于直接查询核心字段。
/// </summary>
public sealed class BusinessEntity
{
    /// <summary>
    /// 实体类型（如 <c>Application</c>、<c>Mod</c>、<c>Markdown</c>）。
    /// </summary>
    public string EntityType { get; init; } = string.Empty;

    /// <summary>
    /// 实体 ID（来自节点 <c>ID</c> 属性）。
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// 实体在元数据树中的路径（<c>A -&gt; B -&gt; C</c>）。
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// 原始属性字典。
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// 归一化后的常用属性（如 <c>PackageCount</c>、<c>FileCount</c>）。
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}
