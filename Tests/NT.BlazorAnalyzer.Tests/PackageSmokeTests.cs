using System.Diagnostics;
using System.IO.Compression;
using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class PackageSmokeTests
{
    [Fact]
    public async Task PackedNuGet_ContainsAnalyzerAndCodeFixAssemblies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "NT.BlazorAnalyzer", "NT.BlazorAnalyzer.csproj");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "nt-blazoranalyzer-pack-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var processStartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = repositoryRoot
            };
            processStartInfo.ArgumentList.Add("pack");
            processStartInfo.ArgumentList.Add(projectPath);
            processStartInfo.ArgumentList.Add("-c");
            processStartInfo.ArgumentList.Add("Debug");
            processStartInfo.ArgumentList.Add("--no-build");
            processStartInfo.ArgumentList.Add("--no-restore");
            processStartInfo.ArgumentList.Add("-o");
            processStartInfo.ArgumentList.Add(outputDirectory);

            using var process = Process.Start(processStartInfo);
            Assert.NotNull(process);

            var standardOutput = process!.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            Assert.True(
                process.ExitCode == 0,
                $"dotnet pack failed with exit code {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{await standardOutput}{Environment.NewLine}stderr:{Environment.NewLine}{await standardError}");

            var packagePath = Directory.GetFiles(outputDirectory, "NT.BlazorAnalyzer.*.nupkg")
                .Single(static path => !path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase));

            using var package = ZipFile.OpenRead(packagePath);
            var packageEntries = package.Entries.Select(static entry => entry.FullName).ToArray();

            Assert.Contains("analyzers/dotnet/cs/NT.BlazorAnalyzer.dll", packageEntries, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("analyzers/dotnet/cs/NT.BlazorAnalyzer.CodeFixes.dll", packageEntries, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("README.md", packageEntries, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("Logo.png", packageEntries, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDirectory is not null)
        {
            if (File.Exists(Path.Combine(currentDirectory.FullName, "NT.BlazorAnalyzer.slnx")))
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
