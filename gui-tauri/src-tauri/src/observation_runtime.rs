use crate::backend_bridge::{BackendBridge, BridgeError, ReactionClient, ReactionRequestBody};
use crate::frame_buffer::{
    mean_absolute_difference, prepare_frame_with_sequence, AdaptiveGate, FrameRing, GateDecision,
    PreparedFrame, ANALYSIS_BATCH_SIZE, MAX_FRAME_AGE, RING_CAPACITY, SAMPLE_INTERVAL,
};
use crate::screen_capture::{CaptureFailure, DxgiPrimaryFrameSource, FrameSource};
use crate::vision_runtime::{
    LlamaVisionRuntime, VisionAnalyzer, VisionError, VisionObservation, VisionServerConfig,
};
use crate::BackendState;
use crossbeam_channel::{bounded, Receiver, RecvTimeoutError, Sender, TrySendError};
use serde::Serialize;
use std::collections::VecDeque;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tauri::{AppHandle, Emitter, Manager, State};

const MAX_EVENT_AGE_MS: u64 = 4_500;
const MAX_CLOUD_FAILURES: u8 = 3;

#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum ObservationPhase {
    Off,
    Starting,
    Observing,
    Paused,
    Error,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ObservationSnapshot {
    pub phase: ObservationPhase,
    pub message: String,
    pub last_latency_ms: Option<u64>,
    pub comments_last_minute: usize,
    pub dropped_stale: u64,
    pub restart_count: u8,
}

impl Default for ObservationSnapshot {
    fn default() -> Self {
        Self {
            phase: ObservationPhase::Off,
            message: "Screen observation is off.".to_owned(),
            last_latency_ms: None,
            comments_last_minute: 0,
            dropped_stale: 0,
            restart_count: 0,
        }
    }
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ObservationMetricsSnapshot {
    pub sample_count: usize,
    pub p50_ms: Option<u64>,
    pub p95_ms: Option<u64>,
    pub worst_ms: Option<u64>,
    pub dropped_stale: u64,
    pub comments_last_minute: usize,
    pub frames_captured: u64,
    pub vision_analyses: u64,
    pub vision_candidates: u64,
    pub cloud_attempts: u64,
    pub cloud_skips: u64,
    pub cloud_failures: u64,
    pub reactions_emitted: u64,
    pub rejected_confidence: u64,
    pub rejected_novelty: u64,
    pub rejected_worthiness: u64,
    pub last_confidence: Option<f32>,
    pub last_novelty: Option<f32>,
    pub last_worthiness: Option<f32>,
    pub last_pre_cloud_age_ms: Option<u64>,
    pub last_cloud_ms: Option<u64>,
    pub last_post_cloud_age_ms: Option<u64>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ScreenReactionEvent {
    pub observation_id: String,
    pub text: String,
    pub voice_label: String,
    pub voice_speed: f32,
    pub event_unix_ms: u64,
}

#[derive(Default)]
struct ObservationMetrics {
    latencies_ms: VecDeque<u64>,
    comment_times_ms: VecDeque<u64>,
    dropped_stale: u64,
    frames_captured: u64,
    vision_analyses: u64,
    vision_candidates: u64,
    cloud_attempts: u64,
    cloud_skips: u64,
    cloud_failures: u64,
    reactions_emitted: u64,
    rejected_confidence: u64,
    rejected_novelty: u64,
    rejected_worthiness: u64,
    last_confidence: Option<f32>,
    last_novelty: Option<f32>,
    last_worthiness: Option<f32>,
    last_pre_cloud_age_ms: Option<u64>,
    last_cloud_ms: Option<u64>,
    last_post_cloud_age_ms: Option<u64>,
}

impl ObservationMetrics {
    fn record_latency(&mut self, latency_ms: u64) {
        if self.latencies_ms.len() == 100 {
            self.latencies_ms.pop_front();
        }
        self.latencies_ms.push_back(latency_ms);
    }

    fn record_comment(&mut self, now_ms: u64) {
        self.comment_times_ms.push_back(now_ms);
        self.prune_comments(now_ms);
    }

    fn prune_comments(&mut self, now_ms: u64) {
        while self
            .comment_times_ms
            .front()
            .is_some_and(|at| now_ms.saturating_sub(*at) > 60_000)
        {
            self.comment_times_ms.pop_front();
        }
    }

    fn snapshot(&mut self, now_ms: u64) -> ObservationMetricsSnapshot {
        self.prune_comments(now_ms);
        let mut sorted = self.latencies_ms.iter().copied().collect::<Vec<_>>();
        sorted.sort_unstable();
        ObservationMetricsSnapshot {
            sample_count: sorted.len(),
            p50_ms: percentile(&sorted, 50),
            p95_ms: percentile(&sorted, 95),
            worst_ms: sorted.last().copied(),
            dropped_stale: self.dropped_stale,
            comments_last_minute: self.comment_times_ms.len(),
            frames_captured: self.frames_captured,
            vision_analyses: self.vision_analyses,
            vision_candidates: self.vision_candidates,
            cloud_attempts: self.cloud_attempts,
            cloud_skips: self.cloud_skips,
            cloud_failures: self.cloud_failures,
            reactions_emitted: self.reactions_emitted,
            rejected_confidence: self.rejected_confidence,
            rejected_novelty: self.rejected_novelty,
            rejected_worthiness: self.rejected_worthiness,
            last_confidence: self.last_confidence,
            last_novelty: self.last_novelty,
            last_worthiness: self.last_worthiness,
            last_pre_cloud_age_ms: self.last_pre_cloud_age_ms,
            last_cloud_ms: self.last_cloud_ms,
            last_post_cloud_age_ms: self.last_post_cloud_age_ms,
        }
    }
}

fn percentile(sorted: &[u64], percentile: usize) -> Option<u64> {
    if sorted.is_empty() {
        return None;
    }
    let index = (sorted.len() * percentile).div_ceil(100).saturating_sub(1);
    sorted.get(index).copied()
}

pub struct ObservationState {
    runtime: Mutex<Option<ObservationRuntime>>,
    snapshot: Arc<Mutex<ObservationSnapshot>>,
    metrics: Arc<Mutex<ObservationMetrics>>,
}

impl ObservationState {
    pub fn new() -> Self {
        Self {
            runtime: Mutex::new(None),
            snapshot: Arc::new(Mutex::new(ObservationSnapshot::default())),
            metrics: Arc::new(Mutex::new(ObservationMetrics::default())),
        }
    }

    fn snapshot(&self) -> ObservationSnapshot {
        self.snapshot
            .lock()
            .map(|value| value.clone())
            .unwrap_or_default()
    }

    fn metrics(&self) -> ObservationMetricsSnapshot {
        self.metrics
            .lock()
            .map(|mut value| value.snapshot(now_unix_ms()))
            .unwrap_or(ObservationMetricsSnapshot {
                sample_count: 0,
                p50_ms: None,
                p95_ms: None,
                worst_ms: None,
                dropped_stale: 0,
                comments_last_minute: 0,
                frames_captured: 0,
                vision_analyses: 0,
                vision_candidates: 0,
                cloud_attempts: 0,
                cloud_skips: 0,
                cloud_failures: 0,
                reactions_emitted: 0,
                rejected_confidence: 0,
                rejected_novelty: 0,
                rejected_worthiness: 0,
                last_confidence: None,
                last_novelty: None,
                last_worthiness: None,
                last_pre_cloud_age_ms: None,
                last_cloud_ms: None,
                last_post_cloud_age_ms: None,
            })
    }
}

impl Default for ObservationState {
    fn default() -> Self {
        Self::new()
    }
}

struct VisionBatch {
    frames: Vec<PreparedFrame>,
    event_unix_ms: u64,
}

struct ReactionCandidate {
    observation_id: String,
    event_unix_ms: u64,
    observation: VisionObservation,
}

fn observation_is_candidate(observation: &VisionObservation) -> bool {
    observation.confidence >= 0.55
}

pub struct ObservationRuntime {
    stop: Arc<AtomicBool>,
    voice_reserved: Arc<AtomicBool>,
    workers: Vec<JoinHandle<()>>,
    ring: Arc<Mutex<FrameRing>>,
    buffered_bytes: Arc<AtomicUsize>,
    pending_id: Arc<Mutex<Option<String>>>,
    awaiting_voice: Arc<Mutex<Option<(String, u64)>>>,
    bridge: Arc<BackendBridge>,
}

impl ObservationRuntime {
    fn start(
        app: AppHandle,
        project_root: PathBuf,
        bridge: Arc<BackendBridge>,
        snapshot: Arc<Mutex<ObservationSnapshot>>,
        metrics: Arc<Mutex<ObservationMetrics>>,
    ) -> Self {
        let stop = Arc::new(AtomicBool::new(false));
        let voice_reserved = Arc::new(AtomicBool::new(false));
        let ring = Arc::new(Mutex::new(FrameRing::new(RING_CAPACITY, MAX_FRAME_AGE)));
        let buffered_bytes = Arc::new(AtomicUsize::new(0));
        let pending_id = Arc::new(Mutex::new(None));
        let awaiting_voice = Arc::new(Mutex::new(None));
        let sequence = Arc::new(AtomicU64::new(1));
        let (batch_tx, batch_rx) = bounded::<VisionBatch>(1);
        let batch_drain = batch_rx.clone();
        let (reaction_tx, reaction_rx) = bounded::<ReactionCandidate>(1);
        let reaction_drain = reaction_rx.clone();
        let (capture_ready_tx, capture_ready_rx) = bounded::<()>(1);

        let capture_worker = spawn_capture_worker(
            stop.clone(),
            ring.clone(),
            buffered_bytes.clone(),
            sequence.clone(),
            batch_tx,
            batch_drain,
            metrics.clone(),
            snapshot.clone(),
            app.clone(),
            capture_ready_tx,
        );
        let vision_worker = spawn_vision_worker(
            stop.clone(),
            voice_reserved.clone(),
            ring.clone(),
            buffered_bytes.clone(),
            batch_rx,
            reaction_tx,
            reaction_drain,
            pending_id.clone(),
            bridge.clone(),
            metrics.clone(),
            snapshot.clone(),
            project_root,
            app.clone(),
            capture_ready_rx,
        );
        let reaction_worker = spawn_reaction_worker(
            stop.clone(),
            voice_reserved.clone(),
            reaction_rx,
            pending_id.clone(),
            awaiting_voice.clone(),
            bridge.clone(),
            snapshot,
            metrics,
            app,
            ring.clone(),
            buffered_bytes.clone(),
        );
        Self {
            stop,
            voice_reserved,
            workers: vec![capture_worker, vision_worker, reaction_worker],
            ring,
            buffered_bytes,
            pending_id,
            awaiting_voice,
            bridge,
        }
    }

    fn stop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Ok(pending) = self.pending_id.lock() {
            if let Some(observation_id) = pending.as_deref() {
                let _ = self.bridge.cancel(observation_id);
            }
        }
        for worker in self.workers.drain(..) {
            let _ = worker.join();
        }
        if let Ok(mut ring) = self.ring.lock() {
            ring.clear();
        }
        self.buffered_bytes.store(0, Ordering::Release);
        self.voice_reserved.store(false, Ordering::Release);
        if let Ok(mut pending) = self.pending_id.lock() {
            pending.take();
        }
        if let Ok(mut awaiting) = self.awaiting_voice.lock() {
            awaiting.take();
        }
    }
}

impl Drop for ObservationRuntime {
    fn drop(&mut self) {
        self.stop();
    }
}

#[allow(clippy::too_many_arguments)]
fn spawn_capture_worker(
    stop: Arc<AtomicBool>,
    ring: Arc<Mutex<FrameRing>>,
    buffered_bytes: Arc<AtomicUsize>,
    sequence: Arc<AtomicU64>,
    batch_tx: Sender<VisionBatch>,
    batch_drain: Receiver<VisionBatch>,
    metrics: Arc<Mutex<ObservationMetrics>>,
    snapshot: Arc<Mutex<ObservationSnapshot>>,
    app: AppHandle,
    capture_ready: Sender<()>,
) -> JoinHandle<()> {
    thread::spawn(move || {
        let mut source = match DxgiPrimaryFrameSource::new() {
            Ok(source) => source,
            Err(_) => {
                set_phase(
                    &snapshot,
                    &app,
                    ObservationPhase::Error,
                    "Primary monitor capture is unavailable.",
                );
                stop.store(true, Ordering::Release);
                return;
            }
        };
        let _ = capture_ready.send(());
        let mut gate = AdaptiveGate::new();
        let mut previous_fingerprint: Option<Arc<[u8]>> = None;
        while !stop.load(Ordering::Acquire) {
            let sample_started = Instant::now();
            let captured = match source.next_frame(SAMPLE_INTERVAL) {
                Ok(Some(frame)) => frame,
                Ok(None) => continue,
                Err(CaptureFailure::AccessLost) => match DxgiPrimaryFrameSource::new() {
                    Ok(recreated) => {
                        source = recreated;
                        continue;
                    }
                    Err(_) => break,
                },
                Err(_) => break,
            };
            let sequence = sequence.fetch_add(1, Ordering::Relaxed);
            let Ok(prepared) = prepare_frame_with_sequence(&captured, sequence) else {
                continue;
            };
            if let Ok(mut value) = metrics.lock() {
                value.frames_captured += 1;
            }
            let change = previous_fingerprint
                .as_deref()
                .map(|previous| mean_absolute_difference(previous, &prepared.fingerprint))
                .unwrap_or(1.0);
            previous_fingerprint = Some(prepared.fingerprint.clone());
            let now_ms = captured.captured_at_unix_ms;
            let mut guard = match ring.lock() {
                Ok(guard) => guard,
                Err(_) => break,
            };
            guard.push(prepared);
            buffered_bytes.store(guard.buffered_bytes(), Ordering::Release);
            if gate.observe(now_ms, change) == GateDecision::Analyze {
                let frames = guard.recent_batch(ANALYSIS_BATCH_SIZE);
                drop(guard);
                if !frames.is_empty() {
                    send_latest(
                        &batch_tx,
                        &batch_drain,
                        VisionBatch {
                            frames,
                            event_unix_ms: now_ms,
                        },
                    );
                }
            }
            sleep_remaining(
                &stop,
                SAMPLE_INTERVAL.saturating_sub(sample_started.elapsed()),
            );
        }
        if !stop.load(Ordering::Acquire) {
            clear_frames(&ring, &buffered_bytes);
            set_phase(
                &snapshot,
                &app,
                ObservationPhase::Error,
                "Primary monitor capture stopped unexpectedly.",
            );
            stop.store(true, Ordering::Release);
        }
    })
}

#[allow(clippy::too_many_arguments)]
fn spawn_vision_worker(
    stop: Arc<AtomicBool>,
    voice_reserved: Arc<AtomicBool>,
    ring: Arc<Mutex<FrameRing>>,
    buffered_bytes: Arc<AtomicUsize>,
    batch_rx: Receiver<VisionBatch>,
    reaction_tx: Sender<ReactionCandidate>,
    reaction_drain: Receiver<ReactionCandidate>,
    pending_id: Arc<Mutex<Option<String>>>,
    bridge: Arc<BackendBridge>,
    metrics: Arc<Mutex<ObservationMetrics>>,
    snapshot: Arc<Mutex<ObservationSnapshot>>,
    project_root: PathBuf,
    app: AppHandle,
    capture_ready: Receiver<()>,
) -> JoinHandle<()> {
    thread::spawn(move || {
        if capture_ready.recv_timeout(Duration::from_secs(2)).is_err() {
            set_phase(
                &snapshot,
                &app,
                ObservationPhase::Error,
                "Primary monitor capture did not start.",
            );
            stop.store(true, Ordering::Release);
            return;
        }
        let mut restart_count = 0_u8;
        loop {
            if stop.load(Ordering::Acquire) {
                return;
            }
            let config = VisionServerConfig::for_project(&project_root, 18111);
            let assign_app = app.clone();
            let runtime = LlamaVisionRuntime::start_cancellable(
                config,
                &move |child| assign_app.state::<BackendState>().assign_owned_child(child),
                &|| stop.load(Ordering::Acquire),
            );
            let mut vision = match runtime {
                Ok(runtime) => runtime,
                Err(VisionError::Cancelled) => return,
                Err(_) if restart_count < 2 => {
                    restart_count += 1;
                    set_restart_count(&snapshot, restart_count);
                    sleep_interruptible(&stop, if restart_count == 1 { 1 } else { 3 });
                    continue;
                }
                Err(_) => {
                    clear_frames(&ring, &buffered_bytes);
                    set_phase(
                        &snapshot,
                        &app,
                        ObservationPhase::Error,
                        "Local vision could not start.",
                    );
                    stop.store(true, Ordering::Release);
                    return;
                }
            };
            set_phase(
                &snapshot,
                &app,
                ObservationPhase::Observing,
                "Watching the primary monitor.",
            );
            loop {
                if stop.load(Ordering::Acquire) {
                    vision.stop();
                    return;
                }
                if voice_reserved.load(Ordering::Acquire) {
                    thread::sleep(Duration::from_millis(50));
                    continue;
                }
                let batch = match batch_rx.recv_timeout(Duration::from_millis(100)) {
                    Ok(batch) => batch,
                    Err(RecvTimeoutError::Timeout) => continue,
                    Err(RecvTimeoutError::Disconnected) => {
                        vision.stop();
                        return;
                    }
                };
                if let Ok(mut value) = metrics.lock() {
                    value.vision_analyses += 1;
                }
                match vision.analyze(&batch.frames) {
                    Ok(observation) => {
                        let rejected_confidence = observation.confidence < 0.55;
                        let rejected_novelty = observation.novelty < 0.55;
                        let rejected_worthiness = observation.comment_worthiness < 0.60;
                        if let Ok(mut value) = metrics.lock() {
                            value.last_confidence = Some(observation.confidence);
                            value.last_novelty = Some(observation.novelty);
                            value.last_worthiness = Some(observation.comment_worthiness);
                            value.rejected_confidence += u64::from(rejected_confidence);
                            value.rejected_novelty += u64::from(rejected_novelty);
                            value.rejected_worthiness += u64::from(rejected_worthiness);
                        }
                        if !observation_is_candidate(&observation) {
                            continue;
                        }
                        if let Ok(mut value) = metrics.lock() {
                            value.vision_candidates += 1;
                        }
                        let observation_id = format!(
                            "screen-{}",
                            batch
                                .frames
                                .last()
                                .map(|frame| frame.sequence)
                                .unwrap_or_default()
                        );
                        if let Ok(pending) = pending_id.lock() {
                            if let Some(previous) = pending.as_deref() {
                                let _ = bridge.cancel(previous);
                            }
                        }
                        send_latest(
                            &reaction_tx,
                            &reaction_drain,
                            ReactionCandidate {
                                observation_id,
                                event_unix_ms: batch.event_unix_ms,
                                observation,
                            },
                        );
                    }
                    Err(VisionError::ServerStart | VisionError::Http) => {
                        vision.stop();
                        if restart_count < 2 {
                            restart_count += 1;
                            set_restart_count(&snapshot, restart_count);
                            sleep_interruptible(&stop, if restart_count == 1 { 1 } else { 3 });
                            break;
                        }
                        set_phase(
                            &snapshot,
                            &app,
                            ObservationPhase::Error,
                            "Local vision stopped after repeated failures.",
                        );
                        clear_frames(&ring, &buffered_bytes);
                        stop.store(true, Ordering::Release);
                        return;
                    }
                    Err(
                        VisionError::InvalidSchema
                        | VisionError::Cancelled
                        | VisionError::MissingAsset
                        | VisionError::Gemini(_),
                    ) => {}
                }
            }
        }
    })
}

#[allow(clippy::too_many_arguments)]
fn spawn_reaction_worker(
    stop: Arc<AtomicBool>,
    voice_reserved: Arc<AtomicBool>,
    reaction_rx: Receiver<ReactionCandidate>,
    pending_id: Arc<Mutex<Option<String>>>,
    awaiting_voice: Arc<Mutex<Option<(String, u64)>>>,
    bridge: Arc<BackendBridge>,
    snapshot: Arc<Mutex<ObservationSnapshot>>,
    metrics: Arc<Mutex<ObservationMetrics>>,
    app: AppHandle,
    ring: Arc<Mutex<FrameRing>>,
    buffered_bytes: Arc<AtomicUsize>,
) -> JoinHandle<()> {
    thread::spawn(move || {
        let mut cloud_failures = 0_u8;
        while !stop.load(Ordering::Acquire) {
            let candidate = match reaction_rx.recv_timeout(Duration::from_millis(100)) {
                Ok(candidate) => candidate,
                Err(RecvTimeoutError::Timeout) => continue,
                Err(RecvTimeoutError::Disconnected) => return,
            };
            if let Ok(mut pending) = pending_id.lock() {
                *pending = Some(candidate.observation_id.clone());
            }
            let age_ms = now_unix_ms().saturating_sub(candidate.event_unix_ms);
            if age_ms > MAX_EVENT_AGE_MS {
                record_stale(&snapshot, &metrics);
                clear_pending(&pending_id, &candidate.observation_id);
                continue;
            }
            let request = ReactionRequestBody {
                observation_id: candidate.observation_id.clone(),
                event_age_ms: age_ms,
                observation: candidate.observation,
            };
            if let Ok(mut value) = metrics.lock() {
                value.cloud_attempts += 1;
                value.last_pre_cloud_age_ms = Some(age_ms);
            }
            let cloud_started = Instant::now();
            let reply = bridge.react(&request);
            let cloud_ms = u64::try_from(cloud_started.elapsed().as_millis()).unwrap_or(u64::MAX);
            let post_cloud_age_ms = now_unix_ms().saturating_sub(candidate.event_unix_ms);
            if let Ok(mut value) = metrics.lock() {
                value.last_cloud_ms = Some(cloud_ms);
                value.last_post_cloud_age_ms = Some(post_cloud_age_ms);
            }
            match reply {
                Ok(reply) => {
                    cloud_failures = 0;
                    clear_pending(&pending_id, &candidate.observation_id);
                    let Some(reaction) = reply.reaction else {
                        if let Ok(mut value) = metrics.lock() {
                            value.cloud_skips += 1;
                        }
                        continue;
                    };
                    if post_cloud_age_ms > MAX_EVENT_AGE_MS {
                        record_stale(&snapshot, &metrics);
                        let _ = bridge.ack(&candidate.observation_id, false);
                        continue;
                    }
                    voice_reserved.store(true, Ordering::Release);
                    if let Ok(mut awaiting) = awaiting_voice.lock() {
                        *awaiting =
                            Some((reaction.observation_id.clone(), candidate.event_unix_ms));
                    }
                    let emitted_id = reaction.observation_id.clone();
                    if app
                        .emit(
                            "ru://screen-reaction",
                            ScreenReactionEvent {
                                observation_id: reaction.observation_id,
                                text: reaction.text,
                                voice_label: reaction.voice_label,
                                voice_speed: reaction.voice_speed,
                                event_unix_ms: candidate.event_unix_ms,
                            },
                        )
                        .is_err()
                    {
                        let _ = bridge.ack(&emitted_id, false);
                        voice_reserved.store(false, Ordering::Release);
                        if let Ok(mut awaiting) = awaiting_voice.lock() {
                            awaiting.take();
                        }
                    } else if let Ok(mut value) = metrics.lock() {
                        value.reactions_emitted += 1;
                    }
                }
                Err(
                    error @ (BridgeError::Transport
                    | BridgeError::Provider
                    | BridgeError::Unauthorized),
                ) => {
                    eprintln!("observation: cloud react failed: {:?}", error);
                    clear_pending(&pending_id, &candidate.observation_id);
                    cloud_failures += 1;
                    if let Ok(mut value) = metrics.lock() {
                        value.cloud_failures += 1;
                    }
                    if cloud_failures >= MAX_CLOUD_FAILURES {
                        set_phase(
                            &snapshot,
                            &app,
                            ObservationPhase::Paused,
                            "Screen observation paused after cloud failures.",
                        );
                        clear_frames(&ring, &buffered_bytes);
                        stop.store(true, Ordering::Release);
                        return;
                    }
                }
                Err(error @ BridgeError::InvalidResponse) => {
                    eprintln!("observation: cloud react invalid response: {:?}", error);
                    clear_pending(&pending_id, &candidate.observation_id);
                    if let Ok(mut value) = metrics.lock() {
                        value.cloud_failures += 1;
                    }
                }
            }
        }
    })
}

fn send_latest<T>(sender: &Sender<T>, drain: &Receiver<T>, value: T) {
    match sender.try_send(value) {
        Ok(()) => {}
        Err(TrySendError::Full(value)) => {
            let _ = drain.try_recv();
            let _ = sender.try_send(value);
        }
        Err(TrySendError::Disconnected(_)) => {}
    }
}

fn clear_pending(pending: &Mutex<Option<String>>, observation_id: &str) {
    if let Ok(mut guard) = pending.lock() {
        if guard.as_deref() == Some(observation_id) {
            guard.take();
        }
    }
}

fn clear_frames(ring: &Mutex<FrameRing>, buffered_bytes: &AtomicUsize) {
    if let Ok(mut frames) = ring.lock() {
        frames.clear();
    }
    buffered_bytes.store(0, Ordering::Release);
}

fn record_stale(snapshot: &Mutex<ObservationSnapshot>, metrics: &Mutex<ObservationMetrics>) {
    if let Ok(mut value) = metrics.lock() {
        value.dropped_stale += 1;
    }
    if let Ok(mut value) = snapshot.lock() {
        value.dropped_stale += 1;
    }
}

fn set_restart_count(snapshot: &Mutex<ObservationSnapshot>, restart_count: u8) {
    if let Ok(mut value) = snapshot.lock() {
        value.restart_count = restart_count;
    }
}

fn set_phase(
    snapshot: &Mutex<ObservationSnapshot>,
    app: &AppHandle,
    phase: ObservationPhase,
    message: &'static str,
) {
    let current = if let Ok(mut value) = snapshot.lock() {
        value.phase = phase;
        value.message = message.to_owned();
        value.clone()
    } else {
        return;
    };
    let _ = app.emit("ru://observation-state", current);
}

fn sleep_interruptible(stop: &AtomicBool, seconds: u64) {
    for _ in 0..seconds * 10 {
        if stop.load(Ordering::Acquire) {
            break;
        }
        thread::sleep(Duration::from_millis(100));
    }
}

fn sleep_remaining(stop: &AtomicBool, duration: Duration) {
    let deadline = Instant::now() + duration;
    while Instant::now() < deadline && !stop.load(Ordering::Acquire) {
        thread::sleep(
            Duration::from_millis(50).min(deadline.saturating_duration_since(Instant::now())),
        );
    }
}

fn now_unix_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .try_into()
        .unwrap_or(u64::MAX)
}

#[tauri::command]
pub(crate) fn observation_start(
    app: AppHandle,
    backend: State<'_, BackendState>,
    state: State<'_, ObservationState>,
) -> Result<ObservationSnapshot, String> {
    let mut runtime_guard = state
        .runtime
        .lock()
        .map_err(|_| "Observation state is unavailable.".to_owned())?;
    if runtime_guard.is_some() {
        return Ok(state.snapshot());
    }
    set_phase(
        &state.snapshot,
        &app,
        ObservationPhase::Starting,
        "Preparing screen observation.",
    );
    let bridge = match BackendBridge::new(backend.runtime_config()) {
        Ok(bridge) => Arc::new(bridge),
        Err(_) => {
            set_phase(
                &state.snapshot,
                &app,
                ObservationPhase::Error,
                "The local backend bridge is unavailable.",
            );
            return Err("The local backend bridge is unavailable.".to_owned());
        }
    };
    if bridge.preflight().is_err() {
        set_phase(
            &state.snapshot,
            &app,
            ObservationPhase::Error,
            "OmniVoice or the cloud backend is not ready.",
        );
        return Err("OmniVoice or the cloud backend is not ready.".to_owned());
    }
    let project_root =
        crate::project_root().map_err(|_| "Ru project assets are unavailable.".to_owned())?;
    *runtime_guard = Some(ObservationRuntime::start(
        app,
        project_root,
        bridge,
        state.snapshot.clone(),
        state.metrics.clone(),
    ));
    Ok(state.snapshot())
}

#[tauri::command]
pub(crate) fn observation_stop(
    app: AppHandle,
    state: State<'_, ObservationState>,
) -> Result<ObservationSnapshot, String> {
    let mut runtime = state
        .runtime
        .lock()
        .map_err(|_| "Observation state is unavailable.".to_owned())?
        .take();
    if let Some(runtime) = runtime.as_mut() {
        runtime.stop();
    }
    set_phase(
        &state.snapshot,
        &app,
        ObservationPhase::Off,
        "Screen observation is off.",
    );
    Ok(state.snapshot())
}

#[tauri::command]
pub(crate) fn observation_status(state: State<'_, ObservationState>) -> ObservationSnapshot {
    state.snapshot()
}

#[tauri::command]
pub(crate) fn observation_metrics(
    state: State<'_, ObservationState>,
) -> ObservationMetricsSnapshot {
    state.metrics()
}

#[tauri::command]
pub(crate) fn observation_voice_started(
    observation_id: String,
    event_age_ms: u64,
    state: State<'_, ObservationState>,
) -> Result<(), String> {
    let runtime = state
        .runtime
        .lock()
        .map_err(|_| "Observation state is unavailable.".to_owned())?;
    let runtime = runtime
        .as_ref()
        .ok_or_else(|| "Observation is not running.".to_owned())?;
    let awaiting = runtime
        .awaiting_voice
        .lock()
        .map_err(|_| "Observation voice state is unavailable.".to_owned())?;
    if awaiting.as_ref().map(|value| value.0.as_str()) != Some(observation_id.as_str()) {
        return Err("Observation reaction is no longer current.".to_owned());
    }
    drop(awaiting);
    if let Ok(mut metrics) = state.metrics.lock() {
        metrics.record_latency(event_age_ms);
    }
    if let Ok(mut snapshot) = state.snapshot.lock() {
        snapshot.last_latency_ms = Some(event_age_ms);
    }
    Ok(())
}

#[tauri::command]
pub(crate) fn observation_voice_finished(
    observation_id: String,
    played: bool,
    state: State<'_, ObservationState>,
) -> Result<(), String> {
    let runtime = state
        .runtime
        .lock()
        .map_err(|_| "Observation state is unavailable.".to_owned())?;
    let runtime = runtime
        .as_ref()
        .ok_or_else(|| "Observation is not running.".to_owned())?;
    let mut awaiting = runtime
        .awaiting_voice
        .lock()
        .map_err(|_| "Observation voice state is unavailable.".to_owned())?;
    if awaiting.as_ref().map(|value| value.0.as_str()) != Some(observation_id.as_str()) {
        return Err("Observation reaction is no longer current.".to_owned());
    }
    awaiting.take();
    runtime.voice_reserved.store(false, Ordering::Release);
    let _ = runtime.bridge.ack(&observation_id, played);
    if played {
        let now = now_unix_ms();
        let comments = if let Ok(mut metrics) = state.metrics.lock() {
            metrics.record_comment(now);
            metrics.comment_times_ms.len()
        } else {
            0
        };
        if let Ok(mut snapshot) = state.snapshot.lock() {
            snapshot.comments_last_minute = comments;
        }
    }
    Ok(())
}

pub(crate) fn stop_observation_state(state: &ObservationState) {
    if let Ok(mut runtime) = state.runtime.lock() {
        if let Some(mut active) = runtime.take() {
            active.stop();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::BackendRuntimeConfig;

    fn idle_runtime(active_workers: Arc<AtomicUsize>) -> ObservationRuntime {
        let stop = Arc::new(AtomicBool::new(false));
        let workers = (0..3)
            .map(|_| {
                let stop = stop.clone();
                let active_workers = active_workers.clone();
                thread::spawn(move || {
                    active_workers.fetch_add(1, Ordering::AcqRel);
                    while !stop.load(Ordering::Acquire) {
                        thread::sleep(Duration::from_millis(1));
                    }
                    active_workers.fetch_sub(1, Ordering::AcqRel);
                })
            })
            .collect::<Vec<_>>();
        let mut frames = FrameRing::new(3, MAX_FRAME_AGE);
        frames.push(PreparedFrame {
            sequence: 1,
            captured_at_unix_ms: 1,
            jpeg: Arc::from([1_u8; 32]),
            fingerprint: Arc::from([2_u8; 16]),
        });
        let buffered_bytes = Arc::new(AtomicUsize::new(frames.buffered_bytes()));
        ObservationRuntime {
            stop,
            voice_reserved: Arc::new(AtomicBool::new(false)),
            workers,
            ring: Arc::new(Mutex::new(frames)),
            buffered_bytes,
            pending_id: Arc::new(Mutex::new(None)),
            awaiting_voice: Arc::new(Mutex::new(None)),
            bridge: Arc::new(
                BackendBridge::new(BackendRuntimeConfig {
                    base_url: "http://127.0.0.1:9".to_owned(),
                    token: "test".to_owned(),
                })
                .unwrap(),
            ),
        }
    }

    #[test]
    fn metrics_are_bounded_and_report_nearest_rank_percentiles() {
        let mut metrics = ObservationMetrics::default();
        for value in 1..=120 {
            metrics.record_latency(value);
        }
        let snapshot = metrics.snapshot(1_000);
        assert_eq!(snapshot.sample_count, 100);
        assert_eq!(snapshot.p50_ms, Some(70));
        assert_eq!(snapshot.p95_ms, Some(115));
        assert_eq!(snapshot.worst_ms, Some(120));
    }

    #[test]
    fn metrics_expose_each_ephemeral_pipeline_stage_without_content() {
        let mut metrics = ObservationMetrics {
            frames_captured: 40,
            vision_analyses: 12,
            vision_candidates: 4,
            cloud_attempts: 2,
            cloud_skips: 1,
            cloud_failures: 0,
            reactions_emitted: 1,
            rejected_confidence: 0,
            rejected_novelty: 8,
            rejected_worthiness: 10,
            last_confidence: Some(0.91),
            last_novelty: Some(0.42),
            last_worthiness: Some(0.38),
            last_pre_cloud_age_ms: Some(900),
            last_cloud_ms: Some(1_800),
            last_post_cloud_age_ms: Some(2_700),
            ..ObservationMetrics::default()
        };

        let snapshot = metrics.snapshot(1_000);

        assert_eq!(snapshot.frames_captured, 40);
        assert_eq!(snapshot.vision_analyses, 12);
        assert_eq!(snapshot.vision_candidates, 4);
        assert_eq!(snapshot.cloud_attempts, 2);
        assert_eq!(snapshot.cloud_skips, 1);
        assert_eq!(snapshot.cloud_failures, 0);
        assert_eq!(snapshot.reactions_emitted, 1);
        assert_eq!(snapshot.rejected_confidence, 0);
        assert_eq!(snapshot.rejected_novelty, 8);
        assert_eq!(snapshot.rejected_worthiness, 10);
        assert_eq!(snapshot.last_confidence, Some(0.91));
        assert_eq!(snapshot.last_novelty, Some(0.42));
        assert_eq!(snapshot.last_worthiness, Some(0.38));
        assert_eq!(snapshot.last_pre_cloud_age_ms, Some(900));
        assert_eq!(snapshot.last_cloud_ms, Some(1_800));
        assert_eq!(snapshot.last_post_cloud_age_ms, Some(2_700));
    }

    #[test]
    fn confident_observation_is_candidate_even_when_subjective_scores_are_zero() {
        let observation = VisionObservation {
            scene: "video".to_owned(),
            event: "scene changed".to_owned(),
            confidence: 0.9,
            novelty: 0.0,
            comment_worthiness: 0.0,
            visible_text: vec![],
        };

        assert!(observation_is_candidate(&observation));
    }

    #[test]
    fn status_strings_never_contain_observation_content() {
        let snapshot = ObservationSnapshot::default();
        assert_eq!(snapshot.message, "Screen observation is off.");
        assert!(!snapshot.message.contains("scene"));
    }

    #[test]
    fn bounded_channel_replaces_an_old_pending_item_with_the_newest() {
        let (sender, receiver) = bounded(1);
        let drain = receiver.clone();
        send_latest(&sender, &drain, 1_u8);
        send_latest(&sender, &drain, 2_u8);
        assert_eq!(receiver.try_recv().unwrap(), 2);
        assert!(receiver.try_recv().is_err());
    }

    #[test]
    fn twenty_start_stop_cycles_leave_zero_workers_and_zero_buffered_bytes() {
        let active_workers = Arc::new(AtomicUsize::new(0));
        for _ in 0..20 {
            let mut runtime = idle_runtime(active_workers.clone());
            let deadline = Instant::now() + Duration::from_secs(1);
            while active_workers.load(Ordering::Acquire) < 3 && Instant::now() < deadline {
                thread::yield_now();
            }
            assert_eq!(active_workers.load(Ordering::Acquire), 3);
            runtime.stop();
            assert_eq!(runtime.buffered_bytes.load(Ordering::Acquire), 0);
            assert!(runtime.ring.lock().unwrap().is_empty());
            assert!(runtime.workers.is_empty());
        }
        assert_eq!(active_workers.load(Ordering::Acquire), 0);
    }
}
