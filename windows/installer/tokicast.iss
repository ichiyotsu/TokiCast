; TokiCast Windows 安装包（Inno Setup 6）。
; 由 CI 调用：choco install innosetup 后用 ISCC 编译，例如
;   ISCC.exe /DPublishDir=<publish 目录> /DAppVersion=0.1.0 /DOutputBaseName=TokiCast-0.1.0 /O"<输出目录>" windows/installer/tokicast.iss
; PublishDir 是 dotnet publish 的输出目录（含 P2PKiwi.WinUI3.exe 及其全部依赖，
; 自包含发布，不需要用户另装 .NET 运行时）。
; 本地直接编译时用下面 #ifndef 的默认值（需要先在 windows 下跑一次
; dotnet publish，把输出拷到 windows/installer/publish）。

#ifndef PublishDir
#define PublishDir "publish"
#endif
#ifndef AppVersion
#define AppVersion "0.1.0"
#endif
#ifndef OutputBaseName
#define OutputBaseName "TokiCast-0.1.0"
#endif

[Setup]
AppId=com.ichiyotsu.tokicast
AppName=TokiCast
AppVersion={#AppVersion}
AppPublisher=ichiyotsu
DefaultDirName={autopf}\TokiCast
DisableDirPage=no
DisableProgramGroupPage=yes
OutputBaseFilename={#OutputBaseName}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\P2PKiwi.WinUI3.exe
SetupIconFile=..\..\src\P2PKiwi.WinUI3\Assets\TokiCast.ico

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\TokiCast"; Filename: "{app}\P2PKiwi.WinUI3.exe"

[Run]
Filename: "{app}\P2PKiwi.WinUI3.exe"; Description: "启动 TokiCast"; Flags: nowait postinstall skipifsilent
