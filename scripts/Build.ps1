# SPDX-License-Identifier: MPL-2.0
param([switch]$Test, [string]$SampleScreenshot = '', [string]$OutputRoot = '')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\EasLoginHelper\EasLoginHelper.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid project release version' }
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = "artifacts\releases\v$version" }
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputRoot)).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
if ($releaseRoot -ne $artifactsRoot -and -not $releaseRoot.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '-OutputRoot must be inside the project artifacts directory'
}
[System.IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$sdkDirectory = Join-Path $projectRoot '.tools\dotnet'
$dotnet = Join-Path $sdkDirectory 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    [System.IO.Directory]::CreateDirectory($sdkDirectory) | Out-Null
    $archive = Join-Path $projectRoot '.tools\dotnet-sdk-10.0.404-win-x64.zip'
    Invoke-WebRequest -Uri 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.404/dotnet-sdk-10.0.404-win-x64.zip' -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash.ToLowerInvariant() -ne '330ddd2665b43cd2e428216a0d8ea1ac7bb19b5434fdc530e62010dc3b52823a620bf95489d628e1ed6f5122401793ee4887cca4a019122361a1231497f632ee') { throw 'SDK SHA512 mismatch' }
    Expand-Archive -LiteralPath $archive -DestinationPath $sdkDirectory -Force
}
$env:DOTNET_ROOT = $sdkDirectory
$modelDirectory = Join-Path $projectRoot 'assets\models'
if (-not (Test-Path -LiteralPath (Join-Path $modelDirectory 'manifest.json'))) { & (Join-Path $PSScriptRoot 'Get-OcrAssets.ps1') }
$manifest = Get-Content -LiteralPath (Join-Path $modelDirectory 'manifest.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath (Join-Path $modelDirectory 'common_old.onnx') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.modelSha256 -or
    (Get-FileHash -LiteralPath (Join-Path $modelDirectory 'charset.json') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.charsetSha256) { throw 'OCR asset integrity check failed' }
Push-Location $projectRoot
try {
    & $dotnet restore '.\src\EasLoginHelper\EasLoginHelper.csproj' --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
    if ($Test) {
        if ($SampleScreenshot -and -not (Test-Path -LiteralPath $SampleScreenshot -PathType Leaf)) { throw '-SampleScreenshot must point to an existing local screenshot' }
        & $dotnet restore '.\tests\EasLoginHelper.Tests\EasLoginHelper.Tests.csproj' --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Test restore failed' }
        & $dotnet build '.\tests\EasLoginHelper.Tests\EasLoginHelper.Tests.csproj' -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
        $testArguments = @('--output', (Join-Path $releaseRoot 'validation'))
        if ($SampleScreenshot) { $testArguments += @('--sample', $SampleScreenshot) }
        & $dotnet '.\tests\EasLoginHelper.Tests\bin\Release\net10.0-windows10.0.19041.0\EasLoginHelper.Tests.dll' @testArguments
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    $auditOutput = & $dotnet list '.\src\EasLoginHelper\EasLoginHelper.csproj' package --vulnerable --include-transitive --no-restore --format json
    if ($LASTEXITCODE -ne 0) { throw 'Dependency audit failed' }
    $auditText = $auditOutput -join [Environment]::NewLine
    $audit = $auditText | ConvertFrom-Json
    [System.IO.File]::WriteAllText((Join-Path $releaseRoot 'dependency-audit.json'), $auditText, [System.Text.UTF8Encoding]::new($false))
    $vulnerablePackages = @($audit.projects.frameworks | ForEach-Object { @($_.topLevelPackages) + @($_.transitivePackages) } | Where-Object { $_.vulnerabilities.Count -gt 0 })
    if ($vulnerablePackages.Count -gt 0) { throw 'Dependency audit found vulnerable packages; release stopped' }
    Write-Output 'Dependency audit: no known vulnerable packages'
    $publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot 'EasLoginHelper-win-x64'))
    if (-not $publishDirectory.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish cleanup target is outside artifacts' }
    $ancestor = [System.IO.DirectoryInfo]$publishDirectory
    while ($null -ne $ancestor -and $ancestor.FullName -ne $projectRoot) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { throw 'Publish output must not use directory links' }
        $ancestor = $ancestor.Parent
    }
    if (Test-Path -LiteralPath $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
    & $dotnet publish '.\src\EasLoginHelper\EasLoginHelper.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None --no-restore -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $sourceCommit = 'uncommitted'
    $sourceWorkingTreeClean = $false
    if (Test-Path -LiteralPath (Join-Path $projectRoot '.git')) {
        $sourceCommit = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source commit' }
        $sourceStatus = @(& git status --porcelain)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source status' }
        $sourceWorkingTreeClean = $sourceStatus.Count -eq 0
    }
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $sourceNotice = "This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.`nA copy of the license is included in LICENSE.`nSource: https://github.com/Xymircai/RsmAutologin/tree/v$version`nBuild commit: $sourceCommit`nThird-party components retain their own licenses; see THIRD-PARTY-NOTICES.md.`n"
    [System.IO.File]::WriteAllText((Join-Path $publishDirectory 'SOURCE.txt'), $sourceNotice, $encoding)
    $buildInfo = [ordered]@{ Version = $version; SourceCommit = $sourceCommit; SourceWorkingTreeClean = $sourceWorkingTreeClean; RuntimeIdentifier = 'win-x64'; SdkVersion = (& $dotnet --version).Trim() }
    [System.IO.File]::WriteAllText((Join-Path $publishDirectory 'BUILD-INFO.json'), ($buildInfo | ConvertTo-Json), $encoding)
    # The distributable has no required sidecars. Static import libraries are build-time material only.
    Get-ChildItem -LiteralPath $publishDirectory -File -Filter '*.lib' | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    $executable = Join-Path $publishDirectory 'EasLoginHelper.exe'
    $distribution = Join-Path $releaseRoot 'EasLoginHelper.exe'
    Copy-Item -LiteralPath $executable -Destination $distribution -Force
    $exeSum = (Get-FileHash -LiteralPath $distribution -Algorithm SHA256).Hash.ToLowerInvariant() + '  EasLoginHelper.exe' + "`n"
    [System.IO.File]::WriteAllText((Join-Path $releaseRoot 'SHA256SUMS.txt'), $exeSum, $encoding)
    Get-Item -LiteralPath $distribution | Select-Object FullName,Length
}
finally { Pop-Location }