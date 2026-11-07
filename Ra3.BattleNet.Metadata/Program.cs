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
        var withWebp = false;
        var layout = MetadataExportLayout.Flat;
        var idFormat = MetadataExportIdFormat.Legacy;
        var inlineManifests = false;
        var resolveVariables = false;
        var inlinePaths = new HashSet<string>(StringComparer.Ordinal);

        try
        {
        foreach (var arg in args)
        {
            if (arg is "build" or "export")
                command = arg;
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
            else if (arg.StartsWith("--layout=", StringComparison.Ordinal))
                layout = Enum.Parse<MetadataExportLayout>(arg["--layout=".Length..], ignoreCase: true);
            else if (arg.StartsWith("--id-format=", StringComparison.Ordinal))
                idFormat = Enum.Parse<MetadataExportIdFormat>(arg["--id-format=".Length..], ignoreCase: true);
            else if (arg.StartsWith("--inline=", StringComparison.Ordinal))
                inlinePaths.Add(arg["--inline=".Length..].Replace('\\', '/'));
            else if (arg == "--inline-manifests")
                inlineManifests = true;
            else if (arg == "--resolve-variables")
                resolveVariables = true;
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

            if (command == "export")
            {
                var entry = Directory.Exists(srcFolder) ? Path.Combine(srcFolder, "metadata.xml") : srcFolder;
                MetadataExporter.Export(MetadataReader.Read(entry), dstFolder, new MetadataExportOptions
                {
                    Layout = layout,
                    IdFormat = idFormat,
                    Inline = layout == MetadataExportLayout.Mixed ? inlinePaths.Contains : null,
                    InlineManifests = inlineManifests,
                    ConvertImages = withWebp,
                    ResolveVariables = resolveVariables
                });
                Console.WriteLine("Export complete.");
                return 0;
            }

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

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Ra3.BattleNet.Metadata — 核心构建

            用法:
              build --src=./Metadata --dst=./Output
              build --webp --src=./Metadata --dst=./Output
              export --src=./Metadata/metadata.xml --dst=./Export --layout=tree --id-format=path
              export --src=./Metadata --dst=./Export --layout=mixed --inline=mods/demo.xml --inline-manifests

            Export only: --layout=flat|tree|mixed, --id-format=legacy|path,
                         --inline=<source-relative-path> (repeatable for mixed),
                         --inline-manifests, --resolve-variables, --webp.

            --webp: 展平后对每张本地图调用 Imaging CLI（只产 webp），
                    由本程序改写 metadata.xml 的 Source。

            NuGet 主包不含 Imaging；Imaging 为仓库编译 CLI。
            """);
    }
}
