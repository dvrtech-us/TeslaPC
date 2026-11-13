//! Windows Graphics Capture, DXGI Desktop Duplication, and WASAPI loopback.
//! Failures are the OS error text, not a stand-in string.

use std::cell::RefCell;
use std::thread;
use std::time::Duration;

use windows::core::{factory, Interface};
use windows::Win32::Foundation::{HMODULE, POINT, RPC_E_CHANGED_MODE};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_UNKNOWN};
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, D3D11_CPU_ACCESS_READ, D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_MAPPED_SUBRESOURCE, D3D11_MAP_READ, D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC, D3D11_USAGE_STAGING, ID3D11Device, ID3D11DeviceContext, ID3D11Resource, ID3D11Texture2D,
};
use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, DXGI_ERROR_ACCESS_LOST, DXGI_ERROR_INVALID_CALL, DXGI_ERROR_SESSION_DISCONNECTED, DXGI_ERROR_WAIT_TIMEOUT, DXGI_OUTDUPL_FRAME_INFO, IDXGIAdapter, IDXGIAdapter1, IDXGIDevice, IDXGIFactory1, IDXGIOutput1, IDXGIOutputDuplication, IDXGIResource};
use windows::Win32::Graphics::Gdi::{MonitorFromPoint, MONITOR_DEFAULTTOPRIMARY};
use windows::Win32::Media::Audio::{eConsole, eRender, IAudioCaptureClient, IAudioClient, IMMDeviceEnumerator, MMDeviceEnumerator, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, WAVEFORMATEX};
use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_ALL, COINIT_MULTITHREADED};
use windows::Win32::System::WinRT::Direct3D11::{CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess};
use windows::Win32::System::WinRT::Graphics::Capture::IGraphicsCaptureItemInterop;
use windows::Win32::System::WinRT::{RoInitialize, RO_INIT_MULTITHREADED};
use windows::Graphics::Capture::{Direct3D11CaptureFramePool, GraphicsCaptureItem, GraphicsCaptureSession};
use windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
use windows::Graphics::DirectX::DirectXPixelFormat;
use windows::Graphics::SizeInt32;

use crate::platform::{Grab, GrabOutcome};
use crate::state::{App, AudioFormat};

/// One short wait per DXGI grab; the capture loop paces the retries.
const DXGI_ACQUIRE_TIMEOUT_MS: u32 = 100;

struct WgcLive {
    device: ID3D11Device,
    context: ID3D11DeviceContext,
    pool: Direct3D11CaptureFramePool,
    _session: GraphicsCaptureSession,
}

struct DxgiLive {
    device: ID3D11Device,
    context: ID3D11DeviceContext,
    dup: IDXGIOutputDuplication,
}

thread_local! {
    static WGC: RefCell<Option<WgcLive>> = const { RefCell::new(None) };
    static DXGI: RefCell<Option<DxgiLive>> = const { RefCell::new(None) };
}

/// Drop this thread's WGC and DXGI sessions. Called before the capture thread moves to another
/// desktop: both are bound to the desktop they were opened on. The next grab reopens them.
pub fn drop_sessions() {
    WGC.with(|slot| *slot.borrow_mut() = None);
    DXGI.with(|slot| *slot.borrow_mut() = None);
}

pub fn grab_wgc() -> Result<GrabOutcome, String> {
    unsafe {
        if let Err(err) = RoInitialize(RO_INIT_MULTITHREADED) {
            if err.code() != RPC_E_CHANGED_MODE {
                return Err(format!("RoInitialize: {err}"));
            }
        }
        match GraphicsCaptureSession::IsSupported() {
            Ok(false) => return Err("GraphicsCaptureSession::IsSupported() == false".into()),
            Err(err) => return Err(format!("GraphicsCaptureSession::IsSupported: {err}")),
            Ok(true) => {}
        }
        if WGC.with(|slot| slot.borrow().is_none()) {
            let live = open_wgc().map_err(|e| e.to_string())?;
            WGC.with(|slot| *slot.borrow_mut() = Some(live));
        }
        let drop_session = || WGC.with(|slot| *slot.borrow_mut() = None);
        let got = WGC.with(|slot| slot.borrow().as_ref().map(|live| live.pool.TryGetNextFrame()));
        let frame = match got {
            Some(Ok(frame)) => frame,
            // WGC only produces a frame when the screen changes. The projection maps a null
            // frame to `Error::empty()`, whose code is S_OK, so a success code here means
            // "no new frame" and the session is healthy.
            Some(Err(err)) if err.code().is_ok() => return Ok(GrabOutcome::NoFrame),
            Some(Err(err)) => {
                drop_session();
                return Err(format!("WGC TryGetNextFrame: {err}"));
            }
            None => return Err("WGC session was not stored".into()),
        };
        let grab = (|| {
            let surface = frame.Surface().map_err(|e| format!("Direct3D11CaptureFrame::Surface: {e}"))?;
            let access: IDirect3DDxgiInterfaceAccess = surface.cast().map_err(|e| format!("IDirect3DDxgiInterfaceAccess: {e}"))?;
            let tex: ID3D11Texture2D = access.GetInterface().map_err(|e| format!("GetInterface(ID3D11Texture2D): {e}"))?;
            WGC.with(|slot| {
                let live = slot.borrow();
                let live = live.as_ref().ok_or_else(|| "WGC session dropped".to_string())?;
                read_texture(&live.context, &live.device, &tex)
            })
        })();
        let _ = frame.Close();
        match grab {
            Ok(grab) => Ok(GrabOutcome::Frame(grab)),
            Err(err) => {
                drop_session();
                Err(err)
            }
        }
    }
}

unsafe fn open_wgc() -> windows::core::Result<WgcLive> {
    let (device, context) = d3d_device(None)?;
    let dxgi: IDXGIDevice = device.cast()?;
    let inspectable = CreateDirect3D11DeviceFromDXGIDevice(&dxgi)?;
    let winrt: IDirect3DDevice = inspectable.cast()?;
    let monitor = MonitorFromPoint(POINT { x: 0, y: 0 }, MONITOR_DEFAULTTOPRIMARY);
    let interop: IGraphicsCaptureItemInterop = factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>()?;
    let item: GraphicsCaptureItem = interop.CreateForMonitor(monitor)?;
    let size: SizeInt32 = item.Size()?;
    let pool = Direct3D11CaptureFramePool::CreateFreeThreaded(&winrt, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size)?;
    let session = pool.CreateCaptureSession(&item)?;
    if let Err(err) = session.SetIsCursorCaptureEnabled(false) {
        println!("[Capture] SetIsCursorCaptureEnabled(false): {err}");
    }
    if let Err(err) = session.SetIsBorderRequired(false) {
        println!("[Capture] SetIsBorderRequired(false): {err}");
    }
    session.StartCapture()?;
    Ok(WgcLive { device, context, pool, _session: session })
}

/// One `AcquireNextFrame` per call. A wait timeout means the screen did not change: the session
/// is kept and the result is `NoFrame`. A lost duplication (desktop switch, mode change,
/// sleep/resume) is reopened once in place; only a failed reopen is an `Err`.
pub fn grab_dxgi() -> Result<GrabOutcome, String> {
    unsafe {
        let mut reopened = false;
        loop {
            if DXGI.with(|slot| slot.borrow().is_none()) {
                let live = open_dxgi().map_err(|e| format!("DXGI DuplicateOutput: {e}"))?;
                DXGI.with(|slot| *slot.borrow_mut() = Some(live));
            }
            let (dup, device, context) = DXGI.with(|slot| {
                let live = slot.borrow();
                let live = live.as_ref().ok_or_else(|| "DXGI session dropped".to_string())?;
                Ok::<_, String>((live.dup.clone(), live.device.clone(), live.context.clone()))
            })?;
            let mut info = std::mem::zeroed::<DXGI_OUTDUPL_FRAME_INFO>();
            let mut resource: Option<IDXGIResource> = None;
            match dup.AcquireNextFrame(DXGI_ACQUIRE_TIMEOUT_MS, &mut info, &mut resource) {
                Err(err) if err.code() == DXGI_ERROR_WAIT_TIMEOUT => return Ok(GrabOutcome::NoFrame),
                Err(err) if dxgi_session_lost(err.code()) => {
                    DXGI.with(|slot| *slot.borrow_mut() = None);
                    if reopened {
                        return Err(format!("AcquireNextFrame after reopen: {err}"));
                    }
                    reopened = true;
                    println!("[Capture] DXGI duplication lost ({err}); reopening.");
                }
                Err(err) => {
                    DXGI.with(|slot| *slot.borrow_mut() = None);
                    return Err(format!("AcquireNextFrame: {err}"));
                }
                Ok(()) => {
                    let resource = match resource {
                        Some(resource) => resource,
                        None => {
                            let _ = dup.ReleaseFrame();
                            DXGI.with(|slot| *slot.borrow_mut() = None);
                            return Err("AcquireNextFrame returned a null IDXGIResource".into());
                        }
                    };
                    let tex: ID3D11Texture2D = match resource.cast() {
                        Ok(tex) => tex,
                        Err(err) => {
                            let _ = dup.ReleaseFrame();
                            DXGI.with(|slot| *slot.borrow_mut() = None);
                            return Err(format!("IDXGIResource::cast ID3D11Texture2D: {err}"));
                        }
                    };
                    let grab = read_texture(&context, &device, &tex);
                    let _ = dup.ReleaseFrame();
                    return grab.map(GrabOutcome::Frame);
                }
            }
        }
    }
}

fn dxgi_session_lost(code: windows::core::HRESULT) -> bool {
    code == DXGI_ERROR_ACCESS_LOST || code == DXGI_ERROR_INVALID_CALL || code == DXGI_ERROR_SESSION_DISCONNECTED
}

unsafe fn open_dxgi() -> windows::core::Result<DxgiLive> {
    let factory: IDXGIFactory1 = CreateDXGIFactory1()?;
    let adapter: IDXGIAdapter1 = factory.EnumAdapters1(0)?;
    let (device, context) = d3d_device(Some(&adapter))?;
    let output = adapter.EnumOutputs(0)?;
    let output1: IDXGIOutput1 = output.cast()?;
    let dup = output1.DuplicateOutput(&device)?;
    Ok(DxgiLive { device, context, dup })
}

unsafe fn d3d_device(adapter: Option<&IDXGIAdapter1>) -> windows::core::Result<(ID3D11Device, ID3D11DeviceContext)> {
    let mut device = None;
    let mut context = None;
    let flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    let created = if let Some(adapter) = adapter {
        let base: IDXGIAdapter = adapter.cast()?;
        // A non-null adapter requires UNKNOWN. HARDWARE plus an adapter fails the call.
        D3D11CreateDevice(&base, D3D_DRIVER_TYPE_UNKNOWN, HMODULE(std::ptr::null_mut()), flags, None, D3D11_SDK_VERSION, Some(&mut device), None, Some(&mut context))
    } else {
        D3D11CreateDevice(None::<&IDXGIAdapter>, D3D_DRIVER_TYPE_HARDWARE, HMODULE(std::ptr::null_mut()), flags, None, D3D11_SDK_VERSION, Some(&mut device), None, Some(&mut context))
    };
    created?;
    match (device, context) {
        (Some(device), Some(context)) => Ok((device, context)),
        _ => Err(windows::core::Error::from(windows::core::HRESULT(0x8000_4005u32 as i32))),
    }
}

unsafe fn read_texture(context: &ID3D11DeviceContext, device: &ID3D11Device, src: &ID3D11Texture2D) -> Result<Grab, String> {
    let mut desc = D3D11_TEXTURE2D_DESC::default();
    src.GetDesc(&mut desc);
    let mut staging_desc = desc;
    staging_desc.Usage = D3D11_USAGE_STAGING;
    staging_desc.BindFlags = 0;
    staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
    staging_desc.MiscFlags = 0;
    staging_desc.MipLevels = 1;
    staging_desc.ArraySize = 1;
    staging_desc.SampleDesc.Count = 1;
    staging_desc.SampleDesc.Quality = 0;
    let mut staging = None;
    device.CreateTexture2D(&staging_desc, None, Some(&mut staging)).map_err(|e| format!("ID3D11Device::CreateTexture2D: {e}"))?;
    let staging = staging.ok_or_else(|| "CreateTexture2D returned null".to_string())?;
    let src_res: ID3D11Resource = src.cast().map_err(|e| format!("texture cast: {e}"))?;
    let dst_res: ID3D11Resource = staging.cast().map_err(|e| format!("staging cast: {e}"))?;
    context.CopyResource(&dst_res, &src_res);
    let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
    context.Map(&dst_res, 0, D3D11_MAP_READ, 0, Some(&mut mapped)).map_err(|e| format!("ID3D11DeviceContext::Map: {e}"))?;
    let width = desc.Width as i32;
    let height = desc.Height as i32;
    let pitch = mapped.RowPitch as usize;
    let mut bgra = vec![0u8; (width as usize) * (height as usize) * 4];
    if !mapped.pData.is_null() && width > 0 && height > 0 {
        let src_ptr = mapped.pData as *const u8;
        for row in 0..height as usize {
            std::ptr::copy_nonoverlapping(src_ptr.add(row * pitch), bgra.as_mut_ptr().add(row * (width as usize) * 4), (width as usize) * 4);
        }
    }
    context.Unmap(&dst_res, 0);
    Ok(Grab { bgra, width, height })
}

/// Balances a successful `CoInitializeEx` (S_OK or S_FALSE) with `CoUninitialize`, so
/// repeated loopback attempts on one thread do not leak COM references. Declared before
/// every COM object in `run_loopback`, so it drops after them.
struct ComScope(bool);

impl Drop for ComScope {
    fn drop(&mut self) {
        if self.0 {
            unsafe { CoUninitialize() };
        }
    }
}

pub fn run_loopback(app: App) -> Result<(), String> {
    unsafe {
        let hr = CoInitializeEx(None, COINIT_MULTITHREADED);
        if hr.is_err() && hr != RPC_E_CHANGED_MODE {
            return Err(format!("CoInitializeEx: {hr}"));
        }
        let _com = ComScope(hr.is_ok());
        let enumerator: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None::<&windows::core::IUnknown>, CLSCTX_ALL).map_err(|e| format!("CoCreateInstance(MMDeviceEnumerator): {e}"))?;
        let device = enumerator.GetDefaultAudioEndpoint(eRender, eConsole).map_err(|e| format!("GetDefaultAudioEndpoint(eRender, eConsole): {e}"))?;
        let client: IAudioClient = device.Activate(CLSCTX_ALL, None).map_err(|e| format!("IMMDevice::Activate(IAudioClient): {e}"))?;
        let mix = client.GetMixFormat().map_err(|e| format!("IAudioClient::GetMixFormat: {e}"))?;
        if mix.is_null() {
            return Err("IAudioClient::GetMixFormat returned null".into());
        }
        let format = describe_wave(mix);
        let rate = format.sample_rate;
        let block = (*mix).nBlockAlign as usize;
        client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 10_000_000, 0, mix, None).map_err(|e| {
            CoTaskMemFree(Some(mix as *const core::ffi::c_void));
            format!("IAudioClient::Initialize(LOOPBACK): {e}")
        })?;
        CoTaskMemFree(Some(mix as *const core::ffi::c_void));
        let capture: IAudioCaptureClient = client.GetService().map_err(|e| format!("IAudioClient::GetService(IAudioCaptureClient): {e}"))?;
        client.Start().map_err(|e| format!("IAudioClient::Start: {e}"))?;
        app.inner.pcm.set_sample_kind(format.bits_per_sample, format.sample_format == "float");
        if let Ok(mut slot) = app.inner.audio_format.lock() {
            *slot = format;
        }
        app.inner.pcm.capturing.store(true, std::sync::atomic::Ordering::Relaxed);
        let nominal = block.max(4).saturating_mul((rate as usize / 50).max(1));
        app.inner.pcm.typical_bytes.store(nominal.max(4), std::sync::atomic::Ordering::Relaxed);
        println!("[Audio] WASAPI loopback started");
        let _ = std::io::Write::flush(&mut std::io::stdout());
        loop {
            let frames = match capture.GetNextPacketSize() {
                Ok(n) => n,
                Err(err) => return Err(format!("IAudioCaptureClient::GetNextPacketSize: {err}")),
            };
            if frames == 0 {
                thread::sleep(Duration::from_millis(5));
                continue;
            }
            let mut data: *mut u8 = std::ptr::null_mut();
            let mut count = 0u32;
            let mut flags = 0u32;
            capture.GetBuffer(&mut data, &mut count, &mut flags, None, None).map_err(|e| format!("IAudioCaptureClient::GetBuffer: {e}"))?;
            let nbytes = count as usize * block.max(1);
            if !data.is_null() && nbytes > 0 && !app.inner.pcm.media_mode.load(std::sync::atomic::Ordering::Relaxed) {
                // AUDCLNT_BUFFERFLAGS_SILENT: the buffer content is undefined and stands for silence.
                let pcm = if flags & 0x2 != 0 {
                    bytes::Bytes::from(vec![0u8; nbytes])
                } else {
                    bytes::Bytes::copy_from_slice(std::slice::from_raw_parts(data, nbytes))
                };
                app.inner.pcm.push(pcm, true);
            }
            capture.ReleaseBuffer(count).map_err(|e| format!("IAudioCaptureClient::ReleaseBuffer: {e}"))?;
        }
    }
}

unsafe fn describe_wave(mix: *mut WAVEFORMATEX) -> AudioFormat {
    let fmt = &*mix;
    let mut is_float = fmt.wFormatTag == 3;
    if fmt.wFormatTag == 0xFFFE {
        let sub = std::ptr::read_unaligned((mix as *const u8).add(24) as *const windows::core::GUID);
        // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT is 00000003-0000-0010-8000-00aa00389b71.
        is_float = sub.data1 == 3;
    }
    AudioFormat::from_wave(fmt.nSamplesPerSec, fmt.wBitsPerSample, fmt.nChannels, is_float)
}
