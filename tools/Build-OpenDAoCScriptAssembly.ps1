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

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $configuredTarget = ""
    $serverConfigCandidates = @(
        (Join-Path (Join-Path $release "config") "serverconfig.xml"),
        (Join-Path $release "serverconfig.xml")
    )

    foreach ($serverConfig in $serverConfigCandidates) {
        if (-not (Test-Path $serverConfig -PathType Leaf)) {
            continue
        }

        try {
            [xml]$serverConfigXml = Get-Content $serverConfig -Raw
            $configuredTarget = [string]$serverConfigXml.root.Server.ScriptCompilationTarget
        }
        catch {
            Write-Verbose "Ignoring unreadable server config: $serverConfig"
        }

        if (-not [string]::IsNullOrWhiteSpace($configuredTarget)) {
            break
        }
    }

    $OutputPath = if ([string]::IsNullOrWhiteSpace($configuredTarget)) {
        Join-Path $lib "GameServerScripts.dll"
    }
    elseif ([System.IO.Path]::IsPathRooted($configuredTarget)) {
        $configuredTarget
    }
    else {
        Join-Path $release $configuredTarget
    }
}

if (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $release $OutputPath
}
$outputFile = [System.IO.Path]::GetFullPath($OutputPath)
$outputName = [System.IO.Path]::GetFileName($outputFile)
$outputDirectory = Split-Path $outputFile -Parent
if (-not (Test-Path $outputDirectory -PathType Container)) {
    throw "Script compilation target directory not found: $outputDirectory"
}
$cacheFile = "$outputFile.xml"

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
        Where-Object {
            $_.Name -notlike "System.*.dll" -and
            $_.Name -ne $outputName
        }
    Get-ChildItem $lib -Filter "*.dll" -File |
        Where-Object {
            $_.Name -notlike "System.*.dll" -and
            $_.Name -ne $outputName
        }
) | Group-Object Name | ForEach-Object {
    $_.Group | Sort-Object { $_.DirectoryName.Length } | Select-Object -First 1
}

$allSourceFiles = @(Get-ChildItem $scripts -Recurse -Filter "*.cs" -File)
$sourceFiles = @($allSourceFiles |
    Where-Object { $_.Name -ne "AssemblyInfo.cs" })

if ($sourceFiles.Count -eq 0) {
    throw "No C# scripts found below: $scripts"
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "aldhran-script-build-" + [guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$responseFile = Join-Path $temporaryRoot "compile.rsp"
$temporaryOutput = Join-Path $temporaryRoot "GameServerScripts.dll"

try {
    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add("/nologo")
    $arguments.Add("/nostdlib+")
    $arguments.Add("/target:library")
    $arguments.Add("/langversion:latest")
    $arguments.Add("/warn:0")
    $arguments.Add('/out:"' + $temporaryOutput + '"')

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
    if ($LASTEXITCODE -ne 0) {
        throw "Script compilation failed. The CSxxxx messages above identify the source file and line."
    }

    Copy-Item $temporaryOutput $outputFile -Force

    $xmlSettings = [System.Xml.XmlWriterSettings]::new()
    $xmlSettings.Indent = $true
    $xmlSettings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $temporaryCache = Join-Path $temporaryRoot "GameServerScripts.dll.xml"
    $writer = [System.Xml.XmlWriter]::Create($temporaryCache, $xmlSettings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement("root")
        foreach ($sourceFile in $allSourceFiles) {
            $writer.WriteStartElement("param")
            $writer.WriteAttributeString("name", $sourceFile.FullName)
            $writer.WriteElementString("size", [string]$sourceFile.Length)
            $writer.WriteElementString("lastmodified", [string]$sourceFile.LastWriteTime.ToFileTime())
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally {
        $writer.Dispose()
    }
    Copy-Item $temporaryCache $cacheFile -Force

    Write-Host "Script compilation succeeded." -ForegroundColor Green
    Write-Host "Precompiled assembly written to: $outputFile" -ForegroundColor Green
    Write-Host "OpenDAoC script cache written to: $cacheFile" -ForegroundColor Green
    Write-Host "EnableCompilation may remain True while the scripts are unchanged. Rerun this builder after every script change." -ForegroundColor Yellow
}
finally {
    Remove-Item $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
