using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata
{
    /// <summary>
    /// 展平元数据树（纯反序列化）。仅 Name/Value/Attributes/Children；
    /// 不含 Include 展开、Defines、public/private 等构建期语义——构建期语义见 MetadataFlattener/MetadataInheritance。
    /// </summary>
    public class Metadata
    {
        internal readonly Dictionary<string, string> _variables = new();
        internal readonly List<Metadata> _children = new();
        internal Metadata? _parent = null;
        internal string? _value = null;

        public string Name { get; private set; } = string.Empty;
        public IReadOnlyDictionary<string, string> Variables => _variables;
        public IReadOnlyList<Metadata> Children => _children;
        public Metadata? Parent => _parent;
        /// <summary>节点文本值（仅叶子节点有效）。</summary>
        public string? Value => _value;

        /// <summary>
        /// 从已展平的 metadata.xml 加载。遇到 Include/Module 会报错——本入口只接受展平产物。
        /// </summary>
        public static Metadata LoadFromFile(string path) => MetadataParser.LoadFromFile(path);

        /// <summary>
        /// 获取属性值。
        /// </summary>
        public string? Get(string key, string? defaultValue = null)
        {
            return _variables.TryGetValue(key, out var value) ? value : defaultValue;
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

        internal void ParseElement(XElement element)
        {
            Name = element.Name.LocalName;

            if (Name is "Include" or "Includes" or "Module")
            {
                throw new InvalidOperationException(
                    $"Load 只接受展平后的 metadata.xml（发现 {Name} 元素，疑似源树文件）");
            }

            foreach (var attr in element.Attributes())
                _variables[attr.Name.LocalName] = attr.Value;

            foreach (var child in element.Elements())
            {
                var childMetadata = new Metadata { _parent = this };
                childMetadata.ParseElement(child);
                _children.Add(childMetadata);
            }

            if (!element.HasElements)
                _value = element.Value;
        }
    }
}
