using System.Text.RegularExpressions;

namespace WinUI3Shell.Services.Mc;

/// <summary>崩溃分析结论。</summary>
public sealed record LogReport(string Headline, List<string> Findings, string Text);

/// <summary>读取游戏日志 / 崩溃报告，并给出中文结论与建议。</summary>
public static class LogAnalyzer
{
    private static readonly (string[] Keys, string Finding)[] Rules =
    {
        (new[] { "outofmemoryerror" }, "内存不足：游戏分配到的内存不够。到「版本设置 → 启动」把最大内存调大（建议 4G 起）。"),
        (new[] { "unsupportedclassversionerror" }, "Java 版本不匹配：当前 Java 太旧或太新，换成本版本要求的 Java。"),
        (new[] { "duplicatemodsfoundexception", "duplicate mods", "duplicate mod" }, "存在重复 Mod：mods 文件夹里有两个同名/同 ID 的 Mod，删掉其中一个。"),
        (new[] { "incompatiblemodsetexception", "incompatible mod set", "mod resolution failed", "missing or invalid dependency" }, "Mod 冲突或缺少依赖：按日志里提示补装前置 Mod，或逐个移除最近添加的 Mod 排查。"),
        (new[] { "nosuchmethoderror", "nosuchfielderror", "noclassdeffounderror" }, "Mod 与游戏/加载器版本不匹配：换成对应游戏版本的 Mod 文件。"),
        (new[] { "mixin apply failed", "mixinapplyerror", "mixin.injection.throwables" }, "Mixin 注入失败：常见于 Mod 冲突或 Mod 与加载器版本不兼容。"),
        (new[] { "pixel format not accelerated", "failed to create window", "glfw error", "org.lwjgl.opengl" }, "显卡/OpenGL 问题：更新显卡驱动，或在「版本设置」里关掉全屏/降低渲染距离后再试。"),
        (new[] { "unrecognized vm option", "unrecognized option" }, "启动参数无效：自定义 JVM 参数里有当前 Java 不支持的选项，清空后重试。"),
        (new[] { "could not find or load main class", "no main manifest attribute" }, "游戏核心文件缺失或损坏：重新下载该版本。"),
        (new[] { "address already in use" }, "端口被占用：关闭占用该端口的程序（常见于同时开多个联机/服务端）。"),
        (new[] { "failed to download", "couldn't download", "download failed" }, "启动时下载资源失败：检查网络，或在设置里换下载源后重试。"),
        (new[] { "invalid session", "failed to verify username" }, "账号验证失败：登录状态已过期，重新登录。"),
        (new[] { "the game crashed whilst", "exception in thread" }, "游戏进程崩溃，具体原因见下方日志末尾。"),
    };

    /// <summary>优先读最新崩溃报告，没有就退回 latest.log。</summary>
    public static string? ReadLog(string gameDir)
    {
        try
        {
            string crashDir = Path.Combine(gameDir, "crash-reports");
            if (Directory.Exists(crashDir))
            {
                var newest = new DirectoryInfo(crashDir).EnumerateFiles("*.txt")
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                if (newest != null)
                {
                    return File.ReadAllText(newest.FullName);
                }
            }
        }
        catch { /* 继续看 latest.log */ }

        string log = Path.Combine(McPaths.GameDir, "logs", "latest.log");
        if (!File.Exists(log)) log = Path.Combine(gameDir, "logs", "latest.log");
        try
        {
            if (!File.Exists(log)) return null;
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string all = reader.ReadToEnd();
            // 只保留末尾 200 KB，避免超大日志卡界面
            const int keep = 200_000;
            return all.Length > keep ? all[^keep..] : all;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>分析日志文本，返回抬头、命中的结论（最多 4 条）和末尾片段。</summary>
    public static LogReport Analyze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new LogReport("没有找到日志", new(), "启动一次游戏后，日志会出现在 .mnymc\\logs\\latest.log。");
        }

        string lower = text.ToLowerInvariant();
        var findings = new List<string>();
        foreach (var (keys, finding) in Rules)
        {
            if (findings.Count >= 4) break;
            if (findings.Contains(finding)) continue;
            if (keys.Any(k => lower.Contains(k))) findings.Add(finding);
        }

        // Mod 数量、Java 版本等简要信息
        var info = new List<string>();
        var java = Regex.Match(text, @"Java Version:\s*([0-9._]+)", RegexOptions.IgnoreCase);
        if (java.Success) info.Add($"Java {java.Groups[1].Value}");
        var mc = Regex.Match(text, @"Loading Minecraft ([0-9][^\s]*)", RegexOptions.IgnoreCase);
        if (mc.Success) info.Add($"Minecraft {mc.Groups[1].Value}");
        var forge = Regex.Match(text, @"(?:Forge|NeoForge) mod loading|FML[^:]*:.*?version", RegexOptions.IgnoreCase);
        if (forge.Success) info.Add("已加载 Forge/NeoForge");
        if (info.Count > 0) findings.Insert(0, string.Join(" · ", info));

        string headline = findings.Count <= 1 ? "日志已读取" : "发现以下问题";
        if (findings.Count == 0) findings.Add("日志里没有匹配到已知的崩溃特征，建议查看日志末尾的报错原文。");

        return new LogReport(headline, findings, text);
    }
}
