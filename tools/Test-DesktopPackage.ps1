#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
$results = [IO.Path]::GetFullPath($ResultsDirectory)
$metadata = Get-Content -LiteralPath (Join-Path $packageRoot 'publish/publication.json') -Raw | ConvertFrom-Json
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
$client = $null

# Выделяет свободный loopback-порт для проверяемого дочернего процесса
function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}

# Запускает компонент именно из распакованного пакета; ключи передаются только через окружение
function Start-Component {
    param([string]$Path, [string[]]$Arguments = @(), [hashtable]$EnvironmentValues = @{})
    $start = [Diagnostics.ProcessStartInfo]::new($Path)
    $start.WorkingDirectory = $results
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($entry in $EnvironmentValues.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
    $process = [Diagnostics.Process]::Start($start)
    $processes.Add($process)
    return $process
}

# Ожидает фактический HTTP-ответ с ограничением времени и проверкой жизнеспособности процесса
function Get-ReadyResponse {
    param([string]$Url, [Diagnostics.Process]$Process)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if ($Process.HasExited) { throw 'Компонент завершился до готовности' }
        try { return $client.GetAsync($Url).GetAwaiter().GetResult() }
        catch { if ([DateTime]::UtcNow -ge $deadline) { throw }; Start-Sleep -Milliseconds 100 }
    } while ($true)
}

try {
    if (Test-Path -LiteralPath $results) { throw 'Каталог нативной проверки уже существует' }
    New-Item -ItemType Directory -Path $results | Out-Null
    $rid = $metadata.RuntimeIdentifier
    $architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $expected = if ($IsWindows) { "win-$architecture" } elseif ($IsLinux) { "linux-$architecture" } else { "osx-$architecture" }
    if ($rid -cne $expected) { throw "Проверка $rid требует нативного процесса $rid, текущий $expected" }
    if ($IsWindows) {
        $archive = Join-Path $packageRoot "$($metadata.PackId)-$($metadata.Channel)-Portable.zip"
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $results)
        $application = Join-Path $results 'current'
    } elseif ($IsLinux) {
        $appImage = Join-Path $packageRoot "$($metadata.PackId)-$($metadata.Channel).AppImage"
        & chmod +x -- $appImage
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось разрешить исполнение AppImage' }
        Push-Location $results
        try {
            & $appImage --appimage-extract | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'AppImage не распаковывается' }
        } finally { Pop-Location }
        $application = Join-Path $results 'squashfs-root/usr/bin'
    } else {
        $archive = Join-Path $packageRoot "$($metadata.PackId)-$($metadata.Channel)-Portable.zip"
        & /usr/bin/ditto -x -k $archive $results
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось распаковать macOS ZIP' }
        $bundles = @(Get-ChildItem -LiteralPath $results -Directory -Filter '*.app')
        if ($bundles.Count -ne 1) { throw 'В macOS ZIP ожидается один .app' }
        $plist = Join-Path $bundles[0].FullName 'Contents/Info.plist'
        $bundleId = & /usr/bin/plutil -extract CFBundleIdentifier raw -o - $plist
        if ($LASTEXITCODE -ne 0 -or $bundleId -cne $metadata.MacOSBundleId) { throw 'Неверный macOS bundle ID' }
        $application = Join-Path $bundles[0].FullName 'Contents/MacOS'
    }
    $desktop = Start-Component -Path (Join-Path $application $metadata.Desktop) -Arguments @('--veloapp-install', $metadata.Version)
    if (!$desktop.WaitForExit(10000) -or $desktop.ExitCode -ne 0) { throw 'Bootstrap Desktop не завершился до создания UI' }
    Write-Host "Desktop bootstrap $rid`: exit 0"
    $token = [Guid]::NewGuid().ToString('N')
    $gatewayPort = Get-FreePort
    $agentPath = Join-Path $application $metadata.Agent
    $gateway = Start-Component -Path $agentPath -Arguments @('--inference-gateway') -EnvironmentValues @{
        Gateway__Address = '127.0.0.1'; Gateway__Port = "$gatewayPort"; Gateway__Backend = 'http://127.0.0.1:1'; Gateway__Token = $token
    }
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(2)
    $url = "http://127.0.0.1:$gatewayPort/olc/gateway-ready"
    $response = Get-ReadyResponse -Url $url -Process $gateway
    try { if ([int]$response.StatusCode -ne 401) { throw 'Шлюз принимает запрос без ключа' } } finally { $response.Dispose() }
    $client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
    $response = Get-ReadyResponse -Url $url -Process $gateway
    try { if ([int]$response.StatusCode -ne 204) { throw 'Шлюз не подтвердил готовность с ключом' } } finally { $response.Dispose() }
    $client.Dispose()
    $agentPort = Get-FreePort
    $agent = Start-Component -Path $agentPath -EnvironmentValues @{
        Router__ListenAddress = '127.0.0.1'; Router__Port = "$agentPort"; Router__AccessToken = $token;
        Router__UseTls = 'false'; Router__AllowRemoteControl = 'false'
    }
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(2)
    $client.DefaultRequestVersion = [Version]'2.0'
    $client.DefaultVersionPolicy = [Net.Http.HttpVersionPolicy]::RequestVersionExact
    $response = Get-ReadyResponse -Url "http://127.0.0.1:$agentPort/" -Process $agent
    try {
        if ([int]$response.StatusCode -ne 401 -or $response.Version.Major -ne 2) { throw 'Agent не подтвердил HTTP/2 и проверку ключа' }
    } finally { $response.Dispose() }
    Write-Host 'Gateway: 401/204; Agent: HTTP/2 401; packaged apphosts started'
} catch {
    Write-Error "Нативная проверка пакета не пройдена: $_"
    throw
} finally {
    if ($client) { $client.Dispose() }
    foreach ($process in $processes) {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
