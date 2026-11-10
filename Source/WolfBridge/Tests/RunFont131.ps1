param([Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$outRoot=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $outRoot){throw 'Use a fresh font test output directory.'}
[IO.Directory]::CreateDirectory($outRoot)|Out-Null
$vs='C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools'
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x86 -host_arch=x64' | Out-Null
$exe=Join-Path $outRoot 'Font131.exe'
& cl /nologo /MT /O2 /EHsc /std:c++17 /utf-8 "/I$projectRoot\Dependencies" (Join-Path $PSScriptRoot 'Font131.cpp') "/Fo$outRoot\Font131.obj" /link gdi32.lib user32.lib "/OUT:$exe" 2>&1 | Tee-Object -FilePath (Join-Path $outRoot 'build.log')
if($LASTEXITCODE -ne 0){throw 'Native font tests did not compile'}
& $exe (Join-Path $projectRoot 'Dependencies\GameMakerFont\bridge-font.otf') (Join-Path $outRoot 'result.json') | Tee-Object -FilePath (Join-Path $outRoot 'run.log')
if($LASTEXITCODE -ne 0){throw 'Native font tests failed; inspect result.json.'}
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $projectRoot 'Source\WolfBridge\WolfFont.h'),(Join-Path $projectRoot 'Dependencies\GameMakerFont\bridge-font.otf'),$exe | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outRoot 'hashes.json') -Encoding utf8
