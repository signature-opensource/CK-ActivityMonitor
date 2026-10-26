Extension methods that send logs to an `IActivityMonitor`: `Debug`, `Trace`, `Info`, `Warn`, `Error`, `Fatal` lines and
`OpenDebug`...`OpenFatal` groups, with file name and line number capture. Their interpolated string handlers skip the
message evaluation when the log is filtered out, and type formats (`{t:C}`, `{t:N}`) give readable .NET type names.
