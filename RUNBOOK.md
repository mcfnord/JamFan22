# RUNBOOK — Operational Diagnostics

## Memory Leak / OOM Risk

Two OOM kills confirmed on the debug instance (May 2026): ~3.4 GB anon RSS at death vs. ~225 MB normal startup. GC heap was stable (~157 MB) — leak is in native/unmanaged memory.

**Mitigations in place:** `deploy-test-build.sh` sets `oom_score_adj=500` on the debug process (preferred kill victim); `/etc/systemd/system/jamfan22.service` sets `OOMScoreAdjust=-500` (production strongly protected).

**`[RSS-bg]` telemetry:** production logs to `output.log`; debug to `/tmp/jamfan-test-build/output.log`. If `threads=` climbs alongside RSS (~4 MB per stack), thread proliferation is the cause. If RSS grows while `gc=` stays flat, the leak is native.

**Suspected causes (in order):**
1. **Thread proliferation** — async loops in `harvest.cs` SSE connections not awaited cleanly, or thread-pool starvation. Watch `threads=` over a debug session.
2. **Unbounded static caches** — `GeolocationService.m_ipAddrToLatLong`, IpAnalyticsService caches, EncounterTracker session maps. Any `Dictionary` never evicted is a candidate.
3. **HttpClient response body buffering** — SSE/streaming connections with large unread payloads.
4. **LOH fragmentation** — large objects never compacted.

**Investigate:** run a debug session and watch `threads=` in `/tmp/jamfan-test-build/output.log` as RSS grows. Compare gcdumps with `DOTNET_ROOT=/root/.dotnet PATH="/root/.dotnet:/root/.dotnet/tools:$PATH" dotnet-gcdump report <file>`.

## Diagnosing Census Gaps

Run `sense-drops.py` first to identify dropout windows.

Check `output.log` for:
- **`[RSS-bg]`** — printed at end of each successful RefreshThreadTask cycle. Format: `[RSS-bg] <utc> <VmRSS>  gc=<kB>kB  threads=<N>`. Absence = task not completing.
- **`[WARN] Slow fetch`** — all URL fetches exceeded 5s.
- **`[WARN] RefreshThreadTask: HTTP timeout/cancel`** — HttpClient timeout caused restart.
- **`RefreshThreadTask restarted (count: N)`** — logged by JamulusListRefreshService.

If slow fetches: check `call-servers-php.service` on `24.199.107.192` — `journalctl -u call-servers-php.service | grep ON-DEMAND-REFRESH-FAIL`.
