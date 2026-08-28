Provides extension methods to send log entries. The OpenDebug/Trace/Info/Warn/Error/Fatal and Debug,
Trace, Info, Warn, Error, Fatal extension methods support file name and line number capture.

Because the message is an interpolated handler, it is not evaluated when the log is filtered out by its
level or its tags: a Debug line costs nothing when Debug is off, as long as the message stays an
interpolated string rather than a concatenation.

Interpolated type formats give readable .NET type names in logs - `{t:C}` yields
`Nested<Dictionary<int,(string,int?)>>` instead of the backticked runtime form.
