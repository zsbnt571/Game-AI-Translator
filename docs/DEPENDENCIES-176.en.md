# FUSION R1 0.6.0.178 Preview 1 — Dependencies


Required app asset: Fusion-0.6.0.178.zip
Optional NVIDIA supplement: Fusion-0.6.0.178-GPU.zip
Developer source: Fusion-0.6.0.178-Source.zip
Use SHA256SUMS.txt to verify downloads.

The main package includes self-contained .NET 8/.NET 10 runtimes for the app/tools, Python, RapidOCR and detection/orientation/recognition models, CPU ONNX/background processing dependencies/models, integrated adapters, font resources, licenses and third-party source snapshots.
VC++ x64/x86 installers are under prerequisites/. They are not automatically installed; use them if Windows reports missing VCRUNTIME/MSVCP components.
Windows 10/11 x64, OS .NET Framework 4.8 and optional Windows OCR languages remain platform requirements. API service/credits, games and GPU drivers are not included.
Proprietary Oodle is excluded; some Unreal resources cannot be extracted. Optional heavy document-layout/vision models are not required by the default CPU/RapidOCR path and are not all bundled.

GPU supplement
Close Fusion. Merge the supplement's runtime folder into the main Fusion-0.6.0.178/runtime.
Expected path: Fusion-0.6.0.178/runtime/background-gpu/site. Do not nest runtime/runtime.
This accelerates supported image background processing, not cloud translation requests. It cannot run alone and needs compatible NVIDIA hardware/driver.
Component files reuse the .169 dependency set. Archive/file integrity is checked; this is not new .178 inference validation across all NVIDIA hardware. Original NVIDIA licenses remain in the package. Use CPU if incompatible.

Licenses and source
Project-owned code uses GPL-3.0-only, as in the public repository. Third parties retain their own licenses; see LICENSE.txt, THIRD_PARTY_NOTICES.txt, licenses/ and third-party-source/.
The .178 source archive includes build instructions. Proprietary Oodle and Unity engine compile references are excluded; rebuilding some plugins requires separately supplied lawful references. This does not require end users to compile the application.
