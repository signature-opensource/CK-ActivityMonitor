Hierarchical logger API - a monitor that follows the code path instead of an ambient context.

Main types: `IActivityMonitor` and its implementation, `IActivityMonitorClient` with standard local
sinks such as `ActivityMonitorTextWriterClient`, and `LogFile` with its central static `RootLogPath`.

Also `AsyncLock`, which detects, handles or rejects asynchronous lock reentrancy without any
`AsyncLocal`, thanks to the ubiquitous monitor parameter; `StaticGate`, to remove the logging overhead
entirely on a hot path while keeping the ability to turn it back on; and a static contextless logger for
the low level zones that have no monitor yet.
