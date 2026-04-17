# verify.ps1
# Pure validation pass — restore, build, and run the full test suite (including
# the CLI-command coverage test that re-checks every emitted verb against
# docs/VERIFICATION.md). No git side-effects.
#
# Usage:
#   pwsh ./verify.ps1
#
# Designed to be invoked by the Claude Code PostToolUse hook on `dotnet build`.

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$log = Join-Path $PSScriptRoot 'verify.log'
"=== verify started $(Get-Date -Format o) ===" | Out-File $log

function Step($name, [ScriptBlock]$body) {
    Write-Host "=== $name ===" -ForegroundColor Cyan
    "--- $name ---" | Out-File $log -Append
    & $body *>&1 | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $name (exit $LASTEXITCODE). See $log" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Step 'restore' { & $dotnet restore src/SiemensScalanceManager.sln }
Step 'build'   { & $dotnet build   src/SiemensScalanceManager.sln -c Debug --no-restore }
Step 'test'    { & $dotnet test    tests/Scalance.Tests/Scalance.Tests.csproj -c Debug --no-build --logger "console;verbosity=normal" }

Write-Host ""
Write-Host "Verify green." -ForegroundColor Green
