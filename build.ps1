param(
    [ValidateSet('Build', 'Probe', 'SelfTest', 'ShellTest', 'RenderTest', 'VisibilityProbe', 'StorageTest', 'StorageRenderTest', 'IconTest', 'ResidentTest', 'Demo', 'Run')]
    [string]$Task = 'Build',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot 'src\DesktopGroups\DesktopGroups.csproj'
$processEnvironment = @{
    DOTNET_CLI_HOME = (Join-Path $projectRoot '.build\dotnet-home')
    NUGET_PACKAGES = (Join-Path $projectRoot '.build\nuget\packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $projectRoot '.build\nuget\http-cache')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $projectRoot '.build\nuget\plugins-cache')
    NUGET_SCRATCH = (Join-Path $projectRoot '.build\nuget\scratch')
    TEMP = (Join-Path $projectRoot '.build\temp')
    TMP = (Join-Path $projectRoot '.build\temp')
    MSBUILDDEBUGPATH = (Join-Path $projectRoot '.build\msbuild-logs')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    MSBUILDDISABLENODEREUSE = '1'
}
$previousEnvironment = @{}
try {
    foreach ($entry in $processEnvironment.GetEnumerator()) {
        $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        if ($entry.Value.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase)) {
            [IO.Directory]::CreateDirectory($entry.Value) | Out-Null
        }
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    Push-Location -LiteralPath $projectRoot
    try {
        & dotnet build $projectFile --configuration $Configuration --disable-build-servers --nologo -v:minimal '-p:UseSharedCompilation=false'
        if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
        if ($Task -ne 'Build') {
            $applicationPath = Join-Path $projectRoot "src\DesktopGroups\bin\$Configuration\net10.0-windows\DesktopGroups.exe"
            $applicationArguments = @('--root', $projectRoot)
            if ($Task -eq 'Probe') { $applicationArguments += '--inspect' }
            if ($Task -eq 'SelfTest') { $applicationArguments += '--self-test' }
            if ($Task -eq 'ShellTest') { $applicationArguments += '--shell-test' }
            if ($Task -eq 'RenderTest') { $applicationArguments += '--render-test' }
            if ($Task -eq 'VisibilityProbe') { $applicationArguments += '--visibility-probe' }
            if ($Task -eq 'StorageTest') { $applicationArguments += '--storage-test' }
            if ($Task -eq 'StorageRenderTest') { $applicationArguments += '--storage-render-test' }
            if ($Task -eq 'IconTest') { $applicationArguments += '--icon-test' }
            if ($Task -eq 'ResidentTest') { $applicationArguments += '--resident-test' }
            if ($Task -eq 'Demo') { $applicationArguments += '--demo' }
            if ($Task -in @('Run', 'Demo')) {
                # Wait explicitly for the GUI host; the app remains resident
                # when its controls are closed. Console checks use dotnet so
                # their exit code and redirected output remain observable.
                $applicationProcess = Start-Process -FilePath $applicationPath -ArgumentList (($applicationArguments | ForEach-Object { '"' + $_ + '"' }) -join ' ') -WindowStyle Hidden -PassThru
                $applicationProcess.WaitForExit()
                $global:LASTEXITCODE = $applicationProcess.ExitCode
            }
            else { & dotnet ([IO.Path]::ChangeExtension($applicationPath, '.dll')) @applicationArguments }
            if ($LASTEXITCODE -ne 0) { throw "Application failed with exit code $LASTEXITCODE" }
        }
    }
    finally { Pop-Location }
}
finally {
    foreach ($entry in $previousEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}
