using System.Net.Http;
using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>authlib-injector：第三方登录启动游戏时注入的 Java agent。</summary>
public static class AuthlibInjector
{
    private const string LatestUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";
    private const long MinSize = 100 * 1024;

    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MnyMC/1.0");
        return client;
    }

    public static string JarPath => Path.Combine(McPaths.GameDir, "runtimes", "authlib-injector.jar");

    /// <summary>确保本地有可用的 jar，返回其路径（已存在且大小正常时不再下载）。</summary>
    public static async Task<string> EnsureAsync(IProgress<McProgress>? progress, CancellationToken ct)
    {
        string path = JarPath;
        if (File.Exists(path) && new FileInfo(path).Length > MinSize)
        {
            return path;
        }

        progress?.Report(new McProgress("获取 authlib-injector 版本", 0, 0, 0, 0));
        string url;
        try
        {
            using var response = await Http.GetAsync(LatestUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"获取 authlib-injector 版本失败（HTTP {(int)response.StatusCode}）");
            }
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            url = json?["download_url"]?.GetValue<string>() ?? "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("获取 authlib-injector 版本失败，请检查网络后重试", ex);
        }
        if (url.Length == 0)
        {
            throw new InvalidOperationException("获取 authlib-injector 下载地址失败");
        }

        // 残缺的旧文件先删掉，否则下载器会认为文件已存在而跳过
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉就让下载报错，提示用户
        }

        try
        {
            await Downloader.DownloadManyAsync(new[] { new McDownloadTask(url, path) }, 1,
                "下载 authlib-injector", progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("下载 authlib-injector 失败，请检查网络后重试", ex);
        }
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("下载 authlib-injector 失败");
        }
        return path;
    }
}
