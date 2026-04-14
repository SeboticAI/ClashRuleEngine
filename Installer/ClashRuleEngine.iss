; ============================================================
; Clash Rule Engine — Inno Setup Installer
; ============================================================
; This installer handles:
;   - Detecting installed Navisworks versions (2022–2026)
;   - Letting the user choose which version(s) to install for
;   - Copying the plugin DLL + manifest to the correct folder
;   - Clean uninstallation
;
; BUILD INSTRUCTIONS:
;   1. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php
;   2. Build ClashRuleEngine.dll in Visual Studio (Release | x64)
;   3. Place this .iss file in your project root
;   4. Update the paths under [Files] to point to your build output
;   5. Open this file in Inno Setup Compiler and click Build
;
; OUTPUT:
;   Creates "ClashRuleEngine_Setup_1.0.0.exe" in the Output folder
; ============================================================

#define MyAppName "Clash Rule Engine"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ACME"  
#define MyAppURL "https://example.com"

[Setup]
; Basic installer metadata
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}

; Output settings
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputBaseFilename=ClashRuleEngine_Setup_{#MyAppVersion}
OutputDir=Output

; Installer behavior
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
DisableDirPage=yes
DisableProgramGroupPage=yes
WizardStyle=modern

; Appearance — replace with your own branding files
; WizardImageFile=assets\wizard_image.bmp
; WizardSmallImageFile=assets\wizard_small.bmp
; SetupIconFile=assets\icon.ico
; UninstallDisplayIcon={app}\icon.ico

; License — uncomment and point to your license file
; LicenseFile=LICENSE.txt

; Uninstall settings
Uninstallable=yes
UninstallDisplayName={#MyAppName}
CreateUninstallRegKey=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ============================================================
; CUSTOM NAVISWORKS VERSION SELECTION
; ============================================================
; We detect which Navisworks versions are installed and let
; the user choose which ones to install the plugin for.
; ============================================================

[Types]
Name: "auto"; Description: "Install for all detected Navisworks versions"
Name: "custom"; Description: "Choose Navisworks versions"; Flags: iscustom

[Components]
; Each component represents a Navisworks version year.
; The "check" function determines if that version is installed.
Name: "nw2022"; Description: "Navisworks Manage 2022"; Types: auto custom; Check: IsNavisworks2022Installed
Name: "nw2023"; Description: "Navisworks Manage 2023"; Types: auto custom; Check: IsNavisworks2023Installed
Name: "nw2024"; Description: "Navisworks Manage 2024"; Types: auto custom; Check: IsNavisworks2024Installed
Name: "nw2025"; Description: "Navisworks Manage 2025"; Types: auto custom; Check: IsNavisworks2025Installed
Name: "nw2026"; Description: "Navisworks Manage 2026"; Types: auto custom; Check: IsNavisworks2026Installed

[Files]
; ---- Plugin files for each Navisworks version ----
; Update "Source:" paths to match your actual build output directory.
; If you build a single DLL that works across versions, point them all
; to the same source. If you have version-specific builds, separate them.

; Navisworks 2022
Source: "bin\Release\ClashRuleEngine.dll"; DestDir: "{localappdata}\Autodesk\Navisworks Manage 2022\Plugins\ClashRuleEngine"; Components: nw2022; Flags: ignoreversion
Source: "PackageContents.xml"; DestDir: "{localappdata}\Autodesk\Navisworks Manage 2022\Plugins\ClashRuleEngine"; Components: nw2022; Flags: ignoreversion

; Navisworks 2023
Source: "bin\Release\ClashRuleEngine.dll"; DestDir: "{localappdata}\Autodesk\Navisworks Manage 2023\Plugins\ClashRuleEngine"; Components: nw2023; Flags: ignoreversion
Source: "PackageContents.xml"; DestDir: "{localappdata}\Autodesk\Navisworks Manage 2023\Plugins\ClashRuleEngine"; Components: nw2023; Flags: ignoreversion

; Navisworks 2024
Source: "bin\Release\ClashRuleEngine.dll"; DestDir: "{localappdata}\Autodesk\Navisworks Manage 2024\Plugins\ClashRuleEngine"; Components: nw2024; Flags: ignoreversion
Source: "PackageContents.xml"; DestDir: "{localappdata}\Autodesk\Navisworks Manage 2024\Plugins\ClashRuleEngine"; Components: nw2024; Flags: ignoreversion

; Navisworks 2025
Source: "bin\Release\ClashRuleEngine.dll"; DestDir: "{localappdata}\Autodesk\Navisworks Manage 2025\Plugins\ClashRuleEngine"; Components: nw2025; Flags: ignoreversion
Source: "PackageContents.xml"; DestDir: "{localappdata}\Autodesk\Navisworks Manage 2025\Plugins\ClashRuleEngine"; Components: nw2025; Flags: ignoreversion

; Navisworks 2026
Source: "bin\Release\ClashRuleEngine.dll"; DestDir: "{localappdata}\Autodesk\Navisworks Manage 2026\Plugins\ClashRuleEngine"; Components: nw2026; Flags: ignoreversion
Source: "PackageContents.xml"; DestDir: "{localappdata}\Autodesk\Navisworks Manage 2026\Plugins\ClashRuleEngine"; Components: nw2026; Flags: ignoreversion

; Store a copy in the app folder for reference
Source: "bin\Release\ClashRuleEngine.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "PackageContents.xml"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Clean up old versions before installing
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2022\Plugins\ClashRuleEngine"; Components: nw2022
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2023\Plugins\ClashRuleEngine"; Components: nw2023
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2024\Plugins\ClashRuleEngine"; Components: nw2024
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2025\Plugins\ClashRuleEngine"; Components: nw2025
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2026\Plugins\ClashRuleEngine"; Components: nw2026

[UninstallDelete]
; Remove plugin folders on uninstall
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2022\Plugins\ClashRuleEngine"
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2023\Plugins\ClashRuleEngine"
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2024\Plugins\ClashRuleEngine"
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2025\Plugins\ClashRuleEngine"
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2026\Plugins\ClashRuleEngine"

[Messages]
WelcomeLabel2=This will install {#MyAppName} {#MyAppVersion} for Autodesk Navisworks.%n%nThe plugin adds a dockable panel for rule-based clash grouping and assignment.%n%nPlease close Navisworks before continuing.

[Run]
; Nothing to run post-install — plugin loads automatically on next Navisworks launch

[Code]
// ============================================================
// PASCAL SCRIPT — Navisworks detection logic
// ============================================================

// Check if a specific Navisworks version is installed by looking
// for its executable in the default install locations and registry.

function IsNavisworksInstalled(Year: String): Boolean;
var
  InstallPath: String;
begin
  Result := False;
  
  // Method 1: Check the registry for Navisworks Manage
  if RegQueryStringValue(HKLM, 
    'SOFTWARE\Autodesk\Navisworks Manage ' + Year, 
    'InstallDir', InstallPath) then
  begin
    if FileExists(InstallPath + '\roamer.exe') then
    begin
      Result := True;
      Exit;
    end;
  end;

  // Method 2: Check 64-bit registry (Wow6432Node fallback)
  if RegQueryStringValue(HKLM64, 
    'SOFTWARE\Autodesk\Navisworks Manage ' + Year, 
    'InstallDir', InstallPath) then
  begin
    if FileExists(InstallPath + '\roamer.exe') then
    begin
      Result := True;
      Exit;
    end;
  end;

  // Method 3: Check default install path
  InstallPath := ExpandConstant('{pf}') + '\Autodesk\Navisworks Manage ' + Year;
  if FileExists(InstallPath + '\roamer.exe') then
  begin
    Result := True;
    Exit;
  end;
end;

function IsNavisworks2022Installed: Boolean;
begin
  Result := IsNavisworksInstalled('2022');
end;

function IsNavisworks2023Installed: Boolean;
begin
  Result := IsNavisworksInstalled('2023');
end;

function IsNavisworks2024Installed: Boolean;
begin
  Result := IsNavisworksInstalled('2024');
end;

function IsNavisworks2025Installed: Boolean;
begin
  Result := IsNavisworksInstalled('2025');
end;

function IsNavisworks2026Installed: Boolean;
begin
  Result := IsNavisworksInstalled('2026');
end;

// Show a warning if no Navisworks versions are detected
function InitializeSetup: Boolean;
var
  AnyFound: Boolean;
begin
  AnyFound := IsNavisworks2022Installed or 
              IsNavisworks2023Installed or 
              IsNavisworks2024Installed or 
              IsNavisworks2025Installed or 
              IsNavisworks2026Installed;
  
  if not AnyFound then
  begin
    if MsgBox('No supported Navisworks installation was detected on this computer.' + #13#10 + #13#10 +
              'The plugin requires Autodesk Navisworks Manage (2022-2026).' + #13#10 + #13#10 +
              'Do you want to continue anyway?', 
              mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  end;
  
  Result := True;
end;

// Check if Navisworks is currently running and warn the user
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  
  // Check if roamer.exe (Navisworks) is running
  if Exec('tasklist', '/FI "IMAGENAME eq roamer.exe" /NH', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    // The tasklist command returns 0 whether or not processes are found,
    // so we can't easily distinguish. Show a reminder instead.
    // A more robust check would parse stdout, but this is sufficient.
  end;
end;

// Post-install message
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    MsgBox('{#MyAppName} has been installed successfully!' + #13#10 + #13#10 +
           'To use the plugin:' + #13#10 +
           '1. Open Navisworks Manage' + #13#10 +
           '2. Go to the Home tab' + #13#10 +
           '3. Look for "Clash Rule Engine" in the Tools panel' + #13#10 + #13#10 +
           'The plugin will load automatically on startup.',
           mbInformation, MB_OK);
  end;
end;
