#requires -Version 7.0
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$Executable)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Executable) { $Executable = Join-Path $projectRoot "src/DesktopGroups/bin/$Configuration/net10.0-windows/Qige.exe" }
$testRoot = Join-Path $projectRoot ('.build/startup-smoke/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory((Join-Path $testRoot 'config')) | Out-Null
# No memberships: the real entry point may inspect the desktop, never collect it.
[IO.File]::WriteAllText((Join-Path $testRoot 'config/drawers.json'), '{"Version":1,"Drawers":[]}')
$results = [Collections.Generic.List[string]]::new()
function Check([bool]$pass, [string]$label) {
    if (-not $pass) { throw $label }
    $results.Add('PASS ' + $label)
    Write-Output ('PASS ' + $label)
}
function Launch([bool]$background) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.ArgumentList.Add('--data-dir'); $info.ArgumentList.Add($testRoot)
    if ($background) { $info.ArgumentList.Add('--background') }
    return [Diagnostics.Process]::Start($info)
}
$resident = $null; $duplicate = $null; $foreground = $null
try {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $resident = Launch $true
    for ($i = 0; $i -lt 100; $i++) {
        if ($resident.HasExited) { throw 'Background launch exited unexpectedly.' }
        $resident.Refresh()
        if ($resident.MainWindowHandle -ne 0) { throw 'Background startup exposed a manager window.' }
        if (Test-Path -LiteralPath (Join-Path $testRoot 'logs/desktop-lifetime.json')) { break }
        Start-Sleep -Milliseconds 100
    }
    Check ($resident.MainWindowHandle -eq 0) 'background startup has no visible manager window'
    Check (Test-Path -LiteralPath (Join-Path $testRoot 'logs/desktop-lifetime.json')) 'actual executable reaches resident initialization'
    $readyMs = $clock.Elapsed.TotalMilliseconds
    $duplicate = Launch $true
    Check ($duplicate.WaitForExit(5000) -and $duplicate.ExitCode -eq 0) 'duplicate background launch exits without opening the manager'
    $resident.Refresh()
    Check ($resident.MainWindowHandle -eq 0) 'the original resident stays hidden after duplicate background launch'
    $foreground = Launch $false
    Check ($foreground.WaitForExit(5000) -and $foreground.ExitCode -eq 0) 'one foreground launch signals the resident instance'
    for ($i = 0; $i -lt 50; $i++) {
        $resident.Refresh()
        if ($resident.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    }
    Check ($resident.MainWindowHandle -ne 0) 'the resident manager opens on the first foreground request'
    [pscustomobject]@{Passed=$results.Count;Results=@($results);Executable=$Executable;ResidentReadyMs=$readyMs;Scope='Warm process startup, zero drawers; excludes Windows logon and cold disk costs';RealDesktopFilesMoved=$false} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testRoot 'results.json') -Encoding utf8
    Write-Output "Results: $testRoot"
}
finally {
    # Only these isolated processes with an empty catalog are terminated.
    foreach ($process in @($foreground,$duplicate,$resident)) {
        if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        if ($process) { $process.Dispose() }
    }
}
