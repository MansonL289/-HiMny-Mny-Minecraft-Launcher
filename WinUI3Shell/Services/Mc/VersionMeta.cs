using System.Text.Json;
using System.Text.Json.Nodes;

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
    /// <summary>隔离开关（旧配置字段，保留以便旧版启动器读取；新逻辑见 IsolationMode）。</summary>
    public bool Isolation { get; set; }
    /// <summary>版本隔离：global（跟随全局）/ on（隔离）/ off（不隔离）。</summary>
    public string IsolationMode { get; set; } = "global";
    /// <summary>自定义 java 路径；空 = 自动选择（自定义的打不开时也会回退自动）。</summary>
    public string JavaPath { get; set; } = "";
    /// <summary>内存分配：global（跟随全局）/ auto（自动配置）/ custom（自定义 MB）。</summary>
    public string MemoryMode { get; set; } = "global";
    public int MemoryMb { get; set; } = 2048;
    /// <summary>启动后自动加入的服务器地址（host 或 host:port），空 = 不加入。</summary>
    public string AutoJoinServer { get; set; } = "";
    /// <summary>追加的 JVM 参数（每行一个，如 -XX:+UseG1GC）。</summary>
    public string ExtraJvmArgs { get; set; } = "";
    /// <summary>追加的游戏参数（每行一个，如 --fullscreen）。</summary>
    public string ExtraGameArgs { get; set; } = "";
    /// <summary>自定义图标：版本目录下的相对路径（约定 icon.png / icon.ico）；空 = 默认图标。</summary>
    public string Icon { get; set; } = "";

    public const string MetaFileName = "mnymc.json";

    public static string DirPath(string dir) => Path.Combine(McPaths.VersionsDir, dir);
    public static string MetaPath(string dir) => Path.Combine(DirPath(dir), MetaFileName);
    public string JsonPath(string dir) => Path.Combine(DirPath(dir), Id + ".json");
    public string JarPath(string dir) => Path.Combine(DirPath(dir), Id + ".jar");

    /// <summary>版本文件是否完整（json 与 jar 都在），未装完的版本不应出现在启动列表。</summary>
    public bool IsComplete(string dir) => File.Exists(JsonPath(dir)) && File.Exists(JarPath(dir));

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

    /// <summary>最终是否隔离：global 跟随全局策略；旧配置（Isolation=true 且未写过 mode）仍按隔离处理。</summary>
    public bool IsIsolated(string dir)
    {
        return IsolationMode switch
        {
            "on" => true,
            "off" => false,
            "global" => Isolation || GlobalPolicyIsolates(dir),
            _ => Isolation,
        };
    }

    /// <summary>全局隔离策略是否要求隔离本版本。</summary>
    private bool GlobalPolicyIsolates(string dir)
    {
        switch (AppSettings.VersionIsolation)
        {
            case "modded":
                return DetectLoader(dir).Length > 0;
            case "unofficial":
                return !IsOfficialRelease(dir);
            case "modded_unofficial":
                return DetectLoader(dir).Length > 0 || !IsOfficialRelease(dir);
            case "all":
                return true;
            default:
                return false; // off（默认）
        }
    }

    /// <summary>版本 json 的 type 是否为 release（正式版）；读不到时按正式版处理，避免误隔离。</summary>
    private bool IsOfficialRelease(string dir)
    {
        try
        {
            string jsonPath = JsonPath(dir);
            if (!File.Exists(jsonPath)) return true;
            var json = JsonNode.Parse(File.ReadAllText(jsonPath))?.AsObject();
            string type = json?["type"]?.GetValue<string>() ?? "release";
            return type.Equals("release", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>隔离时游戏目录 = 版本目录，否则共享主目录。</summary>
    public string GameDir(string dir) => IsIsolated(dir) ? DirPath(dir) : McPaths.GameDir;

    /// <summary>从合并后的版本 json 识别已安装的 Mod 加载器（Fabric / Quilt / Forge / NeoForge），纯原版返回空串。</summary>
    public string DetectLoader(string dir)
    {
        string jsonPath = JsonPath(dir);
        if (!File.Exists(jsonPath)) return "";
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(jsonPath))?.AsObject();
            var libraries = json?["libraries"]?.AsArray();
            string mainClass = json?["mainClass"]?.GetValue<string>() ?? "";

            if (libraries != null)
            {
                // 优先用明确的加载器坐标，能带上版本号
                foreach (var node in libraries)
                {
                    string name = node?["name"]?.GetValue<string>() ?? "";
                    if (name.StartsWith("net.fabricmc:fabric-loader:", StringComparison.OrdinalIgnoreCase))
                        return "Fabric " + LastCoordinatePart(name);
                    if (name.StartsWith("org.quiltmc:quilt-loader:", StringComparison.OrdinalIgnoreCase))
                        return "Quilt " + LastCoordinatePart(name);
                    if (name.StartsWith("net.neoforged:neoforge:", StringComparison.OrdinalIgnoreCase))
                        return "NeoForge " + LastCoordinatePart(name);
                    if (name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase))
                        return "Forge " + LastCoordinatePart(name);
                    // OptiFine / LiteLoader 也走 launchwrapper，先按库坐标识别，避免被认成 Forge
                    if (name.StartsWith("optifine:OptiFine:", StringComparison.OrdinalIgnoreCase))
                        return "OptiFine " + LastCoordinatePart(name);
                    if (name.StartsWith("com.mumfrey:liteloader:", StringComparison.OrdinalIgnoreCase))
                        return "LiteLoader " + LastCoordinatePart(name);
                }
                // 现代 Forge/NeoForge 可能只带 fmlloader / fancymodloader 等坐标
                foreach (var node in libraries)
                {
                    string name = node?["name"]?.GetValue<string>() ?? "";
                    if (name.StartsWith("net.neoforged:", StringComparison.OrdinalIgnoreCase))
                        return "NeoForge " + LastCoordinatePart(name);
                    if (name.StartsWith("net.minecraftforge:", StringComparison.OrdinalIgnoreCase))
                        return "Forge " + LastCoordinatePart(name);
                }
            }

            // 老版本 Forge 依赖 launchwrapper 作为主类
            if (mainClass.Contains("launchwrapper", StringComparison.OrdinalIgnoreCase)) return "Forge";
            if (mainClass.Contains("bootstraplauncher", StringComparison.OrdinalIgnoreCase)) return "Forge / NeoForge";
            return "";
        }
        catch
        {
            return "";
        }
    }

    private static string LastCoordinatePart(string coordinate)
    {
        int index = coordinate.LastIndexOf(':');
        return index >= 0 && index + 1 < coordinate.Length ? coordinate[(index + 1)..] : coordinate;
    }

    /// <summary>加载器类型（小写，供 Mod API 过滤）：fabric / quilt / forge / neoforge；原版返回空串。</summary>
    public string LoaderType(string dir)
    {
        string text = DetectLoader(dir);
        if (text.StartsWith("neoforge", StringComparison.OrdinalIgnoreCase)) return "neoforge";
        if (text.StartsWith("fabric", StringComparison.OrdinalIgnoreCase)) return "fabric";
        if (text.StartsWith("quilt", StringComparison.OrdinalIgnoreCase)) return "quilt";
        if (text.StartsWith("forge", StringComparison.OrdinalIgnoreCase)) return "forge";
        return "";
    }

    /// <summary>底层 MC 版本号：优先 inheritsFrom，其次版本 json 的 id。</summary>
    public string McVersion(string dir)
    {
        try
        {
            string jsonPath = JsonPath(dir);
            if (!File.Exists(jsonPath)) return Id;
            var json = JsonNode.Parse(File.ReadAllText(jsonPath))?.AsObject();
            return json?["inheritsFrom"]?.GetValue<string>()
                ?? json?["id"]?.GetValue<string>()
                ?? Id;
        }
        catch
        {
            return Id;
        }
    }

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
