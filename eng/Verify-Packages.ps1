[CmdletBinding()]
param(
    [Parameter()]
    [string] $PackageDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages'),

    [Parameter()]
    [string] $ExpectedRepositoryCommit
)

$ErrorActionPreference = 'Stop'

$expectedPackageIds = @(
    'AgentForge.NET'
    'AgentForge.Abstractions'
    'AgentForge.Core'
    'AgentForge.Hosting'
    'AgentForge.Tools'
    'AgentForge.Providers.OpenAI'
    'AgentForge.Adapters.MicrosoftExtensionsAI'
)
$expectedSymbolPackageIds = @($expectedPackageIds | Where-Object { $_ -ne 'AgentForge.NET' })
$expectedVersion = '0.3.0-preview.1'
$errors = [System.Collections.Generic.List[string]]::new()

$resolvedPackageDirectory = [System.IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $resolvedPackageDirectory -PathType Container)) {
    throw "Package directory does not exist: $resolvedPackageDirectory"
}

$packages = @(Get-ChildItem -LiteralPath $resolvedPackageDirectory -Filter '*.nupkg' -File |
    Where-Object { -not $_.Name.EndsWith('.snupkg', [StringComparison]::OrdinalIgnoreCase) })
$symbols = @(Get-ChildItem -LiteralPath $resolvedPackageDirectory -Filter '*.snupkg' -File)
$expectedPackageNames = @($expectedPackageIds | ForEach-Object { "$($_).$expectedVersion.nupkg" })
$expectedSymbolNames = @($expectedSymbolPackageIds | ForEach-Object { "$($_).$expectedVersion.snupkg" })

$actualPackageNames = @($packages.Name | Sort-Object)
$actualSymbolNames = @($symbols.Name | Sort-Object)
if (Compare-Object ($expectedPackageNames | Sort-Object) $actualPackageNames) {
    $errors.Add("Expected exactly these library packages: $($expectedPackageNames -join ', '). Actual: $($actualPackageNames -join ', ').")
}
if (Compare-Object ($expectedSymbolNames | Sort-Object) $actualSymbolNames) {
    $errors.Add("Expected exactly these symbol packages: $($expectedSymbolNames -join ', '). Actual: $($actualSymbolNames -join ', ').")
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($package in $packages) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $nuspecEntry = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($nuspecEntry.Count -ne 1) {
            $errors.Add("$($package.Name): expected one nuspec.")
            continue
        }

        $reader = [System.IO.StreamReader]::new($nuspecEntry[0].Open())
        try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        $id = [string] $metadata.id
        if ($expectedPackageIds -notcontains $id) { $errors.Add("$($package.Name): unexpected package ID '$id'.") }
        if ([string] $metadata.version -ne $expectedVersion) { $errors.Add("${id}: wrong version '$($metadata.version)'.") }
        if ([string] $metadata.authors -ne 'Subhajit Labs') { $errors.Add("${id}: authors metadata is incorrect.") }
        if ([string] $metadata.copyright -ne 'Copyright (c) 2026 Subhajit Labs') {
            $errors.Add("${id}: copyright metadata is incorrect.")
        }
        if ([string]::IsNullOrWhiteSpace([string] $metadata.description) -or [string] $metadata.description -eq 'Package Description') {
            $errors.Add("${id}: package description is missing or placeholder text.")
        }
        if ([string] $metadata.readme -ne 'README.md') { $errors.Add("${id}: README metadata is missing.") }
        if ($null -eq $metadata.license -or [string] $metadata.license.type -ne 'expression' -or [string] $metadata.license.'#text' -ne 'MIT') {
            $errors.Add("${id}: MIT license expression metadata is missing or incorrect.")
        }
        if ($null -eq $metadata.repository -or [string] $metadata.repository.type -ne 'git' -or
            [string] $metadata.repository.url -ne 'https://github.com/subhajitlabs/AgentForge.NET') {
            $errors.Add("${id}: repository metadata is missing or incorrect.")
        }
        if (-not [string]::IsNullOrWhiteSpace($ExpectedRepositoryCommit) -and
            [string] $metadata.repository.commit -ne $ExpectedRepositoryCommit) {
            $errors.Add("${id}: repository commit '$($metadata.repository.commit)' does not match '$ExpectedRepositoryCommit'.")
        }

        $entryNames = @($archive.Entries.FullName)
        if ($entryNames -notcontains 'README.md') { $errors.Add("${id}: README.md is not in the package.") }
        if ($id -eq 'AgentForge.NET') {
            $payloadEntries = @($entryNames | Where-Object { $_ -match '^(lib|ref|runtimes)/' })
            if ($payloadEntries.Count -ne 0) {
                $errors.Add("${id}: dependency-only metapackage contains runtime or compile assets: $($payloadEntries -join ', ').")
            }

            $dependencyGroups = @($metadata.dependencies.group)
            $net10Group = @($dependencyGroups | Where-Object { [string] $_.targetFramework -eq 'net10.0' })
            if ($net10Group.Count -ne 1) {
                $errors.Add("${id}: expected exactly one net10.0 dependency group.")
            }
            else {
                $directDependencies = @($net10Group[0].dependency)
                $directDependencyIds = @($directDependencies | ForEach-Object { [string] $_.id } | Sort-Object)
                $expectedDirectDependencyIds = @(
                    'AgentForge.Hosting'
                    'AgentForge.Providers.OpenAI'
                    'AgentForge.Tools'
                ) | Sort-Object
                if (Compare-Object $expectedDirectDependencyIds $directDependencyIds) {
                    $errors.Add("${id}: direct dependency set is incorrect: $($directDependencyIds -join ', ').")
                }
            }
        }
        elseif ($entryNames -notcontains "lib/net10.0/$id.xml") {
            $errors.Add("${id}: XML documentation is not in the package.")
        }

        foreach ($dependency in @($metadata.dependencies.group.dependency)) {
            if ([string] $dependency.id -like 'AgentForge.*' -and [string] $dependency.version -ne $expectedVersion) {
                $errors.Add("${id}: internal dependency '$($dependency.id)' has version '$($dependency.version)'.")
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

foreach ($symbol in $symbols) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($symbol.FullName)
    try {
        $expectedId = $expectedSymbolPackageIds | Where-Object {
            $symbol.Name -eq "$($_).$expectedVersion.snupkg"
        }
        $pdbEntries = @($archive.Entries | Where-Object FullName -Like 'lib/net10.0/*.pdb')
        if ($null -eq $expectedId -or @($expectedId).Count -ne 1) {
            $errors.Add("$($symbol.Name): cannot determine the expected package ID.")
        }
        elseif ($pdbEntries.Count -ne 1 -or $pdbEntries[0].FullName -ne "lib/net10.0/$expectedId.pdb") {
            $errors.Add("$($symbol.Name): expected exactly lib/net10.0/$expectedId.pdb.")
        }
    }
    finally {
        $archive.Dispose()
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    exit 1
}

Write-Host "Verified exactly seven AgentForge.NET $expectedVersion packages, six symbol packages, and the dependency-only metapackage contents."
