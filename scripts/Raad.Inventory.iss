#ifndef PublishDir
  #error Supply the self-contained win-x64 publish directory with /DPublishDir.
#endif
#ifndef OutputDir
  #error Supply the installer output directory with /DOutputDir.
#endif

; Per-user installer for the Raad Inventory preview. It never needs administrator rights and it
; never writes into Program Files: the business data stays in %LOCALAPPDATA%\RaadInventory.

[Setup]
AppId=Raad.Inventory.Windows.Preview
AppName=Raad Inventory (Preview)
AppVersion=0.2.0-preview
AppVerName=Raad Inventory (Preview) 0.2.0-preview
DefaultDirName={localappdata}\Programs\Raad Inventory
DefaultGroupName=Raad Inventory
UninstallDisplayIcon={app}\Raad.Inventory.Feasibility.exe
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
DisableProgramGroupPage=yes
DisableDirPage=no
CloseApplications=yes
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename=Raad-Inventory-0.2.0-preview-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Raad Inventory (Preview)

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Raad Inventory (Preview)"; Filename: "{app}\Raad.Inventory.Feasibility.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall Raad Inventory (Preview)"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\Raad.Inventory.Feasibility.exe"; Description: "Run Raad Inventory"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
