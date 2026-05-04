"""
Syslog client + manual DNS server write probe.

Mirrors the CLI that BuildAddSyslogServer / BuildSetDns produce, then
sends each block line-by-line to the real device, checks responses for
`% Invalid`, reads the config back via `show running-config events all`
and `show running-config dnsclient all`, cleans up, and verifies the
device is back to its pre-state.

Goal: prove the syslog + DNS builders' CLI output is valid on real
S615 firmware (V08.x). Probe values:
  - Syslog server : 192.0.2.1 (RFC 5737 TEST-NET-1, never routable),
                    UDP port 514, no TLS.
  - Manual DNS    : 8.8.8.8 (Google, well-known; only written to
                    config — no DNS query is issued by the probe).

Verified against:
  PH_SCALANCE-S615-CLI_76 sec 13.2.2.1 p. 824 (syslogserver)
  PH_SCALANCE-S615-CLI_76 sec 9.7.3   p. 408-417 (dnsclient / manual srv)
"""

import os
import re
import sys
import time

HOST = "192.168.1.1"
USER = "admin"
PASS = "Industry4.0"

OUT_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "probe_syslog_dns_output.log")

# -- probe values --
SYSLOG_IP   = "192.0.2.1"   # RFC 5737, never routable
SYSLOG_PORT = 514            # default UDP syslog
DNS_IP      = "8.8.8.8"      # config-only; no actual query

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


SYSLOG_SET = [
    "configure terminal",
    "events",
    f"syslogserver ipv4 {SYSLOG_IP} {SYSLOG_PORT}",
    "end",
    "write startup-config",
]

SYSLOG_CLEANUP = [
    "configure terminal",
    "events",
    f"no syslogserver ipv4 {SYSLOG_IP}",
    "end",
    "write startup-config",
]

DNS_SET = [
    "configure terminal",
    "dnsclient",
    f"manual srv {DNS_IP}",
    "exit",
    "end",
    "write startup-config",
]

DNS_CLEANUP = [
    "configure terminal",
    "dnsclient",
    f"no manual srv {DNS_IP}",
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

    w(f"=== syslog + dns probe to {USER}@{HOST} at {time.strftime('%Y-%m-%d %H:%M:%S')} ===")
    w(f"Syslog target: {SYSLOG_IP}:{SYSLOG_PORT} (RFC 5737 TEST-NET-1)")
    w(f"DNS target   : {DNS_IP} (config-only)")
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
    # Note: the BuildAddSyslogServer builder enters `events` mode to add the
    # syslogserver line, but on V08.x firmwares the line is actually emitted
    # under `show running-config syslog all`, NOT under `events all`. We
    # query both to be robust.
    w("=" * 72)
    w("Step 0 — Snapshot pre-state (events, syslog, dnsclient)")
    w("=" * 72)
    pre_events = show("show running-config events all")
    w("--- show running-config events all ---")
    w(pre_events)
    pre_syslog = show("show running-config syslog all")
    w("--- show running-config syslog all ---")
    w(pre_syslog)
    pre_dns = show("show running-config dnsclient all")
    w("--- show running-config dnsclient all ---")
    w(pre_dns)
    pre_has_syslog = (SYSLOG_IP in pre_events) or (SYSLOG_IP in pre_syslog)
    pre_has_dns = DNS_IP in pre_dns
    w(f"  pre-state: probe syslog target present? {pre_has_syslog}")
    w(f"             probe dns    target present? {pre_has_dns}")
    w("")

    # ---- Step 1: write syslog server ----
    w("=" * 72)
    w("Step 1 — Send BuildAddSyslogServer output")
    w("=" * 72)
    syslog_set_failures = send_block(SYSLOG_SET, "syslog-set")
    if syslog_set_failures:
        w(f"✗ {len(syslog_set_failures)} syslog set line(s) failed:")
        for ln, reason, snip in syslog_set_failures:
            w(f"   FAIL [{reason}] {ln}")
            w(f"        device said: {snip[-200:]}")
    else:
        w("✓ Every line of the syslog batch was accepted.")
    w("")

    # ---- Step 2: write manual DNS server ----
    w("=" * 72)
    w("Step 2 — Send BuildSetDns output (manual srv only — leaves server type alone)")
    w("=" * 72)
    dns_set_failures = send_block(DNS_SET, "dns-set")
    if dns_set_failures:
        w(f"✗ {len(dns_set_failures)} DNS set line(s) failed:")
        for ln, reason, snip in dns_set_failures:
            w(f"   FAIL [{reason}] {ln}")
            w(f"        device said: {snip[-200:]}")
    else:
        w("✓ Every line of the DNS batch was accepted.")
    w("")

    # ---- Step 3: read back ----
    w("=" * 72)
    w("Step 3 — Read back (events, syslog, dnsclient)")
    w("=" * 72)
    post_events = show("show running-config events all")
    w("--- show running-config events all ---")
    w(post_events)
    post_syslog = show("show running-config syslog all")
    w("--- show running-config syslog all ---")
    w(post_syslog)
    post_dns = show("show running-config dnsclient all")
    w("--- show running-config dnsclient all ---")
    w(post_dns)
    syslog_in_events = SYSLOG_IP in post_events
    syslog_in_syslog = SYSLOG_IP in post_syslog
    syslog_present = syslog_in_events or syslog_in_syslog
    dns_present = DNS_IP in post_dns
    w(f"  syslog target {SYSLOG_IP} in events config? {syslog_in_events}")
    w(f"  syslog target {SYSLOG_IP} in syslog config? {syslog_in_syslog}")
    w(f"  dns    target {DNS_IP}   in dnsclient config? {dns_present}")
    if syslog_present:
        w(f"  ✓ Syslog write read-back OK.")
    else:
        w(f"  ✗ Syslog target NOT found in events config — write may have been rejected silently.")
    if dns_present:
        w(f"  ✓ Manual DNS write read-back OK.")
    else:
        w(f"  ✗ DNS target NOT found in dnsclient config — write may have been rejected silently.")
    w("")

    # ---- Step 4: cleanup syslog ----
    w("=" * 72)
    w("Step 4 — Cleanup syslog (BuildRemoveSyslogServer output)")
    w("=" * 72)
    syslog_cleanup_failures = send_block(SYSLOG_CLEANUP, "syslog-clean")
    if syslog_cleanup_failures:
        w(f"! {len(syslog_cleanup_failures)} syslog cleanup line(s) failed:")
        for ln, reason, snip in syslog_cleanup_failures:
            w(f"   FAIL [{reason}] {ln}")
            w(f"        device said: {snip[-200:]}")

    # ---- Step 5: cleanup DNS ----
    w("=" * 72)
    w("Step 5 — Cleanup manual DNS srv")
    w("=" * 72)
    dns_cleanup_failures = send_block(DNS_CLEANUP, "dns-clean")
    if dns_cleanup_failures:
        w(f"! {len(dns_cleanup_failures)} DNS cleanup line(s) failed:")
        for ln, reason, snip in dns_cleanup_failures:
            w(f"   FAIL [{reason}] {ln}")
            w(f"        device said: {snip[-200:]}")
    w("")

    # ---- Step 6: verify cleanup ----
    w("=" * 72)
    w("Step 6 — Verify cleanup (back to pre-state)")
    w("=" * 72)
    final_events = show("show running-config events all")
    w("--- show running-config events all ---")
    w(final_events)
    final_syslog = show("show running-config syslog all")
    w("--- show running-config syslog all ---")
    w(final_syslog)
    final_dns = show("show running-config dnsclient all")
    w("--- show running-config dnsclient all ---")
    w(final_dns)
    syslog_still = (SYSLOG_IP in final_events) or (SYSLOG_IP in final_syslog)
    dns_still = DNS_IP in final_dns
    if syslog_still:
        w(f"  ✗ Syslog target {SYSLOG_IP} still present after cleanup.")
    else:
        w(f"  ✓ Syslog target removed.")
    if dns_still:
        w(f"  ✗ DNS target {DNS_IP} still present after cleanup.")
    else:
        w(f"  ✓ Manual DNS target removed.")

    chan.close()
    client.close()

    # ---- Summary ----
    syslog_set_ok = not syslog_set_failures
    syslog_clean_ok = not syslog_cleanup_failures and not syslog_still
    dns_set_ok = not dns_set_failures
    dns_clean_ok = not dns_cleanup_failures and not dns_still

    w("")
    w("=" * 72)
    w("SUMMARY")
    w("=" * 72)
    w(f"  Syslog Set    : {'PASS' if syslog_set_ok else f'FAIL ({len(syslog_set_failures)} lines)'}")
    w(f"  Syslog Read   : {'PASS' if syslog_present else 'FAIL'}")
    w(f"  Syslog Cleanup: {'PASS' if syslog_clean_ok else 'FAIL'}")
    w(f"  DNS Set       : {'PASS' if dns_set_ok else f'FAIL ({len(dns_set_failures)} lines)'}")
    w(f"  DNS Read      : {'PASS' if dns_present else 'FAIL'}")
    w(f"  DNS Cleanup   : {'PASS' if dns_clean_ok else 'FAIL'}")
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
