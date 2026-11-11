; 内网快传 LanShare —— Inno Setup 安装脚本
;
; ┌─ 怎么编译 ────────────────────────────────────────────────────────────┐
; │ 必须用项目内打过补丁的 ISCC，不要用系统装的 Inno Setup 6：            │
; │   signing\InnoSetup6-patched\ISCC.exe /DAppVersion=1.1.0 scripts\installer.iss
; │ 正常流程是 scripts\publish.ps1，它会自己选对编译器并传入版本号。      │
; └──────────────────────────────────────────────────────────────────────┘
;
; ┌─ 为什么要用打过补丁的 Inno ──────────────────────────────────────────┐
; │ 部分国产安全软件（联想电脑管家内置的火绒引擎）会拦截「非提权进程      │
; │ 静默写 %TEMP%」，报 Error 5。Inno 默认的 SetupLdr.e32 清单是          │
; │ asInvoker：loader 先把文件解到 %TEMP%，之后才申请 UAC —— 正好撞上。  │
; │ signing\InnoSetup6-patched 里那份 SetupLdr.e32 的 manifest 已被改成   │
; │ requireAdministrator，loader 启动即提权、之后才创建临时目录，绕过拦截。│
; │ 补丁脚本：signing\Patch-InnoSetup-Loader.ps1（校验用，不是每次构建都要跑）。│
; └──────────────────────────────────────────────────────────────────────┘
;
; 版本号只有一个来源：Directory.Build.props 的 <Version>。
; publish.ps1 用 /DAppVersion 传进来，并断言与程序集 InformationalVersion 一致。
; 不要在这里写死版本号。

#define AppName "内网快传 LanShare"
#define AppExeName "LanShare.exe"
#define AppShortName "LanShare"

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

[Setup]
; AppId 一旦发布就不能改 —— Windows 靠它识别「同一个程序」，改了会变成并存两个。
AppId={{4F1C2E8A-9B7D-4C3E-8A21-6D5F0B9C7E42}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=LanShare
AppPublisherURL=https://github.com/cfengli007/LanShare
AppSupportURL=https://github.com/cfengli007/LanShare/issues
AppUpdatesURL=https://github.com/cfengli007/LanShare/releases
VersionInfoVersion={#AppVersion}

; 优先装到 D:\Program Files (x86)\LanShare（有 D: 盘时），否则系统 Program Files。
; 需要一次 UAC 提权（写 Program Files）。右键菜单是纯 HKCU 注册表，
; 不需要代码签名证书、也不需要 MSIX 包。
DefaultDirName={code:GetDefaultDir}
UsePreviousAppDir=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
SetupLogging=yes

OutputDir=..\dist
; 文件名必须与自更新约定一致：LanShare-Setup-<version>-win-x64.exe
; （SelfUpdater.InstallerNamePattern，改这里必须同步改那边）
OutputBaseFilename=LanShare-Setup-{#AppVersion}-win-x64
SetupIconFile=..\src\LanShare.Desktop\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
MinVersion=10.0.19041
ShowLanguageDialog=no

; ⚠️ 关键的一行：告诉 ISCC 不要去校验预编译的 setupldr。
; 我们用的是 manifest 被改过的 SetupLdr.e32，它的 .issig 签名已经不匹配了，
; 不关掉校验会直接编译失败。
DisablePrecompiledFileVerifications=setupldr

[Languages]
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"

[Tasks]
Name: "contextmenu"; Description: "添加资源管理器右键菜单（Windows 11 直接显示在一级菜单中）"; GroupDescription: "资源管理器集成："; Flags: checkedonce
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; 右键菜单：纯 HKCU 注册表，不需要管理员写 HKLM，也不需要证书。
; 三个动词键都指向同一个 IExplorerCommand CLSID（LanShare.Shell.dll 实现），
; 这样 Windows 11 会把它直接显示在一级右键菜单里，并且支持多选批量分享。
Root: HKCU; Subkey: "Software\Classes\*\shell\LanShare"; ValueType: string; ValueName: "MUIVerb"; ValueData: "用内网快传分享"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\LanShare"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName},0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\LanShare"; ValueType: string; ValueName: "ExplorerCommandHandler"; ValueData: "{{D4A7C1E2-9B3F-4E6A-8C21-7F5B0D9A2E31}"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\LanShare"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\Directory\shell\LanShare"; ValueType: string; ValueName: "MUIVerb"; ValueData: "用内网快传分享"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\LanShare"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName},0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\LanShare"; ValueType: string; ValueName: "ExplorerCommandHandler"; ValueData: "{{D4A7C1E2-9B3F-4E6A-8C21-7F5B0D9A2E31}"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\LanShare"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\LanShare"; ValueType: string; ValueName: "MUIVerb"; ValueData: "用内网快传分享"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\LanShare"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName},0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\LanShare"; ValueType: string; ValueName: "ExplorerCommandHandler"; ValueData: "{{D4A7C1E2-9B3F-4E6A-8C21-7F5B0D9A2E31}"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\LanShare"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu

; CLSID -> 随程序分发的 LanShare.Shell.dll
Root: HKCU; Subkey: "Software\Classes\CLSID\{{D4A7C1E2-9B3F-4E6A-8C21-7F5B0D9A2E31}"; ValueType: string; ValueName: ""; ValueData: "LanShare Explorer Command"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\CLSID\{{D4A7C1E2-9B3F-4E6A-8C21-7F5B0D9A2E31}\InprocServer32"; ValueType: string; ValueName: ""; ValueData: "{app}\LanShare.Shell.dll"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\CLSID\{{D4A7C1E2-9B3F-4E6A-8C21-7F5B0D9A2E31}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Apartment"; Tasks: contextmenu

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; 以「原始用户」身份再执行一次注册，确保右键菜单落在真正使用者的 HKCU 下
; （安装包是提权运行的，直接写 HKCU 可能落到管理员账户）。
Filename: "{app}\{#AppExeName}"; Parameters: "--install-context-menu"; StatusMsg: "正在注册右键菜单..."; Flags: runhidden waituntilterminated runasoriginaluser; Tasks: contextmenu
; 安装完成后启动程序（自更新装完也是走这里）
Filename: "{app}\{#AppExeName}"; Description: "启动内网快传"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall-context-menu"; Flags: runhidden waituntilterminated; RunOnceId: "UnregContextMenu"
; 卸载前先杀掉进程，否则文件被占用删不掉（托盘程序不响应普通关闭消息）。
Filename: "{cmd}"; Parameters: "/c taskkill /IM {#AppExeName} /F >nul 2>&1"; Flags: runhidden waituntilterminated; RunOnceId: "KillLanShare"

[UninstallDelete]
; 卸载时清掉运行期产生的日志/缓存，但保留用户分享的存储目录（{userappdata} 下，不在 {app}）。
Type: filesandordirs; Name: "{app}\logs"

[Code]
function GetDefaultDir(Param: string): string;
begin
  if DirExists('D:\') then
    Result := 'D:\Program Files (x86)\LanShare'
  else
    Result := ExpandConstant('{autopf}\LanShare');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  { 关闭正在运行的实例，避免文件占用；托盘程序不会响应普通关闭消息。 }
  Exec(ExpandConstant('{cmd}'), '/c taskkill /IM {#AppExeName} /F >nul 2>&1', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

{ ── .NET 运行时检测 ────────────────────────────────────────────────────
  本程序是 self-contained 发布，正常情况下不需要用户装 .NET。
  这里只做「存在性提示」：检测不到也不阻塞安装（绝不因为误判让用户装不上），
  因为 self-contained 包里自带运行时，缺共享运行时也能跑。
  真正需要拦的情况（比如以后改成 framework-dependent）再改成阻塞。 }
function HasDotNet8DesktopRuntime(): Boolean;
var
  Base: String;
  I: Integer;
begin
  Result := False;
  Base := ExpandConstant('{commonpf}') + '\dotnet\shared\Microsoft.WindowsDesktop.App';

  if DirExists(Base) then
  begin
    for I := 0 to 200 do
      if DirExists(Base + '\8.0.' + IntToStr(I)) then
      begin
        Result := True;
        Exit;
      end;
  end;

  { 8.x 的小版本号不只两位数，兜底再放宽一点检测 }
  if not Result and DirExists(Base) then
    Result := FileExists(Base + '\8.0.0\.version') or
              FileExists(Base + '\8.0.20\.version');
end;

procedure InitializeWizard();
var
  Detected: String;
begin
  { 只是让用户在安装日志里能看到检测结果，不弹窗、不阻塞。 }
  if HasDotNet8DesktopRuntime() then Detected := 'yes' else Detected := 'no';
  Log('LanShare installer: shared .NET 8 Desktop Runtime detected = ' + Detected);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not HasDotNet8DesktopRuntime() then
  begin
    { 不阻塞：self-contained 包自带运行时。只在安装日志与「准备安装」页留痕。 }
    Log('LanShare installer: 未检测到共享 .NET 8 桌面运行时，将使用程序自带的运行时。');
  end;
end;
