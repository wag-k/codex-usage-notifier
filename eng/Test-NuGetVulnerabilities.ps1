[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$SolutionPath = "CodexUsageNotifier.sln"
)

$ErrorActionPreference = "Stop"
$outputPath = [System.IO.Path]::GetTempFileName()
$errorPath = [System.IO.Path]::GetTempFileName()

try {
    & dotnet list $SolutionPath package --vulnerable --include-transitive --format json 1> $outputPath 2> $errorPath
    if ($LASTEXITCODE -ne 0) {
        $safeError = Get-Content -LiteralPath $errorPath -Raw
        throw "NuGet vulnerability data could not be retrieved. ExitCode=$LASTEXITCODE`n$safeError"
    }

    try {
        $report = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "NuGet vulnerability output is not valid JSON."
    }

    if ($report.version -ne 1 -or $null -eq $report.projects -or @($report.projects).Count -eq 0) {
        throw "NuGet vulnerability report is missing required fields."
    }

    $vulnerablePackages = [System.Collections.Generic.List[object]]::new()
    foreach ($project in @($report.projects)) {
        foreach ($framework in @($project.frameworks)) {
            foreach ($collectionName in @("topLevelPackages", "transitivePackages")) {
                foreach ($package in @($framework.$collectionName)) {
                    if ($null -ne $package.vulnerabilities -and @($package.vulnerabilities).Count -gt 0) {
                        $vulnerablePackages.Add([pscustomobject]@{
                            Project = $project.path
                            Package = $package.id
                            Version = $package.resolvedVersion
                            Vulnerabilities = @($package.vulnerabilities).Count
                        })
                    }
                }
            }
        }
    }

    if ($vulnerablePackages.Count -gt 0) {
        $vulnerablePackages | Format-Table -AutoSize | Out-String | Write-Error
        throw "Known vulnerabilities were found in direct or transitive dependencies."
    }

    Write-Host "NuGet vulnerability check passed for direct and transitive dependencies."
}
finally {
    Remove-Item -LiteralPath $outputPath, $errorPath -Force -ErrorAction SilentlyContinue
}
