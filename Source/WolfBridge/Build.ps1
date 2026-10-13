$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$vs='C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools'
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x86 -host_arch=x64' | Out-Null
$output=Join-Path $root 'Build\Wolf'
$payload=Join-Path $PSScriptRoot 'Payload'
New-Item -ItemType Directory -Path $output,$payload -Force | Out-Null
Push-Location $PSScriptRoot
try {
    & rc /nologo "/fo$output\WolfBridge.res" WolfBridge.rc
    if($LASTEXITCODE -ne 0){throw 'WOLF resources failed'}
    & cl /nologo /LD /MT /O2 /EHsc /std:c++17 /utf-8 "/Fo$output\\" "/I$root\Dependencies" "/I$root\Dependencies\minhook\include" WolfBridge.cpp "$root\Dependencies\minhook\src\buffer.c" "$root\Dependencies\minhook\src\hook.c" "$root\Dependencies\minhook\src\trampoline.c" "$root\Dependencies\minhook\src\hde\hde32.c" /link "$output\WolfBridge.res" gdi32.lib user32.lib /DEF:WolfBridge.def "/OUT:$payload\FusionWolf.dll" "/IMPLIB:$output\FusionWolf.lib"
    if($LASTEXITCODE -ne 0){throw 'WOLF bridge build failed'}
    & cl /nologo /MT /O2 /EHsc /std:c++17 /utf-8 "/Fo$output\\" "/I$root\Dependencies" WolfLauncher.cpp /link "/OUT:$payload\FusionWolfLaunch.exe"
    if($LASTEXITCODE -ne 0){throw 'WOLF launcher build failed'}
} finally {Pop-Location}
