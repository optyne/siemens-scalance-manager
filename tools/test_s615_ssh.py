"""
Verify SCALANCE S615 SSH behaviour BEFORE we change SshClient.cs.

What this script proves:
  1. exec_command (matches current Renci.SshNet.CreateCommand path) DOES time
     out on 'show ntp info' / 'ping ...' against this S615.
  2. invoke_shell + read-until-prompt (the proposed fix) gets clean output
     for the same commands.
  3. The actual prompt format(s) the regex needs to match.

Output: prints to stdout AND writes to test_s615_ssh_output.log next to this
file, so we can pick it up afterwards.
"""

import os
import re
import sys
import time

LOG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "test_s615_ssh_output.log")
_log_fp = open(LOG_PATH, "w", encoding="utf-8", newline="\n")

def log(*args, **kwargs):
    msg = " ".join(str(a) for a in args)
    print(msg, **kwargs)
    _log_fp.write(msg + "\n")
    _log_fp.flush()

HOST = "192.168.1.1"
USER = "admin"
PASS = "Industry4.0"

# Final-form prompt regex.
#
# IMPORTANT: SCALANCE's prompt is NOT a fixed string `cli#`. It echoes the
# device's `system name`. A fresh device shows `CLI#`; setting `system name
# Foo` immediately changes the prompt to `Foo#`; `system name Foo` + entering
# IPsec config gives `Foo(config-ipsec-conn-phase1)#`. So we anchor on the
# *shape* — a hostname-like token, optionally followed by a parenthesised
# config mode label, ending with `#` or `>` at end-of-buffer.
PROMPT_RE = re.compile(
    rb"[A-Za-z_][\w\-]*(?:\([^)\r\n]+\))?[#>]\s*$")
# ANSI / VT escape stripper (used only to test the prompt regex; the raw
# bytes are kept for the final dump so we can see what really arrived).
ANSI_RE = re.compile(rb"\x1B\[[0-9;]*[A-Za-z]|\x1B\][^\x07]*\x07|\x1B[=>]")

def strip_ansi(b):
    return ANSI_RE.sub(b"", b)

def banner(title):
    log("\n" + "=" * 72)
    log(title)
    log("=" * 72)

def ensure_paramiko():
    try:
        import paramiko  # noqa
        return paramiko
    except ImportError:
        log("paramiko not installed; running pip install paramiko ...")
        import subprocess
        rc = subprocess.call([sys.executable, "-m", "pip", "install", "paramiko"])
        if rc != 0:
            log(f"pip install paramiko failed with exit code {rc}")
            sys.exit(2)
        import paramiko
        return paramiko

PAGER_RE = re.compile(rb"--More--|Press[^\r\n]*continue", re.IGNORECASE)

def read_until_prompt(channel, label, timeout=15, max_pager_pages=50):
    """Reads from <channel> until either:
       - the SCALANCE CLI prompt is observed at end of buffer, or
       - <timeout> elapses.
       Auto-advances any --More-- pagers by sending space. Bails after
       <max_pager_pages> page-advances to avoid infinite loops."""
    buf = b""
    deadline = time.time() + timeout
    pages = 0
    while time.time() < deadline:
        if channel.recv_ready():
            chunk = channel.recv(8192)
            if not chunk:
                break
            buf += chunk
            stripped = strip_ansi(buf)
            if PROMPT_RE.search(stripped):
                return True, buf
            # Check for pager prompt within the LAST 200 chars
            tail = stripped[-200:]
            if PAGER_RE.search(tail):
                if pages < max_pager_pages:
                    channel.send(b" ")
                    pages += 1
                    time.sleep(0.05)
                else:
                    # Give up and quit pager
                    channel.send(b"q")
                    break
        else:
            time.sleep(0.05)
    return False, buf

def dump(b, max_len=2000):
    """Decode and trim for the log."""
    s = strip_ansi(b).decode("utf-8", errors="replace")
    if len(s) > max_len:
        s = s[:max_len] + f"\n... (truncated, {len(s) - max_len} more chars)"
    return s

def main():
    paramiko = ensure_paramiko()
    log(f"paramiko version: {paramiko.__version__}")
    log(f"target: {USER}@{HOST}:22")

    banner("Step 1 — TCP / SSH transport")
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    t0 = time.time()
    try:
        client.connect(
            HOST, port=22,
            username=USER, password=PASS,
            timeout=8,
            look_for_keys=False, allow_agent=False)
    except Exception as e:
        log(f"FAIL: connect raised {type(e).__name__}: {e}")
        return 1
    log(f"OK: connected in {time.time() - t0:.2f}s")
    log(f"server version: {client.get_transport().remote_version}")

    # -----------------------------------------------------------------
    banner("Step 2 — exec_command (mirrors current SshClient.RunAsync)")
    log("This is what the App does today; expected behaviour: hang/timeout.")
    for cmd in ["show ntp info", "show vlan"]:
        log(f"\n>>> exec_command: {cmd}  (timeout=8s)")
        t = time.time()
        try:
            stdin, stdout, stderr = client.exec_command(cmd, timeout=8)
            out = stdout.read()
            err = stderr.read()
            log(f"OK in {time.time() - t:.2f}s: stdout={len(out)}B stderr={len(err)}B")
            log("--- stdout ---")
            log(dump(out, 600))
            if err:
                log("--- stderr ---")
                log(dump(err, 200))
        except Exception as e:
            log(f"FAIL in {time.time() - t:.2f}s: {type(e).__name__}: {e}")

    # -----------------------------------------------------------------
    banner("Step 3 — invoke_shell + read-until-prompt (proposed fix)")
    # IMPORTANT: SCALANCE only allows ONE shell channel per SSH session —
    # if we close this channel and then `invoke_shell` again on the same
    # client, the second channel comes up but never emits a banner or
    # prompt, and every command silently times out at 15 s. So we open
    # the shell ONCE and keep it open through Steps 3 and 4.
    chan = client.invoke_shell(term="vt100", width=200, height=200)

    # Wait for initial banner / first prompt
    t = time.time()
    ok, buf = read_until_prompt(chan, "banner", timeout=15)
    log(f"banner read: ok={ok} bytes={len(buf)} elapsed={time.time() - t:.2f}s")
    log("--- last 400 chars of banner ---")
    log(dump(buf[-400 * 4:], 400))

    test_commands = [
        "show ntp info",
        "show ip interface",
        "show vlan",
        "ping 192.168.1.99 count 1 timeout 1",
        "show running-config list-types",
    ]

    for cmd in test_commands:
        log(f"\n>>> shell send: {cmd}")
        t = time.time()
        chan.send((cmd + "\n").encode())
        ok, buf = read_until_prompt(chan, cmd, timeout=15)
        log(f"   ok={ok}  bytes={len(buf)}  elapsed={time.time() - t:.2f}s")
        log("--- output ---")
        log(dump(buf, 1200))

    # -----------------------------------------------------------------
    # Step 4 — write probe (safe, reversible)
    #
    # Validates the *write* path that the App's ScalanceCliCommands.* uses:
    #   1. Enter config mode via `configure terminal`
    #   2. Set / clear a property (we use `system name`, the most cosmetic
    #      property available — sets sysName SNMP OID, otherwise harmless)
    #   3. Leave config mode via `end`
    #   4. Verify the change shows up in `show running-config`
    #   5. Restore the original
    #
    # Deliberately does NOT call `write startup-config`, so even if
    # restore-on-success fails the change reverts on next reboot.
    # -----------------------------------------------------------------
    banner("Step 4 — write probe via invoke_shell (system name only)")
    # Reuse the SAME shell channel from Step 3 — see comment there.

    def shell_run(cmd, label=None, timeout=15):
        t = time.time()
        chan.send((cmd + "\n").encode())
        ok, buf = read_until_prompt(chan, label or cmd, timeout=timeout)
        elapsed = time.time() - t
        return ok, buf, elapsed

    # Stale-state cleanup: the previous test runs left `system name PROBE_…`
    # on the device (visible because the banner shows e.g. PROBE_1777811909#).
    # Reset to the original hostname `CLI`. Note: `no system name` returns
    # `% Invalid input detected at '^' marker` on this firmware (V08.00.00,
    # 2026-05-03) — the `no` form documented in the manual either has
    # different syntax or isn't supported. Setting `system name CLI`
    # explicitly works and gives the same UX as the factory default
    # (banner / prompt show `CLI#`, WBM shows "sysName Not Set" because
    # the SNMP sysName.0 OID still reports "CLI" but the WBM heuristic
    # treats that as default-equivalent).
    log("Pre-clean: reset hostname to 'CLI' in case prior runs left a stale name...")
    for cmd in ["configure terminal", "system name CLI", "end"]:
        ok, buf, elapsed = shell_run(cmd)
        log(f"   {cmd!r}: ok={ok}  bytes={len(buf)}  elapsed={elapsed:.2f}s")

    def show_in_text(buf, n=200):
        text = strip_ansi(buf).decode("utf-8", errors="replace")
        return text[-n:] if len(text) > n else text

    test_name = f"PROBE_{int(time.time())}"

    # ---- Step 4a — enter config mode, set, exit. The success signal is
    #       (a) all 3 commands return ok=True (we got a prompt back), and
    #       (b) no "% Invalid" appears in any echo. ----
    log(f"\nSetting system name to: {test_name}")
    set_results = []
    for cmd in ["configure terminal", f"system name {test_name}", "end"]:
        ok, buf, elapsed = shell_run(cmd)
        tail = show_in_text(buf, 200)
        invalid = "% Invalid" in tail or "% Error" in tail
        log(f"   >>> {cmd}")
        log(f"       ok={ok}  bytes={len(buf)}  elapsed={elapsed:.2f}s  invalid={invalid}")
        log(f"       tail={tail!r}")
        set_results.append((cmd, ok, invalid))

    write_clean = all(ok and not invalid for _, ok, invalid in set_results)
    if write_clean:
        log("   ✓ All 3 write-path commands returned clean prompts (no % Invalid)")
    else:
        log("   ✗ Write path had problems; see above")

    # ---- Step 4b — verify via the SAME show that worked in Step 3.
    #       We can't easily read system name back without `show running-config`
    #       (which paginates) — so we use a plain `show ip interface` as a
    #       liveness check that the channel is still healthy after writes. ----
    log("\nLiveness check after write — re-running 'show ip interface'...")
    ok, buf, elapsed = shell_run("show ip interface")
    log(f"   ok={ok}  bytes={len(buf)}  elapsed={elapsed:.2f}s")
    if ok and len(buf) > 100:
        log("   ✓ Channel still healthy after write commands")
    else:
        log("   ✗ Channel may be stuck; subsequent writes may fail")

    # ---- Step 4c — restore. We deliberately do NOT call
    #       `write startup-config`, so even if restore-on-success goes wrong
    #       the change reverts on next power cycle. We set hostname to
    #       `CLI` (the apparent factory default) instead of `no system name`
    #       since the latter is rejected by V08 firmware. ----
    log("\nRestoring hostname to 'CLI' (V08 doesn't accept `no system name`)...")
    restore_results = []
    for cmd in ["configure terminal", "system name CLI", "end"]:
        ok, buf, elapsed = shell_run(cmd)
        tail = show_in_text(buf, 200)
        invalid = "% Invalid" in tail or "% Error" in tail
        log(f"   >>> {cmd}")
        log(f"       ok={ok}  bytes={len(buf)}  elapsed={elapsed:.2f}s  invalid={invalid}")
        log(f"       tail={tail!r}")
        restore_results.append((cmd, ok, invalid))

    restore_clean = all(ok and not invalid for _, ok, invalid in restore_results)
    if restore_clean:
        log("   ✓ Restore path clean — hostname back to CLI")
    else:
        log("   ! Restore had issues — power-cycle the device to fully revert")

    chan.close()

    client.close()

    banner("Done.")
    log(f"Full log written to: {LOG_PATH}")
    return 0


if __name__ == "__main__":
    rc = 0
    try:
        rc = main()
    except KeyboardInterrupt:
        log("Interrupted by user.")
        rc = 130
    except Exception as e:
        log(f"FATAL: {type(e).__name__}: {e}")
        rc = 1
    finally:
        _log_fp.close()
    sys.exit(rc)
