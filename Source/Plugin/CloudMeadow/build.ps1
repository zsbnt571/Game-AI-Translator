param([Parameter(Mandatory=$true)][string]$GameRoot,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$managed=Join-Path $GameRoot 'Cloud Meadow_Data\Managed'
$compiler='C:\Program Files\dotnet\sdk\10.0.103\Roslyn\bincore\csc.dll'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$names=@('mscorlib.dll','System.dll','System.Core.dll','Newtonsoft.Json.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.UIModule.dll','UnityEngine.UI.dll','Unity.TextMeshPro.dll','UnityEngine.UnityWebRequestModule.dll','UnityEngine.InputModule.dll','UnityEngine.IMGUIModule.dll')
$refs=@($names|ForEach-Object{Join-Path $managed $_})+@("$PSScriptRoot\References\BepInEx.dll","$PSScriptRoot\References\0Harmony.dll")
foreach($ref in $refs){if(-not(Test-Path -LiteralPath $ref)){throw "Missing compile reference: $ref"}}
$arguments=@($compiler,'-nologo','-target:library','-langversion:latest','-nostdlib+','-optimize+','-deterministic+',"-out:$OutputDirectory\CloudMeadowTranslator.dll")
$arguments+=@($refs|ForEach-Object{"-reference:$_"})
$arguments+=@((Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File).FullName)
& 'C:\Program Files\dotnet\dotnet.exe' @arguments
if($LASTEXITCODE -ne 0){throw 'Cloud Meadow plugin compilation failed.'}
