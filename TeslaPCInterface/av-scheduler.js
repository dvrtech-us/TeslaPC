/**
 * Shared host-PTS playout scheduler for TeslaPC A/V sync (phase 3a).
 * Works in the main thread (window) and in workers (self) via IIFE or importScripts.
 */
(function (global) {
    "use strict";

    var anchorHostPtsUs = null;
    var anchorWallMs = null;
    var targetDelayMs = 200;

    function reset() {
        anchorHostPtsUs = null;
        anchorWallMs = null;
    }

    function setTargetDelayMs(ms) {
        var parsed = Number(ms);
        if (Number.isFinite(parsed) && parsed >= 0) {
            targetDelayMs = parsed;
        }
    }

    function anchorOnFirstHostPts(hostPtsUs) {
        var pts = Number(hostPtsUs);
        if (!Number.isFinite(pts)) {
            return;
        }
        if (anchorHostPtsUs === null || pts < anchorHostPtsUs) {
            anchorHostPtsUs = pts;
            anchorWallMs = Date.now();
        }
    }

    function sync(state) {
        if (!state) {
            return;
        }
        if (state.anchorHostPtsUs != null && state.anchorWallMs != null) {
            anchorHostPtsUs = Number(state.anchorHostPtsUs);
            anchorWallMs = Number(state.anchorWallMs);
        }
        if (state.targetDelayMs != null) {
            setTargetDelayMs(state.targetDelayMs);
        }
    }

    function hostPtsToPlayWallMs(hostPtsUs) {
        anchorOnFirstHostPts(hostPtsUs);
        if (anchorHostPtsUs === null || anchorWallMs === null) {
            return Date.now() + targetDelayMs;
        }
        var deltaUs = Number(hostPtsUs) - anchorHostPtsUs;
        return anchorWallMs + deltaUs / 1000 + targetDelayMs;
    }

    function hostPtsToDecoderTimestampUs(hostPtsUs) {
        anchorOnFirstHostPts(hostPtsUs);
        if (anchorHostPtsUs === null) {
            return 0;
        }
        return Math.max(0, Number(hostPtsUs) - anchorHostPtsUs);
    }

    function delayUntilPlayMs(hostPtsUs) {
        return Math.max(0, hostPtsToPlayWallMs(hostPtsUs) - Date.now());
    }

    function getSyncState() {
        return {
            anchorHostPtsUs: anchorHostPtsUs,
            anchorWallMs: anchorWallMs,
            targetDelayMs: targetDelayMs,
        };
    }

    global.TeslaAvScheduler = {
        reset: reset,
        setTargetDelayMs: setTargetDelayMs,
        anchorOnFirstHostPts: anchorOnFirstHostPts,
        sync: sync,
        hostPtsToPlayWallMs: hostPtsToPlayWallMs,
        hostPtsToDecoderTimestampUs: hostPtsToDecoderTimestampUs,
        delayUntilPlayMs: delayUntilPlayMs,
        getSyncState: getSyncState,
        getAnchorHostPtsUs: function () { return anchorHostPtsUs; },
        getAnchorWallMs: function () { return anchorWallMs; },
        getTargetDelayMs: function () { return targetDelayMs; },
    };
})(typeof self !== "undefined" ? self : window);