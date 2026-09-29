param(
    [Parameter(Mandatory=$true)][string]$SourceRoot,
    [Parameter(Mandatory=$true)][string]$DestinationRoot,
    [string[]]$PackageId = @('unity-mono-x86','unity-mono-x64','unity-il2cpp-x64','unity-legacy','unity-specialized','unreal-runtime'),
    [switch]$FlatSource,
    [string]$CatalogPath
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [IO.Path]::IsPathFullyQualified($DestinationRoot)) { throw 'DestinationRoot must be an absolute local directory.' }
$destination = [IO.Path]::GetFullPath($DestinationRoot)
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
if ([string]::IsNullOrWhiteSpace($CatalogPath)) {
    $sourceCatalog = Join-Path $repo 'Source/Screenshot/RuntimePayloadCatalog.json'
    $CatalogPath = if (Test-Path -LiteralPath $sourceCatalog -PathType Leaf) { $sourceCatalog } else { Join-Path $repo 'metadata/runtime-payload-catalog.json' }
}
$catalogFile = [IO.Path]::GetFullPath($CatalogPath)
if (-not (Test-Path -LiteralPath $catalogFile -PathType Leaf)) { throw 'Missing runtime payload catalogue. Supply -CatalogPath with the reviewed catalogue file.' }
if ((Get-Item -LiteralPath $catalogFile).Length -gt 16384) { throw 'Runtime payload catalogue exceeds the supported size.' }
$catalog = Get-Content -LiteralPath $catalogFile -Raw | ConvertFrom-Json
$catalog = @($catalog) + @(
    [pscustomobject]@{ id='unity-classdata'; engine='Unity'; backend='Any'; architecture='Any'; version='classdata-129e1f80f930'; fileName='classdata.tpk'; bytes=289605; sha256='129e1f80f930415db6779fe6089afa75280cb51462bcee812beab6cd81a764c6' },
    [pscustomobject]@{ id='unreal-oodle'; engine='Unreal'; backend='Native'; architecture='x64'; version='oodle9-6f5d41a7892e'; fileName='oo2core_9_win64.dll'; bytes=637952; sha256='6f5d41a7892ea6b2db420f2458dad2f84a63901c9a93ce9497337b16c195f457' }
)
if ($PackageId.Count -eq 0 -or @($PackageId | Select-Object -Unique).Count -ne $PackageId.Count) { throw 'Select one or more distinct pinned package IDs.' }
foreach ($id in $PackageId) { if ($id -notin $catalog.id) { throw "Unknown pinned package ID: $id" } }
$catalog = @($catalog | Where-Object { $_.id -in $PackageId })
$locations = @{
    'unity-mono-x86'='Source/Plugin/UnityEmbedded/Resources/UnityEmbeddedMono32.zip'
    'unity-mono-x64'='Source/Plugin/UnityEmbedded/Resources/UnityEmbeddedMono64.zip'
    'unity-il2cpp-x64'='Source/Plugin/UnityEmbedded/Resources/UnityEmbeddedIl2Cpp64.zip'
    'unity-legacy'='Source/Plugin/Payload/UnityMono.zip'
    'unity-specialized'='Source/Plugin/Payload/CloudMeadow.zip'
    'unreal-runtime'='Source/UnrealBridge/UnrealRuntime.zip'
    'unity-classdata'='Source/Plugin/UnityEmbedded/Resources/classdata.tpk'
    'unreal-oodle'='Source/UnrealCatalog/Native/oo2core_9_win64.dll'
}
function Assert-NoLink([string]$path) {
    for ($current=[IO.Path]::GetFullPath($path); $current; $current=[IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Dependency paths must not use links.' }
    }
}
Assert-NoLink $catalogFile
foreach ($package in $catalog) {
    if ($package.id -notin $locations.Keys -or $package.version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or $package.fileName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or $package.version -in @('.','..') -or $package.fileName -in @('.','..')) { throw 'Catalogue contains an unsupported identity or unsafe path segment.' }
    if ($package.bytes -lt 0 -or $package.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Catalogue integrity metadata is invalid.' }
}
if (@($catalog.id | Select-Object -Unique).Count -ne $catalog.Count) { throw 'Catalogue contains duplicate package IDs.' }
# All inputs are validated before creating destination files. Import is local only;
# neither this manifest nor a matching hash grants permission to distribute a ZIP.
$plan = foreach ($package in $catalog) {
    $sourceRelative = if ($FlatSource) { $package.fileName } else { $locations[$package.id] }
    $from = Join-Path $source $sourceRelative
    $to = Join-Path $destination ($package.id + '/' + $package.version + '/' + $package.fileName)
    Assert-NoLink $from
    Assert-NoLink $to
    Assert-NoLink ($to + '.json')
    if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { throw "Missing local input: $($package.id)" }
    if ((Get-Item -LiteralPath $from).Length -ne $package.bytes -or (Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ne $package.sha256) { throw "Input hash mismatch: $($package.id)" }
    if (Test-Path -LiteralPath $to) {
        if ((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ne $package.sha256) { throw "Destination differs; not overwriting: $($package.id)" }
    }
    $descriptor = $package | ConvertTo-Json -Depth 4
    if ((Test-Path -LiteralPath ($to + '.json')) -and (Get-Content -LiteralPath ($to + '.json') -Raw).Trim() -ne $descriptor.Trim()) { throw "Destination descriptor differs; not overwriting: $($package.id)" }
    [pscustomobject]@{ From=$from; To=$to; Package=$package; Descriptor=$descriptor }
}
foreach ($item in $plan) {
    Assert-NoLink $item.To
    Assert-NoLink ($item.To + '.json')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($item.To)) | Out-Null
    if (-not (Test-Path -LiteralPath $item.To)) {
        $temporary = $item.To + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            Copy-Item -LiteralPath $item.From -Destination $temporary
            if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $item.Package.sha256) { throw 'Input changed during import.' }
            [IO.File]::Move($temporary,$item.To)
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    }
    if ((Get-Item -LiteralPath $item.To).Length -ne $item.Package.bytes -or (Get-FileHash -LiteralPath $item.To -Algorithm SHA256).Hash -ne $item.Package.sha256) { throw "Destination changed during import: $($item.Package.id)" }
    Assert-NoLink ($item.To + '.json')
    if ((Test-Path -LiteralPath ($item.To + '.json')) -and (Get-Content -LiteralPath ($item.To + '.json') -Raw).Trim() -ne $item.Descriptor.Trim()) { throw "Descriptor changed during import: $($item.Package.id)" }
    if (-not (Test-Path -LiteralPath ($item.To + '.json'))) {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($item.Descriptor)
        $stream = [IO.File]::Open($item.To + '.json',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
    }
}
Write-Output "Verified $($plan.Count) local-only runtime packages. Do not copy this directory into a public release."
