Hierarchical logger API: a monitor that follows the code path instead of an ambient context.
Main types: `IActivityMonitor` and its `ActivityMonitor` implementation, `IActivityMonitorClient` with local sinks such as
`ActivityMonitorTextWriterClient`, `LogFilter` and the tag filters of `ActivityMonitor.Tags`, the static `ActivityMonitor.StaticLogger`,
`AsyncLock`, `StaticGate` and `LogFile` with its `RootLogPath`.
