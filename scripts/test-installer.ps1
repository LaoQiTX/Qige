#requires -Version 7.0
param([string]$Version = '0.1.0-beta.10')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Invalid version.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $projectRoot ('.build/installer-smoke/' + [Guid]::NewGuid().ToString('N'))
$installRoot = [IO.Path]::GetFullPath((Join-Path $testRoot '安装验证 with spaces'))
if (-not $installRoot.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test install path.' }
$installer = Join-Path $projectRoot "artifacts/installers/Qige-$Version-setup-win-x64.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Installer is missing.' }
$systemTemp = [Environment]::GetEnvironmentVariable('TEMP', 'User')
if (-not $systemTemp) { $systemTemp = [IO.Path]::GetTempPath() }
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A85C0513-D51D-44C3-A547-6C21BB9AA05D}_is1'
if (Test-Path -LiteralPath $uninstallKey) { throw 'An existing Qige installation is registered; test refused.' }
if (Test-Path -LiteralPath $installRoot) { throw 'The smoke-test install directory already exists.' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$results = [Collections.Generic.List[string]]::new()
function Check([bool]$pass, [string]$label) {
    if (-not $pass) { throw $label }
    $results.Add('PASS ' + $label)
    Write-Output ('PASS ' + $label)
}
function Run-Setup([string]$name, [bool]$expectSuccess = $true) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER','/TASKS=',('/DIR=' + $installRoot),('/LOG=' + (Join-Path $testRoot ($name + '.log'))))
    $start = [Diagnostics.ProcessStartInfo]::new($installer)
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.Environment['TEMP'] = $systemTemp
    $start.Environment['TMP'] = $systemTemp
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'Installer exceeded its isolated test timeout.' }
    Check (($process.ExitCode -eq 0) -eq $expectSuccess) ("$name returns the expected exit status: $($process.ExitCode)")
}
function Run-Uninstall([string]$name) {
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG=' + (Join-Path $testRoot ($name + '.log'))))
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $installRoot 'unins000.exe'))
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.Environment['TEMP'] = $systemTemp
    $start.Environment['TMP'] = $systemTemp
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'Uninstaller exceeded its isolated test timeout.' }
    Check ($process.ExitCode -eq 0) "$name succeeds"
    $uninstallLog = Join-Path $testRoot ($name + '.log')
    $finished = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ((Test-Path -LiteralPath $uninstallLog) -and
            (Get-Content -LiteralPath $uninstallLog -Tail 4 | Select-String -SimpleMatch 'Log closed.')) { $finished = $true; break }
        Start-Sleep -Milliseconds 250
    }
    Check $finished "$name finishes its asynchronous uninstall callbacks"
    for ($attempt = 0; $attempt -lt 20 -and (Test-Path -LiteralPath $uninstallKey); $attempt++) { Start-Sleep -Milliseconds 250 }
    Check (-not (Test-Path -LiteralPath $uninstallKey)) "$name removes only the test uninstall registration"
}
Run-Setup 'install'
Check (Test-Path -LiteralPath (Join-Path $installRoot 'Qige.exe')) 'installation creates Qige.exe in the selected Unicode path'
Check (Test-Path -LiteralPath (Join-Path $installRoot 'QigeData') -PathType Container) 'installation creates the visible QigeData directory'
$sourceExe = Join-Path $projectRoot "artifacts/releases/Qige-$Version-win-x64/Qige.exe"
Check ((Get-FileHash -LiteralPath $sourceExe).Hash -eq (Get-FileHash -LiteralPath (Join-Path $installRoot 'Qige.exe')).Hash) 'installed executable matches the self-contained package'
$shortcut = Join-Path $installRoot 'QigeData/storage/sample/current/验证.url'
$config = Join-Path $installRoot 'QigeData/config/settings.json'
New-Item -ItemType Directory -Path (Split-Path $shortcut),(Split-Path $config),(Join-Path $installRoot 'QigeData/recovery') -Force | Out-Null
Set-Content -LiteralPath $shortcut -Value "[InternetShortcut]`r`nURL=https://example.com/installer-fixture" -Encoding utf8
Set-Content -LiteralPath $config -Value '{"InstallerFixture":true}' -Encoding utf8
$shortcutHash = (Get-FileHash -LiteralPath $shortcut).Hash
$configHash = (Get-FileHash -LiteralPath $config).Hash
Set-Content -LiteralPath (Join-Path $installRoot 'keep-user-file.txt') -Value 'This user-owned file must be retained.'
Set-Content -LiteralPath (Join-Path $installRoot 'DesktopGroups.exe') -Value 'obsolete program fixture'
Set-Content -LiteralPath (Join-Path $installRoot 'DesktopGroups.dll') -Value 'obsolete program fixture'
$lockPath = Join-Path $installRoot 'QigeData/recovery/session.lock'
$fileLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { Run-Setup 'busy-data' $false } finally { $fileLock.Dispose() }
Check ((Get-FileHash -LiteralPath $shortcut).Hash -eq $shortcutHash) 'busy-data rejection preserves stored shortcuts'
Run-Setup 'upgrade'
Check (-not (Test-Path -LiteralPath (Join-Path $installRoot 'DesktopGroups.exe'))) 'upgrade removes the known obsolete executable'
Check (-not (Test-Path -LiteralPath (Join-Path $installRoot 'DesktopGroups.dll'))) 'upgrade removes the known obsolete assembly'
Check ((Get-FileHash -LiteralPath $shortcut).Hash -eq $shortcutHash -and (Get-FileHash -LiteralPath $config).Hash -eq $configHash) 'upgrade preserves stored shortcuts and configuration byte for byte'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$knownName = 'Qige-InstallerSmoke-' + [Guid]::NewGuid().ToString('N')
$changedName = 'Qige-InstallerSmoke-' + [Guid]::NewGuid().ToString('N')
$expectedCommand = '"' + (Join-Path $installRoot 'Qige.exe') + '" --data-dir "' + (Join-Path $installRoot 'QigeData') + '" --background'
New-ItemProperty -LiteralPath $runKeyPath -Name $knownName -Value $expectedCommand -PropertyType String | Out-Null
New-ItemProperty -LiteralPath $runKeyPath -Name $changedName -Value 'manually modified fixture' -PropertyType String | Out-Null
try {
    Run-Uninstall 'uninstall'
    Check (-not (Test-Path -LiteralPath (Join-Path $installRoot 'Qige.exe'))) 'uninstall removes the program executable'
    Check ((Get-FileHash -LiteralPath $shortcut).Hash -eq $shortcutHash -and (Get-FileHash -LiteralPath $config).Hash -eq $configHash) 'uninstall preserves QigeData byte for byte'
    Check (Test-Path -LiteralPath (Join-Path $installRoot 'keep-user-file.txt')) 'uninstall retains unrelated user files in the install directory'
    $runKey = Get-Item -LiteralPath $runKeyPath
    Check ($null -eq $runKey.GetValue($knownName)) 'uninstall removes the exact installed startup command'
    Check ($runKey.GetValue($changedName) -eq 'manually modified fixture') 'uninstall preserves modified startup commands'
    Run-Setup 'reinstall'
    Check ((Get-FileHash -LiteralPath $shortcut).Hash -eq $shortcutHash -and (Get-FileHash -LiteralPath $config).Hash -eq $configHash) 'reinstall retains the original QigeData'
    Run-Uninstall 'final-uninstall'
}
finally {
    Remove-ItemProperty -LiteralPath $runKeyPath -Name $knownName -ErrorAction SilentlyContinue
    Remove-ItemProperty -LiteralPath $runKeyPath -Name $changedName -ErrorAction SilentlyContinue
}
[pscustomobject]@{Passed=$results.Count;Results=@($results);TestDirectory=$testRoot;Installer=$installer;TemporaryDirectory=$systemTemp;ActualDesktopFilesMoved=$false;UserDataPreserved=$true} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testRoot 'results.json') -Encoding utf8
