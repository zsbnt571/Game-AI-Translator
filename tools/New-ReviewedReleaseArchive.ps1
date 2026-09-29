<#
Explicit local packaging command. No network, publishing, downloading, or automatic
approval. Requires an independently reviewed exact artifact allowlist. Audits the
input, copies its exact approved files, audits the snapshot, and verifies ZIP entry
hashes before atomically exposing the final archive. Never run as a release action
until the user has separately approved packaging.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CandidateDirectory,
    [Parameter(Mandatory = $true)][string]$AllowlistPath,
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$ReportDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$candidateRoot = [IO.Path]::GetFullPath($CandidateDirectory).TrimEnd('\', '/')
$reportRoot = [IO.Path]::GetFullPath($ReportDirectory).TrimEnd('\', '/')
$archive = [IO.Path]::GetFullPath($ArchivePath)
function Assert-OutsideCandidate([string]$path) {
    if ($path.Equals($candidateRoot, [StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($candidateRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Reports, staging, and archive output must be outside the candidate.'
    }
}
function Assert-NoLink([string]$path) {
    for ($current = [IO.Path]::GetFullPath($path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Packaging paths cannot contain links.' }
    }
}
Assert-OutsideCandidate $reportRoot
Assert-OutsideCandidate $archive
Assert-NoLink $candidateRoot
Assert-NoLink $reportRoot
Assert-NoLink $archive
if ([IO.Path]::GetExtension($archive) -ne '.zip') { throw 'The output must have a .zip extension.' }
if (Test-Path -LiteralPath $archive) { throw 'Archive already exists; refusing to overwrite.' }
if (-not [IO.Directory]::Exists($candidateRoot)) { throw 'Candidate directory does not exist.' }
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
$workRoot = Join-Path $reportRoot ('package-work-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null
$stage = Join-Path $workRoot 'snapshot'
$partial = Join-Path $workRoot 'verified-output.zip'
try {
    # Freeze approval inputs for this operation. No input directory is auto-approved.
    $inputs = @{
        'allowlist.json' = $AllowlistPath
        'policy.json' = (Join-Path $projectRoot 'docs/redistribution-policy.json')
        'local-dependencies.json' = (Join-Path $projectRoot 'docs/local-dependencies.json')
    }
    foreach ($name in $inputs.Keys) {
        $stream = [IO.File]::Open([IO.Path]::GetFullPath($inputs[$name]), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $copy = [IO.File]::Open((Join-Path $workRoot $name), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $stream.CopyTo($copy) } finally { $copy.Dispose() }
        } finally { $stream.Dispose() }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $workRoot 'allowlist.json') -Raw | ConvertFrom-Json
    $toolOutput = Join-Path $reportRoot 'audit-tool'
    & dotnet build (Join-Path $PSScriptRoot 'ReleaseAudit/ReleaseAudit.csproj') -c Release --artifacts-path (Join-Path $reportRoot 'tool-build') -o $toolOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release audit tool build failed.' }
    $toolDll = Join-Path $toolOutput 'ReleaseAudit.dll'
    function Invoke-RequiredGate([string]$directory, [string]$reportName) {
        & dotnet $toolDll audit $directory (Join-Path $workRoot 'allowlist.json') (Join-Path $workRoot 'policy.json') (Join-Path $workRoot 'local-dependencies.json') (Join-Path $reportRoot $reportName)
        if ($LASTEXITCODE -ne 0) { throw "Release gate rejected $reportName; no archive will be published." }
    }
    Invoke-RequiredGate $candidateRoot 'package-input-audit.json'
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    foreach ($entry in $manifest.files) {
        $from = Join-Path $candidateRoot $entry.path
        $to = Join-Path $stage $entry.path
        Assert-NoLink $from
        Assert-NoLink $to
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to)) | Out-Null
        $input = [IO.File]::Open($from, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $target = [IO.File]::Open($to, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $input.CopyTo($target) } finally { $target.Dispose() }
        } finally { $input.Dispose() }
    }
    Invoke-RequiredGate $stage 'package-snapshot-audit.json'
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $partial, [IO.Compression.CompressionLevel]::Optimal, $false)
    # Verify the bytes actually archived too, so mutations after snapshot audit fail.
    $expected = @{}
    foreach ($entry in $manifest.files) { $expected.Add($entry.path, $entry.sha256) }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $zip = [IO.Compression.ZipFile]::OpenRead($partial)
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name.EndsWith('/')) { continue }
            if (-not $expected.ContainsKey($name) -or -not $seen.Add($name)) { throw "Unknown or duplicate archived entry: $name" }
            $stream = $entry.Open()
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
            } finally { $stream.Dispose() }
            if ($hash -ne $expected[$name]) { throw "Archive entry changed: $name" }
        }
    } finally { $zip.Dispose() }
    if ($seen.Count -ne $expected.Count) { throw 'The archive is missing approved files.' }
    Assert-NoLink $archive
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($archive)) | Out-Null
    [IO.File]::Move($partial, $archive)
    [ordered]@{ passed = $true; files = $seen.Count; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(); note = 'Local audited archive only. No release or upload performed.' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportRoot 'package-result.json') -Encoding utf8
    Write-Output "Created verified local archive with $($seen.Count) files. No upload or release performed."
} finally {
    # This operation owns only its fresh GUID work directory. Verify before deleting.
    $resolvedWork = [IO.Path]::GetFullPath($workRoot)
    if (-not $resolvedWork.StartsWith($reportRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedWork) -notmatch '^package-work-[a-f0-9]{32}$') { throw 'Unsafe packaging cleanup target.' }
    if (Test-Path -LiteralPath $resolvedWork) {
        Assert-NoLink $resolvedWork
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
