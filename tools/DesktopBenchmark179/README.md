# Library rendering benchmark

Build against the chosen package's GameTranslator.dll using `FusionAssembly`. Add `-p:NativeWpf=true` for the 179 native WPF renderer; omit it for the published 178 WinForms renderer.

The command-line options and defaults are in Program.cs. Synthetic cards and explicit output paths are required. `--precise-timer` temporarily requests 1 ms timer resolution and restores it on exit. CPU draw completion and callback pacing do not measure actual presentation FPS. No API or game launch occurs.
