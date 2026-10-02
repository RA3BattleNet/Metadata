using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 缓存内小状态文件的公共读写：禁 DTD/外部实体、限体积、UTF-8（无 BOM）、落盘后 flush 到磁盘。
/// 内容寻址的大对象不走这里（它们是原文字节，不做 XML 包装）。
/// </summary>
internal static class CacheFile
{
    /// <summary>指针、来源绑定、请求映射这类小状态文件的体积上限。</summary>
    public const long MaxStateFileBytes = 4L * 1024 * 1024;

    /// <summary>把 XML 文档序列化成不带 BOM 的 UTF-8 字节。</summary>
    public static byte[] Serialize(XDocument doc)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            OmitXmlDeclaration = false,
        };

        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings))
            doc.Save(writer);
        return buffer.ToArray();
    }

    /// <summary>
    /// 读取一个小状态 XML：先按文件长度挡体积，再禁 DTD 与外部实体。
    /// 任何失败都抛异常，由调用方翻译成 <see cref="CacheError"/>。
    /// </summary>
    public static XDocument LoadSmallXml(string path, long maxBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("状态文件不存在", path);
        if (info.Length > maxBytes) throw new InvalidDataException($"状态文件超过 {maxBytes} 字节上限: {path}");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            MaxCharactersInDocument = maxBytes,
        };

        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader);
    }

    /// <summary>
    /// 同目录临时文件 + flush + 改名。调用方必须自己保证"改名到活动文件"这一步的语义
    /// （见 <see cref="CachePointerFile"/> 的 pending/current/previous 轮换）。
    /// </summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
