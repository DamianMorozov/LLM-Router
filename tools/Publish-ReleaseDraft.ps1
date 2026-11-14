#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$ReleaseTag,
    [ValidateSet('stable', 'beta')][string]$Channel = 'stable',
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$Commit,
    [string]$ApiUrl = 'https://api.github.com',
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$inputRoot = [IO.Path]::GetFullPath($InputDirectory)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$client = $null

# Выполняет запрос к GitHub; токен берётся только из окружения и не включается в аргументы или журнал
function Invoke-ReleaseApi {
    param([string]$Method, [string]$Url, [object]$Body)
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $Url)
    if ($null -ne $Body) {
        $request.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 12 -Compress), [Text.Encoding]::UTF8, 'application/json')
    }
    try {
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            if (!$response.IsSuccessStatusCode) { throw "GitHub API: $Method вернул HTTP $([int]$response.StatusCode)" }
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($text) { return ConvertFrom-Json -InputObject $text }
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}

# Сверяет каждый файл индекса с его размером и SHA256 до создания или изменения черновика
function Test-IndexedFile {
    param([string]$Directory, [object]$Asset, [string]$ExpectedName)
    if ($Asset.FileName -cne $ExpectedName) { throw 'Индекс ссылается на неподходящий пакет' }
    $path = Join-Path $Directory $ExpectedName
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $Asset.Size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $Asset.SHA256) { throw "Пакет не соответствует индексу: $ExpectedName" }
}

try {
    $plan = & (Join-Path $PSScriptRoot 'Get-ReleasePlan.ps1') -ReleaseTag $ReleaseTag -Channel $Channel | ConvertFrom-Json
    $version = $plan.Version
    if (!$Commit) { $Commit = $plan.Commit }
    if ($Commit -notmatch '^[0-9a-f]{40}$' -or $Commit -cne $plan.Commit) { throw 'Коммит релиза не соответствует checkout' }
    if (Test-Path -LiteralPath $outputRoot) { throw 'Каталог общего комплекта уже существует; выбери новый каталог' }
    [xml]$publication = Get-Content -LiteralPath (Join-Path $repositoryRoot 'build/Publication.props') -Raw
    $packId = $publication.SelectSingleNode('/Project/PropertyGroup/VelopackPackId').InnerText
    $selected = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($rid in @('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')) {
        $directory = Join-Path $inputRoot "packages-$rid"
        $metadata = Get-Content -LiteralPath (Join-Path $directory 'publish/publication.json') -Raw | ConvertFrom-Json
        $channelName = "$rid-$Channel"
        if ($metadata.PackId -cne $packId -or $metadata.Version -cne $version -or $metadata.ReleaseTag -cne $ReleaseTag -or
            $metadata.RuntimeIdentifier -cne $rid -or $metadata.Channel -cne $channelName -or $metadata.SelfContained -cne $false) {
            throw "Метаданные $rid не соответствуют плану релиза"
        }
        $fullName = "$packId-$version-$channelName-full.nupkg"
        $portable = if ($rid -eq 'linux-x64') { "$packId-$channelName.AppImage" } else { "$packId-$channelName-Portable.zip" }
        $required = @($fullName, $portable, "releases.$channelName.json", "assets.$channelName.json", "RELEASES-$channelName")
        if ($rid -eq 'win-x64') { $required += "$packId-$channelName-Setup.exe" }
        foreach ($file in $required) {
            $path = Join-Path $directory $file
            if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) { throw "Отсутствует файл $rid/$file" }
            $selected.Add((Get-Item -LiteralPath $path))
        }
        $feed = Get-Content -LiteralPath (Join-Path $directory "releases.$channelName.json") -Raw | ConvertFrom-Json
        $assets = @($feed.Assets)
        $target = @($assets | Where-Object { $_.Version -ceq $version -and $_.Type -ceq 'Full' -and $_.PackageId -ceq $packId })
        if ($target.Count -ne 1) { throw "В индексе $rid нет единственного full-пакета этой версии" }
        Test-IndexedFile -Directory $directory -Asset $target[0] -ExpectedName $fullName
        # Восстановленные пакеты прежнего выпуска остаются в индексе и тоже включаются в общий комплект
        $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in $required + @('SHA256SUMS')) { [void]$allowed.Add($file) }
        foreach ($asset in $assets) {
            $oldVersion = [string]$asset.Version
            if ($asset.PackageId -cne $packId -or $asset.Type -cne 'Full' -or $oldVersion -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
                throw "Неверное содержимое индекса $rid"
            }
            $name = "$packId-$oldVersion-$channelName-full.nupkg"
            Test-IndexedFile -Directory $directory -Asset $asset -ExpectedName $name
            if ($allowed.Add($name)) { $selected.Add((Get-Item -LiteralPath (Join-Path $directory $name))) }
        }
        foreach ($file in Get-ChildItem -LiteralPath $directory -File) {
            if (!$allowed.Contains($file.Name)) { throw "В комплекте $rid посторонний файл: $($file.Name)" }
        }
        $verified = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($line in Get-Content -LiteralPath (Join-Path $directory 'SHA256SUMS')) {
            if ($line -notmatch '^([0-9a-fA-F]{64})  ([^/\\]+)$') { throw "Неверная строка SHA256SUMS $rid" }
            $hash = $Matches[1]
            $name = $Matches[2]
            if (!$allowed.Contains($name) -or !$verified.Add($name) -or $name -eq 'SHA256SUMS' -or
                (Get-FileHash -LiteralPath (Join-Path $directory $name) -Algorithm SHA256).Hash -ine $hash) {
                throw "Ошибка контрольной суммы $rid/$name"
            }
        }
        foreach ($name in $allowed) {
            if ($name -ne 'SHA256SUMS' -and !$verified.Contains($name)) { throw "Нет контрольной суммы $rid/$name" }
        }
    }
    if (($selected | Group-Object Name | Where-Object Count -gt 1)) { throw 'В разных платформах совпали имена файлов' }
    # Копирование начинается только после успешной проверки всех четырёх платформ
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
    foreach ($file in $selected) { Copy-Item -LiteralPath $file.FullName -Destination $outputRoot }
    $sums = $selected | Sort-Object Name | ForEach-Object {
        "$((Get-FileHash -LiteralPath (Join-Path $outputRoot $_.Name) -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
    }
    $sums | Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS') -Encoding utf8NoBOM
    $result = [ordered]@{ Tag = $ReleaseTag; Version = $version; Commit = $Commit; Channel = $Channel; Draft = $true; Files = $selected.Count + 1 }
    if ($ValidateOnly) { $result | ConvertTo-Json; return }
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or !$env:GH_TOKEN) { throw 'Нужны Repository и GH_TOKEN для создания черновика' }
    $api = [uri]$ApiUrl
    if ($api.Scheme -ne 'https' -and !($api.IsLoopback -and $api.Scheme -eq 'http')) { throw 'GitHub API требует HTTPS' }
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(15)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('OpenLLMCluster-Release')
    $client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $env:GH_TOKEN)
    $client.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')
    $apiRoot = $ApiUrl.TrimEnd('/') + "/repos/$Repository"
    $existing = $null
    for ($page = 1; ; $page++) {
        $releases = @(Invoke-ReleaseApi -Method GET -Url "$apiRoot/releases?per_page=100&page=$page")
        $existing = $releases | Where-Object tag_name -CEQ $ReleaseTag | Select-Object -First 1
        if ($existing -or $releases.Count -lt 100) { break }
    }
    if ($existing -and !$existing.draft) { throw 'Релиз уже опубликован; изменение его файлов запрещено' }
    $guideUrl = "https://github.com/$Repository/blob/$Commit/README-DEV.md"
    $notes = "Framework-dependent: .NET Runtime 10 + ASP.NET Core Runtime 10.`n`nUnsigned Windows and preview macOS packages. " +
        "[Installation, update instructions and platform limitations]($guideUrl)."
    $body = @{ tag_name = $ReleaseTag; target_commitish = $Commit; name = "Open LLM Cluster $ReleaseTag";
        body = $notes; draft = $true; prerelease = ($Channel -eq 'beta'); make_latest = 'false' }
    if ($existing) {
        if ($existing.target_commitish -cne $Commit) { throw 'Существующий черновик относится к другому коммиту' }
        $release = Invoke-ReleaseApi -Method PATCH -Url "$apiRoot/releases/$($existing.id)" -Body $body
        $remoteAssets = @(Invoke-ReleaseApi -Method GET -Url "$apiRoot/releases/$($existing.id)/assets?per_page=100")
        foreach ($asset in $remoteAssets) { Invoke-ReleaseApi -Method DELETE -Url "$apiRoot/releases/assets/$($asset.id)" }
    } else {
        $release = Invoke-ReleaseApi -Method POST -Url "$apiRoot/releases" -Body $body
    }
    if (!$release.draft) { throw 'GitHub не подтвердил состояние черновика' }
    $uploadRoot = ([string]$release.upload_url -split '\{')[0]
    $uploadUri = [uri]$uploadRoot
    if (($api.IsLoopback -and (!$uploadUri.IsLoopback -or $uploadUri.Authority -ne $api.Authority)) -or
        (!$api.IsLoopback -and ($uploadUri.Scheme -ne 'https' -or $uploadUri.Host -ne 'uploads.github.com'))) {
        throw 'GitHub вернул неподходящий адрес загрузки'
    }
    foreach ($file in Get-ChildItem -LiteralPath $outputRoot -File | Sort-Object Name) {
        $stream = [IO.File]::OpenRead($file.FullName)
        $content = [Net.Http.StreamContent]::new($stream)
        $content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
        try {
            $response = $client.PostAsync($uploadRoot + '?name=' + [uri]::EscapeDataString($file.Name), $content).GetAwaiter().GetResult()
            try {
                if (!$response.IsSuccessStatusCode) { throw "Загрузка $($file.Name): HTTP $([int]$response.StatusCode)" }
            } finally { $response.Dispose() }
        } finally { $content.Dispose(); $stream.Dispose() }
    }
    $result['Url'] = $release.html_url
    if (!$api.IsLoopback -and $env:GITHUB_STEP_SUMMARY) {
        "[$ReleaseTag draft]($($release.html_url)) — $($result.Files) files; publish manually after review." |
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8NoBOM
    }
    $result | ConvertTo-Json
} catch {
    Write-Error "Черновик релиза не подготовлен: $_"
    throw
} finally { if ($client) { $client.Dispose() } }
