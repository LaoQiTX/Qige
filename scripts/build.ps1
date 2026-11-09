#requires -Version 7.0
param(
    [ValidateSet('Build','Test','Probe','SelfTest','ShellTest','RenderTest','VisibilityProbe','StorageTest','StorageRenderTest','IconTest','ResidentTest','Demo','Run','MigrateData','LayoutTest','ManagerTest','P0Test')]
    [string]$Task = 'Build',
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ProjectEnvironment.ps1')
Invoke-ProjectEnvironment {
    param($projectRoot)
    & dotnet build (Join-Path $projectRoot 'Qige.slnx') --configuration $Configuration --disable-build-servers --nologo -v:minimal '-p:UseSharedCompilation=false'
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
    if ($Task -eq 'Build') { return }
    $tasks = if ($Task -eq 'Test') { @('SelfTest','StorageTest','LayoutTest','P0Test') } else { @($Task) }
    foreach ($item in $tasks) {
        $kind = if ($item -in @('Run','Demo')) { 'App' }
            elseif ($item -in @('SelfTest','StorageTest','StorageRenderTest','IconTest','ResidentTest','LayoutTest','ManagerTest','P0Test')) { 'Tests' }
            else { 'Diagnostics' }
        $folder = switch ($kind) { App { 'src/DesktopGroups' } Tests { 'tests/DesktopGroups.Tests' } Diagnostics { 'tools/DesktopGroups.Diagnostics' } }
        $name = if ($kind -eq 'App') { 'Qige' } else { 'DesktopGroups.' + $kind }
        $data = switch ($kind) { App { '.local/QigeData' } Tests { '.build/test-results' } Diagnostics { '.build/diagnostics' } }
        if ($item -eq 'MigrateData') { $data = '.local/QigeData' }
        $arguments = @('--data-dir', (Join-Path $projectRoot $data))
        $flags = @{
            Probe='--inspect'; SelfTest='--self-test'; ShellTest='--shell-test'; RenderTest='--render-test'
            VisibilityProbe='--visibility-probe'; StorageTest='--storage-test'; StorageRenderTest='--storage-render-test'
            LayoutTest='--layout-test'; ManagerTest='--manager-test'; P0Test='--p0-test'; IconTest='--icon-test'; ResidentTest='--resident-test'; Demo='--demo'; MigrateData='--migrate-data'
        }
        if ($flags.ContainsKey($item)) { $arguments += $flags[$item] }
        if ($item -eq 'MigrateData') { $arguments += @('--migrate-from', $projectRoot) }
        $assembly = Join-Path $projectRoot "$folder/bin/$Configuration/net10.0-windows/$name.dll"
        if ($kind -eq 'App') {
            $process = Start-Process -FilePath ([IO.Path]::ChangeExtension($assembly, '.exe')) -ArgumentList (($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' ') -WindowStyle Hidden -PassThru
            $process.WaitForExit()
            if ($process.ExitCode -ne 0) { throw "Application failed: $($process.ExitCode)" }
        }
        else {
            & dotnet $assembly @arguments
            if ($LASTEXITCODE -ne 0) { throw "$item failed: $LASTEXITCODE" }
        }
    }
}
