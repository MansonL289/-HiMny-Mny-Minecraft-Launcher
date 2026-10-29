namespace WinUI3Shell.Services.Mc;

/// <summary>启动前的 Mod 冲突检测：只做确定性强、误报低的检查。</summary>
public static class ModConflict
{
    /// <summary>检查某版本已启用的 Mod，返回中文问题列表；没有问题时返回空列表。</summary>
    public static List<string> Check(string versionDir)
    {
        var meta = VersionMeta.Load(versionDir);
        var result = new List<string>();
        if (meta == null) return result;

        string modsDir = Path.Combine(meta.GameDir(versionDir), "mods");
        var mods = ModRegistry.Load(modsDir).Where(m => m.Enabled).ToList();
        if (mods.Count == 0) return result;

        string loader = meta.LoaderType(versionDir);
        string mcVersion = meta.McVersion(versionDir);
        // 只比 主版本.次版本，避免 1.20.1 与 1.20.4 被误判（同一大版本通常通用）
        string mcSeries = Series(mcVersion);

        // 1) 重复安装：同一 mod 的多个版本文件 / 同名文件
        foreach (var group in mods.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            result.Add($"「{group.First().Name}」安装了 {group.Count()} 份，只保留一个即可");
        }
        foreach (var group in mods.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            if (group.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                result.Add($"同名 Mod「{group.Key}」出现 {group.Count()} 次，可能重复安装");
            }
        }

        // 2) 加载器不匹配（Mod 标注了加载器且与版本不一致）
        if (loader.Length > 0)
        {
            foreach (var mod in mods)
            {
                if (mod.Loader.Length > 0 && !mod.Loader.Equals(loader, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add($"「{mod.Name}」是 {mod.Loader} 版，但当前版本是 {loader}");
                }
            }
        }

        // 3) 游戏版本不匹配（只比大版本号）
        foreach (var mod in mods)
        {
            if (mod.McVersion.Length > 0 && mcSeries.Length > 0
                && Series(mod.McVersion) != mcSeries)
            {
                result.Add($"「{mod.Name}」标注的是 {mod.McVersion}，当前游戏是 {mcVersion}");
            }
        }

        // 4) 已知互斥组合：OptiFine 与 Sodium/Iris 同时启用会崩
        bool hasOptiFine = mods.Any(m => m.Id.Contains("optifine", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("OptiFine", StringComparison.OrdinalIgnoreCase));
        var sodium = mods.FirstOrDefault(m => m.Id.Contains("sodium", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Sodium", StringComparison.OrdinalIgnoreCase));
        if (hasOptiFine && sodium != null)
        {
            result.Add("OptiFine 与 Sodium 不能同时使用，请禁用其中一个");
        }

        // 5) Fabric 版本缺少 Fabric API 前置
        if (loader == "fabric"
            && !mods.Any(m => m.Id.Contains("fabric-api", StringComparison.OrdinalIgnoreCase)
                || m.File.Contains("fabric-api", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add("Fabric 版本建议安装 Fabric API，否则多数 Mod 无法加载");
        }

        // 去重后返回
        return result.Distinct().ToList();
    }

    /// <summary>取 1.20.1 → "1.20"；格式不认识时原样返回。</summary>
    private static string Series(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? parts[0] + "." + parts[1] : version;
    }
}
