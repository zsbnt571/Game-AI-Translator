param([string]$WorkRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..')),[string]$ToolRoot='',[string]$DotNet=(Get-Command dotnet -ErrorAction Stop).Source)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$buildRoot=Join-Path $WorkRoot 'Build\UnityEmbedded'
$unityToolRoot=if($ToolRoot){$ToolRoot}else{Join-Path $WorkRoot 'Tools\UnityEmbedded'}
& (Join-Path $PSScriptRoot 'build-mono.ps1') -OutputDirectory (Join-Path $buildRoot 'Mono') -DotNet $DotNet
& $DotNet build (Join-Path $PSScriptRoot 'FusionUnityIL2CPP.csproj') -c Release -o (Join-Path $buildRoot 'IL2CPP') --nologo "-p:LoaderDirectory=$unityToolRoot\BepInEx6-IL2CPP\BepInEx\core" "-p:BaseIntermediateOutputPath=$buildRoot\IL2CPP-obj\"
if($LASTEXITCODE -ne 0){throw 'IL2CPP compilation failed.'}
$packages=@(
    @{Input='BepInEx_win_x64_5.4.23.5.zip';Output='UnityEmbeddedMono64.zip';Runtime='Mono'},
    @{Input='BepInEx_win_x86_5.4.23.5.zip';Output='UnityEmbeddedMono32.zip';Runtime='Mono'},
    @{Input='BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip';Output='UnityEmbeddedIl2Cpp64.zip';Runtime='IL2CPP'}
)
$resourceRoot=Join-Path $PSScriptRoot 'Resources'
New-Item -ItemType Directory -Force -Path $resourceRoot | Out-Null
foreach($package in $packages)
{
    $destination=Join-Path $resourceRoot $package.Output
    $temporary=$destination+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    $input=[IO.Compression.ZipFile]::OpenRead((Join-Path $unityToolRoot $package.Input))
    $output=[IO.Compression.ZipFile]::Open($temporary,[IO.Compression.ZipArchiveMode]::Create)
    try
    {
        foreach($entry in $input.Entries)
        {
            if($entry.FullName.EndsWith('/')){continue}
            $copy=$output.CreateEntry($entry.FullName,[IO.Compression.CompressionLevel]::Optimal)
            $sourceStream=$entry.Open();$targetStream=$copy.Open()
            try{$sourceStream.CopyTo($targetStream)}finally{$sourceStream.Dispose();$targetStream.Dispose()}
        }
        $entry=$output.CreateEntry('BepInEx/plugins/FusionUnityEmbedded/FusionUnityEmbedded.dll',[IO.Compression.CompressionLevel]::Optimal)
        $sourceStream=[IO.File]::OpenRead((Join-Path $buildRoot ($package.Runtime+'\FusionUnityEmbedded.dll')));$targetStream=$entry.Open()
        try{$sourceStream.CopyTo($targetStream)}finally{$sourceStream.Dispose();$targetStream.Dispose()}
        $notice=$output.CreateEntry('BepInEx/plugins/FusionUnityEmbedded/THIRD-PARTY-NOTICES.txt')
        $writer=New-Object IO.StreamWriter($notice.Open())
        try{$writer.WriteLine('BepInEx Mono loader v5.4.23.5 is MIT. BepInEx IL2CPP v6.0.0-be.788 (commit 5b766a3) is LGPL-2.1. Source: https://github.com/BepInEx/BepInEx . These unmodified official loader binaries and their bundled dependencies retain their upstream licenses; full texts and pinned sources are in the adjacent licenses directory.');$writer.WriteLine('FusionUnityEmbedded is a separate plugin. Existing specialized MGI and Cloud Meadow plugins are not included.')}finally{$writer.Dispose()}
        $licenseRoot=Join-Path $PSScriptRoot 'Licenses'
        foreach($licenseFile in Get-ChildItem -LiteralPath $licenseRoot -File -Recurse)
        {
            $relative=[IO.Path]::GetRelativePath($licenseRoot,$licenseFile.FullName).Replace('\','/')
            $licenseEntry=$output.CreateEntry('BepInEx/plugins/FusionUnityEmbedded/licenses/'+$relative,[IO.Compression.CompressionLevel]::Optimal)
            $sourceStream=[IO.File]::OpenRead($licenseFile.FullName);$targetStream=$licenseEntry.Open()
            try{$sourceStream.CopyTo($targetStream)}finally{$sourceStream.Dispose();$targetStream.Dispose()}
        }
    }
    finally{$output.Dispose();$input.Dispose()}
    Move-Item -LiteralPath $temporary -Destination $destination -Force
    Get-FileHash -LiteralPath $destination -Algorithm SHA256 | Select-Object Path,Hash
}
