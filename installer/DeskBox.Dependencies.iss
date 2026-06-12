[Code]
const
  DotNetRuntimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/8.0.28/dotnet-runtime-8.0.28-win-x64.exe';
  DotNetRuntimeFallbackUrl = 'https://aka.ms/dotnet/8.0/dotnet-runtime-win-x64.exe';
  DotNetRuntimeInstallerName = 'dotnet-runtime-8.0.28-win-x64.exe';
  WindowsAppRuntimeUrl = 'https://download.microsoft.com/download/d1af52f9-db3d-4138-adb5-960bd1009a43/WindowsAppRuntimeInstall-x64.exe';
  WindowsAppRuntimeFallbackUrl = 'https://aka.ms/windowsappsdk/2.1/2.1.3/windowsappruntimeinstall-x64.exe';
  WindowsAppRuntimeInstallerName = 'WindowsAppRuntimeInstall-x64.exe';

var
  DependencyDownloadPage: TDownloadWizardPage;
  DependencyInstallPage: TOutputProgressWizardPage;
  ShouldInstallDotNetRuntime: Boolean;
  ShouldInstallWindowsAppRuntime: Boolean;
  DependenciesPrepared: Boolean;

function IsMajorVersion(Value: string; ExpectedMajor: Integer): Boolean;
var
  DotPosition: Integer;
  MajorText: string;
begin
  DotPosition := Pos('.', Value);
  if DotPosition > 0 then
    MajorText := Copy(Value, 1, DotPosition - 1)
  else
    MajorText := Value;

  Result := StrToIntDef(MajorText, 0) = ExpectedMajor;
end;

function IsDotNet8RuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{pf}\dotnet\shared\Microsoft.NETCore.App\*'), FindRec) then
  begin
    try
      repeat
        if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
        begin
          if IsMajorVersion(FindRec.Name, 8) then
          begin
            Result := True;
            Exit;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function IsWindowsAppRuntime21Installed: Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -Command "$pkg = Get-AppxPackage -Name Microsoft.WindowsAppRuntime.2 -ErrorAction SilentlyContinue | Where-Object { $_.Architecture -eq ''X64'' -and [version]$_.Version -ge [version]''2.1.3.0'' } | Select-Object -First 1; if (-not $pkg) { $pkg = Get-AppxPackage -AllUsers -Name Microsoft.WindowsAppRuntime.2 -ErrorAction SilentlyContinue | Where-Object { $_.Architecture -eq ''X64'' -and [version]$_.Version -ge [version]''2.1.3.0'' } | Select-Object -First 1 }; if ($pkg) { exit 0 } exit 1"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
end;

procedure DetectDeskBoxDependencies;
begin
  ShouldInstallDotNetRuntime := not IsDotNet8RuntimeInstalled;
  ShouldInstallWindowsAppRuntime := not IsWindowsAppRuntime21Installed;

  Log('DeskBox 依赖检测：.NET 8 Runtime 缺失=' + IntToStr(Integer(ShouldInstallDotNetRuntime)));
  Log('DeskBox 依赖检测：Windows App Runtime 2.1 缺失=' + IntToStr(Integer(ShouldInstallWindowsAppRuntime)));
end;

function QuoteForDisplay(Value: string): string;
begin
  Result := '"' + Value + '"';
end;

function DownloadDependencyWithProgress(
  DisplayName: string;
  Url: string;
  FallbackUrl: string;
  FileName: string;
  var ErrorMessage: string): Boolean;
begin
  Result := False;
  ErrorMessage := '';

  DependencyDownloadPage.Clear;
  DependencyDownloadPage.Add(Url, FileName, '');

  try
    DependencyDownloadPage.Download;
    Result := True;
    Exit;
  except
    if DependencyDownloadPage.AbortedByUser then
    begin
      ErrorMessage := '下载已取消。';
      Exit;
    end;

    ErrorMessage := GetExceptionMessage;
    Log(DisplayName + ' 主下载失败：' + ErrorMessage);
  end;

  DependencyDownloadPage.Clear;
  DependencyDownloadPage.Add(FallbackUrl, FileName, '');

  try
    DependencyDownloadPage.Download;
    Result := True;
  except
    if DependencyDownloadPage.AbortedByUser then
      ErrorMessage := '下载已取消。'
    else
      ErrorMessage :=
        DisplayName + ' 下载失败。' + #13#10 +
        '主下载地址：' + Url + #13#10 +
        '备用下载地址：' + FallbackUrl + #13#10 +
        '错误：' + GetExceptionMessage;

    Log(DisplayName + ' 备用下载失败：' + ErrorMessage);
  end;
end;

function DownloadDeskBoxDependencies: Boolean;
var
  ErrorMessage: string;
begin
  Result := True;

  if not (ShouldInstallDotNetRuntime or ShouldInstallWindowsAppRuntime) then
    Exit;

  DependencyDownloadPage.Show;
  try
    if ShouldInstallDotNetRuntime then
    begin
      DependencyDownloadPage.Msg1Label.Caption := '正在下载 .NET 8 Runtime x64...';
      if not DownloadDependencyWithProgress(
        '.NET 8 Runtime x64',
        DotNetRuntimeUrl,
        DotNetRuntimeFallbackUrl,
        DotNetRuntimeInstallerName,
        ErrorMessage) then
      begin
        SuppressibleMsgBox(ErrorMessage, mbCriticalError, MB_OK, IDOK);
        Result := False;
        Exit;
      end;
    end;

    if ShouldInstallWindowsAppRuntime then
    begin
      DependencyDownloadPage.Msg1Label.Caption := '正在下载 Windows App Runtime 2.1 x64...';
      if not DownloadDependencyWithProgress(
        'Windows App Runtime 2.1 x64',
        WindowsAppRuntimeUrl,
        WindowsAppRuntimeFallbackUrl,
        WindowsAppRuntimeInstallerName,
        ErrorMessage) then
      begin
        SuppressibleMsgBox(ErrorMessage, mbCriticalError, MB_OK, IDOK);
        Result := False;
        Exit;
      end;
    end;
  finally
    DependencyDownloadPage.Hide;
  end;
end;

function InstallDownloadedDependency(
  DisplayName: string;
  FileName: string;
  Parameters: string;
  Step: Integer;
  StepCount: Integer;
  var NeedsRestart: Boolean): Boolean;
var
  InstallerPath: string;
  ResultCode: Integer;
begin
  Result := False;
  InstallerPath := ExpandConstant('{tmp}\' + FileName);

  DependencyInstallPage.SetProgress(Step - 1, StepCount);
  DependencyInstallPage.SetText(
    '正在安装 ' + DisplayName + '...',
    '安装程序可能需要几分钟，请不要关闭窗口。');

  if not Exec(InstallerPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    SuppressibleMsgBox(DisplayName + ' 安装程序无法启动。请确认安装器已用管理员权限运行后重试。', mbCriticalError, MB_OK, IDOK);
    Exit;
  end;

  if (ResultCode = 3010) or (ResultCode = 1641) then
  begin
    NeedsRestart := True;
    Result := True;
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    SuppressibleMsgBox(
      DisplayName + ' 安装失败，退出代码：' + IntToStr(ResultCode) + '。' + #13#10 +
      '请确认系统支持该运行时，或手动安装依赖后重新运行 DeskBox 安装程序。',
      mbCriticalError,
      MB_OK,
      IDOK);
    Exit;
  end;

  DependencyInstallPage.SetProgress(Step, StepCount);
  Result := True;
end;

function InstallDeskBoxDependencies(var NeedsRestart: Boolean): Boolean;
var
  Step: Integer;
  StepCount: Integer;
begin
  Result := True;
  Step := 0;
  StepCount := 0;

  if ShouldInstallDotNetRuntime then
    StepCount := StepCount + 1;

  if ShouldInstallWindowsAppRuntime then
    StepCount := StepCount + 1;

  if StepCount = 0 then
    Exit;

  DependencyInstallPage.Show;
  try
    if ShouldInstallDotNetRuntime then
    begin
      Step := Step + 1;
      if not InstallDownloadedDependency(
        '.NET 8 Runtime x64',
        DotNetRuntimeInstallerName,
        '/install /quiet /norestart',
        Step,
        StepCount,
        NeedsRestart) then
      begin
        Result := False;
        Exit;
      end;
    end;

    if ShouldInstallWindowsAppRuntime then
    begin
      Step := Step + 1;
      if not InstallDownloadedDependency(
        'Windows App Runtime 2.1 x64',
        WindowsAppRuntimeInstallerName,
        '--quiet',
        Step,
        StepCount,
        NeedsRestart) then
      begin
        Result := False;
        Exit;
      end;
    end;
  finally
    DependencyInstallPage.Hide;
  end;
end;

procedure InitializeWizard;
begin
  DependencyDownloadPage := CreateDownloadPage('准备 DeskBox 运行环境', '正在下载缺失的运行时依赖。', nil);
  DependencyDownloadPage.ShowBaseNameInsteadOfUrl := True;
  DependencyInstallPage := CreateOutputProgressPage('准备 DeskBox 运行环境', '正在安装缺失的运行时依赖。');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  NeedsRestart: Boolean;
begin
  Result := True;

  if (CurPageID <> wpReady) or DependenciesPrepared then
    Exit;

  NeedsRestart := False;
  DetectDeskBoxDependencies;

  if not DownloadDeskBoxDependencies then
  begin
    Result := False;
    Exit;
  end;

  if not InstallDeskBoxDependencies(NeedsRestart) then
  begin
    Result := False;
    Exit;
  end;

  DependenciesPrepared := True;

  if NeedsRestart then
  begin
    SuppressibleMsgBox('运行时依赖安装完成，但需要重启电脑。重启后请重新运行 DeskBox 安装程序。', mbInformation, MB_OK, IDOK);
    Result := False;
  end;
end;
