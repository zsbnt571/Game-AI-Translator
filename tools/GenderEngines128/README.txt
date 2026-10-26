GenderEngines128 controlled regression

Build this project with -p:FusionAssembly=<exact product GameTranslator.dll> and
dedicated BaseOutputPath/BaseIntermediateOutputPath under the dialogue128 output
root. The assembly name uses the product's existing Regression0501 friend access.

First run the same runner against the frozen 127 assembly with:
  --assembly=<loaded product DLL> --output=<new baseline output> --snapshot-only
Then build/run against the candidate with:
  --assembly=<candidate DLL> --output=<new candidate output>
  --baseline=<baseline output>/request-snapshots.json

The runner calls actual TranslationService request builders by reflection, and
actual RpgTranslationBatch/RpgTranslationSession implementations with controlled
provider, catalogue and display receipts. It makes no HTTP requests, launches no
game, accesses no real settings, and uses neutral fixture metadata only.

The adapter matrix covers production host options. It does NOT execute native
Unity/Godot/GameMaker/Unreal/Defold/web engine bridges, prove role discovery in
arbitrary games, or verify model linguistic quality. There is no cross-item or
cross-line role-inference claim. Chinese and unknown-evidence request snapshots
are compared with the exact old production request builder output.
