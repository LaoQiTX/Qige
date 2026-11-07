#requires -Version 7.0
param(
    [string]$Version = '0.1.0-beta.2',
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Invalid version.' }
. (Join-Path $PSScriptRoot 'ProjectEnvironment.ps1')
Invoke-ProjectEnvironment {
    param($projectRoot)
    $packageName = 'Qige-' + $Version + '-win-x64'
    if ($FrameworkDependent) { $packageName += '-framework-dependent' }
    $packageFolder = Join-Path $projectRoot ('artifacts/releases/' + $packageName)
    $zip = $packageFolder + '.zip'
    if ((Test-Path -LiteralPath $packageFolder) -or (Test-Path -LiteralPath $zip)) { throw "Package already exists: $packageFolder" }
    $publishFolder = Join-Path $projectRoot ('.build/publish/' + [Guid]::NewGuid().ToString('N'))
    try {
        $arguments = @(
            'publish', (Join-Path $projectRoot 'src/DesktopGroups/DesktopGroups.csproj'),
            '--configuration','Release','--runtime','win-x64','--self-contained',
            (-not $FrameworkDependent).ToString().ToLowerInvariant(),
            '--output',$publishFolder,'--disable-build-servers','--nologo',
            '-p:UseSharedCompilation=false',('-p:Version=' + $Version),
            '-p:PublishSingleFile=false','-p:PublishTrimmed=false','-p:DebugType=embedded'
        )
        if (-not $FrameworkDependent) { $arguments += @('--source','https://api.nuget.org/v3/index.json') }
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }
        [IO.Directory]::CreateDirectory($packageFolder) | Out-Null
        foreach ($item in Get-ChildItem -LiteralPath $publishFolder -Force) {
            Copy-Item -LiteralPath $item.FullName -Destination $packageFolder -Recurse
        }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/user-guide.md') -Destination (Join-Path $packageFolder '使用说明.md')
        Copy-Item -LiteralPath (Join-Path $projectRoot 'CHANGELOG.md') -Destination $packageFolder
        $launcher = @('@echo off','start "" "%~dp0DesktopGroups.exe" --demo','') -join [Environment]::NewLine
        [IO.File]::WriteAllText((Join-Path $packageFolder '打开样例.cmd'), $launcher, [Text.Encoding]::ASCII)
        [IO.File]::WriteAllText((Join-Path $packageFolder 'package.json'), (@{
            name='Qige'; displayName='栖格'; version=$Version; platform='win-x64'
            selfContained=(-not $FrameworkDependent); entry='DesktopGroups.exe'; dataDirectory='QigeData'
        } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        $badFolders = @('QigeData','Data','Storage','TestData','.git','.build','.local','tests','tools')
        foreach ($folder in $badFolders) {
            if (Test-Path -LiteralPath (Join-Path $packageFolder $folder)) { throw "Private folder in package: $folder" }
        }
        $badFiles = @(Get-ChildItem -LiteralPath $packageFolder -Recurse -File | Where-Object {
            $_.Extension -in @('.lnk','.url','.pfx','.p12','.key') -or $_.Name -match 'DesktopGroups\.(Tests|Diagnostics)' -or $_.Name -eq 'desktopgroups.root.json'
        })
        if ($badFiles.Count -ne 0) { throw 'Private or development-only files found in package.' }
        Compress-Archive -LiteralPath $packageFolder -DestinationPath $zip
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText($zip + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($zip) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
        Write-Output "Package: $zip"
        Write-Output "SHA256: $hash"
    }
    finally {
        # Only our own generated staging directory may be removed.
        $allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot '.build/publish')) + [IO.Path]::DirectorySeparatorChar
        $resolved = [IO.Path]::GetFullPath($publishFolder)
        if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging path.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
