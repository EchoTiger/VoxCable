# Changelog

All notable changes to **VoxCable** are documented in this file.

## [2.0.0] - 2026-11-24

### Fixed (Audit Hardening — P0/P1/P2)
- **Deadlock & Stuck UI Fix:** Decoupled `RecordingStopped` / `PlaybackStopped` cleanup onto a background task calling `Stop()` before `EngineError` + non-blocking `Dispatcher.BeginInvoke`, eliminating ABBA UI/audio deadlocks and frozen `DISCONNECT` buttons.
- **8 kHz Bluetooth HFP Nyquist Crash Fix:** Clamped all biquad filter cutoffs to `0.44 * sampleRate` and replaced per-drag `BiQuadFilter` allocations with zero-alloc Transposed Direct Form II `BiquadState` (zero zipper clicks).
- **Jitter-Buffer Priming & Drift Control:** Added pre-roll priming, clickless 2 ms cosine edge fades on startup/underrun, strict `BlockAlign` enforcement, and `DriftCompensatingSampleProvider` (±0.4% PI micro-resampler) to eliminate periodic clock-drift pops.
- **Phase Vocoder Fixes:** Fixed downward-shift bin collision frequency overwrites, aligned dry/wet delay for phase-tight Harmonizer blends, added sample-rate-adaptive FFT sizes (`512..4096`), precomputed LUTs, and stereo-packed single FFT (2–4x faster).
- **Lock-Free Soundboard:** Eliminated audio-thread lock contention with disk I/O via lock-free `ConcurrentQueue` commands, immutable pad sample buffers, async file decoding, and smooth 5 ms fade-outs on `StopAll`.
- **Compatibility & UX Fixes:** Added XLR Left/Right/Mono input modes, clipping-safe 5.1/7.1 downmix, exact `WaveFormatExtensible` float output, smart non-feedback first-launch defaults, feedback confirmation dialog, Reverb Damping slider, Radio voice squelch, natural Echo/Reverb tail decay, atomic `settings.json` saves with debouncing, GPU-accelerated `ScaleTransform` meters, dark custom scrollbars, and Rimshot phase-accumulator fix.

### Added (Studio v2.0 Features)
- **Local Monitor Output ("Hear Myself")** with independent Voice/SFX routing & Monitor Volume.
- **Windows 11 Per-App Process Loopback (`VAD\Process_Loopback`)** to mix single-app audio (Spotify/Browser/Game) alongside your mic.
- **Broadcast Voice Strip:** High-Pass Rumble Filter, Real-time Spectral Wiener AI Denoiser, Hysteresis + Hold Noise Gate (with live Open LED), and RMS Vocal Compressor (with Gain Reduction telemetry).
- **Formant Preservation / Formant Shift + Real-Time Auto-Tune** + **Stereo Chorus** + **Hold-to-Bleep (1 kHz censor tone)**.
- **Interactive Signal-Flow Chain Bar**, **Live Mini Waveform Scope**, **Clip LEDs**, **Mini Always-On-Top Overlay Mode**, and **Double-Click Slider Reset**.
- **2-Bank 16-Pad Drag-and-Drop Soundboard** with looping and live per-pad playback progress fills.
- **Custom User Presets** (Save, Delete, Export/Import JSON).
- **Global Hotkeys (`RegisterHotKey`)**, **System Tray (`Shell_NotifyIconW`)**, **USB Hot-Plug Auto-Reconnect (`IMMNotificationClient`)**, **Single-Instance Guard**, and **Crash Logging**.
- **Opt-In Wireless Touch Deck (Redfur Relay):** Embedded HTTP + WebSocket mobile Stream Deck UI.

## [1.0.0] - 2026-11-24
- Initial release of VoxCable Virtual Audio FX Bridge with WASAPI routing, 8 presets, basic DSP rack, and 8-pad soundboard.
