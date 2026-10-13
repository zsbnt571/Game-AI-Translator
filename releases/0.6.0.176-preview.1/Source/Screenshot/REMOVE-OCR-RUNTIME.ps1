[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$LabRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$RuntimeRoot = [IO.Path]::GetFullPath((Join-Path $LabRoot 'runtime'))
if (-not $RuntimeRoot.StartsWith($LabRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Safety check failed: runtime is outside the OCR lab directory.'
}

$LabModels = Join-Path $LabRoot 'models'
foreach ($name in @('paddlex-home','huggingface-home')) {
    $link = Join-Path $LabModels $name
    if (Test-Path -LiteralPath $link) {
        $item = Get-Item -LiteralPath $link -Force
        if ($item.LinkType -eq 'Junction') { Remove-Item -LiteralPath $link -Force }
    }
}

if (Test-Path -LiteralPath $RuntimeRoot) {
    if ($PSCmdlet.ShouldProcess($RuntimeRoot, 'Remove isolated OCR runtime')) {
        Remove-Item -LiteralPath $RuntimeRoot -Recurse -Force
        Write-Host "Removed isolated OCR runtime: $RuntimeRoot" -ForegroundColor Green
    }
} else {
    Write-Host 'No isolated OCR runtime exists.'
}

Write-Host 'Source, test images, reports, RC1, RC2 and production settings were not removed.'
