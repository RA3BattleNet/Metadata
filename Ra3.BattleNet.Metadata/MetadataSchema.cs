namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 数据契约版本（入口 <c>metadata.xml</c> 根上的 <c>SchemaVersion</c>，源树与发布物都写在这里）。
/// 客户端拿它判断远端数据能否被当前版本直接消费。
/// </summary>
public static class MetadataSchema
{
    /// <summary>当前契约版本，与构建期默认值一致。</summary>
    public const string Current = MetadataBuilder.DefaultSchemaVersion;

    /// <summary>
    /// 判断根清单的 <c>SchemaVersion</c> 是否能被本库直接消费。
    /// </summary>
    /// <param name="version">入口根节点上的 SchemaVersion；缺失时为 null。</param>
    public static bool IsCompatible(string? version)
    {
        return string.Equals(version, Current, StringComparison.Ordinal);
    }
}
