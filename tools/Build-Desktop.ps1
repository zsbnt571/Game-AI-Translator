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
    $common = @(
        '-c', 'Release', '--nologo', '-m:1', '-p:UseSharedCompilation=false',
        "-p:FusionBuildRoot=$desktopRoot", "-p:PathMap=$sourceRoot=/_/Fusion",
        '-p:RestoreSources=https://api.nuget.org/v3/index.json',
        '-p:DebugType=None', '-p:DebugSymbols=false'
    )
    foreach ($project in @('Screenshot\OcrHelper\ScreenshotOcrHelper.csproj', 'CoverCapture\FusionCoverCapture.csproj', 'YukarBridge\FusionYukarBridge.csproj')) {
        # Build restores the non-RID helper assets needed by the main copy targets.
        & $DotNet build (Join-Path $sourceRoot "Source\$project") @common
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
    $publishProperties = @(
        '-p:SkipOcrHelperBuild=true', '-p:SkipGameCoverBuild=true',
        '-p:SkipYukarBridgeBuild=true', '-p:SkipUnitySchemaBuild=true'
    )
    # Publish restores with the same RID, self-contained and patch properties.
    # Cover must precede the main project: the latter's PublishGameCover target
    # publishes without restoring and therefore needs these win-x64 assets first.
    foreach ($item in @(@('CoverCapture\FusionCoverCapture.csproj', 'cover-capture', '8.0.33'), @('UnitySchema\FusionUnitySchema.csproj', 'unity-schema', '8.0.33'), @('UnrealCatalog\FusionUnrealCatalog.csproj', 'unreal-catalog', '10.0.14'))) {
        & $DotNet publish (Join-Path $sourceRoot "Source\$($item[0])") @common -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$($item[2])" @publishProperties -o (Join-Path $outputRoot "Helpers\$($item[1])")
        if ($LASTEXITCODE -ne 0) { throw "Helper publish failed: $($item[1])" }
    }
    & $DotNet publish (Join-Path $sourceRoot 'Source\Screenshot\ScreenshotTranslationUiTester.csproj') @common -r win-x64 --self-contained true '-p:RuntimeFrameworkVersion=8.0.33' '-p:UnitySchemaRuntimeVersion=8.0.33' @publishProperties -o (Join-Path $outputRoot 'Publish')
    if ($LASTEXITCODE -ne 0) { throw 'Main publish failed' }
} finally { Pop-Location }
Write-Output 'Build completed: Build\Publish contains the desktop output; Build\Helpers contains the three independently published helpers. This is a developer build, not a fully assembled public package.'


