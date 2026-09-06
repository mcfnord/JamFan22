# Fleet Server Analysis

## Strategic Notes (2026-06-13)

- **Bangkok/SEA:** Previously ran an AWS spot instance there; it kept getting revoked. Reluctant to commit to a full-year reserved instance. No fleet presence there despite ~880 unique GUIDs across 4 servers. Leave as a known gap for now.
- **Turin → Amsterdam:** Considering moving `92.4.218.204` (Oracle Rivoli/Turin, 5 repeat visitors) to Oracle Amsterdam, which has 222 unique GUIDs on a non-fleet server (`158.101.201.242`) and no current fleet presence.
- **MAX audio rollback test (2026-06-13):** A MAX audio feature (details in code) was suspected of causing audio quality problems. Rolled back on Studio D as an initial test. Freiheit queued for same rollback to rule out the feature as a cause.

## Performance Metrics

**Script:** `fleet-server-stats.py` — run anytime to refresh.

**Time-window rule:** Only measure from the first tick of the **youngest fleet server** so all servers are compared over an identical window. As of 2026-06-13 that is tick `1794769` = 2026-05-31 08:49 UTC (set by `18.170.218.139:22225`).

**Deduplication:** Rows in `census.csv` can repeat (minute, guid, server). Always deduplicate before computing time totals. Audible flag is OR'd across duplicates — if any row marks audible=1, that (minute, guid) is audible.

**Exclusions:** owner hashes + lobby-named GUIDs (from CLAUDE.md).

### Metric definitions

| Column | What it means | Quality |
|--------|--------------|---------|
| `Uniq` | Distinct GUIDs ever seen on the server | Good |
| `Repeat` | GUIDs that visited on 2+ distinct calendar days | **Best** |
| `Rpt%` | Repeat / Unique | Retention rate |
| `t≥1 h` | Hours where ≥1 non-lobby GUID present | OK |
| `t≥2 h` | Hours where ≥2 non-lobby GUIDs present | Good |
| `t≥2aud` | Hours where ≥2 audible GUIDs present | Good |

**Key insight:** Repeat visitors are the strongest signal. A server with many unique visitors but few repeats is a dead end — people tried it and left. Only repeat visitors build community.

### Snapshot — 2026-05-31 to 2026-06-13 (13 days)

```
Server                          Uniq  Repeat   Rpt%   t≥1 h   t≥2 h  t≥2aud
---------------------------------------------------------------------------
24.199.127.71:22224               50      13    26%    15.3    11.5     0.2   ← clear leader
50.116.25.151:22224               51       7    14%    17.6     6.4     0.0
92.4.218.204:22224                43       5    12%     2.6     0.9     0.0
130.61.155.141:22224              58       4     7%     6.6     3.0     0.0
92.4.218.204:22225                16       3    19%     1.5     0.7     0.0
18.170.218.139:22224              41       3     7%     3.9     2.2     0.0
132.226.27.144:22224              12       2    17%     5.8     4.1     0.0
130.61.155.141:22225               8       1    12%     0.3     0.0     0.0
18.170.218.139:22225              17       1     6%     1.4     0.8     0.0
50.116.25.151:22225                1       0     0%     0.2     0.0     0.0
172.239.129.32:22224              18       0     0%     2.0     1.3     0.0
172.239.129.32:22225               9       0     0%     0.5     0.0     0.0
132.226.27.144:22225               1       0     0%     0.0     0.0     0.0
147.182.199.22                        (no census data — not appearing in directory)
```

**Concern:** `172.239.129.32` (Akamai Virginia) — 27 total visitors, zero returned. `132.226.27.144:22225` and `50.116.25.151:22225` essentially empty.

---

## Datacenter / Traffic Adoption Analysis

Goal: find datacenters with active Jamulus traffic where we have no fleet server — these are the best candidates for expansion. Conversely, know which of our fleet servers are in the same datacenter as popular servers, because players from those servers may discover ours.

### Fleet server locations (as of 2026-06-13)

| IP | Provider | Region/City | Ports |
|----|----------|-------------|-------|
| `50.116.25.151` | Akamai (AS63949) | Texas US — Richardson | :22224, :22225 |
| `172.239.129.32` | Akamai (AS63949) | Virginia US — Ashburn | :22224, :22225 |
| `24.199.127.71` | DigitalOcean (AS14061) | California US — Santa Clara | :22224 |
| `147.182.199.22` | DigitalOcean (AS14061) | California US — Santa Clara | :22222 (no census data) |
| `130.61.155.141` | Oracle (AS31898) | Hesse DE — Frankfurt | :22224, :22225 |
| `92.4.218.204` | Oracle (AS31898) | Piedmont IT — Rivoli (Turin) | :22224, :22225 |
| `132.226.27.144` | Oracle (AS31898) | Arizona US — Phoenix | :22224, :22225 |
| `18.170.218.139` | AWS (AS16509) | eu-west-2 — London | :22224, :22225 |

**Providers covered:** Akamai, DigitalOcean, Oracle, AWS.

### Top non-fleet servers and their locations

Top 30 non-fleet servers by unique GUIDs (same 13-day window):

| Unique GUIDs | Server IP | Provider | Region/City | Notes |
|---:|----------|----------|-------------|-------|
| 519 | `82.153.225.55` | AS213426 Danny Lotz | — | Personal/home ASN — not a cloud DC |
| 305 | `150.230.146.17` | Oracle | Hesse DE — Frankfurt | **Same DC as our `130.61.155.141`** |
| 303 | `77.163.83.31` | KPN B.V. (AS1136) | Netherlands | Dutch residential/ISP |
| 298 | `5.94.205.146` | Fastweb (AS30722) | Italy | Italian ISP |
| 282 | `13.42.109.202` | AWS eu-west-2 | London UK | **Same DC as our `18.170.218.139`** |
| 242 | `43.208.146.31` | AWS ap-southeast-7 | Bangkok TH | **Gap — no fleet in SEA** |
| 238 | `51.91.165.236` | OVH (AS16276) | Hauts-de-France FR — Roubaix | **Gap — no fleet on OVH** |
| 228 | `147.50.242.54` | CLOUDFOREST (AS142299) | Bangkok TH | **Gap — no fleet in SEA** |
| 222 | `158.101.201.242` | Oracle | North Holland NL — Amsterdam | **Gap — fleet has Oracle Frankfurt but not Amsterdam** |
| 213 | `31.14.135.67` | Aruba (AS31034) | Italy | Italian hosting |
| 209 | `203.159.94.190` | Siamdata (AS56309) | Nonthaburi TH | **Gap — no fleet in SEA** |
| 205 | `103.253.73.244` | Siamdata (AS56309) | Thailand | **Gap** |
| 196 | `51.105.240.201` | Microsoft Azure (AS8075) | North Holland NL — Amsterdam | **Gap — no fleet on Azure** |
| 170 | `158.179.211.155` | — | — | — |
| 167 | `103.234.236.233` | — | — | — |
| 166 | `31.134.204.78` | A2B IP (AS51088) | Netherlands | |
| 147 | `163.172.13.94` | Scaleway (AS12876) | France | **Gap — no fleet on Scaleway** |
| 145 | `172.237.119.221` | Akamai | — | Same provider as fleet — check region |
| 144 | `130.61.131.111` | Oracle | Hesse DE — Frankfurt | **Same DC as our `130.61.155.141`** |
| 137 | `130.61.31.182` | Oracle | Hesse DE — Frankfurt | **Same DC as our `130.61.155.141`** |
| 133 | `178.208.186.114` | AS213426 Danny Lotz | — | Personal ASN |
| 127 | `31.134.204.78:22324` | A2B IP | Netherlands | (multi-port) |
| 119 | `80.211.236.204` | Aruba | Italy | Italian hosting |
| 118 | `31.134.204.78:22224` | A2B IP | Netherlands | |
| 116 | `3.126.153.135` | AWS eu-central-1 | Frankfurt DE | **Gap — fleet Frankfurt is Oracle, not AWS** |
| 111 | `57.129.46.253` | OVH | Hesse DE — Frankfurt | **Gap** |
| 110 | `154.215.14.108` | NZ Network (AS147176) | — | — |
| 107 | `18.171.105.145` | AWS eu-west-2 | London UK | **Same DC as our `18.170.218.139`** |
| 85 | `31.134.204.78:22424` | A2B IP | Netherlands | |

### Overlap — fleet servers sharing a datacenter with popular non-fleet servers

These are the fleet servers most likely to get cross-pollination from nearby popular servers.

**Oracle Frankfurt** (`130.61.155.141`)
- Co-located with `150.230.146.17` (305 unique), `130.61.131.111` (144), `130.61.31.182` (137)
- ~580 unique GUIDs on co-located servers vs our 4 repeat visitors
- Our server has exposure but isn't converting — worth investigating why retention is low

**AWS eu-west-2 London** (`18.170.218.139`)
- Co-located with `13.42.109.202` (282 unique), `18.171.105.145` (107)
- ~390 unique GUIDs on co-located servers vs our 3 repeat visitors
- Same concern — traffic is nearby but not converting

### Gaps — high-traffic regions with no fleet presence

Sorted roughly by opportunity size:

1. **Thailand / Bangkok** — `43.208.146.31` AWS (242), `147.50.242.54` CLOUDFOREST (228), `203.159.94.190` Siamdata (209), `103.253.73.244` Siamdata (205) — 880+ unique GUIDs across 4 servers, zero fleet presence. Biggest untapped cluster.

2. **OVH** — `51.91.165.236` Roubaix FR (238), `57.129.46.253` Frankfurt (111), plus Scaleway `163.172.13.94` (147) — European hosting that's very popular with Jamulus operators; we have none.

3. **Oracle Amsterdam** — `158.101.201.242` (222 unique). We have Oracle Frankfurt and Oracle Turin — Amsterdam is the obvious next Oracle region.

4. **Azure Amsterdam** — `51.105.240.201` (196 unique). No fleet presence on Microsoft infrastructure at all.

5. **AWS eu-central-1 Frankfurt** — `3.126.153.135` (116 unique). We have AWS London, but Frankfurt AWS is a separate region with its own popular server.

6. **Netherlands non-cloud** — `31.134.204.78` A2B IP (118+127+85 across ports), `77.163.83.31` KPN (303), `31.14.135.67` Aruba IT... Dutch traffic is huge but these are ISP/residential IPs not reproducible in a datacenter.

### Interpretation notes

- Personal/ISP ASNs (Danny Lotz AS213426, KPN AS1136, Fastweb AS30722) dominate raw unique GUIDs but are someone's home server — not a datacenter where you can spin up a fleet node.
- Cloud provider servers (Oracle, AWS, OVH, Azure) are the actionable expansion targets.
- The SEA cluster (Bangkok especially) is the largest untapped cloud datacenter.
- Our two best-performing servers (`24.199.127.71` DigitalOcean Santa Clara and `50.116.25.151` Akamai Texas) are in US datacenters where there's no notable competing non-fleet server traffic — they succeed on their own merits.
