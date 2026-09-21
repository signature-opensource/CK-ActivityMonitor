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
queued for a deferred handling and that's exactly what the GrandOutput (in CK.Monitoring) does.

To avoid a boxed allocation when a `ActivityMonitorLogData` must leave the direct synchronous client log handling, [ActivityMonitorExternalLogData](ActivityMonitorExternalLogData.cs)
can be obtained by calling `ActivityMonitorLogData.AcquireExternalData()`. This reference type is pooled and a simple reference counter
can be used to transfer the ownership and retain it alive until the data becomes useless.

> This obviously requires the `ActivityMonitorExternalLogData` to be released for memory to be reused and achieve 
> zero allocation. A missing `Release()` doesn't corrupt anything (the data is eventually garbage collected) but it
> defeats the pool, and it is now detected and reported: see below.

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

## Future Goal: Utf8 capture and binary content

Currently the only content is the `Text` that is a string (and a string requires an allocation).
Logging JSON payload is rather inefficient: the JSON has to be encoded in UTF-16 in the Text string to
eventually be written/serialized in UTF-8 encoding.

The efficient Utf8JsonWriter directly writes to a IBufferWriter that can be a [RecyclableMemoryStream](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream)
(that uses a pool of reusable buffers).
The idea is to support binary payloads thanks to RecyclableMemoryStream or pooled buffered to:
- compute once on demand the UTF-8 Text string representation and cache it when Text is the logged data.
- offer new ways to log different type of content:
```c#
// Using a Utf8Writer.
// here using the new C# 11 `u8` suffix: https://devblogs.microsoft.com/dotnet/csharp-11-preview-updates/#utf-8-string-literals.
monitor.Info( w => { w.WriteStartObject(); w.WriteNumber("Power"u8, _power ); w.WriteEndObject()} );

// Using a binary writer (binary serialization):
monitor.Info( w => user.Write( w ) );
```
File content may also be logged:
```c#
// Using a Stream (Helper.CopyFile doesn't exist).
monitor.Info( s => Helper.CopyFile( "appsetings.json", s ) );

// May be better to offer easier extension methods... 
monitor.InfoFile( "appsetings.json" );
```

Interpolated string handlers write their content to a (generally) cached and reusable StringBuilder. The StringBuilder (since .Net 6) exposes its
`ReadOnlyMemory<char>` chunks of buffers: this can be used to skip the final string generation and directly encode the interpolation
result as an "UTF8" string into a RecyclableMemoryStream or a pooled byte array (or may be steal the linked list of buffer from the StringBuilder).

Providing that the StringBuilder is a reused one and the `Text` property is NOT solicited later on (the client and handlers only need the Utf8 content
to save it in files or sends it on the wire), this would lead to a real zero allocation logging.

Such binary content goes into an internal RecyclableMemoryStream of the ActivityMonitorLogData or a pooled array of bytes: the eventual release
of the resources (dispose of streams or returns of the byte arrays to their pool) is possible thanks to the `ActivityMonitorExternalLogData`:
at the end of the `UnfilteredOpenGroup` and `UnfilteredLog`, if a `ActivityMonitorExternalLogData` has been acquired, it is the owner of
the resources, otherwise the resources must be released immediately.

> Once the `ActivityMonitorExternalLogData` will hold these resources, forgetting to release them properly WILL LEAK.

This also requires to introduce a notion of "content type" that doesn't exist yet.
We may create a new enum (Text, JSON, Binary...) but this sounds limited. We should simply use Tags to 
decorate/qualify the content type and define:
- "BIN" for binary content.
- "UTF8" for Utf8 string.
- "JSON" for Utf8 encoded Json. Technically this should be [JSON with Comments](https://code.visualstudio.com/docs/languages/json#_json-with-comments)

The `Text` property, if needed, can always be derived from the binary content:
- for "UTF8", "JSON" by encoding the UTF-8 content back to UTF-16.
- for binary, a simple `<binary data>` string can be used or a base64/base64Url be generated.

### Impacts

This has a lot of impacts but totally source based (risks are low) but more importantly requires time that we don't currently have: all this
can be postponed after the current work on the log centralization.




