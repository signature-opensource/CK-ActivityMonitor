# .Net EventSource

EventSource were available in the good old (dying) .Net framework. It has been ported into .Net Core:
- Starts here: https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventsource-getting-started
- And read this if you're wondering "Why not using this instead of the ActivityMonitor library?": https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventsource-activity-ids
  (and recall that all this amazing Guid stuff relies on the [evil AsyncLocal](https://twitter.com/davidfowl/status/1033964051607379968)).

That is what we have and when it is used (by the .Net framework itself), this allows interesting insights about the inner working
(or not working!) of deep code areas.

The static helper [DotNetEventSourceCollector](DotNetEventSourceCollector.cs) does its best to:
- Easily discover the available sources and their current level.
- Disable an EventSource.
- Enables an EventSource at a given level. 
- Render the events in a readable ways into the `ActivityMonitor.StaticLogger`.

By default, EventSources are disabled (and this is a good thing since they can be quite verbose and have performance costs).
For an example of EventSource implementation (outside of the .Net code itself), have a look at the [RecyclableMemoryStream](https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream#metrics-and-hooks).

## Listing available EventSources and EventLevel introduction

The static `GetSources()` retrieves a list of simple value tuples with Name and Level:
```csharp
IReadOnlyList<(string Name, EventLevel? Level)> all = DotNetEventSourceCollector.GetSources();
```
Here's the list that is obtained when run from a unit test in CK.ActivityMonitor.Tests:
Microsoft-Windows-DotNETRuntime, System.Runtime, TestPlatform, System.Threading.Tasks.TplEventSource, System.Buffers.ArrayPoolEventSource,
System.Diagnostics.Eventing.FrameworkEventSource, Private.InternalDiagnostics.System.Net.Sockets, System.Net.Sockets, Microsoft-IO-RecyclableMemoryStream.

Their level are all null, but **this is OUR level**. Whether one of these sources is enabled or not is not our concern. This is a little bit like
the `IActivityMonitor.ActualFilter` vs. `IActivityMonitor.MinimalFilter`: one "participant" want `EventLevel.Verbose` and
another one `EventLevel.Critical`, the EventSource will honor the Verbose (but won't send the Verbose events to the second "participant").
"Participants" here are [EventListener](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.tracing.eventlistener) instances that have
been created so far in the system (a kind of global `IActivityMonitorClient`).

The [EventLevel](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.tracing.eventlevel) has 5 real levels (plus the ambiguous `LogAlways`):

| Level         |   |           Description |
|---------------|---|---------------------------------------------|
| LogAlways     | 0	| No level filtering is done on the event. When used as a level filter for enabling events, for example in EventListener.EnableEvents(), events of all levels will be included.|
| Critical      | 1 | This level corresponds to a critical error, which is a serious error that has caused a major failure.|
| Error         | 2 | This level adds standard errors that signify a problem.|
| Warning       | 3 | This level adds warning events (for example, events that are published because a disk is nearing full capacity).|
| Informational	| 4 | This level adds informational events or messages that are not errors. These events can help trace the progress or state of an application.|
| Verbose       | 5	| This level adds lengthy events or messages. It causes all events to be logged.|

The semantics of the `LogAlways` is ambiguous: it's a level AND a "non filter". This is precisely why we have the `CK.Core.LogLevel` and `CK.Core.LogFilter`.

## Enabling an EventSource
It is as simple as:
```csharp
DotNetEventSourceCollector.Enable( "Private.InternalDiagnostics.System.Net.Sockets", EventLevel.Informational );
```
With this, when playing with sockets, you can see these logs (successful bind but failed connection, taken from a GrandOutput's text output file):
```
2023-02-03 09h55.08.2691411 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='.ctor' message='InterNetworkV6' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.08.2691984 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='(null)' memberName='CreateSocket' message='SafeSocketHandle:43258890(0x604)' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.08.2692122 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='SetSocketOption' message='optionLevel:IPv6 optionName:IPv6Only optionValue:0' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.08.2692239 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='SetSocketOption' message='SetSockOpt returns errorCode:Success' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.08.2692352 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='GetSocketOption' message='GetSockOpt returns errorCode:Success' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.08.2692407 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='GetSocketOption' message='GetSockOpt returns errorCode:Success' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.08.2692519 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='GetSocketOption' message='GetSockOpt returns errorCode:Success' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.08.2692565 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='GetSocketOption' message='GetSockOpt returns errorCode:Success' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.08.2692676 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='AwaitableSocketAsyncEventArgs#14309202' memberName='InitializeInternals' message='new PreAllocatedOverlapped PreAllocatedOverlapped#18194715' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.08.2692738 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='GetSocketOption' message='GetSockOpt returns errorCode:Success' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.08.2692828 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='GetSocketOption' message='GetSockOpt returns errorCode:Success' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.08.2692886 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='WildcardBindForConnectIfNecessary' message='[::]:0' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.08.2693461 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='SafeSocketHandle#43258890' memberName='GetOrAllocateThreadPoolBoundHandle' message='calling ThreadPool.BindHandle()' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.09.2045775 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#34969337' memberName='Poll' message='Poll returns socketCount:0' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.10.2067311 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#34969337' memberName='Poll' message='Poll returns socketCount:0' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.10.3145113 ~### E  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:5] EventName='ErrorMessage' Payload={ thisOrContextObject='Socket#19589663' memberName='UpdateStatusAfterSocketError' message='errorCode:ConnectionRefused' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.10.3147723 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='Dispose' message='timeout = -1' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.10.3147946 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='Dispose' message='disposing:True Disposed:False' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.10.3147964 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='Socket#19589663' memberName='Dispose' message='Calling _handle.CloseAsIs()' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.10.3147993 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='SafeSocketHandle#43258890' memberName='CloseAsIs' message='shouldClose=True' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.10.3148019 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='SafeSocketHandle#43258890' memberName='ReleaseHandle' message='shouldClose=False' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.10.3148064 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='SafeSocketHandle#43258890' memberName='CloseHandle' message='handle:0x604' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.10.3148102 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='SafeSocketHandle#43258890' memberName='DoCloseHandle' message='handle:0x604, Following 'blockable' branch' } Keywords='263882790666241' OpCode='Info'
2023-02-03 09h55.10.3148324 ~### i  [EventSource] [Private.InternalDiagnostics.System.Net.Sockets:4] EventName='Info' Payload={ thisOrContextObject='SafeSocketHandle#43258890' memberName='DoCloseHandle' message='handle:0x604, closesocket()#1:Success' } Keywords='263882790666241' OpCode='Info'
```
The way the events are rendered may evolve in the future (currently, ActivityId are not dumped).
Note that the logs coming from an EventSource is tagged with `[EventSource]`.

## When should this be used?
When your are in deep trouble and TEMPORARY. Do NOT let any EventSource enabled for a long time, especially on a production system!
Note that the `DotNetEventSourceCollector.DisableAll()` helper can be called at any time.

## DotNetEventSourceConfigurator

This helper works exactly like the one for [StaticGate](../StaticGate/README.md). It only exposes 2 static methods:

```csharp
public static void ApplyConfiguration( IActivityMonitor? monitor, string configuration );
```
Applies a new configuration. The configuration string is rather simple, each name is followed by its level:
`"System.Threading.Tasks.TplEventSource:C[ritical];System.Net.Sockets:!"`.

It is enough for the level to be the first character:
- 'L' or 'l' for `LogAlways`.
- 'C' or 'c' for `Critical`.
- 'E' or 'e' for `Error`.
- 'W' or 'w' for `Warning`.
- 'I' or 'i' for `Informational`.
- 'V' or 'v' for `Verbose`.
- '!' to disable the EventSource.

If the level is not specified or is not one of these characters, `EventLevel.Informational` is assumed.
The configuration applies until a new one is applied (the creation of new EventSources is tracked thanks to `DotNetEventSourceCollector.OnNewEventSource`).

```csharp
public static string GetConfiguration( bool? enabled = null )
```
Gets a configuration string that can be applied later by calling `ApplyConfiguration`.
By default both enabled and disabled EventSources are returned. The `enabled` parameter can be true to only consider the
enabled ones and false to only return the disabled ones.

The package [CK.Monitoring](https://github.com/Invenietis/CK-Monitoring) uses this to enable its
GrandOutput configuration to configure the .Net event sources.


