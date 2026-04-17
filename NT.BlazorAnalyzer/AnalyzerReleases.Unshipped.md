### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
NTBA0001 | Reliability | Warning | Interactive component should be rooted in ErrorBoundary or derive from ErrorBoundary.
NTBA0002 | Reliability | Warning | Methods in interactive components without ErrorBoundary protection should contain try/catch.
NTBA0003 | Reliability | Warning | Interactive lifecycle methods should protect operational code with try/catch.
NTBA0004 | Reliability | Warning | Interactive dispose methods should protect operational code with try/catch.
NTBA0005 | Reliability | Warning | Interactive JS interop should use try/catch handling.
NTBA0006 | Reliability | Warning | JS interop before OnAfterRender should be guarded by interactivity.
NTBA0007 | Reliability | Warning | Interactive component methods should not be async void.
NTBA0008 | Reliability | Warning | Catch blocks in interactive components should log, track, or rethrow exceptions.
NTBA0009 | Reliability | Warning | Root ErrorBoundary should provide ErrorContent.
NTBA0010 | Reliability | Warning | Layout-level ErrorBoundary should be keyed by route, reset on navigation, or moved down to page/widget scope.
NTBA0011 | Reliability | Warning | Layout-level ErrorBoundary route key should update on navigation instead of snapshotting the route once.
