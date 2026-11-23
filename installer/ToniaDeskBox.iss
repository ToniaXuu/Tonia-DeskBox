; Tonia-DeskBox 安装包脚本（Inno Setup 6）
;
; 设计要点 —— 全部围绕「覆盖安装不能丢用户数据」：
;   1. 程序装进 %LOCALAPPDATA%\Programs\Tonia-DeskBox（每个用户自己的位置，无需管理员，
;      自动更新因此也不会弹 UAC）。
;   2. AppId 固定，版本号只改 AppVersion：再次运行安装包就是「原地升级」，
;      不会装出第二份，也不会换目录（UsePreviousAppDir）。
;   3. 安装目录里只有 exe 和一个标记文件。配置、日志、盒子里收纳的真实文件
;      全在 %LOCALAPPDATA%\Tonia-DeskBox 下，安装包从不读也不写那里。
;   4. 卸载同样不碰用户数据，所以「卸载再装新版本」也不会丢盒子。
;   5. 静默安装（程序内点「立即更新」走这条路径）结束后自动把程序重新拉起来。
;
; 编译：ISCC.exe installer\ToniaDeskBox.iss

#define AppName        "Tonia-DeskBox"
; 版本号默认值与 csproj 的 <Version> 保持一致；
; CI 或命令行可用 ISCC /DAppVersion=0.3.0 覆盖，避免手改多处。
#ifndef AppVersion
  #define AppVersion   "0.3.0"
#endif
#define AppPublisher   "Tonia"
#define AppExeName     "ToniaDeskBox.exe"
; AppId 一旦发布就不能改：它是「同一个程序」的身份，改了等于另装一个新程序
#define AppId          "{{A3E7B2C4-5D18-4F6A-9B21-7C8D4E6F1023}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/ToniaXuu/Tonia-DeskBox
AppSupportURL=https://github.com/ToniaXuu/Tonia-DeskBox/issues
AppUpdatesURL=https://github.com/ToniaXuu/Tonia-DeskBox/releases

; 每用户安装：不触发 UAC，程序内的静默更新才能无人值守完成
DefaultDirName={localappdata}\Programs\Tonia-DeskBox
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
; 升级时沿用上一次的安装目录，绝不因为换了个默认路径就装出两份
UsePreviousAppDir=yes
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableWelcomePage=yes
AllowNoIcons=yes
; 每次安装都按脚本默认值走，不沿用上次的任务勾选。
; 否则首次安装一旦勾过「桌面快捷方式」，之后每次静默更新都会再往桌面丢一个图标。
UsePreviousTasks=no

OutputDir=..\dist
OutputBaseFilename=ToniaDeskBox-Setup-{#AppVersion}
SetupIconFile=..\src\ToniaDeskBox\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; 覆盖安装时如果程序还开着，由 [Code] 里的 InitializeSetup 处理：
; 本程序是 ShutdownMode=OnExplicitShutdown 的托盘应用，窗口全关掉进程也不会退出，
; 用 Restart Manager（CloseApplications）只会让它「假死」并卡住安装，
; 所以这里彻底交给自己的结束逻辑。
CloseApplications=no
; 由 [Run] 段决定安装后是否重启程序
RestartApplications=no

VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=Copyright (C) 2026 {#AppPublisher}

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; 自包含单文件 exe，安装目录里就这一个可执行文件，升级只需替换它
Source: "..\publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; 标记文件：程序据此把配置与数据放在用户目录，而不是安装目录
Source: "app-installed.marker"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
; 桌面快捷方式只在交互式安装且用户勾选时创建：
; 静默更新是程序自己发起的，不该替用户往桌面丢东西。
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon; Check: not WizardSilent

[Run]
; 交互式安装：完成页上的「运行」勾选项
Filename: "{app}\{#AppExeName}"; Description: "运行 {#AppName}"; Flags: nowait postinstall skipifsilent
; 静默更新：安装完成后把程序重新拉起来。
; 这一条不加 postinstall，所以静默安装时也会执行；用 IsSilentInstall 防止交互式安装重复启动。
Filename: "{app}\{#AppExeName}"; Flags: nowait; Check: IsSilentInstall

[Code]
var
  // 交互式安装时用户是否同意关闭正在运行的程序
  AllowStopRunningApp: Boolean;

function IsSilentInstall: Boolean;
begin
  Result := WizardSilent;
end;

/// 让正在运行的实例退出。
///
/// 首选「请它自己退」：执行 `ToniaDeskBox.exe --quit`，程序收到命名信号后会
/// 保存配置、移除托盘图标再退出——比强杀干净得多。
///
/// 本程序是 ShutdownMode=OnExplicitShutdown 的托盘应用，窗口全关掉进程仍驻留，
/// 所以发 WM_CLOSE 或用 Restart Manager 都不可靠（后者会让它半死不活地卡住安装）。
///
/// 注意：这里用到 {app}，只能在 PrepareToInstall 之后调用；
/// 放进 InitializeSetup 会抛「expanded the app constant before it was initialized」。
procedure StopRunningApp;
var
  ResultCode: Integer;
begin
  // 必须 ewNoWait：不支持 --quit 的旧版本会弹「已在运行」对话框停在那里，
  // 用 ewWaitUntilTerminated 会让安装程序一直等它，整个安装卡死。
  Exec(ExpandConstant('{app}\{#AppExeName}'), '--quit', '', SW_HIDE, ewNoWait, ResultCode);

  // 留出保存配置 + 释放托盘 + 关闭进程的时间
  Sleep(2500);

  // 兜底：从古早版本升级时（不认识 --quit）强杀一次，同样不等待。
  // 进程不存在时 taskkill 直接返回，无副作用。
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ToniaDeskBox.exe',
    '', SW_HIDE, ewNoWait, ResultCode);
  Sleep(600);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  AllowStopRunningApp := True;

  // 静默更新由程序自己发起，不打扰用户
  if WizardSilent then Exit;

  if MsgBox('安装程序会先关闭正在运行的 Tonia-DeskBox，再替换程序文件。' + #13#10#13#10 +
            '盒子里的文件和你的设置都存放在独立的数据目录，安装不会改动它们；' +
            '安装完成后程序会自动重新打开。' + #13#10#13#10 +
            '继续安装吗？',
            mbConfirmation, MB_YESNO) <> IDYES then
    Result := False;
end;

/// 放在这里而不是 InitializeSetup：此时 {app} 已经确定，可以正常调用程序。
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if AllowStopRunningApp then
    StopRunningApp;
end;
