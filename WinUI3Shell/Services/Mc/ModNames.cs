using System;
using System.Collections.Generic;

namespace WinUI3Shell.Services.Mc;

/// <summary>常用 Mod 的官方/社区通用中文名。
/// 机翻专有名词经常出错（Create → "创建"、Just Enough Items → "只要足够的道具"），
/// 命中这里的名字直接用，不再请求翻译接口。</summary>
public static class ModNames
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sodium"] = "钠",
        ["sodium extra"] = "钠扩展",
        ["reeses sodium options"] = "钠选项扩展",
        ["iris shaders"] = "虹膜光影",
        ["iris"] = "虹膜光影",
        ["oculus"] = "目镜",
        ["embeddium"] = "嵌入",
        ["rubidium"] = "铷",
        ["create"] = "机械动力",
        ["just enough items"] = "物品管理器",
        ["jei"] = "物品管理器",
        ["roughly enough items"] = "物品管理器",
        ["rei"] = "物品管理器",
        ["fabric api"] = "织物 API",
        ["mod menu"] = "模组菜单",
        ["cloth config api"] = "布料配置 API",
        ["architectury api"] = "架构 API",
        ["geckolib"] = "Gecko 动画库",
        ["appleskin"] = "苹果皮",
        ["journeymap"] = "旅行地图",
        ["xaero's minimap"] = "Xaero 的小地图",
        ["xaero's world map"] = "Xaero 的世界地图",
        ["lithium"] = "锂",
        ["phosphor"] = "磷光",
        ["starlight"] = "星光",
        ["ferritecore"] = "铁氧体核心",
        ["krypton"] = "氪",
        ["indium"] = "铟",
        ["entity culling"] = "实体剔除",
        ["distant horizons"] = "遥远地平线",
        ["shulker box tooltip"] = "潜影盒提示",
        ["puzzles lib"] = "谜题库",
        ["bookshelf"] = "书架",
        ["mouse tweaks"] = "鼠标手势",
        ["controlling"] = "按键控制",
        ["crafttweaker"] = "合成修改",
        ["ftb quests"] = "FTB 任务",
        ["farmers delight"] = "农夫乐事",
        ["twilight forest"] = "暮色森林",
        ["biomes o plenty"] = "超多生物群系",
        ["biomes o' plenty"] = "超多生物群系",
        ["better combat"] = "更好的战斗",
        ["alexs mobs"] = "亚历克斯的生物",
        ["alex's mobs"] = "亚历克斯的生物",
        ["ice and fire"] = "冰与火",
        ["applied energistics 2"] = "应用能源 2",
        ["mekanism"] = "通用机械",
        ["thermal expansion"] = "热力膨胀",
        ["tinkers construct"] = "匠魂",
        ["tinkers' construct"] = "匠魂",
        ["botania"] = "植物魔法",
        ["blood magic"] = "血魔法",
        ["storage drawers"] = "抽屉",
        ["quark"] = "夸克",
        ["serene seasons"] = "宁静季节",
        ["optifine"] = "高清修复",
        ["zoomify"] = "缩放",
        ["dynamic lights"] = "动态光源",
        ["sound physics remastered"] = "声音物理重制版",
        ["sophisticated backpacks"] = "精致背包",
        ["not enough animations"] = "动画增强",
        ["jade"] = "玉",
        ["wthit"] = "WTHIT",
        ["cloth config"] = "布料配置",
    };

    /// <summary>返回已知中文名；不认识返回空串。</summary>
    public static string Zh(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        return Table.TryGetValue(title.Trim(), out string? zh) ? zh : "";
    }
}
