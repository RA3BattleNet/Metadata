namespace Ra3.BattleNet.Metadata;

public enum MetadataExportLayout
{
    Flat,
    Tree,
    Mixed
}

public sealed class MetadataExportOptions
{
    public MetadataExportLayout Layout { get; init; } = MetadataExportLayout.Flat;
    public Func<string, bool>? Inline { get; init; }
    public bool InlineManifests { get; init; }
    public bool ConvertImages { get; init; }
    public bool ResolveVariables { get; init; }
}
