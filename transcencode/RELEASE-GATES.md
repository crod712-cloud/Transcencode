# Transcencode release gates

The native WPF branch is **not a release** until every required gate below has evidence attached to the pull request. A successful compile by itself is not sufficient.

## P0 — must pass before an installer is offered

- [ ] Windows x64 native HandBrake engine builds with `--enable-nvenc` and `--enable-nvdec`.
- [ ] Patched WPF GUI compiles with zero errors.
- [ ] Application launches on a clean Windows profile and remains alive for at least 60 seconds.
- [ ] Existing HandBrake tabs remain present: Summary, Dimensions, Filters, Video, Audio, Subtitles, Chapters.
- [ ] Native Transcencode tabs remain present: Analyze, Source Tracks, Upscale & Enhance, Verify, Live Engine.
- [ ] A real source file can be selected and scanned through the GUI.
- [ ] Source Tracks displays every audio and subtitle language from a controlled multi-track test file.
- [ ] Analyze completes on a controlled test file without freezing or crashing and produces a recommendation.
- [ ] Same as source cropping preserves the full source dimensions and original black bars.
- [ ] A five-minute encode can be started, paused, resumed, stopped, and completed without corrupting the queue.
- [ ] Live Engine displays current activity, progress, ETA, expected finish time, and average speed.
- [ ] Completed output passes structural verification.
- [ ] Unhandled exceptions write a local crash report under `%LOCALAPPDATA%\Transcencode\logs`.
- [ ] Installer and uninstaller do not overwrite or remove an existing official HandBrake installation.

## P0 — physical NVIDIA workstation tests

These cannot be substituted with compilation or a GPU-less hosted runner.

- [ ] RTX 2080 Ti is detected.
- [ ] H.264 NVENC starts and completes.
- [ ] H.265 NVENC starts and completes.
- [ ] H.265 10-bit NVENC starts and completes.
- [ ] The selected GUI encoder matches the encoder recorded in the activity log.
- [ ] NVENC quality target is visually reviewed against the source at difficult timestamps.
- [ ] NVDEC enabled and disabled paths both scan and encode successfully.
- [ ] HDR10 metadata behavior is verified.
- [ ] Dolby Vision behavior is explicitly reported and never claimed preserved without evidence.

## P1 — required before calling Analyze dependable

- [ ] Dark scenes, gradients, grain, motion, and fine detail are represented in the sample set.
- [ ] Preview indices are always within the source scan preview count.
- [ ] Null or unreadable preview frames do not crash analysis.
- [ ] Analysis can be cancelled and restarted safely.
- [ ] Repeated analysis does not leak scan instances or grow memory without bound.
- [ ] Recommendation boundaries are tested for x264, x265, H.264 NVENC, H.265 NVENC, and 10-bit NVENC.
- [ ] Recommendation wording distinguishes a high-fidelity target from mathematical losslessness.

## P1 — usability regressions

- [ ] Black-bar choices are written in plain language.
- [ ] Same as source is easy to find and explains that it disables cropping.
- [ ] Quality direction is explicit: moving right increases quality and file size while the shown CQ/RF value commonly decreases.
- [ ] Interface scaling works at 100%, 110%, 125%, 150%, 175%, and 200%.
- [ ] Enlarged content remains reachable with scrollbars.
- [ ] Audio and subtitle language lists remain readable at every scale.
- [ ] Keyboard navigation and UI Automation names remain usable.

## Later engine work

- [ ] Frame-by-frame or representative-scene VMAF/SSIM verification.
- [ ] Persistent difficult-scene timeline and comparison viewer.
- [ ] NVIDIA RTX Video SDK super-resolution integration.
- [ ] HDR/Dolby Vision validation for any AI-altered image path.
- [ ] Fully rebranded executable, worker assumptions, NSIS installer, shortcuts, registry keys, and uninstall paths.

No artifact should be described as ready merely because it compiled. The release decision must point to the evidence for every applicable gate above.
