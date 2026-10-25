namespace WinUI3Shell.Services.Mc;

/// <summary>下载时附加的模块。</summary>
public enum McAddon
{
    None,
    Fabric,
    Forge,
    NeoForge,
}

/// <summary>一个后台下载任务（进程式：与页面无关，全局可见）。</summary>
public sealed class DownloadJob
{
    public required string VersionId { get; init; }
    /// <summary>安装目录名（版本名），默认与 VersionId 相同。</summary>
    public required string InstallName { get; init; }
    public required McAddon Addon { get; init; }
    /// <summary>手动指定的加载器版本号；空则自动选最新。</summary>
    public string? LoaderVersion { get; init; }
    public required CancellationTokenSource Cts { get; init; }
    public string StatusText { get; set; } = "排队中...";
    public bool Done { get; set; }
    public string? Error { get; set; }
    /// <summary>加载器后缀显示文本（如 " + Forge"）。</summary>
    public string AddonText => Addon switch
    {
        McAddon.Fabric => " + Fabric",
        McAddon.Forge => " + Forge",
        McAddon.NeoForge => " + NeoForge",
        _ => "",
    };
}

/// <summary>全局下载管理器：支持多个版本同时下载，切换页面不影响任务。</summary>
public static class DownloadManager
{
    private static readonly List<DownloadJob> JobsList = new();
    private static readonly object Gate = new();

    /// <summary>任务列表或状态变化时触发（可能在后台线程，UI 侧需切回 UI 线程）。</summary>
    public static event EventHandler? JobsChanged;

    public static IReadOnlyList<DownloadJob> Jobs
    {
        get { lock (Gate) return JobsList.ToArray(); }
    }

    public static bool IsDownloading(string versionId)
    {
        lock (Gate) return JobsList.Any(j => !j.Done && j.VersionId == versionId);
    }

    /// <summary>是否有同名（版本名）或同版本的活跃任务。</summary>
    public static bool IsNameOrVersionBusy(string installName, string versionId)
    {
        lock (Gate) return JobsList.Any(j => !j.Done && (j.InstallName == installName || j.VersionId == versionId));
    }

    /// <summary>清除所有已结束（完成/失败/取消）的任务。</summary>
    public static void ClearFinished()
    {
        lock (Gate) JobsList.RemoveAll(j => j.Done);
        JobsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>启动一个后台下载任务：原版文件 + 可选附加模块，安装到 versions/&lt;installName&gt;。</summary>
    public static DownloadJob Start(string versionId, string installName, string mirror, McAddon addon,
        string? loaderVersion = null)
    {
        var job = new DownloadJob
        {
            VersionId = versionId,
            InstallName = installName,
            Addon = addon,
            LoaderVersion = loaderVersion,
            Cts = new CancellationTokenSource(),
        };
        // 先写元数据，启动页立即可见（下载中置灰）
        new VersionMeta { Id = versionId }.Save(installName);
        lock (Gate) JobsList.Add(job);
        JobsChanged?.Invoke(null, EventArgs.Empty);
        _ = Task.Run(() => RunJobAsync(job, mirror));
        return job;
    }

    private static async Task RunJobAsync(DownloadJob job, string mirror)
    {
        var progress = new Progress<McProgress>(p =>
        {
            job.StatusText = p.TotalFiles > 0 ? $"{p.Phase} ({p.DoneFiles}/{p.TotalFiles})" : p.Phase;
            JobsChanged?.Invoke(null, EventArgs.Empty);
        });
        try
        {
            var versionJson = await Installer.PrepareAsync(job.VersionId, mirror, 16, progress, job.Cts.Token, job.InstallName);

            if (job.Addon == McAddon.Fabric)
            {
                job.StatusText = "安装 Fabric Loader...";
                JobsChanged?.Invoke(null, EventArgs.Empty);
                await LoaderInstaller.InstallFabricAsync(job.VersionId, job.Cts.Token, job.InstallName, job.LoaderVersion);
            }
            if (job.Addon == McAddon.Forge)
            {
                job.StatusText = "安装 Forge...";
                JobsChanged?.Invoke(null, EventArgs.Empty);
                await ForgeInstaller.InstallForgeAsync(job.VersionId, versionJson, mirror, progress, job.Cts.Token, job.InstallName, job.LoaderVersion);
            }
            if (job.Addon == McAddon.NeoForge)
            {
                job.StatusText = "安装 NeoForge...";
                JobsChanged?.Invoke(null, EventArgs.Empty);
                await ForgeInstaller.InstallNeoForgeAsync(job.VersionId, versionJson, mirror, progress, job.Cts.Token, job.InstallName, job.LoaderVersion);
            }

            job.Done = true;
            job.StatusText = "下载完成";
        }
        catch (OperationCanceledException)
        {
            job.Done = true;
            job.StatusText = "已取消";
        }
        catch (Exception ex)
        {
            job.Done = true;
            job.Error = ex.Message;
            job.StatusText = $"失败: {ex.Message}（重新开始可断点续传）";
        }
        JobsChanged?.Invoke(null, EventArgs.Empty);
    }
}
