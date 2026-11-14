#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseTag,
    [ValidateSet('stable', 'beta')][string]$Channel = 'stable',
    [switch]$RequireTagMatch,
    [string]$GitHubOutputPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    [xml]$properties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
    $version = $properties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$' -or $ReleaseTag -cne "v$version") {
        throw 'Тег должен точно соответствовать v<Version> из Directory.Build.props'
    }
    if ($Channel -eq 'stable' -and $version.Contains('-')) { throw 'Предварительная версия недопустима для stable' }
    $commit = & git rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Не определён коммит исходников' }
    if ($RequireTagMatch) {
        $tagCommit = & git rev-parse --verify "refs/tags/$ReleaseTag^{commit}"
        if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $commit) { throw 'HEAD должен соответствовать существующему тегу релиза' }
        if (& git status --porcelain --untracked-files=normal) { throw 'Подготовка GitHub-релиза требует чистого checkout тега' }
    }
    if ($GitHubOutputPath) {
        @("version=$version", "tag=$ReleaseTag", "channel=$Channel", "commit=$commit") |
            Add-Content -LiteralPath $GitHubOutputPath -Encoding utf8NoBOM
    }
    [ordered]@{ Version = $version; Tag = $ReleaseTag; Channel = $Channel; Commit = $commit } | ConvertTo-Json
} catch {
    Write-Error "План релиза отклонён: $_"
    throw
} finally { Pop-Location }
