# NT.BlazorAnalyzer rules

| ID | Title | Default severity |
| --- | --- | --- |
| [NTBA0001](NTBA0001.md) | Interactive render region should be protected by ErrorBoundary | Info (Warning in strict mode) |
| [NTBA0002](NTBA0002.md) | Uncovered interactive entry method should use try/catch | Info (Warning in strict mode) |
| [NTBA0003](NTBA0003.md) | Lifecycle method should use meaningful exception handling or owner ErrorBoundary coverage | Warning |
| [NTBA0004](NTBA0004.md) | Dispose method should use meaningful exception handling | Warning |
| [NTBA0005](NTBA0005.md) | JS interop should use meaningful exception handling | Warning |
| [NTBA0006](NTBA0006.md) | Early lifecycle JS interop should use a recognized interactivity check | Warning |
| [NTBA0007](NTBA0007.md) | Async component method should return Task | Warning |
| [NTBA0008](NTBA0008.md) | Caught exceptions should report the caught exception or rethrow | Warning |
| [NTBA0009](NTBA0009.md) | Root ErrorBoundary should consider custom ErrorContent | Info |
| [NTBA0010](NTBA0010.md) | Static layout ErrorBoundary has limited interactive coverage | Warning |
| [NTBA0011](NTBA0011.md) | Component task failure is never observed | Warning |
| [NTBA0012](NTBA0012.md) | Callback from outside the render flow should handle failures | Warning |

## Configuration

### Severity mode

By default, NTBA0001 and NTBA0002 are Info and only calls with evidence of failure count. Strict mode raises both rules to Warning and also counts awaited calls with no evidence. To enable it, set the MSBuild property in the consuming project:

```xml
<PropertyGroup>
  <NTBlazorAnalyzerMode>strict</NTBlazorAnalyzerMode>
</PropertyGroup>
```

Other rules keep their severities from the table above.

### Analyzer settings

These settings go in a `.globalconfig` file or in the `[*.cs]` section of `.editorconfig`. Lists are comma-separated.

| Setting | Effect | Rules |
| --- | --- | --- |
| `ntba.failure_prone_type_suffixes` | Replaces the default receiver-type suffixes (`Repository`, `Service`, `Client`, `Provider`, `Manager`, `Store`, `Context`) used to decide that a synchronous call such as `service.GetData()` is failure-prone. | NTBA0002, NTBA0003, NTBA0004, NTBA0011, NTBA0012 |
| `ntba.failure_prone_method_prefixes` | Replaces the default method prefixes (`Get`, `Load`, `Save`, `Search`, `Export`, `Import`, `Open`, `Download`, `Upload`, `Refresh`, `Invoke`, `Send`, `Submit`, `Create`, `Update`, `Patch`, `Delete`, `Remove`, `Archive`, `Restore`) used for the same decision. | NTBA0002, NTBA0003, NTBA0004, NTBA0011, NTBA0012 |
| `ntba.safe_types` | Fully qualified type names, without type arguments, whose members never count as failure-prone, and calls returning them (directly or through `Task<T>`) are safe. Matches base types and interfaces. Built in: `AuthenticationStateProvider`, `AuthenticationState`, `EventCallback`. | NTBA0002, NTBA0003, NTBA0004, NTBA0011, NTBA0012 |
| `ntba.failure_handling_methods` | Method names, such as `OnSuccessAsync, OnFailureAsync`, that convert failures into values. A call chained into one of them is treated as safe. | NTBA0002, NTBA0003, NTBA0004, NTBA0011, NTBA0012 |
| `ntba.assume_owner_error_boundary` | `true` treats every component as covered by an owner `ErrorBoundary`. Defaults to `true` for component libraries (no routable and no render-mode components) and `false` otherwise. | NTBA0001, NTBA0002, NTBA0003 |
| `ntba.unknown_awaits_are_failure_prone` | `true` counts awaited calls with no evidence of failure, such as unknown library methods, as failure-prone. Strict mode sets it. | NTBA0002, NTBA0003, NTBA0004, NTBA0011, NTBA0012 |

Without configuration, calls returning a Result type (a `bool` property such as `IsSuccess`, `IsSuccessful`, `Succeeded`, `IsFailure`, `IsFailed`, `HasFailed` or `IsError`), toast and snackbar services, and built-in browser functions such as `confirm` are not failure-prone. Custom boundaries deriving from `ErrorBoundaryBase` count as error boundaries.

Failure-prone work needs evidence: JS interop, known I/O APIs (HttpClient and its JSON extensions, EF Core, streams and files, `JsonSerializer`, SignalR client, `IBrowserFile`), or operational naming (a `Get`/`Load`/`Save`... method on a `*Service`/`*Repository`/`*Client`... type). A call into the project's own code, including an interface with a single implementation in the project, is judged by what that code does, one level deep; a deliberate `throw` there (validation, an Identity redirect) is not counted. An awaited call with no such evidence, such as an unknown library method, is not failure-prone unless `ntba.unknown_awaits_are_failure_prone = true`, which strict mode sets.

Sample `.globalconfig`:

```ini
is_global = true

# Replace the default failure-prone receiver-type suffixes
ntba.failure_prone_type_suffixes = Repository, Service, Client, Api

# Replace the default failure-prone method prefixes
ntba.failure_prone_method_prefixes = Get, Load, Save, Sync

# Types whose members are never failure-prone (built-in: AuthenticationStateProvider, AuthenticationState, EventCallback)
ntba.safe_types = MyApp.Services.SettingsCache

# Methods that turn failures into values
ntba.failure_handling_methods = OnSuccessAsync, OnFailureAsync

# Only for component libraries whose consumers own the ErrorBoundary
# Library projects default to true; set false to analyze owner coverage anyway
ntba.assume_owner_error_boundary = false
```

## Suppressing

Each rule page shows how to suppress its rule. To suppress a rule for a whole project, set its severity in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.NTBA0009.severity = none
```
