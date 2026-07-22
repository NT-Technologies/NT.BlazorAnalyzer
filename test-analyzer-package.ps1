param([Parameter(Mandatory = $true)][string]$PackagePath)

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

    foreach ($compilerAssembly in 'Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll', 'System.Collections.Immutable.dll', 'System.Reflection.Metadata.dll') {
        if ($archive.Entries | Where-Object FullName -eq "analyzers/dotnet/cs/$compilerAssembly") {
            throw "Analyzer package must not contain compiler-owned assembly '$compilerAssembly'."
        }
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

    $escapedFeedDirectory = [System.Security.SecurityElement]::Escape($feedDirectory)
    $escapedPackagesDirectory = [System.Security.SecurityElement]::Escape($packagesDirectory)
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RestoreSources>$escapedFeedDirectory</RestoreSources>
    <RestorePackagesPath>$escapedPackagesDirectory</RestorePackagesPath>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="$packageId" Version="$packageVersion" />
  </ItemGroup>
</Project>
"@

    $source = @'
namespace AnalyzerPackageConsumer;

[Counter.__PrivateComponentRenderModeAttribute]
public partial class Counter : Microsoft.AspNetCore.Components.ComponentBase
{
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "button");
        builder.AddAttribute(1, "onclick", Microsoft.AspNetCore.Components.EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
        builder.CloseElement();
    }

    private static void HandleClick() => throw new InvalidOperationException();

    private sealed class __PrivateComponentRenderModeAttribute : Microsoft.AspNetCore.Components.RenderModeAttribute
    {
        public override Microsoft.AspNetCore.Components.IComponentRenderMode Mode => Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer;
    }
}
'@

    $projectPath = Join-Path $projectDirectory 'AnalyzerPackageConsumer.csproj'
    [System.IO.File]::WriteAllText($projectPath, $project, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $projectDirectory 'Counter.razor.g.cs'), $source, [System.Text.UTF8Encoding]::new($false))

    $restoreOutput = & dotnet restore $projectPath --source $feedDirectory --ignore-failed-sources --verbosity minimal 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Analyzer package consumer restore failed:`n$($restoreOutput -join [Environment]::NewLine)"
    }

    $buildOutput = & dotnet build $projectPath --no-restore --configuration Release --verbosity minimal 2>&1
    $buildText = $buildOutput -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "Analyzer package consumer build failed:`n$buildText"
    }
    if ($buildText -notmatch '\bNTBA0001\b') {
        throw "Analyzer package consumer build did not report NTBA0001:`n$buildText"
    }

    Write-Host "Verified $packageId $packageVersion and observed NTBA0001 in a consumer build."
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
