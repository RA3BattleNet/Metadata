using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 展平后：扫描 Image 登记节点，调用 Imaging CLI 转 WebP，由本类改写 XML 的 Source/Hash。
/// </summary>
public static class ImagePostProcessor
{
    private static readonly HashSet<string> Convertible = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif"
    };

    /// <summary>
    /// 处理 outputDir 下 metadata.xml；返回转换张数。
    /// </summary>
    public static int ApplyWebP(string outputDir, TextWriter? log = null)
    {
        log ??= Console.Out;
        outputDir = Path.GetFullPath(outputDir);
        var flatPath = Path.Combine(outputDir, "metadata.xml");
        if (!File.Exists(flatPath))
            throw new FileNotFoundException($"找不到展平文件: {flatPath}");

        var doc = XDocument.Load(flatPath);
        var converted = 0;

        foreach (var image in doc.Descendants("Image"))
        {
            if (image.Attribute("ID") == null)
                continue;

            var sourceAttr = image.Attribute("Source");
            if (sourceAttr == null || string.IsNullOrWhiteSpace(sourceAttr.Value))
                continue;
            if (!string.IsNullOrWhiteSpace(image.Attribute("Url")?.Value))
                continue;

            var rel = sourceAttr.Value.Replace('\\', '/');
            var abs = Path.Combine(outputDir, rel);
            if (!File.Exists(abs))
                continue;

            var ext = Path.GetExtension(abs);
            if (!Convertible.Contains(ext))
                continue;

            var webpAbs = Path.ChangeExtension(abs, ".webp")!;
            var webpRel = Path.ChangeExtension(rel, ".webp")!.Replace('\\', '/');

            log.WriteLine($"  convert {rel} -> {webpRel}");
            var hash = ImagingInvoker.ConvertToWebP(abs, webpAbs);
            sourceAttr.Value = webpRel;
            image.SetAttributeValue("Hash", hash);
            converted++;
        }

        doc.Save(flatPath);
        return converted;
    }
}
