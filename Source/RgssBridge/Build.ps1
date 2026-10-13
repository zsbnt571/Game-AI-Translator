$ErrorActionPreference='Stop'
$rgssRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$rgssVs='C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools'
Import-Module "$rgssVs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $rgssVs -SkipAutomaticLocation -DevCmdArguments '-arch=x86 -host_arch=x64' | Out-Null
$rgssOutput=Join-Path $rgssRoot 'Build\Rgss'
$rgssPayload=Join-Path $PSScriptRoot 'Payload'
New-Item -ItemType Directory -Path $rgssOutput,$rgssPayload -Force | Out-Null
Push-Location $PSScriptRoot
try {
 & cl /nologo /LD /MT /O2 /EHsc /std:c++17 /utf-8 "/Fo$rgssOutput\\" FusionRgss.cpp /link user32.lib /DEF:FusionRgss.def "/OUT:$rgssPayload\FusionRgss.dll" "/IMPLIB:$rgssOutput\FusionRgss.lib"
 if($LASTEXITCODE -ne 0){throw 'RGSS bridge build failed'}
} finally {Pop-Location}
