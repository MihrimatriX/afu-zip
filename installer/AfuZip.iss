; AFU Zip kurulumu (Inno Setup 6). Varsayılan olarak yalnızca geçerli kullanıcıya kurar, yönetici izni istemez
; (%LOCALAPPDATA%\Programs\AFU Zip). Explorer sağ tık menüsünü ve "Birlikte aç" kaydını uygulamanın kendisi yapar:
; kurulumda "AfuZip.exe --register", kaldırırken "--unregister". İmzalı menü paketi yanındaysa Windows 11'in
; yeni menüsü de böylece kurulur.
;
;   iscc /DAppVersion=0.7.0 /DArch=x64 /DSourceDir=..\publish\AfuZip installer\AfuZip.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\AfuZip"
#endif

[Setup]
AppId={{21EF4A04-64AB-423B-A625-AF15235686AD}
AppName=AFU Zip
AppVersion={#AppVersion}
AppVerName=AFU Zip {#AppVersion}
AppPublisher=AFU Zip
AppPublisherURL=https://github.com/MihrimatriX/afu-zip
AppSupportURL=https://github.com/MihrimatriX/afu-zip/issues
AppUpdatesURL=https://github.com/MihrimatriX/afu-zip/releases
DefaultDirName={autopf}\AFU Zip
DefaultGroupName=AFU Zip
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
MinVersion=10.0.22000
OutputDir=..\artifacts
OutputBaseFilename=AfuZip-v{#AppVersion}-setup-{#Arch}
SetupIconFile=..\src\AfuZip.App\Assets\AfuZip.ico
UninstallDisplayIcon={app}\AfuZip.exe
UninstallDisplayName=AFU Zip
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Yalnızca AfuZip.exe kapatılır; Explorer'ın yüklü tuttuğu eklenti DLL'i PrepareToInstall'da kenara alınır.
CloseApplications=yes
CloseApplicationsFilter=AfuZip.exe
RestartApplications=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "explorer"; Description: "Explorer sağ tık menüsüne ve ""Birlikte aç"" listesine ekle"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; Excludes: "AfuZip.ShellExtension.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Kaldırırken Explorer DLL'i hâlâ yüklü tutuyorsa yeniden başlatmada silinir.
Source: "{#SourceDir}\AfuZip.ShellExtension.dll"; DestDir: "{app}"; Flags: ignoreversion uninsrestartdelete skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\AFU Zip"; Filename: "{app}\AfuZip.exe"
Name: "{autodesktop}\AFU Zip"; Filename: "{app}\AfuZip.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AfuZip.exe"; Parameters: "--register"; Tasks: explorer; Flags: runhidden waituntilterminated; StatusMsg: "Explorer menüsü ekleniyor..."
Filename: "{app}\AfuZip.exe"; Description: "{cm:LaunchProgram,AFU Zip}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\AfuZip.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "AfuZipUnregister"

[Code]
{ Güncellemede: Explorer eklenti DLL'ini yüklü tutar; kilitli dosya silinemez ama yeniden adlandırılabilir.
  Eski kopya kenara alınır, uygulama sonraki açılışta onu siler. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Dll, Old: String;
  I: Integer;
begin
  Result := '';
  Dll := ExpandConstant('{app}\AfuZip.ShellExtension.dll');
  if FileExists(Dll) and not DeleteFile(Dll) then
  begin
    I := 1;
    Old := Dll + '.eski' + IntToStr(I);
    while FileExists(Old) and not DeleteFile(Old) do
    begin
      I := I + 1;
      Old := Dll + '.eski' + IntToStr(I);
    end;
    RenameFile(Dll, Old);
  end;
end;
