using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace WinUI3Shell.Services.Mc;

/// <summary>微软设备码登录返回的信息：用户码、验证地址、轮询间隔与有效期。</summary>
public sealed record MsaDeviceCode(string UserCode, string VerificationUri, string DeviceCode, int Interval, int ExpiresIn);

/// <summary>微软正版登录：设备码 → Live 令牌 → Xbox Live → XSTS → Minecraft 令牌 → 游戏档案。</summary>
public static class MsaLogin
{
    private const string ClientId = "00000000402b5328";
    private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";
    private const string ConnectUrl = "https://login.live.com/oauth20_connect.srf";
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";

    // 登录链路都是短请求，超时统一 30 秒
    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MnyMC/1.0");
        return client;
    }

    /// <summary>申请设备码（用户在弹出的对话框里输入 user_code 完成授权）。</summary>
    public static async Task<MsaDeviceCode> StartAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
            ["response_type"] = "device_code",
        };
        var (status, body) = await SendAsync(FormRequest(ConnectUrl, form), ct);
        if (status != HttpStatusCode.OK || body?["user_code"] is null || body["device_code"] is null)
        {
            throw new InvalidOperationException(StatusMessage("获取设备代码失败", status, body));
        }
        return new MsaDeviceCode(
            body["user_code"]!.GetValue<string>(),
            body["verification_uri"]?.GetValue<string>() ?? "https://www.microsoft.com/link",
            body["device_code"]!.GetValue<string>(),
            body["interval"]?.GetValue<int>() ?? 5,
            body["expires_in"]?.GetValue<int>() ?? 900);
    }

    /// <summary>按 interval 轮询，用户在浏览器完成授权后走完整条登录链路并返回账号。</summary>
    public static async Task<McAccount> PollAsync(MsaDeviceCode code, Action<string>? progress, CancellationToken ct)
    {
        progress?.Invoke("等待在浏览器中完成登录…");
        int interval = Math.Max(code.Interval, 2);
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(code.ExpiresIn, 60));

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException("登录超时，请重新获取代码");
            }
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            var form = new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = code.DeviceCode,
            };
            var (status, body) = await SendAsync(FormRequest(TokenUrl, form), ct);
            if (status == HttpStatusCode.OK && body?["access_token"] is not null)
            {
                return await ExchangeAsync(body, progress, ct);
            }

            switch (body?["error"]?.GetValue<string>() ?? "")
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "expired_token":
                    throw new InvalidOperationException("验证代码已过期，请重新获取");
                case "authorization_declined":
                    throw new InvalidOperationException("登录已被拒绝，请在浏览器中重新授权");
                case "bad_verification_code":
                    throw new InvalidOperationException("验证代码无效，请重新获取");
                default:
                    throw new InvalidOperationException(StatusMessage("登录失败", status, body));
            }
        }
    }

    /// <summary>用刷新令牌重新走一遍 3→7 全链路，返回带新令牌的账号。</summary>
    public static async Task<McAccount> RefreshAsync(McAccount account, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            throw new InvalidOperationException("登录信息不完整，请重新登录");
        }
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = account.RefreshToken,
            ["scope"] = Scope,
        };
        var (status, body) = await SendAsync(FormRequest(TokenUrl, form), ct);
        if (status != HttpStatusCode.OK || body?["access_token"] is null)
        {
            throw new InvalidOperationException(status == HttpStatusCode.BadRequest
                ? "微软登录已失效，请重新登录"
                : StatusMessage("刷新微软登录失败", status, body));
        }
        return await ExchangeAsync(body, null, ct, account.RefreshToken);
    }

    // ===== 登录链路 =====

    /// <summary>Live 令牌 → Xbox Live → XSTS → Minecraft 令牌 → 档案。</summary>
    private static async Task<McAccount> ExchangeAsync(JsonNode body, Action<string>? progress, CancellationToken ct,
        string fallbackRefreshToken = "")
    {
        string liveToken = body["access_token"]!.GetValue<string>();
        // 刷新令牌轮换：接口没返回新的就用旧的，避免把已有刷新令牌丢掉
        string refreshToken = body["refresh_token"]?.GetValue<string>() ?? fallbackRefreshToken;

        progress?.Invoke("正在验证 Xbox 账号…");
        string xblToken = await XboxAuthenticateAsync(liveToken, ct);

        progress?.Invoke("正在获取 XSTS 令牌…");
        var (xstsToken, uhs) = await XstsAuthorizeAsync(xblToken, ct);

        progress?.Invoke("正在获取 Minecraft 令牌…");
        var (mcToken, mcExpires) = await LoginWithXboxAsync(uhs, xstsToken, ct);

        progress?.Invoke("正在读取游戏档案…");
        var (name, uuid) = await GetProfileAsync(mcToken, ct);

        return new McAccount
        {
            Type = "microsoft",
            Name = name,
            Uuid = uuid,
            AccessToken = mcToken,
            RefreshToken = refreshToken,
            ExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(mcExpires, 60),
        };
    }

    private static async Task<string> XboxAuthenticateAsync(string liveToken, CancellationToken ct)
    {
        static JsonObject Body(string ticket) => new()
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = ticket,
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        };

        var (status, body) = await SendAsync(JsonRequest("https://user.auth.xboxlive.com/user/authenticate", Body(liveToken)), ct);
        if (status == HttpStatusCode.Unauthorized)
        {
            // 新版要求 RpsTicket 带 d= 前缀，旧格式被拒时自动重试一次
            (status, body) = await SendAsync(
                JsonRequest("https://user.auth.xboxlive.com/user/authenticate", Body("d=" + liveToken)), ct);
        }
        if (status != HttpStatusCode.OK || body?["Token"] is null)
        {
            throw new InvalidOperationException(StatusMessage("Xbox Live 验证失败", status, body));
        }
        return body["Token"]!.GetValue<string>();
    }

    private static async Task<(string Token, string Uhs)> XstsAuthorizeAsync(string xblToken, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new JsonArray(JsonValue.Create(xblToken)),
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT",
        };
        var (status, body) = await SendAsync(JsonRequest("https://xsts.auth.xboxlive.com/xsts/authorize", payload), ct);
        if (status != HttpStatusCode.OK || body?["Token"] is null)
        {
            long xerr = 0;
            try { xerr = body?["XErr"]?.GetValue<long>() ?? 0; } catch { /* XErr 缺失按未知错误处理 */ }
            throw new InvalidOperationException(xerr switch
            {
                2148916233L => "该微软账号还没有 Xbox 账户，请先到 Xbox 官网创建",
                2148916235L => "所在地区不支持 Xbox Live",
                2148916238L => "未成年账号需要先加入微软家庭组",
                _ => StatusMessage("Xbox 验证失败", status, body),
            });
        }
        string uhs = body["DisplayClaims"]?["xui"]?[0]?["uhs"]?.GetValue<string>() ?? "";
        if (uhs.Length == 0)
        {
            throw new InvalidOperationException("Xbox 验证失败：没有拿到用户标识");
        }
        return (body["Token"]!.GetValue<string>(), uhs);
    }

    private static async Task<(string Token, int ExpiresIn)> LoginWithXboxAsync(string uhs, string xstsToken, CancellationToken ct)
    {
        var payload = new JsonObject { ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}" };
        var (status, body) = await SendAsync(
            JsonRequest("https://api.minecraftservices.com/authentication/login_with_xbox", payload), ct);
        if (status != HttpStatusCode.OK || body?["access_token"] is null)
        {
            throw new InvalidOperationException(StatusMessage("获取 Minecraft 令牌失败", status, body));
        }
        return (body["access_token"]!.GetValue<string>(), ReadInt(body["expires_in"], 86400));
    }

    private static async Task<(string Name, string Uuid)> GetProfileAsync(string mcToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        var (status, body) = await SendAsync(request, ct);
        if (status == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("该微软账号没有购买 Minecraft Java 版");
        }
        if (status == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Minecraft 登录已失效，请重新登录");
        }
        if (status != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(StatusMessage("读取游戏档案失败", status, body));
        }
        string name = body?["name"]?.GetValue<string>() ?? "";
        string id = body?["id"]?.GetValue<string>() ?? "";
        if (name.Length == 0 || id.Length == 0)
        {
            throw new InvalidOperationException("读取游戏档案失败，请重试");
        }
        return (name, AccountStore.HyphenateUuid(id));
    }

    // ===== 请求辅助 =====

    private static HttpRequestMessage FormRequest(string url, IDictionary<string, string> form) =>
        new(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };

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
                    // 非 JSON 响应（如网关错误页）按状态码报错
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

    private static int ReadInt(JsonNode? node, int fallback)
    {
        try { return node?.GetValue<int>() ?? fallback; }
        catch { return fallback; }
    }

    /// <summary>把接口错误转成中文提示。</summary>
    private static string StatusMessage(string prefix, HttpStatusCode status, JsonNode? body)
    {
        string description = "";
        try
        {
            description = body?["error_description"]?.GetValue<string>()
                ?? body?["errorMessage"]?.GetValue<string>() ?? "";
        }
        catch { /* 字段类型异常时忽略 */ }
        if (description.Length > 0) return $"{prefix}：{description}";
        return $"{prefix}（HTTP {(int)status}），请稍后重试";
    }
}
