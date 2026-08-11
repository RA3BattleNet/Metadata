namespace Ra3.BattleNet.Metadata.Imaging;

/// <summary>
/// Imaging CLI：只处理图片，stdout 输出 MD5。不读/改 XML。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "--help" or "-h"))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            // convert --input=a.png --output=a.webp
            // convert a.png a.webp
            string? input = null;
            string? output = null;
            var i = 0;
            if (args[0] is "convert" or "webp")
                i = 1;

            for (; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--input=", StringComparison.Ordinal))
                    input = a["--input=".Length..];
                else if (a.StartsWith("--output=", StringComparison.Ordinal))
                    output = a["--output=".Length..];
                else if (input == null)
                    input = a;
                else if (output == null)
                    output = a;
            }

            if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
            {
                Console.Error.WriteLine("需要 --input 与 --output（或两个位置参数）");
                return 1;
            }

            var hash = ImageConverter.ConvertToWebP(input, output);
            // 机器可读：stdout 仅一行 hash，供展平管线解析
            Console.WriteLine(hash);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine("""
            Ra3.BattleNet.Metadata.Imaging — 图片 CLI（不读 XML）

            用法:
              convert --input=in.png --output=out.webp
              convert in.png out.webp

            成功: exit 0，stdout 一行 MD5（小写 hex）
            失败: exit != 0，错误在 stderr

            由核心 Build --webp 在展平阶段按图调用；不进 Desktop NuGet。
            """);
    }
}
