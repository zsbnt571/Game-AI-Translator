$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$vs='C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools'
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$output=Join-Path $root 'Build\Slate'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Push-Location $PSScriptRoot
try {
 & cl /nologo /LD /MT /O2 /EHsc /std:c++17 /utf-8 "/Fo$output\\" "/I$root\Dependencies" "/I$root\Dependencies\minhook\include" SlateBridge.cpp "$root\Dependencies\minhook\src\buffer.c" "$root\Dependencies\minhook\src\hook.c" "$root\Dependencies\minhook\src\trampoline.c" "$root\Dependencies\minhook\src\hde\hde64.c" /link dbghelp.lib "/OUT:$output\FusionSlate.dll" "/IMPLIB:$output\FusionSlate.lib"
 if($LASTEXITCODE -ne 0){throw 'Slate bridge build failed'}
} finally {Pop-Location}
