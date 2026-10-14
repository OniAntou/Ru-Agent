#ifndef Product
  #define Product "Desktop"
#endif
#ifndef ReleaseVersion
  #define ReleaseVersion "0.1.0-preview.1"
#endif
#ifndef PackageRoot
  #error PackageRoot is required
#endif
#ifndef OutputRoot
  #error OutputRoot is required
#endif

[Setup]
#ifdef LocalSmoke
AppId=OniAntou.Ru.LocalSmoke.{#Product}
CreateUninstallRegKey=no
#else
AppId=OniAntou.Ru.{#Product}
#endif
AppName=Ru {#Product}
AppVersion={#ReleaseVersion}
AppPublisher=OniAntou
AppPublisherURL=https://github.com/OniAntou/Ru-Agent
DefaultDirName={localappdata}\Programs\Ru\{#Product}
DefaultGroupName=Ru Agent
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.17763
DisableProgramGroupPage=yes
CloseApplications=no
RestartApplications=no
ChangesEnvironment=yes
SetupMutex=OniAntou.Ru.Setup.{#Product}
OutputDir={#OutputRoot}
OutputBaseFilename=Ru-{#Product}-{#ReleaseVersion}-win-x64-Setup
#ifdef LocalSmoke
Compression=none
SolidCompression=no
#else
Compression=lzma2
SolidCompression=yes
#endif
WizardStyle=modern
SetupIconFile={#PackageRoot}\Ru.ico
UninstallDisplayIcon={app}\Ru.ico

[Files]
Source: "{#PackageRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

#if Product == "Desktop"
#ifndef LocalSmoke
[Icons]
Name: "{userprograms}\Ru Agent"; Filename: "{app}\RuApp.exe"
[Run]
Filename: "{app}\RuApp.exe"; Description: "Launch Ru Agent"; Flags: nowait postinstall skipifsilent
#endif
#endif

[Code]
var
  PreviousFiles: TArrayOfString;

const
  GenericReadWrite = $C0000000;
  OpenExisting = 3;
  InvalidHandle = -1;

function CreateFile(FileName: String; Access, Share: Cardinal; Security: Integer;
  Creation, Attributes: Cardinal; Template: Integer): Integer;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: Integer): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function HasLockedFiles(Directory: String): Boolean;
var Entry: TFindRec; Handle: Integer;
begin
  Result := False;
  if FindFirst(AddBackslash(Directory) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          if Entry.Attributes and $400 <> 0 then begin Result := True; Exit; end;
          if Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then begin
            if HasLockedFiles(AddBackslash(Directory) + Entry.Name) then begin Result := True; Exit; end;
          end else begin
            Handle := CreateFile(AddBackslash(Directory) + Entry.Name, GenericReadWrite, 0, 0, OpenExisting, 0, 0);
            if Handle = InvalidHandle then begin Result := True; Exit; end;
            CloseHandle(Handle);
          end;
        end;
      until not FindNext(Entry);
    finally FindClose(Entry); end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Marker: AnsiString; Entry: TFindRec; Nonempty: Boolean;
begin
  Result := '';
  if LoadStringFromFile(ExpandConstant('{app}\ru-install.json'), Marker) then begin
    if Pos('"Product": "{#LowerCase(Product)}"', String(Marker)) = 0 then begin
      Result := 'This folder belongs to another Ru product. Choose its own installation folder.'; Exit;
    end;
  end else if DirExists(ExpandConstant('{app}')) then begin
    Nonempty := False;
    if FindFirst(ExpandConstant('{app}\*'), Entry) then begin
      try
        repeat if (Entry.Name <> '.') and (Entry.Name <> '..') then Nonempty := True;
        until not FindNext(Entry);
      finally FindClose(Entry); end;
    end;
    if Nonempty then begin Result := 'Choose an empty folder for this Ru installation.'; Exit; end;
  end;
  if DirExists(ExpandConstant('{app}')) and HasLockedFiles(ExpandConstant('{app}')) then
    Result := 'Close Ru and any programs using its installation files, then try again. No processes will be terminated.';
  if Result = '' then LoadStringsFromFile(ExpandConstant('{app}\ru-files.lst'), PreviousFiles);
end;

procedure RemoveObsoleteOwnedFiles;
var CurrentFiles: TArrayOfString; I, J: Integer; Relative: String; Found: Boolean;
begin
  if not LoadStringsFromFile(ExpandConstant('{app}\ru-files.lst'), CurrentFiles) then Exit;
  for I := 0 to GetArrayLength(PreviousFiles) - 1 do begin
    Relative := PreviousFiles[I];
    if Relative = '' then Continue;
    { Ignore malformed old manifests: never delete outside this dedicated root. }
    if (Pos('..', Relative) > 0) or (Pos(':', Relative) > 0)
      or (Relative[1] = '\') or (Relative[1] = '/') then Continue;
    Found := False;
    for J := 0 to GetArrayLength(CurrentFiles) - 1 do
      if CompareText(Relative, CurrentFiles[J]) = 0 then Found := True;
    if not Found then DeleteFile(ExpandConstant('{app}\') + Relative);
  end;
end;

#if Product == "CLI"
function CleanPathEntry(Value: String): String;
begin
  Value := Trim(Value);
  StringChangeEx(Value, '"', '', True);
  Result := Lowercase(RemoveBackslashUnlessRoot(Value));
end;

procedure ChangeUserPath(Remove: Boolean);
var Value, Remaining, Part, ResultPath, AppPath: String; Separator: Integer; Found: Boolean;
begin
  RegQueryStringValue(HKCU, 'Environment', 'Path', Value);
  Remaining := Value; ResultPath := ''; Found := False;
  AppPath := CleanPathEntry(ExpandConstant('{app}'));
  while Remaining <> '' do begin
    Separator := Pos(';', Remaining);
    if Separator = 0 then begin Part := Remaining; Remaining := ''; end
    else begin Part := Copy(Remaining, 1, Separator - 1); Delete(Remaining, 1, Separator); end;
    if CleanPathEntry(Part) = AppPath then begin
      Found := True;
      if Remove then Continue;
    end;
    if ResultPath <> '' then ResultPath := ResultPath + ';';
    ResultPath := ResultPath + Part;
  end;
  if not Remove and not Found then begin
    if ResultPath <> '' then ResultPath := ResultPath + ';';
    ResultPath := ResultPath + ExpandConstant('{app}');
  end;
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', ResultPath);
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
begin
  #ifndef LocalSmoke
    if Step = usPostUninstall then ChangeUserPath(True);
  #endif
end;
#endif

procedure CurStepChanged(Step: TSetupStep);
begin
  if Step = ssPostInstall then begin
    RemoveObsoleteOwnedFiles;
    #if Product == "CLI"
#ifndef LocalSmoke
      ChangeUserPath(False);
    #endif
    #endif
  end;
end;
