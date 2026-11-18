#ifndef PublishDir
  #error Supply the self-contained win-x64 publish directory with /DPublishDir.
#endif
#ifndef OutputDir
  #error Supply the installer output directory with /DOutputDir.
#endif

[Setup]
AppId=Raad.Inventory.Windows.Feasibility
AppName=Raad Inventory Feasibility
AppVersion=0.1.0
DefaultDirName={localappdata}\Programs\Raad Inventory Feasibility
DefaultGroupName=Raad Inventory Feasibility
UninstallDisplayIcon={app}\Raad.Inventory.Feasibility.exe
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
DisableProgramGroupPage=yes
CloseApplications=yes
OutputDir={#OutputDir}
OutputBaseFilename=Raad-Inventory-Feasibility-win-x64
Compression=lzma2
SolidCompression=yes

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Raad Inventory Feasibility"; Filename: "{app}\Raad.Inventory.Feasibility.exe"; WorkingDir: "{app}"
