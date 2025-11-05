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

        public string Name { get; private set; } = string.Empty;
        public IReadOnlyDictionary<string, string> Variables => _variables;
        public IReadOnlyList<Metadata> Children => _children;
        public IReadOnlyDictionary<string, string> Defines => _defines;
        public IReadOnlyList<Metadata> DefineChildren => _defineChildren;

        public Metadata()
        {
            foreach (System.Collections.DictionaryEntry env in Environment.GetEnvironmentVariables())
            {
                var key = env.Key?.ToString();
                var value = env.Value?.ToString();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                {
                    _envVars[key] = value;
                }
            }
        }

        /// <summary>
        /// 从文件加载并验证XML元数据
        /// </summary>
        /// <param name="path">XML文件路径</param>
        /// <returns>解析后的Metadata对象</returns>
        /// <exception cref="XmlException">当XML格式无效时抛出</exception>
        /// <exception cref="FileNotFoundException">当文件不存在时抛出</exception>
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
                
                metadata.ParseElement(doc.Root, fullPath);
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
                ReplaceVariables(root);

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
                // 加载原始XML文件
                var doc = XDocument.Load(filePath);
                var root = doc.Root ?? throw new InvalidOperationException("无效的XML结构: 缺少根节点");

                // 验证所有Include/Module资源
                ValidateResources(root, Path.GetDirectoryName(filePath) ?? string.Empty);

                // 替换变量
                ReplaceVariables(root);

                // 保存回原文件
                doc.Save(filePath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"替换变量失败: {filePath}", ex);
            }
        }

        private void ValidateResources(XElement element, string? basePath)
        {
            if (string.IsNullOrEmpty(basePath)) return;

            foreach (var include in element.Elements("Include").Concat(element.Elements("Module")))
            {
                var path = include.Attribute("Path")?.Value ?? include.Attribute("Source")?.Value;
                if (string.IsNullOrEmpty(path)) continue;

                var fullPath = Path.Combine(basePath, path.Replace('/', Path.DirectorySeparatorChar));
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
            foreach (var child in _children)
            {
                element.Add(child.ToXElement());
            }
            return element;
        }

        private void ParseElement(XElement? element, string currentFilePath)
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
                    if (!string.IsNullOrEmpty(includePath))
                    {
                        string normalizedPath = includePath.Replace('/', Path.DirectorySeparatorChar);
                        var dir = Path.GetDirectoryName(currentFilePath);
                        if (string.IsNullOrEmpty(dir))
                        {
                            throw new InvalidOperationException($"无法确定文件目录: {currentFilePath}");
                        }
                        string fullIncludePath = Path.Combine(dir, normalizedPath);
                        var included = LoadFromFile(fullIncludePath);
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
                            defineChild.ParseElement(define, currentFilePath);
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
                    childMetadata.ParseElement(child, currentFilePath);
                    _children.Add(childMetadata);
                }
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
                var fullPath = Path.Combine(dir, source.Replace('/', Path.DirectorySeparatorChar));
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
        private void ReplaceVariables(XElement element)
        {
            try
            {
                foreach (var attr in element.Attributes())
                {
                    try
                    {
                        attr.Value = ResolveVariables(attr.Value, element, attr);
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
                        element.Value = ResolveVariables(element.Value, element, null);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"解析元素值变量失败: {element.Name}", ex);
                    }
                }

                foreach (var child in element.Elements())
                {
                    ReplaceVariables(child);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"替换变量失败: {element.Name}", ex);
            }
        }

        private string ResolveVariables(string input, XElement context, XAttribute? currentAttr)
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
                            // ${MD5::} - When used in a Hash attribute, compute hash of file in Source attribute
                            // Example: <Markdown Source="file.md" Hash="${MD5::}"/> 
                            // This will compute the MD5 hash of "file.md"
                            if (currentAttr != null && currentAttr.Name.LocalName == "Hash")
                            {
                                var sourceAttr = context.Attribute("Source");
                                if (sourceAttr != null && !string.IsNullOrEmpty(sourceAttr.Value))
                                {
                                    fileToHash = sourceAttr.Value;
                                }
                            }
                            
                            if (string.IsNullOrEmpty(fileToHash))
                            {
                                return ComputeFileHash(_currentFilePath);
                            }
                        }
                        var dir = Path.GetDirectoryName(_currentFilePath);
                        if (string.IsNullOrEmpty(dir))
                        {
                            return $"HASH_ERROR_DIR_NOT_FOUND:{_currentFilePath}";
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

            Metadata? current = this;
            foreach (var part in parts)
            {
                current = current?._children.FirstOrDefault(c => c.Name == part);
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
    }
}
