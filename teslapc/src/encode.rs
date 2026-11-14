//! In-process JPEG encode. Quality is the shared `JPEG_QUALITY` (60).

use crate::decisions::stream::JPEG_QUALITY;

pub fn encode_bgra_jpeg(bgra: &[u8], src_w: i32, src_h: i32, out_w: i32, out_h: i32) -> Result<Vec<u8>, String> {
    if src_w <= 0 || src_h <= 0 || out_w <= 0 || out_h <= 0 {
        return Err("bad frame size".into());
    }
    let need = (src_w as usize).saturating_mul(src_h as usize).saturating_mul(4);
    if bgra.len() < need {
        return Err("short frame".into());
    }
    let rgb = scale_to_rgb(bgra, src_w, src_h, out_w, out_h);
    let mut out = Vec::new();
    let mut encoder = image::codecs::jpeg::JpegEncoder::new_with_quality(&mut out, JPEG_QUALITY);
    encoder
        .encode(&rgb, out_w as u32, out_h as u32, image::ExtendedColorType::Rgb8)
        .map_err(|e| e.to_string())?;
    Ok(out)
}

fn scale_to_rgb(bgra: &[u8], src_w: i32, src_h: i32, out_w: i32, out_h: i32) -> Vec<u8> {
    let mut rgb = vec![0u8; (out_w as usize) * (out_h as usize) * 3];
    for y in 0..out_h {
        let sy = ((y as i64) * (src_h as i64) / (out_h as i64)).min(src_h as i64 - 1) as i32;
        for x in 0..out_w {
            let sx = ((x as i64) * (src_w as i64) / (out_w as i64)).min(src_w as i64 - 1) as i32;
            let si = ((sy as usize) * (src_w as usize) + (sx as usize)) * 4;
            let di = ((y as usize) * (out_w as usize) + (x as usize)) * 3;
            rgb[di] = bgra[si + 2];
            rgb[di + 1] = bgra[si + 1];
            rgb[di + 2] = bgra[si];
        }
    }
    rgb
}
