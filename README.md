# VoxCable Studio

A sleek, zero-bloat native **.NET 8 / WPF** Windows Virtual Audio FX Bridge & Soundboard built for Discord, OBS, streaming, and gaming.

Route any microphone (including single-channel XLR USB interfaces), system loopback, or **Windows 11 per-app audio stream** through a real-time stereo broadcast strip & creative DSP rack straight into a virtual audio cable (such as **VB-Cable**, **VoiceMeeter**, or **Steam Streaming Microphone**) — while optionally monitoring yourself live in headphones.

---

## Highlights (v2.0.0)

### Studio Audio Routing & Engine
- **Low-Latency Full-Duplex WASAPI Engine** with MMCSS `"Pro Audio"` priority, 1 ms high-res timer boost, jitter-buffer priming, clickless underrun fading, and **adaptive PI clock-drift compensation** (zero drift pops across dual-crystal USB mics and virtual cables).
- **Local Monitor Output ("Hear Myself")** — audition voice FX and soundboard pads live in your headphones while broadcasting cleanly to a virtual cable, with independent Voice/SFX routing and Monitor Volume.
- **Windows 11 Per-App Process Loopback (`VAD\Process_Loopback`)** — mix audio from a *single* running app (Spotify, browser, or a game) alongside your mic without echoing Discord voices back.
- **XLR & Surround Ready** — Input Channel Mode (`Stereo`, `Mono Sum`, `Left (XLR 1)`, `Right (XLR 2)`), clipping-safe ITU-R BS.775 5.1/7.1 downmix, and exact `WaveFormatExtensible` float output matching.
- **Hot-Plug & Safeguards** — `IMMNotificationClient` live USB/Bluetooth device watcher with auto-reconnect, smart non-feedback default selection, feedback confirmation guard, and human-readable WASAPI error translations.

### Broadcast Voice Strip & Real-Time DSP Rack
- **Broadcast Strip:** 2nd-order High-Pass Rumble Filter (40–200 Hz), real-time **Spectral Wiener AI Denoiser**, Hysteresis + Hold Noise Gate (with live Open LED), and RMS Vocal Compressor (with Gain Reduction meter).
- **Formant-Preserving Pitch Shifter & Auto-Tune:** Stereo-packed adaptive-FFT Phase Vocoder (512–4096 pt from 8 kHz Bluetooth HFP to 192 kHz DACs) with dominant-bin tracking, Identity Phase Locking (IPL), independent Formant Shift, latency-aligned Harmonizer dry mix, and real-time musical scale Auto-Tune.
- **Creative FX:** Zero-click 3-Band EQ, Stereo Quadrature Chorus, Robot Ring-Modulator, Walkie-Talkie Radio (with Discord-friendly voice-gated **Squelch**), Bitcrusher, Tape-Glide Fractional Echo, 6-Comb Stereo Reverb (with Room/Damping/Mix + natural tail decay), and **Hold-to-Bleep** 1 kHz censor tone.
- **12 Built-in Presets + Custom User Presets** (Save, Apply, Delete, Export/Import JSON).

### 16-Pad Soundboard, Hotkeys & Wireless Touch Deck
- **2-Bank × 8-Pad Lock-Free Soundboard:** 8 procedural built-in synths + 8 custom slots with drag-and-drop `.wav`/`.mp3`/`.m4a`/`.wma`/`.aac`, loop toggle, live progress bar fills, and clickless fade-out Stop All.
- **Global Hotkeys + System Tray + Mini Mode:** Control Mute (`Ctrl+Alt+M`), Bypass (`Ctrl+Alt+B`), Hold-to-Bleep (`Ctrl+Alt+X`), Stop SFX (`Ctrl+Alt+0`), and Pads (`Ctrl+Alt+1..8`) right from inside games.
- **Opt-In Wireless Touch Deck (Redfur Relay):** Embedded local HTTP + WebSocket PWA server turning any phone, tablet, or browser into a live touch Stream Deck for presets, pads, mute, and hold-to-bleep.

---

## Quick Start

### Build & Run
```powershell
dotnet build VoxCable.sln
dotnet test VoxCable.sln
dotnet run --project src\VoxCable\VoxCable.csproj
```

### Publish Standalone Single-File Release
```powershell
dotnet publish src\VoxCable\VoxCable.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

### Headless Smoke Test
```powershell
.\dist\VoxCable.exe --smoke-test --smoke-test-out smoke.txt
```

---

## Virtual Audio Cable Setup (Discord / OBS)
1. Install a Windows virtual audio driver such as [VB-Cable](https://vb-audio.com/Cable/) (or use VoiceMeeter / Steam Streaming Microphone).
2. In **VoxCable**, pick your microphone as **Input Source** and `CABLE Input (VB-Audio Virtual Cable)` as **Output Target**.
3. Optionally enable **Monitor Out** pointed to your headphones so you can hear your FX and soundboard clips.
4. Click **CONNECT**, then select `CABLE Output (VB-Audio Virtual Cable)` as your Microphone in Discord, OBS, or your game.

---

## License
[MIT](LICENSE)
