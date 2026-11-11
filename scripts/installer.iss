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
; ┌─ 发布方式 ────────────────────────────────────────────────────────────┐
; │ framework-dependent（框架依赖）：dist\app 约 5 MB，安装包约 2 MB。      │
; │ 目标机必须装 .NET 8 桌面运行时，由下面的 InitializeSetup 检测并引导。  │
; │ 想改回 self-contained：改 scripts\publish.ps1 的 --self-contained 为   │
; │ true，并把下面 InitializeSetup 的阻塞逻辑改回「只记日志」。             │
; └──────────────────────────────────────────────────────────────────────┘
;
; 版本号只有一个来源：Directory.Build.props 的 <Version>。
; publish.ps1 用 /DAppVersion 传进来，并断言与程序集 InformationalVersion 一致。
; 不要在这里写死版本号。

#define AppName "内网快传 LanShare"
#define AppExeName "LanShare.exe"
#define AppShortName "LanShare"

#ifndef AppVersion
  #define AppVersion "1.1.1"
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

{ ── .NET 8 桌面运行时检测 ──────────────────────────────────────────────
  本程序是 framework-dependent 发布（产物约 5 MB，而不是自包含的 188 MB），
  运行时由微软的共享框架提供。所以这里必须**真正拦下来**：
  没装运行时的话，装完双击 exe 会直接弹「需要 .NET」的报错，体验很差。

  ⚠️ 这里有四个必须避开的坑，全部实测过（本机 1920x1200 / .NET 8.0.20 + 8.0.33 + 9.0.5）：

  坑 1｜Inno 安装器是 32 位进程，注册表访问会被 WOW64 重定向。
        实测探针结果：
          RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\dotnet\...')            -> TRUE  （其实是 32 位视图！）
          RegKeyExists(HKLM64,            'SOFTWARE\dotnet\...')            -> FALSE （64 位视图根本没这个键）
          RegKeyExists(HKLM32,            'SOFTWARE\dotnet\...')            -> TRUE
        .NET 的 sharedfx 版本列表**只登记在 32 位视图**（WOW6432Node）。
        所以「64 位视图优先」是假的，必须显式用 HKLM32 / HKLM64 两个常量分别查，
        不能指望默认的 HKEY_LOCAL_MACHINE 能代表 64 位。

  坑 2｜commonpf 常量在纯 32 位安装模式下解析成 C:\Program Files (x86)。
        实测：commonpf = C:\Program Files (x86)。虽然脚本设了
        ArchitecturesInstallIn64BitMode，但探测函数在 InitializeSetup 阶段运行，
        那时安装模式还没定，commonpf 仍是 x86 那个。
        所以目录探测必须**两个路径都查**，不能只查 commonpf。

  坑 3｜RegKeyExists 只看「键在不在」，不看「里面有没有 8.x」。
        WindowsDesktop.App 键在只装了 .NET 9 的机器上**照样存在**（本机就同时有 9.0.5），
        用它判断会误放行。必须逐个子键找 8. 前缀。

  坑 4｜只数 8.0.0..8.0.99 会漏掉将来的 8.0.100+。
        改成直接枚举 Base 下的所有子目录/子键，匹配 '8.' 前缀。 }

const
  DotNet8DownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/8.0';

{ 枚举 Base 下所有子目录，返回是否存在 '8.' 开头的（.NET 8 的版本目录形如 8.0.20）。 }
function DirHas8x(const Base: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(Base) then Exit;
  if FindFirst(AddBackslash(Base) + '*', FindRec) then
  try
    repeat
      if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
        if Pos('8.', FindRec.Name) = 1 then
        begin
          Result := True;
          Exit;
        end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

{ 枚举某注册表键下的所有值名，返回是否存在 '8.' 开头的。
  .NET 安装器把每个已装版本登记成一个 REG_DWORD 值，值名就是版本号。 }
function RegHas8x(RootKey: Integer; const SubKey: String): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if not RegKeyExists(RootKey, SubKey) then Exit;
  if not RegGetValueNames(RootKey, SubKey, Names) then Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
    if Pos('8.', Names[I]) = 1 then
    begin
      Result := True;
      Exit;
    end;
end;

{ 检测 Microsoft.WindowsDesktop.App 8.x（WPF 靠的是它，不是 NETCore.App）。 }
function HasDotNet8DesktopRuntime(): Boolean;
var
  SubKey: String;
begin
  Result := False;
  SubKey := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';

  { 1) 注册表：32 位视图（.NET 实际登记在这里）+ 64 位视图，两个都按「有没有 8.x 值」判断 }
  if RegHas8x(HKLM32, SubKey) then begin Result := True; Exit; end;
  if RegHas8x(HKLM64, SubKey) then begin Result := True; Exit; end;

  { 2) 目录：64 位安装位置 + 32 位安装位置，两个都查（见坑 2） }
  if DirHas8x('C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App') then
  begin Result := True; Exit; end;
  if DirHas8x('C:\Program Files (x86)\dotnet\shared\Microsoft.WindowsDesktop.App') then
  begin Result := True; Exit; end;
end;

procedure InitializeWizard();
var
  Detected: String;
begin
  if HasDotNet8DesktopRuntime() then Detected := 'yes' else Detected := 'no';
  Log('LanShare installer: shared .NET 8 Desktop Runtime detected = ' + Detected);
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
  Message: String;
begin
  Result := True;
  if HasDotNet8DesktopRuntime() then Exit;

  Message :=
    '内网快传需要 .NET 8 桌面运行时（Microsoft .NET Desktop Runtime 8.0），' + #13#10 +
    '当前系统未检测到。' + #13#10 + #13#10 +
    '点「是」会打开官方下载页，装好运行时后再运行本安装程序即可。' + #13#10 +
    '点「否」退出安装。' + #13#10 + #13#10 +
    '提示：这是一个约 55 MB 的微软官方安装程序，装一次即可，' + #13#10 +
    '之后内网快传自身的更新不会再要求它。';

  if MsgBox(Message, mbConfirmation, MB_YESNO) = IDYES then
  begin
    ShellExec('open', DotNet8DownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;

  { 无论用户点哪个，都退出本次安装 —— 没有运行时装下去也用不了。 }
  Result := False;
end;
