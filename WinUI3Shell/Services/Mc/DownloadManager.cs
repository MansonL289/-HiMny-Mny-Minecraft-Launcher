namespace WinUI3Shell.Services.Mc;

/// <summary>下载时附加的模块。</summary>
public enum McAddon
{
    None,
    Fabric,
    Forge,
    NeoForge,
    Quilt,
}

/// <summary>下载任务的种类：决定完成后放到库的哪里、能不能「安装」。</summary>
public enum DownloadKind
{
    /// <summary>游戏版本（原版 + 可选加载器）。</summary>
    Version,
    /// <summary>MOD 文件（下载进 MOD 库）。</summary>
    Mod,
    /// <summary>整合包（安装成一个版本）。</summary>
    Modpack,
    /// <summary>加载器安装器文件（下载进库的「安装器」）。</summary>
    Installer,
    /// <summary>普通文件（如服务端 jar）：下载进库，不需要安装。</summary>
    File,
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
    public DownloadKind Kind { get; init; } = DownloadKind.Version;
    /// <summary>MOD 任务：Modrinth 项目 id（完成后按它从库里取条目）。</summary>
    public string ModProjectId { get; init; } = "";
    /// <summary>这个任务是「安装」动作（把已下载的内容装进版本），不再提示安装。</summary>
    public bool IsInstallAction { get; init; }
    /// <summary>安装器任务：加载器种类（forge/fabric/neoforge/quilt）。</summary>
    public string InstallerLoader { get; init; } = "";
    /// <summary>安装器任务：绑定的游戏版本（为空表示任意版本均可安装）。</summary>
    public string InstallerMc { get; init; } = "";
    /// <summary>安装器任务：安装器版本号（传给加载器安装流程）。</summary>
    public string? InstallerVersion { get; init; }
    /// <summary>安装器任务：下载完成后库里的 jar 路径。</summary>
    public string InstallerPath { get; set; } = "";
    /// <summary>文件任务（服务端等）：下载完成后库里的文件路径。</summary>
    public string LocalPath { get; set; } = "";
    /// <summary>进度 0~1（MOD / 安装器任务用，版本任务看 StatusText 里的文件计数）。</summary>
    public double Fraction { get; set; }
    public ModpackInfo? Pack { get; init; }
    public bool IsMod => Kind == DownloadKind.Mod;
    public bool IsInstaller => Kind == DownloadKind.Installer;
    public bool IsModpack => Kind == DownloadKind.Modpack;
    /// <summary>加载器后缀显示文本（如 " + Forge"）。</summary>
    public string AddonText => Kind == DownloadKind.Modpack ? " · 整合包" : Addon switch
    {
        McAddon.Fabric => " + Fabric",
        McAddon.Forge => " + Forge",
        McAddon.NeoForge => " + NeoForge",
        McAddon.Quilt => " + Quilt",
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

    /// <summary>启动一个整合包安装任务（游戏本体 + 加载器 + 整合包文件）。</summary>
    public static DownloadJob StartModpack(ModpackInfo pack, string installName, string mirror)
    {
        var job = new DownloadJob
        {
            VersionId = pack.McVersion,
            InstallName = installName,
            Addon = McAddon.None,
            Kind = DownloadKind.Modpack,
            Pack = pack,
            Cts = new CancellationTokenSource(),
            CreatedDir = !Directory.Exists(VersionMeta.DirPath(installName)),
        };
        new VersionMeta { Id = pack.McVersion, IsolationMode = "on" }.Save(installName);
        lock (Gate) JobsList.Add(job);
        JobsChanged?.Invoke(null, EventArgs.Empty);
        _ = Task.Run(() => RunJobAsync(job, mirror));
        return job;
    }

    /// <summary>启动一个 MOD 入库下载任务：与版本任务一样出现在下载任务页和标题栏指示器。</summary>
    /// <param name="title">Mod 名（显示用）。</param>
    /// <param name="body">任务体；内部用 <see cref="Report"/> 更新进度。</param>
    /// <param name="projectId">Modrinth 项目 id（完成后「安装」按它取库条目）。</param>
    /// <param name="isInstall">是否「安装」动作（安装完成不再提示安装）。</param>
    /// <param name="kind">任务种类（安装类任务可传 <see cref="DownloadKind.Installer"/>）。</param>
    public static DownloadJob StartMod(string title, Func<DownloadJob, CancellationToken, Task> body,
        string projectId = "", bool isInstall = false, DownloadKind kind = DownloadKind.Mod)
    {
        var job = new DownloadJob
        {
            VersionId = title,
            InstallName = "",
            Addon = McAddon.None,
            Kind = kind,
            ModProjectId = projectId,
            IsInstallAction = isInstall,
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

    /// <summary>启动一个「安装器」下载任务：jar 下载进库的安装器目录，完成后可一键安装到适配版本。</summary>
    /// <param name="loader">加载器种类：forge / fabric / neoforge / quilt。</param>
    /// <param name="mcVersion">绑定游戏版本；空表示任意版本可用。</param>
    /// <param name="displayName">下载任务里显示的名字（如 "Forge 47.2.0"）。</param>
    public static DownloadJob StartInstaller(string loader, string mcVersion, string displayName,
        string url, string fileName, string? loaderVersion = null)
    {
        // 同一个安装器已在下载中：直接复用那个任务，避免两个任务写同一个文件
        lock (Gate)
        {
            var running = JobsList.FirstOrDefault(j => !j.Done && j.Kind == DownloadKind.Installer
                && j.InstallerLoader == loader && j.VersionId == displayName);
            if (running != null) return running;
        }

        var job = new DownloadJob
        {
            VersionId = displayName,
            InstallName = fileName,
            Addon = McAddon.None,
            Kind = DownloadKind.Installer,
            InstallerLoader = loader,
            InstallerMc = mcVersion,
            InstallerVersion = loaderVersion,
            Cts = new CancellationTokenSource(),
        };
        lock (Gate) JobsList.Add(job);
        JobsChanged?.Invoke(null, EventArgs.Empty);
        _ = Task.Run(async () =>
        {
            try
            {
                string dir = Path.Combine(McPaths.InstallersDir, loader);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, fileName);
                Report(job, $"下载安装器：{fileName}", 0);
                await Downloader.DownloadManyAsync(
                    new List<McDownloadTask> { new(url, path) }, 1, fileName, null, job.Cts.Token);
                job.InstallerPath = path;
                job.Done = true;
                job.Fraction = 1;
                job.StatusText = "已放入库";
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

    /// <summary>启动一个普通文件下载任务（如服务端 jar）：下载进库的指定子目录，完成后只提示「查看」。</summary>
    /// <param name="title">显示名（如 "服务端 1.20.1"）。</param>
    /// <param name="subDir">库内子目录（如 server）。</param>
    public static DownloadJob StartFile(string title, string url, string fileName, string subDir,
        string? sha1 = null, long size = 0)
    {
        // 同一个文件已在下载中：直接复用那个任务
        lock (Gate)
        {
            var running = JobsList.FirstOrDefault(j => !j.Done && j.Kind == DownloadKind.File
                && j.InstallName == fileName);
            if (running != null) return running;
        }

        var job = new DownloadJob
        {
            VersionId = title,
            InstallName = fileName,
            Addon = McAddon.None,
            Kind = DownloadKind.File,
            Cts = new CancellationTokenSource(),
        };
        lock (Gate) JobsList.Add(job);
        JobsChanged?.Invoke(null, EventArgs.Empty);
        _ = Task.Run(async () =>
        {
            try
            {
                string dir = Path.Combine(McPaths.GameDir, subDir);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, fileName);
                Report(job, $"下载：{fileName}", 0);
                await Downloader.DownloadManyAsync(
                    new List<McDownloadTask> { new(url, path, sha1, size) }, 1, fileName, null, job.Cts.Token);
                job.LocalPath = path;
                job.Done = true;
                job.Fraction = 1;
                job.StatusText = "已放入库";
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

    /// <summary>任务在当前界面的显示名（「你的 XXX 下载完毕」用）。</summary>
    public static string DisplayName(DownloadJob job) => job.Kind switch
    {
        DownloadKind.Mod => $"MOD「{job.VersionId}」",
        DownloadKind.Modpack => $"整合包「{job.InstallName}」",
        DownloadKind.Installer => $"安装器「{job.VersionId}」",
        DownloadKind.File => $"文件「{job.VersionId}」",
        _ => $"版本「{job.InstallName}{job.AddonText}」",
    };

    /// <summary>下载完成提示文字（应用内横幅与系统通知共用）。</summary>
    public static string BannerText(DownloadJob job) => job.Error != null
        ? $"{DisplayName(job)}失败：{job.Error}"
        : job.IsInstallAction ? $"{DisplayName(job)}安装完成" : $"你的{DisplayName(job)}下载完毕";

    private static async Task RunJobAsync(DownloadJob job, string mirror)
    {
        var progress = new Progress<McProgress>(p =>
        {
            job.StatusText = p.TotalFiles > 0 ? $"{p.Phase} ({p.DoneFiles}/{p.TotalFiles})" : p.Phase;
            JobsChanged?.Invoke(null, EventArgs.Empty);
        });
        try
        {
            // 整合包：游戏本体 + 加载器 + 整合包文件全部由 ModpackInstaller 完成
            if (job.Pack != null)
            {
                await ModpackInstaller.InstallAsync(job.Pack, job.InstallName, mirror, progress, job.Cts.Token);
                job.Done = true;
                job.StatusText = "整合包安装完成";
                GameLanguages.Invalidate();
                JobsChanged?.Invoke(null, EventArgs.Empty);
                return;
            }

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
            if (job.Addon == McAddon.Quilt)
            {
                job.StatusText = "安装 Quilt Loader...";
                JobsChanged?.Invoke(null, EventArgs.Empty);
                await LoaderInstaller.InstallQuiltAsync(job.VersionId, job.Cts.Token, job.InstallName, job.LoaderVersion);
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
