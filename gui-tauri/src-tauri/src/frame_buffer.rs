use image::codecs::jpeg::JpegEncoder;
use image::imageops::FilterType;
use image::{DynamicImage, RgbImage};
use std::collections::VecDeque;
use std::fmt;
use std::sync::Arc;
use std::time::Duration;

pub const SAMPLE_INTERVAL: Duration = Duration::from_millis(500);
pub const RING_CAPACITY: usize = 20;
pub const MAX_FRAME_AGE: Duration = Duration::from_secs(10);
pub const MIN_ANALYSIS_INTERVAL: Duration = Duration::from_millis(1_500);
pub const MAX_ANALYSIS_INTERVAL: Duration = Duration::from_millis(5_000);
pub const ANALYSIS_BATCH_SIZE: usize = 3;
pub const MAX_PREPARED_WIDTH: u32 = 960;
pub const MAX_PREPARED_HEIGHT: u32 = 540;
pub const JPEG_QUALITY: u8 = 80;

const FINGERPRINT_WIDTH: u32 = 64;
const FINGERPRINT_HEIGHT: u32 = 36;
const ACTIVE_CHANGE_THRESHOLD: f32 = 0.08;

pub struct CapturedFrame {
    pub width: u32,
    pub height: u32,
    pub bgra: Vec<u8>,
    pub captured_at_unix_ms: u64,
}

#[derive(Clone)]
pub struct PreparedFrame {
    pub sequence: u64,
    pub captured_at_unix_ms: u64,
    pub jpeg: Arc<[u8]>,
    pub fingerprint: Arc<[u8]>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FramePreparationError {
    InvalidDimensions,
    DimensionOverflow,
    InvalidBgraLength,
    JpegEncoding,
}

impl fmt::Display for FramePreparationError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        let message = match self {
            Self::InvalidDimensions => "captured frame dimensions must be positive",
            Self::DimensionOverflow => "captured frame dimensions overflow addressable memory",
            Self::InvalidBgraLength => "captured frame BGRA length does not match its dimensions",
            Self::JpegEncoding => "captured frame could not be encoded as JPEG",
        };
        formatter.write_str(message)
    }
}

impl std::error::Error for FramePreparationError {}

pub fn prepare_frame(frame: &CapturedFrame) -> Result<PreparedFrame, FramePreparationError> {
    prepare_frame_with_sequence(frame, 0)
}

pub fn prepare_frame_with_sequence(
    frame: &CapturedFrame,
    sequence: u64,
) -> Result<PreparedFrame, FramePreparationError> {
    if frame.width == 0 || frame.height == 0 {
        return Err(FramePreparationError::InvalidDimensions);
    }
    let pixel_count = usize::try_from(frame.width)
        .ok()
        .and_then(|width| {
            usize::try_from(frame.height)
                .ok()
                .and_then(|height| width.checked_mul(height))
        })
        .ok_or(FramePreparationError::DimensionOverflow)?;
    let bgra_length = pixel_count
        .checked_mul(4)
        .ok_or(FramePreparationError::DimensionOverflow)?;
    if frame.bgra.len() != bgra_length {
        return Err(FramePreparationError::InvalidBgraLength);
    }

    let rgb_length = pixel_count
        .checked_mul(3)
        .ok_or(FramePreparationError::DimensionOverflow)?;
    let mut rgb = Vec::with_capacity(rgb_length);
    for pixel in frame.bgra.chunks_exact(4) {
        rgb.extend_from_slice(&[pixel[2], pixel[1], pixel[0]]);
    }
    let image = RgbImage::from_raw(frame.width, frame.height, rgb)
        .ok_or(FramePreparationError::InvalidBgraLength)?;
    let prepared =
        DynamicImage::ImageRgb8(image).thumbnail(MAX_PREPARED_WIDTH, MAX_PREPARED_HEIGHT);

    let fingerprint = prepared
        .resize_exact(FINGERPRINT_WIDTH, FINGERPRINT_HEIGHT, FilterType::Triangle)
        .to_luma8()
        .into_raw();
    let mut jpeg = Vec::new();
    JpegEncoder::new_with_quality(&mut jpeg, JPEG_QUALITY)
        .encode_image(&prepared)
        .map_err(|_| FramePreparationError::JpegEncoding)?;

    Ok(PreparedFrame {
        sequence,
        captured_at_unix_ms: frame.captured_at_unix_ms,
        jpeg: Arc::from(jpeg.into_boxed_slice()),
        fingerprint: Arc::from(fingerprint.into_boxed_slice()),
    })
}

pub fn mean_absolute_difference(left: &[u8], right: &[u8]) -> f32 {
    if left.len() != right.len() {
        return 1.0;
    }
    if left.is_empty() {
        return 0.0;
    }
    let total = left
        .iter()
        .zip(right)
        .map(|(left, right)| left.abs_diff(*right) as u64)
        .sum::<u64>();
    total as f32 / (left.len() as f32 * u8::MAX as f32)
}

pub struct FrameRing {
    frames: VecDeque<PreparedFrame>,
    capacity: usize,
    max_age: Duration,
}

impl FrameRing {
    pub fn new(capacity: usize, max_age: Duration) -> Self {
        Self {
            frames: VecDeque::with_capacity(capacity),
            capacity,
            max_age,
        }
    }

    pub fn push(&mut self, frame: PreparedFrame) {
        self.frames.push_back(frame);
        let newest_timestamp = self
            .frames
            .iter()
            .map(|frame| frame.captured_at_unix_ms)
            .max()
            .unwrap_or_default();
        let max_age_ms = u64::try_from(self.max_age.as_millis()).unwrap_or(u64::MAX);
        self.frames.retain(|frame| {
            newest_timestamp.saturating_sub(frame.captured_at_unix_ms) <= max_age_ms
        });
        while self.frames.len() > self.capacity {
            self.frames.pop_front();
        }
    }

    pub fn recent_batch(&self, count: usize) -> Vec<PreparedFrame> {
        let start = self.frames.len().saturating_sub(count);
        self.frames.iter().skip(start).cloned().collect()
    }

    pub fn clear(&mut self) {
        self.frames.clear();
    }

    pub fn is_empty(&self) -> bool {
        self.frames.is_empty()
    }

    #[cfg(test)]
    fn sequences(&self) -> Vec<u64> {
        self.frames.iter().map(|frame| frame.sequence).collect()
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum GateDecision {
    Wait,
    Analyze,
}

pub struct AdaptiveGate {
    last_analysis_ms: Option<u64>,
}

impl AdaptiveGate {
    pub fn new() -> Self {
        Self {
            last_analysis_ms: None,
        }
    }

    pub fn observe(&mut self, now_ms: u64, change_score: f32) -> GateDecision {
        let interval = if change_score >= ACTIVE_CHANGE_THRESHOLD {
            MIN_ANALYSIS_INTERVAL
        } else {
            MAX_ANALYSIS_INTERVAL
        };
        let interval_ms = u64::try_from(interval.as_millis()).unwrap_or(u64::MAX);
        if self
            .last_analysis_ms
            .is_none_or(|previous| now_ms.saturating_sub(previous) >= interval_ms)
        {
            self.last_analysis_ms = Some(now_ms);
            GateDecision::Analyze
        } else {
            GateDecision::Wait
        }
    }
}

impl Default for AdaptiveGate {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use std::time::Duration;

    fn sample(sequence: u64, captured_at_unix_ms: u64) -> PreparedFrame {
        PreparedFrame {
            sequence,
            captured_at_unix_ms,
            jpeg: Arc::from([sequence as u8]),
            fingerprint: Arc::from([sequence as u8]),
        }
    }

    #[test]
    fn ring_overwrites_oldest_and_clear_releases_every_frame() {
        let mut ring = FrameRing::new(3, Duration::from_secs(10));
        for sequence in 0..4 {
            ring.push(sample(sequence, sequence * 500));
        }
        assert_eq!(ring.sequences(), vec![1, 2, 3]);
        ring.clear();
        assert!(ring.is_empty());
    }

    #[test]
    fn ring_discards_frames_older_than_its_ram_window() {
        let mut ring = FrameRing::new(20, Duration::from_secs(1));
        ring.push(sample(0, 1_000));
        ring.push(sample(1, 1_500));
        ring.push(sample(2, 2_001));

        assert_eq!(ring.sequences(), vec![1, 2]);
    }

    #[test]
    fn recent_batch_is_chronological_and_bounded() {
        let mut ring = FrameRing::new(5, Duration::from_secs(10));
        for sequence in 0..5 {
            ring.push(sample(sequence, sequence * 500));
        }

        let sequences = ring
            .recent_batch(3)
            .into_iter()
            .map(|frame| frame.sequence)
            .collect::<Vec<_>>();
        assert_eq!(sequences, vec![2, 3, 4]);
    }

    #[test]
    fn active_scene_batches_three_frames_but_static_scene_backs_off() {
        let mut gate = AdaptiveGate::new();
        assert_eq!(gate.observe(0, 0.90), GateDecision::Analyze);
        assert_eq!(gate.observe(500, 0.90), GateDecision::Wait);
        assert_eq!(gate.observe(1_500, 0.90), GateDecision::Analyze);
        assert_eq!(gate.observe(2_000, 0.01), GateDecision::Wait);
        assert_eq!(gate.observe(6_500, 0.01), GateDecision::Analyze);
    }

    #[test]
    fn prepare_frame_never_accepts_invalid_bgra_length() {
        assert!(prepare_frame(&CapturedFrame {
            width: 10,
            height: 10,
            bgra: vec![0; 3],
            captured_at_unix_ms: 1,
        })
        .is_err());
    }

    #[test]
    fn prepare_frame_stays_in_memory_and_bounds_dimensions() {
        let captured = CapturedFrame {
            width: 1_920,
            height: 1_080,
            bgra: vec![127; 1_920 * 1_080 * 4],
            captured_at_unix_ms: 123,
        };

        let prepared = prepare_frame_with_sequence(&captured, 7).unwrap();

        assert_eq!(prepared.sequence, 7);
        assert_eq!(prepared.captured_at_unix_ms, 123);
        assert_eq!(prepared.fingerprint.len(), 64 * 36);
        assert!(prepared.jpeg.starts_with(&[0xff, 0xd8]));
        assert!(prepared.jpeg.ends_with(&[0xff, 0xd9]));
    }

    #[test]
    fn mean_absolute_difference_is_normalized() {
        assert_eq!(mean_absolute_difference(&[0, 0], &[255, 255]), 1.0);
        assert_eq!(mean_absolute_difference(&[10, 20], &[10, 20]), 0.0);
        assert_eq!(mean_absolute_difference(&[0], &[]), 1.0);
    }
}
