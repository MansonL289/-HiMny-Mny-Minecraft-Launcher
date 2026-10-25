using System.Text.Json;

namespace WinUI3Shell.Services.Mc;

/// <summary>
/// 单个已装版本的元数据与设置，存于 versions/&lt;版本名&gt;/mnymc.json。
/// 版本名 = 目录名；Id = 底层 MC 版本（目录内的 json/jar 文件按 Id 命名）。
/// 旧版安装（目录名即版本号、无 mnymc.json）自动按目录名兼容。
/// </summary>
public sealed class VersionMeta
{
    public string Id { get; set; } = "";
    public bool Pinned { get; set; }
    public bool Isolation { get; set; }
    /// <summary>自定义 java 路径；空 = 自动选择（自定义的打不开时也会回退自动）。</summary>
    public string JavaPath { get; set; } = "";
    /// <summary>内存分配：global（跟随全局）/ auto（自动配置）/ custom（自定义 MB）。</summary>
    public string MemoryMode { get; set; } = "global";
    public int MemoryMb { get; set; } = 2048;
    /// <summary>启动后自动加入的服务器地址（host 或 host:port），空 = 不加入。</summary>
    public string AutoJoinServer { get; set; } = "";

    public const string MetaFileName = "mnymc.json";

    public static string DirPath(string dir) => Path.Combine(McPaths.VersionsDir, dir);
    public static string MetaPath(string dir) => Path.Combine(DirPath(dir), MetaFileName);
    public string JsonPath(string dir) => Path.Combine(DirPath(dir), Id + ".json");
    public string JarPath(string dir) => Path.Combine(DirPath(dir), Id + ".jar");

    /// <summary>读取版本元数据；无 mnymc.json 且目录内有 &lt;dir&gt;.json 时按旧版兼容。</summary>
    public static VersionMeta? Load(string dir)
    {
        string metaPath = MetaPath(dir);
        try
        {
            if (File.Exists(metaPath))
            {
                var meta = JsonSerializer.Deserialize<VersionMeta>(File.ReadAllText(metaPath));
                return string.IsNullOrEmpty(meta?.Id) ? null : meta;
            }
            // 旧版兼容：目录名即版本号
            if (File.Exists(Path.Combine(DirPath(dir), dir + ".json")))
            {
                return new VersionMeta { Id = dir };
            }
        }
        catch { /* 损坏的元数据视为无版本 */ }
        return null;
    }

    public void Save(string dir)
    {
        Directory.CreateDirectory(DirPath(dir));
        File.WriteAllText(MetaPath(dir), JsonSerializer.Serialize(this));
    }

    /// <summary>隔离时游戏目录 = 版本目录，否则共享主目录。</summary>
    public string GameDir(string dir) => Isolation ? DirPath(dir) : McPaths.GameDir;

    /// <summary>启动参数 -Xmx 的值：跟随全局 / 自动（可用内存 2/3）/ 自定义。</summary>
    public string MaxMemString()
    {
        switch (MemoryMode)
        {
            case "custom":
                return Math.Max(MemoryMb, 512) + "M";
            case "auto":
                var (_, avail) = SysMemory.GetPhysicalMemoryMb();
                return Math.Max(avail * 2 / 3, 1024) + "M";
            default:
                return AppSettings.MaxMemString();
        }
    }

    /// <summary>解析 Java：自定义路径有效就用，否则（或打不开/主版本不符时）自动选择。</summary>
    public async Task<string> ResolveJavaAsync(int minMajor, int maxMajor, IProgress<McProgress>? progress, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(JavaPath) && File.Exists(JavaPath))
        {
            try
            {
                int? major = await JavaService.GetMajorAsync(JavaPath);
                if (major.HasValue && major.Value >= minMajor && major.Value <= maxMajor)
                {
                    return JavaPath;
                }
            }
            catch { /* 自定义 Java 打不开/版本不符，回退自动选择 */ }
        }
        return await JavaService.EnsureAsync(minMajor, maxMajor, progress, ct);
    }
}
