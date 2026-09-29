param([Parameter(Mandatory = $true)][string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceBase = (Resolve-Path -LiteralPath $SourceRoot).Path
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'docs/local-dependencies.json') -Raw | ConvertFrom-Json
$pending = @()
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    if (-not $relative.StartsWith('Source/')) { throw "Invalid dependency path: $relative" }
    $target = [IO.Path]::GetFullPath((Join-Path $repoRoot $relative))
    $origin = [IO.Path]::GetFullPath((Join-Path $sourceBase $relative.Substring(7)))
    if (-not $target.StartsWith($repoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $origin.StartsWith($sourceBase.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Dependency path escapes its root: $relative"
    }
    if (-not (Test-Path -LiteralPath $origin -PathType Leaf)) { throw "Missing dependency: $relative" }
    if ((Get-FileHash -LiteralPath $origin -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Source hash mismatch: $relative" }
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Existing destination differs: $relative" }
    } else { $pending += [pscustomobject]@{ Origin = $origin; Target = $target } }
}
foreach ($item in $pending) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $item.Target) -Force | Out-Null
    Copy-Item -LiteralPath $item.Origin -Destination $item.Target
}
Write-Host "Dependencies verified: $($manifest.files.Count); imported: $($pending.Count)."
