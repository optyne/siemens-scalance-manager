# verify-and-commit.ps1
# Build + test + (optional) commit. Run from repo root:
#   pwsh ./verify-and-commit.ps1
# or just right-click -> Run with PowerShell.

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

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

Step 'restore' { dotnet restore src/SiemensScalanceManager.sln }
Step 'build'   { dotnet build   src/SiemensScalanceManager.sln -c Debug --no-restore }
Step 'test'    { dotnet test    tests/Scalance.Tests/Scalance.Tests.csproj -c Debug --no-build --logger "console;verbosity=normal" }

Write-Host ""
Write-Host "Build + test green." -ForegroundColor Green
Write-Host ""

# Git: only if we're in a repo
git rev-parse --is-inside-work-tree *>$null 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "(not a git repo — skipping commit step)" -ForegroundColor Yellow
    exit 0
}

git status --short
Write-Host ""
$answer = Read-Host "Commit all current changes? [y/N]"
if ($answer -notmatch '^(y|Y)') { Write-Host "Skipped commit."; exit 0 }

$msg = @"
feat: VPN/Subnet/VLAN editors + DryRun + DCP discovery

GUI
- New VpnEditor + SubnetEditor VMs/Views; VLAN editor now editable
- OperationLog singleton + MainWindow device banner and log pane
- VMs surface planned CLI commands via DryRunPreview helper
- DryRun checkbox in banner — operator can flip it at runtime
- New Discovery tab: PROFINET DCP Identify-All via Npcap, lists
  SCALANCE devices on the local L2 segment (name / MAC / IP / vendor)

Drivers
- Extract ScalanceCliCommands (pure, testable) for VLAN / interface / IPsec
- ScalanceCliDriverBase: VLAN get (Q-BRIDGE) + write (CLI),
  interfaces get/set, VPN get/set for IpsecVpn-capable devices
- Fix port identifier: use SCALANCE M.P format (0.1, 0.8, 1.1)
  instead of Cisco-IOS "ethernet 0/N" — verified from WBM manuals
- Add DryRun gate: inferred-syntax writes (VLAN/Interface/VPN) build
  and expose the command list but do NOT execute until an operator
  validates on real hardware. NTP writes bypass DryRun (validated).
- DeviceOperationsService applies DryRun setting to every driver it opens

Protocols
- New Scalance.Protocols.Dcp: DcpFrame (pure builder/parser for PROFINET
  DCP Identify-All, ethertype 0x8892, multicast 01:0E:CF:00:00:00) and
  DcpDiscoveryService (SharpPcap 6.3 raw capture, Npcap-missing graceful
  fallback). Package reference on SharpPcap added.

Docs
- docs/VERIFICATION.md — sources, verified facts, inferred paths, and
  a new "Web-search snippets" section flagging that the S615 CLI uses
  "ports/no ports" inside config-vlan mode (not Cisco "switchport") and
  an "ipsec connection" model (not "crypto map")
- CLAUDE.md — cross-link to VERIFICATION.md, note S610 PDF mislabel,
  note DryRun default
- ScalanceCliCommands.cs header documents the known contradictions

Tests
- CLI builder tests expect "interface 0.1" M.P port format
- FormatPortId theory (1→0.1, 8→0.8, 101→1.1, 216→2.16)
- ParseInterfaces handles module.port interface names
- New DcpFrameTests: request bytes (eth hdr, ethertype, FrameID, XID,
  all-selector block), response parsing (name/mac/ip/vendor/device-id),
  XID mismatch and wrong-ethertype rejection

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
"@

git add -A
git commit -m $msg
git log -1 --stat
