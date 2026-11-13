/*
 * PCM player for /ws/audio (AudioWorklet).
 *
 * Packets of interleaved PCM arrive on this.port. They are decoded to float, resampled per
 * packet when the context rate differs from the source rate, and written to a fixed ring
 * buffer of 1 s. process() reads 128-frame blocks from it.
 *
 * Buffer control (phase 1 of documentation/planning/Streaming/2026-11-13-bounded-audio-latency.md):
 *   warming  - output silence until START_MS plus one packet is buffered.
 *   playing  - output audio. If a block cannot be filled: one underrun, go to starved.
 *   starved  - transient; becomes warming at the next block.
 *   The minimum buffered level is tracked over MEASURE_WINDOW_MS (8 buckets) while playing.
 *   Trim:     windowed minimum > TRIM_ABOVE_MS  -> skip (minMs - TARGET_MS) with a 5 ms crossfade.
 *   Hard:     instantaneous level > HARD_MAX_MS -> skip down to TARGET_MS with the same crossfade.
 *   Overflow: a write that does not fit keeps the newest TARGET_MS (counted as a trim).
 *   The window resets on every state change and after every trim.
 * Stats { type: 'stats', bufferedMs, minMs, underruns, trims } are posted once a second.
 *
 * Classic script (no ES modules, no optional chaining, no private fields) so old Chromium
 * loads it and teslapc/tests/js/pcm_player.test.js can drive it in Node with stubs.
 */
var PCM_DEFAULTS = {
    TARGET_MS: 100,
    START_MS: 100,
    TRIM_ABOVE_MS: 200,
    HARD_MAX_MS: 500,
    MEASURE_WINDOW_MS: 2000
};
var PCM_RING_MS = 1000;
var PCM_CROSSFADE_MS = 5;
var PCM_WINDOW_BUCKETS = 8;
var PCM_MAX_WARM_PACKET_MS = 200;

class PCMPlayerProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        var o = (options && options.processorOptions) || {};
        this.channels = o.channels || 2;
        this.bitsPerSample = o.bitsPerSample || 32;
        this.sampleFormat = o.sampleFormat || 'float';
        this.sourceSampleRate = o.sourceSampleRate || sampleRate;
        this.playbackSampleRate = sampleRate;

        var rate = this.playbackSampleRate;
        function opt(name) {
            var v = o[name];
            return (typeof v === 'number' && v >= 0) ? v : PCM_DEFAULTS[name];
        }
        function msToFrames(ms) { return Math.round(ms * rate / 1000); }
        this.targetFrames = msToFrames(opt('TARGET_MS'));
        this.startFrames = msToFrames(opt('START_MS'));
        this.trimAboveFrames = msToFrames(opt('TRIM_ABOVE_MS'));
        this.hardMaxFrames = msToFrames(opt('HARD_MAX_MS'));
        this.bucketFrames = Math.max(1, msToFrames(opt('MEASURE_WINDOW_MS') / PCM_WINDOW_BUCKETS));
        this.crossfadeFrames = Math.max(1, msToFrames(PCM_CROSSFADE_MS));
        this.maxWarmPacketFrames = msToFrames(PCM_MAX_WARM_PACKET_MS);

        // Ring of interleaved samples. Indexes are in frames.
        this.capacityFrames = Math.max(1, msToFrames(PCM_RING_MS));
        this.ring = new Float32Array(this.capacityFrames * this.channels);
        this.readFrame = 0;
        this.levelFrames = 0;

        // Largest packet seen (frames at the context rate), added to START when warming.
        this.packetFrames = 0;

        this.state = 'warming';
        this.underruns = 0;
        this.trims = 0;

        this.buckets = new Float64Array(PCM_WINDOW_BUCKETS);
        this.resetWindow();

        this.statsFrames = 0;

        if (this.port) {
            this.port.onmessage = this.handleMessage.bind(this);
        }
    }

    resetWindow() {
        this.bucketIndex = 0;
        this.bucketsDone = 0;
        this.bucketFill = 0;
        this.bucketMin = Infinity;
        this.windowMinFrames = Infinity;
    }

    setState(next) {
        if (this.state !== next) {
            this.state = next;
            this.resetWindow();
        }
    }

    handleMessage(event) {
        var data = event.data;
        if (!data || (typeof data.byteLength !== 'number')) return;

        var floatSamples = this.decodeToFloat32(data);
        if (this.sourceSampleRate !== this.playbackSampleRate) {
            floatSamples = this.resample(floatSamples);
        }
        this.write(floatSamples);
    }

    // Append interleaved samples. Keeps the newest frames when the ring would overflow.
    write(floatSamples) {
        var ch = this.channels;
        var cap = this.capacityFrames;
        var n = Math.floor(floatSamples.length / ch);
        if (n <= 0) return;
        if (n > this.packetFrames) {
            this.packetFrames = Math.min(n, this.maxWarmPacketFrames);
        }

        var srcStart = 0;
        var overflow = this.levelFrames + n - cap;
        if (overflow > 0) {
            if (n > cap) {
                // The packet alone is larger than the ring: keep its last cap frames.
                srcStart = n - cap;
                n = cap;
                this.readFrame = 0;
                this.levelFrames = 0;
            } else {
                // Keep the newest TARGET_MS (old tail plus this packet), not just enough to
                // fit, so the rest of a burst does not overflow again on every packet.
                var keep = Math.min(this.levelFrames, Math.max(this.targetFrames - n, this.crossfadeFrames));
                this.skip(this.levelFrames - keep);
            }
            this.trims++;
            this.resetWindow();
        }

        var writeFrame = (this.readFrame + this.levelFrames) % cap;
        var ring = this.ring;
        for (var f = 0; f < n; f++) {
            var dst = writeFrame * ch;
            var src = (srcStart + f) * ch;
            for (var c = 0; c < ch; c++) {
                ring[dst + c] = floatSamples[src + c];
            }
            writeFrame++;
            if (writeFrame === cap) writeFrame = 0;
        }
        this.levelFrames += n;
    }

    // Drop `count` whole frames from the read side. The 5 ms of audio before the gap is
    // crossfaded into the 5 ms after it, in place, so the next read starts on the blend.
    // The fade is shortened when fewer than 5 ms remain after the gap.
    skip(count) {
        if (count <= 0) return;
        if (count > this.levelFrames) count = this.levelFrames;
        var ch = this.channels;
        var cap = this.capacityFrames;
        var ring = this.ring;
        var fade = Math.min(this.crossfadeFrames, this.levelFrames - count);
        var oldFrame = this.readFrame;
        var newFrame = (this.readFrame + count) % cap;
        for (var i = 0; i < fade; i++) {
            var t = (i + 1) / (fade + 1);
            var a = oldFrame * ch;
            var b = newFrame * ch;
            for (var c = 0; c < ch; c++) {
                ring[b + c] = ring[a + c] * (1 - t) + ring[b + c] * t;
            }
            oldFrame++;
            if (oldFrame === cap) oldFrame = 0;
            newFrame++;
            if (newFrame === cap) newFrame = 0;
        }
        this.readFrame = (this.readFrame + count) % cap;
        this.levelFrames -= count;
    }

    trim(count) {
        this.skip(count);
        this.trims++;
        this.resetWindow();
    }

    decodeToFloat32(data) {
        const arrayBuffer = data instanceof ArrayBuffer ? data : data.buffer;

        if (this.sampleFormat === 'float') {
            return new Float32Array(arrayBuffer);
        }

        if (this.sampleFormat === 'pcm16') {
            const int16 = new Int16Array(arrayBuffer);
            const float32 = new Float32Array(int16.length);
            for (let i = 0; i < int16.length; i++) {
                float32[i] = int16[i] / 32768.0;
            }
            return float32;
        }

        if (this.sampleFormat === 'pcm24') {
            const bytes = new Uint8Array(arrayBuffer);
            const sampleCount = Math.floor(bytes.length / 3);
            const float32 = new Float32Array(sampleCount);
            for (let i = 0; i < sampleCount; i++) {
                let val = bytes[i * 3] | (bytes[i * 3 + 1] << 8) | (bytes[i * 3 + 2] << 16);
                if (val & 0x800000) {
                    val |= 0xFF000000;
                }
                float32[i] = val / 8388608.0;
            }
            return float32;
        }

        if (this.sampleFormat === 'pcm32') {
            const int32 = new Int32Array(arrayBuffer);
            const float32 = new Float32Array(int32.length);
            for (let i = 0; i < int32.length; i++) {
                float32[i] = int32[i] / 2147483648.0;
            }
            return float32;
        }

        if (this.bitsPerSample === 16) {
            const int16 = new Int16Array(arrayBuffer);
            const float32 = new Float32Array(int16.length);
            for (let i = 0; i < int16.length; i++) {
                float32[i] = int16[i] / 32768.0;
            }
            return float32;
        }

        return new Float32Array(arrayBuffer);
    }

    // Per-packet linear resampling (unchanged in phase 1).
    resample(floatSamples) {
        const channels = this.channels;
        const sourceFrames = Math.floor(floatSamples.length / channels);
        if (sourceFrames === 0) {
            return floatSamples;
        }

        const targetFrames = Math.max(1, Math.floor(sourceFrames * this.playbackSampleRate / this.sourceSampleRate));
        const result = new Float32Array(targetFrames * channels);
        const ratio = this.sourceSampleRate / this.playbackSampleRate;

        for (let outFrame = 0; outFrame < targetFrames; outFrame++) {
            const srcPos = outFrame * ratio;
            const srcFrame = Math.floor(srcPos);
            const frac = srcPos - srcFrame;
            const nextFrame = Math.min(srcFrame + 1, sourceFrames - 1);

            for (let ch = 0; ch < channels; ch++) {
                const s0 = floatSamples[srcFrame * channels + ch];
                const s1 = floatSamples[nextFrame * channels + ch];
                result[outFrame * channels + ch] = s0 + (s1 - s0) * frac;
            }
        }

        return result;
    }

    framesToMs(frames) {
        return Math.round(frames * 1000 / this.playbackSampleRate);
    }

    process(inputs, outputs, parameters) {
        var output = outputs[0];
        var outputChannels = output.length;
        var framesNeeded = output[0].length;
        var ch = this.channels;

        // 1. Control, before reading.
        if (this.levelFrames > this.hardMaxFrames) {
            this.trim(this.levelFrames - this.targetFrames);
        } else if (this.state === 'playing' && this.bucketsDone >= PCM_WINDOW_BUCKETS &&
                   this.windowMinFrames > this.trimAboveFrames) {
            this.trim(this.windowMinFrames - this.targetFrames);
        }

        // 2. State.
        if (this.state === 'starved') {
            this.setState('warming');
        }
        if (this.state === 'warming' && this.levelFrames >= this.startFrames + this.packetFrames) {
            this.setState('playing');
        }

        // 3. Output.
        var played = 0;
        if (this.state === 'playing') {
            played = Math.min(framesNeeded, this.levelFrames);
            var ring = this.ring;
            var cap = this.capacityFrames;
            var r = this.readFrame;
            var copyChannels = outputChannels < ch ? outputChannels : ch;
            for (var f = 0; f < played; f++) {
                var base = r * ch;
                for (var c = 0; c < copyChannels; c++) {
                    output[c][f] = ring[base + c];
                }
                r++;
                if (r === cap) r = 0;
            }
            this.readFrame = r;
            this.levelFrames -= played;
            if (played < framesNeeded) {
                this.underruns++;
                this.setState('starved');
            }
        }
        for (var oc = 0; oc < outputChannels; oc++) {
            var out = output[oc];
            var from = oc < ch ? played : 0;
            for (var z = from; z < framesNeeded; z++) out[z] = 0;
        }

        // 4. Measure the level after the read (the trough) while playing.
        if (this.state === 'playing') {
            if (this.levelFrames < this.bucketMin) this.bucketMin = this.levelFrames;
            this.bucketFill += framesNeeded;
            if (this.bucketFill >= this.bucketFrames) {
                this.buckets[this.bucketIndex] = this.bucketMin;
                this.bucketIndex = (this.bucketIndex + 1) % PCM_WINDOW_BUCKETS;
                this.bucketsDone++;
                this.bucketFill = 0;
                this.bucketMin = Infinity;
                if (this.bucketsDone >= PCM_WINDOW_BUCKETS) {
                    var m = Infinity;
                    for (var k = 0; k < PCM_WINDOW_BUCKETS; k++) {
                        if (this.buckets[k] < m) m = this.buckets[k];
                    }
                    this.windowMinFrames = m;
                }
            }
        }

        // 5. Stats once a second.
        this.statsFrames += framesNeeded;
        if (this.statsFrames >= this.playbackSampleRate) {
            this.statsFrames = 0;
            if (this.port) {
                var valid = this.bucketsDone >= PCM_WINDOW_BUCKETS;
                this.port.postMessage({
                    type: 'stats',
                    bufferedMs: this.framesToMs(this.levelFrames),
                    minMs: this.framesToMs(valid ? this.windowMinFrames : this.levelFrames),
                    underruns: this.underruns,
                    trims: this.trims
                });
            }
        }
        return true;
    }
}

registerProcessor('pcm-player-processor', PCMPlayerProcessor);
