; ============================================================
; Clash Rule Engine - Inno Setup Installer
; NOTE: keep this file ASCII-only (tooling reads it as ANSI without a BOM).
; ============================================================
; Installs the plugin the ONLY way Navisworks 2027 supports for
; third-party .NET plugins - a name-matched pair in the install's
; Plugins folder:
;
;   C:\Program Files\Autodesk\Navisworks Manage <year>\Plugins\
;     ClashRuleEngine\ClashRuleEngine.dll
;
; Hard-won facts (2026-06-12 debugging session):
;   - Navisworks 2027 DEPRECATED the user-profile plugin folder
;     (%AppData%) - plugins there silently never load.
;   - ApplicationPlugins bundles / PackageContents.xml are not used
;     by Navisworks for .NET plugin loading.
;   - The folder name MUST exactly match the DLL name (minus .dll).
;   - "*.Plugin.dll" naming is Autodesk-internal only.
;   - This layout also works on <= 2026, so it is the single
;     mechanism for every supported version.
;   - Writing to Program Files requires admin: PrivilegesRequired=admin.
;
; BUILD INSTRUCTIONS:
;   1. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php
;   2. Build per Navisworks version you want to ship:
;        msbuild /p:Configuration=Release /p:Platform=x64 /p:NavisworksVersion=2027
;        (older versions need their API DLLs in Refs\<version>\)
;   3. Compile this script (from the Installer folder)
;
; OUTPUT:
;   Installer\Output\ClashRuleEngine_Setup_<version>.exe
; ============================================================

#define MyAppName "Clash Rule Engine"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "ACME"
#define MyAppURL "https://example.com"
#define NwPlugins(Year) "{commonpf64}\Autodesk\Navisworks Manage " + Year + "\Plugins\ClashRuleEngine"

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

; Installer behavior - admin required (writes to Program Files)
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
DisableDirPage=yes
DisableProgramGroupPage=yes
WizardStyle=modern

; Uninstall settings
Uninstallable=yes
UninstallDisplayName={#MyAppName}
CreateUninstallRegKey=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ============================================================
; NAVISWORKS VERSION SELECTION
; One component per Navisworks release year. A component is only
; offered when that Navisworks version is detected, and only
; packaged when its per-version build output exists at compile time.
; ============================================================

[Types]
Name: "auto"; Description: "Install for all detected Navisworks versions"
Name: "custom"; Description: "Choose Navisworks versions"; Flags: iscustom

[Components]
Name: "nw2024"; Description: "Navisworks Manage 2024"; Types: auto custom; Check: IsNavisworks2024Installed
Name: "nw2025"; Description: "Navisworks Manage 2025"; Types: auto custom; Check: IsNavisworks2025Installed
Name: "nw2026"; Description: "Navisworks Manage 2026"; Types: auto custom; Check: IsNavisworks2026Installed
Name: "nw2027"; Description: "Navisworks Manage 2027"; Types: auto custom; Check: IsNavisworks2027Installed

[Files]
; Per-version DLLs into the install's Plugins\ClashRuleEngine\ folder
; (skipped at compile time if that version wasn't built).
Source: "..\bin\x64\Release\2024\ClashRuleEngine.dll"; DestDir: "{#NwPlugins('2024')}"; Components: nw2024; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\bin\x64\Release\2025\ClashRuleEngine.dll"; DestDir: "{#NwPlugins('2025')}"; Components: nw2025; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\bin\x64\Release\2026\ClashRuleEngine.dll"; DestDir: "{#NwPlugins('2026')}"; Components: nw2026; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\bin\x64\Release\2027\ClashRuleEngine.dll"; DestDir: "{#NwPlugins('2027')}"; Components: nw2027; Flags: ignoreversion skipifsourcedoesntexist

[InstallDelete]
; Remove dead copies from every location earlier installers/attempts used
Type: filesandordirs; Name: "{commonappdata}\Autodesk\ApplicationPlugins\ClashRuleEngine.bundle"
Type: filesandordirs; Name: "{userappdata}\Autodesk\ApplicationPlugins\ClashRuleEngine.bundle"
Type: filesandordirs; Name: "{userappdata}\Autodesk Navisworks Manage 2026\Plugins\ClashRuleEngine"
Type: filesandordirs; Name: "{userappdata}\Autodesk Navisworks Manage 2027\Plugins\ClashRuleEngine"
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2022\Plugins\ClashRuleEngine"
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2023\Plugins\ClashRuleEngine"
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2024\Plugins\ClashRuleEngine"
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2025\Plugins\ClashRuleEngine"
Type: filesandordirs; Name: "{localappdata}\Autodesk\Navisworks Manage 2026\Plugins\ClashRuleEngine"

[UninstallDelete]
Type: filesandordirs; Name: "{#NwPlugins('2024')}"
Type: filesandordirs; Name: "{#NwPlugins('2025')}"
Type: filesandordirs; Name: "{#NwPlugins('2026')}"
Type: filesandordirs; Name: "{#NwPlugins('2027')}"

[Messages]
WelcomeLabel2=This will install {#MyAppName} {#MyAppVersion} for Autodesk Navisworks.%n%nThe plugin adds a dockable panel for rule-based clash grouping and assignment.%n%nPlease close Navisworks before continuing.

[Code]
// ============================================================
// PASCAL SCRIPT - Navisworks detection logic
// ============================================================

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

function IsNavisworks2027Installed: Boolean;
begin
  Result := IsNavisworksInstalled('2027');
end;

// Show a warning if no Navisworks versions are detected
function InitializeSetup: Boolean;
var
  AnyFound: Boolean;
begin
  AnyFound := IsNavisworks2024Installed or
              IsNavisworks2025Installed or
              IsNavisworks2026Installed or
              IsNavisworks2027Installed;

  if not AnyFound then
  begin
    if MsgBox('No supported Navisworks installation was detected on this computer.' + #13#10 + #13#10 +
              'The plugin requires Autodesk Navisworks Manage (2024-2027).' + #13#10 + #13#10 +
              'Do you want to continue anyway?',
              mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  end;

  Result := True;
end;

// Post-install message (skipped for silent/enterprise installs)
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) then
  begin
    MsgBox('{#MyAppName} has been installed successfully!' + #13#10 + #13#10 +
           'To use the plugin:' + #13#10 +
           '1. Open Navisworks Manage' + #13#10 +
           '2. Go to View -> Windows -> Clash Rule Engine' + #13#10 + #13#10 +
           'The plugin will load automatically on startup.',
           mbInformation, MB_OK);
  end;
end;
