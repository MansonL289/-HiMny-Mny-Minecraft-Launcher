using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>官方版本清单读取（version_manifest.json / version_manifest_v2.json）。</summary>
public static class Manifest
{
    public const string V1 = "https://launchermeta.mojang.com/mc/game/version_manifest.json";
    public const string V2 = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>拉取清单。v2 额外带每个 version.json 的 sha1。</summary>
    public static async Task<JsonObject> FetchAsync(string mirror, bool v2 = true, CancellationToken ct = default)
    {
        string url = Mirror.Rewrite(v2 ? V2 : V1, mirror);
        return (JsonObject)JsonNode.Parse(await McHttp.Client.GetStringAsync(url, ct))!;
    }

    public static string LatestRelease(JsonObject m) => m["latest"]!["release"]!.GetValue<string>();

    public static IEnumerable<(string Id, string Url, string? Sha1, string Type)> Versions(JsonObject m, string? type = null)
    {
        foreach (var node in m["versions"]!.AsArray())
        {
            var v = node!.AsObject();
            string t = v["type"]!.GetValue<string>();
            if (type != null && t != type) continue;
            yield return (v["id"]!.GetValue<string>(), v["url"]!.GetValue<string>(), v["sha1"]?.GetValue<string>(), t);
        }
    }

    public static (string Url, string? Sha1) Find(JsonObject m, string id)
    {
        foreach (var (vid, url, sha1, _) in Versions(m))
            if (vid == id)
                return (url, sha1);
        throw new ArgumentException($"清单中不存在版本: {id}");
    }
}
