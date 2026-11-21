[Setup]
AppId={{9C8616E8-7FEF-4F65-91B8-0FF03C5B8097}
AppName=GestureFlow
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\GestureFlow
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.22000
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
UninstallDisplayIcon={app}\Desktop\GestureFlow.Desktop.exe
CloseApplications=yes
RestartApplications=no
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputBaseFilename=GestureFlow-{#AppVersion}-win-x64-setup

[Languages]
Name: "zhcn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "zhtw"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"

[Files]
Source: "{#PackageDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\GestureFlow\GestureFlow"; Filename: "{app}\Desktop\GestureFlow.Desktop.exe"; WorkingDir: "{app}\Desktop"
Name: "{userprograms}\GestureFlow\{cm:SimulatorName}"; Filename: "{app}\Lab\GestureFlow.Lab.exe"; WorkingDir: "{app}\Lab"
Name: "{userdesktop}\GestureFlow"; Filename: "{app}\Desktop\GestureFlow.Desktop.exe"; WorkingDir: "{app}\Desktop"; Tasks: desktopicon

[Run]
Filename: "{app}\Desktop\GestureFlow.Desktop.exe"; WorkingDir: "{app}\Desktop"; Description: "{cm:LaunchProgram,GestureFlow}"; Flags: nowait postinstall skipifsilent

[CustomMessages]
zhcn.SimulatorName=GestureFlow 模拟器
zhtw.SimulatorName=GestureFlow 模擬器
en.SimulatorName=GestureFlow Simulator

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupLink, ShortcutTarget: String;
  Shell, Shortcut: Variant;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  StartupLink := ExpandConstant('{userstartup}\GestureFlow.Desktop.lnk');
  if not FileExists(StartupLink) then
    Exit;

  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(StartupLink);
    ShortcutTarget := Shortcut.TargetPath;
    if SameText(ShortcutTarget, ExpandConstant('{app}\Desktop\GestureFlow.Desktop.exe')) then
      if not DeleteFile(StartupLink) then
        Log('Could not remove the GestureFlow startup shortcut.');
  except
    Log('Could not check the GestureFlow startup shortcut: ' + GetExceptionMessage);
  end;
end;
