using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 展平过程的只读 XML 源。路径一律是不透明字符串：本地实现解释为绝对文件路径，
/// 以后的 URL 实现解释为绝对 URL；展平器只通过本接口做存在性检查、读取与路径运算，
/// 不直接碰 System.IO。
/// </summary>
public interface IMetadataXmlSource
{
    /// <summary>目标是否存在。</summary>
    bool Exists(string path);

    /// <summary>读取并解析 XML 文档。</summary>
    XDocument LoadDocument(string path);

    /// <summary>拼接目录与相对路径。</summary>
    string Combine(string basePath, string relativePath);

    /// <summary>取所在目录；无法确定时返回 null。</summary>
    string? GetDirectoryName(string path);

    /// <summary>
    /// 把 <paramref name="toPath"/> 表示为相对 <paramref name="fromPath"/> 的路径；
    /// 两者不同根（跨盘、跨 URL 主机）时原样返回 <paramref name="toPath"/> 的绝对形式。
    /// </summary>
    string GetRelativePath(string fromPath, string toPath);

    /// <summary>规范化为绝对形式（本地实现是绝对路径，URL 实现是绝对 URL）。</summary>
    string GetFullPath(string path);

    /// <summary>是否是根路径（本地绝对路径、绝对 URL）。</summary>
    bool IsPathRooted(string path);
}
