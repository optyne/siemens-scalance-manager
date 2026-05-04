"""
Dump ALL `show running-config <type> all` outputs for every module type
S615 V08 advertises in `show running-config list-types`. We use this to
verify our C# CLI builders against the format the device itself emits.

Output:
  tools/running_config_dump.txt   ← human-readable dump per module
"""

import os
import re
import sys
import time

HOST = "192.168.1.1"
USER = "admin"
PASS = "Industry4.0"

OUT_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "running_config_dump.txt")

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


# Module types reported by `show running-config list-types` on the real
# S615 V08. Hardcoded so we can run without first probing.
MODULES = [
    "syslog", "dhcp", "ssh", "ssl", "ip", "snmp", "sntp", "http",
    "auto-logout", "time", "ntp", "auto-save", "events", "firewall",
    "firewallnat", "openvpn", "sinemarc", "proxyserver", "srs", "ipsec",
    "ddnsclient", "dnsclient", "dnsproxy", "dnsserver", "modem", "vrrp3",
    "stp", "lldp", "cloudconnector", "panel-button", "radius", "umac",
    "connectioncheck", "tcpevent",
    # dynamic — vlan ids and interface names we know about from prior probes:
    "vlan 1",
    "vlan 2",
    "interface vlan 1",
    "interface vlan 2",
    "interface ppp 2",
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

    w(f"=== running-config dump from {USER}@{HOST} at {time.strftime('%Y-%m-%d %H:%M:%S')} ===")
    w("")

    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(HOST, username=USER, password=PASS, timeout=8,
                   look_for_keys=False, allow_agent=False)
    chan = client.invoke_shell(term="vt100", width=200, height=200)
    read_until_prompt(chan, timeout=15)

    for mod in MODULES:
        cmd = f"show running-config {mod} all"
        w("=" * 72)
        w(f">>> {cmd}")
        w("=" * 72)
        chan.send((cmd + "\n").encode())
        ok, buf = read_until_prompt(chan, timeout=20)
        text = strip_ansi(buf).decode("utf-8", errors="replace")
        # Strip the echoed command line and trailing prompt for cleaner output.
        lines = text.split("\n")
        cleaned = []
        for ln in lines:
            stripped_ln = ln.rstrip("\r")
            if stripped_ln.strip() == cmd:
                continue
            if PROMPT_RE.search(stripped_ln.encode()):
                continue
            cleaned.append(stripped_ln)
        w("\n".join(cleaned).rstrip())
        w("")

    chan.close()
    client.close()
    out.close()
    print(f"\nFull dump: {OUT_PATH}")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(f"FATAL: {type(e).__name__}: {e}")
        sys.exit(1)
