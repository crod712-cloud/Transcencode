# Transcencode CLI Wrapper 0.2.9

**Analyze. Encode. Verify.**

This is the recovery path for the working Windows application architecture: a normal WPF interface that launches the official `HandBrakeCLI.exe` directly and invisibly. It is intentionally separate from the later native HandBrake-GUI fork.

## Immediate architecture

- Normal Windows graphical application.
- `HandBrakeCLI.exe` is started with `UseShellExecute=false`, `CreateNoWindow=true`, and `WindowStyle=Hidden`.
- Standard output and standard error are redirected into the **Live Engine** tab and persisted under `%LOCALAPPDATA%\Transcencode\logs`.
- No `cmd.exe`, PowerShell console, or external HandBrakeCLI window is opened for scanning or encoding.
- Settings are isolated under `%LOCALAPPDATA%\Transcencode`.

## Implemented workflow

- Automatic quick source scan with visible audio and subtitle languages/tracks.
- NVIDIA NVENC H.264, H.265, and H.265 10-bit selections.
- Optional NVIDIA NVDEC source decoding as a separate control.
- Plain-language quality targets, including **Match source visually (high-fidelity target)**.
- A custom quality slider that moves right for higher quality while showing the actual lower CQ/RF number.
- **Same as source (preserve original black bars)** as the first crop choice, mapped to `--crop-mode none`.
- Safe auto-crop, normal automatic crop, and exact custom crop.
- Same-source, 1080p, 1440p, 4K, and custom dimensions.
- Representative-frame **Deep Analyze** using OpenCV to measure brightness, dark-pixel load, contrast, detail, motion, scene change, and difficult timestamps.
- Live command, engine output, progress, elapsed time, ETA remaining, local estimated finish time, average speed, warnings, muxing messages, and final exit code.
- Structural output verification plus sampled source/output picture similarity.
- Whole-interface scaling from 100% through 200%.
- Visible crash errors with an exact diagnostic-log path; no blank error dialog.

## Honest limits

- “Match source visually” is a high-fidelity target, not mathematical losslessness.
- Deep Analyze samples representative points rather than decoding every frame.
- Sampled visual verification is advisory and can be affected by intentional cropping, scaling, and filtering.
- Complete Dolby Vision dynamic-metadata preservation is not promised for NVENC. The interface warns before proceeding.
- RTX Video SDK AI super-resolution is not included in this CLI-wrapper milestone.

## Build

The Windows GitHub Actions workflow downloads the pinned official HandBrakeCLI 1.11.2 archive, verifies SHA-256 `80bfe8d5f5d11cc3ef76b834add3ed4e82dee6523ffeb435c283f88b1a21f09d`, builds a self-contained Windows x64 application, launches the normal no-argument GUI path, runs package-free regressions, scans and encodes a generated video through the hidden engine, performs Deep Analyze and Verify, and only then creates the portable ZIP and installer artifacts.
