#Requires -Version 7
<#
.SYNOPSIS
Builds consumer projects with the locally built analyzer and writes a per-rule NTBA diagnostic report.

.EXAMPLE
pwsh ./corpus/Invoke-CorpusRun.ps1 -Project D:\App\App.WebApp\App.WebApp.csproj -OutputPath ./artifacts/corpus/after.txt -BaselinePath ./artifacts/corpus/before.txt
#>
param(
    [Parameter(Mandatory)] [string[]] $Project,
    [Parameter(Mandatory)] [string] $OutputPath,
    [string] $BaselinePath,
    [string] $Framework,
    # Optional .globalconfig with ntba.* settings applied to every project in this run.
    [string] $SettingsPath,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$Project = $Project -split ','
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$analyzerDirectory = Join-Path $repositoryRoot "NT.BlazorAnalyzer/bin/$Configuration/netstandard2.0/"
$swapTargets = Join-Path $PSScriptRoot 'swap.targets'

dotnet build (Join-Path $repositoryRoot 'NT.BlazorAnalyzer/NT.BlazorAnalyzer.csproj') -c $Configuration -v quiet -nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Analyzer build failed.' }

$diagnostics = foreach ($projectPath in $Project) {
    $arguments = @('build', $projectPath, '-v', 'quiet', '-nologo', '-clp:NoSummary',
        "-p:CustomAfterMicrosoftCommonTargets=$swapTargets", "-p:NtbaLocalDir=$analyzerDirectory",
        '-p:TreatWarningsAsErrors=false', '-p:NoWarn=CS0618', '-p:WarningsAsErrors=', '-p:CodeAnalysisTreatWarningsAsErrors=false')
    if ($Framework) { $arguments += @('-f', $Framework) }
    if ($SettingsPath) { $arguments += "-p:NtbaSettingsConfig=$((Resolve-Path $SettingsPath).Path)" }
    # Run from the project directory so the consumer's global.json selects its SDK.
    Push-Location (Split-Path $projectPath -Parent)
    try { $output = & dotnet @arguments } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { $output | Select-Object -Last 30 | Out-Host; throw "Build failed: $projectPath" }
    $output | Select-String -Pattern '^(?<file>.+?)\((?<line>\d+),\d+\): warning (?<id>NTBA\d{4}): (?<message>.+?) \[' |
        ForEach-Object { '{0} {1}({2}): {3}' -f $_.Matches[0].Groups['id'].Value, $_.Matches[0].Groups['file'].Value, $_.Matches[0].Groups['line'].Value, $_.Matches[0].Groups['message'].Value }
}

$diagnostics = @($diagnostics | Sort-Object -Unique)
New-Item -ItemType Directory -Force (Split-Path $OutputPath -Parent) | Out-Null
$diagnostics | Set-Content $OutputPath

$summary = $diagnostics | Group-Object { $_.Substring(0, 8) } | Sort-Object Name | ForEach-Object { '{0}: {1}' -f $_.Name, $_.Count }
"Total: $($diagnostics.Count)"
$summary

if ($BaselinePath) {
    $baseline = @(Get-Content $BaselinePath)
    "Added: $(@($diagnostics | Where-Object { $_ -notin $baseline }).Count)"
    $diagnostics | Where-Object { $_ -notin $baseline } | ForEach-Object { "  + $_" }
    "Removed: $(@($baseline | Where-Object { $_ -notin $diagnostics }).Count)"
    $baseline | Where-Object { $_ -notin $diagnostics } | ForEach-Object { "  - $_" }
}
