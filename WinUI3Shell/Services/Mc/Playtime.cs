using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WinUI3Shell.Services.Mc;

/// <summary>游戏时长统计：playtime.json 记录每个版本目录的累计秒数与上次启动时间。</summary>
public static class Playtime
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class Entry
    {
        public long Seconds { get; set; }
        public DateTimeOffset? LastPlayed { get; set; }
    }

    public static string FilePath => Path.Combine(McPaths.GameDir, "playtime.json");

    /// <summary>累计游戏时长（秒）。</summary>
    public static long GetSeconds(string versionDir)
    {
        lock (Gate)
        {
            return Load().TryGetValue(versionDir, out var entry) ? entry.Seconds : 0;
        }
    }

    /// <summary>上次启动时间（本地时间）；没有记录返回 null。</summary>
    public static DateTime? GetLastPlayed(string versionDir)
    {
        lock (Gate)
        {
            return Load().TryGetValue(versionDir, out var entry) ? entry.LastPlayed?.LocalDateTime : null;
        }
    }

    /// <summary>累加一次游玩时长并记录本次启动时间。</summary>
    public static void Add(string versionDir, long seconds)
    {
        if (string.IsNullOrWhiteSpace(versionDir) || seconds <= 0) return;
        lock (Gate)
        {
            var map = Load();
            if (!map.TryGetValue(versionDir, out var entry))
            {
                entry = new Entry();
                map[versionDir] = entry;
            }
            entry.Seconds += seconds;
            entry.LastPlayed = DateTimeOffset.Now;
            Save(map);
        }
    }

    /// <summary>把秒数格式化成「2 小时 5 分」/「3 分」/「45 秒」。</summary>
    public static string FormatSeconds(long seconds)
    {
        if (seconds <= 0) return "0 秒";
        if (seconds < 60) return $"{seconds} 秒";
        long minutes = seconds / 60;
        if (minutes < 60) return $"{minutes} 分";
        long hours = minutes / 60;
        long rest = minutes % 60;
        return rest == 0 ? $"{hours} 小时" : $"{hours} 小时 {rest} 分";
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonOptions);
            return map == null
                ? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Entry>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // 文件损坏时按空记录处理，不影响启动
            return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save(Dictionary<string, Entry> map)
    {
        try
        {
            Directory.CreateDirectory(McPaths.GameDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map, JsonOptions));
        }
        catch { /* 写入失败忽略，统计数据不是关键数据 */ }
    }
}
