param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$vs='C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools'
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$output=[IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $output -Force | Out-Null
Push-Location $output
try {
 & cl /nologo /MT /O2 /Zi /EHsc /std:c++17 /utf-8 "/Fo$output\\" "/Fd$output\compile.pdb" "/I$root\Dependencies" "/I$root\Dependencies\minhook\include" "$PSScriptRoot\SlateFont135.cpp" "$root\Dependencies\minhook\src\buffer.c" "$root\Dependencies\minhook\src\hook.c" "$root\Dependencies\minhook\src\trampoline.c" "$root\Dependencies\minhook\src\hde\hde64.c" /link /DEBUG /INCREMENTAL:NO /OPT:NOICF dbghelp.lib gdi32.lib user32.lib "/OUT:$output\SlateFont135.exe"
 if($LASTEXITCODE -ne 0){throw 'Slate font ABI test build failed'}
 $font='C:\Windows\Fonts\consola.ttf'
 & "$output\SlateFont135.exe" $font (Get-FileHash -LiteralPath $font -Algorithm SHA256).Hash "$output\result.json"
 if($LASTEXITCODE -ne 0){throw 'Slate font ABI test failed'}
}finally{Pop-Location}
