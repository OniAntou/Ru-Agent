# Logging Performance Benchmark Results

Date: 2026-08-20 | Samples per path: 30 paired samples | 5 repetitions per sample

The benchmark uses temporary diagnostic and activity roots. It alternates
baseline/logging order, reports interpolated p50/p95/p99 values, and gates the
corresponding p95 and p99 deltas at both 2 ms and 2%.

| Operation Path | Baseline p50 (ms) | Baseline p95 (ms) | Baseline p99 (ms) | Logging p50 (ms) | Logging p95 (ms) | Logging p99 (ms) | Delta p95 (ms) | Delta p95 % | Delta p99 (ms) | Delta p99 % | Status |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| `chat_pipeline` | 27.015 | 36.181 | 36.791 | 31.765 | 35.823 | 36.546 | -0.358 | -1.0% | -0.245 | -0.7% | PASS |
| `voice_pipeline` | 42.890 | 45.114 | 45.712 | 43.137 | 45.874 | 46.305 | +0.761 | +1.7% | +0.593 | +1.3% | PASS |
| `stt_pipeline` | 30.736 | 31.633 | 32.232 | 30.961 | 31.467 | 31.603 | -0.166 | -0.5% | -0.630 | -2.0% | PASS |
| `vts_pipeline` | 18.972 | 19.735 | 19.926 | 18.999 | 19.818 | 20.181 | +0.083 | +0.4% | +0.255 | +1.3% | PASS |
| `observation_pipeline` | 26.197 | 26.967 | 27.436 | 26.215 | 26.675 | 26.936 | -0.292 | -1.1% | -0.500 | -1.8% | PASS |

Result: PASS for all five synthetic paths. This is a latency regression gate,
not live speaker, microphone, desktop, or VTube Studio acceptance.
