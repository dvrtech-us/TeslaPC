'use strict';
/*
 * Drives teslapc/assets/PCMPlayerProcessor.js in Node with stub AudioWorkletProcessor,
 * registerProcessor and sampleRate. Simulated time: a source sends numbered PCM packets on a
 * schedule; a sink calls process() with 128-frame blocks at the context rate.
 *
 * Run: node teslapc/tests/js/pcm_player.test.js   (no npm packages; exit code 1 on failure)
 *
 * Frame n carries left = 1 + (n mod M), right = -left, so the output shows source order,
 * gaps, duplicates, channel swaps, and silence (0, 0).
 */
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const WORKLET = path.join(__dirname, '..', '..', 'assets', 'PCMPlayerProcessor.js');
const M = 1 << 22;            // frame number modulus; exact in float32
const BLOCK = 128;
const CONTEXT_RATE = 48000;

let Processor = null;
globalThis.AudioWorkletProcessor = class {
    constructor() {
        this.port = {
            messages: [],
            onmessage: null,
            postMessage(m) { this.messages.push(m); }
        };
    }
};
globalThis.registerProcessor = function (name, cls) {
    if (name !== 'pcm-player-processor') throw new Error('unexpected processor name ' + name);
    Processor = cls;
};
globalThis.sampleRate = CONTEXT_RATE;
vm.runInThisContext(fs.readFileSync(WORKLET, 'utf8'), { filename: WORKLET });
if (!Processor) throw new Error('worklet did not call registerProcessor');

function mulberry32(seed) {
    return function () {
        seed |= 0; seed = (seed + 0x6D2B79F5) | 0;
        let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

// Packet arrival schedule (times in context seconds).
function buildSchedule(c) {
    const srcRate = c.srcRate || CONTEXT_RATE;
    const effRate = srcRate * (1 + (c.drift || 0));
    const P = Math.round((c.packetMs || 10) * srcRate / 1000);
    const rnd = mulberry32(12345);
    const list = [];
    let lastArrival = 0;
    for (let i = 0; ; i++) {
        const produced = (i + 1) * P / effRate;
        if (produced > c.duration) break;
        if (c.silence && produced > c.silence[0] && produced <= c.silence[1]) continue;
        let arrival = produced;
        if (c.jitterMs) arrival += (c.jitterMs + (rnd() * 2 - 1) * c.jitterMs) / 1000;
        if (c.stall && produced > c.stall[0] && produced <= c.stall[0] + c.stall[1]) {
            arrival = c.stall[0] + c.stall[1];
        }
        if (arrival < lastArrival) arrival = lastArrival;   // TCP keeps order
        lastArrival = arrival;
        list.push({ arrival: arrival, first: i * P, frames: P });
    }
    return list;
}

function makePacket(first, frames) {
    const a = new Float32Array(frames * 2);
    for (let f = 0; f < frames; f++) {
        const v = 1 + ((first + f) % M);
        a[2 * f] = v;
        a[2 * f + 1] = -v;
    }
    return a.buffer;
}

function simulate(c) {
    globalThis.sampleRate = CONTEXT_RATE;
    const proc = new Processor({
        processorOptions: {
            channels: 2, bitsPerSample: 32, sampleFormat: 'float',
            sourceSampleRate: c.srcRate || CONTEXT_RATE
        }
    });
    const fade = proc.crossfadeFrames;
    const schedule = buildSchedule(c);
    const out = [new Float32Array(BLOCK), new Float32Array(BLOCK)];
    const blocks = Math.floor(c.duration * CONTEXT_RATE / BLOCK);
    const errors = [];
    function fail(msg) { if (errors.length < 5) errors.push(msg); }

    let p = 0;
    let lastTrims = 0;
    let exempt = 0;
    let sawBlend = true;       // a trim must produce at least one blended (crossfaded) frame
    let explained = false;     // a trim or silence happened since the last clean frame
    let prev = null;
    let outFrame = 0;
    // Standing buffer: minimum level per 250 ms bucket (0 when not playing).
    const bucketBlocks = Math.round(0.25 * CONTEXT_RATE / BLOCK);
    const bucketMins = [];
    let bucketMin = Infinity;

    for (let k = 0; k < blocks; k++) {
        const T = k * BLOCK / CONTEXT_RATE;
        while (p < schedule.length && schedule[p].arrival <= T) {
            proc.port.onmessage({ data: makePacket(schedule[p].first, schedule[p].frames) });
            p++;
        }
        proc.process([], [out], {});
        if (proc.trims > lastTrims) {
            if (!sawBlend) fail('trim before output frame ' + outFrame + ' had no crossfade');
            sawBlend = false;
            lastTrims = proc.trims;
            exempt = fade;
            explained = true;
        }
        for (let f = 0; f < BLOCK; f++, outFrame++) {
            const L = out[0][f], R = out[1][f];
            if (L === 0 && R === 0) { explained = true; continue; }
            if (!(L > 0) || R !== -L) {
                fail('frame ' + outFrame + ': channels wrong or swapped (L=' + L + ', R=' + R + ')');
                continue;
            }
            if (exempt > 0) {                              // inside a crossfade
                exempt--;
                if (!Number.isInteger(L)) sawBlend = true;
                if (exempt === 0 && !sawBlend) fail('trim before output frame ' + outFrame + ' had no crossfade');
                continue;
            }
            if (!c.checkOrder) continue;
            if (!Number.isInteger(L)) { fail('frame ' + outFrame + ': blended value outside a crossfade'); continue; }
            if (prev !== null) {
                const d = ((L - prev) % M + M) % M;
                if (d === 0) fail('frame ' + outFrame + ': duplicate of source frame ' + (L - 1));
                else if (d >= M / 2) fail('frame ' + outFrame + ': went backwards');
                else if (d > 1 && !explained) fail('frame ' + outFrame + ': gap of ' + (d - 1) + ' frames with no trim or underrun');
            }
            prev = L;
            explained = false;
        }
        const level = proc.state === 'playing' ? proc.levelFrames * 1000 / CONTEXT_RATE : 0;
        if (level < bucketMin) bucketMin = level;
        if ((k + 1) % bucketBlocks === 0) { bucketMins.push(bucketMin); bucketMin = Infinity; }
    }

    // Windowed (2 s = 8 buckets) minimum at each bucket end.
    const windowMin = [];
    for (let b = 7; b < bucketMins.length; b++) {
        windowMin.push({ t: (b + 1) * 0.25, ms: Math.min.apply(null, bucketMins.slice(b - 7, b + 1)) });
    }

    // Settled checks: from `from + 5 s` to `to`, the windowed minimum is within bounds.
    let lo = Infinity, hi = -Infinity;
    const recoveries = [];
    (c.settled || []).forEach(function (iv) {
        const samples = windowMin.filter(function (w) { return w.t >= iv[0] && w.t <= iv[1]; });
        const inBounds = function (w) {
            return (c.lo == null || w.ms >= c.lo) && (c.hi == null || w.ms <= c.hi);
        };
        // Recovery time: earliest sample after which every sample is in bounds.
        let rec = null;
        for (let i = samples.length - 1; i >= 0; i--) {
            if (!inBounds(samples[i])) break;
            rec = samples[i].t - iv[0];
        }
        recoveries.push(rec);
        samples.forEach(function (w) {
            if (w.t < iv[0] + 5) return;
            lo = Math.min(lo, w.ms); hi = Math.max(hi, w.ms);
            if (!inBounds(w)) fail('t=' + w.t.toFixed(2) + 's: standing buffer ' + w.ms.toFixed(1) + ' ms outside [' + c.lo + ', ' + c.hi + ']');
        });
    });

    // Stats messages: once a second, integers.
    const stats = proc.port.messages.filter(function (m) { return m && m.type === 'stats'; });
    const expectStats = Math.floor(blocks * BLOCK / CONTEXT_RATE);
    if (Math.abs(stats.length - expectStats) > 1) fail('expected ~' + expectStats + ' stats messages, got ' + stats.length);
    stats.forEach(function (s) {
        ['bufferedMs', 'minMs', 'underruns', 'trims'].forEach(function (key) {
            if (!Number.isInteger(s[key])) fail('stats.' + key + ' not an integer: ' + s[key]);
        });
    });
    const last = stats[stats.length - 1] || {};
    if (last.trims !== proc.trims || last.underruns !== proc.underruns) {
        // last post may precede a final event; tolerate only if counters moved after it
        if (last.trims > proc.trims || last.underruns > proc.underruns) fail('stats counters ahead of processor');
    }

    if (c.maxTrims != null && proc.trims > c.maxTrims) fail('trims ' + proc.trims + ' > bound ' + c.maxTrims);
    if (c.minTrims != null && proc.trims < c.minTrims) fail('trims ' + proc.trims + ' < expected ' + c.minTrims);
    if (c.maxUnderruns != null && proc.underruns > c.maxUnderruns) fail('underruns ' + proc.underruns + ' > bound ' + c.maxUnderruns);
    if (c.minUnderruns != null && proc.underruns < c.minUnderruns) fail('underruns ' + proc.underruns + ' < expected ' + c.minUnderruns);

    return {
        errors: errors, trims: proc.trims, underruns: proc.underruns,
        lo: lo, hi: hi, recoveries: recoveries, packets: schedule.length
    };
}

// Drift bound: the buffer moves 0.2% of real time, i.e. 2 ms per second. Each trim (or each
// underrun) absorbs about 100 ms (TRIM_ABOVE 200 -> TARGET 100, or START 100 -> 0), so over
// 600 s expect 600 * 2 / 100 = 12 events; the bound allows 14.
const DRIFT_BOUND = Math.ceil(600 * 0.002 / 0.1) + 2;

const cases = [
    { name: 'steady 10 ms packets, 30 s', duration: 30, settled: [[0, 30]], lo: 60, hi: 160,
      maxTrims: 0, maxUnderruns: 0, checkOrder: true },
    { name: '1.5 s stall then burst of the backlog', duration: 30, stall: [10, 1.5],
      settled: [[0, 10], [11.5, 30]], lo: 60, hi: 160,
      maxTrims: 2, minUnderruns: 1, maxUnderruns: 1, checkOrder: true },
    { name: 'source clock 0.2% fast, 10 min', duration: 600, drift: 0.002,
      settled: [[0, 600]], lo: 60, hi: 210,
      minTrims: 1, maxTrims: DRIFT_BOUND, maxUnderruns: 0, checkOrder: true },
    { name: 'source clock 0.2% slow, 10 min', duration: 600, drift: -0.002,
      settled: [[0, 600]], lo: null, hi: 160,
      maxTrims: 0, minUnderruns: 1, maxUnderruns: DRIFT_BOUND, checkOrder: true },
    { name: '+/-30 ms delivery jitter, 60 s', duration: 60, jitterMs: 30,
      settled: [[0, 60]], lo: 60, hi: 160, maxTrims: 0, maxUnderruns: 0, checkOrder: true },
    { name: '5 s audio, 15 s no packets, resume', duration: 40, silence: [5, 20],
      settled: [[0, 5], [20, 40]], lo: 60, hi: 160,
      maxTrims: 0, minUnderruns: 1, maxUnderruns: 1, checkOrder: true },
    { name: '1 ms packets, 30 s', duration: 30, packetMs: 1, settled: [[0, 30]], lo: 60, hi: 160,
      maxTrims: 0, maxUnderruns: 0, checkOrder: true },
    { name: '10 ms packets, 30 s', duration: 30, packetMs: 10, settled: [[0, 30]], lo: 60, hi: 160,
      maxTrims: 0, maxUnderruns: 0, checkOrder: true },
    { name: '100 ms packets, 30 s', duration: 30, packetMs: 100, settled: [[0, 30]], lo: 60, hi: 160,
      maxTrims: 0, maxUnderruns: 0, checkOrder: true },
    { name: 'source 44.1 kHz resampled to 48 kHz, 30 s', duration: 30, srcRate: 44100,
      settled: [[0, 30]], lo: 60, hi: 160, maxTrims: 0, maxUnderruns: 0, checkOrder: false }
];

let failed = 0;
cases.forEach(function (c) {
    const r = simulate(c);
    const ok = r.errors.length === 0;
    if (!ok) failed++;
    const rec = r.recoveries.map(function (x) { return x == null ? 'never' : x.toFixed(2) + 's'; }).join(', ');
    console.log((ok ? 'PASS ' : 'FAIL ') + c.name);
    console.log('     trims=' + r.trims + ' underruns=' + r.underruns +
        ' standing buffer (settled) ' + r.lo.toFixed(1) + '-' + r.hi.toFixed(1) + ' ms' +
        ' recovered after: ' + rec);
    r.errors.forEach(function (e) { console.log('     - ' + e); });
});
console.log(failed === 0 ? 'All ' + cases.length + ' cases passed.' : failed + ' of ' + cases.length + ' cases failed.');
process.exit(failed === 0 ? 0 : 1);
