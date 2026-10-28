using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>资源包 / 光影包条目。</summary>
public sealed class PackItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsFolder { get; set; }
    public long Size { get; set; }
    public bool Enabled { get; set; }
    /// <summary>true = 光影包，false = 资源包。</summary>
    public bool IsShader { get; set; }

    public string SizeText => Size >= 1024 * 1024
        ? $"{Size / 1024.0 / 1024.0:0.#} MB"
        : $"{Math.Max(1, Size / 1024)} KB";
    public string SubtitleText => IsFolder ? $"文件夹 · {SizeText}" : SizeText;
}

/// <summary>resourcepacks / shaderpacks 的读取、启用、导入、删除。</summary>
public static class Packs
{
    public static string ResourcePacksDir(string gameDir) => Path.Combine(gameDir, "resourcepacks");
    public static string ShaderPacksDir(string gameDir) => Path.Combine(gameDir, "shaderpacks");

    /// <summary>列出资源包（zip 或文件夹）。</summary>
    public static List<PackItem> List(string gameDir, bool shader)
    {
        string dir = shader ? ShaderPacksDir(gameDir) : ResourcePacksDir(gameDir);
        var items = new List<PackItem>();
        try
        {
            if (!Directory.Exists(dir)) return items;
            var enabledPacks = ReadEnabledResourcePacks(gameDir);
            string? enabledShader = shader ? ReadEnabledShader(gameDir) : null;

            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                bool isFolder = (entry.Attributes & FileAttributes.Directory) != 0;
                if (!isFolder && !entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                // 光影只认 zip（文件夹形式游戏不识别）
                if (shader && isFolder) continue;

                items.Add(new PackItem
                {
                    Name = entry.Name,
                    Path = entry.FullName,
                    IsFolder = isFolder,
                    Size = isFolder ? DirSize(entry.FullName) : ((FileInfo)entry).Length,
                    IsShader = shader,
                    Enabled = shader
                        ? string.Equals(enabledShader, entry.Name, StringComparison.OrdinalIgnoreCase)
                        : enabledPacks.Contains("file/" + entry.Name),
                });
            }
        }
        catch { /* 读不到就返回已有部分 */ }
        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return items;
    }

    /// <summary>启用/停用资源包或光影。</summary>
    public static void SetEnabled(string gameDir, PackItem item, bool on)
    {
        if (item.IsShader)
        {
            WriteShader(gameDir, on ? item.Name : "OFF");
            return;
        }

        var list = ReadEnabledResourcePacks(gameDir);
        string key = "file/" + item.Name;
        if (on) list.Add(key);
        else list.Remove(key);
        WriteResourcePacks(gameDir, list);
    }

    /// <summary>把文件复制到对应目录（同名自动加序号）。返回新路径。</summary>
    public static string Import(string gameDir, bool shader, string filePath)
    {
        string dir = shader ? ShaderPacksDir(gameDir) : ResourcePacksDir(gameDir);
        Directory.CreateDirectory(dir);
        string name = Path.GetFileName(filePath);
        string baseName = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        string dest = Path.Combine(dir, name);
        for (int i = 2; File.Exists(dest) || Directory.Exists(dest); i++)
        {
            dest = Path.Combine(dir, $"{baseName} ({i}){ext}");
        }
        File.Copy(filePath, dest);
        return dest;
    }

    public static void Delete(PackItem item)
    {
        if (item.IsFolder) Directory.Delete(item.Path, true);
        else File.Delete(item.Path);
    }

    // ---------- options.txt ----------

    private static List<string> ReadEnabledResourcePacks(string gameDir)
    {
        var result = new List<string>();
        string? value = ReadOption(gameDir, "resourcePacks");
        if (value == null) return result;
        try
        {
            if (JsonNode.Parse(value) is JsonArray arr)
            {
                foreach (var node in arr)
                {
                    if (node?.GetValue<string>() is string s) result.Add(s);
                }
            }
        }
        catch { /* 解析失败按空处理 */ }
        return result;
    }

    private static void WriteResourcePacks(string gameDir, List<string> packs)
    {
        var array = new JsonArray();
        foreach (string p in packs.Distinct()) array.Add(JsonValue.Create(p));
        WriteOption(gameDir, "resourcePacks", array.ToJsonString());
    }

    private static string? ReadEnabledShader(string gameDir)
    {
        string? value = ReadOption(gameDir, "shaderPack");
        if (string.IsNullOrWhiteSpace(value) || value == "OFF" || value == "(internal)") return null;
        return value;
    }

    private static void WriteShader(string gameDir, string value)
    {
        WriteOption(gameDir, "shaderPack", value);

        // Iris 把光影选择存在 config/iris.properties，存在才同步
        string iris = Path.Combine(gameDir, "config", "iris.properties");
        if (File.Exists(iris))
        {
            try
            {
                var lines = File.ReadAllLines(iris).ToList();
                int i = lines.FindIndex(l => l.StartsWith("shaderPack=", StringComparison.Ordinal));
                if (i >= 0) lines[i] = "shaderPack=" + (value == "OFF" ? "" : value);
                else lines.Add("shaderPack=" + (value == "OFF" ? "" : value));
                File.WriteAllLines(iris, lines);
            }
            catch { /* 写不进就只改 options.txt */ }
        }
    }

    private static string? ReadOption(string gameDir, string key)
    {
        string path = Path.Combine(gameDir, "options.txt");
        try
        {
            if (!File.Exists(path)) return null;
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith(key + ":", StringComparison.Ordinal))
                {
                    return line[(key.Length + 1)..];
                }
            }
        }
        catch { /* 读不到视为没有 */ }
        return null;
    }

    private static void WriteOption(string gameDir, string key, string value)
    {
        string path = Path.Combine(gameDir, "options.txt");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        int i = lines.FindIndex(l => l.StartsWith(key + ":", StringComparison.Ordinal));
        if (i >= 0) lines[i] = key + ":" + value;
        else lines.Add(key + ":" + value);
        File.WriteAllLines(path, lines);
    }

    private static long DirSize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }
}
