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
    /// <summary>任务开始时版本目录是否为本次新建（失败/取消时据此清理残留）。</summary>
    public bool CreatedDir { get; init; }
    public string StatusText { get; set; } = "排队中...";
    public bool Done { get; set; }
    public string? Error { get; set; }
    /// <summary>是否被用户取消。</summary>
    public bool Cancelled { get; set; }
    /// <summary>MOD 入库下载任务：VersionId 存 Mod 标题，InstallName 为空。</summary>
    public bool IsMod { get; init; }
    /// <summary>进度 0~1（MOD 任务用，版本任务看 StatusText 里的文件计数）。</summary>
    public double Fraction { get; set; }
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

    /// <summary>同一个 Mod 是否已有入库/安装任务在跑（避免重复点下载生成两个任务）。</summary>
    public static bool IsModBusy(string title)
    {
        lock (Gate) return JobsList.Any(j => !j.Done && j.IsMod && j.VersionId == title);
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
            CreatedDir = !Directory.Exists(VersionMeta.DirPath(installName)),
        };
        // 先写元数据，启动页立即可见（下载中置灰）
        new VersionMeta { Id = versionId }.Save(installName);
        lock (Gate) JobsList.Add(job);
        JobsChanged?.Invoke(null, EventArgs.Empty);
        _ = Task.Run(() => RunJobAsync(job, mirror));
        return job;
    }

    /// <summary>启动一个 MOD 入库下载任务：与版本任务一样出现在下载任务页和标题栏指示器。</summary>
    /// <param name="title">Mod 名（显示用）。</param>
    /// <param name="body">任务体；内部用 <see cref="Report"/> 更新进度。</param>
    public static DownloadJob StartMod(string title, Func<DownloadJob, CancellationToken, Task> body)
    {
        var job = new DownloadJob
        {
            VersionId = title,
            InstallName = "",
            Addon = McAddon.None,
            IsMod = true,
            Cts = new CancellationTokenSource(),
        };
        lock (Gate) JobsList.Add(job);
        JobsChanged?.Invoke(null, EventArgs.Empty);
        _ = Task.Run(async () =>
        {
            try
            {
                await body(job, job.Cts.Token);
                job.Done = true;
                job.Fraction = 1;
            }
            catch (OperationCanceledException)
            {
                job.Done = true;
                job.Cancelled = true;
                job.StatusText = "已取消";
            }
            catch (Exception ex)
            {
                job.Done = true;
                job.Error = ex.Message;
                job.StatusText = $"失败: {ex.Message}";
            }
            JobsChanged?.Invoke(null, EventArgs.Empty);
        });
        return job;
    }

    /// <summary>任务体内更新进度（任意线程可调用）。</summary>
    public static void Report(DownloadJob job, string status, double fraction)
    {
        job.StatusText = status;
        job.Fraction = fraction;
        JobsChanged?.Invoke(null, EventArgs.Empty);
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
            GameLanguages.Invalidate(); // 新版本带来新的语言列表
        }
        catch (OperationCanceledException)
        {
            CleanupFailedInstall(job);
            job.Done = true;
            job.Cancelled = true;
            job.StatusText = "已取消";
        }
        catch (Exception ex)
        {
            CleanupFailedInstall(job);
            job.Done = true;
            job.Error = ex.Message;
            job.StatusText = $"失败: {ex.Message}";
        }
        JobsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>失败/取消时删除本次新建的版本目录，避免启动页出现装了一半的版本。</summary>
    private static void CleanupFailedInstall(DownloadJob job)
    {
        if (!job.CreatedDir) return;
        try
        {
            string dir = VersionMeta.DirPath(job.InstallName);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch { /* 删不掉就留着，下次下载会覆盖 */ }
    }
}
