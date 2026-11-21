# SPDX-License-Identifier: MPL-2.0
param([string]$OutputRoot = '', [string]$DotnetPath = '')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$projectFile = Join-Path $projectRoot 'src\EasLoginHelper\EasLoginHelper.csproj'
$project = [xml](Get-Content -LiteralPath $projectFile -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if (-not $OutputRoot) { $OutputRoot = 'artifacts\EasLoginHelper-v' + $version + '-win-x64' }
$outputDirectory = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputRoot))
if (-not $outputDirectory.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '-OutputRoot must be below the project artifacts directory' }
$tempParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$scratchRoot = [System.IO.Path]::GetFullPath((Join-Path $tempParent ('RsmAutologin-build-' + [Guid]::NewGuid().ToString('N'))))
[System.IO.Directory]::CreateDirectory($scratchRoot) | Out-Null
$names = @('DOTNET_ROOT','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_CLI_USE_MSBUILD_SERVER','MSBUILDDISABLENODEREUSE','RsmAutologinBuildArtifacts')
$savedEnvironment = @{}
foreach ($name in $names) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
Push-Location $projectRoot
try {
    $dotnet = $DotnetPath
    if (-not $dotnet) {
        $installed = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($installed) {
            $installedSdks = @(& $installed.Source --list-sdks)
            if ($LASTEXITCODE -eq 0 -and ($installedSdks -match '^10\.0\.404\s')) { $dotnet = $installed.Source }
        }
    }
    if (-not $dotnet) {
        $sdkDirectory = Join-Path $scratchRoot 'sdk'
        $archive = Join-Path $scratchRoot 'sdk.zip'
        Invoke-WebRequest -Uri 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.404/dotnet-sdk-10.0.404-win-x64.zip' -OutFile $archive
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash.ToLowerInvariant() -ne '330ddd2665b43cd2e428216a0d8ea1ac7bb19b5434fdc530e62010dc3b52823a620bf95489d628e1ed6f5122401793ee4887cca4a019122361a1231497f632ee') { throw 'SDK SHA512 mismatch' }
        Expand-Archive -LiteralPath $archive -DestinationPath $sdkDirectory
        Remove-Item -LiteralPath $archive -Force
        $dotnet = Join-Path $sdkDirectory 'dotnet.exe'
    }
    $dotnet = [System.IO.Path]::GetFullPath($dotnet)
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw 'dotnet executable not found' }
    $env:DOTNET_ROOT = [System.IO.Path]::GetDirectoryName($dotnet)
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'; $env:MSBUILDDISABLENODEREUSE = '1'
    $env:RsmAutologinBuildArtifacts = Join-Path $scratchRoot 'build'
    $models = Join-Path $projectRoot 'assets\models'
    if (-not (Test-Path -LiteralPath (Join-Path $models 'manifest.json'))) { & (Join-Path $PSScriptRoot 'Get-OcrAssets.ps1') }
    $manifest = Get-Content -LiteralPath (Join-Path $models 'manifest.json') -Raw | ConvertFrom-Json
    if ((Get-FileHash -LiteralPath (Join-Path $models 'common_old.onnx') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.modelSha256 -or
        (Get-FileHash -LiteralPath (Join-Path $models 'charset.json') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.charsetSha256) { throw 'OCR integrity check failed' }
    & $dotnet restore $projectFile --locked-mode -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
    $auditOutput = & $dotnet list $projectFile package --vulnerable --include-transitive --no-restore --format json
    if ($LASTEXITCODE -ne 0) { throw 'Dependency audit failed' }
    $audit = ($auditOutput -join [Environment]::NewLine) | ConvertFrom-Json
    $vulnerable = @($audit.projects.frameworks | ForEach-Object { @($_.topLevelPackages) + @($_.transitivePackages) } | Where-Object { $_.vulnerabilities.Count -gt 0 })
    if ($vulnerable.Count) { throw 'Known vulnerable dependencies found' }
    $publish = Join-Path $scratchRoot 'publish'
    & $dotnet publish $projectFile -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:UseSharedCompilation=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    $executable = Join-Path $outputDirectory 'EasLoginHelper.exe'
    Copy-Item -LiteralPath (Join-Path $publish 'EasLoginHelper.exe') -Destination $executable -Force
    $extensionSource = Join-Path $projectRoot 'src\BrowserExtension'
    $extensionDestination = Join-Path $outputDirectory 'BrowserExtension'
    [System.IO.Directory]::CreateDirectory($extensionDestination) | Out-Null
    foreach ($file in @('manifest.json','sites.js','login-form.js','background.js','content.js')) {
        Copy-Item -LiteralPath (Join-Path $extensionSource $file) -Destination (Join-Path $extensionDestination $file) -Force
    }
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $checksum = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText((Join-Path $outputDirectory 'SHA256SUMS.txt'), ($checksum + '  EasLoginHelper.exe' + "`n"), $encoding)
    $sourceCommit = 'uncommitted'; $clean = $false
    if (Test-Path -LiteralPath (Join-Path $projectRoot '.git')) {
        $sourceCommit = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Cannot read source commit' }
        $clean = @(& git status --porcelain).Count -eq 0
    }
    $info = [ordered]@{Version=$version;SourceCommit=$sourceCommit;SourceWorkingTreeClean=$clean;RuntimeIdentifier='win-x64';SdkVersion=(& $dotnet --version).Trim();ExeSHA256=$checksum}
    [System.IO.File]::WriteAllText((Join-Path $outputDirectory 'BUILD-INFO.json'), ($info | ConvertTo-Json), $encoding)
    Get-Item -LiteralPath $executable | Select-Object FullName,Length
}
finally {
    Pop-Location
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process') }
    $safeScratch = [System.IO.Path]::GetFullPath($scratchRoot)
    if (-not $safeScratch.StartsWith($tempParent + [System.IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($safeScratch) -notmatch '^RsmAutologin-build-[a-f0-9]{32}$') { throw 'Temporary cleanup target rejected' }
    if (Test-Path -LiteralPath $safeScratch) { Remove-Item -LiteralPath $safeScratch -Recurse -Force }
}
