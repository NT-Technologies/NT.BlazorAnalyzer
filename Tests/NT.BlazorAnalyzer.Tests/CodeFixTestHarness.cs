using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace NT.BlazorAnalyzer.Tests;

internal static class CodeFixTestHarness
{
    public static async Task<IReadOnlyList<CodeAction>> GetCodeActionsAsync(
        string path,
        string text,
        Diagnostic diagnostic,
        CodeFixProvider codeFixProvider,
        bool includeLogging = true)
    {
        var document = CreateProject(path, text, includeLogging).Documents.Single();
        return await RegisterAsync(document, diagnostic, codeFixProvider);
    }

    public static async Task<string> ApplyCodeActionAsync(
        string path,
        string text,
        Diagnostic diagnostic,
        CodeFixProvider codeFixProvider,
        string title,
        bool includeLogging = true)
    {
        var updatedDocument = await ApplyAsync(path, text, diagnostic, codeFixProvider, title, includeLogging);
        return (await updatedDocument.GetTextAsync()).ToString();
    }

    /// <summary>Applies the fix, compiles the updated document, and returns its compilation errors.</summary>
    public static async Task<IReadOnlyList<string>> GetCompilationErrorsAfterFixAsync(
        string path,
        string text,
        Diagnostic diagnostic,
        CodeFixProvider codeFixProvider,
        string title)
    {
        var updatedDocument = await ApplyAsync(path, text, diagnostic, codeFixProvider, title, includeLogging: true);
        return await GetErrorsAsync(updatedDocument.Project);
    }

    /// <summary>Compiles the unmodified document; used as the control for a compile round-trip.</summary>
    public static Task<IReadOnlyList<string>> GetCompilationErrorsAsync(string path, string text) =>
        GetErrorsAsync(CreateProject(path, text, includeLogging: true));

    private static async Task<IReadOnlyList<string>> GetErrorsAsync(Project project)
    {
        var compilation = await project.GetCompilationAsync();
        return compilation!.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.ToString())
            .ToArray();
    }

    private static async Task<List<CodeAction>> RegisterAsync(Document document, Diagnostic diagnostic, CodeFixProvider codeFixProvider)
    {
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await codeFixProvider.RegisterCodeFixesAsync(context);
        return actions;
    }

    private static async Task<Document> ApplyAsync(string path, string text, Diagnostic diagnostic, CodeFixProvider codeFixProvider, string title, bool includeLogging)
    {
        var document = CreateProject(path, text, includeLogging).Documents.Single();
        var actions = await RegisterAsync(document, diagnostic, codeFixProvider);
        var action = Assert.Single(actions, candidate => candidate.Title == title);
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var changedSolution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        return changedSolution.GetDocument(document.Id)!;
    }

    private static Project CreateProject(string path, string text, bool includeLogging)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(
            projectId,
            "CodeFixTests",
            "CodeFixTests",
            LanguageNames.CSharp);

        solution = solution
            .WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.Preview))
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        foreach (var reference in GetMetadataReferences(includeLogging))
        {
            solution = solution.AddMetadataReference(projectId, reference);
        }

        var documentId = DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(documentId, Path.GetFileName(path), SourceText.From(text), filePath: path);
        return solution.GetProject(projectId)!;
    }

    private static IReadOnlyList<MetadataReference> GetMetadataReferences(bool includeLogging)
    {
        var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (includeLogging || !Path.GetFileName(path).StartsWith("Microsoft.Extensions.Logging", StringComparison.OrdinalIgnoreCase))
                {
                    references[path] = MetadataReference.CreateFromFile(path);
                }
            }
        }

        AddReference(typeof(object).Assembly);
        AddReference(typeof(Enumerable).Assembly);
        AddReference(typeof(Task).Assembly);
        AddReference(typeof(Microsoft.AspNetCore.Components.ComponentBase).Assembly);
        AddReference(typeof(Microsoft.AspNetCore.Components.Web.ErrorBoundary).Assembly);
        AddReference(typeof(Microsoft.AspNetCore.Components.RenderModeAttribute).Assembly);
        AddReference(typeof(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder).Assembly);
        if (includeLogging)
        {
            AddReference(typeof(Microsoft.Extensions.Logging.ILogger).Assembly);
        }

        return references.Values.ToArray();

        void AddReference(Assembly assembly)
        {
            references[assembly.Location] = MetadataReference.CreateFromFile(assembly.Location);
        }
    }
}
