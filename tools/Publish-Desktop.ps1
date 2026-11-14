#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')][string]$RuntimeIdentifier,
    [ValidateSet('stable', 'beta')][string]$Channel = 'stable',
    [string]$ReleaseTag,
    [string]$ReleaseRoot,
    [switch]$ValidateOnly,
    [switch]$PackWindows,
    [switch]$Pack
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src/OpenLLMCluster.Desktop/OpenLLMCluster.Desktop.csproj'
$manifestPath = Join-Path $repositoryRoot '.config/dotnet-tools.json'

# Вызывает dotnet с контролем кода завершения, включая ошибки нативного процесса
function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Команда dotnet завершилась с кодом $LASTEXITCODE" }
}

# Проверяет ссылки на внешние runtime и отсутствие встроенного .NET в опубликованном приложении
function Test-PublishedComponent {
    param([string]$Directory, [string]$Name, [bool]$RequiresAspNet)
    $apphost = if ($RuntimeIdentifier.StartsWith('win-')) { "$Name.exe" } else { $Name }
    $nativeFiles = if ($RuntimeIdentifier.StartsWith('win-')) {
        @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')
    } elseif ($RuntimeIdentifier.StartsWith('linux-')) {
        @('libhostfxr.so', 'libhostpolicy.so', 'libcoreclr.so')
    } else {
        @('libhostfxr.dylib', 'libhostpolicy.dylib', 'libcoreclr.dylib')
    }
    foreach ($file in @($apphost, "$Name.dll", "$Name.runtimeconfig.json", "$Name.deps.json")) {
        $path = Join-Path $Directory $file
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "Неполная framework-dependent публикация: отсутствует или пуст $path"
        }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $Directory "$Name.runtimeconfig.json") -Raw | ConvertFrom-Json
    $frameworks = @($runtime.runtimeOptions.framework) + @($runtime.runtimeOptions.frameworks) | Where-Object { $_ }
    if ($runtime.runtimeOptions.includedFrameworks -or 'Microsoft.NETCore.App' -notin $frameworks.name) {
        throw "$Name должен ссылаться на внешний .NET Runtime"
    }
    if ($RequiresAspNet -and 'Microsoft.AspNetCore.App' -notin $frameworks.name) {
        throw "$Name должен ссылаться на внешний ASP.NET Core Runtime"
    }
    foreach ($framework in $frameworks) {
        if ($framework.version -notmatch '^10\.0\.\d+$') { throw "Неподдерживаемая версия runtime у $Name`: $($framework.version)" }
    }
    foreach ($file in @('System.Private.CoreLib.dll', 'Microsoft.AspNetCore.dll') + $nativeFiles) {
        if (Test-Path -LiteralPath (Join-Path $Directory $file)) { throw "$Name содержит встроенный runtime: $file" }
    }
    $deps = Get-Content -LiteralPath (Join-Path $Directory "$Name.deps.json") -Raw | ConvertFrom-Json -AsHashtable
    if (!$deps.libraries.ContainsKey("$Name/$($properties.Version)") -or !$deps.runtimeTarget.name.EndsWith("/$RuntimeIdentifier")) {
        throw "Версия или RID $Name не совпадает с параметрами публикации"
    }
    return $apphost
}

Push-Location $repositoryRoot
try {
    if ($PackWindows -and ($RuntimeIdentifier -ne 'win-x64' -or !$IsWindows)) {
        throw 'Упаковка -PackWindows выполняется на Windows только для win-x64'
    }
    if ($Pack -and !(($IsWindows -and $RuntimeIdentifier -eq 'win-x64') -or ($IsLinux -and $RuntimeIdentifier -eq 'linux-x64') -or
        ($IsMacOS -and $RuntimeIdentifier.StartsWith('osx-')))) { throw 'Упаковка -Pack требует нативную ОС целевой платформы' }
    # Стабильные версии библиотеки и локального инструмента должны совпадать
    [xml]$packages = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Packages.props') -Raw
    $packageVersion = $packages.SelectSingleNode('/Project/ItemGroup/PackageVersion[@Include="Velopack"]').Version
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (!$packageVersion -or $packageVersion.Contains('-') -or $manifest.tools.vpk.version -cne $packageVersion) {
        throw 'Версии стабильных Velopack и vpk должны совпадать в Directory.Packages.props и .config/dotnet-tools.json'
    }
    $propertyArguments = @("-p:RuntimeIdentifier=$RuntimeIdentifier", "-p:ReleaseChannel=$Channel")
    if ($ReleaseTag) { $propertyArguments += "-p:ReleaseTag=$ReleaseTag" }
    if ($ReleaseRoot) { $propertyArguments += "-p:GitHubReleaseRoot=$([IO.Path]::GetFullPath($ReleaseRoot, $repositoryRoot))" }
    $propertyNames = 'Version,PackageVersion,ReleaseTag,PublishDir,GitHubReleaseDir,VelopackPackId,MacOSBundleId,VelopackChannel,WindowsRuntimeDependencies'
    $evaluation = & dotnet msbuild $project -nologo -target:ValidatePublication -p:_IsPublishing=true @propertyArguments "-getProperty:$propertyNames"
    if ($LASTEXITCODE -ne 0) { throw "Проверка параметров публикации не пройдена: $($evaluation -join [Environment]::NewLine)" }
    $properties = ($evaluation -join [Environment]::NewLine | ConvertFrom-Json).Properties
    if ($ValidateOnly) { $properties | ConvertTo-Json; return }

    $outputDirectory = [IO.Path]::GetFullPath($properties.PublishDir)
    # Чистый каталог исключает включение старых весов, настроек и файлов другой версии
    if ((Test-Path -LiteralPath $outputDirectory) -and (Get-ChildItem -LiteralPath $outputDirectory -Force | Select-Object -First 1)) {
        throw "Каталог публикации не пуст: $outputDirectory. Укажи другой -ReleaseRoot или освободи каталог перед повторной публикацией"
    }
    Invoke-DotNet -Arguments @('tool', 'restore', '--tool-manifest', $manifestPath)
    Invoke-DotNet -Arguments (@('publish', $project, '-c', 'Release', '--runtime', $RuntimeIdentifier, '--self-contained', 'false') + $propertyArguments)

    $desktop = Test-PublishedComponent -Directory $outputDirectory -Name 'OpenLLMCluster.Desktop' -RequiresAspNet $false
    $agent = Test-PublishedComponent -Directory (Join-Path $outputDirectory 'Agent') -Name 'OpenLLMCluster.Agent' -RequiresAspNet $true
    $unexpected = Get-ChildItem -LiteralPath $outputDirectory -Recurse -File | Where-Object {
        $_.Name -match '(?i)(\.gguf$|\.log$|\.secrets$|\.pfx$|^profile.*\.json$|^llama[-.]|^ggml[-.]|^rpc-server)'
    }
    if ($unexpected) { throw "В публикации обнаружены пользовательские данные или движок: $($unexpected.Name -join ', ')" }

    # Метаданные не содержат персональных путей и пригодятся следующему этапу упаковки
    [ordered]@{
        PackId = $properties.VelopackPackId
        MacOSBundleId = $properties.MacOSBundleId
        Version = $properties.Version
        PackageVersion = $properties.PackageVersion
        ReleaseTag = $properties.ReleaseTag
        RuntimeIdentifier = $RuntimeIdentifier
        Channel = $properties.VelopackChannel
        VelopackVersion = $packageVersion
        SelfContained = $false
        RequiredFrameworks = @('Microsoft.NETCore.App/10.0', 'Microsoft.AspNetCore.App/10.0')
        WindowsRuntimeDependencies = $properties.WindowsRuntimeDependencies
        Desktop = $desktop
        Agent = "Agent/$agent"
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDirectory 'publication.json') -Encoding utf8NoBOM
    Write-Host "Публикация проверена: $outputDirectory"
    Write-Host "Канал для будущей упаковки: $($properties.VelopackChannel)"
    if ($PackWindows -or $Pack) {
        & (Join-Path $PSScriptRoot 'Pack-Desktop.ps1') -PublishDirectory $outputDirectory -OutputDirectory ([IO.Path]::GetFullPath($properties.GitHubReleaseDir))
    } else {
        Write-Host 'GitHub-пакеты не создавались'
    }
} catch {
    Write-Error "Публикация остановлена: $_"
    throw
} finally {
    Pop-Location
}
