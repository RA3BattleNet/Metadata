# 客户端集成指南

## 版本一致性机制

本系统使用 UTC 时间戳作为版本标识，确保所有客户端获取的 metadata 具有一致性。

### 版本标识

每次构建时，所有 XML 文件中的 `${TIMESTAMP}` 变量会被替换为构建时的 UTC 时间戳（格式：`yyyy-MM-ddTHH:mm:ssZ`）。

例如：
```xml
<Metadata>
  <Tags>
    <Commit>2026-02-24T14:30:00Z</Commit>
  </Tags>
  ...
</Metadata>
```

## 如何确保 Metadata 一致性

### 方案 1: 使用版本检查（推荐）

客户端应该定期检查版本信息，只在版本变化时更新：

```
1. 获取 /version.json
2. 比较 version 字段（UTC 时间戳）与本地缓存的版本
3. 如果版本不同：
   - 下载 /metadata.xml
   - 解析并下载所有引用的资源文件
   - 更新本地缓存版本号
4. 如果版本相同：
   - 使用本地缓存
```

### 方案 2: 使用 ETag 和条件请求

利用 HTTP 缓存机制：

```
1. 首次请求 /metadata.xml，保存 ETag
2. 后续请求使用 If-None-Match 头
3. 如果返回 304 Not Modified，使用本地缓存
4. 如果返回 200，更新本地缓存和 ETag
```

### 方案 3: 使用时间戳

从 metadata.xml 中读取 Commit 标签：

```xml
<Metadata>
  <Tags>
    <Commit>2026-02-24T14:30:00Z</Commit>
  </Tags>
  ...
</Metadata>
```

使用这个 UTC 时间戳作为版本标识。

## API 端点

- `GET /version.json` - 快速版本检查（包含 UTC 时间戳）
- `GET /metadata.xml` - 主元数据文件
- `GET /mods/mods.xml` - Mod 列表
- `GET /apps/apps.xml` - 应用列表

## version.json 格式

```json
{
  "version": "2026-02-24T14:30:00Z",
  "metadata_url": "/metadata.xml"
}
```

## 缓存策略

- XML 文件：5 分钟客户端缓存，1 小时 CDN 缓存
- 资源文件（.zip, .pak）：24 小时不可变缓存

## 示例代码

### C# 客户端

```csharp
public class MetadataClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private string? _cachedVersion;

    public async Task<bool> CheckForUpdates()
    {
        var response = await _http.GetStringAsync($"{_baseUrl}/version.json");
        var versionInfo = JsonSerializer.Deserialize<VersionInfo>(response);

        if (versionInfo.Version != _cachedVersion)
        {
            _cachedVersion = versionInfo.Version;
            return true;
        }
        return false;
    }

    public async Task<Metadata> GetMetadata()
    {
        var xml = await _http.GetStringAsync($"{_baseUrl}/metadata.xml");
        return Metadata.Parse(xml);
    }
}

public class VersionInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("metadata_url")]
    public string MetadataUrl { get; set; } = "";
}
```

### JavaScript 客户端

```javascript
class MetadataClient {
  constructor(baseUrl) {
    this.baseUrl = baseUrl;
    this.cachedVersion = null;
  }

  async checkForUpdates() {
    const response = await fetch(`${this.baseUrl}/version.json`);
    const versionInfo = await response.json();

    if (versionInfo.version !== this.cachedVersion) {
      this.cachedVersion = versionInfo.version;
      return true;
    }
    return false;
  }

  async getMetadata() {
    const response = await fetch(`${this.baseUrl}/metadata.xml`);
    const xml = await response.text();
    return this.parseMetadata(xml);
  }
}
```

## 注意事项

1. **原子性更新**：所有文件在同一次部署中更新，确保一致性
2. **版本标识**：使用 UTC 时间戳作为唯一版本标识
3. **时间戳格式**：ISO 8601 格式（`yyyy-MM-ddTHH:mm:ssZ`）
4. **缓存控制**：合理使用 HTTP 缓存减少请求
5. **错误处理**：客户端应该处理网络错误和版本不匹配的情况
6. **时区无关**：所有时间戳均为 UTC+0，避免时区问题
