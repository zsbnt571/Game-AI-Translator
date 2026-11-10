param([Parameter(Mandatory=$true)][string]$Output,[Parameter(Mandatory=$true)][string]$DxLib)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$outRoot=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $outRoot){throw 'Use a fresh native test output directory.'}
[IO.Directory]::CreateDirectory($outRoot)|Out-Null
$vs='C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools'
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x86 -host_arch=x64' | Out-Null
$exe=Join-Path $outRoot 'Font135DxLib.exe'
Push-Location $outRoot
try{
 & cl /nologo /MT /O2 /EHsc /std:c++17 /utf-8 "/I$projectRoot\Dependencies" "/I$projectRoot\Dependencies\minhook\include" (Join-Path $PSScriptRoot 'Font135DxLib.cpp') "$projectRoot\Dependencies\minhook\src\buffer.c" "$projectRoot\Dependencies\minhook\src\hook.c" "$projectRoot\Dependencies\minhook\src\trampoline.c" "$projectRoot\Dependencies\minhook\src\hde\hde32.c" /link gdi32.lib user32.lib "/OUT:$exe" 2>&1 | Tee-Object -FilePath (Join-Path $outRoot 'build.log')
 if($LASTEXITCODE -ne 0){throw 'Native font tests did not compile'}
 $font='C:\Windows\Fonts\consola.ttf'
 $hash=(Get-FileHash -LiteralPath $font -Algorithm SHA256).Hash
 & $exe $DxLib $font $hash (Join-Path $outRoot 'result.json') | Tee-Object -FilePath (Join-Path $outRoot 'run.log')
 if($LASTEXITCODE -ne 0){throw 'Native font tests failed; inspect result.json.'}
 Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $projectRoot 'Source\WolfBridge\WolfFont.h'),$DxLib,$font,$exe | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outRoot 'hashes.json') -Encoding utf8
}finally{Pop-Location}
