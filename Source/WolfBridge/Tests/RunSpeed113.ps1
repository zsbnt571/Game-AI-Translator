param([Parameter(Mandatory)][string]$DependencyRoot,[Parameter(Mandatory)][string]$VerificationRoot)
$ErrorActionPreference='Stop'
$DependencyRoot=(Resolve-Path -LiteralPath $DependencyRoot).Path
$VerificationRoot=[IO.Path]::GetFullPath($VerificationRoot)
$build=Join-Path $VerificationRoot 'build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$vs='C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools'
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x86 -host_arch=x64' | Out-Null
$common=@('/nologo','/MT','/O2','/EHsc','/std:c++17','/utf-8',"/I$DependencyRoot","/I$DependencyRoot\minhook\include","/Fo$build\\")
$hookSources=@('buffer.c','hook.c','trampoline.c','hde\hde32.c') | ForEach-Object {Join-Path "$DependencyRoot\minhook\src" $_}
Push-Location $build
try {
    & cl @common /LD (Join-Path $PSScriptRoot 'Speed113Payload.cpp') @hookSources /link winmm.lib "/DEF:$PSScriptRoot\Speed113Payload.def" "/OUT:$build\Speed113Payload.dll"
    if($LASTEXITCODE -ne 0){throw 'WOLF neutral payload build failed'}
    & cl @common (Join-Path $PSScriptRoot 'Speed113.cpp') /link winmm.lib "/OUT:$build\Speed113.exe"
    if($LASTEXITCODE -ne 0){throw 'WOLF neutral fixture build failed'}
    & cl @common /LD (Join-Path $PSScriptRoot 'Speed113GenericMarker.cpp') /link "/OUT:$build\FusionSpeed.x86.dll"
    if($LASTEXITCODE -ne 0){throw 'WOLF coexistence marker build failed'}
    & cl @common (Join-Path $PSScriptRoot 'Speed40.cpp') @hookSources /link "/OUT:$build\Speed40.exe"
    if($LASTEXITCODE -ne 0){throw 'WOLF deterministic protocol build failed'}
    $math=@(& "$build\Speed40.exe");$mathExit=$LASTEXITCODE
    $math | Set-Content -LiteralPath (Join-Path $VerificationRoot 'deterministic.txt') -Encoding utf8
    if($mathExit -ne 0){throw 'WOLF deterministic protocol check failed'}
    $mathResult=($math|Where-Object {$_ -match '^\{"checks":'}|Select-Object -Last 1)|ConvertFrom-Json
    $dataReports=@()
    foreach($name in @('DisabledData101','Data32','TargetedData101')){
        & cl @common (Join-Path $PSScriptRoot ($name+'.cpp')) /link "/OUT:$build\$name.exe"
        if($LASTEXITCODE -ne 0){throw "WOLF data regression build failed: $name"}
        $text=@(& "$build\$name.exe");$exitCode=$LASTEXITCODE
        $text|Set-Content -LiteralPath (Join-Path $VerificationRoot ($name+'.txt')) -Encoding utf8
        $result=($text|Where-Object {$_ -match '^\{"checks":'}|Select-Object -Last 1)|ConvertFrom-Json
        $dataReports+=@{name=$name;checks=$result.checks;failures=$result.failures;exitCode=$exitCode}
        if($exitCode -ne 0 -or -not $result){throw "WOLF data regression failed: $name"}
    }
    $wolfRoot=Split-Path $PSScriptRoot -Parent
    Push-Location $wolfRoot
    try{& rc /nologo "/fo$build\WolfBridge.res" WolfBridge.rc;if($LASTEXITCODE -ne 0){throw 'Production WOLF resources did not compile'}}finally{Pop-Location}
    & cl @common /LD (Join-Path $wolfRoot 'WolfBridge.cpp') @hookSources /link "$build\WolfBridge.res" gdi32.lib user32.lib "/DEF:$wolfRoot\WolfBridge.def" "/OUT:$build\FusionWolf.dll"
    if($LASTEXITCODE -ne 0){throw 'Production WOLF bridge did not compile'}
    $single=Join-Path $VerificationRoot 'single'
    & "$build\Speed113.exe" $single 'single'
    if($LASTEXITCODE -ne 0){throw 'WOLF neutral clock check failed'}
    $pairA=Join-Path $VerificationRoot 'pair-A';$pairB=Join-Path $VerificationRoot 'pair-B'
    foreach($path in @($pairA,$pairB)){
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        foreach($name in @('ready.json','results.json','stop.signal','peer-closed.signal')){
            $old=Join-Path $path $name
            if(Test-Path -LiteralPath $old){Move-Item -LiteralPath $old -Destination ($old+'.previous-'+[DateTime]::UtcNow.Ticks)}
        }
    }
    $a=$null;$b=$null
    try {
        $a=Start-Process -FilePath "$build\Speed113.exe" -ArgumentList @(('"'+$pairA+'"'),'A') -WindowStyle Hidden -PassThru
        $b=Start-Process -FilePath "$build\Speed113.exe" -ArgumentList @(('"'+$pairB+'"'),'B') -WindowStyle Hidden -PassThru
        $until=[Diagnostics.Stopwatch]::StartNew()
        while(-not ((Test-Path -LiteralPath (Join-Path $pairA 'ready.json')) -and (Test-Path -LiteralPath (Join-Path $pairB 'ready.json')))){
            if($until.Elapsed.TotalSeconds -gt 12 -or $a.HasExited -or $b.HasExited){throw 'Owned parallel WOLF fixture did not become ready'}
            Start-Sleep -Milliseconds 30
        }
        Set-Content -LiteralPath (Join-Path $pairA 'stop.signal') -Value 'close owned A'
        if(-not $a.WaitForExit(6000)-or $a.ExitCode -ne 0){throw 'Owned WOLF fixture A did not exit normally'}
        Set-Content -LiteralPath (Join-Path $pairB 'peer-closed.signal') -Value 'owned A exited'
        if(-not $b.WaitForExit(6000)-or $b.ExitCode -ne 0){throw 'Owned WOLF fixture B did not exit normally'}
    } finally {
        foreach($child in @($a,$b)){if($child){if(-not $child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}}
    }
    $runs=@($single,$pairA,$pairB)|ForEach-Object {Get-Content -Raw -LiteralPath (Join-Path $_ 'results.json')|ConvertFrom-Json}
    $production=@('WolfSpeed.h','WolfSpeedProtocol.h','WolfData.h','WolfBridge.cpp')|ForEach-Object {$path=Join-Path (Split-Path $PSScriptRoot -Parent) $_;@{file=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}}
    $summary=@{scope='Controlled production WOLF clock/protocol headers in x86 neutral process and DLL, synthetic data regressions, full WOLF DLL compile. No actual WOLF game, renderer, save, API or release acceptance.';checks=([int]$mathResult.checks+($runs|Measure-Object -Property checks -Sum).Sum+($dataReports|Measure-Object -Property checks -Sum).Sum);failures=([int]$mathResult.failed+($runs|Measure-Object -Property failures -Sum).Sum+($dataReports|Measure-Object -Property failures -Sum).Sum);deterministic=$mathResult;dataRegressions=$dataReports;runs=$runs;productionSources=$production;productionBridgeSha256=(Get-FileHash -LiteralPath "$build\FusionWolf.dll" -Algorithm SHA256).Hash}
    $summary|ConvertTo-Json -Depth 20|Set-Content -LiteralPath (Join-Path $VerificationRoot 'results.json') -Encoding utf8
    Write-Output ("WOLF speed checks="+$summary.checks+" failures="+$summary.failures)
    if($summary.failures -ne 0){throw 'WOLF speed report has failures'}
} finally {Pop-Location}
