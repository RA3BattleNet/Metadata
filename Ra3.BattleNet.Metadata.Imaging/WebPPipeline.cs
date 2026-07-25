using System.Security.Cryptography;
using System.Xml.Linq;
using SkiaSharp;

namespace Ra3.BattleNet.Metadata.Imaging;

/// <summary>
/// 对已展平 Output 做 WebP 转码：改写 Image/@Source，并按新文件重算 Image/@Hash。
/// </summary>
public static class WebPPipeline
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif"
    };

    /// <summary>
    /// 处理 outputDir 中的 metadata.xml 与本地图片。
    /// </summary>
    /// <returns>成功转换的图片数量。</returns>
    public static int Process(string outputDir)
    {
        outputDir = Path.GetFullPath(outputDir);
        var flat = Path.Combine(outputDir, "metadata.xml");
        if (!Directory.Exists(outputDir))
            throw new DirectoryNotFoundException($"输出目录不存在: {outputDir}");
        if (!File.Exists(flat))
            throw new FileNotFoundException($"找不到展平文件: {flat}");

        var doc = XDocument.Load(flat);
        var converted = 0;
        foreach (var image in doc.Descendants("Image"))
        {
            // 仅登记节点（带 ID）；Background 文本引用无 Source 属性
            if (image.Attribute("ID") == null)
                continue;

            var sourceAttr = image.Attribute("Source");
            if (sourceAttr == null || string.IsNullOrWhiteSpace(sourceAttr.Value))
                continue;
            if (!string.IsNullOrWhiteSpace(image.Attribute("Url")?.Value))
                continue;

            var sourcePath = Path.Combine(outputDir, sourceAttr.Value.Replace('\\', '/'));
            if (!File.Exists(sourcePath))
                continue;

            var ext = Path.GetExtension(sourcePath);
            if (!Supported.Contains(ext))
            {
                // 已是 webp 等：仍刷新 Hash
                if (ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
                    image.SetAttributeValue("Hash", ComputeMd5(sourcePath));
                continue;
            }

            var webpPath = Path.ChangeExtension(sourcePath, ".webp")!;
            if (!ConvertToWebP(sourcePath, webpPath))
                continue;

            var newRel = Path.ChangeExtension(sourceAttr.Value, ".webp")!.Replace('\\', '/');
            sourceAttr.Value = newRel;
            image.SetAttributeValue("Hash", ComputeMd5(webpPath));
            converted++;
        }

        doc.Save(flat);
        return converted;
    }

    public static string ComputeMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool ConvertToWebP(string sourcePath, string webpPath)
    {
        try
        {
            using var input = File.OpenRead(sourcePath);
            using var codec = SKCodec.Create(input);
            if (codec == null) return false;
            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap == null) return false;
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Webp, 90);
            using var output = File.Open(webpPath, FileMode.Create, FileAccess.Write);
            data.SaveTo(output);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  转换失败 {sourcePath}: {ex.Message}");
            return false;
        }
    }
}
