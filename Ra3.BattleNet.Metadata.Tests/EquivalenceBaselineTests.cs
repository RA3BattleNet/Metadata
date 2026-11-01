using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ra3.BattleNet.Metadata.Tests;

/// <summary>
/// 语义等价基准：「源树读取」（<see cref="MetadataBuilder.LoadSource"/>）与
/// 「发布物读取」（<see cref="MetadataBuilder.Build"/> 之后 <see cref="MetadataBuilder.Load"/>）
/// 读同一份仓库数据，必须得到同一棵语义树。
/// 两棵树各自规范化成可比较文本后逐字比较；根 ContentRevision 与根 Tags/Commit 这两个构建期字段
/// 不逐字比较，但单独断言（源树模式不求值构建变量）。
/// </summary>
[TestClass]
public class EquivalenceBaselineTests
{
    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string RepoMetadataDir => Path.Combine(RepoRoot, "Metadata");

    private const string BuildContentRevision = "equiv-test";

    /// <summary>源树读到的构建期变量字面量；发布物里已被替换成构建时刻。</summary>
    private const string TimestampPlaceholder = "${TIMESTAMP}";

    /// <summary>规范化时把根 Tags/Commit 的构建期文本折叠成这个标记（两边的差异已单独断言）。</summary>
    private const string BuildStampToken = "<build-stamp>";

    private static readonly Regex BuildTimestampPattern =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", RegexOptions.Compiled);

    private static readonly string[] ReferenceElementNames = ["Icon", "Logo", "Content", "HeadImage"];

    [TestMethod]
    public void SourceTreeAndPublishedTree_AreSemanticallyEquivalent()
    {
        var src = RepoMetadataDir;
        var tempOut = Path.Combine(Path.GetTempPath(), $"equiv-{Guid.NewGuid():N}");

        try
        {
            MetadataBuilder.Build(src, tempOut, contentRevision: BuildContentRevision);

            var fromSource = MetadataBuilder.LoadSource(Path.Combine(src, "metadata.xml"), src);
            var fromPublish = MetadataBuilder.Load(Path.Combine(tempOut, "metadata.xml"));

            // 根 ContentRevision 与根 Tags/Commit 是构建期字段，不参与整棵树逐字比较；
            // 但它们恰恰是两条路径的已知差异点，单独断言，不静默跳过。
            fromSource.Get("ContentRevision").Should().Be("local-source", "源树没有发布修订，LoadSource 标 local-source");
            fromPublish.Get("ContentRevision").Should().Be(BuildContentRevision, "Build 把传入的 contentRevision 写到根上");

            // 折叠点精确到根下的 Tags/Commit 一个节点：源树读到的就是 ${TIMESTAMP} 字面量，
            // 发布物里被 VariableResolver 替换成构建时刻。别处任何 ${...} 都算数据问题。
            var sourceBuildStamp = fromSource.Find("Tags:Commit");
            var publishBuildStamp = fromPublish.Find("Tags:Commit");
            var sourceStampValue = sourceBuildStamp?.Value?.Trim();
            var publishStampValue = publishBuildStamp?.Value?.Trim();
            sourceStampValue.Should().Be(TimestampPlaceholder,
                "源树模式不求值构建变量，根 Tags/Commit 必须是 ${TIMESTAMP} 字面量");
            publishStampValue.Should().MatchRegex(BuildTimestampPattern,
                "发布物里 ${TIMESTAMP} 已替换成构建时刻");
            PlaceholdersOutsideBuildStamp(fromSource, sourceBuildStamp).Should().BeEmpty(
                "源树里除根 Tags/Commit 的 ${TIMESTAMP} 外不应再有构建期变量占位符");

            var sourceText = Normalize(fromSource, sourceBuildStamp);
            var publishText = Normalize(fromPublish, publishBuildStamp);

            // 规模指标只用「两边一致 + 非空」断言，具体数字打进输出：数据一变，固定数字就会炸。
            var sourceNodes = CountNodes(fromSource);
            var publishNodes = CountNodes(fromPublish);
            var sourceRegistryIds = RegistryIds(fromSource);
            var publishRegistryIds = RegistryIds(fromPublish);
            var sourceEntityIds = EntityIds(fromSource);
            var publishEntityIds = EntityIds(fromPublish);
            var sourceReferences = CountReferences(fromSource);
            var publishReferences = CountReferences(fromPublish);

            Console.WriteLine($"nodes: source={sourceNodes}, publish={publishNodes}");
            Console.WriteLine($"registry ids: source={sourceRegistryIds.Length}, publish={publishRegistryIds.Length}");
            Console.WriteLine($"entity ids: source={sourceEntityIds.Length}, publish={publishEntityIds.Length}");
            Console.WriteLine($"references: source={sourceReferences}, publish={publishReferences}");
            Console.WriteLine($"normalized lines: source={LineCount(sourceText)}, publish={LineCount(publishText)}");

            // 1) 规范化文本逐字相等（唯一豁免：根 ContentRevision 与 Tags/Commit 这两个构建期字段）。
            publishText.Should().Be(
                sourceText,
                "发布物与源树必须表达同一棵语义树，只有根 ContentRevision 与构建期时间戳允许不同。{0}",
                DiffHint(sourceText, publishText));

            // 2) 登记 ID 集合（带 ID 的 Image/Markdown/Manifest）：两边一致且非空。
            publishRegistryIds.Should().Equal(sourceRegistryIds);
            sourceRegistryIds.Should().NotBeEmpty("真实仓库数据里必须有资源登记");

            // 3) 实体 ID 集合（Mod/Application）：两边一致且非空。
            publishEntityIds.Should().Equal(sourceEntityIds);
            sourceEntityIds.Should().NotBeEmpty("真实仓库数据里必须有实体");

            // 4) 引用节点数量（Icon/Logo/Content/HeadImage 与 Package 下的 Manifest 文本）：两边一致且非空。
            publishReferences.Should().Be(sourceReferences);
            sourceReferences.Should().BeGreaterThan(0, "真实仓库数据里必须有资源引用");

            // 5) 节点规模两边一致且非空（不写死具体数字）。
            publishNodes.Should().Be(sourceNodes);
            sourceNodes.Should().BeGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(tempOut))
                Directory.Delete(tempOut, recursive: true);
        }
    }

    /// <summary>
    /// 把一棵 Metadata 树递归 dump 成稳定文本：元素名 + 属性（按名 Ordinal 排序）+ 叶子文本 Trim，
    /// 每层固定两空格缩进。根节点上的 ContentRevision 是构建时间，跳过；
    /// <paramref name="buildStamp"/> 指向的构建期文本折叠成固定标记（两边取值已在调用处单独断言）。
    /// </summary>
    private static string Normalize(Metadata tree, Metadata? buildStamp)
    {
        var builder = new StringBuilder();
        Dump(tree, 0, builder, isRoot: true, buildStamp);
        return builder.ToString();
    }

    private static void Dump(Metadata node, int depth, StringBuilder builder, bool isRoot, Metadata? buildStamp)
    {
        builder.Append(' ', depth * 2).Append(node.Name);

        foreach (var variable in node.Variables
                     .Where(v => !(isRoot && string.Equals(v.Key, "ContentRevision", StringComparison.Ordinal)))
                     .OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            builder.Append(' ').Append(variable.Key).Append("=\"").Append(variable.Value).Append('"');
        }

        var text = ReferenceEquals(node, buildStamp) ? BuildStampToken : node.Value?.Trim();
        if (!string.IsNullOrEmpty(text))
            builder.Append(" = ").Append(text);

        builder.Append('\n');

        foreach (var child in node.Children)
            Dump(child, depth + 1, builder, isRoot: false, buildStamp);
    }

    private static int CountNodes(Metadata node) => 1 + node.Children.Sum(CountNodes);

    /// <summary>登记 ID：带 ID 属性的 Image/Markdown/Manifest 节点，按 OrdinalIgnoreCase 排序。</summary>
    private static string[] RegistryIds(Metadata tree) =>
        tree.GetAllElements("Image")
            .Concat(tree.GetAllElements("Markdown"))
            .Concat(tree.GetAllElements("Manifest"))
            .Select(node => node.Get("ID"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>实体 ID：带 ID 属性的 Mod/Application 节点，按 OrdinalIgnoreCase 排序。</summary>
    private static string[] EntityIds(Metadata tree) =>
        tree.GetAllElements("Mod")
            .Concat(tree.GetAllElements("Application"))
            .Select(node => node.Get("ID"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// 引用节点数量：Icon/Logo/Content/HeadImage 的文本引用，加上 Package 下作为文本的 Manifest 引用
    /// （带 ID 的 Manifest 是登记节点，不算引用）。
    /// </summary>
    private static int CountReferences(Metadata tree)
    {
        var count = ReferenceElementNames.Sum(name =>
            tree.GetAllElements(name).Count(node => !string.IsNullOrWhiteSpace(node.Value)));

        foreach (var package in tree.GetAllElements("Package"))
        {
            count += package.Children.Count(child =>
                child.Name == "Manifest"
                && string.IsNullOrWhiteSpace(child.Get("ID"))
                && !string.IsNullOrWhiteSpace(child.Value));
        }

        return count;
    }

    private static int LineCount(string text) => text.Split('\n').Length - 1;

    /// <summary>
    /// 源树里除根 Tags/Commit 这条构建期变量外，还有没有别的 <c>${...}</c> 占位符（文本或属性）。
    /// 返回 "路径 = 值" 描述，供断言直接展示；新增变量会立刻让这里非空。
    /// </summary>
    private static string[] PlaceholdersOutsideBuildStamp(Metadata tree, Metadata? buildStamp)
    {
        var found = new List<string>();
        CollectPlaceholders(tree, buildStamp, found);
        return found.ToArray();
    }

    private static void CollectPlaceholders(Metadata node, Metadata? buildStamp, List<string> found)
    {
        if (!ReferenceEquals(node, buildStamp))
        {
            var text = node.Value?.Trim();
            if (!string.IsNullOrEmpty(text) && text.Contains("${", StringComparison.Ordinal))
                found.Add($"{node.GetElementPath()} = {text}");

            foreach (var variable in node.Variables.Where(v => v.Value.Contains("${", StringComparison.Ordinal)))
                found.Add($"{node.GetElementPath()} @{variable.Key} = {variable.Value}");
        }

        foreach (var child in node.Children)
            CollectPlaceholders(child, buildStamp, found);
    }

    /// <summary>失败时给出足够定位的差异摘要，不把整棵树倒进断言消息。</summary>
    private static string DiffHint(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var differing = 0;
        var firstDifference = -1;

        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
        {
            var left = i < expectedLines.Length ? expectedLines[i] : null;
            var right = i < actualLines.Length ? actualLines[i] : null;
            if (string.Equals(left, right, StringComparison.Ordinal))
                continue;
            differing++;
            if (firstDifference < 0)
                firstDifference = i;
        }

        var builder = new StringBuilder();
        builder.Append($"规范化文本行数 源树={expectedLines.Length} 发布物={actualLines.Length}，不同行数={differing}。");

        if (differing > 0)
        {
            builder.Append('\n').Append("第一处不同在行 ").Append(firstDifference + 1).Append("：\n");
            for (var i = Math.Max(0, firstDifference - 3); i <= Math.Min(expectedLines.Length - 1, firstDifference + 3); i++)
                builder.Append("  源树   [").Append(i + 1).Append("] ").Append(expectedLines[i]).Append('\n');
            for (var i = Math.Max(0, firstDifference - 3); i <= Math.Min(actualLines.Length - 1, firstDifference + 3); i++)
                builder.Append("  发布物 [").Append(i + 1).Append("] ").Append(actualLines[i]).Append('\n');
        }

        return builder.ToString();
    }
}
