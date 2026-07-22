# NT.BlazorAnalyzer

`NT.BlazorAnalyzer` is a Roslyn analyzer for Blazor component error-handling rules.

Current focus:

- interactive `.razor` components
- the generated `BuildRenderTree` shape from Razor
- component methods in `.razor` and `.razor.cs` partials

## Installation

Install the latest stable package from NuGet:

```bash
dotnet add package NT.BlazorAnalyzer
```

The package is marked as a development dependency and contributes analyzer and code-fix assets without adding a compile-time or runtime library reference. The analyzer assemblies are loaded from `analyzers/dotnet/cs`.

All diagnostics are enabled by default. Consumer projects can change individual severities in `.editorconfig`, for example:

```ini
[*.cs]
dotnet_diagnostic.NTBA0009.severity = none
```

The analyzer assembly targets `netstandard2.0`. This repository builds and tests with the .NET 10 SDK.

## Projects

- [NT.BlazorAnalyzer/NT.BlazorAnalyzer.csproj](NT.BlazorAnalyzer/NT.BlazorAnalyzer.csproj): analyzer implementation and NuGet package definition
- [NT.BlazorAnalyzer.CodeFixes/NT.BlazorAnalyzer.CodeFixes.csproj](NT.BlazorAnalyzer.CodeFixes/NT.BlazorAnalyzer.CodeFixes.csproj): IDE code-fix providers packaged with the analyzer
- [Tests/NT.BlazorAnalyzer.Tests/NT.BlazorAnalyzer.Tests.csproj](Tests/NT.BlazorAnalyzer.Tests/NT.BlazorAnalyzer.Tests.csproj): xUnit v3 test suite running on Microsoft Testing Platform v2 with Microsoft Code Coverage
- [NT.BlazorAnalyzer.slnx](NT.BlazorAnalyzer.slnx): solution containing all three projects

## Rules

| Rule | Default severity | Summary | Code fix |
| --- | --- | --- | --- |
| `NTBA0001` | Warning | Protect independently interactive render regions with `ErrorBoundary` | No |
| `NTBA0002` | Warning | Handle failures in uncovered interactive entry methods | No |
| `NTBA0003` | Warning | Handle failures in lifecycle methods or provide owner-boundary coverage | Wrap body in `try/catch` |
| `NTBA0004` | Warning | Handle failure-prone disposal | Wrap body in `try/catch` |
| `NTBA0005` | Warning | Handle JS interop failures | Wrap body in `try/catch` |
| `NTBA0006` | Warning | Guard early-lifecycle JS interop with an interactivity check | Guard with `RendererInfo.IsInteractive` |
| `NTBA0007` | Warning | Return `Task` or `ValueTask` instead of `async void` | Change return type to `Task` |
| `NTBA0008` | Warning | Report the caught exception object or rethrow | Rethrow the exception |
| `NTBA0009` | Info | Consider custom content for a root `ErrorBoundary` | Add `ErrorContent` |
| `NTBA0010` | Warning | Avoid relying on static layout boundaries for interactive failures | No |

### `NTBA0001`

Warning when an explicitly interactive component has an unprotected independently interactive render region. The analyzer evaluates each top-level render region independently, ignores `PageTitle` and `HeadContent`, allows inert HTML roots, and requires event-callback HTML roots or interactive component roots to be protected by `ErrorBoundary` or a derived component at an appropriate containment level.

NTBA0001 is region-based, not component-root-based:
- one diagnostic is emitted per uncovered interactive region
- diagnostics are reported at the region root or interactive attribute when source mapping is available
- generated `BuildRenderTree` semantic analysis is the source of truth for component interactivity
- Razor parsing is used for HTML `@on...` / `@bind-...`, boundary detection, and `.razor` location mapping

Interactive regions that count:
- HTML event-handler roots such as `@onclick`
- component callback roots backed by `EventCallback`, delegates, method groups, lambdas, or anonymous methods
- component binding roots such as `@bind-Value` / generated `ValueChanged` callback patterns

Content that does not count by itself:
- inert HTML with no interactive attributes
- plain `RenderFragment` / `RenderFragment<T>` content parameters without callback or binding semantics
- ignored roots such as `PageTitle` and `HeadContent`

Diagnostic text:
`Interactive render region in component '{ComponentName}' should be protected by ErrorBoundary. Wrapping '{RootName}' in an ErrorBoundary resolves this warning. Suggested scope: '{SuggestedScope}'.`

### `NTBA0002`

Warning when an interactive entry method reachable from an independently interactive render region without `ErrorBoundary` coverage performs failure-prone work without `try/catch` handling.

This rule is entrypoint-oriented, not helper-oriented:
- only interactive entry methods discovered from uncovered render regions are considered
- helper methods do not need their own `try/catch` when every reachable caller already has one
- a wrapper method does not need a warning if it only delegates to another safe member method
- helper methods do not warn on their own, even when they contain the risky operation
- trivial local state mutation alone does not trigger `NTBA0002`
- lifecycle, dispose, and JS interop concerns are still covered by `NTBA0003` through `NTBA0006`

Diagnostic text:
`Method '{MethodName}' in interactive component '{ComponentName}' performs failure-prone work reachable from an uncovered interactive region without try/catch handling`

### `NTBA0003`

Warning when an interactive lifecycle method performs failure-prone work without meaningful exception handling or known owner `ErrorBoundary` coverage.

Covered lifecycle methods:
- `OnInitialized`
- `OnInitializedAsync`
- `OnParametersSet`
- `OnParametersSetAsync`
- `OnAfterRender`
- `OnAfterRenderAsync`
- `SetParametersAsync`

Notes:
- early lifecycle methods are treated as the highest-risk surface because unhandled failures can break prerendering or circuit initialization
- a component's own root `ErrorBoundary` does not protect that component's lifecycle method; the component must be rendered by an owner that is already covered
- if every known static owner path renders the component inside `ErrorBoundary`, local lifecycle `try/catch` is not required
- root-boundary coverage can flow through local `RenderTreeBuilder` helper methods, protected concrete derived component usages, and `DynamicComponent` dialog hosts whose type slot is inside a known `ErrorBoundary`
- custom boundary tags are trusted only when the component type is known to inherit `ErrorBoundary`, including boundaries from referenced assemblies
- pure delegation to a safe local helper is accepted
- trivial local state mutation alone does not trigger `NTBA0003`
- a swallowed catch can still trigger `NTBA0003` alongside `NTBA0008`

### `NTBA0004`

Warning when `Dispose` or `DisposeAsync` performs failure-prone cleanup without meaningful exception handling.

Notes:
- `DisposeCore`-style helpers are analyzed transitively, but the warning stays on `Dispose` or `DisposeAsync`
- pure delegation to a safe local cleanup helper is accepted
- trivial local cleanup alone does not trigger `NTBA0004`
- a swallowed catch can still trigger `NTBA0004` alongside `NTBA0008`

### `NTBA0005`

Warning when a component method performs JS interop without meaningful exception handling.

Notes:
- a catch that logs or rethrows satisfies the rule
- a swallowed catch can still trigger `NTBA0005` alongside `NTBA0008`
- `DisposeAsync` cleanup that catches `JSDisconnectedException` is treated as an allowed Blazor cleanup pattern
- methods that only delegate to a safe local JS helper do not need their own wrapper

### `NTBA0006`

Warning when JS interop is performed in early lifecycle methods before `OnAfterRender{Async}` without a recognized interactivity check.

Notes:
- the rule is path-aware for early lifecycle methods: `OnInitialized{Async}`, `OnParametersSet{Async}`, and `SetParametersAsync`
- direct checks such as `if (RendererInfo.IsInteractive)` and `if (AssignedRenderMode is not null)` suppress the warning
- helper wrappers that delegate to those checks are accepted
- interactivity guard-clause patterns such as `if (!RendererInfo.IsInteractive) return;` are accepted
- guarded helper calls do not warn when the helper performs the JS interop and is only reached through the recognized guard
- unrelated boolean conditions do not suppress the warning

### `NTBA0007`

Warning on `async void` methods in interactive components.

Notes:
- aligns with Blazor guidance to return `Task` or `ValueTask` from asynchronous component methods
- the goal is to keep completion and exception flow observable to the framework

### `NTBA0008`

Warning on `catch` blocks that neither:
- log or report the caught exception object
- nor rethrow

Notes:
- scope-only or message-only calls such as `Logger.BeginScope(...)` or `Logger.LogError(ex.Message)` do not satisfy the rule
- expected `JSDisconnectedException` cleanup during JS object disposal is still treated as an accepted pattern

### `NTBA0009`

Info when a component opens `ErrorBoundary` first but relies on the default `ErrorBoundary` fallback UI.

Notes:
- custom `ErrorContent` is optional in Blazor
- this rule is a UX recommendation for root or broadly user-visible boundaries, not a framework requirement

### `NTBA0010`

Warning when a static layout component uses a root `ErrorBoundary` without globally interactive app routes.

Notes:
- in Blazor Web Apps, a static layout boundary only covers static SSR
- it doesn't catch interactive event-handler failures unless app routes are globally interactive
- narrower page or widget boundaries are usually the safer default

## Code Fixes

The packaged code-fix provider supports `NTBA0003` through `NTBA0009`, except rules whose fixes require a design or containment decision:

- `NTBA0003`, `NTBA0004`, and `NTBA0005`: wrap a supported method body in `try/catch`
- `NTBA0006`: add a `RendererInfo.IsInteractive` guard
- `NTBA0007`: change an eligible `async void` method to return `Task`
- `NTBA0008`: rethrow the caught exception
- `NTBA0009`: add Razor `ErrorContent` markup when the boundary shape can be edited safely

Batch Fix All is available through Roslyn's standard batch fixer. `NTBA0001`, `NTBA0002`, and `NTBA0010` intentionally have no automatic fix because the correct boundary placement or error-handling design depends on the component hierarchy.

## Examples

### Emits `NTBA0001` and `NTBA0002`

```razor
@rendermode InteractiveServer

<button @onclick="IncrementCount">Click</button>

@code {
    private void IncrementCount()
    {
        throw new InvalidOperationException();
    }
}
```

Why:
- the button creates an independently interactive region and is not protected by `ErrorBoundary`
- `IncrementCount` is an uncovered interactive entry method and performs failure-prone work without `try/catch`

### Emits `NTBA0001` for a component callback root

```razor
@rendermode InteractiveServer

<EditorForm OnSave="HandleSave" />

@code {
    private void HandleSave()
    {
        Save();
    }

    private void Save()
    {
    }
}
```

Why:
- `OnSave="HandleSave"` is a semantic component callback root
- the component root is independently interactive and is not protected by `ErrorBoundary`

### Does not emit `NTBA0001` for plain templated content alone

```razor
@rendermode InteractiveServer

<ShellLayout>
    <ChildContent>
        <h1>Static title</h1>
    </ChildContent>
</ShellLayout>
```

Why:
- plain `RenderFragment` / templated content is not treated as interactive by itself
- without callback or binding semantics, the component root is inert for NTBA0001

### Emits one `NTBA0001` per uncovered interactive region

```razor
@rendermode InteractiveServer

<button @onclick="IncrementCount">Increment</button>
<EditorForm OnSave="HandleSave" />

@code {
    private void IncrementCount()
    {
        CurrentCount++;
    }

    private void HandleSave()
    {
        Save();
    }

    private void Save()
    {
    }

    private int CurrentCount { get; set; }
}
```

Why:
- the button is one uncovered interactive region
- `EditorForm OnSave="HandleSave"` is a second uncovered interactive region
- NTBA0001 reports each region separately at its own source location when available

### Emits `NTBA0001` and `NTBA0002` even if the component type derives from `ErrorBoundary`

```csharp
public partial class MyComponent : ErrorBoundary
{
    private void HandleClick()
    {
        throw new InvalidOperationException();
    }
}
```

If the generated `BuildRenderTree` contains an unprotected independently interactive region, the component still warns. The rule is based on rendered regions, not the component base type.

### Emits `NTBA0002` only on an uncaught root when a helper path is failure-prone

```razor
@rendermode InteractiveServer

<button @onclick="HandleUnsafe">Unsafe</button>
<button @onclick="HandleSafe">Safe</button>

@code {
    private void HandleSafe()
    {
        try
        {
            IncrementCore();
        }
        catch (Exception)
        {
        }
    }

    private void HandleUnsafe()
    {
        IncrementCore();
    }

    private void IncrementCore()
    {
        ThrowNow();
    }

    private void ThrowNow() => throw new InvalidOperationException();
}
```

Why:
- `HandleUnsafe` is an uncovered interactive entry method
- the reachable helper path is failure-prone
- `IncrementCore` does not get its own `NTBA0002`; the warning stays on the entry method

### Does not emit `NTBA0002` for a helper used only from caught roots

```razor
@rendermode InteractiveServer

<button @onclick="HandleClick">Click</button>

@code {
    private void HandleClick()
    {
        try
        {
            IncrementCore();
        }
        catch (Exception)
        {
        }
    }

    private void IncrementCore()
    {
        CurrentCount++;
    }

    private int CurrentCount { get; set; }
}
```

Why:
- `IncrementCore` has no `try/catch`
- every reachable caller path into `IncrementCore` is already protected

### Does not emit `NTBA0002` for a delegating wrapper around a safe method

```razor
@rendermode InteractiveServer

<button @onclick="HandleClick">Click</button>

@code {
    private void HandleClick() => HandleClickCore();

    private void HandleClickCore()
    {
        try
        {
            Save();
        }
        catch (Exception)
        {
        }
    }

    private void Save()
    {
    }
}
```

Why:
- `HandleClick` delegates entirely to `HandleClickCore`
- `HandleClickCore` already provides the protection

### Does not emit when interactive content is protected by `ErrorBoundary`

```razor
@rendermode InteractiveServer

<ErrorBoundary>
    <button @onclick="IncrementCount">Click</button>
</ErrorBoundary>

@code {
    private void IncrementCount()
    {
        CurrentCount++;
    }

    private int CurrentCount { get; set; }
}
```

Why:
- the interactive region is inside `ErrorBoundary`
- `NTBA0002` is suppressed because the interactive region is already boundary-protected

### Emits `NTBA0003` for an uncaught lifecycle method

```razor
@rendermode InteractiveServer

@code {
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await Task.CompletedTask;
        throw new InvalidOperationException();
    }
}
```

### Emits `NTBA0005` for uncaught JS interop

```razor
@rendermode InteractiveServer

<button @onclick="HandleClick">Click</button>

@code {
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private async Task HandleClick()
    {
        await JS.InvokeVoidAsync("doSomething");
    }
}
```

### Emits `NTBA0006` for JS interop too early in the lifecycle

```razor
@rendermode InteractiveServer

@code {
    [Inject] private IJSRuntime JS { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("doSomething");
        }
        catch (Exception)
        {
            throw;
        }
    }
}
```

### Does not emit `NTBA0006` when interactivity is explicitly checked

```razor
@rendermode InteractiveServer

@code {
    [Inject] private IJSRuntime JS { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        if (RendererInfo.IsInteractive)
        {
            await JS.InvokeVoidAsync("doSomething");
        }
    }
}
```

### Does not emit `NTBA0006` when a helper or guard clause proves interactivity

```razor
@rendermode InteractiveServer

@code {
    [Inject] private IJSRuntime JS { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        if (!IsInteractiveRender())
        {
            return;
        }

        await LoadClientStateAsync();
    }

    private bool IsInteractiveRender() => RendererInfo.IsInteractive;

    private async Task LoadClientStateAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("doSomething");
        }
        catch (Exception)
        {
            throw;
        }
    }
}
```

### Emits `NTBA0008` for swallowed exceptions

```razor
@rendermode InteractiveServer

<button @onclick="HandleClick">Click</button>

@code {
    private void HandleClick()
    {
        try
        {
            Save();
        }
        catch (Exception)
        {
        }
    }
}
```

### Emits `NTBA0009` as a UX recommendation when root `ErrorBoundary` has no `ErrorContent`

```razor
@rendermode InteractiveServer

<ErrorBoundary>
    <button @onclick="HandleClick">Click</button>
</ErrorBoundary>
```

### Emits `NTBA0010` when a static layout uses `ErrorBoundary`

```razor
@inherits LayoutComponentBase

<ErrorBoundary>
    @Body
    <ErrorContent>
        <p>Something went wrong.</p>
    </ErrorContent>
</ErrorBoundary>
```

Why:
- the layout boundary only covers static SSR unless app routes are globally interactive
- interactive event failures below the layout can still bypass the layout boundary in Blazor Web Apps
- narrower page or widget boundaries are usually a better default

## Build, Test, And Pack

Prerequisites for local development are the .NET 10 SDK and PowerShell 7 for the package verification and release-tracking scripts.

```bash
dotnet restore NT.BlazorAnalyzer.slnx
dotnet build NT.BlazorAnalyzer.slnx -c Release -v minimal
dotnet test --solution NT.BlazorAnalyzer.slnx -c Release --no-build -v minimal
```

Collect Microsoft Testing Platform coverage with:

```bash
dotnet test --solution NT.BlazorAnalyzer.slnx -c Release --no-build -v minimal --coverage --coverage-output-format cobertura --coverage-output ./TestResults/coverage.cobertura.xml
```

Create and verify a local analyzer package with:

```powershell
dotnet pack ./NT.BlazorAnalyzer/NT.BlazorAnalyzer.csproj --configuration Release --no-build --output ./artifacts/nuget -p:PackageVersion=0.0.0-local
pwsh ./test-analyzer-package.ps1 -PackagePath ./artifacts/nuget/NT.BlazorAnalyzer.0.0.0-local.nupkg
```

The package verifier checks the NuGet layout and metadata, rejects compile/runtime and compiler-owned assemblies, installs the package into an isolated .NET 10 consumer, and requires that the consumer build report `NTBA0001`.

The repository selects Microsoft Testing Platform through `global.json`, uses `xunit.v3.mtp-v2` as its test runner integration, and collects coverage through `Microsoft.Testing.Extensions.CodeCoverage`. It does not use Coverlet or the VSTest compatibility runner.

Repository builds enable the .NET SDK analyzers and code-style analysis, treat every warning as an error, and require XML documentation for public APIs through `CS1591`.

## GitHub CI/CD

GitHub Actions files are under `.github/workflows`:

- `ci.yml`: restores, builds, tests, and collects coverage on pull requests, pushes to `main`, and manual runs
- `publish-prerelease.yml`: validates pushes to `main`, tags release-worthy builds with a semantic-release preview version, then verifies and publishes that tagged version's `.nupkg` and `.snupkg` to NuGet
- `release.yml`: manually tags a stable semantic release from `main`, verifies and publishes that full version, unlists superseded previews, and opens a follow-up analyzer release-tracking pull request when unshipped rules need promotion

[Dependabot configuration](.github/dependabot.yml) checks NuGet and GitHub Actions dependencies weekly and groups updates by ecosystem.

Release configuration lives in `.releaserc.json` and uses:

- `@semantic-release/commit-analyzer`
- `@semantic-release/release-notes-generator`
- `@semantic-release/github` to create GitHub releases with generated release notes

Commit analysis and release notes use the Conventional Commits preset.

The release flow is intentionally split:

1. A push to `main` validates the repository.
2. Semantic-release evaluates the Conventional Commits history on the `preview` release branch and, when warranted, tags the validated commit as `v${version}-preview.${number}`.
3. Only after that preview tag is resolved does the same workflow rebuild with the tagged version, pack and verify the analyzer, and publish the `.nupkg` and `.snupkg` to NuGet. A push with no new preview tag skips package publication.
4. A stable release is created only by manually dispatching `release.yml`. Semantic-release tags `main` as `v${version}`, after which the workflow rebuilds, verifies, and publishes the full version.
5. The stable workflow unlists prerelease versions at or below the released core version.
6. After a stable release, `promote-analyzer-release.ps1` moves unshipped analyzer entries into the shipped file and the workflow opens a pull request when that produces a change.

Both publishing workflows remove a newly created tag if a downstream publication step fails.

Required GitHub secrets:

- `NUGET_API_KEY`: NuGet.org API key with package push permissions

GitHub release creation, tag management, preview-branch updates, and release-tracking pull requests use the workflow-provided `GITHUB_TOKEN`; no separate semantic-release token is required.

## NuGet Package Layout

The package is a development-only Roslyn analyzer package:

- analyzer and code-fix assemblies are under `analyzers/dotnet/cs`
- code-fix workspace and composition dependencies are colocated with the code-fix provider
- compiler-owned assemblies such as `Microsoft.CodeAnalysis.dll`, `Microsoft.CodeAnalysis.CSharp.dll`, `System.Collections.Immutable.dll`, and `System.Reflection.Metadata.dll` are excluded
- no assemblies are published under `lib`, so the package does not add a compile-time or runtime API
- the `.nupkg` includes `README.md`, `Logo.png`, analyzer symbols, and repository metadata; packing also creates a `.snupkg`
- XML documentation is generated beside the analyzer and code-fix build outputs
