#!/usr/bin/env python3
"""
Probe all non-fleet Jamulus servers for connectionless 1028 support.
Sends PROTMESSID_CLM_REQ_CHANNEL_LEVEL_LIST without establishing a named connection.
Servers that respond are 1028-capable and can be silence-sampled invisibly.
"""

import asyncio
import json
import socket
import struct
import urllib.request

FLEET_IPS = {
    '50.116.25.151', '24.199.127.71', '130.61.155.141',
    '172.239.129.32', '92.4.218.204', '132.226.27.144', '18.170.218.139'
}

DIRECTORY_URLS = [
    'anygenre1.jamulus.io:22124', 'anygenre2.jamulus.io:22224',
    'anygenre3.jamulus.io:22524', 'anygenre4.jamulus.io:22624',
    'rock.jamulus.io:22424',      'jazz.jamulus.io:22324',
    'classical.jamulus.io:22724',
]

TIMEOUT = 0.4  # seconds — enough for a round trip; fast fail if not supported


def jamulus_crc(data):
    crc = 0xFFFF
    for b in data:
        crc ^= b << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return (~crc) & 0xFFFF


def build_1028_frame():
    f = bytearray(9)
    f[0], f[1] = 0x00, 0x00   # TAG
    f[2], f[3] = 0x04, 0x04   # ID 1028 LE
    f[4]       = 0x00          # counter
    f[5], f[6] = 0x00, 0x00   # body length = 0
    crc = jamulus_crc(f[:7])
    f[7] = crc & 0xFF
    f[8] = (crc >> 8) & 0xFF
    return bytes(f)


FRAME = build_1028_frame()


def fetch_servers():
    servers = {}  # ip:port -> name
    for dirhost in DIRECTORY_URLS:
        url = f'http://24.199.107.192:5001/servers_data/{dirhost}/cached_data'
        try:
            with urllib.request.urlopen(url, timeout=10) as r:
                data = json.load(r)
            for s in data.get('servers_data', data):
                ip = s.get('ip')
                port = s.get('port')
                if not ip or not port:
                    continue
                if ip in FLEET_IPS:
                    continue
                key = f'{ip}:{port}'
                if key not in servers:
                    servers[key] = s.get('name', '')
        except Exception as e:
            print(f'  [fetch error] {dirhost}: {e}')
    return servers


def parse_levels(buf):
    if len(buf) < 9:
        return []
    body_len = buf[5] | (buf[6] << 8)
    levels = []
    if body_len > 0:
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
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.settimeout(TIMEOUT)
        sock.sendto(FRAME, (ip, port))
        data, _ = sock.recvfrom(256)
        sock.close()
        return data
    except socket.timeout:
        return None
    except Exception:
        return None


def main():
    print('Fetching server list...')
    servers = fetch_servers()
    print(f'Found {len(servers)} non-fleet servers. Probing with 1028 frame...\n')

    responding = []
    silent = []

    for i, (ipport, name) in enumerate(sorted(servers.items())):
        ip, port_str = ipport.rsplit(':', 1)
        port = int(port_str)
        buf = probe(ip, port)
        if buf is not None:
            levels = parse_levels(buf)
            quiet = all(l == 0 for l in levels)
            audible = sum(1 for l in levels if l > 0)
            responding.append((ipport, name, levels, quiet, audible))
            marker = '🔊' if not quiet else '🔇'
            print(f'{marker} RESPONDS  {ipport:25s}  levels={levels}  "{name}"')
        if (i + 1) % 20 == 0:
            print(f'  ... {i+1}/{len(servers)} probed')

    print(f'\n--- Results ---')
    print(f'Total non-fleet servers probed: {len(servers)}')
    print(f'Responded to connectionless 1028: {len(responding)}')
    if responding:
        print('\nCapable servers:')
        for ipport, name, levels, quiet, audible in responding:
            print(f'  {ipport:25s}  audible={audible}/{len(levels)}  "{name}"')
    else:
        print('\nNo non-fleet servers responded — all require a named connection (Ear).')


if __name__ == '__main__':
    main()
