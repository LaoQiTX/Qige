#requires -Version 7.0
function Invoke-ProjectEnvironment {
    param([Parameter(Mandatory)][scriptblock]$Action)
    $projectRoot = Split-Path -Parent $PSScriptRoot
    $values = @{
        DOTNET_CLI_HOME = (Join-Path $projectRoot '.build/dotnet-home')
        NUGET_PACKAGES = (Join-Path $projectRoot '.build/nuget/packages')
        NUGET_HTTP_CACHE_PATH = (Join-Path $projectRoot '.build/nuget/http-cache')
        NUGET_PLUGINS_CACHE_PATH = (Join-Path $projectRoot '.build/nuget/plugins-cache')
        NUGET_SCRATCH = (Join-Path $projectRoot '.build/nuget/scratch')
        TEMP = (Join-Path $projectRoot '.build/temp')
        TMP = (Join-Path $projectRoot '.build/temp')
        MSBUILDDEBUGPATH = (Join-Path $projectRoot '.build/msbuild-logs')
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
        DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
        MSBUILDDISABLENODEREUSE = '1'
    }
    $previous = @{}
    try {
        foreach ($entry in $values.GetEnumerator()) {
            $previous[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
            if ($entry.Value.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase)) {
                [IO.Directory]::CreateDirectory($entry.Value) | Out-Null
            }
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
        Push-Location -LiteralPath $projectRoot
        try { & $Action $projectRoot }
        finally { Pop-Location }
    }
    finally {
        foreach ($entry in $previous.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
}
