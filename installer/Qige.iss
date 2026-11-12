#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir is required
#endif
#ifndef NumericVersion
  #define NumericVersion "0.1.0.0"
#endif

[Setup]
AppId={{A85C0513-D51D-44C3-A547-6C21BB9AA05D}
AppName=栖格 Qige
AppVersion={#AppVersion}
AppPublisher=LaoQiTX
AppPublisherURL=https://github.com/LaoQiTX/Qige
AppSupportURL=https://github.com/LaoQiTX/Qige/issues
DefaultDirName={code:GetDefaultInstallDir}
DefaultGroupName=栖格 Qige
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
AppMutex=Local\Qige.Application
SetupMutex=Local\Qige.Setup
CloseApplications=no
RestartApplications=no
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
SetupIconFile={#SourcePath}..\assets\brand\qige.ico
UninstallDisplayIcon={app}\Qige.exe
UninstallDisplayName=栖格 Qige
VersionInfoVersion={#NumericVersion}
OutputDir={#InstallerOutputDir}
OutputBaseFilename=Qige-{#AppVersion}-setup-win-x64

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Messages]
WelcomeLabel2=本向导将在您选择的目录中安装栖格 Qige。%n%n收纳的文件、桌面快捷方式和配置统一保存在安装目录内的 QigeData 文件夹。您可以直接打开此文件夹查看内容。%n%n升级和卸载均保留 QigeData。请先在旧版中“退出并归还全部”，再继续安装。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Excludes: "QigeData\*,Data\*,Storage\*,TestData\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\DesktopGroups.exe"
Type: files; Name: "{app}\DesktopGroups.dll"
Type: files; Name: "{app}\DesktopGroups.deps.json"
Type: files; Name: "{app}\DesktopGroups.runtimeconfig.json"

[Icons]
Name: "{group}\栖格 Qige"; Filename: "{app}\Qige.exe"; WorkingDir: "{app}"
Name: "{group}\栖格的数据文件夹"; Filename: "{app}\QigeData"; WorkingDir: "{app}"
Name: "{autodesktop}\栖格 Qige"; Filename: "{app}\Qige.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Dirs]
Name: "{app}\QigeData"; Flags: uninsneveruninstall

[Run]
Filename: "{app}\Qige.exe"; Description: "运行栖格 Qige"; Flags: nowait postinstall skipifsilent

; QigeData is never included in [UninstallDelete]. Only installed program files are removed.
[Code]
function GetDefaultInstallDir(Param: String): String;
var
  Drive: String;
begin
  Drive := ExtractFileDrive(ExpandConstant('{srcexe}'));
  if (Length(Drive) <> 2) or (Copy(Drive, 2, 1) <> ':') then
    Drive := ExtractFileDrive(ExpandConstant('{sd}'));
  Result := AddBackslash(Drive) + 'Qige';
end;

function DataIsBusy: Boolean;
var
  Paths: array[0..1] of String;
  Index: Integer;
  Contents: AnsiString;
begin
  Result := False;
  Paths[0] := ExpandConstant('{app}\QigeData\recovery\session.lock');
  Paths[1] := ExpandConstant('{app}\QigeData\recovery\demo-session.lock');
  for Index := 0 to 1 do
    if FileExists(Paths[Index]) then
    begin
      if not LoadStringFromFile(Paths[Index], Contents) then
      begin
        Result := True;
        Exit;
      end;
    end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine +
    '文件、桌面快捷方式和配置的位置：' + NewLine + Space +
    ExpandConstant('{app}\QigeData') + NewLine + NewLine +
    '升级与卸载保留此数据文件夹。请勿在仍收纳内容时删除它。' + NewLine + NewLine + MemoTasksInfo;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Probe: String;
begin
  Result := '';
  if CheckForMutexes('Local\Qige.Application') or DataIsBusy then
  begin
    Result := '栖格仍在运行或 QigeData 被占用。请在应用中选择“退出并归还全部”，然后重试。';
    Exit;
  end;
  try
    if not ForceDirectories(ExpandConstant('{app}')) then
      RaiseException('无法创建安装目录。');
    Probe := ExpandConstant('{app}\.qige-install-write-check.tmp');
    if FileExists(Probe) then RaiseException('安装探测文件已存在。');
    if not SaveStringToFile(Probe, 'Qige installer write probe', False) then
      RaiseException('无法写入安装目录。');
    if not DeleteFile(Probe) then RaiseException('无法删除安装探测文件。');
  except
    Result := '所选安装目录不可写。请选择您拥有写入权限的目录；QigeData 将保存在该目录内。';
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := not CheckForMutexes('Local\Qige.Application') and not DataIsBusy;
  if not Result then
    SuppressibleMsgBox('请先在栖格中选择“退出并归还全部”，再卸载。QigeData 将被保留。', mbError, MB_OK, IDOK);
end;

procedure RemoveKnownStartup;
var
  Names: TArrayOfString;
  Index: Integer;
  Value, DataCommand, OldDataCommand, OldRootCommand: String;
begin
  DataCommand := ExpandConstant('"{app}\Qige.exe" --data-dir "{app}\QigeData" --background');
  OldDataCommand := ExpandConstant('"{app}\DesktopGroups.exe" --data-dir "{app}\QigeData" --background');
  OldRootCommand := ExpandConstant('"{app}\DesktopGroups.exe" --root "{app}" --background');
  Log('Qige startup cleanup for: ' + DataCommand);
  if RegGetValueNames(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Run', Names) then
    for Index := 0 to GetArrayLength(Names) - 1 do
      if (Copy(Names[Index], 1, 5) = 'Qige-') or (Copy(Names[Index], 1, 14) = 'DesktopGroups-') then
        if RegQueryStringValue(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Run', Names[Index], Value) then
        begin
          Log('Qige startup candidate ' + Names[Index] + ': ' + Value);
          if (Value = DataCommand) or (Value = OldDataCommand) or (Value = OldRootCommand) then
            RegDeleteValue(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Run', Names[Index]);
        end;
end;

procedure RemoveKnownLogonTasks;
var
  Service, Folder, Tasks, Task, Actions, Action: Variant;
  Index: Integer;
  ExpectedPath, ExpectedArgs: String;
begin
  ExpectedPath := ExpandConstant('{app}\Qige.exe');
  ExpectedArgs := ExpandConstant('--data-dir "{app}\QigeData" --background');
  try
    Service := CreateOleObject('Schedule.Service');
    Service.Connect;
    Folder := Service.GetFolder('\');
    Tasks := Folder.GetTasks(1);
    for Index := Tasks.Count downto 1 do
    begin
      Task := Tasks.Item(Index);
      if Copy(String(Task.Name), 1, 5) = 'Qige-' then
      begin
        Actions := Task.Definition.Actions;
        if (String(Task.Definition.RegistrationInfo.Source) = 'Qige') and (Actions.Count = 1) then
        begin
          Action := Actions.Item(1);
          if (String(Action.Path) = ExpectedPath) and (String(Action.Arguments) = ExpectedArgs) then
          begin
            Log('Removing matching Qige logon task: ' + String(Task.Name));
            Folder.DeleteTask(Task.Name, 0);
          end;
        end;
      end;
    end;
  except
    Log('Qige logon-task cleanup: ' + GetExceptionMessage);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveKnownStartup;
    RemoveKnownLogonTasks;
    if not UninstallSilent then
      MsgBox(ExpandConstant('程序已卸载。您的文件、桌面快捷方式和配置仍保留在：{app}\QigeData'), mbInformation, MB_OK);
  end;
end;
