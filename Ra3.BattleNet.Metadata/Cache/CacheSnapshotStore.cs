using System.Xml;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 快照的落盘与解析。
///
/// 快照保存的是**服务器原始字节**（不为了本地路径而重写 XML），所以它的身份就是原文字节摘要；
/// 这个摘要同时是快照目录名，读取方因此可以随时核对文件有没有被换掉。
/// </summary>
internal static class CacheSnapshotStore
{
    /// <summary>把一个已验证的根原文写成快照（幂等：同字节已存在就不重写）；返回快照 ID。</summary>
    public static string SaveRoot(CacheLayout layout, byte[] rootBytes)
    {
        var snapshotId = CacheHash.Sha256Hex(rootBytes);
        var target = layout.SnapshotMetadataPath(snapshotId);
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(layout.SnapshotRoot(snapshotId));
            CacheFile.WriteAtomic(target, rootBytes);
        }
        return snapshotId;
    }

    /// <summary>读出快照目录里的根原文。</summary>
    public static byte[] ReadRoot(CacheLayout layout, string snapshotId) =>
        File.ReadAllBytes(layout.SnapshotMetadataPath(snapshotId));

    /// <summary>
    /// 解析根原文。拒绝 DTD/外部实体、超过体积上限的文档，以及不是 <c>&lt;Metadata&gt;</c> 的根；
    /// 展平产物以外的东西（含 Include/Module 的源树文件）由 <see cref="CacheParseException"/> 拒绝。
    /// </summary>
    public static Metadata ParseRoot(byte[] rootBytes, long maxBytes)
    {
        if (rootBytes.LongLength > maxBytes)
            throw new CacheParseException(CacheErrorCodes.TooLarge, $"根 XML 超过 {maxBytes} 字节上限");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            MaxCharactersInDocument = maxBytes,
            MaxCharactersFromEntities = 0,
        };

        XDocument doc;
        try
        {
            using var stream = new MemoryStream(rootBytes, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new CacheParseException(CacheErrorCodes.InvalidXml, $"根 XML 无法解析：{ex.Message}");
        }

        if (doc.Root is null || doc.Root.Name.LocalName != "Metadata")
            throw new CacheParseException(CacheErrorCodes.InvalidXml, "根节点不是 Metadata");

        try
        {
            var metadata = new Metadata();
            metadata.ParseElement(doc.Root);
            return metadata;
        }
        catch (InvalidOperationException ex)
        {
            throw new CacheParseException(CacheErrorCodes.InvalidXml, ex.Message);
        }
    }
}

/// <summary>根原文验证失败。调用方据此把快照判为不可用，而不是当成半成品继续用。</summary>
internal sealed class CacheParseException : Exception
{
    public CacheParseException(string code, string message) : base(message) => Code = code;

    /// <summary>见 <see cref="CacheErrorCodes"/>。</summary>
    public string Code { get; }
}
