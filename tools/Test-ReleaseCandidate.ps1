<#
Read-only, fail-closed release gate. This does not package, download, approve files,
change policy, load candidate assemblies, or create a GitHub Release.
Use -AllowlistPath for a separately reviewed exact artifact manifest. Inspection
output is evidence only: it must never be auto-promoted to distribution approval.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CandidateDirectory,
    [Parameter(Mandatory = $true)][string]$ReportDirectory,
    [string]$AllowlistPath,
    [switch]$RunSelfTests
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $AllowlistPath) { $AllowlistPath = Join-Path $projectRoot 'docs/release-allowlist.json' }
$candidateRoot = [IO.Path]::GetFullPath($CandidateDirectory).TrimEnd('\', '/')
$reportRoot = [IO.Path]::GetFullPath($ReportDirectory).TrimEnd('\', '/')
if ($reportRoot.Equals($candidateRoot, [StringComparison]::OrdinalIgnoreCase) -or $reportRoot.StartsWith($candidateRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Report/build output must be outside the Release Candidate.'
}
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
$toolProject = Join-Path $PSScriptRoot 'ReleaseAudit/ReleaseAudit.csproj'
$toolOutput = Join-Path $reportRoot 'audit-tool'
& dotnet build $toolProject -c Release --artifacts-path (Join-Path $reportRoot 'tool-build') -o $toolOutput --nologo
if ($LASTEXITCODE -ne 0) { throw "Release audit tool build failed ($LASTEXITCODE)." }
$toolDll = Join-Path $toolOutput 'ReleaseAudit.dll'
if ($RunSelfTests) {
    & dotnet $toolDll self-test (Join-Path $reportRoot 'tests')
    if ($LASTEXITCODE -ne 0) { throw "Release audit self-tests failed ($LASTEXITCODE)." }
}
& dotnet $toolDll audit $candidateRoot $AllowlistPath (Join-Path $projectRoot 'docs/redistribution-policy.json') (Join-Path $projectRoot 'docs/local-dependencies.json') (Join-Path $reportRoot 'release-audit.json')
if ($LASTEXITCODE -ne 0) { throw "Release Candidate rejected by the gate ($LASTEXITCODE). See release-audit.json. No package was created." }
