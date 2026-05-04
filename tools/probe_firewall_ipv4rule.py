"""
Firewall ipv4rule write probe.

Mirrors the CLI that ScalanceCliCommands.BuildCreateFirewallRule produces
for a minimal "VLAN1 -> VLAN2 acc 0.0.0.0/0 -> 0.0.0.0/0" rule, then
sends it line-by-line to the real device, checks each response for
`% Invalid`, reads the rule list back, and cleans up by deleting only
the rule we created (looked up by idx diff against the pre-write
snapshot — never `no ipv4rule all`).

Goal: prove BuildCreateFirewallRule's output is valid CLI on the live
S615 firmware (V08.x) without modifying the App. If every line is
accepted, the App can write firewall rules with confidence.

Verified against PH_SCALANCE-S615-CLI_76 sec 12.3.4.31 p. 627-629.
"""

import os
import re
import sys
import time

HOST = "192.168.1.1"
USER = "admin"
PASS = "Industry4.0"

OUT_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "probe_firewall_output.log")

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


# Probe rule: VLAN1 -> VLAN2, allow all, with a unique comment so it can be
# found unambiguously even if the show-output parser is fuzzy.
PROBE_COMMENT = "probe2026fw"

PROBE_LINES = [
    "configure terminal",
    "firewall",
    f"ipv4rule from vlan 1 to vlan 2 srcip 0.0.0.0/0 dstip 0.0.0.0/0 action acc service all comment {PROBE_COMMENT}",
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


# Best-effort idx extractor. We treat the line as "matched" if it contains
# our PROBE_COMMENT and pick the first integer token on the line as idx.
# If parsing fails we fall back to "the new idx is the largest one in the
# post-snapshot that wasn't in the pre-snapshot" — see find_new_idx().
def collect_idxs(text, comment_filter=None):
    found = []
    for raw in text.splitlines():
        line = raw.strip()
        if not line:
            continue
        if comment_filter and comment_filter not in line:
            # We still want all idxs for diff mode; only filter when explicitly asked.
            pass
        # Look for a leading integer (1-128 per manual sec 12.3.4.31).
        m = re.match(r"^\s*(\d{1,3})\b", line)
        if not m:
            continue
        idx = int(m.group(1))
        if 1 <= idx <= 128:
            found.append((idx, line))
    return found


def find_new_idx(pre_text, post_text, comment):
    """Return (idx, source) where source is 'comment' or 'diff' or None."""
    # Preferred: locate by unique comment.
    for raw in post_text.splitlines():
        if comment in raw:
            m = re.match(r"^\s*(\d{1,3})\b", raw.strip())
            if m:
                idx = int(m.group(1))
                if 1 <= idx <= 128:
                    return idx, "comment"
    # Fallback: idx diff (exactly one new entry).
    pre = {i for i, _ in collect_idxs(pre_text)}
    post = {i for i, _ in collect_idxs(post_text)}
    new = sorted(post - pre)
    if len(new) == 1:
        return new[0], "diff"
    return None, None


def main():
    paramiko = ensure_paramiko()
    out = open(OUT_PATH, "w", encoding="utf-8", newline="\n")

    def w(s=""):
        print(s)
        out.write(s + "\n")
        out.flush()

    w(f"=== firewall ipv4rule probe to {USER}@{HOST} at {time.strftime('%Y-%m-%d %H:%M:%S')} ===")
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

    def show_rules():
        chan.send(b"show firewall ip-rules ipv4\n")
        ok, buf = read_until_prompt(chan, timeout=20)
        return strip_ansi(buf).decode("utf-8", errors="replace")

    # ---- Step 0: snapshot existing rules ----
    w("=" * 72)
    w("Step 0 — Snapshot existing firewall ipv4 rules (BEFORE write)")
    w("=" * 72)
    pre_text = show_rules()
    w(pre_text)
    pre_idxs = sorted({i for i, _ in collect_idxs(pre_text)})
    w(f"  pre-existing idxs: {pre_idxs} ({len(pre_idxs)} rules)")
    w("")

    # ---- Step 1: send the BuildCreateFirewallRule-equivalent command set ----
    w("=" * 72)
    w("Step 1 — Send BuildCreateFirewallRule output for VLAN1→VLAN2 acc")
    w("=" * 72)
    set_failures = []
    for ln in PROBE_LINES:
        ok, invalid, text = send_line(ln, label="set")
        if invalid or not ok:
            set_failures.append((ln, "invalid" if invalid else "no-prompt", text[-300:]))

    w("")
    if set_failures:
        w(f"✗ {len(set_failures)} line(s) of the BuildCreateFirewallRule batch failed:")
        for ln, reason, snippet in set_failures:
            w(f"   FAIL [{reason}] {ln}")
            w(f"        device said: {snippet[-200:]}")
    else:
        w("✓ Every line of the BuildCreateFirewallRule batch was accepted by the device.")

    # ---- Step 2: read back ----
    w("")
    w("=" * 72)
    w("Step 2 — Read back firewall ipv4 rules (AFTER write)")
    w("=" * 72)
    post_text = show_rules()
    w(post_text)
    post_idxs = sorted({i for i, _ in collect_idxs(post_text)})
    w(f"  post-write idxs: {post_idxs} ({len(post_idxs)} rules)")
    contains_probe = PROBE_COMMENT in post_text
    w("")
    if contains_probe:
        w(f"✓ Read-back contains comment '{PROBE_COMMENT}'.")
    else:
        w(f"⚠ Read-back does NOT show comment '{PROBE_COMMENT}' verbatim — show output may have truncated/quoted it differently. Will fall back to idx-diff for cleanup.")

    new_idx, idx_source = find_new_idx(pre_text, post_text, PROBE_COMMENT)
    if new_idx is None:
        w(f"✗ Could not determine new rule idx (pre={pre_idxs}, post={post_idxs}). ABORTING cleanup to avoid wiping unrelated rules.")
        w(f"  Manual cleanup needed: log into WBM and remove the VLAN1→VLAN2 acc rule with comment '{PROBE_COMMENT}'.")
        chan.close(); client.close()
        # Final summary (failed)
        w("")
        w("=" * 72); w("SUMMARY"); w("=" * 72)
        w(f"  Set    : {'PASS' if not set_failures else f'FAIL ({len(set_failures)} lines)'}")
        w(f"  Read   : {'PASS' if contains_probe else 'PARTIAL'}")
        w(f"  Cleanup: SKIPPED — manual WBM removal required")
        w(f"\nFull log: {OUT_PATH}")
        return

    w(f"  → new rule idx = {new_idx} (source: {idx_source})")

    # ---- Step 3: cleanup ----
    w("")
    w("=" * 72)
    w(f"Step 3 — Cleanup (no ipv4rule idx {new_idx})")
    w("=" * 72)
    cleanup_lines = [
        "configure terminal",
        "firewall",
        f"no ipv4rule idx {new_idx}",
        "end",
        "write startup-config",
    ]
    cleanup_failures = []
    for ln in cleanup_lines:
        ok, invalid, text = send_line(ln, label="cleanup")
        if invalid or not ok:
            cleanup_failures.append((ln, "invalid" if invalid else "no-prompt", text[-300:]))

    if cleanup_failures:
        w("")
        w(f"! {len(cleanup_failures)} cleanup line(s) failed:")
        for ln, reason, snippet in cleanup_failures:
            w(f"   FAIL [{reason}] {ln}")
            w(f"        device said: {snippet[-200:]}")

    # ---- Step 4: verify cleanup ----
    w("")
    w("=" * 72)
    w("Step 4 — Verify cleanup (rule list back to pre-state)")
    w("=" * 72)
    final_text = show_rules()
    w(final_text)
    final_idxs = sorted({i for i, _ in collect_idxs(final_text)})
    still_present = (PROBE_COMMENT in final_text) or (new_idx in final_idxs)
    w(f"  final idxs: {final_idxs}")
    if still_present:
        w(f"✗ Probe rule still present after cleanup. Manual WBM cleanup needed (idx={new_idx}).")
    elif final_idxs != pre_idxs:
        w(f"⚠ Cleanup removed our idx but rule list differs from pre-state — pre={pre_idxs} final={final_idxs}. Investigate.")
    else:
        w(f"✓ Probe rule removed cleanly. Rule list back to pre-state.")

    chan.close()
    client.close()

    # Final summary
    w("")
    w("=" * 72)
    w("SUMMARY")
    w("=" * 72)
    w(f"  Set    : {'PASS' if not set_failures else f'FAIL ({len(set_failures)} lines)'}")
    w(f"  Read   : {'PASS' if contains_probe or new_idx is not None else 'FAIL'}")
    w(f"  Cleanup: {'PASS' if not cleanup_failures and not still_present and final_idxs == pre_idxs else 'FAIL/PARTIAL'}")
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
