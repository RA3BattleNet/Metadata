using System.Text.Json;

namespace Ra3.BattleNet.Metadata;

internal static class Program
{
    private const string DefaultSrcFolder = "./Metadata";
    private const string DefaultDstFolder = "./Output";

    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "hash", StringComparison.Ordinal))
            return RunHash(args[1..]);

        var srcFolder = DefaultSrcFolder;
        var dstFolder = DefaultDstFolder;
        string? schemaVersion = null;
        string? contentRevision = null;
        var command = "build";
        var withWebp = false;

        foreach (var arg in args)
        {
            if (arg == "build")
                command = "build";
            else if (arg == "--webp")
                withWebp = true;
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
        Console.WriteLine($"WebP: {(withWebp ? "开（展平后按图调 Imaging CLI）" : "关")}");
        Console.WriteLine();

        try
        {
            if (command != "build")
            {
                Console.Error.WriteLine($"未知命令: {command}");
                return 1;
            }

            Console.WriteLine(">>> 构建（展平 / 校验" + (withWebp ? " / Imaging" : "") + "）");
            MetadataBuilder.Build(srcFolder, dstFolder, schemaVersion, contentRevision, convertImages: withWebp);
            Console.WriteLine("✓ 构建完成");

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

    /// <summary><c>hash &lt;file&gt;... [--json]</c>：按清单口径输出文件的 CRC-32C 与字节数，不写任何文件。</summary>
    private static int RunHash(string[] args)
    {
        var json = false;
        var files = new List<string>();
        foreach (var arg in args)
        {
            if (arg == "--json")
                json = true;
            else if (arg is "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"未知参数: {arg}");
                return 1;
            }
            else
            {
                files.Add(arg);
            }
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("用法: hash <file> [<file>...] [--json]");
            return 1;
        }

        try
        {
            var results = new List<(string Path, long Size, string Hash)>();
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                {
                    Console.Error.WriteLine($"错误: 文件不存在: {file}");
                    return 1;
                }

                results.Add((file, info.Length, Crc32C.ComputeFile(file).ToString("X8")));
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true,
                    command = "hash",
                    files = results.Select(r => new { path = r.Path, size = r.Size, crc32c = r.Hash }).ToArray(),
                }));
            }
            else
            {
                foreach (var result in results)
                    Console.WriteLine($"{result.Path}\t{result.Hash}\t{result.Size}");
            }

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
            Ra3.BattleNet.Metadata — 核心构建

            用法:
              build --src=./Metadata --dst=./Output
              build --webp --src=./Metadata --dst=./Output
              hash <file> [<file>...] [--json]

            --webp: 展平后对每张本地图调用 Imaging CLI（只产 webp），
                    由本程序改写 metadata.xml 的 Source。

            hash: 按清单口径算 CRC-32C（完整文件，8 位十六进制大写）并给出字节数；
                  文本输出一行一个文件：路径 <TAB> CRC32C <TAB> 字节数。
                  压缩分发时 Hash 取解压后的正式文件，Size 取压缩包大小。

            NuGet 主包不含 Imaging；Imaging 为仓库编译 CLI。
            """);
    }
}
