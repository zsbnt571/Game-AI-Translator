param([string]$OutputRoot,[switch]$TestCapture)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$vs='C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools'
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x86 -host_arch=x64' | Out-Null
# Some Build Tools installations expose MSVC but omit the registered SDK from
# the developer environment. Resolve a complete on-disk SDK, never mix versions.
if(-not(Get-Command rc.exe -ErrorAction SilentlyContinue)){
    $kit='C:\Program Files (x86)\Windows Kits\10'
    $sdk=Get-ChildItem -LiteralPath (Join-Path $kit 'Include') -Directory | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h')) -and
        (Test-Path -LiteralPath (Join-Path $kit "bin\$($_.Name)\x64\rc.exe")) -and
        (Test-Path -LiteralPath (Join-Path $kit "Lib\$($_.Name)\um\x86\kernel32.lib")) -and
        (Test-Path -LiteralPath (Join-Path $kit "Lib\$($_.Name)\ucrt\x86\ucrt.lib"))
    } | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
    if(-not $sdk){throw 'A complete Windows SDK for x86 is required.'}
    $env:PATH=(Join-Path $kit "bin\$($sdk.Name)\x64")+';'+$env:PATH
    $env:INCLUDE+=(';'+((@('ucrt','shared','um','winrt')|ForEach-Object {Join-Path $sdk.FullName $_}) -join ';'))
    $env:LIB+=(';'+(Join-Path $kit "Lib\$($sdk.Name)\um\x86")+';'+(Join-Path $kit "Lib\$($sdk.Name)\ucrt\x86"))
}
$output=if($OutputRoot){[IO.Path]::GetFullPath($OutputRoot)}else{Join-Path $root 'Build\Wolf'}
$payload=if($TestCapture){Join-Path $output 'TestPayload'}else{Join-Path $PSScriptRoot 'Payload'}
[string[]]$defines=if($TestCapture){@('/DFUSION_TEST_CAPTURE')}else{@()}
New-Item -ItemType Directory -Path $output,$payload -Force | Out-Null
Push-Location $PSScriptRoot
try {
    & rc /nologo "/fo$output\WolfBridge.res" WolfBridge.rc
    if($LASTEXITCODE -ne 0){throw 'WOLF resources failed'}
    & cl /nologo /LD /MT /O2 /EHsc /std:c++17 /utf-8 @defines "/Fo$output\\" "/I$root\Dependencies" "/I$root\Dependencies\minhook\include" WolfBridge.cpp "$root\Dependencies\minhook\src\buffer.c" "$root\Dependencies\minhook\src\hook.c" "$root\Dependencies\minhook\src\trampoline.c" "$root\Dependencies\minhook\src\hde\hde32.c" /link "$output\WolfBridge.res" gdi32.lib user32.lib /DEF:WolfBridge.def "/OUT:$payload\FusionWolf.dll" "/IMPLIB:$output\FusionWolf.lib"
    if($LASTEXITCODE -ne 0){throw 'WOLF bridge build failed'}
    & cl /nologo /MT /O2 /EHsc /std:c++17 /utf-8 "/Fo$output\\" "/I$root\Dependencies" WolfLauncher.cpp /link "/OUT:$payload\FusionWolfLaunch.exe"
    if($LASTEXITCODE -ne 0){throw 'WOLF launcher build failed'}
} finally {Pop-Location}
