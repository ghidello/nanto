[CmdletBinding()]
param(
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$spikeRoot = $PSScriptRoot
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $spikeRoot '..\..\..'))
$project = Join-Path $spikeRoot 'Nanto.RawWebView2AotMinimum.csproj'
$generatorProject = Join-Path $repositoryRoot 'eng\Nanto.WebView2InteropGen\Nanto.WebView2InteropGen.csproj'
$generatorAssembly = Join-Path $repositoryRoot 'eng\Nanto.WebView2InteropGen\bin\Release\net10.0\Nanto.WebView2InteropGen.dll'
$artifactRoot = Join-Path $repositoryRoot 'artifacts\size-spike\raw-webview2-aot-minimum'
$publishDirectory = Join-Path $artifactRoot 'publish'
$verificationDirectory = Join-Path $spikeRoot 'obj\interop-verification'
$userDataDirectory = Join-Path $artifactRoot 'webview2-udf'

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & dotnet @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $exitCode."
    }

    if ($output -match '(?i)(?:^|:)\s*warning\s+(?:IL|CS|CA|MSB|NETSDK)\d+') {
        throw 'The build emitted a compiler, analyzer, trimming, or linker warning.'
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

function Remove-UserDataDirectory {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if (-not (Test-Path -LiteralPath $userDataDirectory)) {
            return
        }

        try {
            Remove-Item -LiteralPath $userDataDirectory -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            Start-Sleep -Milliseconds 100
        }
    }

    throw 'The WebView2 user-data directory remained locked after process exit.'
}

if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}

if (-not $NoRestore) {
    foreach ($buildDirectory in @((Join-Path $spikeRoot 'obj'), (Join-Path $spikeRoot 'bin'))) {
        if (Test-Path -LiteralPath $buildDirectory) {
            Remove-Item -LiteralPath $buildDirectory -Recurse -Force
        }
    }

    Invoke-DotNet @('restore', $generatorProject)
    Invoke-DotNet @('restore', $project)
}

Invoke-DotNet @('build', $generatorProject, '-c', 'Release', '--no-restore')
New-Item -ItemType Directory -Path $verificationDirectory -Force | Out-Null
Invoke-DotNet @(
    $generatorAssembly,
    '--package-root', (dotnet msbuild $generatorProject -getProperty:PkgMicrosoft_Web_WebView2),
    '--spec', (Join-Path $spikeRoot 'webview2-interop-spec.json'),
    '--output', $verificationDirectory,
    '--manifest-spec-path', 'eng/spikes/Nanto.RawWebView2AotMinimum/webview2-interop-spec.json',
    '--manifest-output-path', 'eng/spikes/Nanto.RawWebView2AotMinimum/Generated/WebView2Interop.g.cs'
)

foreach ($fileName in @('WebView2Interop.g.cs', 'webview2-interop-manifest.json')) {
    $committedPath = Join-Path $spikeRoot "Generated\$fileName"
    $verifiedPath = Join-Path $verificationDirectory $fileName
    if ((Get-FileHash -LiteralPath $committedPath -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $verifiedPath -Algorithm SHA256).Hash) {
        throw "Generated file '$fileName' differs from its committed copy."
    }
}

New-Item -ItemType Directory -Path $publishDirectory | Out-Null
$publishArguments = @(
    'publish', $project,
    '-c', 'Release',
    '-p:IlcGenerateMapFile=true',
    '-o', $publishDirectory,
    '--no-restore'
)
Invoke-DotNet $publishArguments

$publishFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File)
if ($publishFiles.Count -ne 1 -or $publishFiles[0].Extension -ne '.exe') {
    throw 'The publish directory must contain exactly one executable and no other deployable files.'
}

$executablePath = $publishFiles[0].FullName
$process = Start-Process -FilePath $executablePath -ArgumentList @($userDataDirectory) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "The hidden WebView2 self-test failed with exit code $($process.ExitCode)."
}

Remove-UserDataDirectory

$packageDirectory = Join-Path $artifactRoot 'package'
New-Item -ItemType Directory -Path $packageDirectory | Out-Null
$zipPath = Join-Path $packageDirectory 'Nanto.RawWebView2AotMinimum.zip'
New-DeterministicZip -InputDirectory $publishDirectory -DestinationPath $zipPath

$mapPath = Join-Path $spikeRoot 'obj\Release\net10.0-windows10.0.19041.0\win-x64\native\Nanto.RawWebView2AotMinimum.map.xml'
$mapText = Get-Content -LiteralPath $mapPath -Raw
$productionMatches = ([regex]::Matches($mapText, 'Nanto_(?:Core|Hosting_Windows)')).Count
$executable = Get-Item -LiteralPath $executablePath
$zip = Get-Item -LiteralPath $zipPath
$evidence = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    sdkVersion = (& dotnet --version).Trim()
    osVersion = [System.Environment]::OSVersion.VersionString
    architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    targetFramework = 'net10.0-windows10.0.19041.0'
    runtimeIdentifier = 'win-x64'
    webView2PackageVersion = '1.0.4129.50'
    executable = [ordered]@{
        bytes = $executable.Length
        sha256 = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash
    }
    deterministicZip = [ordered]@{
        bytes = $zip.Length
        sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    }
    peSections = Get-PeSections $executablePath
    behavior = [ordered]@{
        hiddenWindowAndWebView = 'passed'
        navigationCompleted = 'passed'
        userDataDirectoryReleased = $true
    }
    productionAssemblyMapMatches = $productionMatches
    publishFileCount = $publishFiles.Count
    accepted = $productionMatches -eq 0
}

$evidencePath = Join-Path $artifactRoot 'evidence.json'
$evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM
Write-Host "Evidence: $evidencePath"
Write-Host "Executable: $($executable.Length) bytes"
Write-Host "Deterministic ZIP: $($zip.Length) bytes"

if (-not $evidence.accepted) {
    exit 1
}
