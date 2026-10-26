param([string]$VerificationRoot)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$vs='C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools'
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x86 -host_arch=x64' | Out-Null
$buildRoot=Join-Path $projectRoot 'Build\Wolf'
if(-not $VerificationRoot){$VerificationRoot=Join-Path $projectRoot 'Build\WolfTests'}
New-Item -ItemType Directory -Path $buildRoot,$VerificationRoot -Force | Out-Null
$testExe=Join-Path $buildRoot 'SessionFiles101.exe'
& cl /nologo /MT /O2 /EHsc /std:c++17 /utf-8 "/I$projectRoot\Dependencies" "/I$projectRoot\Dependencies\minhook\include" (Join-Path $PSScriptRoot 'SessionFiles101.cpp') "/Fo$buildRoot\SessionFiles101.obj" /link "/OUT:$testExe"
if($LASTEXITCODE -ne 0){throw 'Native session tests did not compile'}
$testRoot=Join-Path $VerificationRoot ('native-session-'+[Guid]::NewGuid().ToString('N'))
$output=@(& $testExe $testRoot)
$exitCode=$LASTEXITCODE
$output | Write-Output
$summary=$output | Where-Object {$_ -match '^checks=(\d+) failures=(\d+)$'} | Select-Object -Last 1
if(-not $summary -or $summary -notmatch '^checks=(\d+) failures=(\d+)$'){throw 'Native session test result missing'}
$report=[ordered]@{
    scope='Synthetic local file I/O only; no game launched, no screen captured, no user data accessed'
    checks=[int]$Matches[1]
    failures=[int]$Matches[2]
    exitCode=$exitCode
    fixture=$testRoot
    launcherSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $projectRoot 'Source\WolfBridge\Payload\FusionWolfLaunch.exe')).Hash
    bridgeSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $projectRoot 'Source\WolfBridge\Payload\FusionWolf.dll')).Hash
}
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $VerificationRoot 'native-session-results.json') -Encoding utf8
if($exitCode -ne 0){throw 'Native session tests failed'}
