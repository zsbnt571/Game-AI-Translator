param([string]$OutputRoot=(Join-Path $PSScriptRoot 'Payload'),[string]$BuildRoot=(Join-Path $PSScriptRoot 'Build'),[switch]$Tests)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$vs='C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools'
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
foreach($arch in @('x86','x64')) {
 Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=$arch -host_arch=x64" | Out-Null
 $obj=Join-Path ([IO.Path]::GetFullPath($BuildRoot)) $arch
 New-Item -ItemType Directory -Path $obj -Force | Out-Null
 $definition=Join-Path $obj 'SpeedBridge.def'
 if($arch -eq 'x86'){[IO.File]::WriteAllText($definition,"EXPORTS`r`nFusionSpeedStart=_FusionSpeedStart@4`r`nFusionSpeedProtocolVersion DATA`r`n")}else{[IO.File]::WriteAllText($definition,"EXPORTS`r`nFusionSpeedStart`r`nFusionSpeedProtocolVersion DATA`r`n")}
 $decoder=if($arch -eq 'x86'){'hde32.c'}else{'hde64.c'}
 Push-Location $PSScriptRoot
 try {
  & cl /nologo /LD /MT /O2 /EHsc /std:c++17 /utf-8 "/Fo$obj\\" "/I$root\Dependencies\minhook\include" SpeedBridge.cpp "$root\Dependencies\minhook\src\buffer.c" "$root\Dependencies\minhook\src\hook.c" "$root\Dependencies\minhook\src\trampoline.c" "$root\Dependencies\minhook\src\hde\$decoder" /link "/DEF:$definition" "/OUT:$OutputRoot\FusionSpeed.$arch.dll" "/IMPLIB:$obj\FusionSpeed.lib"
  if($LASTEXITCODE -ne 0){throw "Speed DLL $arch build failed"}
  & cl /nologo /MT /O2 /EHsc /std:c++17 /utf-8 "/Fo$obj\\" SpeedHost.cpp /link "/OUT:$OutputRoot\FusionSpeedHost.$arch.exe"
  if($LASTEXITCODE -ne 0){throw "Speed helper $arch build failed"}
  if($Tests){
   foreach($module in @('SDLFixture','UnrelatedFixture','FusionWolf')){
    & cl /nologo /LD /MT /O2 /EHsc /std:c++17 /utf-8 "/Fo$obj\\" Tests\TestModule.cpp /link "/OUT:$OutputRoot\$module.$arch.dll" "/IMPLIB:$obj\$module.lib"
    if($LASTEXITCODE -ne 0){throw "Speed test module $arch build failed"}
   }
   & cl /nologo /MT /O2 /EHsc /std:c++17 /utf-8 "/Fo$obj\\" Tests\Fixture.cpp /link winmm.lib "/OUT:$OutputRoot\SpeedFixture.$arch.exe"
   if($LASTEXITCODE -ne 0){throw "Speed fixture $arch build failed"}
   & cl /nologo /MT /O2 /EHsc /std:c++17 /utf-8 "/Fo$obj\\" "/I$root\Dependencies" Tests\SpeedTests.cpp /link "/OUT:$OutputRoot\SpeedTests.$arch.exe"
   if($LASTEXITCODE -ne 0){throw "Speed tests $arch build failed"}
  }
 } finally {Pop-Location}
}
