[CmdletBinding()]
param(
    [string]$ManifestPath,
    [string]$AppLockPath,
    [string]$TestLockPath,
    [string]$ProjectAssetsPath,
    [string]$PublishDirectory
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $PSScriptRoot "licenses-audit.json"
}
if ([string]::IsNullOrWhiteSpace($AppLockPath)) {
    $AppLockPath = Join-Path $repositoryRoot "src\CodexUsageNotifier\packages.lock.json"
}
if ([string]::IsNullOrWhiteSpace($TestLockPath)) {
    $TestLockPath = Join-Path $repositoryRoot "tests\CodexUsageNotifier.Tests\packages.lock.json"
}
if ([string]::IsNullOrWhiteSpace($ProjectAssetsPath)) {
    $ProjectAssetsPath = Join-Path $repositoryRoot "src\CodexUsageNotifier\obj\project.assets.json"
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required JSON file is missing: $Path"
    }
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "JSON file is invalid: $Path"
    }
}

function Get-LockPackages {
    param([Parameter(Mandatory = $true)][string]$Path)
    $lock = Read-JsonFile -Path $Path
    $frameworkProperty = @($lock.dependencies.PSObject.Properties)[0]
    if ($null -eq $frameworkProperty) {
        throw "Lock file has no target framework: $Path"
    }
    $packages = @{}
    foreach ($property in $frameworkProperty.Value.PSObject.Properties) {
        if ($property.Value.type -eq "Project") {
            continue
        }
        $packages[$property.Name.ToLowerInvariant()] = [pscustomobject]@{
            Id = $property.Name
            Version = [string]$property.Value.resolved
            DependencyType = [string]$property.Value.type
        }
    }
    return $packages
}

function Assert-LicenseAllowed {
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)]$Policy
    )
    $license = [string]$Entry.license
    if ([string]::IsNullOrWhiteSpace($license)) {
        throw "License is empty for $($Entry.id)."
    }
    foreach ($token in @($Policy.reviewRequiredTokens)) {
        if ($license.IndexOf([string]$token, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Review-required license '$license' was found for $($Entry.id)."
        }
    }
    if (@($Policy.allowedExpressions) -notcontains $license) {
        throw "License '$license' is not approved for $($Entry.id)."
    }
}

function Assert-PackageSet {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Actual,
        [Parameter(Mandatory = $true)][object[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Scope
    )
    $expectedMap = @{}
    foreach ($entry in $Expected) {
        $key = ([string]$entry.id).ToLowerInvariant()
        if ($expectedMap.ContainsKey($key)) {
            throw "Duplicate package in license manifest: $($entry.id)"
        }
        $expectedMap[$key] = $entry
    }
    foreach ($key in $Actual.Keys) {
        if (-not $expectedMap.ContainsKey($key)) {
            throw "$Scope package is missing from license manifest: $($Actual[$key].Id) $($Actual[$key].Version)"
        }
        $actualPackage = $Actual[$key]
        $expectedPackage = $expectedMap[$key]
        if ($actualPackage.Version -ne [string]$expectedPackage.version -or
            $actualPackage.DependencyType -ne [string]$expectedPackage.dependencyType) {
            throw "$Scope package metadata differs: $($actualPackage.Id) $($actualPackage.Version) $($actualPackage.DependencyType)"
        }
    }
    foreach ($key in $expectedMap.Keys) {
        if (-not $Actual.ContainsKey($key)) {
            throw "License manifest contains a stale $Scope package: $($expectedMap[$key].id)"
        }
    }
}

function Get-GlobalPackagesDirectory {
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        return [System.IO.Path]::GetFullPath($env:NUGET_PACKAGES)
    }
    $output = & dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0) {
        throw "NuGet global-packages directory could not be resolved."
    }
    $line = @($output | Where-Object { $_ -match '^global-packages:\s*' })[0]
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw "NuGet global-packages output was not recognized."
    }
    return [System.IO.Path]::GetFullPath(($line -replace '^global-packages:\s*', '').Trim())
}

function Get-PackageDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$GlobalPackagesDirectory,
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Version
    )
    $path = Join-Path $GlobalPackagesDirectory (Join-Path $Id.ToLowerInvariant() $Version)
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Restored package directory is missing: $Id $Version"
    }
    return $path
}

function Get-PackageLicenseMetadata {
    param([Parameter(Mandatory = $true)][string]$PackageDirectory)
    $nuspec = Get-ChildItem -LiteralPath $PackageDirectory -Filter "*.nuspec" -File | Select-Object -First 1
    if ($null -eq $nuspec) {
        throw "Package nuspec is missing: $PackageDirectory"
    }
    [xml]$document = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $document.package.metadata
    return [pscustomobject]@{
        Expression = ([string]$metadata.license.InnerText).Trim()
        LegacyUrl = ([string]$metadata.licenseUrl).Trim()
        Copyright = ([string]$metadata.copyright).Trim()
        ProjectUrl = ([string]$metadata.projectUrl).Trim()
    }
}

function Get-PackageNoticeFiles {
    param([Parameter(Mandatory = $true)][string]$PackageDirectory)
    return @(
        Get-ChildItem -LiteralPath $PackageDirectory -File | Where-Object {
            $_.Name -match '^(LICENSE|NOTICE|THIRD[-_ ]PARTY)'
        }
    )
}

function Copy-NoticeFiles {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo[]]$Files,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    if ($Files.Count -eq 0) {
        return
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($file in $Files) {
        if ($file.Length -le 0) {
            throw "License or notice file is empty: $($file.FullName)"
        }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Destination $file.Name) -Force
    }
}

$licensePath = Join-Path $repositoryRoot "LICENSE"
$thirdPartyPath = Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.txt"
foreach ($requiredFile in @($licensePath, $thirdPartyPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf) -or (Get-Item -LiteralPath $requiredFile).Length -le 0) {
        throw "Required repository license file is missing or empty: $requiredFile"
    }
}

$manifest = Read-JsonFile -Path $ManifestPath
if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported license audit manifest schema."
}
$appPackages = Get-LockPackages -Path $AppLockPath
$testPackages = Get-LockPackages -Path $TestLockPath
$testOnlyPackages = @{}
foreach ($key in $testPackages.Keys) {
    if (-not $appPackages.ContainsKey($key)) {
        $testOnlyPackages[$key] = $testPackages[$key]
    }
}

Assert-PackageSet -Actual $appPackages -Expected @($manifest.runtimePackages) -Scope "runtime"
Assert-PackageSet -Actual $testOnlyPackages -Expected @($manifest.buildAndTestOnlyPackages) -Scope "build/test"

$globalPackages = Get-GlobalPackagesDirectory
$allEntries = @($manifest.runtimePackages) + @($manifest.buildAndTestOnlyPackages)
foreach ($entry in $allEntries) {
    Assert-LicenseAllowed -Entry $entry -Policy $manifest.licenseMetadataPolicy
    if ($entry.distributedInRelease -ne ($manifest.runtimePackages -contains $entry)) {
        throw "Distributed flag is inconsistent for $($entry.id)."
    }
    $packageDirectory = Get-PackageDirectory -GlobalPackagesDirectory $globalPackages -Id $entry.id -Version $entry.version
    $metadata = Get-PackageLicenseMetadata -PackageDirectory $packageDirectory
    if ([string]::IsNullOrWhiteSpace($metadata.Expression)) {
        if ([string]::IsNullOrWhiteSpace([string]$entry.legacyLicenseUrl) -or
            $metadata.LegacyUrl -ne [string]$entry.legacyLicenseUrl) {
            throw "Package has no verified license expression: $($entry.id) $($entry.version)"
        }
    }
    elseif ($metadata.Expression -ne [string]$entry.license) {
        throw "NuGet license metadata differs for $($entry.id): $($metadata.Expression)"
    }
}

$assets = Read-JsonFile -Path $ProjectAssetsPath
$frameworkSection = @($assets.project.frameworks.PSObject.Properties)[0].Value
$frameworkReferenceNames = @($frameworkSection.frameworkReferences.PSObject.Properties.Name)
$downloadDependencies = @($frameworkSection.downloadDependencies)
$actualFrameworks = @{}
foreach ($frameworkName in $frameworkReferenceNames) {
    $runtimePackageId = "$frameworkName.Runtime.win-x64"
    $download = @($downloadDependencies | Where-Object { $_.name -eq $runtimePackageId })[0]
    if ($null -eq $download) {
        throw "Runtime pack version is missing from project.assets.json: $runtimePackageId"
    }
    $version = ([string]$download.version).Trim('[', ']').Split(',')[0].Trim()
    $actualFrameworks[$runtimePackageId.ToLowerInvariant()] = $version
}

$expectedFrameworks = @{}
foreach ($framework in @($manifest.runtimeFrameworks)) {
    Assert-LicenseAllowed -Entry $framework -Policy $manifest.licenseMetadataPolicy
    $expectedFrameworks[([string]$framework.id).ToLowerInvariant()] = $framework
}
foreach ($key in $actualFrameworks.Keys) {
    if (-not $expectedFrameworks.ContainsKey($key) -or
        $actualFrameworks[$key] -ne [string]$expectedFrameworks[$key].version) {
        throw "Runtime framework is missing or stale in license manifest: $key $($actualFrameworks[$key])"
    }
}
foreach ($key in $expectedFrameworks.Keys) {
    if (-not $actualFrameworks.ContainsKey($key)) {
        throw "License manifest contains a runtime framework that is not distributed: $($expectedFrameworks[$key].id)"
    }
}

if (-not [string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $publishPath = [System.IO.Path]::GetFullPath($PublishDirectory)
    if (-not (Test-Path -LiteralPath $publishPath -PathType Container)) {
        throw "Publish directory is missing: $publishPath"
    }
    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $publishPath "LICENSE") -Force
    Copy-Item -LiteralPath $thirdPartyPath -Destination (Join-Path $publishPath "THIRD-PARTY-NOTICES.txt") -Force
    Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $publishPath "licenses-audit.json") -Force

    foreach ($entry in @($manifest.runtimePackages)) {
        $packageDirectory = Get-PackageDirectory -GlobalPackagesDirectory $globalPackages -Id $entry.id -Version $entry.version
        $files = Get-PackageNoticeFiles -PackageDirectory $packageDirectory
        $destinationName = "$($entry.id)-$($entry.version)"
        Copy-NoticeFiles -Files $files -Destination (Join-Path $publishPath ("licenses\nuget\" + $destinationName))
    }
    foreach ($framework in @($manifest.runtimeFrameworks)) {
        $packageDirectory = Get-PackageDirectory -GlobalPackagesDirectory $globalPackages -Id $framework.id -Version $framework.version
        $licenseFile = Join-Path $packageDirectory ([string]$framework.requiredLicenseFile)
        if (-not (Test-Path -LiteralPath $licenseFile -PathType Leaf)) {
            throw "Runtime license file is missing: $licenseFile"
        }
        $files = @((Get-Item -LiteralPath $licenseFile))
        if (-not [string]::IsNullOrWhiteSpace([string]$framework.requiredNoticeFile)) {
            $noticeFile = Join-Path $packageDirectory ([string]$framework.requiredNoticeFile)
            if (-not (Test-Path -LiteralPath $noticeFile -PathType Leaf)) {
                throw "Runtime third-party notice is missing: $noticeFile"
            }
            $files += Get-Item -LiteralPath $noticeFile
        }
        $destinationName = "$($framework.id)-$($framework.version)"
        Copy-NoticeFiles -Files $files -Destination (Join-Path $publishPath ("licenses\dotnet\" + $destinationName))
    }

    foreach ($requiredName in @("LICENSE", "THIRD-PARTY-NOTICES.txt", "licenses-audit.json")) {
        $requiredPath = Join-Path $publishPath $requiredName
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf) -or (Get-Item -LiteralPath $requiredPath).Length -le 0) {
            throw "Publish license file is missing or empty: $requiredName"
        }
    }
}

Write-Host "License audit passed: $($manifest.runtimePackages.Count) runtime packages, $($manifest.buildAndTestOnlyPackages.Count) build/test packages, $($manifest.runtimeFrameworks.Count) runtime frameworks."
