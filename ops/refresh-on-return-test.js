// Exercises the refreshOnReturn block exactly as it appears in Client.cshtml.
// Extracts the real source, runs it against a stub DOM + controllable clock,
// and asserts the behaviour we care about. No network, no browser.
const fs = require('fs');
const vm = require('vm');

const FILE = '/root/JamFan22/JamFan22/Pages/Client.cshtml';
const src  = fs.readFileSync(FILE, 'utf8');

const start = src.indexOf('    let _lastReturnRefresh =');
const endTok = "window.addEventListener('online', refreshOnReturn);";
const end = src.indexOf(endTok);
if (start < 0 || end < 0) { console.error('FAIL: could not locate the block in ' + FILE); process.exit(1); }
const block = src.slice(start, src.indexOf('\n', end) + 1);

// ---- stub environment -------------------------------------------------------
let clock = 1_000_000;
const handlers = {};              // event name -> fn
const calls = { grid: 0, nearby: 0, hotties: 0 };
const listen = (ev, fn) => { handlers[ev] = fn; };

const sandbox = {
    _lastRenderAt: 0,
    document: { hidden: false, addEventListener: listen },
    window:   { addEventListener: listen },
    Date:     { now: () => clock },
    fetchAndRender:        () => { calls.grid++;    sandbox._lastRenderAt = clock; },
    fetchNearbyMusicians:  () => { calls.nearby++; },
    fetchHotties:          () => { calls.hotties++; },
};
vm.createContext(sandbox);
vm.runInContext(block, sandbox);

// ---- assertions -------------------------------------------------------------
let failed = 0;
function check(label, fn) {
    const before = { ...calls };
    fn();
    const fired = calls.grid - before.grid;
    return {
        firedGrid: fired,
        nearby: calls.nearby - before.nearby,
        hotties: calls.hotties - before.hotties,
        label,
    };
}
function expect(label, got, want) {
    const ok = got === want;
    if (!ok) failed++;
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}  (got ${got}, want ${want})`);
}

// every listener the patch is supposed to install
for (const ev of ['visibilitychange', 'pageshow', 'focus', 'online']) {
    expect(`listener installed: ${ev}`, typeof handlers[ev], 'function');
}

// 1. load-time pageshow/focus must NOT duplicate the fetches the page just made
let r = check('load', () => { handlers.pageshow(); handlers.focus(); });
expect('1. pageshow+focus at load -> no extra grid fetch', r.firedGrid, 0);
expect('1. pageshow+focus at load -> no extra nearby fetch', r.nearby, 0);

// 2. the real case: tab backgrounded for hours, timers frozen, then looked at
clock += 3 * 3600 * 1000;
sandbox.document.hidden = true;
r = check('hidden', () => handlers.visibilitychange());
expect('2a. visibilitychange while still hidden -> nothing', r.firedGrid, 0);
sandbox.document.hidden = false;
r = check('return', () => handlers.visibilitychange());
expect('2b. tab visible after 3h -> grid refreshes', r.firedGrid, 1);
expect('2b. tab visible after 3h -> nearby refreshes', r.nearby, 1);
expect('2b. tab visible after 3h -> hotties refresh', r.hotties, 1);

// 3. visibilitychange and focus both fire on the same return: only one refresh
r = check('dup', () => handlers.focus());
expect('3. focus 0ms after that -> no second refresh', r.firedGrid, 0);

// 4. quick alt-tab while the 15s poll is keeping up: nothing to do
clock += 6000;                       // past the 5s return-throttle
sandbox._lastRenderAt = clock - 1000; // ...but the poll just rendered
r = check('fresh', () => handlers.focus());
expect('4. alt-tab with fresh data -> no refresh', r.firedGrid, 0);

// 5. laptop woke and the network came back, data is stale
clock += 60000;
r = check('online', () => handlers.online());
expect('5. online event with 61s-old data -> refreshes', r.firedGrid, 1);

// 6. bfcache restore (Back button) after a long detour
clock += 600000;
r = check('bfcache', () => handlers.pageshow());
expect('6. pageshow after 10min -> refreshes', r.firedGrid, 1);

console.log(failed ? `\n${failed} FAILED` : '\nall checks passed');
process.exit(failed ? 1 : 0);
