# SPDX-License-Identifier: MPL-2.0
param([string]$Destination = (Join-Path $PSScriptRoot '..\assets\models'))
$ErrorActionPreference = 'Stop'
$modelRef = 'c40f56f95412e10bcb9bd0bd24411e92f896d238'
$origin = "https://raw.githubusercontent.com/sml2h3/ddddocr/$modelRef"
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetFullPath($Destination)) | Out-Null
$modelPath = Join-Path $Destination 'common_old.onnx'
if (-not (Test-Path -LiteralPath $modelPath)) {
    Invoke-WebRequest -Uri "$origin/ddddocr/common_old.onnx" -OutFile $modelPath
}
if ((Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'b8f2ad9cbc1f2e3922a6cb9459e30824e7e2467f3fb4fd61420640e34ea0bf68') { throw 'Pinned OCR model SHA256 mismatch' }
$charsetSource = Invoke-RestMethod -Uri "$origin/ddddocr/models/charset_manager.py"
$charsetMatch = [regex]::Match($charsetSource, '(?s)def _get_old_charset.*?return\s*(\[.*?\])\s*\r?\n\s*def _get_beta_charset')
if (-not $charsetMatch.Success) { throw 'Cannot extract official old charset' }
$characters = $charsetMatch.Groups[1].Value | ConvertFrom-Json
if ($characters.Count -lt 1000 -or $characters[0] -ne '') { throw 'Invalid charset' }
$encoding = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $Destination 'charset.json'), (ConvertTo-Json -InputObject @($characters) -Depth 4 -Compress), $encoding)
$license = Invoke-RestMethod -Uri "$origin/LICENSE"
[System.IO.File]::WriteAllText((Join-Path $Destination 'LICENSE-ddddocr.txt'), $license, $encoding)
$manifest = [ordered]@{
    repository = 'https://github.com/sml2h3/ddddocr'
    commit = $modelRef
    model = 'common_old.onnx'
    modelSha256 = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant()
    charsetSha256 = (Get-FileHash -LiteralPath (Join-Path $Destination 'charset.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    charsetCount = $characters.Count
    license = 'MIT'
}
[System.IO.File]::WriteAllText((Join-Path $Destination 'manifest.json'), ($manifest | ConvertTo-Json), $encoding)
$manifest | ConvertTo-Json
