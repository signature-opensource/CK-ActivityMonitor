# ActivityMonitorLogData details

This [struct](ActivityMonitorLogData.cs) holds the data common to any log event (except the closing group that is not a concern of this library). It contains the text,
level, tags, logTime, exception, file name and line number, plus the identifier of the emitting monitor, the depth
of the log in the opened groups and 3 flags (whether the log is a parallel one, whether it opens a group and
whether the data is frozen).

Currently, it is a mutable struct (88 bytes on 64 bits architecture) always passed by reference: it always lies on the stack except
in the corner case of the [ActivityMonitor.InternalMonitor](ActivityMonitor/ActivityMonitor.InternalMonitor.cs) that handles buggy clients
where we don't care if some allocations happen.

Groups (see [ActivityMonitor.Group.cs](ActivityMonitor/ActivityMonitor.Group.cs)) are pooled by each monitor and reused: their life
cycle is directly driven by the Open/CloseGroup of the monitor API and their memory is reused.

## Towards Zero allocation: the ActivityMonitorExternalLogData pool

The `ActivityMonitorLogData` struct passed by reference works great in terms of allocations as long as the data doesn't need to be
queued for a deferred handling (a sink that writes the logs asynchronously, for instance).

To avoid a boxed allocation when a `ActivityMonitorLogData` must leave the direct synchronous client log handling, [ActivityMonitorExternalLogData](ActivityMonitorExternalLogData.cs)
can be obtained by calling `ActivityMonitorLogData.AcquireExternalData()`. This reference type is pooled and a simple reference counter
can be used to transfer the ownership and retain it alive until the data becomes useless.

> This obviously requires the `ActivityMonitorExternalLogData` to be released for memory to be reused and achieve 
> zero allocation. A missing `Release()` doesn't corrupt anything (the data is eventually garbage collected) but it
> defeats the pool, and it is detected and reported: see below.

## Leak detection: the PoolDiagnostics

The pool is instrumented by a [PoolDiagnostics](PoolDiagnostics/PoolDiagnostics.cs) exposed by the static
`ActivityMonitorExternalLogData.PoolDiagnostics` property. A pool of reference counted objects can be abused in two
very different ways and the two signals are handled independently:

- **Dropped objects.** A data is acquired and its last reference is lost without `Release()` being called.
  The finalizer of a pooled object that is still alive calls `PoolDiagnostics.OnLeaked`: this is an *exact* detection
  with no false positive, since a pooled object is rooted by its pool and can only ever be finalized while it is alive.
  `LeakedCount` counts them and up to `MaxLeakSampleCount` (10) `LeakSamples` describe the culprits.
- **Retained objects.** A data is acquired (or `AddRef()`-ed) and kept forever by an ever growing container. Such an
  object stays reachable so no finalizer will ever run for it: the only observable symptom is that the *floor* of
  `AliveCount` rises across the successive observation windows. This is what `CurrentFloor` tracks.

Both are reported as `Error` lines tagged `ToBeInvestigated` on the `ActivityMonitor.StaticLogger`. There is
deliberately no [StaticGate](StaticGate/README.md) here and no way to silence them: a leak is always a bug. Reports
are throttled (`InitialReportDelay`, then doubling up to `MaximalReportDelay`) so that a leaking process doesn't
flood its own logs.

Pool saturation and capacity growth are **not** leak signals: they are the high-water mark of the concurrently alive
objects, that is a peak of activity. They are counted (`SaturatedCount`, `CapacityIncreaseCount`, `PeakAliveCount`)
so that metrics can expose them, but nothing is logged about them.
