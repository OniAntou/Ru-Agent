use crate::frame_buffer::CapturedFrame;
use std::time::Duration;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CaptureFailure {
    AccessDenied,
    AccessLost,
    NoPrimaryMonitor,
    Fatal,
}

pub trait FrameSource: Send {
    fn next_frame(&mut self, timeout: Duration) -> Result<Option<CapturedFrame>, CaptureFailure>;
}

#[cfg(windows)]
pub struct DxgiPrimaryFrameSource {
    manager: dxgi_capture_rs::DXGIManager,
}

#[cfg(windows)]
impl DxgiPrimaryFrameSource {
    pub fn new() -> Result<Self, CaptureFailure> {
        let mut manager = dxgi_capture_rs::DXGIManager::new(500).map_err(|error| match error {
            dxgi_capture_rs::OutputDuplicationError::NoOutput => CaptureFailure::NoPrimaryMonitor,
            dxgi_capture_rs::OutputDuplicationError::DeviceError(_) => CaptureFailure::Fatal,
        })?;
        manager.set_capture_source_index(0);
        if manager.geometry() == (0, 0) {
            return Err(CaptureFailure::NoPrimaryMonitor);
        }
        Ok(Self { manager })
    }
}

#[cfg(windows)]
impl FrameSource for DxgiPrimaryFrameSource {
    fn next_frame(&mut self, timeout: Duration) -> Result<Option<CapturedFrame>, CaptureFailure> {
        let timeout_ms = u32::try_from(timeout.as_millis()).unwrap_or(u32::MAX);
        self.manager.set_timeout_ms(timeout_ms);
        let (pixels, (width, height), _metadata) = match self.manager.capture_frame_with_metadata()
        {
            Ok(frame) => frame,
            Err(dxgi_capture_rs::CaptureError::Timeout) => return Ok(None),
            Err(dxgi_capture_rs::CaptureError::AccessDenied) => {
                return Err(CaptureFailure::AccessDenied)
            }
            Err(dxgi_capture_rs::CaptureError::AccessLost) => {
                return Err(CaptureFailure::AccessLost)
            }
            Err(
                dxgi_capture_rs::CaptureError::RefreshFailure
                | dxgi_capture_rs::CaptureError::Fail(_),
            ) => return Err(CaptureFailure::Fatal),
        };
        let width = u32::try_from(width).map_err(|_| CaptureFailure::Fatal)?;
        let height = u32::try_from(height).map_err(|_| CaptureFailure::Fatal)?;
        let bgra = flatten_bgra(pixels, width, height)?;
        let captured_at_unix_ms = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(|_| CaptureFailure::Fatal)?
            .as_millis()
            .try_into()
            .map_err(|_| CaptureFailure::Fatal)?;
        Ok(Some(CapturedFrame {
            width,
            height,
            bgra,
            captured_at_unix_ms,
        }))
    }
}

#[cfg(windows)]
fn flatten_bgra(
    pixels: Vec<dxgi_capture_rs::BGRA8>,
    width: u32,
    height: u32,
) -> Result<Vec<u8>, CaptureFailure> {
    let pixel_count = usize::try_from(width)
        .ok()
        .and_then(|width| {
            usize::try_from(height)
                .ok()
                .and_then(|height| width.checked_mul(height))
        })
        .ok_or(CaptureFailure::Fatal)?;
    if pixels.len() != pixel_count {
        return Err(CaptureFailure::Fatal);
    }
    let byte_count = pixel_count.checked_mul(4).ok_or(CaptureFailure::Fatal)?;
    let mut bgra = Vec::with_capacity(byte_count);
    for pixel in pixels {
        bgra.extend_from_slice(&[pixel.b, pixel.g, pixel.r, pixel.a]);
    }
    Ok(bgra)
}

#[cfg(all(test, windows))]
mod tests {
    use super::*;

    #[test]
    fn flatten_rejects_pixel_count_mismatch() {
        let pixels = vec![dxgi_capture_rs::BGRA8 {
            b: 1,
            g: 2,
            r: 3,
            a: 4,
        }];

        assert_eq!(
            flatten_bgra(pixels, 2, 2).unwrap_err(),
            CaptureFailure::Fatal
        );
    }

    #[test]
    fn flatten_preserves_bgra_component_order() {
        let pixels = vec![dxgi_capture_rs::BGRA8 {
            b: 1,
            g: 2,
            r: 3,
            a: 4,
        }];

        assert_eq!(flatten_bgra(pixels, 1, 1).unwrap(), vec![1, 2, 3, 4]);
    }

    #[test]
    #[ignore = "requires an interactive Windows desktop"]
    fn captures_primary_monitor_into_memory() {
        let mut source = DxgiPrimaryFrameSource::new().unwrap();
        let frame = source.next_frame(Duration::from_secs(2)).unwrap().unwrap();
        assert!(frame.width >= 640);
        assert!(frame.height >= 480);
        assert_eq!(
            frame.bgra.len(),
            frame.width as usize * frame.height as usize * 4
        );
    }
}
