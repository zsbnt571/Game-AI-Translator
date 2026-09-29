param(
    [string]$ReferenceDirectory = "$PSScriptRoot\References",
    [string]$OutputDirectory = "$PSScriptRoot\..\..\Work\plugin-build",
    [string]$DotNet = (Get-Command dotnet -ErrorAction Stop).Source,
    [string]$SdkVersion = '10.0.103'
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path (Split-Path $DotNet) "sdk\$SdkVersion\Roslyn\bincore\csc.dll"
if (-not (Test-Path -LiteralPath $compiler)) { throw "Required compiler missing: $compiler" }
$references = @('mscorlib.dll','System.dll','System.Core.dll','UnityEngine.dll','UnityEngine.UI.dll','BepInEx.dll') |
    ForEach-Object {
        $path = Join-Path $ReferenceDirectory $_
        if (-not (Test-Path -LiteralPath $path)) { throw "Compile reference missing: $path" }
        $path
    }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $output 'MGITranslator.dll')) { throw 'Build DLL already exists; choose a new output directory.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$arguments = @($compiler,'-nologo','-target:library','-langversion:latest','-nostdlib+','-optimize+',"-out:$output\MGITranslator.dll")
$arguments += $references | ForEach-Object { "-reference:$_" }
$arguments += "$PSScriptRoot\MGITranslator.cs"
$arguments += "$PSScriptRoot\FungusDialogueNormalizer.cs"
& $DotNet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Plugin compilation failed.' }
