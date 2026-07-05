class PCMPlayerProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        this.samples = new Float32Array();
        this.channels = (options.processorOptions && options.processorOptions.channels) || 2;
        this.bitsPerSample = (options.processorOptions && options.processorOptions.bitsPerSample) || 32;
        this.sampleFormat = (options.processorOptions && options.processorOptions.sampleFormat) || 'float';
        this.sourceSampleRate = (options.processorOptions && options.processorOptions.sourceSampleRate) || sampleRate;
        this.playbackSampleRate = sampleRate;
        this.audioBoost = this.clampBoost(
            options.processorOptions && options.processorOptions.audioBoost,
        );
        this.port.onmessage = this.handleMessage.bind(this);
    }

    clampBoost(value) {
        const parsed = Number(value);
        if (!Number.isFinite(parsed)) {
            return 1.0;
        }
        return Math.min(6.0, Math.max(0.25, parsed));
    }

    applyBoost(floatSamples) {
        if (this.audioBoost === 1.0) {
            return floatSamples;
        }
        for (let i = 0; i < floatSamples.length; i++) {
            const boosted = floatSamples[i] * this.audioBoost;
            floatSamples[i] = boosted < -1 ? -1 : boosted > 1 ? 1 : boosted;
        }
        return floatSamples;
    }

    handleMessage(event) {
        const data = event.data;

        if (data && typeof data === 'object' && data.type === 'gain') {
            this.audioBoost = this.clampBoost(data.value);
            return;
        }

        let floatSamples = this.decodeToFloat32(data);
        if (this.sourceSampleRate !== this.playbackSampleRate) {
            floatSamples = this.resample(floatSamples);
        }
        floatSamples = this.applyBoost(floatSamples);

        const maxSamples = this.playbackSampleRate * this.channels * 2;
        const totalSamples = this.samples.length + floatSamples.length;

        let existingSamples = this.samples;
        if (totalSamples > maxSamples) {
            const samplesToDiscard = totalSamples - maxSamples;
            const alignedDiscard = samplesToDiscard - (samplesToDiscard % this.channels);
            existingSamples = this.samples.subarray(alignedDiscard);
        }

        const tmp = new Float32Array(existingSamples.length + floatSamples.length);
        tmp.set(existingSamples, 0);
        tmp.set(floatSamples, existingSamples.length);
        this.samples = tmp;
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

    process(inputs, outputs, parameters) {
        const output = outputs[0];
        const outputChannels = output.length;
        const framesNeeded = output[0].length;
        const samplesNeeded = framesNeeded * this.channels;

        if (this.samples.length < samplesNeeded) {
            for (let ch = 0; ch < outputChannels; ch++) {
                output[ch].fill(0);
            }
            return true;
        }

        const chunk = this.samples.subarray(0, samplesNeeded);

        for (let frame = 0; frame < framesNeeded; frame++) {
            for (let ch = 0; ch < outputChannels && ch < this.channels; ch++) {
                output[ch][frame] = chunk[frame * this.channels + ch];
            }
        }

        this.samples = this.samples.subarray(samplesNeeded);
        return true;
    }
}

registerProcessor('pcm-player-processor', PCMPlayerProcessor);