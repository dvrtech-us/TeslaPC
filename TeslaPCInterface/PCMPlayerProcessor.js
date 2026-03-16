class PCMPlayerProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        this.samples = new Float32Array();
        this.channels = (options.processorOptions && options.processorOptions.channels) || 2;
        this.bitsPerSample = (options.processorOptions && options.processorOptions.bitsPerSample) || 32;
        this.port.onmessage = this.handleMessage.bind(this);
    }

    handleMessage(event) {
        const data = event.data;

        // Convert raw PCM bytes to Float32 samples
        const floatSamples = this.decodeToFloat32(data);

        // Limit buffer to ~2 seconds of audio to prevent unbounded growth
        const maxSamples = sampleRate * this.channels * 2;
        const totalSamples = this.samples.length + floatSamples.length;

        let existingSamples = this.samples;
        if (totalSamples > maxSamples) {
            // Discard oldest samples, aligned to frame boundaries
            const samplesToDiscard = totalSamples - maxSamples;
            const alignedDiscard = samplesToDiscard - (samplesToDiscard % this.channels);
            existingSamples = this.samples.subarray(alignedDiscard);
        }

        // Append new samples
        const tmp = new Float32Array(existingSamples.length + floatSamples.length);
        tmp.set(existingSamples, 0);
        tmp.set(floatSamples, existingSamples.length);
        this.samples = tmp;
    }

    /**
     * Converts raw PCM bytes (ArrayBuffer) to Float32 samples.
     * WASAPI loopback typically outputs 32-bit IEEE float.
     */
    decodeToFloat32(data) {
        const arrayBuffer = data instanceof ArrayBuffer ? data : data.buffer;

        if (this.bitsPerSample === 32) {
            // 32-bit IEEE float — direct reinterpretation
            return new Float32Array(arrayBuffer);
        } else if (this.bitsPerSample === 16) {
            // 16-bit signed integer PCM — convert to float
            const int16 = new Int16Array(arrayBuffer);
            const float32 = new Float32Array(int16.length);
            for (let i = 0; i < int16.length; i++) {
                float32[i] = int16[i] / 32768.0;
            }
            return float32;
        }

        // Fallback: treat as 32-bit float
        return new Float32Array(arrayBuffer);
    }

    process(inputs, outputs, parameters) {
        const output = outputs[0];
        const outputChannels = output.length;
        const framesNeeded = output[0].length;
        const samplesNeeded = framesNeeded * this.channels;

        if (this.samples.length < samplesNeeded) {
            // Not enough data — output silence
            return true;
        }

        const chunk = this.samples.subarray(0, samplesNeeded);

        // Deinterleave into output channels
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
