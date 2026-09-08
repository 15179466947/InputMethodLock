; Inno Setup 6 打包脚本 —— 输入法锁定（按当前用户安装，无需管理员权限）
; 先运行 build-release.cmd 生成 release\ 目录，再用 ISCC 编译本脚本

#define MyAppName "输入法锁定"
#define MyAppNameEn "InputMethodLock"
#define MyAppVersion "1.0.0"
#define MyAppExeName "InputMethodLock.exe"

[Setup]
; AppId 前双写 {{ 是 Inno 的字面量大括号转义
AppId={{7A4C2F9E-1D3B-4E86-9C05-2F8B6A1D4E37}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={localappdata}\Programs\{#MyAppNameEn}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\dist
OutputBaseFilename={#MyAppNameEn}-{#MyAppVersion}-setup
Compression=lzma2
SolidCompression=yes
; 按当前用户安装：无需 UAC，与程序的 HKCU 自启/配置存储一致
PrivilegesRequired=lowest
; 卸载时若程序正在运行，提示关闭
CloseApplications=yes
RestartApplications=no

[Languages]
; 简体中文语言包（Inno 默认安装不含，随仓库附带 packaging\ChineseSimplified.isl）
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked
Name: "autostart"; Description: "开机自动运行（与程序内设置共用同一开关）"

[Files]
Source: "..\release\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\release\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 与 AutoStart.cs 写的是同一个 HKCU Run 值，程序内开关与安装选项互通
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "InputMethodLock"; \
    ValueData: """{app}\{#MyAppExeName}"""; \
    Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; 停止正在运行的实例，避免文件占用
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillApp"
