using System.Text.Json.Serialization;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 缓存旁挂 JSON（<c>origin.json</c> 与 <c>.etag</c>）的源生成上下文。选项与原来的运行时反射选项等价：
/// 属性名大小写不敏感、不缩进、默认属性命名与默认转义，null 属性照常写出。
/// 源生成在 NativeAOT 下不触发反射序列化，写出的字节与旧实现逐字节一致。
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(CacheOrigin))]
[JsonSerializable(typeof(CacheStamp))]
internal sealed partial class MetadataCacheJsonContext : JsonSerializerContext
{
}
