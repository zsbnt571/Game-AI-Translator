param(
    [Parameter(Mandatory=$true)][string]$SourceRoot,
    [Parameter(Mandatory=$true)][string]$DestinationRoot
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not [IO.Path]::IsPathFullyQualified($DestinationRoot)) { throw 'DestinationRoot must be an absolute local directory.' }
$destination = [IO.Path]::GetFullPath($DestinationRoot)
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$catalog = Get-Content -LiteralPath (Join-Path $repo 'Source/Screenshot/RuntimePayloadCatalog.json') -Raw | ConvertFrom-Json
$locations = @{
    'unity-mono-x86'='Source/Plugin/UnityEmbedded/Resources/UnityEmbeddedMono32.zip'
    'unity-mono-x64'='Source/Plugin/UnityEmbedded/Resources/UnityEmbeddedMono64.zip'
    'unity-il2cpp-x64'='Source/Plugin/UnityEmbedded/Resources/UnityEmbeddedIl2Cpp64.zip'
    'unity-legacy'='Source/Plugin/Payload/UnityMono.zip'
    'unity-specialized'='Source/Plugin/Payload/CloudMeadow.zip'
    'unreal-runtime'='Source/UnrealBridge/UnrealRuntime.zip'
}
function Assert-NoLink([string]$path) {
    for ($current=[IO.Path]::GetFullPath($path); $current; $current=[IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Dependency paths must not use links.' }
    }
}
# All inputs are validated before creating destination files. Import is local only;
# neither this manifest nor a matching hash grants permission to distribute a ZIP.
$plan = foreach ($package in $catalog) {
    $from = Join-Path $source $locations[$package.id]
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
