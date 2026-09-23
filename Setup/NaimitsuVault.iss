; Setup script for Tabito's Works NaimitsuVault
; Build the publish output first (PublishDir in FolderProfile.pubxml is bin\x64\NaimitsuVault-x64):
;   dotnet publish NaimitsuVault\NaimitsuVault.csproj -c Release -p:Platform=x64 -p:PublishProfile=FolderProfile

#define MyAppName "NaimitsuVault"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Tabito's Works"
#define MyAppURL "https://github.com/tabitos-atelier/naimitsu"
#define MyAppBlogURL "https://tabitos-voyage.com/about"
#define MyAppExeName "NaimitsuVault.exe"

[Setup]
; --- Application identity ---
AppId={{8A994A6B-1833-46FB-BE7A-AFCF54AA67CE}
AppName={cm:AppDisplayName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
AppCopyright=© 2026 Tabito's Works

; --- Install location / privileges ---
DefaultDirName={localappdata}\TabitosWorks\{#MyAppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Blocks install/uninstall while the app is running (matches the Mutex held in Program.cs).
AppMutex=Global\NaimitsuVaultAppMutex

; --- Wizard / branding ---
WizardStyle=modern
SetupIconFile=..\NaimitsuVault\Assets\NaimitsuVault.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={cm:AppDisplayName}
WizardImageFile=Images\SideImage.bmp
WizardSmallImageFile=Images\SmallImage.bmp
WizardImageStretch=yes

; No language picker dialog; auto-detect from Windows UI language (falls back to English, listed first).
ShowLanguageDialog=no

; --- Compiler output ---
OutputBaseFilename=NaimitsuVault-Setup-x64
OutputDir=Output
SolidCompression=yes

; --- File version info ---
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
english.AppDisplayName=Naimitsu Vault
japanese.AppDisplayName=ないみつクン

english.LaunchProgramCustom=Launch Naimitsu Vault (NaimitsuVault)
japanese.LaunchProgramCustom=ないみつクン (NaimitsuVault) を起動する

english.VisitBlog=Visit the author's profile (Tabito's Voyage, Ja)
japanese.VisitBlog=作者の経歴（たびとの旅路）を開く

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; ignoreversion required: the app's Version is fixed across builds, so Inno's default
; version-checked copy would otherwise skip re-copying exe/dll on upgrade installs.
; Excludes: the app writes vault data to {app}\data and logs to {app}\logs at runtime. If the publish folder was
; ever launched for testing, those folders exist there and must never be shipped or overwrite the user's vault.
; Patterns start with a backslash so they match only at the root, not a same-named subfolder deeper in the tree.
Source: "..\NaimitsuVault\bin\x64\NaimitsuVault-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "\data\*,\logs\*"
; LICENSE, THIRD-PARTY-NOTICES.md and THIRD-PARTY-NOTICES-ja.md are already part of the publish folder
; (the IncludeLicenseFilesInPublish target in NaimitsuVault.csproj), so the wildcard above installs them.

[Icons]
Name: "{autoprograms}\{cm:AppDisplayName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{cm:AppDisplayName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgramCustom}"; Flags: nowait postinstall skipifsilent
Filename: "{#MyAppBlogURL}"; Description: "{cm:VisitBlog}"; Flags: shellexec nowait postinstall runasoriginaluser

