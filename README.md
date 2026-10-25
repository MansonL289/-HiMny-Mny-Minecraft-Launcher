# MnyMinecraftLauncher

###### *AI写的README凑合看吧*

一个基于 **WinUI 3 / .NET 8** 的现代化 Minecraft Java 版第三方启动器（Windows x64），支持原版与主流模组加载器的一键安装、多版本管理和离线登录。

## 功能特性

### 下载与安装
- 原版 + **Fabric / Forge / NeoForge** 一键安装，可自动选最新构建或手动挑选指定版本
- 自动检测加载器对所选 MC 版本的适配，**不支持的选项直接禁用**，避免装到一半报错
- **BMCLAPI 镜像加速**（可切换官方源），版本列表、库文件、资源、加载器均走镜像
- 独立**下载任务页**：后台并行下载、实时进度、单任务取消、一键清除已完成

### 启动
- 离线账号（自定义玩家名），完整支持新旧两种启动参数格式（`arguments` / `minecraftArguments`）
- **Java 自动管理**：按版本需求自动选择 Java，缺失时从 Adoptium 自动下载 JRE；老版本（launchwrapper）自动锁定 Java 8，杜绝 `URLClassLoader` 崩溃
- 老版本完整支持：legacy Forge 安装器、pre-1.6 虚拟资源物化（修复老版本无声/崩溃）、log4j 配置
- 启动前**自愈**：缺失文件自动补下载、natives 为空自动重解压、资源未物化自动补齐
- 启动过程可取消（取消即终止游戏进程树），`logs/latest.log` 实时记录
- 自定义游戏窗口分辨率、语言（跟随系统或指定）、启动后自动进入服务器

### 版本管理
- 每个版本独立设置：改名、置顶、删除、打开目录、导出启动脚本（.bat）
- 每版本可覆盖全局设置：Java 路径、内存分配（自动/自定义）、JVM/游戏参数、自动进服、版本隔离
- **Mod 管理**：列表开关直接启用/禁用（`.disabled` 约定）、安装本地 jar、删除、一键打开 mods 目录

### 其他
- 浅色/深色/跟随系统主题、开机自启
- 界面遵循 Fluent Design，标题栏集成下载进度指示

## 运行环境

- Windows 10 1809 (17763) 及以上，x64
- 无需预装 .NET（独立发布）；游戏按需自动下载 JRE 8/17/21 等

## 构建与运行

前置要求：安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 和 Visual Studio 的「Windows 应用 SDK C# 模板」工作负载（或直接装 WinUI 3 开发环境）。

```powershell
dotnet build WinUI3Shell/WinUI3Shell.csproj -p:Platform=x64
WinUI3Shell\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\MnyMinecraftLauncher.exe
```

仓库根目录的 `直接运行用我.py` 可直接启动已构建的程序：

```powershell
python 直接运行用我.py
```

## 打包分发

一条命令打出完整独立包，内置 .NET 8 运行时和 Windows App SDK，对方电脑什么都不用装：

```powershell
dotnet publish WinUI3Shell/WinUI3Shell.csproj -c Release -p:Platform=x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true
```

打包完成后：

1. 产物在 `WinUI3Shell\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\`，约 200 MB
2. 把整个 publish 文件夹压缩成 zip 即可分发，约 80 MB。别只拷 exe，里面的 dll 都是运行必需的
3. 对方解压后双击 `MnyMinecraftLauncher.exe` 就能用

> 说明：csproj 里已内置 CopyXamlArtifactsToPublish 目标，发布时自动补齐 XAML 编译产物，所以上面的命令不需要任何额外步骤，也不会出现发布版闪退的问题。

## 目录结构

```
WinUI3Shell/
├── Pages/                  # 页面：启动、下载、任务、版本详情、版本设置（常规/设置/Mod）
│   ├── HomePage            # 启动页：版本选择、玩家名、进度、取消
│   ├── DownloadsPage       # 下载外壳：游戏版本 / 手动下载 / 下载任务
│   ├── VersionDetailPage   # 安装页：加载器选择 + 构建版本下拉
│   └── VersionSettingsPage # 版本设置外壳
├── Services/Mc/            # 核心逻辑
│   ├── Manifest.cs         # 版本清单
│   ├── Installer.cs        # 原版安装管线（库/natives/资源/log4j）
│   ├── ForgeInstaller.cs   # Forge/NeoForge 安装（新旧两种安装器格式）
│   ├── LoaderInstaller.cs  # Fabric 合并安装
│   ├── DownloadManager.cs  # 后台任务队列
│   ├── Launch.cs           # 启动命令拼接、进程与日志
│   └── Java.cs             # Java 查找/下载、版本范围匹配
└── MainWindow              # 导航外壳、标题栏、主题
```

游戏数据默认存放于 `%USERPROFILE%\.mnymc`（versions / libraries / assets / runtimes / logs）。

## 说明

- 设置保存在注册表 `HKCU\Software\MnyMC`。
