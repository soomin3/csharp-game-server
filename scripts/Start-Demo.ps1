[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Path $PSScriptRoot -Parent),
    [ValidateRange(1, 65535)][int]$Port = 7777,
    [ValidateRange(1, 65535)][int]$LoginPort = 5000,
    [ValidateRange(1, [int]::MaxValue)][int]$Workers = 2,
    [ValidateRange(1, [int]::MaxValue)][int]$Rooms = 3,
    [string]$DataPath,
    [string]$LogPath,
    [string]$MetricsPath,
    [ValidateRange(0, [int]::MaxValue)][int]$RunSeconds = 0,
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Resolve-DemoFile([string]$Path, [string]$Fallback, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Path)) { $Path = $Fallback }
    if (-not [System.IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $Root $Path }
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-DemoPortAvailable([int]$Number) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Number)
    $listener.ExclusiveAddressUse = $true
    try {
        $listener.Start()
    }
    catch [System.Net.Sockets.SocketException] {
        throw "127.0.0.1:$Number 포트를 사용할 수 없습니다. 다른 서버의 포트를 확인하세요."
    }
    finally {
        $listener.Stop()
    }
}

function Write-DemoError([string]$Message) {
    try { [Console]::Error.WriteLine("[Start-Demo] $Message") }
    catch [System.IO.IOException] { }
    catch [System.ObjectDisposedException] { }
}

$taskExitCode = 1
$taskLoginProcess = $null
$taskKeyName = 'MY_SERVER_TOKEN_KEY'
$taskOriginalKey = [Environment]::GetEnvironmentVariable($taskKeyName, [EnvironmentVariableTarget]::Process)
$taskHadKey = [Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Process).Contains($taskKeyName)
$taskTemporaryKey = $null
$taskVerboseOutput = $PSBoundParameters.ContainsKey('Verbose') -and [bool]$PSBoundParameters['Verbose']

try {
    if ($Port -eq $LoginPort) { throw '게임 포트와 로그인 포트는 서로 달라야 합니다.' }
    $taskRootItem = Get-Item -LiteralPath $ProjectRoot
    if (-not $taskRootItem.PSIsContainer) { throw 'ProjectRoot에는 프로젝트 폴더를 지정하세요.' }
    $taskRoot = $taskRootItem.FullName
    $taskSolution = Join-Path $taskRoot 'MyServer.slnx'
    if (-not [System.IO.File]::Exists($taskSolution)) { throw 'ProjectRoot에서 MyServer.slnx를 찾을 수 없습니다.' }
    $taskDotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source

    Assert-DemoPortAvailable $Port
    Assert-DemoPortAvailable $LoginPort

    if (-not $NoBuild) {
        & $taskDotnet build $taskSolution --configuration Release
        $taskBuildExitCode = $LASTEXITCODE
        if ($taskBuildExitCode -ne 0) {
            $taskExitCode = $taskBuildExitCode
            throw "Release 빌드에 실패했습니다. 종료 코드: $taskBuildExitCode"
        }
    }

    $taskLoginDll = Join-Path $taskRoot 'Login/bin/Release/net8.0/Login.dll'
    $taskGameDll = Join-Path $taskRoot 'Game/bin/Release/net8.0/Game.dll'
    if (-not [System.IO.File]::Exists($taskLoginDll) -or -not [System.IO.File]::Exists($taskGameDll)) {
        throw 'Release 실행 파일이 없습니다. NoBuild 옵션을 빼고 실행하세요.'
    }

    $taskDataFile = Resolve-DemoFile $DataPath 'data/players.db' $taskRoot
    $taskLogFile = Resolve-DemoFile $LogPath 'logs/game.log' $taskRoot
    $taskMetricsFile = Resolve-DemoFile $MetricsPath 'artifacts/tick-metrics.json' $taskRoot
    $taskDistinctFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($taskFile in @($taskDataFile, $taskLogFile, $taskMetricsFile)) {
        if (-not $taskDistinctFiles.Add($taskFile)) { throw 'DB, 로그, 측정 결과는 서로 다른 파일로 지정하세요.' }
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($taskFile)) | Out-Null
    }

    $taskLoginLogDirectory = [System.IO.Path]::GetDirectoryName($taskLogFile)
    $taskLoginOutput = Join-Path $taskLoginLogDirectory "login-$LoginPort.stdout.log"
    $taskLoginError = Join-Path $taskLoginLogDirectory "login-$LoginPort.stderr.log"
    foreach ($taskLoginRedirect in @($taskLoginOutput, $taskLoginError)) {
        if (-not $taskDistinctFiles.Add($taskLoginRedirect)) {
            throw '로그인 서버의 출력 파일은 DB, 게임 로그, 측정 결과와 다른 경로여야 합니다.'
        }
    }
    $taskLoginUrl = "http://127.0.0.1:$LoginPort"

    # 키는 현재 스크립트 프로세스의 환경에만 두고 두 자식 프로세스가 상속한다.
    # 인자·출력·로그에는 키와 토큰을 넣지 않는다.
    if ([string]::IsNullOrEmpty($taskOriginalKey)) {
        $taskTemporaryKey = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        [Environment]::SetEnvironmentVariable($taskKeyName, $taskTemporaryKey, [EnvironmentVariableTarget]::Process)
    }

    # Start-Process는 ArgumentList를 공백으로 합치므로 DLL 경로의 따옴표를 명시한다.
    $taskLoginArguments = @(('"' + $taskLoginDll + '"'), '--urls', $taskLoginUrl)
    $taskLoginProcess = Start-Process -FilePath $taskDotnet -ArgumentList $taskLoginArguments `
        -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $taskLoginOutput -RedirectStandardError $taskLoginError

    $taskReady = $false
    $taskDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while ([DateTimeOffset]::UtcNow -lt $taskDeadline) {
        $taskLoginProcess.Refresh()
        if ($taskLoginProcess.HasExited) {
            if ($taskLoginProcess.ExitCode -ne 0) { $taskExitCode = $taskLoginProcess.ExitCode }
            throw "로그인 서버가 준비 전에 종료됐습니다. 로그: $taskLoginError"
        }
        try {
            $taskHealth = Invoke-RestMethod -Uri "$taskLoginUrl/" -Method Get -TimeoutSec 1 -NoProxy
            if ($taskHealth.service -eq 'MyServer demo login') {
                $taskReady = $true
                break
            }
        }
        catch {
            # HTTP 서버가 아직 준비되지 않았으면 제한 시간 안에서 다시 확인한다.
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $taskReady) { throw "로그인 서버 준비 시간이 초과됐습니다. 로그: $taskLoginError" }

    # 준비 확인 중 다른 프로그램이 게임 포트를 사용하기 시작한 경우도 거절한다.
    Assert-DemoPortAvailable $Port
    $taskGameArguments = @(
        $taskGameDll,
        '--port', $Port.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--workers', $Workers.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--rooms', $Rooms.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--data', $taskDataFile,
        '--log', $taskLogFile,
        '--metrics', $taskMetricsFile
    )
    if ($RunSeconds -gt 0) {
        $taskGameArguments += @('--shutdown-after', $RunSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
    }
    if (-not $taskVerboseOutput) { $taskGameArguments += '--quiet' }

    Write-Host "로그인 서버 준비 완료: $taskLoginUrl"
    Write-Host "게임 서버 시작: 127.0.0.1:$Port (종료: Ctrl+C)"
    & $taskDotnet @taskGameArguments
    $taskExitCode = $LASTEXITCODE
}
catch [System.Management.Automation.PipelineStoppedException] {
    $taskExitCode = 130
}
catch {
    Write-DemoError $_.Exception.Message
}
finally {
    try {
        if ($null -ne $taskLoginProcess) {
            $taskLoginProcess.Refresh()
            if (-not $taskLoginProcess.HasExited) {
                # 이 스크립트가 만든 프로세스 객체만 종료한다. 포트로 남의 프로세스를 찾지 않는다.
                $taskLoginProcess.Kill($true)
                if (-not $taskLoginProcess.WaitForExit(5000)) {
                    Write-DemoError '로그인 서버의 종료를 제한 시간 안에 확인하지 못했습니다.'
                    if ($taskExitCode -eq 0) { $taskExitCode = 1 }
                }
            }
        }
    }
    catch {
        Write-DemoError '이 스크립트가 시작한 로그인 서버를 정리하지 못했습니다.'
        if ($taskExitCode -eq 0) { $taskExitCode = 1 }
    }
    finally {
        try {
            if ($null -ne $taskLoginProcess) { $taskLoginProcess.Dispose() }
        }
        finally {
            if ($taskHadKey) {
                [Environment]::SetEnvironmentVariable($taskKeyName, $taskOriginalKey, [EnvironmentVariableTarget]::Process)
            }
            else {
                [Environment]::SetEnvironmentVariable($taskKeyName, $null, [EnvironmentVariableTarget]::Process)
            }
            $taskTemporaryKey = $null
            $taskOriginalKey = $null
        }
    }
}

exit $taskExitCode
