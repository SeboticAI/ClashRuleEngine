; ============================================================
; OConnors Clash Engine - Inno Setup Installer
; NOTE: keep this file ASCII-only (tooling reads it as ANSI without a BOM).
; ============================================================
; Installs the plugin the ONLY way Navisworks 2027 supports for
; third-party .NET plugins - a name-matched pair in the install's
; Plugins folder:
;
;   <Navisworks install dir>\Plugins\
;     ClashRuleEngine\ClashRuleEngine.dll
;     ClashRuleEngine\oconnors_clash_16.ico
;     ClashRuleEngine\oconnors_clash_32.ico
;
; The install dir is read from the registry per version at run time
; (NwInstallDir in [Code]) so a non-default drive/location works.
;
; The two .ico files MUST sit next to the DLL: that is where Navisworks
; resolves the Icon/LargeIcon named on the [Command]/[AddInPlugin]
; attributes from. Without them the ribbon button falls back to a
; generic placeholder image.
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
; ONE BUILD PER NAVISWORKS VERSION IS UNAVOIDABLE. The API assemblies
; are strong-named per release (2024 = 21.0.0.0, 2025 = 22.0,
; 2026 = 23.0, 2027 = 24.0) and roamer.exe.config pins each one with
; publisherPolicy apply="no" - so a DLL compiled against 24.0 cannot
; load in 2026 no matter where it is put.
;
; BUILD INSTRUCTIONS:
;   1. Install Inno Setup 6:
;        winget install --id JRSoftware.InnoSetup
;   2. Harvest the API DLLs for any version not installed locally
;      (run on a machine that has it, or point -From at a copy):
;        tools\harvest-refs.ps1 -Version 2026
;   3. Build everything and compile this script in one go:
;        tools\build-all.ps1 -Installer
;
; This script packages EXACTLY the versions whose build output exists
; when it is compiled (see the #if FileExists blocks below), and a
; component is offered only for a version that is BOTH packaged and
; installed - so a user can never select a version that would silently
; copy nothing.
;
; OUTPUT:
;   Installer\Output\OConnorsClashEngine_Setup_<version>.exe
; ============================================================

#define MyAppName "OConnors Clash Engine"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "OConnors"

; Destination is resolved AT RUN TIME from the machine's actual Navisworks install
; location (see NwPluginDir in [Code]) - NOT a hardcoded C:\Program Files path. A team
; member who installed Navisworks on another drive would otherwise get files written to
; a folder Navisworks never reads, with the install reporting success.
#define NwPlugins(Year) "{code:NwPluginDir|" + Year + "}"

; Per-version build output, relative to this script.
#define Rel AddBackslash(SourcePath) + "..\bin\x64\Release\"

; ---- Detect at COMPILE TIME which per-version builds exist ----------
; Everything downstream (components, files, the wizard text) keys off these,
; so the packaged set and the offered set can never disagree.
#if FileExists(Rel + "2024\ClashRuleEngine.dll")
  #define Has2024
#endif
#if FileExists(Rel + "2025\ClashRuleEngine.dll")
  #define Has2025
#endif
#if FileExists(Rel + "2026\ClashRuleEngine.dll")
  #define Has2026
#endif
#if FileExists(Rel + "2027\ClashRuleEngine.dll")
  #define Has2027
#endif

#if !defined(Has2024) && !defined(Has2025) && !defined(Has2026) && !defined(Has2027)
  #error No per-version build output found under bin\x64\Release\<year>\. Run tools\build-all.ps1 first.
#endif

[Setup]
; Basic installer metadata
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
SetupIconFile=oconnors_clash.ico

; Output settings
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputBaseFilename=OConnorsClashEngine_Setup_{#MyAppVersion}
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
; The AppId is unchanged from the 1.1.0 "Clash Rule Engine" installer so that an upgrade
; replaces it instead of stacking a second entry in Apps & Features. But Inno then reuses
; the REMEMBERED directory from that install, which put the uninstaller in
; "C:\Program Files\Clash Rule Engine" under the old name. Force the current
; DefaultDirName and delete the old folder below.
UsePreviousAppDir=no
; Acknowledged: [InstallDelete] touches per-user areas from an elevated install. That is
; best-effort tidy-up of dead legacy copies only - see the note in [InstallDelete].
UsedUserAreasWarning=no

; Uninstall settings
Uninstallable=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\oconnors_clash.ico
CreateUninstallRegKey=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ============================================================
; NAVISWORKS VERSION SELECTION
; One component per Navisworks release year. A component exists only
; if this setup.exe actually CONTAINS that version's build, and is
; only offered if that Navisworks version is detected on the machine.
; ============================================================

[Types]
Name: "auto"; Description: "Install for all detected Navisworks versions"
Name: "custom"; Description: "Choose Navisworks versions"; Flags: iscustom

[Components]
#ifdef Has2024
Name: "nw2024"; Description: "Navisworks Manage 2024"; Types: auto custom; Check: IsNavisworks2024Installed
#endif
#ifdef Has2025
Name: "nw2025"; Description: "Navisworks Manage 2025"; Types: auto custom; Check: IsNavisworks2025Installed
#endif
#ifdef Has2026
Name: "nw2026"; Description: "Navisworks Manage 2026"; Types: auto custom; Check: IsNavisworks2026Installed
#endif
#ifdef Has2027
Name: "nw2027"; Description: "Navisworks Manage 2027"; Types: auto custom; Check: IsNavisworks2027Installed
#endif

[Files]
; The uninstall display icon.
Source: "oconnors_clash.ico"; DestDir: "{app}"; Flags: ignoreversion

; ------------------------------------------------------------------
; Per version, the WHOLE staged payload - and the SUBFOLDERS MATTER:
;   ClashRuleEngine.dll
;   en-US\ClashRuleEngineRibbon.xaml   <- [RibbonLayout]. Navisworks loads this as a
;                                         LOOSE FILE from a locale folder next to the
;                                         DLL. Miss it and the custom ribbon tab simply
;                                         does not appear (silently - no error anywhere).
;   en-US\ClashRuleEngine.name         <- [Strings]
;   Images\*.ico  +  *.ico             <- Icon/LargeIcon; SDK allows either location
; "recursesubdirs createallsubdirs" mirrors the build output exactly, so a new file
; added to the staged payload ships automatically instead of being forgotten here.
; ------------------------------------------------------------------
#ifdef Has2024
Source: "..\bin\x64\Release\2024\ClashRuleEngine.dll"; DestDir: "{#NwPlugins('2024')}"; Components: nw2024; Flags: ignoreversion
Source: "..\bin\x64\Release\2024\en-US\*"; DestDir: "{#NwPlugins('2024')}\en-US"; Components: nw2024; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\bin\x64\Release\2024\Images\*"; DestDir: "{#NwPlugins('2024')}\Images"; Components: nw2024; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\bin\x64\Release\2024\*.ico"; DestDir: "{#NwPlugins('2024')}"; Components: nw2024; Flags: ignoreversion
#endif
#ifdef Has2025
Source: "..\bin\x64\Release\2025\ClashRuleEngine.dll"; DestDir: "{#NwPlugins('2025')}"; Components: nw2025; Flags: ignoreversion
Source: "..\bin\x64\Release\2025\en-US\*"; DestDir: "{#NwPlugins('2025')}\en-US"; Components: nw2025; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\bin\x64\Release\2025\Images\*"; DestDir: "{#NwPlugins('2025')}\Images"; Components: nw2025; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\bin\x64\Release\2025\*.ico"; DestDir: "{#NwPlugins('2025')}"; Components: nw2025; Flags: ignoreversion
#endif
#ifdef Has2026
Source: "..\bin\x64\Release\2026\ClashRuleEngine.dll"; DestDir: "{#NwPlugins('2026')}"; Components: nw2026; Flags: ignoreversion
Source: "..\bin\x64\Release\2026\en-US\*"; DestDir: "{#NwPlugins('2026')}\en-US"; Components: nw2026; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\bin\x64\Release\2026\Images\*"; DestDir: "{#NwPlugins('2026')}\Images"; Components: nw2026; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\bin\x64\Release\2026\*.ico"; DestDir: "{#NwPlugins('2026')}"; Components: nw2026; Flags: ignoreversion
#endif
#ifdef Has2027
Source: "..\bin\x64\Release\2027\ClashRuleEngine.dll"; DestDir: "{#NwPlugins('2027')}"; Components: nw2027; Flags: ignoreversion
Source: "..\bin\x64\Release\2027\en-US\*"; DestDir: "{#NwPlugins('2027')}\en-US"; Components: nw2027; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\bin\x64\Release\2027\Images\*"; DestDir: "{#NwPlugins('2027')}\Images"; Components: nw2027; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\bin\x64\Release\2027\*.ico"; DestDir: "{#NwPlugins('2027')}"; Components: nw2027; Flags: ignoreversion
#endif

[InstallDelete]
; Remove dead copies from every location earlier installers/attempts used. A stale copy
; matters most on <= 2026, which DOES still load the per-user plugin folder: two copies
; of the same plugin ID would then fight. 2027 ignores those folders outright.
;
; CAVEAT (this is what UsedUserAreasWarning below is about): the setup runs elevated, so
; {userappdata}/{localappdata} resolve to the profile of the account that ELEVATED. When
; an admin user accepts their own UAC prompt that is their own profile and the cleanup
; works; if a standard user types someone else's admin credentials, their own stale
; copies are out of reach. Harmless either way - nothing here is required for the new
; install to work, it only tidies up.
; The pre-1.2.0 app folder, left behind by UsePreviousAppDir=no above. Safe to remove:
; [InstallDelete] runs before any file is copied, the new uninstaller goes to the new
; folder, and the uninstall registry key is the same {AppId}_is1 either way, so nothing
; is orphaned.
Type: filesandordirs; Name: "{autopf}\Clash Rule Engine"
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
WelcomeLabel2=This will install {#MyAppName} {#MyAppVersion} for Autodesk Navisworks Manage.%n%nThe plugin adds an OConnors Clash ribbon tab for rule-based clash assignment, auto-approval and grouping.%n%nPlease close Navisworks before continuing.

[Code]
// ============================================================
// PASCAL SCRIPT
// ============================================================

// -- Which Navisworks versions is this setup.exe carrying? ------------
// Driven by the same compile-time #ifdefs as [Components]/[Files], so the
// wizard text can never claim a version that was not packaged.
function PackagedVersions: String;
begin
  Result := '';
#ifdef Has2024
  Result := Result + '2024, ';
#endif
#ifdef Has2025
  Result := Result + '2025, ';
#endif
#ifdef Has2026
  Result := Result + '2026, ';
#endif
#ifdef Has2027
  Result := Result + '2027, ';
#endif
  if Length(Result) >= 2 then
    Result := Copy(Result, 1, Length(Result) - 2);
end;

function IsVersionPackaged(Year: String): Boolean;
begin
  Result := False;
#ifdef Has2024
  if Year = '2024' then Result := True;
#endif
#ifdef Has2025
  if Year = '2025' then Result := True;
#endif
#ifdef Has2026
  if Year = '2026' then Result := True;
#endif
#ifdef Has2027
  if Year = '2027' then Result := True;
#endif
end;

// -- Navisworks detection ---------------------------------------------
// Registry layout, dumped from a real Navisworks Manage 2027 install (2026-08-10).
// Autodesk keys these by API MAJOR version, not by release year:
//   HKLM\SOFTWARE\Autodesk\Navisworks API Runtime\24\Navisworks Manage  Path = <dir>
//   HKLM\SOFTWARE\Autodesk\Navisworks Manage\24.0\Location             Path = <dir>
// major = year - 2003  (2024 = 21, 2025 = 22, 2026 = 23, 2027 = 24, 2028 = 25),
// the same mapping as the "Nw<major>" Series token in PackageContents.xml.
//
// The previous version of this script probed
// HKLM\SOFTWARE\Autodesk\Navisworks Manage <year> for an "InstallDir" value. That key
// does not exist in any release - detection always fell through to the hardcoded
// C:\Program Files path, so a non-default install was invisible.
function NwApiMajor(Year: String): String;
begin
  Result := IntToStr(StrToIntDef(Year, 0) - 2003);
end;

function NwInstallDir(Year: String): String;
var
  Major: String;
  P: String;
begin
  Result := '';
  Major := NwApiMajor(Year);

  // Both registry views are queried explicitly: plain HKLM follows the installer's
  // current bitness view, which is easy to get subtly wrong.
  if RegQueryStringValue(HKLM64, 'SOFTWARE\Autodesk\Navisworks API Runtime\' + Major
       + '\Navisworks Manage', 'Path', P) then
    if FileExists(AddBackslash(P) + 'roamer.exe') then
    begin
      Result := RemoveBackslashUnlessRoot(AddBackslash(P));
      Exit;
    end;

  if RegQueryStringValue(HKLM32, 'SOFTWARE\Autodesk\Navisworks API Runtime\' + Major
       + '\Navisworks Manage', 'Path', P) then
    if FileExists(AddBackslash(P) + 'roamer.exe') then
    begin
      Result := RemoveBackslashUnlessRoot(AddBackslash(P));
      Exit;
    end;

  if RegQueryStringValue(HKLM64, 'SOFTWARE\Autodesk\Navisworks Manage\' + Major
       + '.0\Location', 'Path', P) then
    if FileExists(AddBackslash(P) + 'roamer.exe') then
    begin
      Result := RemoveBackslashUnlessRoot(AddBackslash(P));
      Exit;
    end;

  // Last resort: the default install path.
  P := ExpandConstant('{commonpf64}') + '\Autodesk\Navisworks Manage ' + Year;
  if FileExists(AddBackslash(P) + 'roamer.exe') then
    Result := P;
end;

// DestDir for [Files]/[UninstallDelete]. Navisworks requires a name-matched
// folder/DLL pair, hence the fixed "ClashRuleEngine" leaf.
function NwPluginDir(Year: String): String;
var
  Dir: String;
begin
  Dir := NwInstallDir(Year);
  if Dir = '' then
    // Only reachable for a version whose component was not selected (its Check failed),
    // so nothing is copied here - but never hand Inno an empty DestDir.
    Dir := ExpandConstant('{commonpf64}') + '\Autodesk\Navisworks Manage ' + Year;
  Result := AddBackslash(Dir) + 'Plugins\ClashRuleEngine';
end;

function IsNavisworksInstalled(Year: String): Boolean;
begin
  Result := (NwInstallDir(Year) <> '');
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

// A Navisworks version that is installed here but NOT in this setup.exe would
// otherwise be a silent no-op for that user - so name it explicitly.
function UnpackagedInstalledVersions: String;
var
  Years: array[0..4] of String;
  I: Integer;
begin
  Result := '';
  Years[0] := '2024'; Years[1] := '2025'; Years[2] := '2026';
  Years[3] := '2027'; Years[4] := '2028';
  for I := 0 to 4 do
  begin
    if IsNavisworksInstalled(Years[I]) and (not IsVersionPackaged(Years[I])) then
      Result := Result + Years[I] + ', ';
  end;
  if Length(Result) >= 2 then
    Result := Copy(Result, 1, Length(Result) - 2);
end;

// -- Is Navisworks running? -------------------------------------------
// A loaded plugin DLL is locked by roamer.exe, so an upgrade would either fail
// mid-copy or leave the OLD build in place. tasklist+find is used rather than a
// window-class or mutex probe because Navisworks exposes no documented name for
// either. "find" exits 0 when it matches, 1 when it does not.
function IsNavisworksRunning: Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if Exec(ExpandConstant('{cmd}'),
          '/C tasklist /FI "IMAGENAME eq roamer.exe" | find /I "roamer.exe"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 0);
end;

function InitializeSetup: Boolean;
var
  Missing: String;
begin
  Result := True;

  if PackagedVersions = '' then
  begin
    MsgBox('This installer contains no Navisworks builds. It was compiled without any '
           + 'per-version build output.', mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  if not (IsNavisworks2024Installed or IsNavisworks2025Installed
          or IsNavisworks2026Installed or IsNavisworks2027Installed) then
  begin
    if MsgBox('No supported Navisworks Manage installation was detected on this computer.'
              + #13#10 + #13#10
              + 'This installer supports Navisworks Manage: ' + PackagedVersions + '.'
              + #13#10 + #13#10
              + 'Do you want to continue anyway?',
              mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  end;

  Missing := UnpackagedInstalledVersions;
  if Missing <> '' then
  begin
    MsgBox('Heads up: this computer has Navisworks Manage ' + Missing + ', which this '
           + 'installer does NOT include a build for.' + #13#10 + #13#10
           + 'It contains: ' + PackagedVersions + '.' + #13#10 + #13#10
           + 'Those versions will be left untouched. Ask for an installer built with '
           + 'that version''s API references if you need it.',
           mbInformation, MB_OK);
  end;
end;

// Checked immediately before any file is copied - the accurate moment for a
// file-lock question.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsNavisworksRunning then
    Result := 'Navisworks is currently running.' + #13#10 + #13#10
            + 'Close every Navisworks window and click Back, then Install again. '
            + 'A plugin that is already loaded is locked on disk, so installing now '
            + 'would leave the previous version in place.';
end;

// Post-install message (skipped for silent/enterprise installs)
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) then
  begin
    MsgBox('{#MyAppName} {#MyAppVersion} installed for Navisworks ' + PackagedVersions + '.'
           + #13#10 + #13#10
           + 'To use it:' + #13#10
           + '1. Start Navisworks Manage' + #13#10
           + '2. Open the "OConnors Clash" ribbon tab' + #13#10
           + '3. Click "Clash Engine" - the panel opens straight away'
           + #13#10 + #13#10
           + '(It is also under Tool Add-ins as "Clash Engine".)',
           mbInformation, MB_OK);
  end;
end;
