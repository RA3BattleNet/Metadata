namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 发布物的契约版本（<c>metadata.xml</c> 根上的 <c>SchemaVersion</c>）。
/// 客户端拿它判断远端数据能否被当前版本直接消费。
/// </summary>
public static class MetadataSchema
{
    /// <summary>当前契约版本，与构建期默认值一致。</summary>
    public const string Current = MetadataBuilder.DefaultSchemaVersion;

    /// <summary>
    /// 判断发布物的 <c>SchemaVersion</c> 是否能被本库直接消费。
    /// </summary>
    /// <param name="version">发布物根上的 SchemaVersion；缺失时为 null。</param>
    public static bool IsCompatible(string? version)
    {
        return string.Equals(version, Current, StringComparison.Ordinal);
    }
}
