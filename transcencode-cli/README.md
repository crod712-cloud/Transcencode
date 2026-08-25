# Transcencode CLI-first Windows application

**Analyze. Encode. Verify.**

This branch restores the practical architecture that worked first: a normal Windows graphical application which drives `HandBrakeCLI.exe` in the background.

The command-line engine is never opened in a separate console window. Standard output and error are redirected into the application's **Live Engine** tab, where Transcencode displays the raw engine output, progress, frame rate, remaining time, and estimated completion time.

## Stabilization goals

- Normal Windows x64 application and installer.
- No visible external command prompt.
- Source scan clearly labeled **Scan File Details**.
- Audio and subtitle languages visible immediately after scanning.
- NVIDIA NVENC choices exposed directly.
- Plain-English quality profiles and an editable CQ/RF value.
- **Same as source** crop mode preserves the original frame and black bars.
- UI scaling from 100% to 200%.
- Structural verification after encoding.
- Local crash and diagnostic logs under `%LOCALAPPDATA%\Transcencode\logs`.
- Automated Windows startup, scan, encode, verification, and packaging tests.

The native HandBrake WPF fork remains on its own development branch. It is not used by this stabilization build.
