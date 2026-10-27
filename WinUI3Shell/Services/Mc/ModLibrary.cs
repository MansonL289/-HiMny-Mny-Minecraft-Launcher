using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace WinUI3Shell.Services.Mc;

/// <summary>库里某个 Mod 的一份文件（对应一个 加载器 + MC 版本 组合）。</summary>
public sealed class ModLibraryFile
{
    public string Loader { get; set; } = "";
    public string McVersion { get; set; } = "";
    /// <summary>Modrinth 版本 id，安装时据此解析依赖。</summary>
    public string VersionId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>库里的一个 Mod：一个项目可能有多份不同加载器/版本的文件。</summary>
public sealed class ModLibraryEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    /// <summary>Mod 图标地址（Modrinth CDN），没有就显示占位图标。</summary>
    public string IconUrl { get; set; } = "";
    public List<ModLibraryFile> Files { get; set; } = new();
}

/// <summary>MOD 库索引：modlibrary/library.json，文件按项目分目录存放。</summary>
public static class ModLibrary
{
    public const string IndexFileName = "library.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string IndexPath => Path.Combine(McPaths.ModLibraryDir, IndexFileName);

    /// <summary>某个 Mod 的文件目录。</summary>
    public static string DirFor(string projectId) => Path.Combine(McPaths.ModLibraryDir, projectId);

    public static List<ModLibraryEntry> Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new();
            return JsonSerializer.Deserialize<List<ModLibraryEntry>>(File.ReadAllText(IndexPath), JsonOptions)
                ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static void Save(List<ModLibraryEntry> entries)
    {
        Directory.CreateDirectory(McPaths.ModLibraryDir);
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(entries, JsonOptions));
    }

    public static ModLibraryEntry? Find(List<ModLibraryEntry> entries, string projectId) =>
        entries.FirstOrDefault(e => e.Id.Equals(projectId, StringComparison.OrdinalIgnoreCase));

    /// <summary>取该 Mod 在指定加载器 + MC 版本下的文件记录。</summary>
    public static ModLibraryFile? FindFile(ModLibraryEntry entry, string loader, string mcVersion) =>
        entry.Files.FirstOrDefault(f =>
            f.Loader.Equals(loader, StringComparison.OrdinalIgnoreCase)
            && f.McVersion.Equals(mcVersion, StringComparison.OrdinalIgnoreCase));

    /// <summary>写入（或更新）一个 Mod 的某份文件，返回最新的条目。
    /// 同加载器 + MC 版本只保留一份，旧文件名的 jar 会被删掉，避免库目录里堆积孤儿文件。</summary>
    public static ModLibraryEntry UpsertFile(string projectId, string name, string author, string iconUrl,
        ModLibraryFile file)
    {
        var entries = Load();
        var entry = Find(entries, projectId);
        if (entry == null)
        {
            entry = new ModLibraryEntry { Id = projectId };
            entries.Add(entry);
        }
        entry.Name = name;
        entry.Author = author;
        if (!string.IsNullOrEmpty(iconUrl)) entry.IconUrl = iconUrl;

        var replaced = entry.Files
            .Where(f => f.Loader.Equals(file.Loader, StringComparison.OrdinalIgnoreCase)
                && f.McVersion.Equals(file.McVersion, StringComparison.OrdinalIgnoreCase)
                && !f.FileName.Equals(file.FileName, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FileName)
            .ToList();
        entry.Files.RemoveAll(f =>
            f.Loader.Equals(file.Loader, StringComparison.OrdinalIgnoreCase)
            && f.McVersion.Equals(file.McVersion, StringComparison.OrdinalIgnoreCase));
        entry.Files.Insert(0, file);
        Save(entries);

        foreach (string old in replaced)
        {
            try
            {
                string path = Path.Combine(DirFor(projectId), old);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* 占用时留待下次覆盖 */ }
        }
        return entry;
    }

    /// <summary>从库里彻底删除一个 Mod（索引 + 文件）。</summary>
    public static void Remove(string projectId)
    {
        var entries = Load();
        if (entries.RemoveAll(e => e.Id.Equals(projectId, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save(entries);
        }
        try
        {
            string dir = DirFor(projectId);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { /* 文件占用时留待下次覆盖 */ }
    }
}

/// <summary>检查本地 Mod jar 是否带中文语言文件（任意命名空间下 lang/zh_*.json 或 zh_*.lang）。
/// 有则游戏语言为中文时 Mod 界面自动显示中文。</summary>
public static class ModLang
{
    public static bool SupportsChinese(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            foreach (var entry in zip.Entries)
            {
                string full = entry.FullName.Replace('\\', '/');
                int i = full.LastIndexOf("lang/", StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;
                string name = full[(i + "lang/".Length)..];
                if (!name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".lang", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
