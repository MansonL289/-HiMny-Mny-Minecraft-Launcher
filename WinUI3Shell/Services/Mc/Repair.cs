using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>单个版本的完整性检查结果。</summary>
public sealed class RepairReport
{
    public string Dir { get; init; } = "";
    /// <summary>中文问题描述（每条一行，可直接显示）。</summary>
    public List<string> Problems { get; } = new();
    public int MissingLibraries { get; set; }
    public int MissingAssets { get; set; }
    public bool JarMissing { get; set; }
    public bool NativesMissing { get; set; }
    public bool JsonMissing { get; set; }

    public bool NeedsRepair => Problems.Count > 0;

    /// <summary>一句话摘要，如「缺少 12 个依赖库、3 个资源文件」。</summary>
    public string SummaryText => NeedsRepair ? string.Join("、", Problems) : "文件完整，可以正常启动";
}

/// <summary>版本文件体检与幂等修复：只补齐缺失的依赖库 / 客户端 jar / 资源 / natives，
/// 不改 version.json（保留 Fabric/Forge 合并信息），不动 mods 与存档。</summary>
public static class Repair
{
    /// <summary>只检查不下载（纯本地文件判断）。异常一律转成 Problems，不向调用方抛出。</summary>
    public static RepairReport Check(string dir, string mirror)
    {
        var report = new RepairReport { Dir = dir };
        try
        {
            var meta = VersionMeta.Load(dir);
            if (meta == null)
            {
                report.Problems.Add("版本元数据缺失，无法检查");
                return report;
            }

            string jsonPath = meta.JsonPath(dir);
            if (!File.Exists(jsonPath))
            {
                report.JsonMissing = true;
                report.Problems.Add("版本 json 缺失");
                return report;
            }
            if (!File.Exists(meta.JarPath(dir)))
            {
                report.JarMissing = true;
                report.Problems.Add("客户端 jar 缺失");
            }

            JsonObject json;
            try
            {
                json = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
            }
            catch (Exception ex)
            {
                report.Problems.Add($"版本 json 无法解析（{ex.Message}）");
                return report;
            }

            try
            {
                var (tasks, _, natives) = Libraries.Collect(json, mirror);
                int missingLibs = tasks.Count(t => !File.Exists(t.Dest));
                if (missingLibs > 0)
                {
                    report.MissingLibraries = missingLibs;
                    report.Problems.Add($"缺少 {missingLibs} 个依赖库");
                }

                string nativesDir = Path.Combine(McPaths.VersionDir(dir), "natives");
                if (natives.Count > 0 &&
                    (!Directory.Exists(nativesDir) || !Directory.EnumerateFiles(nativesDir, "*.dll").Any()))
                {
                    report.NativesMissing = true;
                    report.Problems.Add("缺少运行库");
                }
            }
            catch (Exception ex)
            {
                report.Problems.Add($"依赖库清单无法解析（{ex.Message}）");
            }

            try
            {
                // 索引 id 取 assetIndex.id；老清单只有 assets 字段，则取 assets
                var indexNode = json["assetIndex"]?.AsObject();
                string? indexId = indexNode?["id"]?.GetValue<string>()
                    ?? json["assets"]?.GetValue<string>();
                // 仅检查能下载的现代索引：远古版本只有 assets 名、没有索引下载地址，
                // 本启动器不管理其资源文件，避免误报成「缺资源」
                if (indexNode?["url"] != null && !string.IsNullOrEmpty(indexId))
                {
                    string indexPath = Path.Combine(McPaths.AssetsDir, "indexes", indexId + ".json");
                    if (!File.Exists(indexPath))
                    {
                        report.MissingAssets = 1;
                        report.Problems.Add("资源索引缺失");
                    }
                    else
                    {
                        var index = JsonNode.Parse(File.ReadAllText(indexPath))?.AsObject();
                        if (index?["objects"] is JsonObject objects)
                        {
                            int missing = 0;
                            foreach (var kv in objects)
                            {
                                string hash = kv.Value?["hash"]?.GetValue<string>() ?? "";
                                if (hash.Length < 2 ||
                                    !File.Exists(Path.Combine(McPaths.AssetsDir, "objects", hash[..2], hash)))
                                {
                                    missing++;
                                }
                            }
                            if (missing > 0)
                            {
                                report.MissingAssets = missing;
                                report.Problems.Add($"缺少 {missing} 个资源文件");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report.Problems.Add($"资源索引无法解析（{ex.Message}）");
            }
        }
        catch (Exception ex)
        {
            report.Problems.Add($"检查失败（{ex.Message}）");
        }
        return report;
    }

    /// <summary>按检查结果补齐缺失文件；不写 version.json。</summary>
    public static async Task FixAsync(string dir, string mirror, IProgress<McProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new McProgress("检查版本文件", 0, 0, 0, 0));
        var meta = VersionMeta.Load(dir)
            ?? throw new InvalidOperationException("版本元数据缺失，无法修复。");
        string jsonPath = meta.JsonPath(dir);
        if (!File.Exists(jsonPath))
        {
            throw new InvalidOperationException("版本 json 缺失，需要用下载器重新安装该版本。");
        }

        JsonObject json;
        try
        {
            json = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath, ct))!.AsObject();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("版本 json 无法解析，需要用下载器重新安装该版本。", ex);
        }

        var (tasks, _, natives) = Libraries.Collect(json, mirror);
        if (tasks.Count > 0)
        {
            await Downloader.DownloadManyAsync(tasks, 16, "补齐依赖库", progress, ct);
        }

        string jarPath = meta.JarPath(dir);
        if (!File.Exists(jarPath))
        {
            var client = json["downloads"]?["client"]?.AsObject();
            string? url = client?["url"]?.GetValue<string>();
            if (url != null)
            {
                await Downloader.DownloadManyAsync(new[]
                {
                    new McDownloadTask(Mirror.Rewrite(url, mirror), jarPath,
                        client!["sha1"]?.GetValue<string>(), client!["size"]?.GetValue<long>()),
                }, 1, "补齐客户端 jar", progress, ct);
            }
            // 合并后的 json 没有 downloads.client：跳过，由界面提示「客户端 jar 需要重新下载版本」
        }

        string nativesDir = Path.Combine(McPaths.VersionDir(dir), "natives");
        if (natives.Count > 0 &&
            (!Directory.Exists(nativesDir) || !Directory.EnumerateFiles(nativesDir, "*.dll").Any()))
        {
            progress?.Report(new McProgress("解压运行库", 0, 0, 0, 0));
            await Task.Run(() => Libraries.ExtractNatives(natives, nativesDir), ct);
        }

        // 资产索引 + 资源文件（含老版本 virtual 资源的物化）
        if (json["assetIndex"]?["url"] != null)
        {
            await Assets.EnsureAsync(json, mirror, 16, progress, ct);
        }

        progress?.Report(new McProgress("修复完成", 1, 1, 1, 1));
    }
}
