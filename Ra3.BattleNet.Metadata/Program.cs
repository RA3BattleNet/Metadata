namespace Ra3.BattleNet.Metadata;

internal static class Program
{
    private static int Main(string[] args)
    {
        var source = "./Metadata";
        var output = "./Output";
        var command = "build";
        var convertImages = false;
        var layout = MetadataExportLayout.Flat;
        var inlineManifests = false;
        var resolveVariables = false;
        var inlinePaths = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var argument in args)
            {
                if (argument is "build" or "export")
                    command = argument;
                else if (argument == "--webp")
                    convertImages = true;
                else if (argument.StartsWith("--src=", StringComparison.Ordinal))
                    source = argument["--src=".Length..];
                else if (argument.StartsWith("--dst=", StringComparison.Ordinal))
                    output = argument["--dst=".Length..];
                else if (argument.StartsWith("--layout=", StringComparison.Ordinal))
                    layout = Enum.Parse<MetadataExportLayout>(argument["--layout=".Length..], ignoreCase: true);
                else if (argument.StartsWith("--inline=", StringComparison.Ordinal))
                    inlinePaths.Add(argument["--inline=".Length..].Replace('\\', '/'));
                else if (argument == "--inline-manifests")
                    inlineManifests = true;
                else if (argument == "--resolve-variables")
                    resolveVariables = true;
                else if (argument is "--help" or "-h")
                {
                    PrintHelp();
                    return 0;
                }
                else
                    throw new ArgumentException($"未知参数: {argument}");
            }

            Console.WriteLine($"源: {source}");
            Console.WriteLine($"输出目录: {output}");
            if (command == "export")
            {
                var entry = Directory.Exists(source) ? Path.Combine(source, "metadata.xml") : source;
                MetadataExporter.Export(MetadataReader.Read(entry), output, new MetadataExportOptions
                {
                    Layout = layout,
                    Inline = layout == MetadataExportLayout.Mixed ? inlinePaths.Contains : null,
                    InlineManifests = inlineManifests,
                    ConvertImages = convertImages,
                    ResolveVariables = resolveVariables
                });
                Console.WriteLine("Export complete.");
                return 0;
            }

            MetadataBuilder.Build(source, output, convertImages);
            var root = MetadataReader.Read(Path.Combine(output, "metadata.xml")).Root;
            Console.WriteLine($"SchemaVersion={root.Get("SchemaVersion")}");
            foreach (var app in root.Applications())
                Console.WriteLine($"Application id={app.Id} version={app.Version}");
            foreach (var mod in root.Mods())
                Console.WriteLine($"Mod id={mod.Id} version={mod.Version}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"错误: {error.Message}");
            return 1;
        }
    }

    private static void PrintHelp() => Console.WriteLine("""
        Ra3.BattleNet.Metadata — 构建与导出

        用法:
          build --src=./Metadata --dst=./Output
          build --webp --src=./Metadata --dst=./Output
          export --src=./Metadata/metadata.xml --dst=./Export --layout=tree
          export --src=./Metadata --dst=./Export --layout=mixed --inline=mods/demo.xml --inline-manifests

        Build: 入口与契约版本都来自源树，没有额外参数（SchemaVersion 写在源根 metadata.xml 上）。
        Export: --layout=flat|tree|mixed,
                --inline=<source-relative-path> (repeatable for mixed),
                --inline-manifests, --resolve-variables.
        --webp: 调用仓库 Imaging CLI 转换本地图并改写 Source。
        NuGet 主包不含 Imaging。
        """);
}
