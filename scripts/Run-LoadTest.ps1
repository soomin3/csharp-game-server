[CmdletBinding()]
param(
    [Alias('ProjectRoot')][string]$Root = (Split-Path -Path $PSScriptRoot -Parent),
    [int]$Clients = 500,
    [int]$Duration = 10,
    [int]$Warmup = 2,
    [int]$Interval = 100,
    [int]$Rooms = 3,
    [int]$Workers = 2,
    [int]$Port = 17777,
    [int]$LoginPort = 15000,
    [string]$Output = 'benchmarks/latest.json',
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Write-BenchmarkReport([string]$Path, $Report) {
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Path)) | Out-Null
    $temporary = $Path + '.tmp-' + [Guid]::NewGuid().ToString('N')
    $json = ConvertTo-Json -InputObject $Report -Depth 32
    [System.IO.File]::WriteAllText($temporary, $json, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::Move($temporary, $Path, $true)
}

function ConvertTo-BenchmarkArgument([string]$Value) {
    # Windows command-line quoting, including backslashes before a quote or at the end.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Get-BenchmarkValue($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return ,$property.Value
}

function Get-BenchmarkNumber($Object, [string]$Name) {
    $value = Get-BenchmarkValue $Object $Name
    if ($null -eq $value -or $value -is [bool]) { throw "측정 숫자 항목이 없습니다: $Name" }
    $number = [Convert]::ToDouble($value, [Globalization.CultureInfo]::InvariantCulture)
    if (-not [double]::IsFinite($number) -or $number -lt 0) { throw "측정 숫자 항목이 잘못됐습니다: $Name" }
    return $number
}

function Read-BenchmarkJson([string]$Path) {
    $parameters = @{ InputObject = [System.IO.File]::ReadAllText($Path) }
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $parameters.DateKind = 'String' }
    return ConvertFrom-Json @parameters
}

function Get-BenchmarkUnixTime($Value) {
    if ($Value -is [DateTimeOffset]) { return $Value.ToUnixTimeMilliseconds() }
    if ($Value -is [DateTime]) { return ([DateTimeOffset]$Value).ToUnixTimeMilliseconds() }
    if ([string]::IsNullOrWhiteSpace([string]$Value)) { throw '클라이언트 측정 시각이 없습니다.' }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture).ToUnixTimeMilliseconds()
}

function Measure-BenchmarkTicks([object[]]$Samples) {
    if ($null -eq $Samples -or $Samples.Count -eq 0) {
        return [pscustomobject]@{ count=0; maxMilliseconds=0; meanMilliseconds=0; p95Milliseconds=0; p99Milliseconds=0; over50MillisecondsCount=0; maxPendingJobs=0; maxPlayerCount=0 }
    }
    [double[]]$elapsed = $Samples | ForEach-Object { $_.elapsedMilliseconds }
    [Array]::Sort($elapsed)
    $mean = 0.0
    $over50 = 0
    $pending = 0
    $players = 0
    for ($index = 0; $index -lt $Samples.Count; $index++) {
        $sample = $Samples[$index]
        $mean += ($sample.elapsedMilliseconds - $mean) / ($index + 1)
        if ($sample.elapsedMilliseconds -gt 50) { $over50++ }
        $pending = [Math]::Max($pending, $sample.pendingJobs)
        $players = [Math]::Max($players, $sample.playerCount)
    }
    return [pscustomobject]@{
        count = $Samples.Count
        maxMilliseconds = $elapsed[-1]
        meanMilliseconds = $mean
        p95Milliseconds = $elapsed[[int][Math]::Ceiling($elapsed.Count * 0.95) - 1]
        p99Milliseconds = $elapsed[[int][Math]::Ceiling($elapsed.Count * 0.99) - 1]
        over50MillisecondsCount = $over50
        maxPendingJobs = $pending
        maxPlayerCount = $players
    }
}

function Add-BenchmarkCondition($List, [string]$Name, [bool]$Passed, $Actual, $Expected) {
    $List.Add([pscustomobject]@{ name=$Name; passed=$Passed; actual=$Actual; expected=$Expected })
}

function Test-BenchmarkNoErrors($Value) {
    if ($null -eq $Value) { return $false }
    if ($Value -is [System.Array]) { return $Value.Length -eq 0 }
    if ($Value -is [string] -or $Value -is [bool]) { return $false }
    return $Value -eq 0
}

$taskExitCode = 1
$taskDemo = $null
$taskOutputFile = $null
$taskReport = [ordered]@{
    success=$false; passed=$false; phase='setup'; startedUtc=[DateTimeOffset]::UtcNow.ToString('O')
    baseCommit=$null; worktreeDirty=$null
    configuration=[ordered]@{ clients=$Clients; durationSeconds=$Duration; warmupSeconds=$Warmup; intervalMilliseconds=$Interval; rooms=$Rooms; workers=$Workers; port=$Port; loginPort=$LoginPort; seed=42; quiet=$true; configuration='Release' }
    artifacts=$null; conditions=@()
    client=$null; window=$null; server=$null; assemblyHashes=$null
}

try {
    $taskRoot = [System.IO.Path]::GetFullPath($Root)
    $taskOutputFile = if ([System.IO.Path]::IsPathRooted($Output)) { [System.IO.Path]::GetFullPath($Output) } else { [System.IO.Path]::GetFullPath((Join-Path $taskRoot $Output)) }
    # 시작할 때 false를 저장해 이전의 성공 결과가 실패한 실행에 남지 않게 한다.
    Write-BenchmarkReport $taskOutputFile $taskReport

    foreach ($range in @(
        @('Clients',$Clients,1,5000), @('Duration',$Duration,1,3600), @('Warmup',$Warmup,0,60),
        @('Interval',$Interval,10,10000), @('Rooms',$Rooms,1,100), @('Workers',$Workers,1,1024),
        @('Port',$Port,1,65535), @('LoginPort',$LoginPort,1,65535)
    )) {
        if ($range[1] -lt $range[2] -or $range[1] -gt $range[3]) { throw "$($range[0]) 범위는 $($range[2])~$($range[3])입니다." }
    }
    if ($Port -eq $LoginPort) { throw '게임 포트와 로그인 포트는 달라야 합니다.' }
    $taskSolution = Join-Path $taskRoot 'MyServer.slnx'
    if (-not [System.IO.File]::Exists($taskSolution)) { throw 'Root에서 MyServer.slnx를 찾을 수 없습니다.' }
    $taskDotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $taskPwsh = Join-Path $PSHOME 'pwsh.exe'
    if (-not [System.IO.File]::Exists($taskPwsh)) { $taskPwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source }
    $taskStartDemo = Join-Path $PSScriptRoot 'Start-Demo.ps1'
    if (-not [System.IO.File]::Exists($taskStartDemo)) { throw 'Start-Demo.ps1을 찾을 수 없습니다.' }

    foreach ($number in @($Port, $LoginPort)) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $number)
        $listener.ExclusiveAddressUse = $true
        try { $listener.Start() }
        catch [System.Net.Sockets.SocketException] { throw "127.0.0.1:$number 포트를 사용할 수 없습니다." }
        finally { $listener.Stop() }
    }

    if (-not $NoBuild) {
        & $taskDotnet build $taskSolution --configuration Release
        if ($LASTEXITCODE -ne 0) { throw "Release 빌드 실패: $LASTEXITCODE" }
    }
    $taskLoadDll = Join-Path $taskRoot 'LoadClient/bin/Release/net8.0/LoadClient.dll'
    if (-not [System.IO.File]::Exists($taskLoadDll)) { throw 'LoadClient Release 실행 파일이 없습니다.' }
    $taskRunId = [Guid]::NewGuid().ToString('N')
    $taskRunDirectory = Join-Path $taskRoot ('artifacts/run-' + $taskRunId)
    [System.IO.Directory]::CreateDirectory($taskRunDirectory) | Out-Null
    $taskDatabase = Join-Path $taskRunDirectory 'players.db'
    $taskGameLog = Join-Path $taskRunDirectory 'game.log'
    $taskServerJson = Join-Path $taskRunDirectory 'server.json'
    $taskClientJson = Join-Path $taskRunDirectory 'client.json'
    $taskReport.configuration = [ordered]@{ clients=$Clients; durationSeconds=$Duration; warmupSeconds=$Warmup; intervalMilliseconds=$Interval; rooms=$Rooms; workers=$Workers; port=$Port; loginPort=$LoginPort; seed=42; quiet=$true; configuration='Release' }
    $taskReport.artifacts = [ordered]@{ runDirectory=[System.IO.Path]::GetRelativePath($taskRoot,$taskRunDirectory); serverResult=[System.IO.Path]::GetRelativePath($taskRoot,$taskServerJson); clientResult=[System.IO.Path]::GetRelativePath($taskRoot,$taskClientJson) }

    $taskGit = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $taskGit) {
        $commit = & $taskGit.Source -C $taskRoot rev-parse HEAD 2>$null
        if ($LASTEXITCODE -eq 0) {
            $taskReport.baseCommit = ([string]$commit).Trim()
            $status = & $taskGit.Source -C $taskRoot status --porcelain 2>$null
            if ($LASTEXITCODE -eq 0) { $taskReport.worktreeDirty = [bool]$status }
        }
    }
    $taskHashes = [ordered]@{}
    foreach ($assembly in @('Game/bin/Release/net8.0/Game.dll','Game/bin/Release/net8.0/Common.dll','Login/bin/Release/net8.0/Login.dll','LoadClient/bin/Release/net8.0/LoadClient.dll')) {
        $taskHashes[$assembly] = (Get-FileHash -LiteralPath (Join-Path $taskRoot $assembly) -Algorithm SHA256).Hash
    }
    $taskReport.assemblyHashes = $taskHashes
    Write-BenchmarkReport $taskOutputFile $taskReport

    $taskRunSeconds = 60 + $Warmup + $Duration + 5
    $taskDemoArguments = @('-NoLogo','-NoProfile','-NonInteractive','-File',$taskStartDemo,'-ProjectRoot',$taskRoot,
        '-Port',"$Port",'-LoginPort',"$LoginPort",'-Workers',"$Workers",'-Rooms',"$Rooms",'-DataPath',$taskDatabase,
        '-LogPath',$taskGameLog,'-MetricsPath',$taskServerJson,'-RunSeconds',"$taskRunSeconds",'-NoBuild')
    $taskQuotedArguments = $taskDemoArguments | ForEach-Object { ConvertTo-BenchmarkArgument $_ }
    $taskDemo = Start-Process -FilePath $taskPwsh -ArgumentList $taskQuotedArguments -WorkingDirectory $taskRoot `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskRunDirectory 'demo.stdout.log') `
        -RedirectStandardError (Join-Path $taskRunDirectory 'demo.stderr.log')

    $taskReady = $false
    $taskDeadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $taskDeadline) {
        $taskDemo.Refresh()
        if ($taskDemo.HasExited) { throw "서비스 시작 실패. 종료 코드: $($taskDemo.ExitCode)" }
        $probe = $null
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$LoginPort/" -Method Get -TimeoutSec 1 -NoProxy
            if ((Get-BenchmarkValue $health 'service') -eq 'MyServer demo login') {
                $probe = [System.Net.Sockets.TcpClient]::new()
                $probe.Connect('127.0.0.1',$Port)
                $taskReady = $true
                break
            }
        }
        catch { }
        finally { if ($null -ne $probe) { $probe.Dispose() } }
        Start-Sleep -Milliseconds 100
    }
    if (-not $taskReady) { throw 'HTTP 또는 게임 서버의 준비 시간이 초과됐습니다.' }

    $taskReport.phase = 'measurement'
    Write-BenchmarkReport $taskOutputFile $taskReport
    $taskLoadArguments = @($taskLoadDll,'--host','127.0.0.1','--port',"$Port",'--login-url',"http://127.0.0.1:$LoginPort/login",
        '--clients',"$Clients",'--duration',"$Duration",'--warmup',"$Warmup",'--interval',"$Interval",'--rooms',"$Rooms",'--seed','42','--output',$taskClientJson)
    & $taskDotnet @taskLoadArguments 1> (Join-Path $taskRunDirectory 'client.stdout.log') 2> (Join-Path $taskRunDirectory 'client.stderr.log')
    $taskLoadExitCode = $LASTEXITCODE
    $taskReport.clientExitCode = $taskLoadExitCode
    $taskReport.phase = 'shutdown'
    Write-BenchmarkReport $taskOutputFile $taskReport
    if (-not $taskDemo.WaitForExit(($taskRunSeconds + 35) * 1000)) { throw '서버의 정상 자동 종료를 확인하지 못했습니다.' }
    $taskDemoExitCode = $taskDemo.ExitCode
    $taskReport.demoExitCode = $taskDemoExitCode

    $taskReport.phase = 'analysis'
    $client = Read-BenchmarkJson $taskClientJson
    $server = Read-BenchmarkJson $taskServerJson
    $taskReport.client = $client
    $metrics = Get-BenchmarkValue $server 'metrics'
    $rawSamples = Get-BenchmarkValue $metrics 'samples'
    if ($null -eq $rawSamples) { throw '서버의 타임스탬프 틱 샘플이 없습니다.' }
    $start = Get-BenchmarkUnixTime (Get-BenchmarkValue $client 'measurementStartUtc')
    $end = Get-BenchmarkUnixTime (Get-BenchmarkValue $client 'measurementEndUtc')
    if ($start -lt 0 -or $end -le $start) { throw '측정 구간이 잘못됐습니다.' }
    $selected = [System.Collections.Generic.List[object]]::new()
    foreach ($sample in $rawSamples) {
        $time = Get-BenchmarkNumber $sample 'timestampUtcMilliseconds'
        $room = Get-BenchmarkNumber $sample 'roomId'
        $elapsed = Get-BenchmarkNumber $sample 'elapsedMilliseconds'
        $players = Get-BenchmarkNumber $sample 'playerCount'
        $pending = Get-BenchmarkNumber $sample 'pendingJobs'
        if ($time -ne [Math]::Floor($time) -or $room -ne [Math]::Floor($room) -or $players -ne [Math]::Floor($players) -or $pending -ne [Math]::Floor($pending)) { throw '틱 샘플의 정수 항목이 잘못됐습니다.' }
        if ($time -ge $start -and $time -lt $end) {
            $selected.Add([pscustomobject]@{ roomId=[int]$room; timestampUtcMilliseconds=[long]$time; elapsedMilliseconds=$elapsed; playerCount=[int]$players; pendingJobs=[int]$pending })
        }
    }
    $samples = $selected.ToArray()
    $overall = Measure-BenchmarkTicks $samples
    $minimumTicks = [int][Math]::Floor($Duration * 20 * 0.8)
    $conditions = [System.Collections.Generic.List[object]]::new()
    $taskReport.conditions = $conditions
    Add-BenchmarkCondition $conditions 'loadExitZero' ($taskLoadExitCode -eq 0) $taskLoadExitCode 0
    Add-BenchmarkCondition $conditions 'demoExitZero' ($taskDemoExitCode -eq 0) $taskDemoExitCode 0
    $success = Get-BenchmarkValue $client 'success'
    Add-BenchmarkCondition $conditions 'clientSuccess' ($success -is [bool] -and $success) $success $true
    foreach ($name in @('requestedClients','healthyAtStart','healthyAtEnd','clientsWithMeasuredMoves','clientsWithConfirmedMoves')) {
        $value = Get-BenchmarkNumber $client $name
        Add-BenchmarkCondition $conditions $name ($value -eq $Clients) $value $Clients
    }
    $errors = Get-BenchmarkNumber $client 'errorCount'
    Add-BenchmarkCondition $conditions 'clientErrorsZero' ($errors -eq 0) $errors 0
    foreach ($name in @('movesSent','movesConfirmed','packetsSent','packetsReceived','bytesReceived')) {
        $value = Get-BenchmarkNumber $client $name
        Add-BenchmarkCondition $conditions ('client.'+$name) ($value -gt 0) $value '> 0'
    }
    $dropped = Get-BenchmarkNumber $metrics 'droppedCount'
    $retained = Get-BenchmarkNumber $metrics 'retainedCount'
    $recorded = Get-BenchmarkNumber $metrics 'totalRecorded'
    $rawSampleCount = if ($rawSamples -is [Array]) { $rawSamples.Length } else { 1 }
    Add-BenchmarkCondition $conditions 'retainedSampleCountMatches' ($retained -eq $rawSampleCount) $retained $rawSampleCount
    Add-BenchmarkCondition $conditions 'retentionAccountingMatches' ($recorded -ge $retained -and $recorded - $retained -eq $dropped) ($recorded-$retained) $dropped
    Add-BenchmarkCondition $conditions 'measurementDuration' (($end-$start)/1000.0 -ge $Duration*0.8) (($end-$start)/1000.0) ('>= '+($Duration*0.8)+' seconds')
    Add-BenchmarkCondition $conditions 'noDroppedTickSamples' ($dropped -eq 0) $dropped 0
    Add-BenchmarkCondition $conditions 'samplesPresent' ($overall.count -gt 0) $overall.count '> 0'
    Add-BenchmarkCondition $conditions 'onlyConfiguredRooms' (@($samples | Where-Object { $_.roomId -lt 1 -or $_.roomId -gt $Rooms }).Count -eq 0) @($samples | Select-Object -ExpandProperty roomId -Unique) (1..$Rooms)
    Add-BenchmarkCondition $conditions 'tickBudget' ($overall.maxMilliseconds -le 50 -and $overall.over50MillisecondsCount -eq 0) $overall.maxMilliseconds '<= 50ms'
    $roomStatistics = @(
        for ($id = 1; $id -le $Rooms; $id++) {
            $roomStats = Measure-BenchmarkTicks @($samples | Where-Object { $_.roomId -eq $id })
            $expectedPlayers = [int][Math]::Floor($Clients / $Rooms)
            if ($id -le ($Clients % $Rooms)) { $expectedPlayers++ }
            Add-BenchmarkCondition $conditions "room.$id.tickCount" ($roomStats.count -ge $minimumTicks) $roomStats.count $minimumTicks
            Add-BenchmarkCondition $conditions "room.$id.tickBudget" ($roomStats.count -gt 0 -and $roomStats.maxMilliseconds -le 50) $roomStats.maxMilliseconds '<= 50ms with samples'
            Add-BenchmarkCondition $conditions "room.$id.players" ($roomStats.maxPlayerCount -eq $expectedPlayers) $roomStats.maxPlayerCount $expectedPlayers
            [pscustomobject]@{ roomId=$id; statistics=$roomStats; expectedPlayers=$expectedPlayers }
        }
    )
    foreach ($name in @('activeSessions','pendingJobs','pendingPersistence','sessionErrors','jobErrors')) {
        $value = Get-BenchmarkNumber $server $name
        Add-BenchmarkCondition $conditions ('server.'+$name) ($value -eq 0) $value 0
    }
    $shutdownErrors = Get-BenchmarkValue $server 'shutdownErrors'
    Add-BenchmarkCondition $conditions 'shutdownErrorsZero' (Test-BenchmarkNoErrors $shutdownErrors) $shutdownErrors 0
    $environment = Get-BenchmarkValue $server 'environment'
    $environmentPresent = -not [string]::IsNullOrWhiteSpace([string](Get-BenchmarkValue $environment 'framework')) -and
        -not [string]::IsNullOrWhiteSpace([string](Get-BenchmarkValue $environment 'os')) -and
        -not [string]::IsNullOrWhiteSpace([string](Get-BenchmarkValue $environment 'assemblyMvid')) -and
        (Get-BenchmarkNumber $environment 'processorCount') -gt 0
    Add-BenchmarkCondition $conditions 'environmentPresent' $environmentPresent $environment 'framework/os/processorCount/assemblyMvid'
    $traffic = Get-BenchmarkValue $server 'traffic'
    $packetsSent = Get-BenchmarkNumber $traffic 'packetsSent'
    $bytesSent = Get-BenchmarkNumber $traffic 'bytesSent'
    Add-BenchmarkCondition $conditions 'serverTrafficPresent' ($packetsSent -gt 0 -and $bytesSent -gt 0) $traffic '> 0'
    $taskReport.window = [ordered]@{ startUtcMilliseconds=$start; endUtcMilliseconds=$end; durationSeconds=($end-$start)/1000.0; minimumTicksPerRoom=$minimumTicks; percentile='nearest-rank'; overall=$overall; rooms=$roomStatistics; samples=$samples }
    $taskReport.server = [ordered]@{ environment=$environment; lifetimeTraffic=$traffic; totalRecorded=(Get-BenchmarkNumber $metrics 'totalRecorded'); retainedCount=(Get-BenchmarkNumber $metrics 'retainedCount'); droppedCount=$dropped; activeSessions=(Get-BenchmarkNumber $server 'activeSessions'); pendingJobs=(Get-BenchmarkNumber $server 'pendingJobs'); pendingPersistence=(Get-BenchmarkNumber $server 'pendingPersistence'); sessionErrors=(Get-BenchmarkNumber $server 'sessionErrors'); jobErrors=(Get-BenchmarkNumber $server 'jobErrors'); shutdownErrors=$shutdownErrors }
    $taskReport.conditions = $conditions.ToArray()
    $taskReport.success = @($conditions | Where-Object { -not $_.passed }).Count -eq 0
    $taskReport.passed = $taskReport.success
    $taskReport.phase = 'complete'
    if ($taskReport.success) { $taskExitCode = 0 }
}
catch [System.Management.Automation.PipelineStoppedException] {
    $taskReport.phase = 'cancelled'
    $taskReport.success = $false
    $taskReport.passed = $false
    $taskExitCode = 130
}
catch {
    $taskReport.failureStage = $taskReport.phase
    $taskReport.phase = 'failed'
    $taskReport.success = $false
    $taskReport.passed = $false
    $taskReport.error = $_.Exception.Message
    try { [Console]::Error.WriteLine('[Run-LoadTest] ' + $_.Exception.Message) } catch { }
}
finally {
    try {
        if ($null -ne $taskDemo) {
            $taskDemo.Refresh()
            if (-not $taskDemo.HasExited) {
                $taskDemo.Kill($true)
                if (-not $taskDemo.WaitForExit(5000)) { throw '자체 데모 프로세스의 종료를 확인하지 못했습니다.' }
            }
        }
    }
    catch {
        $taskReport.cleanupError = '이 실행이 시작한 데모 프로세스의 정리에 실패했습니다.'
        $taskReport.success = $false
        $taskReport.passed = $false
        $taskExitCode = 1
    }
    finally {
        if ($null -ne $taskDemo) { $taskDemo.Dispose() }
        $taskReport.finishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        if ($null -ne $taskOutputFile) {
            try { Write-BenchmarkReport $taskOutputFile $taskReport }
            catch { $taskExitCode = 1; try { [Console]::Error.WriteLine('최종 벤치마크 결과를 저장하지 못했습니다.') } catch { } }
        }
    }
}

if ($taskExitCode -eq 0) { Write-Host "부하 검증 통과: $taskOutputFile" }
exit $taskExitCode
