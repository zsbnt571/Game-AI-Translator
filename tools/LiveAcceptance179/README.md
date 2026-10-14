# Explicit isolated-game acceptance harness

This developer harness can launch games, install/remove Fusion components and make billable API requests. It is never run by normal application startup. Use only an independently copied game and an independently copied data directory.

Build with `-p:FusionAssembly="C:\Fusion-0.6.0.179\GameTranslator.dll"`. Required arguments are `--mode=inspect|install|restore|api|live`, `--game=<copied exe>`, `--data-root=<isolated data>` and `--output=<new output directory>`. API modes also require `--allow-api`; `--profile-game=<original library path>` chooses an existing profile without launching that original.

Except for inspect mode, the copied game directory must contain `.fusion-test-copy` with exactly `FusionTranslationTestCopy/1`. Child environment directories are redirected beneath `.fusion-test-data`. Check each engine's separate save conventions before use; the marker is not a universal guarantee for every external save path. Never place it in an original installation.

Live mode uses bounded catalogue/API work, explicit disable-request/stop-request files, and private evidence. Do not publish its data directory, private samples, credentials, histories or game content. See Program.cs for current budgets and protocol.
