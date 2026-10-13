param(
    [string]$BaselineZip =
        "$PSScriptRoot\payloads\UnityMono.zip",
    [string]$PluginDll =
        "$PSScriptRoot\..\Plugin\VerifiedBinary\MGITranslator.dll",
    [string]$OutputZip =
        "$PSScriptRoot\..\..\Work\payload-check\UnityMono.zip"
)

$ErrorActionPreference = "Stop"
$baseline = (Resolve-Path -LiteralPath $BaselineZip).Path
$plugin = (Resolve-Path -LiteralPath $PluginDll).Path
$output = [IO.Path]::GetFullPath($OutputZip)
$outputDirectory = [IO.Path]::GetDirectoryName($output)
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$temporary = $output + ".tmp"
if ([IO.File]::Exists($temporary)) {
    [IO.File]::Delete($temporary)
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceArchive = [IO.Compression.ZipFile]::OpenRead($baseline)
$targetStream = [IO.File]::Create($temporary)
$targetArchive = [IO.Compression.ZipArchive]::new(
    $targetStream,
    [IO.Compression.ZipArchiveMode]::Create,
    $false)
try {
    foreach ($entry in $sourceArchive.Entries) {
        $portableName = $entry.FullName.Replace('\', '/')
        $newEntry = $targetArchive.CreateEntry(
            $portableName,
            [IO.Compression.CompressionLevel]::Optimal)
        if ([string]::IsNullOrEmpty($entry.Name)) {
            continue
        }
        $outputStream = $newEntry.Open()
        try {
            if ($portableName.Equals(
                "BepInEx/plugins/MGITranslator/MGITranslator.dll",
                [StringComparison]::OrdinalIgnoreCase)) {
                $inputStream = [IO.File]::OpenRead($plugin)
            } else {
                $inputStream = $entry.Open()
            }
            try {
                $inputStream.CopyTo($outputStream)
            } finally {
                $inputStream.Dispose()
            }
        } finally {
            $outputStream.Dispose()
        }
    }
} finally {
    $targetArchive.Dispose()
    $targetStream.Dispose()
    $sourceArchive.Dispose()
}

if ([IO.File]::Exists($output)) {
    [IO.File]::Delete($output)
}
[IO.File]::Move($temporary, $output)
Write-Host "Built $output"
