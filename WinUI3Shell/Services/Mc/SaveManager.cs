using System.Globalization;

namespace WinUI3Shell.Services.Mc;

/// <summary>游戏规则值的类型：布尔 / 整数 / 其他字符串。</summary>
public enum GameRuleKind
{
    Bool,
    Int,
    Text,
}

/// <summary>一条可编辑的游戏规则。旧版存字符串，新版（26.x）存 Byte/Int 原生类型。</summary>
public sealed class GameRuleEdit
{
    public string Name { get; set; } = "";
    public GameRuleKind Kind { get; set; }
    public bool BoolValue { get; set; }
    public double NumberValue { get; set; }
    public string TextValue { get; set; } = "";

    /// <summary>新版规则的原始存储类型（写回时保持一致）。</summary>
    internal NbtType StoreType { get; set; } = NbtType.String;

    /// <summary>界面上去掉 minecraft: 命名空间前缀，并把原版规则名显示成中文。</summary>
    public string DisplayName => GameRuleNames.Of(Name.StartsWith("minecraft:", StringComparison.Ordinal)
        ? Name["minecraft:".Length..]
        : Name);

    public string ValueText => Kind switch
    {
        GameRuleKind.Bool => BoolValue ? "true" : "false",
        GameRuleKind.Int => ((long)NumberValue).ToString(CultureInfo.InvariantCulture),
        _ => TextValue,
    };
}

/// <summary>原版游戏规则的中文名；未收录的规则（如 Mod 规则）直接显示原名。</summary>
internal static class GameRuleNames
{
    private static readonly Dictionary<string, string> Chinese = new(StringComparer.Ordinal)
    {
        ["announceAdvancements"] = "宣告进度",
        ["blockExplosionDropDecay"] = "方块爆炸掉落物衰减",
        ["commandBlockOutput"] = "命令方块输出",
        ["commandBlocksEnabled"] = "启用命令方块",
        ["disableElytraMovementCheck"] = "鞘翅移动检测",
        ["disableRaids"] = "禁用袭击",
        ["doDaylightCycle"] = "昼夜更替",
        ["doEntityDrops"] = "实体掉落物",
        ["doFireTick"] = "火焰蔓延",
        ["doImmediateRespawn"] = "立即重生",
        ["doInsomnia"] = "幻翼生成",
        ["doLimitedCrafting"] = "限制合成",
        ["doMobLoot"] = "生物掉落物",
        ["doMobSpawning"] = "生物生成",
        ["doPatrolSpawning"] = "巡逻队生成",
        ["doTileDrops"] = "方块掉落物",
        ["doTraderSpawning"] = "流浪商人生成",
        ["doVinesSpread"] = "藤蔓蔓延",
        ["doWardenSpawning"] = "监守者生成",
        ["doWeatherCycle"] = "天气更替",
        ["drowningDamage"] = "溺水伤害",
        ["elytraMovementCheck"] = "鞘翅移动检测",
        ["enderPearlsVanishOnDeath"] = "末影珍珠死亡消失",
        ["fallDamage"] = "摔落伤害",
        ["fireDamage"] = "火焰伤害",
        ["forgiveDeadPlayers"] = "宽恕死亡玩家",
        ["freezeDamage"] = "冰冻伤害",
        ["globalSoundEvents"] = "全局音效事件",
        ["keepInventory"] = "死亡保留物品",
        ["lavaSourceConversion"] = "熔岩源转化",
        ["logAdminCommands"] = "记录管理员命令",
        ["maxCommandChainLength"] = "命令链最大长度",
        ["maxEntityCramming"] = "实体挤压上限",
        ["minecartMaxSpeed"] = "矿车最大速度",
        ["mobExplosionDropDecay"] = "生物爆炸掉落物衰减",
        ["mobGriefing"] = "生物破坏方块",
        ["naturalRegeneration"] = "自然生命恢复",
        ["playersSleepingPercentage"] = "玩家睡眠比例",
        ["projectilesCanBreakBlocks"] = "投掷物破坏方块",
        ["pvp"] = "玩家对战",
        ["randomTickSpeed"] = "随机刻速度",
        ["reducedDebugInfo"] = "简化调试信息",
        ["sendCommandFeedback"] = "发送命令反馈",
        ["showDeathMessages"] = "显示死亡消息",
        ["snowAccumulationHeight"] = "积雪高度",
        ["spawnChunkRadius"] = "出生点区块半径",
        ["spawnRadius"] = "重生半径",
        ["spectatorsGenerateChunks"] = "旁观者生成区块",
        ["tntExplosionDropDecay"] = "TNT 爆炸掉落物衰减",
        ["universalAnger"] = "通用愤怒",
        ["waterSourceConversion"] = "水源转化",
    };

    public static string Of(string name) => Chinese.TryGetValue(name, out string? zh) ? zh : name;
}

/// <summary>存档只读信息（列表与「查看」用）。</summary>
public sealed class SaveInfo
{
    public string SavePath { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string LevelName { get; set; } = "";
    public int GameType { get; set; }
    /// <summary>难度原始值：旧版 0-3，新版 peaceful/easy/normal/hard。</summary>
    public string Difficulty { get; set; } = "";
    public bool DifficultyLocked { get; set; }
    public bool AllowCommands { get; set; }
    public bool Hardcore { get; set; }
    public string VersionText { get; set; } = "未知";
    public long LastPlayed { get; set; }
    /// <summary>旧版才有 DayTime；新版未单独保存时为空。</summary>
    public long? DayTime { get; set; }
    public long GameTime { get; set; }
    public int SpawnX { get; set; }
    public int SpawnY { get; set; }
    public int SpawnZ { get; set; }
    public long Seed { get; set; }
    public long SizeBytes { get; set; }
    public int RegionFiles { get; set; }
    public int PlayerDataFiles { get; set; }
    public List<GameRuleEdit> Rules { get; set; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(LevelName) ? FolderName : LevelName;

    public string GameModeText => GameType switch
    {
        0 => "生存",
        1 => "创造",
        2 => "冒险",
        3 => "旁观",
        _ => $"未知（{GameType}）",
    };

    public string DifficultyText
    {
        get
        {
            string text = Difficulty switch
            {
                "peaceful" or "0" => "和平",
                "easy" or "1" => "简单",
                "normal" or "2" => "普通",
                "hard" or "3" => "困难",
                "" => "未知",
                _ => Difficulty,
            };
            return DifficultyLocked ? text + "（已锁定）" : text;
        }
    }

    public string CheatsText => AllowCommands ? "允许" : "不允许";
    public string HardcoreText => Hardcore ? "是" : "否";
    public string LastPlayedText => FormatTime(LastPlayed);
    public string DayText => DayTime.HasValue ? FormatDay(DayTime.Value) : "未知";
    public string PlayTimeText => FormatPlayTime(GameTime);
    public string SpawnText => $"{SpawnX}, {SpawnY}, {SpawnZ}";
    public string SeedText => Seed.ToString(CultureInfo.InvariantCulture);
    public string SizeText => FormatSize(SizeBytes);
    public string RegionText => $"{RegionFiles} 个区域文件";
    public string PlayerDataText => $"{PlayerDataFiles} 个玩家数据";
    public string SubtitleText => $"{GameModeText} · {VersionText} · {LastPlayedText}";

    internal static string FormatTime(long unixMs)
    {
        if (unixMs <= 0) return "未知";
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return "未知";
        }
    }

    internal static string FormatDay(long dayTime)
    {
        long day = dayTime / 24000;
        long clock = (dayTime % 24000 + 6000) % 24000; // 0 刻 = 早上 6 点
        int hour = (int)(clock / 1000);
        int minute = (int)((clock % 1000) * 60 / 1000);
        return $"第 {day} 天 {hour:00}:{minute:00}";
    }

    internal static string FormatPlayTime(long ticks)
    {
        if (ticks <= 0) return "未知";
        double hours = ticks / 20.0 / 3600.0;
        return hours >= 1 ? $"{hours:0.#} 小时" : $"{ticks / 20.0:0} 秒";
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / 1024.0:0.#} KB";
    }
}

/// <summary>可编辑的存档草稿：读取完整 NBT 树，改内存中的字段，最后统一写回。
/// 同时兼容旧版 level.dat 内嵌 GameRules 与新版独立 game_rules.dat。</summary>
public sealed class SaveDraft
{
    private readonly NbtTag _root;
    private readonly NbtTag _data;
    private readonly NbtTag? _rulesRoot;
    private readonly NbtTag? _rulesData;

    public string SavePath { get; }
    public string FolderName { get; }
    public string LevelName { get; }
    public int GameType { get; set; }
    public bool AllowCommands { get; set; }
    public bool Hardcore { get; }
    public List<GameRuleEdit> Rules { get; }

    private SaveDraft(string savePath, NbtTag root, NbtTag data, NbtTag? rulesRoot, NbtTag? rulesData,
        int gameType, bool allowCommands, bool hardcore, List<GameRuleEdit> rules)
    {
        SavePath = savePath;
        FolderName = Path.GetFileName(savePath);
        _root = root;
        _data = data;
        _rulesRoot = rulesRoot;
        _rulesData = rulesData;
        LevelName = data["LevelName"]?.GetString() ?? "";
        GameType = gameType;
        AllowCommands = allowCommands;
        Hardcore = hardcore;
        Rules = rules;
    }

    public static SaveDraft? Load(string savePath)
    {
        string levelPath = Path.Combine(savePath, "level.dat");
        if (!File.Exists(levelPath)) return null;
        NbtTag root;
        try
        {
            root = NbtFile.Read(levelPath);
        }
        catch
        {
            return null;
        }
        if (root["Data"] is not { Type: NbtType.Compound } data) return null;

        SaveManager.TryReadNewRules(savePath, out var rulesRoot, out var rulesData);
        var rules = rulesData != null
            ? SaveManager.ReadTypedRules(rulesData)
            : data["GameRules"] is { Type: NbtType.Compound } legacy
                ? SaveManager.ReadStringRules(legacy)
                : new List<GameRuleEdit>();

        return new SaveDraft(savePath, root, data, rulesRoot, rulesData,
            data["GameType"]?.GetInt() ?? 0,
            data["allowCommands"]?.GetBool() ?? false,
            SaveManager.ReadHardcore(data),
            rules);
    }

    /// <summary>把修改写回：游戏规则（独立文件或内嵌）、玩家模式，最后原子替换 level.dat。</summary>
    public void Save()
    {
        _data.SetChild(NbtTag.IntTag("GameType", GameType));
        _data.SetChild(NbtTag.ByteTag("allowCommands", (byte)(AllowCommands ? 1 : 0)));

        if (_rulesRoot != null && _rulesData != null)
        {
            foreach (var rule in Rules)
            {
                _rulesData.SetChild(BuildRuleTag(rule));
            }
            SaveManager.WriteAtomically(SaveManager.GameRulesPath(SavePath), _rulesRoot);
        }
        else if (Rules.Count > 0)
        {
            var gameRules = _data.GetOrAddCompound("GameRules");
            foreach (var rule in Rules)
            {
                gameRules.SetChild(NbtTag.StringTag(rule.Name, rule.ValueText));
            }
        }

        ApplyPlayerGameType();

        string levelPath = Path.Combine(SavePath, "level.dat");
        string tempPath = levelPath + ".tmp";
        try
        {
            NbtFile.Write(tempPath, _root);
            try
            {
                File.Copy(levelPath, Path.Combine(SavePath, "level.dat_old"), true);
            }
            catch
            {
                // 备份失败不阻断保存
            }
            File.Move(tempPath, levelPath, true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* 清理失败忽略 */ }
            throw;
        }
    }

    /// <summary>新版玩家数据散落在 players/data/*.dat；旧版在 Data.Player。两处都把模式与能力标志同步。</summary>
    private void ApplyPlayerGameType()
    {
        string playersDir = Path.Combine(SavePath, "players", "data");
        if (Directory.Exists(playersDir))
        {
            foreach (string file in Directory.GetFiles(playersDir, "*.dat"))
            {
                NbtTag playerRoot;
                try
                {
                    playerRoot = NbtFile.Read(file);
                }
                catch
                {
                    continue; // 单个玩家文件损坏不阻断
                }
                ApplyToPlayerTag(playerRoot);
                SaveManager.WriteAtomically(file, playerRoot);
            }
            return;
        }

        if (_data["Player"] is { Type: NbtType.Compound } player)
        {
            ApplyToPlayerTag(player);
        }
    }

    private void ApplyToPlayerTag(NbtTag player)
    {
        player.SetChild(NbtTag.IntTag("playerGameType", GameType));
        if (player["abilities"] is not { Type: NbtType.Compound } abilities) return;

        bool creative = GameType == 1;
        bool spectator = GameType == 3;
        abilities.SetChild(BoolByte("invulnerable", creative || spectator));
        abilities.SetChild(BoolByte("mayfly", creative || spectator));
        abilities.SetChild(BoolByte("instabuild", creative));
        abilities.SetChild(BoolByte("mayBuild", !spectator));
        if (spectator)
        {
            abilities.SetChild(BoolByte("flying", true));
        }
        else if (!creative)
        {
            abilities.SetChild(BoolByte("flying", false));
        }
    }

    private static NbtTag BuildRuleTag(GameRuleEdit rule)
    {
        switch (rule.Kind)
        {
            case GameRuleKind.Bool:
                return NbtTag.ByteTag(rule.Name, (byte)(rule.BoolValue ? 1 : 0));
            case GameRuleKind.Int:
                long value = (long)rule.NumberValue;
                return rule.StoreType switch
                {
                    NbtType.Byte => NbtTag.ByteTag(rule.Name, (byte)value),
                    NbtType.Short => new NbtTag(NbtType.Short, rule.Name, (short)value),
                    NbtType.Long => new NbtTag(NbtType.Long, rule.Name, value),
                    _ => NbtTag.IntTag(rule.Name, (int)value),
                };
            default:
                return NbtTag.StringTag(rule.Name, rule.TextValue);
        }
    }

    private static NbtTag BoolByte(string name, bool value) => NbtTag.ByteTag(name, (byte)(value ? 1 : 0));
}

/// <summary>存档目录的枚举、信息读取与删除（修改见 SaveDraft）。</summary>
public static class SaveManager
{
    public static string SavesDir(string gameDir) => Path.Combine(gameDir, "saves");

    /// <summary>新版（26.x）独立规则文件。</summary>
    public static string GameRulesPath(string savePath) =>
        Path.Combine(savePath, "data", "minecraft", "game_rules.dat");

    /// <summary>列出所有存档（含 level.dat 的目录），按最后游玩时间倒序。</summary>
    public static List<SaveInfo> Enumerate(string gameDir)
    {
        var result = new List<SaveInfo>();
        string root = SavesDir(gameDir);
        if (!Directory.Exists(root)) return result;

        foreach (string savePath in Directory.EnumerateDirectories(root))
        {
            var info = LoadInfo(savePath);
            if (info != null) result.Add(info);
        }
        return result.OrderByDescending(s => s.LastPlayed)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static SaveInfo? LoadInfo(string savePath)
    {
        string levelPath = Path.Combine(savePath, "level.dat");
        if (!File.Exists(levelPath)) return null;
        NbtTag root;
        try
        {
            root = NbtFile.Read(levelPath);
        }
        catch
        {
            return null;
        }
        if (root["Data"] is not { Type: NbtType.Compound } data) return null;

        var (spawnX, spawnY, spawnZ) = ReadSpawn(data);
        return new SaveInfo
        {
            SavePath = savePath,
            FolderName = Path.GetFileName(savePath),
            LevelName = data["LevelName"]?.GetString() ?? "",
            GameType = data["GameType"]?.GetInt() ?? 0,
            Difficulty = ReadDifficulty(data),
            DifficultyLocked = ReadDifficultyLocked(data),
            AllowCommands = data["allowCommands"]?.GetBool() ?? false,
            Hardcore = ReadHardcore(data),
            VersionText = ReadVersionText(data),
            LastPlayed = data["LastPlayed"]?.GetLong() ?? 0,
            DayTime = data["DayTime"]?.GetLong(),
            GameTime = data["Time"]?.GetLong() ?? 0,
            SpawnX = spawnX,
            SpawnY = spawnY,
            SpawnZ = spawnZ,
            Seed = ReadSeed(savePath, data),
            SizeBytes = DirectorySize(savePath),
            RegionFiles = CountRegionFiles(savePath),
            PlayerDataFiles = CountPlayerFiles(savePath),
            Rules = ReadRules(savePath, data),
        };
    }

    public static void Delete(string savePath) => Directory.Delete(savePath, true);

    // ========== 游戏规则 ==========

    internal static bool TryReadNewRules(string savePath, out NbtTag? root, out NbtTag? container)
    {
        root = null;
        container = null;
        string path = GameRulesPath(savePath);
        if (!File.Exists(path)) return false;
        try
        {
            root = NbtFile.Read(path);
            container = root["data"] is { Type: NbtType.Compound } c ? c : null;
            return container != null;
        }
        catch
        {
            return false;
        }
    }

    internal static List<GameRuleEdit> ReadRules(string savePath, NbtTag data)
    {
        if (TryReadNewRules(savePath, out _, out var container))
        {
            return ReadTypedRules(container!);
        }
        return data["GameRules"] is { Type: NbtType.Compound } legacy
            ? ReadStringRules(legacy)
            : new List<GameRuleEdit>();
    }

    /// <summary>新版规则：Byte 当布尔（0/1），其余整数类型按数字，字符串按文本。</summary>
    internal static List<GameRuleEdit> ReadTypedRules(NbtTag container)
    {
        var rules = new List<GameRuleEdit>();
        foreach (var tag in container.Children)
        {
            switch (tag.Type)
            {
                case NbtType.Byte when tag.GetLong() is 0 or 1:
                    rules.Add(new GameRuleEdit
                    {
                        Name = tag.Name,
                        Kind = GameRuleKind.Bool,
                        BoolValue = tag.GetBool(),
                        StoreType = NbtType.Byte,
                    });
                    break;
                case NbtType.Byte:
                case NbtType.Short:
                case NbtType.Int:
                case NbtType.Long:
                    rules.Add(new GameRuleEdit
                    {
                        Name = tag.Name,
                        Kind = GameRuleKind.Int,
                        NumberValue = tag.GetLong(),
                        StoreType = tag.Type,
                    });
                    break;
                case NbtType.String:
                    rules.Add(new GameRuleEdit
                    {
                        Name = tag.Name,
                        Kind = GameRuleKind.Text,
                        TextValue = tag.GetString(),
                        StoreType = NbtType.String,
                    });
                    break;
            }
        }
        SortRules(rules);
        return rules;
    }

    /// <summary>旧版规则：GameRules 复合标签里的字符串值。</summary>
    internal static List<GameRuleEdit> ReadStringRules(NbtTag gameRules)
    {
        var rules = new List<GameRuleEdit>();
        foreach (var tag in gameRules.Children)
        {
            if (tag.Type != NbtType.String) continue;
            string raw = tag.GetString();
            if (bool.TryParse(raw, out bool boolValue))
            {
                rules.Add(new GameRuleEdit { Name = tag.Name, Kind = GameRuleKind.Bool, BoolValue = boolValue });
            }
            else if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long numberValue))
            {
                rules.Add(new GameRuleEdit { Name = tag.Name, Kind = GameRuleKind.Int, NumberValue = numberValue });
            }
            else
            {
                rules.Add(new GameRuleEdit { Name = tag.Name, Kind = GameRuleKind.Text, TextValue = raw });
            }
        }
        SortRules(rules);
        return rules;
    }

    private static void SortRules(List<GameRuleEdit> rules) =>
        rules.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));

    // ========== level.dat 字段兼容 ==========

    internal static bool ReadHardcore(NbtTag data) =>
        data["difficulty_settings"]?["hardcore"]?.GetBool() ?? data["Hardcore"]?.GetBool() ?? false;

    private static bool ReadDifficultyLocked(NbtTag data) =>
        data["difficulty_settings"]?["locked"]?.GetBool() ?? data["DifficultyLocked"]?.GetBool() ?? false;

    private static string ReadDifficulty(NbtTag data)
    {
        var settings = data["difficulty_settings"];
        if (settings != null)
        {
            string name = settings["difficulty"]?.GetString() ?? "";
            if (name.Length > 0) return name;
        }
        var legacy = data["Difficulty"];
        return legacy != null ? legacy.GetInt().ToString(CultureInfo.InvariantCulture) : "";
    }

    private static (int X, int Y, int Z) ReadSpawn(NbtTag data)
    {
        if (data["spawn"]?["pos"] is { Type: NbtType.IntArray } pos && pos.Value is int[] values && values.Length >= 3)
        {
            return (values[0], values[1], values[2]);
        }
        return (data["SpawnX"]?.GetInt() ?? 0, data["SpawnY"]?.GetInt() ?? 0, data["SpawnZ"]?.GetInt() ?? 0);
    }

    private static long ReadSeed(string savePath, NbtTag data)
    {
        var legacy = data["RandomSeed"];
        if (legacy != null) return legacy.GetLong();

        // 新版种子在 data/minecraft/world_gen_settings.dat 的 data/seed
        string path = Path.Combine(savePath, "data", "minecraft", "world_gen_settings.dat");
        if (!File.Exists(path)) return 0;
        try
        {
            var root = NbtFile.Read(path);
            return root["data"]?["seed"]?.GetLong() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadVersionText(NbtTag data)
    {
        var version = data["Version"];
        if (version == null) return "未知";
        if (version.Type == NbtType.Compound)
        {
            string name = version["Name"]?.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(name)) return name;
            var id = version["Id"];
            return id != null ? $"数据版本 {id.GetInt()}" : "未知";
        }
        if (version.Type == NbtType.Int) return $"数据版本 {version.GetInt()}";
        return "未知";
    }

    // ========== 文件统计 ==========

    internal static long DirectorySize(string path)
    {
        try
        {
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* 单个文件读不到忽略 */ }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountRegionFiles(string savePath)
    {
        int count = CountFiles(Path.Combine(savePath, "region"), "*.mca");
        string dimensions = Path.Combine(savePath, "dimensions");
        try
        {
            if (Directory.Exists(dimensions))
            {
                count += Directory.EnumerateFiles(dimensions, "*.mca", SearchOption.AllDirectories)
                    .Count(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "region",
                        StringComparison.OrdinalIgnoreCase));
            }
        }
        catch
        {
            // 统计失败不影响信息展示
        }
        return count;
    }

    private static int CountPlayerFiles(string savePath)
    {
        int legacy = CountFiles(Path.Combine(savePath, "playerdata"), "*.dat");
        int modern = CountFiles(Path.Combine(savePath, "players", "data"), "*.dat");
        return Math.Max(legacy, modern);
    }

    private static int CountFiles(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ========== 写盘 ==========

    /// <summary>先写临时文件再原子替换，避免写一半损坏 NBT。</summary>
    internal static void WriteAtomically(string path, NbtTag root)
    {
        string temp = path + ".tmp";
        try
        {
            NbtFile.Write(temp, root);
            File.Move(temp, path, true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* 清理失败忽略 */ }
            throw;
        }
    }
}
