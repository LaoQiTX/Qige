#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $projectRoot ('.build/logon-cleanup/' + [Guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot '安装 with spaces'
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$source = [IO.File]::ReadAllText((Join-Path $projectRoot 'installer/Qige.iss'))
$cleanup = [regex]::Match($source, '(?s)procedure RemoveKnownLogonTasks;.*?\r?\nend;').Value
if (-not $cleanup) { throw 'Installer cleanup procedure not found.' }
$id = [Guid]::NewGuid().ToString('N')
$script = @"
[Setup]
AppId=Qige-CleanupTest-$id
AppName=Qige isolated cleanup test
AppVersion=1.0
DefaultDirName=$installRoot
PrivilegesRequired=lowest
UninstallDisplayName=Qige isolated cleanup test
OutputDir=$testRoot
OutputBaseFilename=setup
CreateAppDir=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
[Files]
Source: "$testRoot\fixture.txt"; DestDir: "{app}"
[Code]
$cleanup
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then RemoveKnownLogonTasks;
end;
"@
[IO.File]::WriteAllText((Join-Path $testRoot 'fixture.txt'), 'isolated cleanup fixture')
[IO.File]::WriteAllText((Join-Path $testRoot 'cleanup.iss'), $script, [Text.UTF8Encoding]::new($true))
& (Join-Path $projectRoot '.build/tools/InnoSetup/ISCC.exe') '/Qp' (Join-Path $testRoot 'cleanup.iss')
if ($LASTEXITCODE -ne 0) { throw 'Cleanup fixture compilation failed.' }
function Run-Hidden([string]$path, [string]$log) {
    $info = [Diagnostics.ProcessStartInfo]::new($path)
    $info.UseShellExecute = $false; $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=$log")) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw 'Cleanup fixture timed out.' }
    if ($process.ExitCode -ne 0) { throw "Cleanup fixture failed: $($process.ExitCode)" }
}
$service = New-Object -ComObject Schedule.Service
$service.Connect(); $folder = $service.GetFolder('\')
$names = @('Qige-test-exact-','Qige-test-modified-','Qige-test-other-') | ForEach-Object { $_ + $id }
try {
    Run-Hidden (Join-Path $testRoot 'setup.exe') (Join-Path $testRoot 'install.log')
    for ($i = 0; $i -lt $names.Count; $i++) {
        $task = $service.NewTask(0)
        $task.RegistrationInfo.Source = 'Qige'
        $task.Principal.UserId = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $task.Principal.LogonType = 3
        $action = $task.Actions.Create(0)
        $action.Path = if ($i -eq 2) { Join-Path $testRoot 'other/Qige.exe' } else { Join-Path $installRoot 'Qige.exe' }
        $action.Arguments = '--data-dir "' + (Join-Path $installRoot 'QigeData') + '" --background'
        if ($i -eq 1) { $action.Arguments += ' --changed' }
        $null = $folder.RegisterTaskDefinition($names[$i], $task, 6, $task.Principal.UserId, $null, 3)
    }
    Run-Hidden (Join-Path $installRoot 'unins000.exe') (Join-Path $testRoot 'uninstall.log')
    $remaining = @()
    for ($i = 0; $i -lt 80; $i++) {
        $remaining = @($folder.GetTasks(1) | ForEach-Object { $_.Name })
        if ($names[0] -notin $remaining) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($names[0] -in $remaining) { throw 'Matching installed logon task was not removed.' }
    if ($names[1] -notin $remaining -or $names[2] -notin $remaining) { throw 'Modified or other-installation task was removed.' }
    [pscustomobject]@{Passed=3;MatchingRemoved=$true;ModifiedPreserved=$true;OtherInstallPreserved=$true;RealStartupChanged=$false} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'results.json') -Encoding utf8
    Write-Output "PASS installer logon cleanup: matching removed, modified and other installation preserved. Results: $testRoot"
}
finally {
    foreach ($name in $names) { try { $folder.DeleteTask($name, 0) } catch { } }
}
