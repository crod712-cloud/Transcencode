# Transcencode development

**Analyze. Encode. Verify.**

This branch is the first native Windows GUI development line for Transcencode. It starts from the real HandBrake repository and keeps HandBrake's mature Summary, Dimensions, Filters, Video, Audio, Subtitles, Chapters, presets, preview, and queue workflow.

The native alpha adds:

- **Analyze** — representative-frame picture analysis for darkness, contrast, edge/detail load, scene variation, encoding difficulty, and an encoder-aware Constant Quality recommendation.
- **Source Tracks** — a direct view of all scanned audio and subtitle languages, formats, channels, bitrates, names, and capabilities.
- **Upscale & Enhance** — guided source-size, 1080p, 1440p, 4K, and custom HandBrake scaling controls. RTX AI processing is deliberately not represented as complete yet.
- **Verify** — structural inspection of the encoded file using the packaged HandBrakeCLI.
- **Live Engine** — native HandBrake log output, progress, ETA remaining, expected finish time, and average speed.

## Development branch

`transcencode/native-wpf-alpha`

The bootstrap archive in `transcencode/bootstrap/` contains the current C# and XAML source overlay plus a fail-closed integration patch. The Windows build workflow expands it, applies it to the checked-out HandBrake source, builds the native engine with NVIDIA NVENC/NVDEC enabled, compiles the WPF GUI, and publishes a runnable Windows artifact.

This is a temporary bootstrap mechanism for the first compile gate. Once the native build passes, the added source and upstream-file edits will be committed directly to the branch and the bootstrap archive removed.

## Current build target

The first gate is a runnable Windows x64 portable build from GitHub Actions. The internal executable remains `HandBrake.exe` during this gate so HandBrake's worker/process-isolation assumptions remain valid. Full executable and NSIS installer renaming will follow after the native UI build is stable.
