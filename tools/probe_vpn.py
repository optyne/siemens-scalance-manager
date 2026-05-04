"""
VPN/IPsec write probe.

Mirrors what `ScalanceCliCommands.BuildSetVpnTunnel` produces for a minimal
PSK-authenticated, disabled tunnel, then sends it line-by-line to the real
device, checks each response for `% Invalid`, reads the config back, and
cleans up.

Goal: prove BuildSetVpnTunnel's output is valid CLI without modifying the
App. If every line is accepted, the App can write VPN with confidence.
"""

import os
import re
import sys
import time

HOST = "192.168.1.1"
USER = "admin"
PASS = "Industry4.0"

OUT_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "probe_vpn_output.log")

PROMPT_RE = re.compile(rb"[A-Za-z_][\w\-]*(?:\([^)\r\n]+\))?[#>]\s*$")
ANSI_RE = re.compile(rb"\x1B\[[0-9;]*[A-Za-z]|\x1B\][^\x07]*\x07|\x1B[=>]")
PAGER_RE = re.compile(rb"--More--|Press[^\r\n]*continue", re.IGNORECASE)


def strip_ansi(b):
    return ANSI_RE.sub(b"", b)


def read_until_prompt(channel, timeout=15):
    buf = b""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if channel.recv_ready():
            chunk = channel.recv(8192)
            if not chunk:
                break
            buf += chunk
            stripped = strip_ansi(buf)
            if PROMPT_RE.search(stripped):
                return True, buf
            tail = stripped[-200:]
            if PAGER_RE.search(tail):
                channel.send(b" ")
                time.sleep(0.05)
        else:
            time.sleep(0.05)
    return False, buf


# Mirror the EXACT lines BuildSetVpnTunnel emits for this VpnTunnel:
#   Name = "probe1"
#   RemoteEndpoint = "10.99.99.99"
#   RemoteSubnet = "10.99.99.0/24"
#   LocalSubnet = "192.168.1.0/24"
#   Enabled = false (operation disabled)
#   AuthMode = Psk
#   PreSharedKey = "TestProbe_2026_05_04"
#   Ike = { Encryption=aes256cbc, Hash=sha256, DhGroup=14, LifetimeMinutes=480 }
#   Esp = { Encryption=aes256cbc, Hash=sha256, PfsGroup=14, LifetimeMinutes=60 }
PROBE_NAME = "probe2026"
PROBE_REMOTE_NAME = f"{PROBE_NAME}-remote"
PROBE_LINES = [
    "configure terminal",
    "ipsec",
    f"remote-end name {PROBE_REMOTE_NAME}",
    "addr 10.99.99.99",
    "conn-mode standard",
    "subnet 10.99.99.0/24",
    "exit",
    f"connection name {PROBE_NAME}",
    f"rmend name {PROBE_REMOTE_NAME}",
    "loc-subnet 192.168.1.0/24",
    "k-proto ikev2",
    "operation disabled",
    "authentication",
    "auth psk TestProbe_2026_05_04",
    "exit",
    "phase 1",
    "no default-ciphers",
    "ike-encryption aes256cbc",
    "ike-auth sha256",
    "ike-keyderivation dhgroup 14",
    "ike-lifetime 480",
    "exit",
    "phase 2",
    "no default-ciphers",
    "esp-encryption aes256cbc",
    "esp-auth sha256",
    "esp-keyderivation dhgroup 14",
    "lifetime 60",
    "exit",
    "exit",
    "no shutdown",
    "exit",
    "end",
    "write startup-config",
]

CLEANUP_LINES = [
    "configure terminal",
    "ipsec",
    f"no connection name {PROBE_NAME}",
    f"no remote-end name {PROBE_REMOTE_NAME}",
    "end",
    "write startup-config",
]


def ensure_paramiko():
    try:
        import paramiko
        return paramiko
    except ImportError:
        import subprocess
        subprocess.check_call([sys.executable, "-m", "pip", "install", "paramiko"])
        import paramiko
        return paramiko


def main():
    paramiko = ensure_paramiko()
    out = open(OUT_PATH, "w", encoding="utf-8", newline="\n")

    def w(s=""):
        print(s)
        out.write(s + "\n")
        out.flush()

    w(f"=== VPN probe to {USER}@{HOST} at {time.strftime('%Y-%m-%d %H:%M:%S')} ===")
    w("")

    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(HOST, username=USER, password=PASS, timeout=8,
                   look_for_keys=False, allow_agent=False)
    chan = client.invoke_shell(term="vt100", width=200, height=200)
    read_until_prompt(chan, timeout=15)
    w("connected.")
    w("")

    def send_line(line, label=""):
        chan.send((line + "\n").encode())
        ok, buf = read_until_prompt(chan, timeout=15)
        text = strip_ansi(buf).decode("utf-8", errors="replace")
        invalid = "% Invalid" in text or "% Error" in text or "% Unknown" in text
        # Collapse output for log
        compact = " ".join(text.split())
        if len(compact) > 200:
            compact = compact[:200] + "…"
        marker = " ✗" if invalid else " ✓" if ok else " ?"
        w(f"  {label or ''}>>> {line}")
        w(f"          {marker}  {compact}")
        return ok, invalid, text

    # ---- Step 1: send the BuildSetVpnTunnel-equivalent command set ----
    w("=" * 72)
    w("Step 1 — Send BuildSetVpnTunnel output for probe2026 tunnel")
    w("=" * 72)
    set_failures = []
    for ln in PROBE_LINES:
        ok, invalid, text = send_line(ln, label="set")
        if invalid or not ok:
            set_failures.append((ln, "invalid" if invalid else "no-prompt", text[-300:]))

    w("")
    if set_failures:
        w(f"✗ {len(set_failures)} line(s) of the BuildSetVpnTunnel batch failed:")
        for ln, reason, snippet in set_failures:
            w(f"   FAIL [{reason}] {ln}")
            w(f"        device said: {snippet[-200:]}")
    else:
        w("✓ Every line of the BuildSetVpnTunnel batch was accepted by the device.")

    # ---- Step 2: read back via show running-config ipsec all ----
    w("")
    w("=" * 72)
    w("Step 2 — Read back the IPsec config")
    w("=" * 72)
    chan.send(b"show running-config ipsec all\n")
    ok, buf = read_until_prompt(chan, timeout=20)
    text = strip_ansi(buf).decode("utf-8", errors="replace")
    w(text)
    contains_probe = PROBE_NAME in text and PROBE_REMOTE_NAME in text
    w("")
    if contains_probe:
        w(f"✓ Read-back includes both '{PROBE_NAME}' and '{PROBE_REMOTE_NAME}'.")
    else:
        w(f"✗ Read-back does NOT show our probe tunnel — write may have been rolled back or rejected silently.")

    # ---- Step 3: clean up ----
    w("")
    w("=" * 72)
    w("Step 3 — Cleanup (delete probe connection + remote-end)")
    w("=" * 72)
    cleanup_failures = []
    for ln in CLEANUP_LINES:
        ok, invalid, text = send_line(ln, label="cleanup")
        if invalid or not ok:
            cleanup_failures.append((ln, "invalid" if invalid else "no-prompt", text[-300:]))

    if cleanup_failures:
        w("")
        w(f"! {len(cleanup_failures)} cleanup line(s) failed — manual WBM cleanup may be needed:")
        for ln, reason, snippet in cleanup_failures:
            w(f"   FAIL [{reason}] {ln}")
            w(f"        device said: {snippet[-200:]}")

    # ---- Step 4: confirm cleanup ----
    w("")
    w("=" * 72)
    w("Step 4 — Verify cleanup")
    w("=" * 72)
    chan.send(b"show running-config ipsec all\n")
    ok, buf = read_until_prompt(chan, timeout=20)
    text = strip_ansi(buf).decode("utf-8", errors="replace")
    still_present = PROBE_NAME in text or PROBE_REMOTE_NAME in text
    if still_present:
        w(f"✗ Probe tunnel still present after cleanup. Manual WBM cleanup needed for '{PROBE_NAME}'.")
        w(text)
    else:
        w(f"✓ Probe tunnel removed cleanly.")

    chan.close()
    client.close()

    # Final summary
    w("")
    w("=" * 72)
    w("SUMMARY")
    w("=" * 72)
    w(f"  Set    : {'PASS' if not set_failures else f'FAIL ({len(set_failures)} lines)'}")
    w(f"  Read   : {'PASS' if contains_probe else 'FAIL'}")
    w(f"  Cleanup: {'PASS' if not cleanup_failures and not still_present else 'FAIL'}")
    w("")
    w(f"Full log: {OUT_PATH}")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(f"FATAL: {type(e).__name__}: {e}")
        import traceback
        traceback.print_exc()
        sys.exit(1)
