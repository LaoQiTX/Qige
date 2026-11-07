#requires -Version 7.0
# Stable convenience entry point. Implementation lives in scripts/.
& (Join-Path $PSScriptRoot 'scripts/publish.ps1') @args
