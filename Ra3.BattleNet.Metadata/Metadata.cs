using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata
{
    /// <summary>
    /// 元数据树节点，支持 Include 引用、Defines、业务实体查询和树结构导航。
    /// </summary>
    public class Metadata
    {
        internal readonly Dictionary<string, string> _variables = new();
        internal readonly List<Metadata> _children = new();
        internal readonly Dictionary<string, string> _defines = new();
        internal readonly List<Metadata> _defineChildren = new();
        internal string _currentFilePath = string.Empty;
        internal string? _value = null;
        internal Metadata? _parent = null;
        internal string _includeType = "public";

        public string Name { get; private set; } = string.Empty;
        public IReadOnlyDictionary<string, string> Variables => _variables;
        public IReadOnlyList<Metadata> Children => _children;
        public IReadOnlyDictionary<string, string> Defines => _defines;
        public IReadOnlyList<Metadata> DefineChildren => _defineChildren;
        public Metadata? Parent => _parent;
        public string IncludeType => _includeType;
        /// <summary>节点文本值（仅叶子节点有效）。</summary>
        public string? Value => _value;

        /// <summary>
        /// 从文件加载并验证 XML 元数据（含 Include 展开和循环检测）。
        /// </summary>
        public static Metadata LoadFromFile(string path) => MetadataParser.LoadFromFile(path);

        /// <summary>
        /// 从文件加载并验证 XML 元数据，同时使用 XSD Schema 验证。
        /// </summary>
        public static Metadata LoadFromFileWithSchema(string path, string? schemaPath = null)
            => MetadataParser.LoadFromFileWithSchema(path, schemaPath);

        /// <summary>
        /// 在输出目录中替换变量（${TIMESTAMP}、${ENV:}、${MD5:}、${META:}、${this:}）。
        /// 注意：此方法操作的是输出目录的副本，不会修改源文件。
        /// </summary>
        public void ReplaceVariablesInFile(string filePath)
        {
            var resolver = new VariableResolver();
            resolver.ReplaceInFile(filePath);
        }

        /// <summary>
        /// 将当前元数据转换为可反序列化使用的树状结构。
        /// </summary>
        public MetadataNode ToNodeTree() => BuildNode(this);

        /// <summary>
        /// 获取所有业务实体（Application/Mod/Markdown/Image/Manifest）节点。
        /// </summary>
        public IReadOnlyList<BusinessEntity> GetBusinessEntities()
        {
            var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Application", "Mod", "Markdown", "Image", "Manifest"
            };

            return GetAllNodes()
                .Where(n => supported.Contains(n.Name))
                .Select(node => new BusinessEntity
                {
                    EntityType = node.Name,
                    Id = node.Get("ID"),
                    Path = node.GetElementPath(),
                    Attributes = new Dictionary<string, string>(node._variables, StringComparer.OrdinalIgnoreCase),
                    Properties = CollectEntityProperties(node)
                })
                .ToList();
        }

        /// <summary>
        /// 获取属性值（先查 XML 属性，再查 Defines）。
        /// </summary>
        public string? Get(string key, string? defaultValue = null)
        {
            if (_variables.TryGetValue(key, out var value)) return value;
            if (_defines.TryGetValue(key, out value)) return value;
            return defaultValue;
        }

        /// <summary>
        /// 按路径查找子元素，使用 <c>:</c> 分隔。
        /// </summary>
        public Metadata? Find(string path)
        {
            var parts = path.Split(':');
            if (parts.Length == 0) return null;

            Metadata? current = this;
            foreach (var part in parts)
            {
                current = current?._children.FirstOrDefault(c => c.Name == part);
                if (current == null) return null;
            }
            return current;
        }

        /// <summary>
        /// 通过路径获取 Define 值（如 <c>"Application:Version"</c>）。
        /// </summary>
        public string? GetDefine(string path)
        {
            var lastColon = path.LastIndexOf(':');
            if (lastColon == -1)
                return _defines.TryGetValue(path, out var value) ? value : null;

            var parentPath = path[..lastColon];
            var key = path[(lastColon + 1)..];
            return Find(parentPath)?.Get(key);
        }

        /// <summary>
        /// 按 ID 查找元素，考虑 public/private 访问控制规则。
        /// </summary>
        public Metadata? GetElementById(string id)
        {
            if (Get("ID") == id) return this;

            foreach (var child in _children)
            {
                if (child._includeType == "private")
                {
                    var found = child.GetElementByIdInSubtree(id);
                    if (found != null) return found;
                }
                else
                {
                    var found = child.GetElementById(id);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>
        /// 获取所有指定名称的元素。
        /// </summary>
        public List<Metadata> GetAllElements(string name)
        {
            var results = new List<Metadata>();
            if (Name == name) results.Add(this);
            foreach (var child in _children)
                results.AddRange(child.GetAllElements(name));
            return results;
        }

        /// <summary>
        /// 获取元素的完整路径（从根到当前元素）。
        /// </summary>
        public string GetElementPath()
        {
            var path = new List<string>();
            var current = this;
            while (current != null)
            {
                path.Insert(0, current.Name);
                current = current._parent;
            }
            return string.Join(" -> ", path);
        }

        /// <summary>
        /// 获取 Include 引用关系树的可视化表示。
        /// </summary>
        public string GetIncludeTree(int indent = 0)
        {
            var sb = new System.Text.StringBuilder();
            var indentStr = new string(' ', indent * 2);
            var id = Get("ID") ?? Name;
            var typeInfo = _includeType == "private" ? " [private]" : "";
            sb.AppendLine($"{indentStr}{id}{typeInfo}");
            foreach (var child in _children)
                sb.Append(child.GetIncludeTree(indent + 1));
            return sb.ToString();
        }

        // ---- 内部方法 ----

        internal void ParseElement(XElement? element, string currentFilePath, HashSet<string>? loadingPaths = null)
        {
            if (element == null) return;

            Name = element.Name.LocalName;
            foreach (var attr in element.Attributes())
                _variables[attr.Name.LocalName] = attr.Value;

            foreach (var child in element.Elements())
            {
                if (child.Name.LocalName is "Include" or "Module")
                {
                    var includePath = child.Attribute("Path")?.Value ?? child.Attribute("Source")?.Value;
                    var includeType = child.Attribute("Type")?.Value ?? "public";

                    if (string.IsNullOrEmpty(includePath)) continue;

                    var normalizedPath = includePath.Replace('\\', '/');
                    var dir = Path.GetDirectoryName(currentFilePath)
                        ?? throw new InvalidOperationException($"无法确定文件目录: {currentFilePath}");
                    var fullIncludePath = Path.GetFullPath(Path.Combine(dir, normalizedPath));

                    if (loadingPaths != null && loadingPaths.Contains(fullIncludePath))
                    {
                        var pathChain = string.Join(" -> ", loadingPaths) + " -> " + fullIncludePath;
                        throw new InvalidOperationException($"检测到循环引用: {pathChain}");
                    }

                    var included = MetadataParser.LoadFromFileInternal(fullIncludePath, loadingPaths);
                    included._parent = this;
                    included._includeType = includeType;
                    _children.Add(included);
                }
                else if (child.Name.LocalName == "Defines")
                {
                    foreach (var define in child.Elements())
                    {
                        if (define.HasElements)
                        {
                            var defineChild = new Metadata { _parent = this };
                            defineChild.ParseElement(define, currentFilePath, loadingPaths);
                            _defineChildren.Add(defineChild);
                        }
                        else
                        {
                            _defines[define.Name.LocalName] = define.Value;
                        }
                    }
                }
                else
                {
                    var childMetadata = new Metadata { _parent = this };
                    childMetadata.ParseElement(child, currentFilePath, loadingPaths);
                    _children.Add(childMetadata);
                }
            }

            if (!element.HasElements)
                _value = element.Value;
        }

        private MetadataNode BuildNode(Metadata metadata)
        {
            return new MetadataNode(
                metadata.Name,
                metadata.Value,
                new Dictionary<string, string>(metadata._variables, StringComparer.OrdinalIgnoreCase),
                metadata._children.Select(BuildNode).ToList());
        }

        private List<Metadata> GetAllNodes()
        {
            var nodes = new List<Metadata> { this };
            foreach (var child in _children)
                nodes.AddRange(child.GetAllNodes());
            return nodes;
        }

        private Metadata? GetElementByIdInSubtree(string id)
        {
            if (Get("ID") == id) return this;
            foreach (var child in _children)
            {
                var found = child.GetElementByIdInSubtree(id);
                if (found != null) return found;
            }
            return null;
        }

        private static Dictionary<string, string> CollectEntityProperties(Metadata node)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var child in node._children)
            {
                if (!string.IsNullOrWhiteSpace(child._value) && !result.ContainsKey(child.Name))
                    result[child.Name] = child._value;
            }

            var packageCount = node._children
                .FirstOrDefault(c => c.Name == "Packages")
                ?._children.Count(c => c.Name == "Package");
            if (packageCount.HasValue)
                result["PackageCount"] = packageCount.Value.ToString();

            var fileCount = node._children.Count(c => c.Name == "File");
            if (fileCount > 0)
                result["FileCount"] = fileCount.ToString();

            // 归一化关键属性
            var currentVersion = node._children.FirstOrDefault(c => c.Name == "CurrentVersion");
            if (currentVersion?.Value != null)
                result["CurrentVersion"] = currentVersion.Value;

            var icon = node._children.FirstOrDefault(c => c.Name == "Icon");
            if (icon?.Value != null)
                result["Icon"] = icon.Value;

            return result;
        }
    }
}
