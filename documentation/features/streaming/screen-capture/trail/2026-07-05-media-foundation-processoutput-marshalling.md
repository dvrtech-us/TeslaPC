# Media Foundation ProcessOutput Marshalling Fix

- Date: 2026-07-05
- Feature: `streaming/screen-capture`
- Related code: `TeslaPCInterface/H264MediaFoundationEncoder.cs`

## Context

The native Windows Media Foundation H264 encoder could be created and started, but the Windows
probe returned zero access units. The log showed repeated encode failures with:

```text
[H264] native encode failed: Typelib export: Type library is not registered. (0x80131165)
```

This happened after `ProcessInput` succeeded and while draining output. The encoder itself was
available; the failure was at the .NET COM interop boundary for `IMFTransform.ProcessOutput`.

## Decision

`IMFTransform.ProcessOutput` no longer accepts a managed `MFT_OUTPUT_DATA_BUFFER[]`. The wrapper
now allocates one unmanaged `MFT_OUTPUT_DATA_BUFFER` with `Marshal.AllocHGlobal`, passes its
pointer to `ProcessOutput`, then reads the updated struct with `Marshal.PtrToStructure`.

`MFT_OUTPUT_DATA_BUFFER.pSample` remains an `IntPtr`. When the encoder returns a sample pointer,
`Marshal.GetObjectForIUnknown` resolves it to `IMFSample` only after the COM call has completed.
`pEvents`, `pSample`, the original provided output sample pointer, and the unmanaged struct
buffer are released explicitly.

## Verification

The Windows synthetic probe against `C:\dev\Repos\TeslaPC` passed:

```text
IsAvailable=True
[H264] Native Media Foundation encoder started 320x180@30
RESULT accessUnits=82 bytes=143158 sps=1 pps=1 idr=1 slice=81
__PROBE_DONE__ 0
```

The Mac DVR/Chrome probe against `https://192.168.12.235:8443/` also passed for display:

```text
displayRenderer=h264
displayTransport=websocket
h264Available=true
displayFormat.renderer=h264
displayFrames=827
errors=[]
```

## Consequences

- Positive: the native H264 encoder now returns access units on the lab Windows PC.
- Positive: the browser receives H264 WebSocket frames and renders through the canvas/WebCodecs
  path.
- Negative: `ProcessOutput` cleanup is more manual because samples/events are raw COM pointers.
