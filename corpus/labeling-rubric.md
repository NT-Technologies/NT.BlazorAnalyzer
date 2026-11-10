# Finding labeling rubric

Label each analyzer finding **TP** (true positive) or **FP** (false positive) by checking facts in the source. Do not judge how likely a failure is beyond what the rubric states, and do not judge the code's intent (a deliberate demo is still labeled on the facts).

A finding is **TP** when all of the following hold. If any is false, it is **FP**. Use **UNCERTAIN** only when the needed fact cannot be determined from the repository, and name the missing fact.

## Shared facts

- **Interactive:** the component runs interactively when it, or a component that renders it, declares an interactive render mode, or when the app's `Routes` component has an interactive render mode. A routable page with neither renders in static SSR, where a failure becomes an HTTP error response.
- **Covered:** a region is covered when an `ErrorBoundary` (or any `ErrorBoundaryBase` subclass) encloses it in the same component, or encloses the component in every component that renders it, including layouts that wrap `@Body`.
- **Can fail:** an operation can fail when it performs I/O (HTTP, database, file, stream, SignalR), JS interop that runs application script (built-ins such as `confirm`, `alert` or `history.back` do not count), deserialization of external data, or contains an explicit `throw` that a caller in the reported path can reach. Follow calls into project code to see what they do. A method documented or implemented to return failures as values (Result types, Refit `IApiResponse`) does not fail by returning a failure value.
- **Unhandled:** no `try/catch` on the path catches the failure. A catch that only rethrows, or only catches cancellation (`OperationCanceledException`, `TaskCanceledException`, `when (...IsCancellationRequested)`) or `JSDisconnectedException`, does not handle other failures.

## Clarifications

Added after the first labeling round, before any later data was seen:

- **Database I/O** means running a query, saving changes, or executing SQL. Creating a context (`CreateDbContext`) or disposing one does not perform I/O.
- **Missing host project:** when a client project's pages are hosted by a server project, read the server project's `App.razor`, `Routes.razor` and layouts. If they cannot be found, label from the client project alone.
- **Interactive region:** an `EventCallback` or delegate passed to a child component counts as an interactive region in the parent.
- **NTBA0005:** a catch of only `JSDisconnectedException` does not handle a `JSException`, except in disposal code, where the NTBA0004 allowance applies.
- **Unknown JS function:** a function that is not a browser built-in counts as application script even when its definition is not in the repository.

Decided by the project owner after the third labeling round:

- **Unrendered component:** a component that no page or component in the app renders, and that is not routable, cannot fail at runtime; its findings are FP.
- **Message-only logging:** a catch that logs only `exception.Message` loses the exception type and stack trace, so it does not report the exception object; NTBA0008 is TP.

## Per rule

| Rule | TP when |
| --- | --- |
| NTBA0001 | The reported region is interactive (an event handler, callback or binding) and is not covered for at least one component that renders it, in an interactive context. |
| NTBA0002 | The reported entry method is reached from an uncovered interactive region and contains, or calls, an operation that can fail and is unhandled. |
| NTBA0003 | The reported lifecycle method runs interactively, is not covered by every owner, and an operation in it (or in project code it calls) can fail and is unhandled. |
| NTBA0004 | The reported `Dispose`/`DisposeAsync` performs, directly or through project code, an operation that can fail and is unhandled. Catching only `JSDisconnectedException` around JS module cleanup follows Microsoft's documented pattern and counts as handled. |
| NTBA0005 | The reported method performs JS interop that runs application script, and a `JSException` from it is unhandled. |
| NTBA0006 | The reported early lifecycle method performs JS interop reachable during prerendering without an interactivity check. |
| NTBA0007 | The reported method is `async void` in an interactive component. |
| NTBA0008 | The reported general catch (`catch` or `catch (Exception)`) neither logs or reports the exception object, nor rethrows, nor shows the user an error (a toast or an error message in component state). |
| NTBA0009 | The reported root boundary is Microsoft's `ErrorBoundary` (or a subclass that renders no error content of its own) with no `ErrorContent`. |
| NTBA0010 | The reported static layout's root boundary is the only boundary around interactive pages, and app routes are not globally interactive. |
| NTBA0011 | The task is not awaited or observed, and its work contains an operation that can fail and is unhandled inside the task. |
| NTBA0012 | The callback runs outside the render flow (timer, `LocationChanged`, application service event) and contains an operation that can fail and is unhandled inside the callback. |

## Output

One line per finding: `ID<TAB>TP|FP|UNCERTAIN<TAB>the fact that decided it (file and line you checked)`.
