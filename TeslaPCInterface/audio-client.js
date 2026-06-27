/*
 * Self-contained audio client for the /ws/audio PCM stream.
 *
 * Connects to the audio WebSocket, reads the format metadata, and plays the PCM via an
 * AudioWorklet (PCMPlayerProcessor.js) with a manual-scheduling fallback. Used by the media
 * player page (play.html). index.html has its own inline copy wired to its playback button.
 *
 * Browsers require a user gesture before audio can start, so call startTeslaAudio() from a tap.
 */
(function (global) {
    var audioContext = null;
    var audioSocket = null;
    var pcmPlayerNode = null;
    var audioFormat = null;
    var useWorklet = true;
    var scheduledTime = 0;
    var connecting = false;

    function getWsUrl(path) {
        var protocol = (location.protocol === 'https:') ? 'wss://' : 'ws://';
        return protocol + location.host + path;
    }

    function teardown() {
        try { if (audioSocket) audioSocket.close(); } catch (e) {}
        try { if (audioContext) audioContext.close(); } catch (e) {}
        audioContext = null;
        audioSocket = null;
        pcmPlayerNode = null;
        audioFormat = null;
        useWorklet = true;
        scheduledTime = 0;
    }

    function startTeslaAudio() {
        if (connecting) return;
        if (audioContext != null) teardown();
        connecting = true;

        audioSocket = new WebSocket(getWsUrl('/ws/audio'));
        audioSocket.binaryType = 'arraybuffer';
        audioSocket.onclose = function () { connecting = false; };
        audioSocket.onerror = function (ev) { console.error('Audio WebSocket error:', ev); connecting = false; };

        audioSocket.onmessage = async function (event) {
            if (typeof event.data === 'string') {
                audioFormat = JSON.parse(event.data);
                console.log('Audio format:', audioFormat);

                audioContext = new (window.AudioContext || window.webkitAudioContext)({
                    sampleRate: audioFormat.sampleRate,
                    latencyHint: 'interactive'
                });

                try {
                    await audioContext.audioWorklet.addModule('PCMPlayerProcessor.js');
                    pcmPlayerNode = new AudioWorkletNode(audioContext, 'pcm-player-processor', {
                        outputChannelCount: [audioFormat.channels],
                        processorOptions: {
                            channels: audioFormat.channels,
                            bitsPerSample: audioFormat.bitsPerSample,
                            sampleFormat: audioFormat.sampleFormat || (audioFormat.bitsPerSample === 16 ? 'pcm16' : 'float'),
                            sourceSampleRate: audioFormat.sampleRate
                        }
                    });
                    pcmPlayerNode.connect(audioContext.destination);
                    useWorklet = true;
                } catch (e) {
                    console.warn('AudioWorklet unavailable, using fallback:', e);
                    useWorklet = false;
                    scheduledTime = 0;
                }

                await audioContext.resume();
                connecting = false;
                console.log('Audio playback started');
                return;
            }

            if (!audioFormat || !audioContext) return;
            if (useWorklet && pcmPlayerNode) {
                pcmPlayerNode.port.postMessage(event.data);
            } else {
                playPcmChunkFallback(event.data);
            }
        };
    }

    function decodePcmToFloat32(arrayBuffer, format) {
        var sampleFormat = format.sampleFormat || (format.bitsPerSample === 16 ? 'pcm16' : 'float');
        if (sampleFormat === 'float') return new Float32Array(arrayBuffer);
        if (sampleFormat === 'pcm16') {
            var int16 = new Int16Array(arrayBuffer);
            var p16 = new Float32Array(int16.length);
            for (var i = 0; i < int16.length; i++) p16[i] = int16[i] / 32768.0;
            return p16;
        }
        if (sampleFormat === 'pcm24') {
            var bytes = new Uint8Array(arrayBuffer);
            var n = Math.floor(bytes.length / 3);
            var p24 = new Float32Array(n);
            for (var j = 0; j < n; j++) {
                var v = bytes[j * 3] | (bytes[j * 3 + 1] << 8) | (bytes[j * 3 + 2] << 16);
                if (v & 0x800000) v |= 0xFF000000;
                p24[j] = v / 8388608.0;
            }
            return p24;
        }
        if (sampleFormat === 'pcm32') {
            var int32 = new Int32Array(arrayBuffer);
            var p32 = new Float32Array(int32.length);
            for (var k = 0; k < int32.length; k++) p32[k] = int32[k] / 2147483648.0;
            return p32;
        }
        return new Float32Array(arrayBuffer);
    }

    function resampleFloat32(s, channels, sourceRate, targetRate) {
        if (sourceRate === targetRate) return s;
        var sourceFrames = Math.floor(s.length / channels);
        if (sourceFrames === 0) return s;
        var targetFrames = Math.max(1, Math.floor(sourceFrames * targetRate / sourceRate));
        var out = new Float32Array(targetFrames * channels);
        var ratio = sourceRate / targetRate;
        for (var f = 0; f < targetFrames; f++) {
            var sp = f * ratio, sf = Math.floor(sp), frac = sp - sf;
            var nf = Math.min(sf + 1, sourceFrames - 1);
            for (var c = 0; c < channels; c++) {
                var a = s[sf * channels + c], b = s[nf * channels + c];
                out[f * channels + c] = a + (b - a) * frac;
            }
        }
        return out;
    }

    function playPcmChunkFallback(rawData) {
        var arrayBuffer = rawData instanceof ArrayBuffer ? rawData : rawData.buffer;
        var channels = audioFormat.channels;
        var sourceRate = audioFormat.sampleRate;
        var playbackRate = audioContext.sampleRate;

        var floatSamples = decodePcmToFloat32(arrayBuffer, audioFormat);
        floatSamples = resampleFloat32(floatSamples, channels, sourceRate, playbackRate);
        var frames = Math.floor(floatSamples.length / channels);
        if (frames === 0) return;

        var audioBuffer = audioContext.createBuffer(channels, frames, playbackRate);
        for (var c = 0; c < channels; c++) {
            var cd = audioBuffer.getChannelData(c);
            for (var f = 0; f < frames; f++) cd[f] = floatSamples[f * channels + c];
        }
        var src = audioContext.createBufferSource();
        src.buffer = audioBuffer;
        src.connect(audioContext.destination);
        var now = audioContext.currentTime;
        if (scheduledTime < now) scheduledTime = now + 0.02;
        src.start(scheduledTime);
        scheduledTime += audioBuffer.duration;
    }

    global.startTeslaAudio = startTeslaAudio;
    global.stopTeslaAudio = teardown;
})(window);
