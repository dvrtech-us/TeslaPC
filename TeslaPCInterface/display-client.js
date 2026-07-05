/**
 * Display transport for TeslaPC: MJPEG or H264 over /ws/display (format v2 + host PTS prefix).
 * Falls back to HTTP /stream when displayTransport is "http".
 */
(function (global) {
    "use strict";

    var PTS_BYTES = 8;
    var socket = null;
    var format = null;
    var sessionConfig = null;
    var intentionalClose = false;
    var canvas = null;
    var img = null;
    var h264Worker = null;
    var currentBlobUrl = null;
    var displayWidth = 1280;
    var displayHeight = 720;

    function getWsUrl(path) {
        var protocol = (location.protocol === "https:") ? "wss://" : "ws://";
        return protocol + location.host + path;
    }

    function loadSessionConfig() {
        return fetch("/config").then(function (r) { return r.json(); }).then(function (c) {
            sessionConfig = c || {};
            return sessionConfig;
        }).catch(function () {
            sessionConfig = { displayRenderer: "mjpeg", displayTransport: "websocket" };
            return sessionConfig;
        });
    }

    function stripPts(arrayBuffer) {
        if (arrayBuffer.byteLength <= PTS_BYTES) {
            return { pts: 0, payload: new Uint8Array(arrayBuffer) };
        }
        var view = new DataView(arrayBuffer);
        var pts = Number(view.getBigInt64(0, true));
        return { pts: pts, payload: new Uint8Array(arrayBuffer, PTS_BYTES) };
    }

    function applyMjpegFrame(blob) {
        if (currentBlobUrl) {
            URL.revokeObjectURL(currentBlobUrl);
        }
        currentBlobUrl = URL.createObjectURL(blob);
        img.src = currentBlobUrl;
    }

    function ensureH264Worker() {
        if (h264Worker) {
            return;
        }
        canvas = document.getElementById("streamCanvas");
        img = document.getElementById("streamImg");
        if (!canvas || !img) {
            return;
        }

        if (typeof global.VideoDecoder === "undefined") {
            var unavailable = "H264: VideoDecoder (WebCodecs) is not available in this browser";
            console.error(unavailable);
            if (global.__teslaPcDebug && global.__teslaPcDebug.errors) {
                global.__teslaPcDebug.errors.push(unavailable);
            }
            return;
        }

        img.style.display = "none";
        canvas.style.display = "block";

        var offscreen = canvas.transferControlToOffscreen();

        h264Worker = new Worker("display-h264-worker.js");
        h264Worker.onmessage = function (ev) {
            if (ev.data && ev.data.error) {
                console.error("H264 worker:", ev.data.error);
                if (global.__teslaPcDebug && global.__teslaPcDebug.errors) {
                    global.__teslaPcDebug.errors.push("H264 worker: " + ev.data.error);
                }
            }
        };
        h264Worker.postMessage({
            canvas: offscreen,
            displayWidth: displayWidth,
            displayHeight: displayHeight,
            windowWidth: canvas.clientWidth || global.innerWidth,
            windowHeight: canvas.clientHeight || global.innerHeight,
        }, [offscreen]);

        global.addEventListener("resize", function () {
            if (!h264Worker || !canvas) {
                return;
            }
            h264Worker.postMessage({
                config: {
                    displayWidth: displayWidth,
                    displayHeight: displayHeight,
                    windowWidth: canvas.clientWidth || global.innerWidth,
                    windowHeight: canvas.clientHeight || global.innerHeight,
                },
            });
        });
    }

    function ensureMjpegDom() {
        img = document.getElementById("streamImg");
        canvas = document.getElementById("streamCanvas");
        if (!img || !canvas) {
            return;
        }
        img.style.display = "block";
        canvas.style.display = "none";
    }

    function startHttpMjpeg() {
        ensureMjpegDom();
        img.removeAttribute("src");
        img.src = "/stream?_=" + Date.now();
    }

    function startWebSocketDisplay(onDisconnect) {
        var renderer = (sessionConfig.displayRenderer || "mjpeg").toLowerCase();
        if (renderer === "h264" && sessionConfig.h264Available === false) {
            renderer = "mjpeg";
        }

        intentionalClose = false;
        var url = getWsUrl("/ws/display") + "?renderer=" + encodeURIComponent(renderer);
        socket = new WebSocket(url);
        socket.binaryType = "arraybuffer";

        socket.onclose = function () {
            if (!intentionalClose && typeof onDisconnect === "function") {
                onDisconnect();
            }
        };
        socket.onerror = function () {
            if (!intentionalClose && typeof onDisconnect === "function") {
                onDisconnect();
            }
        };

        socket.onmessage = function (ev) {
            if (typeof ev.data === "string") {
                format = JSON.parse(ev.data);
                displayWidth = format.width || displayWidth;
                displayHeight = format.height || displayHeight;
                console.log("Display format:", format);
                if (format.renderer === "h264") {
                    ensureH264Worker();
                } else {
                    ensureMjpegDom();
                }
                return;
            }

            var parsed = stripPts(ev.data);
            if (!format) {
                return;
            }

            if (format.renderer === "h264") {
                if (!h264Worker) {
                    return;
                }
                var copy = parsed.payload.slice();
                h264Worker.postMessage({ h264Data: copy.buffer }, [copy.buffer]);
            } else {
                applyMjpegFrame(new Blob([parsed.payload], { type: "image/jpeg" }));
            }
        };
    }

    function startDisplay(onDisconnect) {
        return loadSessionConfig().then(function (cfg) {
            sessionConfig = cfg;
            if ((cfg.displayTransport || "websocket") === "http") {
                startHttpMjpeg();
                return;
            }
            startWebSocketDisplay(onDisconnect);
        });
    }

    function stopDisplay() {
        intentionalClose = true;
        if (socket) {
            try { socket.close(); } catch (_e) { /* ignore */ }
            socket = null;
        }
        if (h264Worker) {
            try { h264Worker.terminate(); } catch (_e) { /* ignore */ }
            h264Worker = null;
        }
        if (currentBlobUrl) {
            URL.revokeObjectURL(currentBlobUrl);
            currentBlobUrl = null;
        }
        format = null;
    }

    function getDisplayElement() {
        if (format && format.renderer === "h264") {
            return document.getElementById("streamCanvas");
        }
        return document.getElementById("streamImg");
    }

    function getStreamSize() {
        var el = getDisplayElement();
        var rect = el ? el.getBoundingClientRect() : { left: 0, top: 0, width: 1, height: 1 };
        var natW = displayWidth;
        var natH = displayHeight;
        if (el && el.tagName === "IMG") {
            natW = el.naturalWidth || displayWidth;
            natH = el.naturalHeight || displayHeight;
        }
        var scale = Math.min(rect.width / natW, rect.height / natH);
        var dispW = natW * scale;
        var dispH = natH * scale;
        return {
            rect: rect,
            displayWidth: natW,
            displayHeight: natH,
            renderWidth: dispW,
            renderHeight: dispH,
            offsetX: (rect.width - dispW) / 2,
            offsetY: (rect.height - dispH) / 2,
        };
    }

    global.TeslaDisplay = {
        start: startDisplay,
        stop: stopDisplay,
        getDisplayElement: getDisplayElement,
        getStreamSize: getStreamSize,
    };
})(window);
