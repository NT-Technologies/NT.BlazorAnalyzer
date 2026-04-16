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
        CodeFixProvider codeFixProvider)
    {
        var document = CreateProject(path, text).Documents.Single();
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await codeFixProvider.RegisterCodeFixesAsync(context);
        return actions;
    }

    public static async Task<string> ApplyCodeActionAsync(
        string path,
        string text,
        Diagnostic diagnostic,
        CodeFixProvider codeFixProvider,
        string title)
    {
        var document = CreateProject(path, text).Documents.Single();
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(
            document,
            diagnostic,
            (action, _) => actions.Add(action),
            CancellationToken.None);

        await codeFixProvider.RegisterCodeFixesAsync(context);
        var action = Assert.Single(actions, candidate => candidate.Title == title);
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        var changedSolution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        var updatedDocument = changedSolution.GetDocument(document.Id);
        var updatedText = await updatedDocument!.GetTextAsync();
        return updatedText.ToString();
    }

    private static Project CreateProject(string path, string text)
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

        foreach (var reference in GetMetadataReferences())
        {
            solution = solution.AddMetadataReference(projectId, reference);
        }

        var documentId = DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(documentId, Path.GetFileName(path), SourceText.From(text), filePath: path);
        return solution.GetProject(projectId)!;
    }

    private static IReadOnlyList<MetadataReference> GetMetadataReferences()
    {
        var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                references[path] = MetadataReference.CreateFromFile(path);
            }
        }

        AddReference(typeof(object).Assembly);
        AddReference(typeof(Enumerable).Assembly);
        AddReference(typeof(Task).Assembly);
        AddReference(typeof(Microsoft.AspNetCore.Components.ComponentBase).Assembly);
        AddReference(typeof(Microsoft.AspNetCore.Components.Web.ErrorBoundary).Assembly);
        AddReference(typeof(Microsoft.AspNetCore.Components.RenderModeAttribute).Assembly);
        AddReference(typeof(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder).Assembly);

        return references.Values.ToArray();

        void AddReference(Assembly assembly)
        {
            references[assembly.Location] = MetadataReference.CreateFromFile(assembly.Location);
        }
    }
}
