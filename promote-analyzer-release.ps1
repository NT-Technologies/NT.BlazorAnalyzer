[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,
    [string] $ShippedPath = (Join-Path $PSScriptRoot 'NT.BlazorAnalyzer/AnalyzerReleases.Shipped.md'),
    [string] $UnshippedPath = (Join-Path $PSScriptRoot 'NT.BlazorAnalyzer/AnalyzerReleases.Unshipped.md')
)

$lineEnding = "`r`n"
$encoding = [System.Text.UTF8Encoding]::new($false)
$shippedText = [System.IO.File]::ReadAllText($ShippedPath)
$unshippedText = [System.IO.File]::ReadAllText($UnshippedPath)
$unshippedContent = (($unshippedText -split '\r?\n' | Where-Object { -not $_.StartsWith(';') }) -join $lineEnding).Trim()

if ($unshippedContent -notmatch '(?m)^NTBA\d{4}\s*\|') {
    Write-Host 'No unshipped analyzer rules were found.'
    return
}

if ($shippedText -match "(?m)^## Release $([regex]::Escape($Version))\r?$") {
    throw "Analyzer release $Version is already present in $ShippedPath."
}

$normalizedShippedText = ($shippedText -replace '\r?\n', $lineEnding).TrimEnd()
$newShippedText = $normalizedShippedText + $lineEnding + $lineEnding + "## Release $Version" + $lineEnding + $lineEnding + $unshippedContent + $lineEnding
$newUnshippedText = '; Unshipped analyzer release' + $lineEnding + '; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md' + $lineEnding + $lineEnding

[System.IO.File]::WriteAllText($ShippedPath, $newShippedText, $encoding)
[System.IO.File]::WriteAllText($UnshippedPath, $newUnshippedText, $encoding)

Write-Host "Promoted analyzer rules to release $Version."
