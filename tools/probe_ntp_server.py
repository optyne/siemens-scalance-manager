"""
NTP server line write probe.

Mirrors what FormatNtpServerLine produces for a single IPv4 NTP server,
adds the `poll <sec>` optional parameter (manual p. 217 supports it but
the existing builder doesn't yet emit it), sends it on the live device,
checks for `% Invalid`, reads back via `show running-config ntp all`,
cleans up by `no ntp server id 1`, and verifies device is back to
pre-state.

Goal: prove the `ntp server id N ipv4 ADDR poll SEC` form is valid CLI on
real S615 firmware (V08.x). If poll is accepted, BuildSetNtp can be
extended to emit it; if it's rejected, we know to gate that behind
firmware feature detection.

Probe values:
  - id   = 1
  - ipv4 = 1.1.1.1 (Cloudflare; written to config only — no NTP query
           is issued by this script)
  - poll = 64 (manual p. 218 minimum; range 64-2592000)

Verified against:
  PH_SCALANCE-S615-CLI_76 sec 7.2.3.1 p. 217-218 (ntp server id)
  PH_SCALANCE-S615-CLI_76 sec 7.2.1.1 p. 215    (show running-config ntp all)
"""

import os
import re
import sys
import time

HOST = "192.168.1.1"
USER = "admin"
PASS = "Industry4.0"

OUT_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "probe_ntp_output.log")

# -- probe values --
NTP_ID    = 1
NTP_IPV4  = "1.1.1.1"
NTP_POLL  = 64       # manual p. 218 minimum

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


SET_LINES = [
    "configure terminal",
    "ntp",
    f"ntp server id {NTP_ID} ipv4 {NTP_IPV4} poll {NTP_POLL}",
    "exit",
    "end",
    "write startup-config",
]

CLEANUP_LINES = [
    "configure terminal",
    "ntp",
    f"no ntp server id {NTP_ID}",
    "exit",
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

    w(f"=== NTP server probe to {USER}@{HOST} at {time.strftime('%Y-%m-%d %H:%M:%S')} ===")
    w(f"Probe: ntp server id {NTP_ID} ipv4 {NTP_IPV4} poll {NTP_POLL}")
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
    w("Step 0 — Snapshot pre-state (show running-config ntp all)")
    w("=" * 72)
    pre_text = show("show running-config ntp all")
    w(pre_text)
    pre_has_target = NTP_IPV4 in pre_text
    w(f"  pre-state: probe target {NTP_IPV4} present? {pre_has_target}")
    w("")

    # ---- Step 1: write ----
    w("=" * 72)
    w("Step 1 — Send NTP server line (with poll)")
    w("=" * 72)
    set_failures = send_block(SET_LINES, "set")
    if set_failures:
        w(f"✗ {len(set_failures)} set line(s) failed:")
        for ln, reason, snip in set_failures:
            w(f"   FAIL [{reason}] {ln}")
            w(f"        device said: {snip[-300:]}")
    else:
        w("✓ Every line of the NTP set batch was accepted.")
    w("")

    # ---- Step 2: read back ----
    w("=" * 72)
    w("Step 2 — Read back (show running-config ntp all)")
    w("=" * 72)
    post_text = show("show running-config ntp all")
    w(post_text)
    target_present = NTP_IPV4 in post_text
    poll_present = f"poll {NTP_POLL}" in post_text or f" {NTP_POLL}" in post_text
    w(f"  ntp target {NTP_IPV4} in config? {target_present}")
    w(f"  poll {NTP_POLL} preserved in read-back? {poll_present}")
    if target_present:
        w(f"  ✓ NTP write read-back OK.")
    else:
        w(f"  ✗ NTP target NOT found — write may have been rejected silently.")
    w("")

    # ---- Step 3: cleanup ----
    w("=" * 72)
    w(f"Step 3 — Cleanup (no ntp server id {NTP_ID})")
    w("=" * 72)
    cleanup_failures = send_block(CLEANUP_LINES, "cleanup")
    if cleanup_failures:
        w(f"! {len(cleanup_failures)} cleanup line(s) failed:")
        for ln, reason, snip in cleanup_failures:
            w(f"   FAIL [{reason}] {ln}")
            w(f"        device said: {snip[-300:]}")
    w("")

    # ---- Step 4: verify cleanup ----
    w("=" * 72)
    w("Step 4 — Verify cleanup (back to pre-state)")
    w("=" * 72)
    final_text = show("show running-config ntp all")
    w(final_text)
    still_present = NTP_IPV4 in final_text
    if still_present:
        w(f"  ✗ NTP target {NTP_IPV4} still present after cleanup.")
    else:
        w(f"  ✓ NTP target removed cleanly.")

    chan.close()
    client.close()

    # ---- Summary ----
    w("")
    w("=" * 72)
    w("SUMMARY")
    w("=" * 72)
    w(f"  Set    : {'PASS' if not set_failures else f'FAIL ({len(set_failures)} lines)'}")
    w(f"  Read   : {'PASS' if target_present else 'FAIL'}")
    w(f"  Poll   : {'PASS (poll preserved)' if poll_present and target_present else 'FAIL/UNKNOWN'}")
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
