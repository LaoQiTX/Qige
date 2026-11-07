param(
    [string]$Version = '0.1.0-beta.1',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Invalid version.' }
$releaseRoot = $PSScriptRoot
$packageName = 'Qige-' + $Version + '-win-x64'
if ($FrameworkDependent) { $packageName += '-framework-dependent' }
$packageFolder = Join-Path $releaseRoot ('artifacts\' + $packageName)
if (Test-Path -LiteralPath $packageFolder) { throw ('Package folder already exists: ' + $packageFolder) }
$publishFolder = Join-Path $releaseRoot ('.build\publish\' + [Guid]::NewGuid().ToString('N'))
$releaseEnvironment = @{
    DOTNET_CLI_HOME = (Join-Path $releaseRoot '.build\dotnet-home')
    NUGET_PACKAGES = (Join-Path $releaseRoot '.build\nuget\packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $releaseRoot '.build\nuget\http-cache')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $releaseRoot '.build\nuget\plugins-cache')
    NUGET_SCRATCH = (Join-Path $releaseRoot '.build\nuget\scratch')
    TEMP = (Join-Path $releaseRoot '.build\temp')
    TMP = (Join-Path $releaseRoot '.build\temp')
    MSBUILDDEBUGPATH = (Join-Path $releaseRoot '.build\msbuild-logs')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    MSBUILDDISABLENODEREUSE = '1'
}
$previousReleaseEnvironment = @{}
try {
    foreach ($entry in $releaseEnvironment.GetEnumerator()) {
        $previousReleaseEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        if ($entry.Value.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
            [IO.Directory]::CreateDirectory($entry.Value) | Out-Null
        }
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    $publishArguments = @(
        'publish', (Join-Path $releaseRoot 'src\DesktopGroups\DesktopGroups.csproj'),
        '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained',
        (-not $FrameworkDependent).ToString().ToLowerInvariant(),
        '--output', $publishFolder, '--disable-build-servers', '--nologo',
        '-p:UseSharedCompilation=false', ('-p:Version=' + $Version),
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=embedded'
    )
    if (-not $FrameworkDependent) { $publishArguments += @('--source', 'https://api.nuget.org/v3/index.json') }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw ('Publish failed: ' + $LASTEXITCODE) }

    [IO.Directory]::CreateDirectory($packageFolder) | Out-Null
    # Only clean publish output and explicitly named public documents are copied.
    # Runtime Data/Storage/TestData, credentials and development caches never enter the package.
    foreach ($item in Get-ChildItem -LiteralPath $publishFolder -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $packageFolder -Recurse
    }
    foreach ($name in @('desktopgroups.root.json', 'BETA使用说明.md', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $releaseRoot $name) -Destination $packageFolder
    }
    $utf8 = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $packageFolder '打开样例.cmd'), "@echo off`r`nstart `"`" `"%~dp0DesktopGroups.exe`" --demo`r`n", [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $packageFolder 'package.json'), (@{
        name = 'Qige'; displayName = '栖格'; version = $Version; platform = 'win-x64'
        selfContained = (-not $FrameworkDependent); entry = 'DesktopGroups.exe'
    } | ConvertTo-Json), $utf8)
    $badFolders = @('Data','Storage','TestData','.git','.build')
    foreach ($folder in $badFolders) {
        if (Test-Path -LiteralPath (Join-Path $packageFolder $folder)) { throw ('Unexpected private folder in package: ' + $folder) }
    }
    $badFiles = @(Get-ChildItem -LiteralPath $packageFolder -Recurse -File | Where-Object { $_.Extension -in @('.lnk','.url','.pfx','.p12','.key') })
    if ($badFiles.Count -ne 0) { throw 'Unexpected user shortcuts or credential files in package.' }
    $zip = $packageFolder + '.zip'
    Compress-Archive -LiteralPath $packageFolder -DestinationPath $zip
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($zip + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($zip) + "`n", $utf8)
    Write-Output ('Package: ' + $zip)
    Write-Output ('SHA256: ' + $hash)
}
finally {
    foreach ($entry in $previousReleaseEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}
