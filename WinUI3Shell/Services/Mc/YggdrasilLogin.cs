using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>第三方登录：authlib-injector 兼容的 Yggdrasil 验证服务器（如 LittleSkin）。</summary>
public static class YggdrasilLogin
{
    private const string ClientToken = "mnymc";

    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MnyMC/1.0");
        return client;
    }

    /// <summary>邮箱 + 密码登录，返回可用的第三方账号。</summary>
    public static async Task<McAccount> LoginAsync(string serverUrl, string email, string password, CancellationToken ct)
    {
        string root = Normalize(serverUrl);
        if (root.Length == 0) throw new InvalidOperationException("请填写验证服务器地址");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("请填写邮箱和密码");
        }

        var payload = new JsonObject
        {
            ["agent"] = new JsonObject { ["name"] = "Minecraft", ["version"] = 1 },
            ["username"] = email,
            ["password"] = password,
            ["requestUser"] = true,
        };
        var (status, body) = await SendAsync(JsonRequest(root + "/authserver/authenticate", payload), ct);
        if (status != HttpStatusCode.OK)
        {
            throw AuthError(status, body);
        }

        string token = body?["accessToken"]?.GetValue<string>() ?? "";
        if (token.Length == 0) throw new InvalidOperationException("登录失败：服务器没有返回令牌");

        var profile = body?["selectedProfile"];
        string id = profile?["id"]?.GetValue<string>() ?? "";
        string name = profile?["name"]?.GetValue<string>() ?? "";
        if (id.Length == 0 || name.Length == 0)
        {
            throw new InvalidOperationException("该账号没有绑定 Minecraft 角色");
        }

        return new McAccount
        {
            Type = "yggdrasil",
            Name = name,
            Uuid = AccountStore.HyphenateUuid(id),
            AccessToken = token,
            ServerUrl = root,
            ExpiresAtUnix = 0,
        };
    }

    /// <summary>用 accessToken 续期；失败时抛中文异常（调用方提示重新登录）。</summary>
    public static async Task<McAccount> RefreshAsync(McAccount account, CancellationToken ct)
    {
        string root = Normalize(account.ServerUrl);
        if (root.Length == 0 || string.IsNullOrEmpty(account.AccessToken))
        {
            throw new InvalidOperationException("第三方登录已过期，请重新登录");
        }

        var payload = new JsonObject
        {
            ["accessToken"] = account.AccessToken,
            ["clientToken"] = ClientToken,
        };
        var (status, body) = await SendAsync(JsonRequest(root + "/authserver/refresh", payload), ct);
        string token = status == HttpStatusCode.OK
            ? body?["accessToken"]?.GetValue<string>() ?? ""
            : "";
        if (token.Length == 0)
        {
            throw new InvalidOperationException("第三方登录已过期，请重新登录");
        }

        var result = new McAccount
        {
            Type = "yggdrasil",
            Name = account.Name,
            Uuid = account.Uuid,
            AccessToken = token,
            RefreshToken = account.RefreshToken,
            ServerUrl = account.ServerUrl,
            ExpiresAtUnix = 0,
        };
        // 服务器可能顺带返回新的角色信息
        var profile = body?["selectedProfile"];
        string name = profile?["name"]?.GetValue<string>() ?? "";
        string id = profile?["id"]?.GetValue<string>() ?? "";
        if (name.Length > 0) result.Name = name;
        if (id.Length > 0) result.Uuid = AccountStore.HyphenateUuid(id);
        return result;
    }

    /// <summary>去掉尾部斜杠；没写协议时默认 https。</summary>
    private static string Normalize(string serverUrl)
    {
        string root = serverUrl?.Trim() ?? "";
        if (root.Length == 0) return "";
        if (!root.Contains("://", StringComparison.Ordinal)) root = "https://" + root;
        return root.TrimEnd('/');
    }

    private static HttpRequestMessage JsonRequest(string url, JsonNode payload) =>
        new(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using (request)
            using (var response = await Http.SendAsync(request, ct))
            {
                string text = await response.Content.ReadAsStringAsync(ct);
                JsonNode? body = null;
                try
                {
                    if (text.Length > 0) body = JsonNode.Parse(text);
                }
                catch
                {
                    // 非 JSON 响应按状态码报错
                }
                return (response.StatusCode, body);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException("网络请求超时，请检查网络后重试", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("网络请求失败，请检查网络后重试", ex);
        }
    }

    /// <summary>把 Yggdrasil 的 error / errorMessage 转成中文提示。</summary>
    private static Exception AuthError(HttpStatusCode status, JsonNode? body)
    {
        string error = body?["error"]?.GetValue<string>() ?? "";
        string message = body?["errorMessage"]?.GetValue<string>() ?? "";
        return error switch
        {
            "ForbiddenOperationException" => new InvalidOperationException("邮箱或密码错误"),
            "IllegalArgumentException" => new InvalidOperationException("邮箱或密码格式不正确"),
            _ => new InvalidOperationException(message.Length > 0
                ? $"登录失败：{message}"
                : $"登录失败（HTTP {(int)status}），请检查服务器地址"),
        };
    }
}
