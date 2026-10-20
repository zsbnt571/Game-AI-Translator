# Fusion R1 1.0.0 Preview 1 — validation

**Game modification is experimental. 游戏修改处于实验阶段。 Изменение игровых данных — экспериментальная функция.**

The final public application passed **613 controlled checks, 0 failures**, using .NET 8.0.24.

| Area | Checks |
| --- | ---: |
| English interface | 87 |
| Russian interface | 87 |
| Modification-language updates and draft preservation | 95 |
| Catalog-language updates | 34 |
| Map-language updates | 39 |
| Category switching, concurrent responses and speed limits | 72 |
| Usable translations first and bounded repair | 57 |
| Terminology classification and conservative cleanup | 43 |
| Native startup and shutdown with isolated settings | 16 |
| Character, common-action and CG layouts | 53 |
| Embedded RPG Maker MV/MZ speed scripts | 30 |

The category stress fixture contained 21,116 rows. Across 42 warm switches, P95 was 79.16 ms and the maximum was 87.41 ms; 60 overlapping requests also passed. These are controlled classification/UI timings, **not a measurement or promise of physical 60 FPS**.

The translation tests use controlled responses; they do not establish provider quality or compatibility with every game. The release regression made no real provider calls and performed no real game writes. WOLF speed selection covers 0.25–100×; RPG Maker MV/MZ uses integer 1–100×. Selected speed is not guaranteed actual speed, and other engines do not all have a verified speed interface.

The package includes local CPU runtimes and permitted dependencies. Some existing licensed plugin payloads are reused. This is not a claim that every plugin was rebuilt from fresh source or that the full build is byte reproducible.

Private test reports, fixtures, games, saves, library contents, translation histories and credentials are excluded. The separate release-level `PACKAGE-VERIFICATION.json` records archive hashes, independent extraction, first-launch results and the exact public source commit.

All-engine compatibility, prolonged gameplay, high-speed audiovisual behavior, clean-machine installation and every monitor/DPI configuration remain outside the validated scope.
