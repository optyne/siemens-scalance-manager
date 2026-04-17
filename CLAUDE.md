# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Windows desktop tool (WPF) to centrally manage Siemens SCALANCE network devices: S610, S615, XC-200, XB-200, XF-200BA, XP-200, XR-300WG. Supports multi-device inventory, credential storage, and reading/writing network settings (VLAN, NTP, IP, VPN) through SNMP / SSH-CLI / WBM-HTTPS depending on the model's capabilities.

Reference PDFs (vendor manuals) live at the repo root: `SIEMENS_BA_SCALANCE-*.pdf` and `SIEMENS_PH_SCALANCE-*.pdf`. These are the source of truth for vendor-specific OIDs, CLI commands, and WBM endpoints — consult them when implementing concrete driver behavior.

**CLI verification status lives in `docs/VERIFICATION.md`** — per-command it tells you which commands are backed by primary sources vs. inferred from Cisco-IOS conventions. `ScalanceCliDriverBase.DryRun` defaults to `true` so inferred writes never hit a real device without operator review; update `docs/VERIFICATION.md` and consider flipping the default only after validating against a physical S615 / X-200.

The file `SIEMENS_BA_SCALANCE-S610_76.pdf` is misnamed — its Validity section identifies its content as the **S615** Operating Instructions. No S610-specific Siemens doc is currently in the repo.

## Build / run / test

```bash
# From repo root. dotnet 8 SDK required (at C:\Program Files\dotnet on dev box).
dotnet build src/SiemensScalanceManager.sln
dotnet test  tests/Scalance.Tests/Scalance.Tests.csproj
dotnet test  tests/Scalance.Tests/Scalance.Tests.csproj --filter "FullyQualifiedName~CapabilityMatrixTests.S615_supports_IpsecVpn_and_Ssh"
dotnet run   --project src/Scalance.App/Scalance.App.csproj
```

The app writes its SQLite DB to `%LOCALAPPDATA%\SiemensScalanceManager\scalance.db` (created via `EnsureCreated` on startup — no migrations yet).

All projects target `net8.0` except `Scalance.Data`, `Scalance.App`, and `Scalance.Tests`, which target `net8.0-windows` because DPAPI (`System.Security.Cryptography.ProtectedData`) and WPF are Windows-only. Anything that transitively references `Scalance.Data` must also target `net8.0-windows`.

## Architecture

Layered; dependencies flow inward toward `Scalance.Core`.

- **Scalance.Core** — pure domain types, no I/O. `IDeviceDriver` is the contract every device implementation must satisfy. `CapabilityMatrix` (`Capabilities/CapabilityMatrix.cs`) is the single source of truth for what each `DeviceModelKind` supports; UI and drivers both read from it to decide whether a feature is available. Add a new capability flag in `DeviceCapability` and update the matrix when onboarding a new feature.
- **Scalance.Protocols** — thin wrappers: `SnmpClient` (SharpSnmpLib, v2c full, v3 partial — walk/set on v3 are `NotSupportedException`), `SshSession` (SSH.NET). Drivers compose these; they do not know about device semantics.
- **Scalance.Drivers** — one driver per model family. `SnmpDriverBase` implements the generic MIB-II path (`GetStatusAsync`, port list via `ReadPortsAsync`) and returns "not implemented" for vendor features; subclasses override only what they actually support. `S615Driver` lazily opens SSH on first CLI call and reuses the session. `DeviceDriverFactory` maps `DeviceModelKind` → concrete driver; update it when adding a new driver.
- **Scalance.Data** — EF Core + SQLite. `DpapiCredentialStore` serializes `Credential` to JSON and encrypts with DPAPI `CurrentUser` scope using fixed entropy `"SiemensScalanceManager-v1"` — changing that string invalidates every stored credential on upgrade.
- **Scalance.App** — WPF + CommunityToolkit.Mvvm. DI is built in `App.OnStartup` (no generic host). `DeviceOperationsService` is the bridge between VMs and drivers: it loads the stored credential, constructs a driver via the factory, and disposes it after the call. VMs never touch drivers or the DB directly.

### Conventions that aren't obvious from the code

- Driver methods return `OperationResult` / `OperationResult<T>` rather than throwing for protocol-level failures. Only `OpenAsync` in `DeviceOperationsService` throws, because it's called imperatively from UI code that wraps it in try/catch.
- `Device.Id` / `Credential.Id` are GUIDs generated client-side; `Device.CredentialId` is nullable because a device can exist before its credentials are saved (the editor window creates the credential on Save and then writes `CredentialId` back on the device).
- `MainWindow.xaml` binds tabs to child VMs off `MainViewModel`. New feature tabs should follow the same pattern: add a property on `MainViewModel`, register the VM in `App.OnStartup`, and bind the tab's `DataContext` to that property.
- SNMPv3 credential fields (`SnmpV3AuthProtocol.Md5`, `Sha`, `DESPrivacyProvider`) are intentionally kept despite deprecation warnings — some older S610 units still require them. Don't remove.
