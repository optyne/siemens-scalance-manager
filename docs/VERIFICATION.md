# CLI / Protocol Verification Status

This document records, per feature, whether the implementation is grounded in
primary Siemens documentation or inferred from other sources. Update it every
time you verify a command against a real device.

## Sources actually in this repo

| File | Covers | Scope |
| --- | --- | --- |
| `SIEMENS_BA_SCALANCE-S610_76.pdf` | **S615 Operating Instructions** (file is mislabeled — Validity section on page 3 says "SCALANCE S615"). Hardware install, LEDs, PLUG handling. | No CLI commands. |
| `SIEMENS_BA_SCALANCE-XC-200_76.pdf` | XC-200 Operating Instructions. | No CLI commands. |
| `SIEMENS_PH_SCALANCE-S615-WBM_76.pdf` | S615 Web Based Management configuration manual (346 pp). | WBM field semantics for IP / VLAN / IPsec VPN / NTP / firewall. No CLI. |
| `SIEMENS_PH_SCALANCE-XB-200-XC-200-XF-200BA-XP-200-XR-300WG-WBM_76.pdf` | X-200 family WBM configuration manual (422 pp). | WBM field semantics. No CLI. |

| `docs/PH_SCALANCE-S615-CLI_76_en-US.pdf` | S615 CLI configuration manual (850 pp). | Full CLI command reference for S615. Primary source for all CLI builders. |

**Additional CLI manuals** (not yet in this repo):

- XB/XC/XP-200: `https://support.industry.siemens.com/cs/attachments/109743150/PH_SCALANCE-XB-200-XC-200-XP-200-CLI_76.pdf`
- XB/XC/XF-200BA/XP/XR-300WG (consolidated): `https://support.industry.siemens.com/cs/attachments/109817329/PH_SCALANCE-XB-200-XC-200-XF-200BA-XP-200-XR-300WG-CLI_76.pdf`
- SC-600 (related reference): `https://support.industry.siemens.com/cs/attachments/109754814/PH_SCALANCE-SC-600-CLI_76.pdf`

## Verified from primary sources

| Item | Source | Notes |
| --- | --- | --- |
| Port identifier format `M.P` (e.g. `0.1` = module 0 port 1) | S615 WBM p. ~236, X-200 WBM p. 281 — both quote verbatim: *"port 0.1 is module 0, port 1"* | Implemented in `ScalanceCliCommands.FormatPortId`. |
| S615 supports IPsec VPN up to 20 phase-2 connections | S615 WBM §2.5 | `CapabilityMatrix.S615` has `IpsecVpn`. |
| X-200 family does NOT support IPsec | X-200 WBM: 0 occurrences of "IPsec" in 422 pages | `CapabilityMatrix` correctly gates `IpsecVpn` to S615 only. |
| S615 VPN semantic model (Remote End, Connections, Auth, Phase 1, Phase 2) | S615 WBM §4.10.6 | `VpnEditorViewModel` fields and `VpnTunnel` / `IkeSettings` / `EspSettings` match. |
| S615 authentication modes: Disabled / Remote Cert / CA Cert / PSK | S615 WBM §4.10.6.4 | `VpnAuthMode` enum. |
| Phase 1 DH groups 1,2,5,14,15,16,17,18 and hashes MD5/SHA1/SHA256/SHA384/SHA512 | S615 WBM §4.10.6.5 | `VpnEditorViewModel` dropdown options. |
| Phase 2 PFS groups same set, plus None | S615 WBM §4.10.6.6 | Same. |
| DCP Ethertype 0x8892 and multicast MAC `01:0E:CF:00:00:00` | PROFINET University + Wireshark docs | Not yet implemented; noted for DCP discovery feature. |
| `vlan <id>` to create VLAN and enter VLAN config mode | PH_SCALANCE-S615-CLI_76 p. 249 | `BuildSetVlans` uses this. Prompt: `cli(config-vlan-$$$)#` |
| `name <vlan-name>` to set VLAN name (max 32 chars) | PH_SCALANCE-S615-CLI_76 p. 265 | Inside VLAN config mode. |
| `ports (<tagged-list>) [untagged (<untagged-list>)] [forbidden (<forbidden-list>)]` for VLAN port membership | PH_SCALANCE-S615-CLI_76 pp. 266-267 | Replaces old Cisco-style `switchport access/trunk` approach. Port membership is configured per-VLAN, not per-interface. |
| `base bridge-mode {dot1d-bridge\|dot1q-vlan}` for VLAN-aware mode | PH_SCALANCE-S615-CLI_76 p. 242 | Not currently emitted by builder; device may need to be in dot1q-vlan mode first. |
| `switchport mode {trunk\|hybrid}` (default: hybrid) | PH_SCALANCE-S615-CLI_76 p. 258 | S615 uses trunk/hybrid (NOT access), in interface config mode. Not emitted by VLAN builder. |
| `ip address <ip> {<mask> \| / <prefix>}` in VLAN interface mode | PH_SCALANCE-S615-CLI_76 pp. 338-339 | In `cli(config-if-vlan-$$$)#`. DHCP must be disabled first via `no ip address dhcp`. |
| `ip address dhcp` in VLAN interface mode | PH_SCALANCE-S615-CLI_76 p. 340 | In `cli(config-if-vlan-$$$)#`. |
| `ip route <prefix> <mask> <next-hop>` for static routes / default gateway | PH_SCALANCE-S615-CLI_76 pp. 331-332 | In global config mode. Replaces old `ip default-gateway`. |
| `ntp` to enter NTP config mode | PH_SCALANCE-S615-CLI_76 p. 216 | Prompt: `cli(config-ntp)#`. |
| `ntp server id <1-3> { ipv4 <ip> \| fqdn-name <fqdn> } [poll <sec>]` | PH_SCALANCE-S615-CLI_76 pp. 217-218 | Max 3 servers. Poll range 64-2592000 seconds. |
| `ipsec` to enter IPsec config mode | PH_SCALANCE-S615-CLI_76 p. 697 | Prompt: `cli(config-ipsec)#`. |
| `connection name <name>` to create/enter IPsec connection | PH_SCALANCE-S615-CLI_76 p. 699 | Prompt: `cli(config-conn-X)#`. Max 122 chars. |
| `remote-end name <name>` to create/enter remote end | PH_SCALANCE-S615-CLI_76 p. 705 | Prompt: `cli(config-ipsec-rmend-X)#`. Max 128 chars. |
| `addr <subnet\|dns>` for remote endpoint address | PH_SCALANCE-S615-CLI_76 p. 711 | In remote-end config mode. |
| `conn-mode {roadwarrior\|standard}` | PH_SCALANCE-S615-CLI_76 pp. 713-714 | In remote-end config mode. |
| `subnet <subnet\|dns>` for remote subnet | PH_SCALANCE-S615-CLI_76 p. 715 | In remote-end config mode. |
| `loc-subnet <cidr>` for local subnet | PH_SCALANCE-S615-CLI_76 p. 719 | In connection config mode. |
| `k-proto {ikev1\|ikev2}` for IKE version | PH_SCALANCE-S615-CLI_76 p. 718 | In connection config mode. |
| `operation {disabled\|start\|wait\|on-demand\|...}` | PH_SCALANCE-S615-CLI_76 pp. 719-721 | In connection config mode. |
| `authentication` to enter auth config mode | PH_SCALANCE-S615-CLI_76 p. 717 | Prompt: `cli(config-conn-auth)#`. |
| `auth psk <key>` for pre-shared key | PH_SCALANCE-S615-CLI_76 p. 728 | In auth config mode. |
| `auth cacert <ca> localcert <cert>` for CA certificate auth | PH_SCALANCE-S615-CLI_76 p. 727 | In auth config mode. |
| `phase <1\|2>` to enter phase config mode | PH_SCALANCE-S615-CLI_76 p. 721 | Prompt: `cli(config-conn-phsX)#`. |
| `shutdown` / `no shutdown` for IPsec enable/disable | PH_SCALANCE-S615-CLI_76 pp. 709-710 | In IPsec config mode. Global IPsec on/off. |
| Phase 1 sub-commands `ike-encryption`, `ike-auth`, `ike-keyderivation`, `ike-lifetime` | PH_SCALANCE-S615-CLI_76 pp. 740-744 | Inside `cli(config-ipsec-conn-phase1)#`. DH group lives in `ike-keyderivation`, NOT `dh-group`. |
| Phase 2 sub-commands `esp-encryption`, `esp-auth`, `esp-keyderivation`, `lifetime` | PH_SCALANCE-S615-CLI_76 pp. 749-752 | Inside `cli(config-ipsec-conn-phase2)#`. PFS group lives in `esp-keyderivation`, NOT `pfs-group`. |
| `no default-ciphers` required before custom phase ciphers | PH_SCALANCE-S615-CLI_76 pp. 735-736, 747-748 | `BuildSetVpnTunnel` emits this in both phase 1 and phase 2 blocks so user-supplied values are not overridden by the device's default cipher set. |
| `auth cacert <ca> localcert <local>` for certificate-based VPN auth | PH_SCALANCE-S615-CLI_76 p. 727 | `BuildSetVpnTunnel` emits this when `AuthMode == Certificate` and `LocalCertificateName` is set. The `VpnTunnel` model currently carries one cert name and reuses it for both operands — add a separate CA field if the two need to differ. |
| `base bridge-mode dot1q-vlan` prerequisite emitted by `BuildSetVlans` | PH_SCALANCE-S615-CLI_76 p. 242 | Idempotent — sending it when already in dot1q-vlan mode is a no-op. Now emitted before any `vlan <id>` so VLAN commands cannot be rejected for being in dot1d-bridge mode. |
| DNS uses `dnsclient` mode + `manual srv <ip>` | PH_SCALANCE-S615-CLI_76 sec 9.7, pp. 408-417 (mode entries: `dnsclient` p. 411, `server type` p. 415, `manual srv` p. 414, `no shutdown` p. 417) | `BuildSetInterface` now emits the dnsclient block instead of the (incorrect) Cisco-style `ip name-server`. |
| `configure terminal` / `end` / `exit` mode navigation | PH_SCALANCE-S615-CLI_76 pp. 241, 262, 697 | Confirmed: `configure terminal` enters global config; `end` returns to privileged EXEC; `exit` goes up one level. |
| `write startup-config` to save configuration | PH_SCALANCE-S615-CLI_76 sec 5.4.2 p. 137 | **Verified + fixed 2026-04**。先前 builders 都發 `write memory`（Cisco-IOS 慣用語），但 S615 手冊**完全沒有**定義這個縮寫；唯一記錄的儲存指令是 `write startup-config`。全部 builders（含 NTP forceExecute 路徑）已全面改用 manual-verified 形式。|
| `show firewall ip-rules ipv4` | PH_SCALANCE-S615-CLI_76 pp. 591–646 | Read command for IPv4 firewall rules. Output parsing is best-effort until validated on real device. |
| `show firewall pre-rules ipv4` | PH_SCALANCE-S615-CLI_76 pp. 591–646 | Read command for predefined firewall service rules. Output parsing is best-effort. |
| `ipv4rule from <if> to <if> srcip <cidr> dstip <cidr> action <acc\|drop\|rej> [service <svc>] [log <level>] [prior <n>]` | PH_SCALANCE-S615-CLI_76 pp. 591–646 | Create IPv4 firewall rule. Gated by DryRun. |
| `no ipv4rule idx <N>` | PH_SCALANCE-S615-CLI_76 pp. 591–646 | Delete IPv4 firewall rule by index. Gated by DryRun. |
| `prerule <service> <interface>` | PH_SCALANCE-S615-CLI_76 pp. 591–646 | Toggle predefined service on interface (vlan 1 = local, vlan 2 = external). Gated by DryRun. |

## Web-search snippets (now confirmed against CLI manual)

The following signals from web-search snippets have been **confirmed** against
`docs/PH_SCALANCE-S615-CLI_76_en-US.pdf` and the corresponding CLI builders
have been updated. All items below are now in the "Verified" table above.

| Signal | Confirmed | Status |
| --- | --- | --- |
| VLAN mode `cli(config-vlan-$$$)#` with `ports`/`no ports` | Yes, pp. 249-267 | `BuildSetVlans` updated. |
| `base bridge-mode dot1q-vlan` | Yes, p. 242 | Documented but not yet emitted (noted in Inferred). |
| `ip address dhcp` on VLAN interface | Yes, p. 340 | `BuildSetInterface` confirmed correct. |
| S615 IPsec uses `ipsec`/`connection name` model | Yes, pp. 697-732 | `BuildSetVpnTunnel` completely rewritten. |
| NTP uses `ntp server id` with `ipv4`/`fqdn-name` keywords | Yes, pp. 217-218 | `SetNtpAsync` updated. |

## Inferred (needs real-device validation)

These items are still guarded by `ScalanceCliDriverBase.DryRun = true`.
Most CLI commands have been updated from the S615 CLI manual; the remaining
inferred items are limited to output parsing and edge cases.

| Item | What's assumed | Risk |
| --- | --- | --- |
| `show ip interface brief` output format (6 cols) | Cisco-IOS convention | Parser is defensive — handles `ethernet 0/1` and `0.1` (module.port) interface formats. Adjust when real output is available. |
| Cipher value tokens (`aes256`, `sha256`, DH group `14`, …) for `ike-encryption` / `ike-auth` / `ike-keyderivation` / `esp-encryption` / `esp-auth` / `esp-keyderivation` | Tokens taken from the WBM dropdowns; the CLI command names are verified but the exact accepted operand strings live on the same pages as the commands and were not transcribed | Risk: device may reject e.g. `aes256` if it expects `aes-256`. `BuildSetVpnTunnel` passes the IkeSettings/EspSettings strings through verbatim — adjust either the model defaults or add a translator if a real device rejects them. |
| VPN tunnel parser (`ParseVpnTunnels`) | Still parses legacy Cisco-style `crypto map` output format | Needs update to parse real S615 `show ipsec connections` output once a sample is available. |

## Admin password / DNS / Basic Wizard (2026-04, 已對照 S615 CLI 手冊修正)

| Item | Source | Status |
|------|--------|--------|
| `change password <pwd>` (User/Privileged EXEC, 改自己的密碼) | PH_SCALANCE-S615-CLI_76 sec 12.1.2 p. 567 | **Verified by manual**。`BuildChangeOwnPassword` 產生；不需 `configure terminal` / `write startup-config`（Trial mode 立即儲存）。|
| `user-account <name> password <pwd> role <role>` (global config, 改別人密碼) | PH_SCALANCE-S615-CLI_76 sec 12.1.4.7 p. 575 | **Verified by manual**。`BuildSetUserAccount` 產生；`role` 為必要參數。手冊明確規定：**無法修改當前已登入的使用者**，必須用 `change password`。|
| 密碼字元限制：不可含 `§ ? " ; : ß \` 空白 Delete | S615 CLI manual p. 576 | **Re-verified 2026-04 via pypdf**。先前版本誤把 `ß`（U+00DF, sharp-s）抄成 `` ` ``（backtick），導致程式錯誤地允許 `ß`（裝置會拒絕）並禁止 `` ` ``（裝置允許）。`ValidatePassword` 已修正。CR/LF/`"` 於 builder 層額外擋，以防 SSH 行級注入。|
| `system name <name>` (不是 `hostname`) | PH_SCALANCE-S615-CLI_76 sec 5.1.11.12 p. 98 | **Verified by manual**。`ApplyBasicWizardAsync` 已改用此指令。|
| `dnsclient` → `server type manual` / `manual srv <ip>` / `no shutdown` | PH_SCALANCE-S615-CLI_76 sec 9.7, pp. 408-417 | **Verified by manual**。`BuildSetDns` 產生。|
| `no manual all` 清除全部 DNS 伺服器 | PH_SCALANCE-S615-CLI_76 sec 9.7.3.2 p. 415 | **Verified by manual**。先前 `no manual srv`（無參數）無效，已修正。|
| `ip domain name <name>` (空格，非連字號) | PH_SCALANCE-S615-CLI_76 p. 10741 交叉引用 | **Verified by manual**。先前 `ip domain-name` 錯誤，已修正。|
| `show dnsclient information` (非 `show dnsclient`) | PH_SCALANCE-S615-CLI_76 sec 9.7.1.1 p. 409 | **Verified by manual**。`GetDnsAsync` 已修正。|
| `ApplyBasicWizardAsync` 組合順序 | hostname → interface → dns → ntp → password | 設計決策 — 密碼最後才改，避免中途斷線；NTP `forceExecute=true` 會跳過 DryRun。|
| VLAN `ports (fa 0/1,0/2) untagged (fa 0/3)` | PH_SCALANCE-S615-CLI_76 sec 8.1.4.5 p. 266 + sec 3.7.5 p. 57 | **Verified by manual**。CLI 實際用 `fa 0/N`（fast-ethernet interface-type）；`0.N` dotted 形式只是 WBM 顯示格式，CLI 不接受。已新增 `FormatCliPortId`。|
| `interface fa 0/N` vs `interface vlan N` | PH_SCALANCE-S615-CLI_76 sec 3.7.5 p. 57 | **Verified**。實體埠用 `fa 0/N`，VLAN 介面用 `vlan N`。`BuildSetInterface` 透過 `InterfaceName` 字串直傳，呼叫端需帶正確前綴。|
| `ip route <prefix> <mask> <next-hop>` | PH_SCALANCE-S615-CLI_76 sec 9.1.2.5 p. 331 | **Verified by manual**。`ip route 0.0.0.0 0.0.0.0 <gw>` 設預設路由。|
| `ip address <ip> <mask>` / `ip address dhcp` | PH_SCALANCE-S615-CLI_76 sec 9.1.3.2/3.3 p. 338-340 | **Verified by manual**。interface config 模式（VLAN 或 router port）。|
| `ntp server id <1-3> { ipv4 <ip> \| fqdn-name <FQDN> \| ipv6 <ipv6> }` | PH_SCALANCE-S615-CLI_76 sec 7.2.3.1 p. 217 | **Verified by manual**。最多 3 台；`poll` 範圍 64-2592000 秒。|
| `ntp time diff +HH:MM`（非 `clock timezone`） | PH_SCALANCE-S615-CLI_76 sec 7.2.3.6 p. 221 | **Verified by manual**。於 NTP config 模式內使用；必須帶正負號、兩位數。先前用 `clock timezone` 是 Cisco 語法誤植，已修正並加入 `IsValidNtpTimeDiff` 格式驗證。|
| IPsec `ipsec` → `connection name <name>` → `phase <1\|2>` 模式層 | PH_SCALANCE-S615-CLI_76 sec 12.4.2.1 p. 697 / 12.4.3.2 p. 699 / 12.4.5.5 p. 721 | **Re-verified 2026-04**。`BuildSetVpnTunnel` 已符合手冊。|
| IPsec `ike-encryption` / `esp-encryption` 合法值 | PH_SCALANCE-S615-CLI_76 sec 12.4.7.10 p. 741 / sec 12.4.8.6 p. 749 | **Verified + enforced**。僅接受 `3des / aes{128,192,256}{cbc,ctr,ccm16,gcm16}`；先前預設 `aes256` 不合法，已改 `aes256cbc` 並加入 `ValidateIkeEncryption`。|
| IPsec `ike-auth` / `esp-auth` | PH_SCALANCE-S615-CLI_76 sec 12.4.7.9 p. 740 / sec 12.4.8.5 p. 749 | **Verified + enforced**。僅 `md5/sha1/sha256/sha384/sha512`。|
| IPsec `ike-keyderivation dhgroup <N>` / `esp-keyderivation {none\|dhgroup <N>}` | PH_SCALANCE-S615-CLI_76 sec 12.4.7.11 p. 742 / sec 12.4.8.7 p. 751 | **Verified + fixed**。先前漏掉 `dhgroup` 關鍵字（直接送 `ike-keyderivation 14`）；已修正並驗證 DH 群組值 1/2/5/14-18。|
| IPsec `ike-lifetime` / phase-2 `lifetime` **單位：分鐘** | PH_SCALANCE-S615-CLI_76 sec 12.4.7.12 p. 744 / sec 12.4.8.8 p. 752 | **Verified + fixed**。手冊明確標 `<min(...)>`；先前變數名 `LifetimeSeconds` 且預設值以秒為單位，會造成裝置拒絕或 lifetime 太長。已改 `LifetimeMinutes`，預設 480/60 分，範圍驗證 10-2500000 / 10-16666666。|
| `addr <subnet\|dns>` / `conn-mode {roadwarrior\|standard}` / `subnet <subnet\|dns>` (remote-end) | PH_SCALANCE-S615-CLI_76 sec 12.4.4.1/3/5 p. 711-715 | **Re-verified 2026-04**。|
| `loc-subnet` / `operation` / `rmend name` / `k-proto` (connection) | PH_SCALANCE-S615-CLI_76 sec 12.4.5.* p. 717-722 | **Re-verified 2026-04**。`operation` 合法值：`disabled\|start\|wait\|on-demand\|start-di\|wait-di\|start-sms\|wait-sms\|start-tcp\|wait-tcp\|start-action\|wait-action\|start-gps\|wait-gps`（p. 719）。|
| 名稱長度限制：`connection name` 最多 122 字元、`remote-end name` 最多 128 字元 | PH_SCALANCE-S615-CLI_76 sec 12.4.3.2 p. 699 / 12.4.3.4 p. 705 | **Verified + enforced**。已在 `BuildSetVpnTunnel` 加 pre-flight check（因程式會自動產生 `<tunnel>-remote`，tunnel 名最多 121 字元才能確保派生名不超限）。|
| Firewall `ipv4rule from <iftype> [<if>] to <iftype> [<if>] srcip <ip\|subnet\|range> dstip <..> action {drop\|acc\|rej} [service <all\|name(32)>] [log {no\|info\|war\|cri}] [prior <0-127>] [comment <str(32)>]` | PH_SCALANCE-S615-CLI_76 sec 12.3.4.31 p. 627-629 | **Verified + fixed**。先前將 `0.0.0.0/0` 映射成 `srcip *` / `dstip *`，手冊不接受 `*`，裝置會拒絕；已改直接送 `0.0.0.0/0`。`prior` 新增 0-127 範圍驗證、`service` 名稱 32 字元驗證。|
| Firewall `prerule <svc> ipv4 int <if-type> <if-id> {enabled\|disabled}` | PH_SCALANCE-S615-CLI_76 sec 12.3.4.57-66 p. 653-663 | **Verified + fixed**。先前送 `prerule <svc> vlan 1` 漏了 `ipv4 / int / enabled\|disabled` 三個關鍵詞，裝置會拒絕。已改為完整形式；`LocalAccess`/`ExternalAccess` 映射為 `vlan 1`/`vlan 2` 並以 `enabled`/`disabled` 顯式控制。`ServiceName` 改用白名單驗證（dcp/dhcp/dns/http/https/ipsec/ping/snmp/ssh/syslog/systime/tcpevent/telnet/vrrp/openvpn/sinemarc）。|
| `show ntp info` (非 `show ntp`) | PH_SCALANCE-S615-CLI_76 sec 7.2.1.1 p. 215 | **Verified + fixed**。`GetNtpAsync` 原本送 `show ntp`，S615 接受的命令名是 `show ntp info`。已修正。|
| `no ipv4rule {all\|idx <1-128>}` | PH_SCALANCE-S615-CLI_76 sec 12.3.4.32 p. 630 | **Verified + enforced**。`BuildDeleteFirewallRule` 加入 idx 1-128 範圍驗證；新增 `BuildDeleteAllFirewallRules()` 使用 `no ipv4rule all`。|
| `show running-config` | PH_SCALANCE-S615-CLI_76 sec 5.4.1.1 p. 135 | **Re-verified 2026-04**。|
| IPsec `authentication` 子模式 → `auth psk <key>` / `auth cacert <ca> localcert <local>` | PH_SCALANCE-S615-CLI_76 sec 12.4.6.1/6.2 p. 727-728 | **Re-verified 2026-04**。|
| `k-proto {ikev1\|ikev2}` | PH_SCALANCE-S615-CLI_76 sec 12.4.5.2 p. 718 | **Re-verified 2026-04**。|
| `show firewall ip-rules {ipv4\|ipv6\|any}` / `show firewall pre-rules [{ipv4\|ipv6}]` | PH_SCALANCE-S615-CLI_76 sec 12.3.2.5/6/7 p. 593-594 | **Re-verified 2026-04**。|
| DNS client `shutdown` 語意 | PH_SCALANCE-S615-CLI_76 sec 9.7.3.4 p. 416 | **手冊文件錯誤**：該節 Description 寫「enable the DNS client」但 Result 寫「DNS client is disabled」。以 Result 為準（符合 Cisco 慣例：`shutdown` 禁用、`no shutdown` 啟用）。程式語意正確。|
| `show ip interface [{vlan <id>\| <if-type> <if-id>}]` | PH_SCALANCE-S615-CLI_76 sec 5.1.1.12 p. 76 | **Re-verified 2026-04**。手冊未提供輸出樣本，`ParseInterfaces` 已做多格式容錯。|
| Firewall `from`/`to` iftype 與 ifstring 為**分開兩個 token**（空格） | PH_SCALANCE-S615-CLI_76 sec 12.3.4.31 p. 627（`from <iftype> [<ifstring>]`）；p. 65 / p. 430 範例 `int vlan 1` | **Verified + fixed 2026-04**。先前 `FirewallRule.From = "vlan1"` 及 `BasicWizardViewModel.InterfaceName = "vlan1"` 無空格，裝置會把 `vlan1` 當成未知 iftype 而拒絕。已改為 `vlan 1` / `vlan 2`；`BuildCreateFirewallRule` 新增空值檢查。|
| `vlan <vlan-id(1-4094)>` 範圍驗證 + name 32 字元上限 | PH_SCALANCE-S615-CLI_76 sec 8.1.2.10 p. 250；sec 8.1.4.3 p. 265 | **Verified + enforced 2026-04**。`BuildSetVlans` 對 `Vlan.Id < 1 \|\| > 4094` 丟 `ArgumentOutOfRangeException`；`Name.Length > 32` 丟 `ArgumentException`，避免裝置拒絕 cryptic 錯誤。|
| IPsec `auth psk <string(255)>` / `auth cacert <string(255)> localcert <string(255)>` 長度上限 | PH_SCALANCE-S615-CLI_76 sec 12.4.6.1 p. 728；sec 12.4.6.2 p. 729 | **Verified + enforced 2026-04**。`BuildSetVpnTunnel` 對 PSK 與憑證名超過 255 字元丟例外；PSK 額外禁 CR/LF/`"` 以防 SSH 行級注入。|
| Predefined firewall service 白名單 | PH_SCALANCE-S615-CLI_76 sec 12.3.4.52-67 pp. 647-664 | **Verified + fixed 2026-04**。先前白名單含有手冊不存在的 `dcp`/`syslog`/`openvpn`/`sinemarc`（裝置會拒絕），同時遺漏了手冊有定義的 `cloudconnector`（p. 648）與 `vxlan`（p. 664）。已更正 `PredefinedRuleNames` 為：`cloudconnector/dhcp/dns/http/https/ipsec/ping/snmp/ssh/systime/tcpevent/telnet/vrrp/vxlan`。|
| `system name <name>` 255 字元上限 + SSH 安全字元檢查 | PH_SCALANCE-S615-CLI_76 sec 5.1.11.12 p. 98-99 | **Verified + enforced 2026-04**。新增 `BuildSetSystemName` 共用 builder；長度超過 255 或含 CR/LF/`"`/NUL 立即丟例外；`ApplyBasicWizardAsync` 改呼叫此 builder，避免原本字串直接內插至 `configure terminal` 批次中。|
| `user-account <user-name>` 1-250 字元 + 禁用字元 `§ ? " ; :` 空白 Delete | PH_SCALANCE-S615-CLI_76 sec 12.1.4.7 p. 575 | **Verified + enforced 2026-04**。先前 `BuildSetUserAccount` 只擋空字串，長度超限與含分號/引號的 username 會造成 CLI 行被誤切（例：`name;inject` → `user-account name;inject password …`）。新增 `ValidateUserName` 與 `ValidateRoleOrToken`，防禦 SSH 行級注入並對應手冊禁用集。|
| IPv4 位址/遮罩/閘道/DNS 嚴格四段驗證 | PH_SCALANCE-S615-CLI_76 sec 9.1.2.5 p. 331（ip route）、sec 9.1.3.2 p. 338（ip address）、sec 9.7.3.1 p. 414（manual srv） | **Verified + enforced 2026-04**。`RequireIpv4` 強制 `a.b.c.d` 四段；`System.Net.IPAddress.TryParse` 會把 `1.2.3` 誤判為 `1.2.0.3`（legacy BSD form），裝置不接受。`BuildSetInterface` 與 `BuildSetDns` 所有 IP 欄位（含 DNS 伺服器、閘道、靜態 IP/遮罩）皆經此檢查；`InterfaceName` 與 `DomainName` 同步加上 SSH 安全字元檢查。|
| NTP server 行格式化 + FQDN 100 字元上限 | PH_SCALANCE-S615-CLI_76 sec 7.2.3.1 p. 217 | **Verified + enforced 2026-04**。抽出 `FormatNtpServerLine(id, host)` 共用 builder；id 1-3 驗證；IPv4 採嚴格四段；FQDN 超過 100 字元或含 space/CR/LF/`"`/NUL 立即丟例外。`SetNtpAsync` 改呼叫此 builder，失敗直接回 `OperationResult.Fail`。|
| IPsec tunnel name / RemoteEndpoint / LocalSubnet / RemoteSubnet 單一 CLI token 檢查 | PH_SCALANCE-S615-CLI_76 sec 12.4.3.2 p. 699（connection name）；sec 12.4.4.1 p. 711（addr）；sec 12.4.4.5 p. 715（subnet）；sec 12.4.5.3 p. 719（loc-subnet） | **Verified + enforced 2026-04**。新增 `RequireCliToken` 共用檢查：禁 space/CR/LF/`"`/NUL。這些 IPsec 欄位原本直接內插，含空白的值會被裝置誤切欄位（如 `connection name foo bar` → 裝置把 `bar` 當成下一個參數），CR/LF 則會切斷 SSH 批次。|
| Firewall `from`/`to` iftype 白名單 + 格式檢查 | PH_SCALANCE-S615-CLI_76 sec 12.3.4.1 pp. 598-599（available interfaces 表）；sec 12.3.4.31 p. 627 | **Verified + enforced 2026-04**。`ValidateFirewallInterface` 強制首個 token 必須是手冊列出的 `vlan/ppp/IPsec[all]/OpenVPN[all]/SinemaRC/Device`（大小寫依手冊 table）；第二個 token（若有）必須是 0-4094 的整數（對應手冊 ifstring 範圍）；最多兩個 token。`SourceCidr`/`DestinationCidr`/`Service` 補 CR/LF/`"` 檢查，防止 srcip 區段插入行級注入。|
| DNS servers 最多 3 台 | PH_SCALANCE-S615-CLI_76 sec 9.7.3.1 p. 414（"A maximum of three DNS servers can be configured"） | **Verified + enforced 2026-04**。`BuildSetDns` 與 `BuildSetInterface` 兩個 DNS 入口都加上 `>3` 拒絕，避免部分 `manual srv` 被裝置接受、後面被拒絕造成半套狀態。|
| `show ntp info` 輸出解析對應新 CLI 形式 | PH_SCALANCE-S615-CLI_76 sec 7.2.3.1 p. 217（`ntp server id <N> ipv4 <ip>` / `fqdn-name <fqdn>`） | **Fixed 2026-04**。原 `ParseNtp` 取 `token[2]`，遇到新形式會把 `"id"` 當作 host 回傳。改為優先抽取 `ipv4` / `fqdn-name` 關鍵詞後的一個 token；舊式 `ntp server <host>` 亦保留 fallback，避免對既有 fixture 回歸。|
| IPsec 逐 tunnel disable 絕不發全域 `shutdown` | PH_SCALANCE-S615-CLI_76 sec 12.4.3.18/19 pp. 709-710 | **Fixed 2026-04**。原 `BuildSetVpnTunnel` 在 `t.Enabled=false` 時發 `shutdown`，但手冊明確說該指令會關閉**整個 IPsec 子系統**，連帶殺掉所有其他 tunnels。改為一律發 `no shutdown`（idempotent），per-tunnel 的停用完全由 `operation disabled` 處理。|
| 設定 `ip address` 前先 `no ip address`（DHCP↔static 切換安全） | PH_SCALANCE-S615-CLI_76 sec 9.1.3.2 p. 339 要求：「DHCP was disabled with the no ip address command」 | **Fixed 2026-04**。`BuildSetInterface` 在 DHCP 與 static 分支都先 emit `no ip address`。從 DHCP 轉 static 時若省略此步驟，裝置可能仍保留舊 DHCP 狀態而拒絕新 static；idempotent，對既有靜態 IP 也無害。|
| Syslog client 新功能（原本系統未支援） | PH_SCALANCE-S615-CLI_76 sec 13.2.2.1 p. 824（`syslogserver { ipv4 \| fqdn-name \| ipv6 } [port(1-65535)] [tls]`）；sec 13.2.2.2 p. 825（`no syslogserver ...`） | **Added 2026-04**。新增 `SyslogServer` 模型、`BuildAddSyslogServer` / `BuildRemoveSyslogServer` builders、`AddSyslogServerAsync` / `RemoveSyslogServerAsync` driver methods（S615 透過 CLI，X-200 從 SnmpDriverBase 取得 fail-by-default）。`DeviceCapability.SyslogClient` 加入 S615 與 ManagedSwitchCaps()。UI tab 已於 205ba0f 接上。|
| Ping 診斷新功能（原本系統未曝露） | PH_SCALANCE-S615-CLI_76 sec 5.1.8 p. 85-86（`ping { <ip> \| fqdn-name <fqdn> } [size 0-2080] [count 1-10] [timeout 1-100]`） | **Added 2026-04**。新增 `PingOptions` 模型、`FormatPingCommand` pure builder（嚴格 IPv4 / FQDN≤100、各參數範圍驗證）、`IDeviceDriver.PingAsync` 與 driver 實作（diagnostic 讀取類指令，User/Priv EXEC 執行，不經 DryRun）。UI 為新「診斷」分頁：主機、size/count/timeout 欄位與 raw CLI 輸出。|
| Traceroute 診斷新功能 | PH_SCALANCE-S615-CLI_76 sec 5.1.10 p. 88（`traceroute {ip <ip-address> \| ipv6 <ip6-address>}`） | **Added 2026-04**。`FormatTraceRouteCommand` 採嚴格 IPv4 / IPv6 literal；手冊**不接受 FQDN**，builder 顯式拒絕。`IDeviceDriver.TraceRouteAsync` 於 Privileged EXEC 執行（SSH 預設即為此模式）；「診斷」分頁擴充第二列輸入。|
| 裝置端 `configbackup` 新功能 + 匯出 running-config 為檔案 | PH_SCALANCE-S615-CLI_76 sec 5.4.1.2 p. 136（`show configbackup`）；sec 5.4.3.3-5 pp. 140-142（`configbackup create \| restore \| delete <name(64)>`） | **Added 2026-04**。新增 `BuildConfigBackup{Create,Restore,Delete}` builders（name 長度 64 + 單一 token 檢查）、`ParseConfigBackupNames`（`show configbackup` best-effort parser）、`IDeviceDriver.ListConfigBackupsAsync` / `CreateConfigBackupAsync` / `RestoreConfigBackupAsync` / `DeleteConfigBackupAsync`。新增「備份」分頁含 ListBox + 建立/還原/刪除按鈕，以及「匯出目前 running-config 為檔案」鈕（使用 `BackupConfigAsync` + `SaveFileDialog`）。能力 gating 採 `ConfigBackup + SshCli`。|
| SNMP 代理硬化（enabled / version / port） | PH_SCALANCE-S615-CLI_76 sec 9.8.2.1-2 pp. 437-438（`snmpagent` / `no snmpagent`）；sec 9.8.2.5 pp. 441-442（`snmp agent version {v3only\|all}`）；sec 9.8.2.16 p. 451（`snmpagent port <1024-65535>`） | **Added 2026-04**。新增 `SnmpAgentVersionPolicy` enum + `SnmpAgentConfig` 模型、三個 builders（port 範圍驗證 1024-65535，手冊明確把 161 排除於可設定範圍外）、三個 driver 方法、「SNMP 代理」分頁（啟用 checkbox、僅允許 SNMPv3 硬化 checkbox、監聽埠輸入框各自獨立 Apply 按鈕）。|
| 裝置重啟 `restart [memory\|factory]` | PH_SCALANCE-S615-CLI_76 sec 5.3.1 p. 130-131 | **Added 2026-04**。新增 `RestartMode` enum（Current/Memory/Factory）、`FormatRestartCommand`（單行指令無 `configure terminal` 包裝，對應 Privileged EXEC）、`IDeviceDriver.RestartAsync`（走 `RunOrPlanAsync`，DryRun 仍生效）。「診斷」分頁加入紅框 confirmation checkbox + 三顆獨立 restart 按鈕；SSH 斷線視為預期（重啟中），並把 confirmation 重置。|

## Re-verification procedure

1. Obtain the S615 CLI manual (`PH_SCALANCE-S615-CLI_76.pdf`) and drop it at
   the repo root alongside the WBM PDFs.
2. Set `ScalanceCliDriverBase.DryRun = false` on a test device only.
3. From the app, click **Apply** on each editor (VLAN / Subnet / VPN / NTP).
   The driver now builds AND executes commands. `LastPlannedCommands` still
   contains the last attempted batch for post-mortem.
4. Compare planned commands against the CLI manual. For each mismatch, update
   `ScalanceCliCommands` and move the row in the "Inferred" table above to the
   "Verified" table with a citation.
5. Once all four editors are green against a real device of each capability
   class (S615, one X-200 variant), `DryRun` default can be flipped to `false`.

## Products we claim to support

From `CapabilityMatrix`: S610, S615, XB-200, XC-200, XF-200BA, XP-200, XR-300WG.

**Caveat on S610:** the only S610 document in the repo is mislabeled — it
actually contains the S615 Operating Instructions. Siemens catalog does list
an S610 as a distinct product, but we have no primary source on it locally.
Treat S610 support as untested. Confirm the correct article number and grab
its real Operating + WBM manuals before shipping an S610 driver.
