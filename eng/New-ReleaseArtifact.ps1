[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Version must use MAJOR.MINOR.PATCH format."
}

$resolvedPublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$resolvedReleaseDirectory = [System.IO.Path]::GetFullPath($ReleaseDirectory)
$licenseAuditScript = Join-Path $PSScriptRoot "Audit-Licenses.ps1"
& $licenseAuditScript -PublishDirectory $resolvedPublishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "License audit failed before Release packaging."
}
$executablePath = Join-Path $resolvedPublishDirectory "CodexUsageNotifier.exe"
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "CodexUsageNotifier.exe is missing from publish output."
}

$pdbFiles = @(Get-ChildItem -LiteralPath $resolvedPublishDirectory -Recurse -File -Filter "*.pdb")
if ($pdbFiles.Count -gt 0) {
    $pdbFiles | Remove-Item -Force
}

$forbiddenNames = @(
    "settings.json",
    "state.json",
    "usage-history.jsonl",
    "google-oauth-client.json",
    "google-oauth-credentials.dat",
    ".gitignore"
)
$forbiddenExtensions = @(".log", ".pdb", ".cs", ".xaml", ".csproj", ".sln")
$publishPrefixLength = $resolvedPublishDirectory.TrimEnd('\').Length
$forbiddenEntries = @(
    Get-ChildItem -LiteralPath $resolvedPublishDirectory -Recurse -Force | Where-Object {
        $relative = $_.FullName.Substring($publishPrefixLength).TrimStart('\').Replace('\', '/')
        $forbiddenNames -contains $_.Name -or
        $forbiddenExtensions -contains $_.Extension -or
        $relative -match '(^|/)(\.git|tests|obj)(/|$)'
    }
)
if ($forbiddenEntries.Count -gt 0) {
    throw "Publish output contains forbidden files: $($forbiddenEntries.FullName -join ', ')"
}

$versionInfo = (Get-Item -LiteralPath $executablePath).VersionInfo
$productVersion = ($versionInfo.ProductVersion -split '\+', 2)[0]
$fileVersion = ($versionInfo.FileVersion -split '\+', 2)[0]
if ($productVersion -ne $Version -or -not $fileVersion.StartsWith("$Version.", [System.StringComparison]::Ordinal)) {
    throw "Executable version does not match. ProductVersion=$($versionInfo.ProductVersion), FileVersion=$($versionInfo.FileVersion)"
}

New-Item -ItemType Directory -Path $resolvedReleaseDirectory -Force | Out-Null
$baseName = "CodexUsageNotifier-v$Version-win-x64"
$zipPath = Join-Path $resolvedReleaseDirectory "$baseName.zip"
$hashPath = "$zipPath.sha256"
Remove-Item -LiteralPath $zipPath, $hashPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $resolvedPublishDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal

if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or (Get-Item -LiteralPath $zipPath).Length -le 0) {
    throw "Release ZIP was not generated."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    if (-not ($entryNames -contains "CodexUsageNotifier.exe")) {
        throw "CodexUsageNotifier.exe is missing from Release ZIP."
    }

    foreach ($requiredLicenseEntry in @("LICENSE", "THIRD-PARTY-NOTICES.txt", "licenses-audit.json")) {
        $entry = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq $requiredLicenseEntry })[0]
        if ($null -eq $entry -or $entry.Length -le 0) {
            throw "Required license file is missing or empty in Release ZIP: $requiredLicenseEntry"
        }
    }
    if (-not ($entryNames | Where-Object { $_ -like "licenses/dotnet/*" })) {
        throw "The .NET runtime license files are missing from Release ZIP."
    }

    $forbiddenZipEntries = @($entryNames | Where-Object {
        $leaf = [System.IO.Path]::GetFileName($_)
        $extension = [System.IO.Path]::GetExtension($_)
        $forbiddenNames -contains $leaf -or
        $forbiddenExtensions -contains $extension -or
        $_ -match '(^|/)(\.git|tests|obj)(/|$)'
    })
    if ($forbiddenZipEntries.Count -gt 0) {
        throw "Release ZIP contains forbidden files: $($forbiddenZipEntries -join ', ')"
    }
}
finally {
    $archive.Dispose()
}

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([System.IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath $hashPath -Encoding ascii -NoNewline
$recordedHash = ((Get-Content -LiteralPath $hashPath -Raw) -split '\s+', 2)[0]
$verifiedHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($recordedHash -ne $verifiedHash) {
    throw "SHA-256 verification failed."
}

Write-Host "Release artifact verification passed: $zipPath"
Write-Host "ProductVersion=$($versionInfo.ProductVersion), FileVersion=$($versionInfo.FileVersion)"
Write-Host "SHA256=$verifiedHash"
