[CmdletBinding()]
param(
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$spikeRoot = $PSScriptRoot
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $spikeRoot '..\..\..'))
$generatorProject = Join-Path $spikeRoot 'Generator\Nanto.WinRtAppearanceAbiGen.csproj'
$appProject = Join-Path $spikeRoot 'App\Nanto.WinRtAppearanceAbiSpike.csproj'
$artifactRoot = Join-Path $repositoryRoot 'artifacts\size-spike\winrt-appearance'
$modes = @('SdkProjection', 'NarrowAbi')

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & dotnet @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $exitCode."
    }

    if ($output -match '(?i)(?:^|:)\s*warning\s+(?:IL|CS|CA|MSB|NETSDK)\d+') {
        throw "The Native AOT build emitted a compiler, analyzer, trimming, or linker warning."
    }
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory)][string]$InputDirectory,
        [Parameter(Mandatory)][string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            Get-ChildItem -LiteralPath $InputDirectory -File -Recurse |
                Sort-Object { [System.IO.Path]::GetRelativePath($InputDirectory, $_.FullName) } |
                ForEach-Object {
                    $relativePath = [System.IO.Path]::GetRelativePath($InputDirectory, $_.FullName).Replace('\', '/')
                    $entry = $archive.CreateEntry($relativePath, [System.IO.Compression.CompressionLevel]::Optimal)
                    $entry.LastWriteTime = [System.DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
                    $input = $_.OpenRead()
                    try {
                        $output = $entry.Open()
                        try {
                            $input.CopyTo($output)
                        }
                        finally {
                            $output.Dispose()
                        }
                    }
                    finally {
                        $input.Dispose()
                    }
                }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-PeSections {
    param([Parameter(Mandatory)][string]$ExecutablePath)

    Add-Type -AssemblyName System.Reflection.Metadata
    $stream = [System.IO.File]::OpenRead($ExecutablePath)
    try {
        $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            return @($reader.PEHeaders.SectionHeaders | ForEach-Object {
                [ordered]@{
                    name = $_.Name
                    virtualSize = $_.VirtualSize
                    rawSize = $_.SizeOfRawData
                    relativeVirtualAddress = $_.VirtualAddress
                    characteristics = $_.SectionCharacteristics.ToString()
                }
            })
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Read-SelfTestReport {
    param([Parameter(Mandatory)][string]$ReportPath)

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $ReportPath) {
        $separator = $line.IndexOf('=')
        if ($separator -le 0) {
            throw "Unexpected self-test report line '$line'."
        }

        $values[$line.Substring(0, $separator)] = $line.Substring($separator + 1)
    }

    return $values
}

if (-not $NoRestore) {
    Invoke-DotNet @('restore', $generatorProject)
}

Invoke-DotNet @('msbuild', $generatorProject, '-t:VerifyWinRtAppearanceAbi', '-p:Configuration=Release')

$results = @()
foreach ($mode in $modes) {
    $modeRoot = Join-Path $artifactRoot $mode
    if (Test-Path -LiteralPath $modeRoot) {
        Remove-Item -LiteralPath $modeRoot -Recurse -Force
    }

    if (-not $NoRestore) {
        foreach ($buildDirectory in @(
            (Join-Path $spikeRoot "App\obj\$mode"),
            (Join-Path $spikeRoot "App\bin\$mode"))) {
            if (Test-Path -LiteralPath $buildDirectory) {
                Remove-Item -LiteralPath $buildDirectory -Recurse -Force
            }
        }
    }

    $publishDirectory = Join-Path $modeRoot 'publish'
    $packageDirectory = Join-Path $modeRoot 'package'
    $reportPath = Join-Path $modeRoot 'self-test.txt'
    $userDataDirectory = Join-Path $modeRoot 'webview2-udf'
    New-Item -ItemType Directory -Path $publishDirectory, $packageDirectory | Out-Null

    $publishArguments = @(
        'publish', $appProject,
        '-c', 'Release',
        ("-p:AppearanceInteropMode={0}" -f $mode),
        '-p:IlcGenerateMapFile=true',
        '-o', $publishDirectory
    )
    if ($NoRestore) {
        $publishArguments += '--no-restore'
    }

    Invoke-DotNet $publishArguments

    $executablePath = Join-Path $publishDirectory 'Nanto.WinRtAppearanceAbiSpike.exe'
    $process = Start-Process -FilePath $executablePath -ArgumentList @($userDataDirectory, $reportPath) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        $detail = if (Test-Path -LiteralPath $reportPath) { Get-Content -LiteralPath $reportPath -Raw } else { 'No report was produced.' }
        throw "$mode self-test failed with exit code $($process.ExitCode). $detail"
    }

    $report = Read-SelfTestReport $reportPath
    $zipPath = Join-Path $packageDirectory 'Nanto.WinRtAppearanceAbiSpike.zip'
    New-DeterministicZip -InputDirectory $publishDirectory -DestinationPath $zipPath

    $mapPath = Join-Path $spikeRoot "App\obj\$mode\Release\net10.0-windows10.0.19041.0\win-x64\native\Nanto.WinRtAppearanceAbiSpike.map.xml"
    $mapText = Get-Content -LiteralPath $mapPath -Raw
    $winRtRuntimeSymbols = ([regex]::Matches($mapText, 'WinRT_Runtime_')).Count
    $uiSettingsSymbols = ([regex]::Matches($mapText, 'UISettings')).Count
    $executable = Get-Item -LiteralPath $executablePath
    $zip = Get-Item -LiteralPath $zipPath

    $results += [ordered]@{
        mode = $mode
        executable = [ordered]@{
            bytes = $executable.Length
            sha256 = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash
        }
        deterministicZip = [ordered]@{
            bytes = $zip.Length
            sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        }
        peSections = Get-PeSections $executablePath
        mappedSymbolCategories = [ordered]@{
            winRtRuntime = $winRtRuntimeSymbols
            uiSettings = $uiSettingsSymbols
        }
        winRtRuntimeReachable = $winRtRuntimeSymbols -gt 0
        behavior = [ordered]@{
            currentColorIsDark = [bool]::Parse($report['isDark'])
            appearanceCycles = [int]$report['appearanceCycles']
            syntheticCallbacks = $report['syntheticCallbacks']
            postDisposalSuppression = $report['postDisposalSuppression']
            repeatedDisposal = $report['repeatedDisposal']
            failureCleanup = $report['failureCleanup']
            webView = $report['webView']
        }
    }
}

$sdkResult = $results | Where-Object mode -eq 'SdkProjection'
$narrowResult = $results | Where-Object mode -eq 'NarrowAbi'
$saving = $sdkResult.executable.bytes - $narrowResult.executable.bytes
$behaviorMatches = $sdkResult.behavior.currentColorIsDark -eq $narrowResult.behavior.currentColorIsDark
$accepted = $behaviorMatches -and -not $narrowResult.winRtRuntimeReachable -and $saving -ge 1MB
$sdkVersion = (& dotnet --version).Trim()

$evidence = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    sdkVersion = $sdkVersion
    osVersion = [System.Environment]::OSVersion.VersionString
    architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    targetFramework = 'net10.0-windows10.0.19041.0'
    runtimeIdentifier = 'win-x64'
    targetingPackVersion = '10.0.19041.57'
    modes = $results
    comparison = [ordered]@{
        behaviorMatches = $behaviorMatches
        executableSavingBytes = $saving
        minimumSavingBytes = 1MB
        narrowWinRtRuntimeAbsent = -not $narrowResult.winRtRuntimeReachable
        accepted = $accepted
    }
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$evidencePath = Join-Path $artifactRoot 'evidence.json'
$evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM
Write-Host "Evidence: $evidencePath"
Write-Host "Executable saving: $saving bytes"
Write-Host "Decision: $(if ($accepted) { 'adopt' } else { 'retain SDK projection' })"

if (-not $accepted) {
    exit 1
}
