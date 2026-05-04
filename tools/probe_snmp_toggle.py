"""
SNMP agent toggle probe.

Disables the SNMP agent (`no snmpagent`), verifies it's disabled by
re-reading `show running-config snmp all`, then re-enables it
(`snmpagent`) and verifies it's enabled again. Critical safety:
the re-enable + write-startup-config block runs in a `finally` so
that a crash mid-probe still leaves the device with SNMP back on.

Probe is idempotent — at the end the device should be in exactly
the pre-state. Affects RUNTIME state only between steps 1 and 3;
startup-config is only written AFTER the re-enable so a power loss
mid-probe also boots the device back into a working state.

Verified against:
  PH_SCALANCE-S615-CLI_76 sec 11.1.3.1 (snmpagent / no snmpagent — global config)
"""

import os
import re
import sys
import time

HOST = "192.168.1.1"
USER = "admin"
PASS = "Industry4.0"

OUT_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "probe_snmp_output.log")

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


# Disable: runtime only, do NOT write startup-config so a power-loss
# mid-probe still boots into the pre-state.
DISABLE_LINES = [
    "configure terminal",
    "no snmpagent",
    "end",
]

# Re-enable: writes startup-config to permanently restore.
REENABLE_LINES = [
    "configure terminal",
    "snmpagent",
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


def state_from_snmp_show(text):
    """Return 'enabled', 'disabled', or 'unknown' based on the LAST occurrence
    of `snmpagent` / `no snmpagent` in the output (the order in show output
    reflects effective config — last write wins)."""
    last_state = "unknown"
    for raw in text.splitlines():
        s = raw.strip()
        if s == "snmpagent":
            last_state = "enabled"
        elif s == "no snmpagent":
            last_state = "disabled"
    return last_state


def main():
    paramiko = ensure_paramiko()
    out = open(OUT_PATH, "w", encoding="utf-8", newline="\n")

    def w(s=""):
        print(s)
        out.write(s + "\n")
        out.flush()

    w(f"=== SNMP agent toggle probe to {USER}@{HOST} at {time.strftime('%Y-%m-%d %H:%M:%S')} ===")
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

    disable_failures = []
    reenable_failures = []
    pre_state = "unknown"
    post_disable_state = "unknown"
    post_reenable_state = "unknown"

    try:
        # ---- Step 0: snapshot ----
        w("=" * 72)
        w("Step 0 — Snapshot pre-state (show running-config snmp all)")
        w("=" * 72)
        pre_text = show("show running-config snmp all")
        w(pre_text)
        pre_state = state_from_snmp_show(pre_text)
        w(f"  pre-state SNMP agent: {pre_state}")
        w("")
        if pre_state != "enabled":
            w(f"⚠ Pre-state is '{pre_state}', not 'enabled'. Aborting probe to avoid leaving device in worse shape.")
            return

        # ---- Step 1: disable (RUNTIME ONLY — no write startup-config) ----
        w("=" * 72)
        w("Step 1 — Disable SNMP agent (no snmpagent, runtime only)")
        w("=" * 72)
        disable_failures = send_block(DISABLE_LINES, "disable")
        if disable_failures:
            w(f"✗ {len(disable_failures)} disable line(s) failed:")
            for ln, reason, snip in disable_failures:
                w(f"   FAIL [{reason}] {ln}")
                w(f"        device said: {snip[-300:]}")
        else:
            w("✓ Every line of the disable batch was accepted.")
        w("")

        # ---- Step 2: verify disabled ----
        w("=" * 72)
        w("Step 2 — Verify SNMP agent is disabled")
        w("=" * 72)
        post_disable_text = show("show running-config snmp all")
        w(post_disable_text)
        post_disable_state = state_from_snmp_show(post_disable_text)
        w(f"  post-disable SNMP agent: {post_disable_state}")
        if post_disable_state == "disabled":
            w("  ✓ Disable confirmed by show output.")
        else:
            w(f"  ✗ Expected 'disabled', got '{post_disable_state}'.")
        w("")

    finally:
        # ---- Step 3: ALWAYS re-enable (and commit to startup-config) ----
        w("=" * 72)
        w("Step 3 — Re-enable SNMP agent (snmpagent + write startup-config)")
        w("        [runs in `finally` — always executes, even on earlier failure]")
        w("=" * 72)
        try:
            reenable_failures = send_block(REENABLE_LINES, "reenable")
            if reenable_failures:
                w(f"✗ {len(reenable_failures)} re-enable line(s) failed:")
                for ln, reason, snip in reenable_failures:
                    w(f"   FAIL [{reason}] {ln}")
                    w(f"        device said: {snip[-300:]}")
            else:
                w("✓ Every line of the re-enable batch was accepted.")
            w("")

            # ---- Step 4: verify re-enabled ----
            w("=" * 72)
            w("Step 4 — Verify SNMP agent is back to enabled")
            w("=" * 72)
            post_reenable_text = show("show running-config snmp all")
            w(post_reenable_text)
            post_reenable_state = state_from_snmp_show(post_reenable_text)
            w(f"  post-reenable SNMP agent: {post_reenable_state}")
            if post_reenable_state == "enabled":
                w("  ✓ Re-enable confirmed by show output.")
            else:
                w(f"  ✗✗✗ Expected 'enabled', got '{post_reenable_state}'. MANUAL INTERVENTION REQUIRED — log into WBM and turn SNMP back on.")
        finally:
            try:
                chan.close()
                client.close()
            except Exception:
                pass

    # ---- Summary ----
    w("")
    w("=" * 72)
    w("SUMMARY")
    w("=" * 72)
    w(f"  Pre-state      : {pre_state}")
    w(f"  Disable Set    : {'PASS' if not disable_failures else f'FAIL ({len(disable_failures)} lines)'}")
    w(f"  Disable Verify : {'PASS' if post_disable_state == 'disabled' else f'FAIL ({post_disable_state})'}")
    w(f"  Re-enable Set  : {'PASS' if not reenable_failures else f'FAIL ({len(reenable_failures)} lines)'}")
    w(f"  Re-enable Verify: {'PASS' if post_reenable_state == 'enabled' else f'FAIL ({post_reenable_state})'}")
    w("")
    if post_reenable_state == "enabled":
        w("  ✓ Device left in enabled state — pre-state restored.")
    else:
        w("  ✗ DEVICE NOT FULLY RESTORED — verify SNMP via WBM and re-enable manually if needed.")
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
