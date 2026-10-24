; phoneMic 安装包脚本（Inno Setup）
;
; 产出：dist\phoneMic-setup.exe —— 一个 exe，双击就能装好，
;       不需要解压、不需要手动找文件。
;
; 说明：VB-CABLE 驱动以官方原版 zip 随包附带，安装时解压到临时目录，
;       再启动它**自己的**安装程序（不是我们静默装），符合其许可要求。

#define MyAppName "phoneMic"
#define MyAppVersion "1.2.1"
#define MyAppExeName "PhoneMic.exe"
#define MyAppPublisher "phoneMic"

[Setup]
AppId={{8C3F1A52-2A7E-4B1D-9F3C-6D2E7B4A9C11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\phoneMic
DefaultGroupName=phoneMic
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=phoneMic-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\PhoneMic\app.ico
; 语言：默认中文
ShowLanguageDialog=no

[Languages]
Name: "zh"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked
Name: "installcable"; Description: "同时安装虚拟声卡 VB-CABLE（推荐，装完需重启一次）"; GroupDescription: "附加任务："

[Files]
Source: "..\out\phoneMic\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 驱动 zip 单独放一份到临时目录，安装过程中解压用
Source: "..\out\phoneMic\driver\VBCABLE_Driver_Pack43.zip"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{group}\phoneMic"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 phoneMic"; Filename: "{uninstallexe}"
Name: "{autodesktop}\phoneMic"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 phoneMic"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; 卸载时顺带把虚拟声卡也卸掉（官方卸载程序，静默）
Filename: "{commonpf}\VB\CABLE\VBCABLE_Setup_x64.exe"; Parameters: "-u -h"; Flags: runhidden skipifdoesntexist
Filename: "{commonpf32}\VB\CABLE\VBCABLE_Setup.exe"; Parameters: "-u -h"; Flags: runhidden skipifdoesntexist

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\phoneMic"

[Code]
procedure RunCableSetup();
var
  ZipPath, TmpDir, Cmd, SetupExe: String;
  ResultCode: Integer;
begin
  ZipPath := ExpandConstant('{tmp}\VBCABLE_Driver_Pack43.zip');
  TmpDir  := ExpandConstant('{tmp}\vbcable');
  ForceDirectories(TmpDir);

  // 用系统自带的 PowerShell 解压，不额外依赖任何东西
  Cmd := '/c powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath ''' +
         ZipPath + ''' -DestinationPath ''' + TmpDir + ''' -Force"';
  if not Exec(ExpandConstant('{cmd}'), Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('解压虚拟声卡驱动失败。' + #13#10 +
           '程序装好后可以在界面里点「安装虚拟声卡」重试。', mbError, MB_OK);
    Exit;
  end;

  SetupExe := TmpDir + '\VBCABLE_Setup_x64.exe';
  if not FileExists(SetupExe) then
    SetupExe := TmpDir + '\VBCABLE_Setup.exe';

  if FileExists(SetupExe) then
  begin
    // 打开官方安装程序本体，由用户在他自己的界面里点 Install Driver
    ShellExec('', SetupExe, '', TmpDir, SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
    MsgBox('虚拟声卡安装程序已结束。' + #13#10#13#10 +
           '如果刚装好，建议重启一次电脑；之后在你的语音输入软件里' + #13#10 +
           '把麦克风选成「CABLE Output」即可。', mbInformation, MB_OK);
  end
  else
    MsgBox('没找到驱动安装程序。程序装好后可以在界面里点「安装虚拟声卡」。', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('installcable') then
  begin
    ExtractTemporaryFile('VBCABLE_Driver_Pack43.zip');
    RunCableSetup();
  end;
end;
