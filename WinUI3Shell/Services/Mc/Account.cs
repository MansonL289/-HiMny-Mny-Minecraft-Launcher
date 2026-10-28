using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinUI3Shell.Services.Mc;

/// <summary>一个可用的游戏账号：离线 / 微软正版 / 第三方（authlib-injector）。</summary>
public sealed class McAccount
{
    public string Type { get; set; } = "offline";   // offline / microsoft / yggdrasil
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    /// <summary>第三方登录的验证服务器根地址（authlib-injector 用），离线/微软为空。</summary>
    public string ServerUrl { get; set; } = "";
    /// <summary>令牌过期时间（Unix 秒），0 = 未知。</summary>
    public long ExpiresAtUnix { get; set; }

    [JsonIgnore]
    public string TypeText => Type switch
    {
        "microsoft" => "微软账号",
        "yggdrasil" => "第三方账号",
        _ => "离线账号",
    };

    [JsonIgnore]
    public bool IsExpired => ExpiresAtUnix > 0
        && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= ExpiresAtUnix - 60;
}

/// <summary>accounts.json 的文件结构。</summary>
internal sealed class AccountsFile
{
    public string Active { get; set; } = "";
    public List<McAccount> Accounts { get; set; } = new();
}

/// <summary>账号列表的本地存储：%USERPROFILE%\.mnymc\accounts.json。</summary>
public static class AccountStore
{
    private static readonly object Gate = new();
    private static readonly List<McAccount> Items = new();
    private static string _activeKey = "";
    private static bool _loaded;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>账号列表变化（新增/删除/切换/令牌更新）。可能在后台线程触发。</summary>
    public static event EventHandler? Changed;

    public static string FilePath => Path.Combine(McPaths.GameDir, "accounts.json");

    /// <summary>账号唯一标识：类型 + 名称 + 验证服务器地址。</summary>
    public static string KeyOf(McAccount account) =>
        $"{account.Type}|{account.Name}|{account.ServerUrl}";

    public static IReadOnlyList<McAccount> Accounts
    {
        get
        {
            lock (Gate)
            {
                EnsureLoaded();
                return Items.ToList();
            }
        }
    }

    /// <summary>当前使用的账号；没有任何账号时为 null。</summary>
    public static McAccount? Active
    {
        get
        {
            lock (Gate)
            {
                EnsureLoaded();
                return _activeKey.Length == 0
                    ? null
                    : Items.FirstOrDefault(a => KeyOf(a) == _activeKey);
            }
        }
    }

    public static string ActiveKey
    {
        get
        {
            lock (Gate)
            {
                EnsureLoaded();
                return _activeKey;
            }
        }
    }

    /// <summary>重新从磁盘读取（文件损坏时当作空列表）。</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            LoadCore();
        }
    }

    /// <summary>添加账号并保存；同类型 + 名称 + 验证服务器的账号视为同一个，覆盖令牌。</summary>
    public static void Add(McAccount account, bool select = true)
    {
        if (account == null) return;
        lock (Gate)
        {
            EnsureLoaded();
            string key = KeyOf(account);
            var existing = Items.FirstOrDefault(a => KeyOf(a) == key);
            if (existing == null)
            {
                Items.Add(account);
            }
            else
            {
                existing.Uuid = account.Uuid;
                existing.AccessToken = account.AccessToken;
                existing.RefreshToken = account.RefreshToken;
                existing.ServerUrl = account.ServerUrl;
                existing.ExpiresAtUnix = account.ExpiresAtUnix;
            }
            if (select) _activeKey = key;
            SaveCore();
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Remove(McAccount account)
    {
        if (account == null) return;
        bool removed;
        lock (Gate)
        {
            EnsureLoaded();
            string key = KeyOf(account);
            removed = Items.RemoveAll(a => KeyOf(a) == key) > 0;
            if (_activeKey == key) _activeKey = "";
            if (removed) SaveCore();
        }
        if (removed) Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void SetActive(McAccount account)
    {
        if (account == null) return;
        lock (Gate)
        {
            EnsureLoaded();
            _activeKey = KeyOf(account);
            SaveCore();
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Save()
    {
        lock (Gate)
        {
            EnsureLoaded();
            SaveCore();
        }
    }

    /// <summary>把 32 位无横线 UUID 转成标准 8-4-4-4-12 格式。</summary>
    public static string HyphenateUuid(string id)
    {
        string hex = id.Replace("-", "").Trim();
        if (hex.Length != 32) return id;
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    // 以下 *Core 方法都要求在 Gate 锁内调用

    private static void EnsureLoaded()
    {
        if (!_loaded) LoadCore();
    }

    private static void LoadCore()
    {
        _loaded = true;
        Items.Clear();
        _activeKey = "";
        try
        {
            if (!File.Exists(FilePath)) return;
            var file = JsonSerializer.Deserialize<AccountsFile>(File.ReadAllText(FilePath), JsonOptions);
            if (file?.Accounts == null) return;
            Items.AddRange(file.Accounts.Where(a => a != null));
            _activeKey = file.Active ?? "";
        }
        catch
        {
            // 文件损坏：当作空列表，不打断启动
            Items.Clear();
            _activeKey = "";
        }
    }

    private static void SaveCore()
    {
        try
        {
            Directory.CreateDirectory(McPaths.GameDir);
            var file = new AccountsFile { Active = _activeKey, Accounts = Items };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(file, JsonOptions));
        }
        catch
        {
            // 写盘失败（占用/权限）暂时忽略，下次再存
        }
    }
}
