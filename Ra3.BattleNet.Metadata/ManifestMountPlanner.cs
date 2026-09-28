namespace Ra3.BattleNet.Metadata;

/// <summary>挂载计划里一条命令的种类。</summary>
public enum ManifestMountKind
{
    /// <summary>挂载清单里的一个文件（add-big）。</summary>
    Big,

    /// <summary>挂载模组目录下用户自己放的文件（add-config）。</summary>
    Config,
}

/// <summary>
/// 挂载计划里的一条命令。列表顺序就是 skudef 里行的顺序。
/// </summary>
/// <param name="Kind">命令种类。</param>
/// <param name="Target">Big 为清单里的 <c>FileName</c>；Config 为模组目录下的纯文件名。</param>
public sealed record ManifestMountCommand(ManifestMountKind Kind, string Target);

/// <summary>
/// 算挂载计划要用到的清单文件字段。
/// </summary>
/// <param name="FileName">清单里的安装名。</param>
/// <param name="Mount">旧格式的挂载角色（base/language/optional）；缺省 base。</param>
/// <param name="Language">语言包标记，仅 language 角色使用。</param>
/// <param name="Package">可选包标记，仅 optional 角色使用。</param>
public sealed record ManifestMountFile(string FileName, string? Mount = null, string? Language = null, string? Package = null);

/// <summary>
/// 按清单声明（<c>Skudef</c>）或旧挂载角色（<c>File@Mount</c>）算出有序挂载计划。
/// 写了 <c>Skudef</c> 就按声明顺序与条件产出，没写就按旧角色算法；规则的唯一实现在这里，消费方只管落盘。
/// </summary>
public static class ManifestMountPlanner
{
    private static readonly string[] MountRoles = ["base", "language", "optional"];

    /// <summary>
    /// 算出挂载计划。
    /// </summary>
    /// <param name="skudef">清单声明的 skudef；null 表示走旧角色算法。</param>
    /// <param name="files">清单文件表；旧算法下这个顺序就是挂载顺序。</param>
    /// <param name="language">客户端语言设置。</param>
    /// <param name="packages">客户端已开启的开关。</param>
    /// <param name="localConfigExists">判断模组目录下是否存在某个本地配置文件。</param>
    public static IReadOnlyList<ManifestMountCommand> Build(
        ManifestSkudefEntry? skudef,
        IReadOnlyList<ManifestMountFile> files,
        string language,
        IReadOnlyList<string> packages,
        Func<string, bool> localConfigExists)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(localConfigExists);

        return skudef is null
            ? LegacyPlan(files, language, packages)
            : DeclaredPlan(skudef, files, language, packages, localConfigExists);
    }

    /// <summary>声明算法：按 Skudef 里的指令顺序输出，条件命中才挂。</summary>
    private static IReadOnlyList<ManifestMountCommand> DeclaredPlan(
        ManifestSkudefEntry skudef,
        IReadOnlyList<ManifestMountFile> files,
        string language,
        IReadOnlyList<string> packages,
        Func<string, bool> localConfigExists)
    {
        var byName = new Dictionary<string, ManifestMountFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!byName.TryAdd(file.FileName, file))
                throw new InvalidOperationException($"清单里 FileName 重复：{file.FileName}");
        }

        var commands = new List<ManifestMountCommand>();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in skudef.Commands)
        {
            if (command.Kind == SkudefCommandKind.Config)
            {
                RequirePlainFileName(command.Target);
                if (localConfigExists(command.Target))
                    commands.Add(new ManifestMountCommand(ManifestMountKind.Config, command.Target));
                else if (!command.Optional)
                    throw new InvalidOperationException($"找不到本地配置：{command.Target}");

                continue;
            }

            if (!byName.ContainsKey(command.Target))
                throw new InvalidOperationException($"add-big 引用了清单里没有的文件：{command.Target}");

            if (!referenced.Add(command.Target))
                throw new InvalidOperationException($"add-big 重复引用：{command.Target}");

            var conditioned = command.Language is not null || command.Package is not null;
            var matched = (command.Language is not null
                    && string.Equals(command.Language, language, StringComparison.OrdinalIgnoreCase))
                || (command.Package is not null && PackageEnabled(command.Package, packages));
            if (conditioned && !matched)
                continue;

            commands.Add(new ManifestMountCommand(ManifestMountKind.Big, command.Target));
        }

        foreach (var file in files)
        {
            if (!referenced.Contains(file.FileName))
                throw new InvalidOperationException($"清单缺少 add-big：{file.FileName}");
        }

        return commands;
    }

    /// <summary>旧算法：按清单顺序挂 base，再挂客户端语言包，最后挂客户端开着开关的可选包。</summary>
    private static IReadOnlyList<ManifestMountCommand> LegacyPlan(
        IReadOnlyList<ManifestMountFile> files,
        string language,
        IReadOnlyList<string> packages)
    {
        var commands = new List<ManifestMountCommand>();
        foreach (var file in files)
        {
            var role = string.IsNullOrWhiteSpace(file.Mount) ? "base" : file.Mount.Trim().ToLowerInvariant();
            if (Array.IndexOf(MountRoles, role) < 0)
                throw new InvalidOperationException($"{file.FileName} 的挂载角色非法: {file.Mount}");

            if (role == "base"
                || (role == "language" && string.Equals(file.Language, language, StringComparison.OrdinalIgnoreCase))
                || (role == "optional" && PackageEnabled(file.Package, packages)))
            {
                commands.Add(new ManifestMountCommand(ManifestMountKind.Big, file.FileName));
            }
        }

        return commands;
    }

    /// <summary>add-config 只允许模组目录下的单个文件名：不带路径分隔符、空格、引号与控制字符。</summary>
    private static void RequirePlainFileName(string name)
    {
        if (name != Path.GetFileName(name)
            || name.Contains(' ')
            || name.Contains('"')
            || name.Any(char.IsControl))
        {
            throw new InvalidOperationException($"本地配置名非法：{name}");
        }
    }

    private static bool PackageEnabled(string? package, IReadOnlyList<string> packages) =>
        !string.IsNullOrEmpty(package)
        && packages.Any(name => string.Equals(name, package, StringComparison.OrdinalIgnoreCase));
}
