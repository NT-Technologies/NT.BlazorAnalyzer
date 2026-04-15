# NT.BlazorAnalyzer

`NT.BlazorAnalyzer` is a Roslyn analyzer for Blazor component error-handling rules.

Current focus:
- interactive `.razor` components
- the generated `BuildRenderTree` shape from Razor
- component methods in `.razor` and `.razor.cs` partials

## Projects

- [NT.BlazorAnalyzer/NT.BlazorAnalyzer.csproj](/home/natet/NT.BlazorAnalyzer/NT.BlazorAnalyzer/NT.BlazorAnalyzer.csproj): analyzer implementation
- [Tests/NT.BlazorAnalyzer.Tests/NT.BlazorAnalyzer.Tests.csproj](/home/natet/NT.BlazorAnalyzer/Tests/NT.BlazorAnalyzer.Tests/NT.BlazorAnalyzer.Tests.csproj): xUnit v3 test suite
- [NT.BlazorAnalyzer.slnx](/home/natet/NT.BlazorAnalyzer/NT.BlazorAnalyzer.slnx): solution

## Rules

### `NTBA0001`

Warning when an explicitly interactive component has an unprotected interactive render root. The analyzer evaluates each top-level render root independently, ignores `PageTitle` and `HeadContent`, allows inert HTML roots, and requires event-callback HTML roots or component roots to be protected by `ErrorBoundary` or a derived component.

Diagnostic text:
`Interactive component '{ComponentName}' should protect interactive render roots with ErrorBoundary or a derived component`

### `NTBA0002`

Warning when a method in an interactive component without a valid root `ErrorBoundary` can be reached without `try/catch` handling.

This rule is entrypoint-oriented, not helper-oriented:
- root/API methods should be protected
- helper methods do not need their own `try/catch` when every reachable caller already has one
- a wrapper method does not need a warning if it only delegates to another safe member method
- a helper method does warn if at least one uncaught root path reaches it

Diagnostic text:
`Method '{MethodName}' in interactive component '{ComponentName}' can be reached without try/catch handling`

### `NTBA0003`

Warning when an interactive lifecycle method with operational code does not use `try/catch`.

Covered lifecycle methods:
- `OnInitialized`
- `OnInitializedAsync`
- `OnParametersSet`
- `OnParametersSetAsync`
- `OnAfterRender`
- `OnAfterRenderAsync`
- `SetParametersAsync`

### `NTBA0004`

Warning when `Dispose` or `DisposeAsync` contains operational code without `try/catch`.

### `NTBA0005`

Warning when a component method performs JS interop without `try/catch`.

### `NTBA0006`

Warning when JS interop is performed in early lifecycle methods before `OnAfterRender{Async}` without an interactivity guard.

This warning is suppressed when the JS interop call is inside an `if` statement that checks for interactivity, for example `if (RendererInfo.IsInteractive)`.

### `NTBA0007`

Warning on `async void` methods in interactive components.

### `NTBA0008`

Warning on `catch` blocks that neither:
- log
- track/report telemetry
- nor rethrow

### `NTBA0009`

Warning when a component opens `ErrorBoundary` first but does not provide `ErrorContent`.

## Warning Examples

### Emits `NTBA0001` and `NTBA0002`

```razor
@rendermode InteractiveServer

<button @onclick="IncrementCount">Click</button>

@code {
    private void IncrementCount()
    {
        CurrentCount++;
    }

    private int CurrentCount { get; set; }
}
```

Why:
- the root `button` has an interactive callback and is not protected by `ErrorBoundary`
- `IncrementCount` is a UI entry method and has no `try/catch`

### Emits `NTBA0001` and `NTBA0002` even if the component type derives from `ErrorBoundary`

```csharp
public partial class MyComponent : ErrorBoundary
{
    private void HandleClick()
    {
        DoWork();
    }
}
```

If the generated `BuildRenderTree` contains an unprotected interactive root, the component still warns. The rule is based on rendered roots, not the component base type.

### Emits `NTBA0002` on an uncaught root and helper

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
        CurrentCount++;
    }

    private int CurrentCount { get; set; }
}
```

Why:
- `HandleUnsafe` is reachable without `try/catch`
- `IncrementCore` is also reachable from an uncaught root path

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
- the interactive button is inside `ErrorBoundary`
- `NTBA0002` is suppressed because the interactive component is already boundary-protected

### Emits `NTBA0003` for an uncaught lifecycle method

```razor
@rendermode InteractiveServer

@code {
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private Task LoadAsync() => Task.CompletedTask;
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

### Emits `NTBA0009` when root `ErrorBoundary` has no `ErrorContent`

```razor
@rendermode InteractiveServer

<ErrorBoundary>
    <button @onclick="HandleClick">Click</button>
</ErrorBoundary>
```

## Build And Test

```bash
dotnet test NT.BlazorAnalyzer.slnx -v minimal
dotnet build NT.BlazorAnalyzer.slnx -c Release -v minimal
dotnet test NT.BlazorAnalyzer.slnx -v minimal -p:TestingPlatformCommandLineArguments="--coverage --coverage-output-format cobertura --coverage-output ./TestResults/coverage.cobertura.xml"
```

## GitHub CI/CD

GitHub Actions files are under `.github/workflows`:
- `ci.yml`: restores, builds, tests, and collects coverage on pull requests and pushes to `main`; after validation passes on `main`, it installs semantic-release with `npm install --no-save` and runs `npx semantic-release`
- `release.yml`: runs when a `v*` tag is pushed, packs the analyzer using the tag version, and publishes the `.nupkg` and `.snupkg` to NuGet

Release configuration lives in `.releaserc.json` and uses:
- `@semantic-release/commit-analyzer`
- `@semantic-release/release-notes-generator`
- `@semantic-release/github` creating the GitHub release with generated release notes

The release flow is intentionally split:
- `main` push passes build/test, then semantic-release creates the GitHub release and `v${version}` tag on the validated commit
- tag creation starts the package publish workflow
- the tag version is passed as both the build version and NuGet package version

Required GitHub secrets:
- `SEMANTIC_RELEASE_TOKEN`: GitHub token with repository contents write permission. This must be a PAT or equivalent token because tags created by the default `GITHUB_TOKEN` do not reliably trigger the tag-based publish workflow.
- `NUGET_API_KEY`: NuGet.org API key with package push permissions

The NuGet package is published as a Roslyn analyzer package, so the analyzer assembly is placed under `analyzers/dotnet/cs` in the generated `.nupkg`.
