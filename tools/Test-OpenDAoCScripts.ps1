# SPDX-License-Identifier: GPL-3.0-only
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Container })]
    [string]$ReleasePath,

    [string]$DotNetPath = "dotnet",

    [string]$OutputPath = "",

    [ValidateScript({ $_ -eq 0 -or $_ -ge 6 })]
    [int]$TargetFrameworkMajor = 0
)

$ErrorActionPreference = "Stop"
$release = (Resolve-Path $ReleasePath).Path
$scripts = Join-Path $release "scripts"
$lib = Join-Path $release "lib"

if (-not (Test-Path $scripts -PathType Container)) {
    throw "OpenDAoC scripts directory not found: $scripts"
}

if (-not (Test-Path $lib -PathType Container)) {
    throw "OpenDAoC library directory not found: $lib"
}

$sdkLines = @(& $DotNetPath --list-sdks)
if ($LASTEXITCODE -ne 0 -or $sdkLines.Count -eq 0) {
    throw "No .NET SDK was found. Install the SDK used to build OpenDAoC first."
}

$installedSdks = foreach ($line in $sdkLines) {
    if ($line -match '^([^ ]+) \[(.+)\]$') {
        $plainVersion = $Matches[1].Split('-')[0]
        [pscustomobject]@{
            Version = [version]$plainVersion
            Name = $Matches[1]
            Root = $Matches[2]
        }
    }
}

if ($TargetFrameworkMajor -eq 0) {
    $runtimeConfigs = @(Get-ChildItem $release -Filter "*.runtimeconfig.json" -File |
        Sort-Object { if ($_.Name -ieq "CoreServer.runtimeconfig.json") { 0 } else { 1 } })

    foreach ($runtimeConfig in $runtimeConfigs) {
        try {
            $runtimeData = Get-Content $runtimeConfig.FullName -Raw | ConvertFrom-Json
            $tfm = [string]$runtimeData.runtimeOptions.tfm
            if ($tfm -match '^net([0-9]+)(?:\.[0-9]+)?$') {
                $TargetFrameworkMajor = [int]$Matches[1]
                break
            }
        }
        catch {
            Write-Verbose "Ignoring unreadable runtime config: $($runtimeConfig.FullName)"
        }
    }
}

if ($TargetFrameworkMajor -eq 0) {
    throw "Could not detect the OpenDAoC target framework. Pass -TargetFrameworkMajor explicitly."
}

$sdk = $installedSdks |
    Where-Object { $_.Version.Major -eq $TargetFrameworkMajor } |
    Sort-Object Version -Descending |
    Select-Object -First 1
if ($null -eq $sdk) {
    throw ".NET SDK $TargetFrameworkMajor.x is required because this OpenDAoC release targets net$TargetFrameworkMajor.0."
}

$compiler = Join-Path (Join-Path (Join-Path (Join-Path $sdk.Root $sdk.Name) "Roslyn") "bincore") "csc.dll"
if (-not (Test-Path $compiler -PathType Leaf)) {
    throw "Roslyn compiler not found: $compiler"
}

$dotnetRoot = Split-Path $sdk.Root -Parent
$referencePackRoot = Join-Path (Join-Path $dotnetRoot "packs") "Microsoft.NETCore.App.Ref"
$targetMajor = $TargetFrameworkMajor
$referencePack = Get-ChildItem $referencePackRoot -Directory |
    Where-Object { $_.Name -like "$targetMajor.*" } |
    Sort-Object { [version]($_.Name.Split('-')[0]) } -Descending |
    Select-Object -First 1

if ($null -eq $referencePack) {
    throw ".NET $targetMajor reference pack not found below: $referencePackRoot"
}

$frameworkReferences = Join-Path (Join-Path $referencePack.FullName "ref") "net$targetMajor.0"
if (-not (Test-Path $frameworkReferences -PathType Container)) {
    throw "Reference assemblies not found: $frameworkReferences"
}

$references = @(
    Get-ChildItem $frameworkReferences -Filter "*.dll" -File
    Get-ChildItem $release -Filter "*.dll" -File |
        Where-Object { $_.Name -notlike "System.*.dll" }
    Get-ChildItem $lib -Filter "*.dll" -File |
        Where-Object {
            $_.Name -notlike "System.*.dll" -and
            $_.Name -ne "GameServerScripts.dll"
        }
) | Group-Object Name | ForEach-Object {
    $_.Group | Sort-Object { $_.DirectoryName.Length } | Select-Object -First 1
}

$sourceFiles = @(Get-ChildItem $scripts -Recurse -Filter "*.cs" -File |
    Where-Object { $_.Name -ne "AssemblyInfo.cs" })

if ($sourceFiles.Count -eq 0) {
    throw "No C# scripts found below: $scripts"
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "aldhran-script-check-" + [guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$responseFile = Join-Path $temporaryRoot "compile.rsp"
$outputFile = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    Join-Path $temporaryRoot "GameServerScripts.dll"
}
else {
    [System.IO.Path]::GetFullPath($OutputPath)
}

try {
    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add("/nologo")
    $arguments.Add("/nostdlib+")
    $arguments.Add("/target:library")
    $arguments.Add("/langversion:latest")
    # OpenDAoC's stock script tree emits thousands of warnings. Suppress them
    # here so the hidden CSxxxx errors remain immediately visible.
    $arguments.Add("/warn:0")
    $arguments.Add('/out:"' + $outputFile + '"')

    foreach ($reference in $references) {
        $arguments.Add('/reference:"' + $reference.FullName + '"')
    }

    foreach ($sourceFile in $sourceFiles) {
        $arguments.Add('"' + $sourceFile.FullName + '"')
    }

    $utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllLines($responseFile, $arguments, $utf8WithoutBom)

    Write-Host "Compiling $($sourceFiles.Count) scripts against $($references.Count) assemblies..."
    & $DotNetPath $compiler "@$responseFile"
    $compilerExitCode = $LASTEXITCODE

    if ($compilerExitCode -eq 0) {
        Write-Host "Script compilation succeeded." -ForegroundColor Green
        if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
            Write-Host "Precompiled script assembly written to: $outputFile" -ForegroundColor Green
        }
    }
    else {
        Write-Host "Script compilation failed. The CSxxxx messages above identify the real source file and line." -ForegroundColor Red
    }
}
finally {
    Remove-Item $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}

exit $compilerExitCode
