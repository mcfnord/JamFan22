#!/usr/bin/env python3
"""
Probe fleet servers with 1014+1028 on the same socket, print name→slot→level.
Run this while watching the live UI to confirm the slot-mapping fix is working.
  python3 fleet-probe.py [ip:port] [--loop]
Default: probe all fleet servers once. --loop repeats every 5s.
"""

import socket, struct, sys, time

FLEET_SERVERS_FILE = "JamFan22/data/fleet-server-ips.txt"
TIMEOUT = 2.0  # seconds


def jamulus_crc(data):
    crc = 0xFFFF
    for b in data:
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return (~crc) & 0xFFFF


def build_frame(id_lo, id_hi):
    f = bytearray(9)
    f[0], f[1] = 0x00, 0x00
    f[2], f[3] = id_lo, id_hi
    f[4] = 0x00
    f[5], f[6] = 0x00, 0x00
    crc = jamulus_crc(f[:7])
    f[7] = crc & 0xFF
    f[8] = (crc >> 8) & 0xFF
    return bytes(f)


FRAME_1014 = build_frame(0xF6, 0x03)  # CLM_REQ_CONN_CLIENTS_LIST
FRAME_1028 = build_frame(0x04, 0x04)  # CLM_REQ_CHANNEL_LEVEL_LIST


def parse_1013(buf):
    """Returns list of (channelId, name) from a 1013 response."""
    result = []
    if len(buf) < 9 or not (buf[2] == 0xF5 and buf[3] == 0x03):
        return result
    body_len = buf[5] | buf[6] << 8
    pos, end = 7, min(7 + body_len, len(buf) - 2)
    while pos + 16 <= end:
        channel_id = buf[pos]; pos += 12
        if pos + 2 > end: break
        name_len = buf[pos] | buf[pos+1] << 8; pos += 2
        if pos + name_len > end: break
        name = buf[pos:pos+name_len].decode("utf-8", errors="replace"); pos += name_len
        if pos + 2 > end: break
        city_len = buf[pos] | buf[pos+1] << 8; pos += 2 + city_len
        result.append((channel_id, name))
    return result


def parse_levels(buf):
    """Returns list of levels from a 1015 response (index = channel slot)."""
    levels = []
    if len(buf) < 9:
        return levels
    body_len = buf[5] | buf[6] << 8
    end = min(7 + body_len, len(buf) - 2)
    for i in range(7, end):
        lo = buf[i] & 0x0F
        levels.append(lo)
        hi = (buf[i] >> 4) & 0x0F
        if hi == 0x0F:
            break
        levels.append(hi)
    return levels


def probe(ip, port):
    """Send 1014 then 1028 on the same socket. Returns (clients, levels) or raises."""
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.settimeout(TIMEOUT)
        s.connect((ip, port))

        # Step 1: 1014 → 1013 (who is here, by slot)
        s.send(FRAME_1014)
        buf1013 = s.recv(4096)
        clients = parse_1013(buf1013)

        # Step 2: 1028 → 1015 (level nibbles by slot)
        s.send(FRAME_1028)
        buf1015 = s.recv(4096)
        levels = parse_levels(buf1015)

    return clients, levels


def probe_and_print(ip, port):
    addr = f"{ip}:{port}"
    try:
        clients, levels = probe(ip, port)
    except socket.timeout:
        print(f"  {addr}  NO RESPONSE")
        return
    except Exception as e:
        print(f"  {addr}  ERROR: {e}")
        return

    if not clients and not levels:
        print(f"  {addr}  (empty)")
        return

    print(f"  {addr}  clients={len(clients)}  level_slots={len(levels)}")
    if clients:
        for (slot, name) in sorted(clients):
            level = levels[slot] if slot < len(levels) else "?"
            audible = "AUDIBLE" if isinstance(level, int) and level > 0 else "silent"
            print(f"    slot {slot:2d}  level={level:>2}  {audible}  {name!r}")
    else:
        print(f"    (no 1013 clients — raw levels: {levels})")


def load_fleet_servers(target=None):
    if target:
        parts = target.split(":")
        return [(parts[0], int(parts[1]))]
    servers = []
    try:
        with open(FLEET_SERVERS_FILE) as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith('#'):
                    continue
                parts = line.split(':')
                if len(parts) >= 2:
                    try:
                        servers.append((parts[0], int(parts[1])))
                    except ValueError:
                        pass
    except FileNotFoundError:
        print(f"Cannot find {FLEET_SERVERS_FILE} — run from /root/JamFan22/")
        sys.exit(1)
    return servers


if __name__ == "__main__":
    args = sys.argv[1:]
    loop = "--loop" in args
    args = [a for a in args if a != "--loop"]
    target = args[0] if args else None

    servers = load_fleet_servers(target)

    while True:
        print(f"\n=== {time.strftime('%H:%M:%S')} ===")
        for ip, port in servers:
            probe_and_print(ip, port)
        if not loop:
            break
        time.sleep(5)
