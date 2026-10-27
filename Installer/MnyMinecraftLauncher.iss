; MnyMinecraftLauncher 安装包脚本（Inno Setup 6）
; 用法：先运行 dotnet publish 生成 publish 目录，再用 Inno Setup 编译本文件
;   dotnet publish WinUI3Shell\WinUI3Shell.csproj -c Release -p:Platform=x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true

#define AppName "MnyMinecraftLauncher"
#define AppVersion "1.0.0"
#define AppPublisher "MNY"
#define AppURL "https://github.com/MansonL289/HiMny-MnyMinecraftLauncher"
#define AppExeName "MnyMinecraftLauncher.exe"
; 注意：Inno Setup 不认路径里的 [MNY] 方括号，所以图标和发布文件都从无括号的中转目录取
; Tools\pub 是 publish 目录的副本，Tools\AppIcon.ico 是图标的副本（编译前可用 robocopy 刷新）
#define PublishDir "d:\0Mny\Tools\pub"
#define IconFile "d:\0Mny\Tools\AppIcon.ico"

; 应用唯一标识，生成后不要再改，否则卸载/升级会错乱
#define AppId "{{8F3A2B71-4C6D-4E9A-B2F1-7D5C9E0A1B3C}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=d:\0Mny
OutputBaseFilename=MnyMinecraftLauncherSetup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent
