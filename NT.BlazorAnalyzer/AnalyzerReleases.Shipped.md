## Release 1.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
NTBA0001 | Reliability | Warning | Independently interactive render regions should be covered by ErrorBoundary at an appropriate containment level.
NTBA0002 | Reliability | Warning | Methods reachable from interactive regions without ErrorBoundary coverage should contain try/catch.
NTBA0003 | Reliability | Warning | Interactive lifecycle methods should protect operational code with try/catch.
NTBA0004 | Reliability | Warning | Interactive dispose methods should protect operational code with try/catch.
NTBA0005 | Reliability | Warning | Interactive JS interop should use try/catch handling.
NTBA0006 | Reliability | Warning | JS interop before OnAfterRender should be guarded by interactivity.
NTBA0007 | Reliability | Warning | Interactive component methods should not be async void.
NTBA0008 | Reliability | Warning | Catch blocks in interactive components should log, track, or rethrow exceptions.
NTBA0009 | Reliability | Info | Root ErrorBoundary should consider custom ErrorContent.
NTBA0010 | Reliability | Warning | Static layout ErrorBoundary has limited interactive coverage.

## Release 3.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
NTBA0011 | Reliability | Warning | Fire-and-forget component tasks should observe failures.
NTBA0012 | Reliability | Warning | Callbacks from timers, navigation, or service events should handle failures.

### Changed Rules

Rule ID | New Category | New Severity | Old Category | Old Severity | Notes
--------|--------------|--------------|--------------|--------------|-------
NTBA0001 | Reliability | Info | Reliability | Warning | Recommended default; set NTBlazorAnalyzerMode=strict for Warning.
NTBA0002 | Reliability | Info | Reliability | Warning | Recommended default; set NTBlazorAnalyzerMode=strict for Warning.
