namespace Ra3.BattleNet.Metadata;

internal static class Program
{
    private const string DefaultSrcFolder = "./Metadata";
    private const string DefaultDstFolder = "./Output";

    private static int Main(string[] args)
    {
        var srcFolder = DefaultSrcFolder;
        var dstFolder = DefaultDstFolder;
        string? schemaVersion = null;
        string? contentRevision = null;
        var command = "build";

        foreach (var arg in args)
        {
            if (arg is "build" or "stage-a")
                command = "build";
            else if (arg.StartsWith("--src=", StringComparison.Ordinal))
                srcFolder = arg["--src=".Length..];
            else if (arg.StartsWith("--dst=", StringComparison.Ordinal))
                dstFolder = arg["--dst=".Length..];
            else if (arg.StartsWith("--schema-version=", StringComparison.Ordinal))
                schemaVersion = arg["--schema-version=".Length..];
            else if (arg.StartsWith("--content-revision=", StringComparison.Ordinal))
                contentRevision = arg["--content-revision=".Length..];
            else if (arg is "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }
        }

        Console.WriteLine($"工作目录: {Environment.CurrentDirectory}");
        Console.WriteLine($"源目录: {srcFolder}");
        Console.WriteLine($"输出目录: {dstFolder}");
        Console.WriteLine();

        try
        {
            if (command != "build")
            {
                Console.Error.WriteLine($"未知命令: {command}");
                return 1;
            }

            Console.WriteLine("执行核心构建（校验 / 变量 / XML 展平 / 复制资源）...");
            MetadataBuilder.Build(srcFolder, dstFolder, schemaVersion, contentRevision);
            Console.WriteLine("✓ 核心构建完成");

            var flatPath = Path.Combine(dstFolder, "metadata.xml");
            var loaded = MetadataBuilder.Load(flatPath);
            Console.WriteLine();
            Console.WriteLine("=== 展平结果 ===");
            Console.WriteLine($"SchemaVersion={loaded.Get("SchemaVersion")}");
            Console.WriteLine($"ContentRevision={loaded.Get("ContentRevision")}");
            foreach (var app in loaded.Applications())
                Console.WriteLine($"Application id={app.Id} version={app.Version}");
            foreach (var mod in loaded.Mods())
                Console.WriteLine($"Mod id={mod.Id} version={mod.Version}");

            Console.WriteLine();
            Console.WriteLine("处理完成。WebP 请显式运行 Imaging（npm run build:webp 或 bash build.sh --webp）。");
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
            Ra3.BattleNet.Metadata — 核心构建（纯 managed）

            用法:
              dotnet run --project Ra3.BattleNet.Metadata -- build --src=./Metadata --dst=./Output

            参数:
              --src=DIR                 源目录（含 metadata.xml）
              --dst=DIR                 输出目录
              --schema-version=VER      默认 1.0
              --content-revision=REV    默认 UTC 时间戳

            说明:
              build: 展平 XML、变量替换、硬失败校验、复制资源
              WebP:  独立项目 Ra3.BattleNet.Metadata.Imaging（npm run build:webp / build.sh --webp）
              Load(path|url): 见 MetadataBuilder.Load
              Build(url): 本期仅支持本地 path，远程源构建延后
            """);
    }
}
