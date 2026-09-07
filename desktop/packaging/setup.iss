#ifndef PublishDir
  #error PublishDir must be provided
#endif
#ifndef OutputDir
  #error OutputDir must be provided
#endif
[Setup]
AppId={{BC7D9E66-230B-4F78-B9F3-CF362A53B248}
AppName=点钞机运行状态监测与故障管理系统
AppVersion=0.4.0
AppPublisher=点钞机软件项目
DefaultDirName={localappdata}\Programs\MoneyCounterMonitor
DefaultGroupName=点钞机运行状态监测与故障管理系统
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=MoneyCounterMonitor-0.4.0-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\MoneyCounter.Desktop.exe
CloseApplications=yes
RestartApplications=no
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\点钞机运行状态监测与故障管理系统"; Filename: "{app}\MoneyCounter.Desktop.exe"
Name: "{userdesktop}\点钞机运行状态监测与故障管理系统"; Filename: "{app}\MoneyCounter.Desktop.exe"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
