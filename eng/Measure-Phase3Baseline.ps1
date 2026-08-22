param(
    [int]$WarmIterations = 30,
    [string]$Project = "tests/Nanto.Hosting.Windows.TestApp/Nanto.Hosting.Windows.TestApp.csproj"
)

$ErrorActionPreference = "Stop"
if ($WarmIterations -lt 20) {
    throw "WarmIterations must be at least 20 to report p95."
}

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $repositoryRoot $Project
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "The configured project does not exist."
}

function Invoke-MeasuredBuild {
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $buildOutput = & dotnet build $projectPath --no-restore --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Baseline build failed with exit code $LASTEXITCODE. $($buildOutput -join ' ')"
    }

    $stopwatch.Stop()
    return $stopwatch.Elapsed.TotalMilliseconds
}

Push-Location $repositoryRoot
try {
    $restoreOutput = & dotnet restore $projectPath --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Baseline restore failed with exit code $LASTEXITCODE. $($restoreOutput -join ' ')"
    }

    $cleanOutput = & dotnet clean $projectPath --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Baseline clean failed with exit code $LASTEXITCODE. $($cleanOutput -join ' ')"
    }

    $cold = Invoke-MeasuredBuild
    $warm = 1..$WarmIterations | ForEach-Object { Invoke-MeasuredBuild }
    $ordered = @($warm | Sort-Object)
    $median = if ($ordered.Count % 2 -eq 0) {
        ($ordered[$ordered.Count / 2 - 1] + $ordered[$ordered.Count / 2]) / 2
    }
    else {
        $ordered[[math]::Floor($ordered.Count / 2)]
    }
    $p95Index = [math]::Ceiling(0.95 * $ordered.Count) - 1

    [ordered]@{
        schemaVersion = 1
        project = $Project.Replace("\", "/")
        buildMode = "CoreClrFrameworkDependent"
        coldMilliseconds = [math]::Round($cold, 1)
        warmIterations = $WarmIterations
        warmMedianMilliseconds = [math]::Round($median, 1)
        warmP95Milliseconds = [math]::Round($ordered[$p95Index], 1)
        failures = 0
        dotnetSdk = (& dotnet --version)
        operatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        processor = $env:PROCESSOR_IDENTIFIER
        powerMode = "record-manually"
        storage = "record-manually"
        node = if (Get-Command node -ErrorAction SilentlyContinue) { (& node --version) } else { "not-found" }
        npm = if (Get-Command npm -ErrorAction SilentlyContinue) { (& npm --version) } else { "not-found" }
    } | ConvertTo-Json
}
finally {
    Pop-Location
}
