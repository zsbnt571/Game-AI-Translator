param([string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $sourceRoot 'Build'
if (Test-Path -LiteralPath $outputRoot) { throw 'Build already exists. Preserve it and build from a fresh source extraction.' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$desktopRoot = Join-Path $outputRoot 'Desktop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
Push-Location $sourceRoot
try {
    foreach ($project in @('Screenshot\OcrHelper\ScreenshotOcrHelper.csproj', 'CoverCapture\FusionCoverCapture.csproj', 'UnitySchema\FusionUnitySchema.csproj', 'YukarBridge\FusionYukarBridge.csproj')) {
        & $DotNet restore (Join-Path $sourceRoot "Source\$project") --nologo "-p:FusionBuildRoot=$desktopRoot" '-p:RestoreSources=https://api.nuget.org/v3/index.json'
        if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
        & $DotNet build (Join-Path $sourceRoot "Source\$project") -c Release --no-restore --nologo -m:1 -p:UseSharedCompilation=false "-p:FusionBuildRoot=$desktopRoot"
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
    & $DotNet publish (Join-Path $sourceRoot 'Source\Screenshot\ScreenshotTranslationUiTester.csproj') -c Release -r win-x64 --self-contained true --nologo -m:1 -p:UseSharedCompilation=false "-p:FusionBuildRoot=$desktopRoot" '-p:RestoreSources=https://api.nuget.org/v3/index.json' -p:RuntimeFrameworkVersion=8.0.24 -p:SkipOcrHelperBuild=true -p:SkipGameCoverBuild=true -p:SkipUnitySchemaBuild=true -p:SkipYukarBridgeBuild=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $outputRoot 'Publish')
    if ($LASTEXITCODE -ne 0) { throw 'Main publish failed' }
    foreach ($item in @(@('CoverCapture\FusionCoverCapture.csproj', 'cover-capture', '8.0.24'), @('UnitySchema\FusionUnitySchema.csproj', 'unity-schema', '8.0.24'), @('UnrealCatalog\FusionUnrealCatalog.csproj', 'unreal-catalog', '10.0.3'))) {
        $runtimeArgs = @()
        if ($item[1] -ne 'cover-capture') { $runtimeArgs = @("-p:RuntimeFrameworkVersion=$($item[2])") }
        & $DotNet publish (Join-Path $sourceRoot "Source\$($item[0])") -c Release -r win-x64 --self-contained true --nologo -m:1 -p:UseSharedCompilation=false "-p:FusionBuildRoot=$desktopRoot" '-p:RestoreSources=https://api.nuget.org/v3/index.json' @runtimeArgs -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $outputRoot "Helpers\$($item[1])")
        if ($LASTEXITCODE -ne 0) { throw "Helper publish failed: $($item[1])" }
    }
} finally { Pop-Location }
Write-Output 'Build completed. This is a developer build, not a fully assembled public package.'
