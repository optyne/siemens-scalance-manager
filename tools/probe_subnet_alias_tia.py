"""
Subnet / alias / tia-interface re-apply probe (vlan 1, idempotent).

Tests the IDEMPOTENT subset of BuildSetInterface against the real S615
management interface (vlan 1, 192.168.1.1/24). Writes the SAME values
the device already has (alias INT, tia-interface, ip address
192.168.1.1 255.255.255.0) to verify the CLI form is accepted.

SAFETY: the FULL BuildSetInterface batch begins with `no ip address`
which would *briefly* drop the IP from the management interface and
*could* close our SSH session before the follow-up `ip address ...`
takes effect. Since we're connected over the same vlan we're rewriting,
this probe DELIBERATELY OMITS the `no ip address` step. The omission
means we don't validate the DHCP→static transition path — that needs
either a console session or a non-management VLAN to test safely.
What this probe DOES validate:
  - `interface vlan 1` mode entry
  - `alias INT`         (S615 CLI manual sec 5.1.12.1 p. 99)
  - `tia-interface`     (S615 CLI manual: tia-interface keyword)
  - `ip address 192.168.1.1 255.255.255.0` re-applied with same value
  - read-back via `show running-config interface vlan 1 all`

If any line is rejected, the probe stops short of `write startup-config`
so the device's persisted config is unchanged.
"""

import os
import re
import sys
import time

HOST = "192.168.1.1"
USER = "admin"
PASS = "Industry4.0"

OUT_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "probe_subnet_output.log")

# -- expected current values (from running_config_dump.txt 2026-05-04) --
EXPECTED_IP    = "192.168.1.1"
EXPECTED_MASK  = "255.255.255.0"
EXPECTED_ALIAS = "INT"

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


# Idempotent re-apply: NO `no ip address` before the new ip-address line.
SET_LINES = [
    "configure terminal",
    "interface vlan 1",
    f"alias {EXPECTED_ALIAS}",
    "tia-interface",
    f"ip address {EXPECTED_IP} {EXPECTED_MASK}",
    "exit",
    "end",
]

WRITE_LINES = [
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

    w(f"=== subnet/alias/tia probe to {USER}@{HOST} at {time.strftime('%Y-%m-%d %H:%M:%S')} ===")
    w(f"Target: vlan 1 — alias={EXPECTED_ALIAS}, tia-interface, ip {EXPECTED_IP} {EXPECTED_MASK}")
    w("(Idempotent re-apply — `no ip address` step OMITTED for SSH-safety)")
    w("")

    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(HOST, username=USER, password=PASS, timeout=8,
                   look_for_keys=False, allow_agent=False)
    chan = client.invoke_shell(term="vt100", width=240, height=200)
    read_until_prompt(chan, timeout=15)
    w("connected.")
    w("")

    def send_line(line, label=""):
        chan.send((line + "\n").encode())
        ok, buf = read_until_prompt(chan, timeout=15)
        text = strip_ansi(buf).decode("utf-8", errors="replace")
        invalid = "% Invalid" in text or "% Error" in text or "% Unknown" in text
        compact = " ".join(text.split())
        if len(compact) > 200:
            compact = compact[:200] + "…"
        marker = " ✗" if invalid else " ✓" if ok else " ?"
        w(f"  {label or ''}>>> {line}")
        w(f"          {marker}  {compact}")
        return ok, invalid, text

    def show(cmd):
        chan.send((cmd + "\n").encode())
        ok, buf = read_until_prompt(chan, timeout=20)
        return strip_ansi(buf).decode("utf-8", errors="replace")

    def send_block(lines, label):
        failures = []
        for ln in lines:
            ok, invalid, text = send_line(ln, label=label)
            if invalid or not ok:
                failures.append((ln, "invalid" if invalid else "no-prompt", text[-300:]))
        return failures

    # ---- Step 0: snapshot ----
    w("=" * 72)
    w("Step 0 — Snapshot pre-state (show running-config interface vlan 1 all)")
    w("=" * 72)
    pre_text = show("show running-config interface vlan 1 all")
    w(pre_text)
    pre_alias = f"alias {EXPECTED_ALIAS}" in pre_text
    pre_tia = "tia-interface" in pre_text
    pre_ip = (EXPECTED_IP in pre_text and EXPECTED_MASK in pre_text)
    w(f"  pre-state: alias={pre_alias}  tia-interface={pre_tia}  ip+mask={pre_ip}")
    w("")

    # ---- Step 1: send re-apply batch ----
    w("=" * 72)
    w("Step 1 — Send re-apply batch (alias + tia-interface + ip address)")
    w("=" * 72)
    set_failures = send_block(SET_LINES, "set")
    if set_failures:
        w(f"✗ {len(set_failures)} set line(s) failed — SKIPPING write startup-config:")
        for ln, reason, snip in set_failures:
            w(f"   FAIL [{reason}] {ln}")
            w(f"        device said: {snip[-300:]}")
    else:
        w("✓ Every line of the re-apply batch was accepted.")
    w("")

    # ---- Step 2: read back BEFORE writing startup-config ----
    w("=" * 72)
    w("Step 2 — Read back (still in-memory only — write startup-config not yet sent)")
    w("=" * 72)
    mid_text = show("show running-config interface vlan 1 all")
    w(mid_text)
    mid_alias = f"alias {EXPECTED_ALIAS}" in mid_text
    mid_tia = "tia-interface" in mid_text
    mid_ip = (EXPECTED_IP in mid_text and EXPECTED_MASK in mid_text)
    w(f"  in-memory: alias={mid_alias}  tia-interface={mid_tia}  ip+mask={mid_ip}")
    w("")

    # ---- Step 3: write startup-config (only if everything is sane) ----
    write_failures = []
    if not set_failures and mid_alias and mid_tia and mid_ip:
        w("=" * 72)
        w("Step 3 — Persist to startup-config")
        w("=" * 72)
        write_failures = send_block(WRITE_LINES, "write")
        if write_failures:
            w(f"✗ {len(write_failures)} write line(s) failed:")
            for ln, reason, snip in write_failures:
                w(f"   FAIL [{reason}] {ln}")
                w(f"        device said: {snip[-300:]}")
        else:
            w("✓ write startup-config OK.")
    else:
        w("⚠ Skipping write startup-config because earlier steps failed or read-back is incomplete.")
    w("")

    # ---- Step 4: final read-back ----
    w("=" * 72)
    w("Step 4 — Final read-back (post-write or post-skip)")
    w("=" * 72)
    final_text = show("show running-config interface vlan 1 all")
    w(final_text)
    final_alias = f"alias {EXPECTED_ALIAS}" in final_text
    final_tia = "tia-interface" in final_text
    final_ip = (EXPECTED_IP in final_text and EXPECTED_MASK in final_text)
    w(f"  final: alias={final_alias}  tia-interface={final_tia}  ip+mask={final_ip}")

    chan.close()
    client.close()

    # ---- Summary ----
    overall_set_ok = not set_failures
    read_ok = mid_alias and mid_tia and mid_ip
    persist_ok = (not write_failures) and final_alias and final_tia and final_ip

    w("")
    w("=" * 72)
    w("SUMMARY")
    w("=" * 72)
    w(f"  Set    : {'PASS' if overall_set_ok else f'FAIL ({len(set_failures)} lines)'}")
    w(f"  Read   : {'PASS' if read_ok else 'FAIL'} (alias={mid_alias}, tia={mid_tia}, ip={mid_ip})")
    w(f"  Persist: {'PASS' if persist_ok else 'FAIL/SKIPPED'}")
    w("")
    w(f"  Note   : `no ip address` not tested — gives DHCP→static transition path coverage gap.")
    w(f"  Mitig. : test on console or non-management VLAN before relying on App apply path for that case.")
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
