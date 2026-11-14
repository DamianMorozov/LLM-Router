#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [string]$OutputDirectory,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$payload = [IO.Path]::GetFullPath($PublishDirectory)
if (!$OutputDirectory) { $OutputDirectory = Split-Path -Parent $payload }
$output = [IO.Path]::GetFullPath($OutputDirectory)

# Проверяет состав и ссылки на внешние runtime перед передачей файлов упаковщику
function Test-Component {
    param([string]$Directory, [string]$Name, [bool]$RequiresAspNet)
    $apphost = if ($metadata.RuntimeIdentifier -eq 'win-x64') { "$Name.exe" } else { $Name }
    foreach ($file in @($apphost, "$Name.dll", "$Name.runtimeconfig.json", "$Name.deps.json")) {
        $path = Join-Path $Directory $file
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "Неполная публикация: отсутствует или пуст $path"
        }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $Directory "$Name.runtimeconfig.json") -Raw | ConvertFrom-Json -AsHashtable
    $frameworks = @($runtime.runtimeOptions.framework) + @($runtime.runtimeOptions.frameworks) | Where-Object { $_ }
    if ($runtime.runtimeOptions.includedFrameworks -or 'Microsoft.NETCore.App' -notin $frameworks.name -or
        ($RequiresAspNet -and 'Microsoft.AspNetCore.App' -notin $frameworks.name)) { throw "У $Name не заданы внешние runtime" }
    foreach ($framework in $frameworks) {
        if ($framework.version -notmatch '^10\.0\.\d+$') { throw "У $Name неверная версия runtime" }
    }
    $deps = Get-Content -LiteralPath (Join-Path $Directory "$Name.deps.json") -Raw | ConvertFrom-Json -AsHashtable
    if (!$deps.libraries.ContainsKey("$Name/$($metadata.Version)") -or !$deps.runtimeTarget.name.EndsWith('/' + $metadata.RuntimeIdentifier)) {
        throw "Версия или архитектура $Name не совпадает с метаданными"
    }
}

# Вызывает локально закреплённый vpk без зависимости от глобальной версии инструмента
function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Команда dotnet завершилась с кодом $LASTEXITCODE" }
}

# Проверяет распаковку штатным runtime AppImage, а не только успешный выход mksquashfs
function Test-AppImage {
    param([string]$Path)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $inspection = Join-Path $temporaryRoot ('olc-appimage-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $inspection | Out-Null
    Push-Location $inspection
    try {
        & $Path --appimage-extract | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'AppImage не распаковывается собственным runtime' }
        foreach ($entry in @('OpenLLMCluster.Desktop', 'Agent/OpenLLMCluster.Agent')) {
            if (!(Test-Path -LiteralPath (Join-Path $inspection "squashfs-root/usr/bin/$entry"))) {
                throw "AppImage не содержит $entry"
            }
        }
    } finally {
        Pop-Location
        $resolved = [IO.Path]::GetFullPath($inspection)
        if (!$resolved.StartsWith([IO.Path]::TrimEndingDirectorySeparator($temporaryRoot) + [IO.Path]::DirectorySeparatorChar)) {
            throw 'Неверная граница временного каталога проверки'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

Push-Location $repositoryRoot
try {
    $metadata = Get-Content -LiteralPath (Join-Path $payload 'publication.json') -Raw | ConvertFrom-Json -AsHashtable
    [xml]$properties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
    [xml]$publication = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/Publication.props') -Raw
    $version = $properties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    $packId = $publication.SelectSingleNode('/Project/PropertyGroup/VelopackPackId').InnerText
    $bundleId = $publication.SelectSingleNode('/Project/PropertyGroup/MacOSBundleId').InnerText
    $manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot '.config/dotnet-tools.json') -Raw | ConvertFrom-Json -AsHashtable
    [xml]$packages = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Packages.props') -Raw
    $libraryVersion = $packages.SelectSingleNode('/Project/ItemGroup/PackageVersion[@Include="Velopack"]').Version
    $rid = $metadata.RuntimeIdentifier
    if ($rid -notin @('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')) { throw 'Неизвестная платформа публикации' }
    if ($metadata.Version -cne $version -or $metadata.PackageVersion -cne $version -or $metadata.ReleaseTag -cne "v$version" -or
        $metadata.PackId -cne $packId -or $metadata.MacOSBundleId -cne $bundleId) { throw 'Версия или идентификаторы публикации не совпадают с проектом' }
    if ($metadata.Channel -cne "$rid-stable" -and $metadata.Channel -cne "$rid-beta") { throw 'Канал публикации не соответствует RID' }
    if ($metadata.SelfContained -isnot [bool] -or $metadata.SelfContained -or
        ($metadata.RequiredFrameworks -join ',') -cne 'Microsoft.NETCore.App/10.0,Microsoft.AspNetCore.App/10.0') {
        throw 'Ожидается framework-dependent публикация с .NET и ASP.NET Core Runtime 10'
    }
    if ($metadata.VelopackVersion -cne $manifest.tools.vpk.version -or $metadata.VelopackVersion -cne $libraryVersion) {
        throw 'Версии Velopack в публикации, библиотеке и инструменте не совпадают'
    }
    $expectedDesktop = if ($rid -eq 'win-x64') { 'OpenLLMCluster.Desktop.exe' } else { 'OpenLLMCluster.Desktop' }
    $expectedAgent = if ($rid -eq 'win-x64') { 'Agent/OpenLLMCluster.Agent.exe' } else { 'Agent/OpenLLMCluster.Agent' }
    if ($metadata.Desktop -cne $expectedDesktop -or $metadata.Agent -cne $expectedAgent) { throw 'Неверные точки входа Desktop или Agent' }
    Test-Component -Directory $payload -Name 'OpenLLMCluster.Desktop' -RequiresAspNet $false
    Test-Component -Directory (Join-Path $payload 'Agent') -Name 'OpenLLMCluster.Agent' -RequiresAspNet $true
    $unexpected = Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object {
        $_.Name -match '(?i)(\.gguf$|\.log$|\.secrets$|\.pfx$|^profile.*\.json$|^llama[-.]|^ggml[-.]|^rpc-server)' -or
        $_.Name -match '^(hostfxr\.dll|hostpolicy\.dll|coreclr\.dll|lib(hostfxr|hostpolicy|coreclr)\.(so|dylib)|System\.Private\.CoreLib\.dll)$'
    }
    if ($unexpected) { throw "Публикация содержит runtime или пользовательские файлы: $($unexpected.Name -join ', ')" }

    $requiredHost = if ($rid -eq 'win-x64') { 'Windows' } elseif ($rid -eq 'linux-x64') { 'Linux' } else { 'macOS' }
    $hostSupported = ($IsWindows -and $requiredHost -eq 'Windows') -or ($IsLinux -and $requiredHost -eq 'Linux') -or ($IsMacOS -and $requiredHost -eq 'macOS')
    $iconExtension = if ($rid -eq 'win-x64') { 'ico' } elseif ($rid -eq 'linux-x64') { 'png' } else { 'icns' }
    $icon = Join-Path $repositoryRoot "src/OpenLLMCluster.Desktop/Assets/open-llm-cluster.$iconExtension"
    if (!(Test-Path -LiteralPath $icon -PathType Leaf)) { throw "Не найдена иконка $icon" }
    $arguments = @('vpk', 'pack', '--packId', $packId, '--packVersion', $version, '--packDir', $payload, '--outputDir', $output,
        '--runtime', $rid, '--channel', $metadata.Channel, '--packTitle', 'Open LLM Cluster', '--mainExe', $expectedDesktop,
        '--icon', $icon, '--delta', 'None')
    if ($rid -eq 'win-x64') {
        if ($metadata.WindowsRuntimeDependencies -cne 'net10.0-x64-runtime,net10.0-x64-aspnetcore') { throw 'Неверные зависимости Windows Setup' }
        $arguments += @('--framework', $metadata.WindowsRuntimeDependencies)
    } elseif ($rid -eq 'linux-x64') {
        # Встроенный runtime этой версии vpk не умеет читать xz, хотя mksquashfs успешно его создаёт
        $arguments += @('--compression', 'gzip', '--categories', 'Development;Utility')
    } else {
        # Предварительная неподписанная сборка: ZIP с .app, без PKG и обращения к Apple
        $arguments += @('--bundleId', $bundleId, '--noInst')
    }
    if ($ValidateOnly) {
        [ordered]@{ RequiredHost = $requiredHost; HostSupported = $hostSupported; Arguments = $arguments } | ConvertTo-Json -Depth 4
        return
    }
    if (!$hostSupported) { throw "OLCPACK_HOST: упаковка $rid требует $requiredHost; используй -ValidateOnly для проверки параметров" }
    if (Test-Path -LiteralPath (Join-Path $output "$packId-$version-$($metadata.Channel)-full.nupkg")) {
        throw 'Пакет этой версии и канала уже существует; используй отдельный каталог для повторной проверки'
    }
    if (!$IsWindows) {
        foreach ($entry in @($expectedDesktop, $expectedAgent)) {
            & chmod +x -- (Join-Path $payload $entry)
            if ($LASTEXITCODE -ne 0) { throw "Не удалось сделать исполняемым $entry" }
        }
    }
    Invoke-DotNet -Arguments @('tool', 'restore', '--tool-manifest', (Join-Path $repositoryRoot '.config/dotnet-tools.json'))
    Invoke-DotNet -Arguments $arguments
    $feed = Join-Path $output "releases.$($metadata.Channel).json"
    if (!(Test-Path -LiteralPath $feed)) { throw 'Упаковщик не создал индекс канала' }
    $assets = (Get-Content -LiteralPath $feed -Raw | ConvertFrom-Json).Assets
    $full = @($assets | Where-Object { $_.Type -eq 'Full' -and $_.Version -eq $version -and $_.PackageId -eq $packId })
    if ($full.Count -ne 1) { throw 'В индексе отсутствует единственный full-пакет текущей версии' }
    $fullPath = Join-Path $output $full[0].FileName
    $fullHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    if ((Get-Item -LiteralPath $fullPath).Length -ne $full[0].Size -or $fullHash -cne $full[0].SHA256.ToUpperInvariant()) {
        throw 'Размер или SHA256 full-пакета не совпадает с индексом'
    }
    $extension = if ($rid -eq 'win-x64') { '*Setup.exe' } elseif ($rid -eq 'linux-x64') { '*.AppImage' } else { '*Portable.zip' }
    if (!(Get-ChildItem -LiteralPath $output -File -Filter $extension)) { throw "Не создан дистрибутив $extension" }
    if ($rid -eq 'linux-x64') {
        $appImage = Get-ChildItem -LiteralPath $output -File -Filter '*.AppImage' | Select-Object -First 1
        Test-AppImage -Path $appImage.FullName
    }
    $checksums = Get-ChildItem -LiteralPath $output -File | Where-Object { $_.Name -ne 'SHA256SUMS' } | Sort-Object Name | ForEach-Object {
        "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
    }
    $checksums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS') -Encoding utf8NoBOM
    Write-Host "Пакеты $rid и SHA256SUMS подготовлены: $output"
    Write-Host 'Загрузка на GitHub не выполнялась'
} catch {
    Write-Error "Упаковка остановлена: $_"
    throw
} finally {
    Pop-Location
}
