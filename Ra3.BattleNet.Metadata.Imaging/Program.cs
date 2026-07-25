namespace Ra3.BattleNet.Metadata.Imaging;

/// <summary>
/// Imaging CLI：编译/发布阶段可选调用，不进入 Desktop 主 NuGet。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h"))
        {
            PrintHelp();
            return 0;
        }

        // 兼容: webp --dst=...  或  --dst=...（默认 webp）
        var command = "webp";
        var dst = "./Output";
        foreach (var arg in args)
        {
            if (arg is "webp" or "images")
                command = "webp";
            else if (arg.StartsWith("--dst=", StringComparison.Ordinal))
                dst = arg["--dst=".Length..];
        }

        try
        {
            if (command != "webp")
            {
                Console.Error.WriteLine($"未知命令: {command}");
                return 1;
            }

            Console.WriteLine($"Imaging webp → {Path.GetFullPath(dst)}");
            var n = WebPPipeline.Process(dst);
            Console.WriteLine($"Imaging 完成，转换 {n} 张图片（已更新 Source 与 Hash）");
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"错误: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Ra3.BattleNet.Metadata.Imaging — 发布期图片 CLI（非 Desktop NuGet）

            用法:
              dotnet run --project Ra3.BattleNet.Metadata.Imaging -- webp --dst=./Output

            行为:
              将 Output 中 Image 本地图转为 WebP，改写 Source，并重算 Hash(MD5)

            由核心 CLI 在 --webp 时自动调用，也可单独执行。
            """);
    }
}
