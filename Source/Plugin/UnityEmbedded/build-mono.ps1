param([string]$OutputDirectory = "$PSScriptRoot\..\..\..\Build\UnityEmbedded\Mono",[string]$FrameworkReferenceRoot = '',[string]$DotNet=(Get-Command dotnet -ErrorAction Stop).Source,[string]$SdkVersion='10.0.103')
$ErrorActionPreference='Stop'
$pluginRoot=Split-Path $PSScriptRoot
$compiler=Join-Path (Split-Path $DotNet) "sdk\$SdkVersion\Roslyn\bincore\csc.dll"
$referenceRoot=Join-Path $pluginRoot 'References'
$frameworkRoot=if($FrameworkReferenceRoot){$FrameworkReferenceRoot}else{$referenceRoot}
$references=@('mscorlib.dll','System.dll','System.Core.dll') | ForEach-Object {Join-Path $frameworkRoot $_}
$references+=@('UnityEngine.dll','BepInEx.dll') | ForEach-Object {Join-Path $referenceRoot $_}
$references+=Join-Path $pluginRoot 'CloudMeadow\References\0Harmony.dll'
foreach($reference in $references){if(-not(Test-Path -LiteralPath $reference)){throw "Missing reference: $reference"}}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$arguments=@($compiler,'-nologo','-target:library','-langversion:latest','-nostdlib+','-optimize+','-deterministic+',"-out:$OutputDirectory\FusionUnityEmbedded.dll")
$arguments+=@($references | ForEach-Object {"-reference:$_"})
$arguments+=@('BridgeJson.cs','TextLookup.cs','UnityPipeStream.cs','UnityBridgeRuntime.cs','MonoPlugin.cs') | ForEach-Object {Join-Path $PSScriptRoot $_}
& $DotNet @arguments
if($LASTEXITCODE -ne 0){throw 'Unity embedded Mono compilation failed.'}
