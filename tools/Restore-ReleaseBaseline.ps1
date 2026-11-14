#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')][string]$RuntimeIdentifier,
    [ValidateSet('stable', 'beta')][string]$Channel = 'stable',
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$ApiUrl = 'https://api.github.com'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Неверное имя GitHub-репозитория' }
$api = [uri]$ApiUrl
if ($api.Scheme -ne 'https' -and !($api.IsLoopback -and $api.Scheme -eq 'http')) { throw 'GitHub API требует HTTPS' }
$headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'OpenLLMCluster-Release' }
if ($env:GH_TOKEN) { $headers.Authorization = "Bearer $env:GH_TOKEN" }
$indexName = "releases.$RuntimeIdentifier-$Channel.json"
Push-Location $repositoryRoot
try {
    $previous = $null
    for ($page = 1; ; $page++) {
        $releases = @(Invoke-RestMethod -Uri ($ApiUrl.TrimEnd('/') + "/repos/$Repository/releases?per_page=100&page=$page") -Headers $headers -TimeoutSec 30)
        $previous = $releases | Where-Object { !$_.draft -and ($Channel -eq 'beta' -or !$_.prerelease) -and $indexName -cin $_.assets.name } |
            Select-Object -First 1
        if ($previous -or $releases.Count -lt 100) { break }
    }
    if (!$previous) { Write-Host "Первый выпуск канала $RuntimeIdentifier-${Channel}: предыдущего пакета нет"; return }
    Write-Host "Восстановление опубликованного выпуска $($previous.tag_name), канал $RuntimeIdentifier-$Channel"
    & dotnet tool restore --tool-manifest (Join-Path $repositoryRoot '.config/dotnet-tools.json') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось восстановить vpk' }
    $arguments = @('vpk', 'download', 'github', '--repoUrl', "https://github.com/$Repository", '--channel', "$RuntimeIdentifier-$Channel",
        '--outputDir', ([IO.Path]::GetFullPath($PackageDirectory)))
    if ($Channel -eq 'beta') { $arguments += '--pre' }
    # Переменная VPK_TOKEN читается самим vpk, секрет не передаётся через командную строку
    $originalToken = $env:VPK_TOKEN
    try {
        $env:VPK_TOKEN = $env:GH_TOKEN
        & dotnet @arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось загрузить опубликованный пакет; сборка остановлена' }
    } finally { $env:VPK_TOKEN = $originalToken }
} catch {
    Write-Error "Восстановление предыдущего выпуска не выполнено: $_"
    throw
} finally { Pop-Location }
