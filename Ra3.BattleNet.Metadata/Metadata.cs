using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Ra3.BattleNet.Metadata
{
    public class Metadata
    {
        private readonly Dictionary<string, string> _variables = new();
        private readonly List<Metadata> _children = new();
        private readonly Dictionary<string, string> _defines = new();
        private readonly List<Metadata> _defineChildren = new();
        private readonly Dictionary<string, string> _envVars = new();
        private readonly Dictionary<string, string> _fileHashes = new();
        private string _currentFilePath = string.Empty;
        private string? _value = null;
        private Metadata? _parent = null;
        private string _includeType = "public"; // "public" 或 "private"

        public string Name { get; private set; } = string.Empty;
        public IReadOnlyDictionary<string, string> Variables => _variables;
        public IReadOnlyList<Metadata> Children => _children;
        public IReadOnlyDictionary<string, string> Defines => _defines;
        public IReadOnlyList<Metadata> DefineChildren => _defineChildren;
        public Metadata? Parent => _parent;
        public string IncludeType => _includeType;
        /// <summary>
        /// 节点文本值（仅叶子节点有效）。
        /// </summary>
        public string? Value => _value;

        public Metadata()
        {
            foreach (System.Collections.DictionaryEntry env in Environment.GetEnvironmentVariables())
            {
                _envVars[env.Key.ToString()] = env.Value.ToString();
            }
        }

        /// <summary>
        /// 从文件加载并验证XML元数据
        /// </summary>
        /// <param name="path">XML文件路径</param>
        /// <returns>解析后的Metadata对象</returns>
        /// <exception cref="XmlException">当XML格式无效时抛出</exception>
        /// <exception cref="FileNotFoundException">当文件不存在时抛出</exception>
        /// <exception cref="InvalidOperationException">当检测到循环引用时抛出</exception>
        public static Metadata LoadFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("文件路径不能为空", nameof(path));

            string fullPath = Path.IsPathRooted(path)
                ? path
                : Path.Combine(Environment.CurrentDirectory, path);

            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"找不到文件: {fullPath}");

            // 配置XML验证设置
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                ValidationFlags = XmlSchemaValidationFlags.None,
                IgnoreWhitespace = true
            };

            try
            {
                using var reader = XmlReader.Create(fullPath, settings);
                var doc = XDocument.Load(reader);

                if (doc.Root == null || doc.Root.Name != "Metadata")
                    throw new XmlException("无效的XML结构: 缺少根节点或根节点名称不是'Metadata'");

                var metadata = new Metadata
                {
                    _currentFilePath = fullPath
                };

                // 创建路径追踪集合用于循环检测
                var loadingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullPath };
                metadata.ParseElement(doc.Root, fullPath, loadingPaths);
                return metadata;
            }
            catch (XmlException ex)
            {
                throw new XmlException($"XML解析失败: {fullPath}", ex);
            }
        }

        /// <summary>
        /// 编译当前元数据及其所有引用
        /// </summary>
        /// <returns>编译后的XDocument</returns>
        /// <exception cref="InvalidOperationException">当XML结构无效时抛出</exception>
        public XDocument Compile()
        {
            try
            {
                var doc = new XDocument(new XElement(Name));
                var root = doc.Root ?? throw new InvalidOperationException("无效的XML结构: 缺少根节点");
                
                // 复制属性
                foreach (var var in _variables)
                {
                    root.SetAttributeValue(var.Key, var.Value);
                }

                // 处理子元素
                foreach (var child in _children)
                {
                    try
                    {
                        root.Add(child.ToXElement());
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"处理子元素失败: {child.Name}", ex);
                    }
                }

                // 处理Includes和变量替换
                ProcessIncludes(root);
                ReplaceVariables(root, _currentFilePath);

                return doc;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"编译元数据失败: {Name}", ex);
            }
        }

        /// <summary>
        /// 在原文件中替换变量并验证资源
        /// </summary>
        /// <param name="filePath">要处理的XML文件路径</param>
        /// <exception cref="InvalidOperationException">当XML结构无效或资源验证失败时抛出</exception>
        /// <summary>
        /// 在原文件中替换变量并验证资源
        /// </summary>
        /// <param name="filePath">要处理的XML文件路径</param>
        /// <exception cref="InvalidOperationException">当XML结构无效或资源验证失败时抛出</exception>
        public void ReplaceVariablesInFile(string filePath)
        {
            try
            {
                var fullPath = Path.GetFullPath(filePath);
                ReplaceVariablesInFileRecursive(
                    fullPath,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"替换变量失败: {filePath}", ex);
            }
        }

        private void ReplaceVariablesInFileRecursive(string filePath, HashSet<string> processingPaths, HashSet<string> processedPaths)
        {
            if (processedPaths.Contains(filePath))
            {
                return;
            }

            if (!processingPaths.Add(filePath))
            {
                throw new InvalidOperationException($"检测到循环引用: {filePath}");
            }

            var doc = XDocument.Load(filePath);
            var root = doc.Root ?? throw new InvalidOperationException("无效的XML结构: 缺少根节点");
            var basePath = Path.GetDirectoryName(filePath)
                ?? throw new InvalidOperationException($"无法确定文件目录: {filePath}");

            // 验证并递归处理所有Include/Module资源
            ValidateResources(root, basePath);

            foreach (var include in root.Descendants().Where(e => e.Name.LocalName == "Include" || e.Name.LocalName == "Module"))
            {
                var path = include.Attribute("Path")?.Value ?? include.Attribute("Source")?.Value;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var referencedFile = Path.GetFullPath(Path.Combine(basePath, path.Replace('\\', '/')));
                if (File.Exists(referencedFile))
                {
                    ReplaceVariablesInFileRecursive(referencedFile, processingPaths, processedPaths);
                }
            }

            ReplaceVariables(root, filePath);
            doc.Save(filePath);

            processingPaths.Remove(filePath);
            processedPaths.Add(filePath);
        }

        private void ValidateResources(XElement element, string basePath)
        {
            foreach (var include in element.Elements("Include").Concat(element.Elements("Module")))
            {
                var path = include.Attribute("Path")?.Value ?? include.Attribute("Source")?.Value;
                if (string.IsNullOrEmpty(path)) continue;

                var fullPath = Path.Combine(basePath, path.Replace('\\', '/'));
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException($"引用的资源文件不存在: {fullPath} (来自元素: {include.Name.LocalName})");
                }
            }

            foreach (var child in element.Elements())
            {
                ValidateResources(child, basePath);
            }
        }

        private XElement ToXElement()
        {
            var element = new XElement(Name);
            foreach (var var in _variables)
            {
                element.SetAttributeValue(var.Key, var.Value);
            }

            if (!string.IsNullOrWhiteSpace(_value) && !_children.Any())
            {
                element.Value = _value;
            }

            foreach (var child in _children)
            {
                element.Add(child.ToXElement());
            }
            return element;
        }

        private void ParseElement(XElement? element, string currentFilePath, HashSet<string>? loadingPaths = null)
        {
            if (element == null) return;

            Name = element.Name.LocalName;

            foreach (var attr in element.Attributes())
            {
                _variables[attr.Name.LocalName] = attr.Value;
            }

            foreach (var child in element.Elements())
            {
                if (child.Name.LocalName == "Include" || child.Name.LocalName == "Module")
                {
                    var includePath = child.Attribute("Path")?.Value ?? child.Attribute("Source")?.Value;
                    var includeType = child.Attribute("Type")?.Value ?? "public"; // 默认为public

                    if (!string.IsNullOrEmpty(includePath))
                    {
                        string normalizedPath = includePath.Replace('\\', '/');
                        var dir = Path.GetDirectoryName(currentFilePath);
                        if (string.IsNullOrEmpty(dir))
                        {
                            throw new InvalidOperationException($"无法确定文件目录: {currentFilePath}");
                        }
                        string fullIncludePath = Path.GetFullPath(Path.Combine(dir, normalizedPath));

                        // 循环引用检测
                        if (loadingPaths != null && loadingPaths.Contains(fullIncludePath))
                        {
                            var pathChain = string.Join(" -> ", loadingPaths) + " -> " + fullIncludePath;
                            throw new InvalidOperationException($"检测到循环引用: {pathChain}");
                        }

                        // 加载Include文件
                        var included = LoadFromFileInternal(fullIncludePath, loadingPaths);
                        included._parent = this;
                        included._includeType = includeType;
                        _children.Add(included);
                    }
                }
                else if (child.Name.LocalName == "Defines")
                {
                    foreach (var define in child.Elements())
                    {
                        if (define.HasElements)
                        {
                            var defineChild = new Metadata();
                            defineChild._parent = this;
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
                    var childMetadata = new Metadata();
                    childMetadata._parent = this;
                    childMetadata.ParseElement(child, currentFilePath, loadingPaths);
                    _children.Add(childMetadata);
                }
            }

            if (!element.HasElements)
            {
                _value = element.Value;
            }
        }

        /// <summary>
        /// 将当前元数据转换为可反序列化使用的树状结构。
        /// </summary>
        public MetadataNode ToNodeTree()
        {
            return BuildNode(this);
        }

        /// <summary>
        /// 获取所有业务实体（Application/Mod/Markdown/Image/Manifest）节点。
        /// </summary>
        public IReadOnlyList<BusinessEntity> GetBusinessEntities()
        {
            var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Application", "Mod", "Markdown", "Image", "Manifest"
            };

            var allNodes = GetAllNodes();
            var entities = new List<BusinessEntity>();

            foreach (var node in allNodes.Where(n => supported.Contains(n.Name)))
            {
                var entity = new BusinessEntity
                {
                    EntityType = node.Name,
                    Id = node.Get("ID"),
                    Path = node.GetElementPath(),
                    Attributes = new Dictionary<string, string>(node._variables, StringComparer.OrdinalIgnoreCase),
                    Properties = CollectEntityProperties(node)
                };

                entities.Add(entity);
            }

            return entities;
        }

        private static Dictionary<string, string> CollectEntityProperties(Metadata node)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var child in node._children)
            {
                if (!string.IsNullOrWhiteSpace(child._value) && !result.ContainsKey(child.Name))
                {
                    result[child.Name] = child._value;
                }
            }

            var packageCount = node._children.FirstOrDefault(c => c.Name == "Packages")
                ?._children.Count(c => c.Name == "Package");
            if (packageCount.HasValue)
            {
                result["PackageCount"] = packageCount.Value.ToString();
            }

            var fileCount = node._children.Count(c => c.Name == "File");
            if (fileCount > 0)
            {
                result["FileCount"] = fileCount.ToString();
            }

            return result;
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
            {
                nodes.AddRange(child.GetAllNodes());
            }

            return nodes;
        }

        /// <summary>
        /// 内部加载方法，支持循环检测
        /// </summary>
        private static Metadata LoadFromFileInternal(string fullPath, HashSet<string>? loadingPaths)
        {
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"找不到文件: {fullPath}");

            // 配置XML验证设置
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                ValidationFlags = XmlSchemaValidationFlags.None,
                IgnoreWhitespace = true
            };

            try
            {
                using var reader = XmlReader.Create(fullPath, settings);
                var doc = XDocument.Load(reader);

                if (doc.Root == null || doc.Root.Name != "Metadata")
                    throw new XmlException("无效的XML结构: 缺少根节点或根节点名称不是'Metadata'");

                var metadata = new Metadata
                {
                    _currentFilePath = fullPath
                };

                // 创建新的路径集合或复制现有集合
                HashSet<string> newLoadingPaths;
                if (loadingPaths != null)
                {
                    newLoadingPaths = new HashSet<string>(loadingPaths, StringComparer.OrdinalIgnoreCase);
                    newLoadingPaths.Add(fullPath);
                }
                else
                {
                    newLoadingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullPath };
                }

                metadata.ParseElement(doc.Root, fullPath, newLoadingPaths);
                return metadata;
            }
            catch (XmlException ex)
            {
                throw new XmlException($"XML解析失败: {fullPath}", ex);
            }
        }

        private void ProcessIncludes(XElement element)
        {
            foreach (var include in element.Elements("Include"))
            {
                var source = include.Attribute("Source")?.Value;
                if (string.IsNullOrEmpty(source)) continue;

                var dir = Path.GetDirectoryName(_currentFilePath);
                if (string.IsNullOrEmpty(dir))
                {
                    throw new InvalidOperationException($"无法确定文件目录: {_currentFilePath}");
                }
                var fullPath = Path.Combine(dir, source.Replace('\\', '/'));
                if (!File.Exists(fullPath)) continue;

                var includedDoc = XDocument.Load(fullPath);
                var includedRoot = includedDoc.Root;
                if (includedRoot == null) continue;

                ProcessIncludes(includedRoot);
                include.ReplaceWith(includedRoot.Elements());
            }
        }

        /// <summary>
        /// 递归替换XML元素中的变量
        /// </summary>
        /// <param name="element">要处理的XML元素</param>
        private void ReplaceVariables(XElement element, string currentFilePath)
        {
            try
            {
                foreach (var attr in element.Attributes())
                {
                    try
                    {
                        attr.Value = ResolveVariables(attr.Value, element, currentFilePath);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"解析属性变量失败: {attr.Name}", ex);
                    }
                }

                if (!element.HasElements && !string.IsNullOrEmpty(element.Value))
                {
                    try
                    {
                        element.Value = ResolveVariables(element.Value, element, currentFilePath);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"解析元素值变量失败: {element.Name}", ex);
                    }
                }

                foreach (var child in element.Elements())
                {
                    ReplaceVariables(child, currentFilePath);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"替换变量失败: {element.Name}", ex);
            }
        }

        private string ResolveVariables(string input, XElement context, string currentFilePath)
        {
            return System.Text.RegularExpressions.Regex.Replace(input, @"\$\{(.*?)\}", match =>
            {
                var expr = match.Groups[1].Value;
                var parts = expr.Split(':');
                if (parts.Length < 1) return match.Value;

                switch (parts[0])
                {
                    case "ENV":
                        return parts.Length > 1 && _envVars.TryGetValue(parts[1], out var envValue) 
                            ? envValue 
                            : match.Value;
                    case "MD5":
                        var fileToHash = parts.Length > 1 ? parts[1] : "";
                        if (string.IsNullOrEmpty(fileToHash))
                        {
                            return ComputeFileHash(currentFilePath);
                        }
                        var dir = Path.GetDirectoryName(currentFilePath);
                        if (string.IsNullOrEmpty(dir))
                        {
                            return $"HASH_ERROR_DIR_NOT_FOUND:{currentFilePath}";
                        }
                        return ComputeFileHash(Path.Combine(dir, fileToHash));
                    case "META":
                        if (parts.Length < 2) return match.Value;
                        try
                        {
                            var metaPath = string.Join(":", parts.Skip(1));
                            var target = FindMetaReference(context.Document?.Root, metaPath);
                            return target?.Value ?? $"META_NOT_FOUND:{metaPath}";
                        }
                        catch
                        {
                            return $"META_ERROR:{string.Join(":", parts.Skip(1))}";
                        }
                    case "this":
                        if (parts.Length < 2) return match.Value;
                        try
                        {
                            var currentModule = GetCurrentModule(context);
                            var target = FindInDefines(currentModule, parts[1]);
                            return target ?? $"THIS_NOT_FOUND:{parts[1]}";
                        }
                        catch
                        {
                            return $"THIS_ERROR:{parts[1]}";
                        }
                    default:
                        return match.Value;
                }
            });
        }

        private string ComputeFileHash(string filePath)
        {
            if (_fileHashes.TryGetValue(filePath, out var hash))
            {
                return hash;
            }

            try
            {
                using var md5 = MD5.Create();
                using var stream = File.OpenRead(filePath);
                var hashBytes = md5.ComputeHash(stream);
                hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                _fileHashes[filePath] = hash;
                return hash;
            }
            catch
            {
                return $"HASH_ERROR_{filePath}";
            }
        }

        private XElement? FindMetaReference(XElement? root, string path)
        {
            if (root == null) return null;
            
            var parts = path.Split(':');
            XElement? current = root;
            
            foreach (var part in parts)
            {
                current = current.Elements().FirstOrDefault(e => e.Name.LocalName == part);
                if (current == null) return null;
            }
            
            return current;
        }

        private XElement? GetCurrentModule(XElement element)
        {
            var current = element;
            while (current != null)
            {
                if (current.Name.LocalName == "Module") return current;
                current = current.Parent;
            }
            return null;
        }

        private string? FindInDefines(XElement? module, string key)
        {
            if (module == null) return null;
            
            var defines = module.Elements("Defines").FirstOrDefault();
            if (defines == null) return null;
            
            var define = defines.Elements().FirstOrDefault(e => e.Name.LocalName == key);
            return define?.Value;
        }

        public string? Get(string key, string? defaultValue = null)
        {
            if (_variables.TryGetValue(key, out var value))
                return value;
            
            if (_defines.TryGetValue(key, out value))
                return value;
                
            return defaultValue;
        }

        public Metadata? Find(string path)
        {
            var parts = path.Split(':');
            if (parts.Length == 0)
                return null;

            Metadata current = this;
            foreach (var part in parts)
            {
                current = current._children.FirstOrDefault(c => c.Name == part);
                if (current == null)
                    return null;
            }
            return current;
        }

        public string? GetDefine(string path)
        {
            var lastColon = path.LastIndexOf(':');
            if (lastColon == -1)
                return _defines.TryGetValue(path, out var value) ? value : null;

            var parentPath = path.Substring(0, lastColon);
            var key = path.Substring(lastColon + 1);

            var parent = Find(parentPath);
            return parent?.Get(key);
        }

        /// <summary>
        /// 按ID查找元素，考虑访问控制规则
        /// </summary>
        /// <param name="id">元素ID</param>
        /// <returns>找到的元素，如果不存在或不可访问则返回null</returns>
        public Metadata? GetElementById(string id)
        {
            // 在当前节点查找
            if (Get("ID") == id)
                return this;

            // 在子节点中查找
            foreach (var child in _children)
            {
                // 如果是private类型的Include，只在其子树中查找
                if (child._includeType == "private")
                {
                    var found = child.GetElementByIdInSubtree(id);
                    if (found != null)
                        return found;
                }
                // 如果是public类型，递归查找
                else
                {
                    var found = child.GetElementById(id);
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        /// <summary>
        /// 在子树中查找元素（不向上传播）
        /// </summary>
        private Metadata? GetElementByIdInSubtree(string id)
        {
            if (Get("ID") == id)
                return this;

            foreach (var child in _children)
            {
                var found = child.GetElementByIdInSubtree(id);
                if (found != null)
                    return found;
            }

            return null;
        }

        /// <summary>
        /// 获取所有指定名称的元素
        /// </summary>
        /// <param name="name">元素名称</param>
        /// <returns>匹配的元素列表</returns>
        public List<Metadata> GetAllElements(string name)
        {
            var results = new List<Metadata>();

            if (Name == name)
                results.Add(this);

            foreach (var child in _children)
            {
                results.AddRange(child.GetAllElements(name));
            }

            return results;
        }

        /// <summary>
        /// 获取元素的完整路径
        /// </summary>
        /// <returns>从根到当前元素的路径字符串</returns>
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
        /// 获取Include引用关系树的可视化表示
        /// </summary>
        /// <param name="indent">缩进级别</param>
        /// <returns>树状结构字符串</returns>
        public string GetIncludeTree(int indent = 0)
        {
            var sb = new System.Text.StringBuilder();
            var indentStr = new string(' ', indent * 2);

            var id = Get("ID") ?? Name;
            var typeInfo = _includeType == "private" ? " [private]" : "";
            sb.AppendLine($"{indentStr}{id}{typeInfo}");

            foreach (var child in _children)
            {
                sb.Append(child.GetIncludeTree(indent + 1));
            }

            return sb.ToString();
        }
    }
}
