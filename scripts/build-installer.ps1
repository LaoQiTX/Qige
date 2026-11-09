#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Version,
    [string]$InnoSetupCompiler
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Invalid version.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $package 'package.json') -Raw | ConvertFrom-Json
if ($manifest.entry -ne 'Qige.exe' -or -not $manifest.selfContained -or $manifest.version -ne $Version) {
    throw 'Installer requires a matching self-contained Qige package.'
}
if (-not (Test-Path -LiteralPath (Join-Path $package 'Qige.exe') -PathType Leaf)) { throw 'Qige.exe is missing.' }
$privateNames = @('QigeData','Data','Storage','TestData','.git','.build','.local','tests','tools')
$unsafe = @(Get-ChildItem -LiteralPath $package -Recurse -Force | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    ($_.PSIsContainer -and $_.Name -in $privateNames) -or
    (-not $_.PSIsContainer -and ($_.Extension -in @('.lnk','.url','.pfx','.p12','.key') -or $_.Name -match 'DesktopGroups\.(Tests|Diagnostics)'))
})
if ($unsafe.Count) { throw 'Private data or development-only files found in installer source.' }
if (-not $InnoSetupCompiler) {
    $candidates = @(
        (Join-Path $projectRoot '.build/tools/InnoSetup/ISCC.exe'),
        'C:/Program Files/Inno Setup 7/ISCC.exe',
        'C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
    )
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    $InnoSetupCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $InnoSetupCompiler) { throw 'Inno Setup is required. Pass -InnoSetupCompiler <absolute path to ISCC.exe>.' }
$compiler = (Resolve-Path -LiteralPath $InnoSetupCompiler).Path
$output = Join-Path $projectRoot 'artifacts/installers'
$installer = Join-Path $output "Qige-$Version-setup-win-x64.exe"
if (Test-Path -LiteralPath $installer) { throw "Installer already exists: $installer" }
$numeric = $Version.Split('-')[0] + '.0'
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.build/installer-staging'))
$staging = Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $staging,$output -Force | Out-Null
    & $compiler '/Qp' ("/DAppVersion=$Version") ("/DNumericVersion=$numeric") ("/DPackageDir=$package") ("/DInstallerOutputDir=$staging") (Join-Path $projectRoot 'installer/Qige.iss')
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed: $LASTEXITCODE" }
    $stagedInstaller = Join-Path $staging ([IO.Path]::GetFileName($installer))
    # Keep the staging file's ACL on this same-volume move. Building directly in
    # artifacts can inherit workspace execution restrictions and break TEMP access.
    Move-Item -LiteralPath $stagedInstaller -Destination $installer
}
finally {
    $resolved = [IO.Path]::GetFullPath($staging)
    if (-not $resolved.StartsWith($stagingRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unsafe installer staging path.'
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($installer + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($installer) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Installer: $installer"
Write-Output "SHA256: $hash"
