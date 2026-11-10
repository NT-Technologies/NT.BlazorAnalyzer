param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    # Each framework compiles the component with its own Razor language version.
    [string[]]$TargetFrameworks = @('net8.0', 'net9.0', 'net10.0')
)

$ErrorActionPreference = 'Stop'

$resolvedPackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem

$archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackagePath)
try {
    $requiredEntries = @(
        'analyzers/dotnet/cs/NT.BlazorAnalyzer.dll',
        'analyzers/dotnet/cs/NT.BlazorAnalyzer.CodeFixes.dll',
        'README.md',
        'Logo.png'
    )

    foreach ($requiredEntry in $requiredEntries) {
        if (-not ($archive.Entries | Where-Object FullName -eq $requiredEntry)) {
            throw "Analyzer package is missing required entry '$requiredEntry'."
        }
    }

    if ($archive.Entries | Where-Object { $_.FullName -like 'lib/*.dll' -or $_.FullName -like 'lib/*/*.dll' }) {
        throw 'Analyzer package must not expose its assembly as a compile or runtime library.'
    }

    $hostOwnedEntries = $archive.Entries | Where-Object { $_.FullName -like 'analyzers/*' -and $_.FullName -notlike 'analyzers/dotnet/cs/NT.BlazorAnalyzer.*' }
    if ($hostOwnedEntries) {
        throw "Analyzer package must only contain NT.BlazorAnalyzer assemblies under analyzers/; found: $($hostOwnedEntries.FullName -join ', ')."
    }

    $nuspecEntry = $archive.Entries | Where-Object FullName -like '*.nuspec' | Select-Object -First 1
    if (-not $nuspecEntry) {
        throw 'Analyzer package does not contain a nuspec file.'
    }

    $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
    try {
        [xml]$nuspec = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    $packageId = [string]$nuspec.package.metadata.id
    $packageVersion = [string]$nuspec.package.metadata.version
    $developmentDependency = [string]$nuspec.package.metadata.developmentDependency
    if ($packageId -ne 'NT.BlazorAnalyzer' -or [string]::IsNullOrWhiteSpace($packageVersion)) {
        throw "Unexpected analyzer package identity '$packageId' version '$packageVersion'."
    }
    if ($developmentDependency -ne 'true') {
        throw 'Analyzer package must be marked as a development dependency.'
    }
}
finally {
    $archive.Dispose()
}

$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "nt-blazor-analyzer-package-$([Guid]::NewGuid().ToString('N'))"
$feedDirectory = Join-Path $temporaryDirectory 'feed'
$projectDirectory = Join-Path $temporaryDirectory 'consumer'
$packagesDirectory = Join-Path $temporaryDirectory 'packages'

try {
    New-Item -ItemType Directory -Path $feedDirectory, $projectDirectory | Out-Null
    Copy-Item -LiteralPath $resolvedPackagePath -Destination $feedDirectory

    $escapedPackagesDirectory = [System.Security.SecurityElement]::Escape($packagesDirectory)
    $project = @"
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFrameworks>$($TargetFrameworks -join ';')</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RestorePackagesPath>$escapedPackagesDirectory</RestorePackagesPath>
    <NuGetAudit>false</NuGetAudit>
    <NTBlazorAnalyzerMode>strict</NTBlazorAnalyzerMode>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="$packageId" Version="$packageVersion" />
  </ItemGroup>
</Project>
"@

    # A real component, so the Razor source generator produces the code the analyzer inspects.
    $component = @'
@using Microsoft.AspNetCore.Components.Web
@rendermode RenderMode.InteractiveServer

<button @onclick="HandleClick">Click</button>

@code {
    private static void HandleClick() => throw new InvalidOperationException();
}
'@

    $projectPath = Join-Path $projectDirectory 'AnalyzerPackageConsumer.csproj'
    [System.IO.File]::WriteAllText($projectPath, $project, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $projectDirectory 'Counter.razor'), $component, [System.Text.UTF8Encoding]::new($false))
    # Build with the same SDK band as the repository rather than whatever newest preview is installed.
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'global.json') -Destination $temporaryDirectory

    # The local feed supplies the package under test; nuget.org supplies any targeting packs not installed locally.
    $nugetConfig = "<configuration><packageSources><clear /><add key=`"local`" value=`"$([System.Security.SecurityElement]::Escape($feedDirectory))`" /><add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`" /></packageSources></configuration>"
    [System.IO.File]::WriteAllText((Join-Path $temporaryDirectory 'nuget.config'), $nugetConfig, [System.Text.UTF8Encoding]::new($false))
    $restoreOutput = & dotnet restore $projectPath --verbosity minimal 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Analyzer package consumer restore failed:`n$($restoreOutput -join [Environment]::NewLine)"
    }

    $buildOutput = & dotnet build $projectPath --no-restore --configuration Release --verbosity minimal 2>&1
    $buildText = $buildOutput -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "Analyzer package consumer build failed:`n$buildText"
    }

    foreach ($targetFramework in $TargetFrameworks) {
        foreach ($ruleId in 'NTBA0001', 'NTBA0002') {
            $pattern = "Counter\.razor\(\d+,\d+\): warning $ruleId\b.*TargetFramework=$([regex]::Escape($targetFramework))\]"
            if ($buildText -notmatch $pattern) {
                throw "Consumer build for $targetFramework did not report $ruleId at the Razor source location:`n$buildText"
            }
        }
    }

    # Recommended (default) mode: the boundary-coverage rules are Info, so the same consumer reports no NTBA0001/NTBA0002 warning.
    [System.IO.File]::WriteAllText($projectPath, $project.Replace('<NTBlazorAnalyzerMode>strict</NTBlazorAnalyzerMode>', ''), [System.Text.UTF8Encoding]::new($false))
    $defaultBuildText = (& dotnet build $projectPath --no-restore --configuration Release --verbosity minimal 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "Analyzer package consumer build in default mode failed:`n$defaultBuildText"
    }
    if ($defaultBuildText -match 'warning NTBA000[12]\b') {
        throw "Default-mode consumer build reported NTBA0001/NTBA0002 as a warning; recommended mode should report them as Info:`n$defaultBuildText"
    }

    Write-Host "Verified $packageId ${packageVersion}: strict-mode NTBA0001 and NTBA0002 reported at Counter.razor for $($TargetFrameworks -join ', '), and not as warnings in default mode."
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
