param([string]$VerificationRoot)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$vs='C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools'
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x86 -host_arch=x64' | Out-Null
if(-not $VerificationRoot){$VerificationRoot=Join-Path $projectRoot 'Build\DataBoundaryTests'}
New-Item -ItemType Directory -Path $VerificationRoot -Force | Out-Null
$reports=@()
foreach($name in @('DisabledData101','Data32','TargetedData101')){
    $testExe=Join-Path $VerificationRoot "$name.exe"
    & cl /nologo /MT /O2 /EHsc /std:c++17 /utf-8 "/I$projectRoot\Dependencies" (Join-Path $PSScriptRoot "$name.cpp") "/Fo$VerificationRoot\$name.obj" /link "/OUT:$testExe"
    if($LASTEXITCODE -ne 0){throw "Native data boundary test did not compile: $name"}
    $output=@(& $testExe)
    $exitCode=$LASTEXITCODE
    $output | Set-Content -LiteralPath (Join-Path $VerificationRoot "$name.txt") -Encoding utf8
    $summary=($output | Where-Object {$_ -match '^\{"checks":'} | Select-Object -Last 1) | ConvertFrom-Json
    if(-not $summary){throw "Native data boundary test result missing: $name"}
    $reports+=@{name=$name;checks=$summary.checks;failures=$summary.failures;exitCode=$exitCode}
    Write-Output "$name checks=$($summary.checks) failures=$($summary.failures)"
    if($exitCode -ne 0){throw "Native data boundary test failed: $name"}
}
@{scope='Synthetic local in-process memory only; no game launched or injected';tests=$reports} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $VerificationRoot 'wolf-data-boundary-results.json') -Encoding utf8
