using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>离线账号、启动命令拼接（新旧两种 arguments 格式）、进程启动与日志收集。</summary>
public static class LaunchService
{
    /// <summary>与 Java UUID.nameUUIDFromBytes("OfflinePlayer:&lt;name&gt;") 一致。</summary>
    public static string OfflineUuid(string name)
    {
        byte[] md5 = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        md5[6] = (byte)((md5[6] & 0x0f) | 0x30); // version 3
        md5[8] = (byte)((md5[8] & 0x3f) | 0x80); // variant
        string hex = Convert.ToHexString(md5).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static string Sub(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (k, v) in values)
            text = text.Replace("${" + k + "}", v);
        return text;
    }

    /// <summary>展开 arguments 数组：字符串直接替换变量，对象先过 rules（可带 features）再替换。</summary>
    private static List<string> Expand(JsonArray? entries, IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, bool>? features = null)
    {
        var output = new List<string>();
        if (entries == null) return output;
        foreach (var node in entries)
        {
            if (node is JsonValue)
            {
                output.Add(Sub(node!.GetValue<string>(), values));
            }
            else if (node is JsonObject obj && McRules.Allow(obj["rules"]?.AsArray(), features))
            {
                var v = obj["value"];
                if (v is JsonArray arr)
                    output.AddRange(arr.Select(x => Sub(x!.GetValue<string>(), values)));
                else
                    output.Add(Sub(v!.GetValue<string>(), values));
            }
        }
        return output;
    }

    /// <summary>按 version.json 的 mainClass/arguments 拼接完整启动命令。
    /// gameDir 即 ${game_directory} 与进程工作目录；windowWidth/Height 为游戏窗口大小。
    /// account 为空或离线账号时行为与旧版本一致；第三方账号需要通过 authlibAgent 注入验证服务器。</summary>
    public static List<string> BuildCommand(JsonObject versionJson, string java, string player,
        string maxMem, string gameDir, int windowWidth = 0, int windowHeight = 0,
        McAccount? account = null, string? authlibAgent = null)
    {
        string id = versionJson["id"]!.GetValue<string>();
        string vdir = McPaths.VersionDir(id);
        string nativesDir = Path.Combine(vdir, "natives");

        // 重新计算 classpath（纯解析，不触发下载），client.jar 放最后
        var (_, classpath, _) = Libraries.Collect(versionJson, Mirror.Official);
        classpath.Add(Path.Combine(vdir, id + ".jar"));
        string classpathStr = string.Join(';', classpath);

        string assetsIndex = versionJson["assets"]?.GetValue<string>()
            ?? versionJson["assetIndex"]?["id"]?.GetValue<string>() ?? "legacy";

        // 正版 / 第三方账号带真实令牌；离线账号沿用旧的离线 UUID 与 "0" 令牌
        bool online = account != null && account.Type is "microsoft" or "yggdrasil";
        string playerName = online ? account!.Name : player;
        string uuid = online ? account!.Uuid : OfflineUuid(player);
        string token = online ? account!.AccessToken : "0";
        string userType = account?.Type == "microsoft" ? "msa"
            : account?.Type == "yggdrasil" ? "mojang" : "legacy";

        var values = new Dictionary<string, string>
        {
            ["auth_player_name"] = playerName,
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = token,
            ["auth_session"] = token, // 旧版 minecraftArguments 使用 ${auth_session}
            ["clientid"] = "0",
            ["auth_xuid"] = "0",
            ["user_type"] = userType,
            ["version_name"] = id,
            ["version_type"] = versionJson["type"]?.GetValue<string>() ?? "release",
            ["game_directory"] = gameDir,
            ["assets_root"] = McPaths.AssetsDir,
            ["assets_index_name"] = assetsIndex,
            // 旧版（pre-1.6/legacy）用 ${game_assets}，按官方约定指向 assets/virtual/<index>
            ["game_assets"] = Path.Combine(McPaths.AssetsDir, "virtual", assetsIndex),
            ["natives_directory"] = nativesDir,
            ["library_directory"] = McPaths.LibrariesDir,
            ["classpath"] = classpathStr,
            ["classpath_separator"] = ";",
            ["launcher_name"] = "MnyMC",
            ["launcher_version"] = "1.0",
            ["resolution_width"] = windowWidth.ToString(),
            ["resolution_height"] = windowHeight.ToString(),
        };

        // 自定义游戏窗口大小需要开启 has_custom_resolution 特性，
        // 否则带该 feature 的 --width/--height 参数会被 rules 过滤掉
        IReadOnlyDictionary<string, bool>? features = windowWidth > 0 && windowHeight > 0
            ? new Dictionary<string, bool> { ["has_custom_resolution"] = true }
            : null;

        var cmd = new List<string> { java, $"-Xmx{maxMem}" };
        // 第三方登录：javaagent 必须在主类之前；紧跟 -Xmx 插入，位于所有 jvm 参数之前
        if (!string.IsNullOrEmpty(authlibAgent))
        {
            cmd.Add($"-javaagent:{authlibAgent}={account?.ServerUrl ?? ""}");
        }
        var arguments = versionJson["arguments"]?.AsObject();
        if (arguments != null)
        {
            // 1.13+ 新版格式
            cmd.AddRange(Expand(arguments["jvm"]?.AsArray(), values, features));

            // log4j 日志配置（文件在安装阶段已下载）
            var logging = versionJson["logging"]?["client"]?.AsObject();
            if (logging != null)
            {
                string logCfg = Path.Combine(McPaths.AssetsDir, "log_configs",
                    logging["file"]!["id"]!.GetValue<string>());
                if (File.Exists(logCfg))
                    cmd.Add(Sub(logging["argument"]!.GetValue<string>(),
                        new Dictionary<string, string> { ["path"] = logCfg }));
            }

            cmd.Add(versionJson["mainClass"]!.GetValue<string>());
            cmd.AddRange(Expand(arguments["game"]?.AsArray(), values, features));
        }
        else
        {
            // 旧版 minecraftArguments 字符串格式
            cmd.Add($"-Djava.library.path={nativesDir}");
            cmd.Add("-cp");
            cmd.Add(classpathStr);
            cmd.Add(versionJson["mainClass"]!.GetValue<string>());
            cmd.AddRange(Sub(versionJson["minecraftArguments"]!.GetValue<string>(), values)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            // 旧格式没有分辨率占位符，直接附加
            if (windowWidth > 0 && windowHeight > 0)
            {
                cmd.Add("--width");
                cmd.Add(windowWidth.ToString());
                cmd.Add("--height");
                cmd.Add(windowHeight.ToString());
            }
        }
        return cmd;
    }

    /// <summary>把游戏语言写入游戏目录的 options.txt（lang:xx_yy），保留其他选项。
    /// 修复游戏默认英文的问题。</summary>
    public static void WriteGameLanguage(string gameDir, string locale)
    {
        Directory.CreateDirectory(gameDir);
        string file = Path.Combine(gameDir, "options.txt");
        var lines = File.Exists(file)
            ? File.ReadAllLines(file).Where(l => !l.StartsWith("lang:")).ToList()
            : new List<string>();
        lines.Add($"lang:{locale}");
        File.WriteAllLines(file, lines);
    }

    /// <summary>启动游戏，输出实时写入 logs/latest.log 并回调给 UI，返回退出码。
    /// gameDir 为进程工作目录；onStarted 在游戏进程成功启动后回调（用于停止 UI 进度条跳动）。</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> cmd, string gameDir, Action<string>? log = null,
        CancellationToken ct = default, Action? onStarted = null)
    {
        string logDir = Path.Combine(McPaths.GameDir, "logs");
        Directory.CreateDirectory(logDir);
        Directory.CreateDirectory(gameDir);
        await using var logWriter = new StreamWriter(Path.Combine(logDir, "latest.log"), false, Encoding.UTF8);

        void Write(string line)
        {
            lock (logWriter)
            {
                logWriter.WriteLine(line);
                logWriter.Flush();
            }
            try { log?.Invoke(line); } catch { /* UI 回调不影响游戏进程 */ }
        }

        var psi = new ProcessStartInfo(cmd[0])
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in cmd.Skip(1))
            psi.ArgumentList.Add(arg);

        Write(string.Join(' ', cmd)); // 记录完整启动命令
        using var p = Process.Start(psi)!;
        // 取消时杀掉整个进程树，不留孤儿游戏进程
        using var killReg = ct.Register(() => { try { p.Kill(true); } catch { /* 已退出 */ } });
        onStarted?.Invoke();

        var readOut = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) != null) Write(line);
        });
        var readErr = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync(ct)) != null) Write(line);
        });

        try
        {
            await p.WaitForExitAsync(ct);
            await Task.WhenAll(readOut, readErr);
        }
        catch (OperationCanceledException)
        {
            // 进程已被上面的 Register 杀掉，等管道读取任务收尾，再向上抛取消
            try { await Task.WhenAll(readOut, readErr); } catch { }
            throw;
        }
        return p.ExitCode;
    }
}
