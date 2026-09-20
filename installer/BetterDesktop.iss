; ============================================================
;  Better Desktop Cordis — Inno Setup 安装器
;
;  标准 Windows 安装程序（user-level，无需管理员）：
;    - 组件选择：主程序（必装）/ 截屏 / 剪贴板面板
;    - 文件放置：由安装器复制到 {app}（默认 %LOCALAPPDATA%\BetterDesktop\app\<build>）
;    - 系统集成：安装后调用 install-betterdesktop.ps1 -SkipCopy
;                （注册右键菜单 / 托盘自启 / 启动托盘 / 重启资源管理器）
;    - 卸载：调用 uninstall-betterdesktop.ps1 -KeepInstallFiles 清理注册，
;            文件由安装器卸载程序删除
;
;  编译（ISCC，Inno Setup 7）：
;    & "C:\Program Files\Inno Setup 7\ISCC.exe" installer\BetterDesktop.iss
;       [/DBuild=2026.09.18.0257] [/DSourceDir=dist\modules\BetterDesktop-xxx-modules]
;
;  验证用参数：BetterDesktop-Setup.exe /SKIPPOSTINSTALL /DIR=测试目录
;              （只放置文件，不执行安装脚本）
; ============================================================

#define MyAppName "Better Desktop Cordis"
#define MyAppVersion "1.3.0"
#ifndef Build
  #define Build "2026.09.18.0257"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\modules\BetterDesktop-2026.09.18.0305-modules"
#endif

[Setup]
AppId={{8E5C6D4A-2F3B-4A9E-8C7D-1B2A3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Better Desktop Cordis
DefaultDirName={localappdata}\BetterDesktop\app\{#Build}
DefaultGroupName=Better Desktop Cordis
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist\installer
OutputBaseFilename=BetterDesktop-Setup-{#MyAppVersion}-{#Build}
SetupIconFile=..\host\Assets\BetterDesktop.ico
UninstallDisplayIcon={app}\BetterDesktop.Tray.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Types]
Name: "full"; Description: "完整安装（主程序 + 截屏 + 剪贴板面板）"
Name: "compact"; Description: "仅主程序"
Name: "custom"; Description: "自定义"

[Components]
Name: "main"; Description: "主程序（桌面外壳 / 菜单栏 / Dock / 搜索 / 灵动岛 / 热键 / 剪贴板 / 转换引擎）"; Types: full compact custom; Flags: fixed
Name: "capture"; Description: "截屏组件（独立进程 BetterDesktop.Capture.exe）"; Types: full custom
Name: "clipboard"; Description: "剪贴板历史面板（独立进程 BetterDesktop.Clipboard.Panel.exe）"; Types: full custom

[Files]
Source: "{#SourceDir}\01-主程序\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: main
Source: "{#SourceDir}\02-截屏\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: capture
Source: "{#SourceDir}\03-剪贴板面板\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: clipboard

[Run]
; 安装完成后注册系统集成并启动（SkipCopy：文件已由安装器放置）
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-betterdesktop.ps1"" -Source ""{app}"" -SkipCopy"; WorkingDir: "{app}"; StatusMsg: "正在注册系统集成并启动……"; Flags: runascurrentuser waituntilterminated; Check: NotSkipPostInstall

[UninstallRun]
; 卸载时先清理注册与停止组件（保留文件，由安装器删除）
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall-betterdesktop.ps1"" -KeepInstallFiles"; WorkingDir: "{app}"; Flags: runascurrentuser waituntilterminated runhidden

[Icons]
Name: "{group}\Better Desktop Cordis"; Filename: "{app}\BetterDesktop.Tray.exe"
Name: "{group}\设置中心"; Filename: "{app}\BetterDesktop.Settings.exe"
Name: "{group}\卸载 Better Desktop Cordis"; Filename: "{uninstallexe}"

[Code]
// 验证模式：/SKIPPOSTINSTALL 时不执行安装脚本（只放置文件）
function CmdLineParamExists(const Param: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Param) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function NotSkipPostInstall(): Boolean;
begin
  Result := not CmdLineParamExists('/SKIPPOSTINSTALL');
end;
