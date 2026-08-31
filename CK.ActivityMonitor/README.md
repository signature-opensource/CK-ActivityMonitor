# ActivityMonitor implementation & design
---
**Note**

The ActivityMonitor is a different logger. The original motivation (back in... 2004) for that "logger" was to support structured logs ("structured" not in the sense of [SeriLog](https://serilog.net/), but structured as a book can be, with Sections, Parts, Chapters, Paragraphs etc.).
This is an opinionated framework: one strong belief is that **logging is NOT a "cross-cutting concern"** (I know how much this could hurt a lot of architects). Logging is the developer's voice, designing logs is an important mission of the developer: logs
must describe the program *by* its execution, they tell the story of the running code, they play a crucial role
in the maintenance, exploitation and evolution phase of any serious project.

The ActivityMonitor is more a **Storyteller**, than a regular Logger.

We believe that more and more architectures, tools, programs will take this path because it's one of the mean to handle high complexity.
MSBuild has this https://msbuildlog.com/, CI/CD interfaces starts to display toggled section around the execution steps, etc.

---


## No AmbientContext

The design of this library is all about fighting the implicit "Ambient Context" that too many libraries use (the worst word
in this sentence being **implicit**).

A `IActivityMonitor` follows the current executing context: it appears as a parameter in a lot of methods.
This is clearly an API pollution. Yes... but this is the only way to be explicit and not rely on implicit context.

An Implicit (also named Ambient) Context requires specific mechanism to be able to "follow the code path".
There is basically two such mechanisms: TLS and AsyncLocal.

### Thread Local Storage (good old synchronous world)

This one is easy, safe, and rather efficient. [Wikipedia](https://fr.wikipedia.org/wiki/Thread_Local_Storage) explains
it well. In C#, this is as simple as using the [ThreadStatic attribute](https://learn.microsoft.com/en-us/dotnet/api/system.threadstaticattribute)
Numerous historical logging framework used this to enrich the logs with contextual information, to structure the logs (by
opening "scopes"). This was easy, lock-free by design and "magical".

Unfortunately this cannot be used as soon as the code enter the asynchronous world, for the same reason as
why a [classical lock doesn't support await](AsyncLock.md).

### The AsyncLocal in the asynchronous world

The [async locals](https://docs.microsoft.com/en-us/dotnet/api/system.threading.asynclocal-1) is functionally equivalent
to thread static in the asynchronous world.

Unfortunately, this is much more complex and less efficient than TLS: the information that must "follow the code" is
encapsulated in an `ExecutionContext`, a kind of associative map whose implementation is deeply rooted in the framework.
Below a piece of [source](https://source.dot.net/#System.Private.CoreLib/src/libraries/System.Private.CoreLib/src/System/Threading/AsyncLocal.cs,ef9ce034697240ba):
```c#
    /// <summary>
    /// Interface used to store an IAsyncLocal => object mapping in ExecutionContext.
    /// Implementations are specialized based on the number of elements in the immutable
    /// map in order to minimize memory consumption and look-up times.
    /// </summary>
    internal interface IAsyncLocalValueMap
    {
        bool TryGetValue(IAsyncLocal key, out object? value);
        IAsyncLocalValueMap Set(IAsyncLocal key, object? value, bool treatNullValueAsNonexistent);
    }
```
Contrary to the `SynchronizationContext`, suppressing the flowing of this context is not as easy as calling `ConfigureAwait(false)`
because since "some code somewhere" may need this hidden context, it is considered too dangerous to be exposed (it can
still be [suppressed](https://learn.microsoft.com/en-us/dotnet/api/system.threading.executioncontext.suppressflow)).

The good news is that as long as this context is not used (ideally remains empty), the overhead is rather small. But
overusing `AsyncLocal<T>` will definitely cost.

To understand difference (and unfortunate coupling) between ExecutionContext and SynchronizationContext, read [this post from Stephen Toub](https://devblogs.microsoft.com/pfxteam/executioncontext-vs-synchronizationcontext/).

## Local Clients first

An `ActivityMonitor` first collects its received logs locally (and synchronously) and dispatch them to its
registered [`IActivityMonitorClient`](CoreModel/IActivityMonitorClient.cs).

A Client can be temporarily registered and act as a local log interceptor that can provide information to the call
site. In this scenario, an ActivityMonitor can be used as a kind of information channel between the callees up to their
caller. 

This is described in more details in [Client](Client/README.md).


## Using a client to assert on what the code logged

The local client architecture enables an interesting pattern for tests. `CollectTexts` registers a
client that captures what was logged, so the assertion is on the *story* the code told, not on its
return values. This is
[`demo_using_CollectTexts`](../Tests/CK.ActivityMonitor.Tests/DocumentationCodeSnippets.cs), verbatim:

```csharp
[Test]
public void demo_using_CollectTexts()
{
    var monitor = new ActivityMonitor();

    EventHandler<ActionEvent>? sender = null;

    sender += OnAction;

    using( monitor.CollectTexts( out var texts ) )
    {
        sender.Invoke( null, new ActionEvent( monitor, ( monitor, i ) => monitor.Info( $"Action {i}" ) ) );
        sender.Invoke( monitor, new ActionEvent( monitor, null ) );
        texts.ShouldBe( new[]
        {
            "Received Action and executing it.",
            "Action 3712",
            "Received a null Action. Ignoring it."
        } );
    }

    static void OnAction( object? sender, ActionEvent e )
    {
        if( e.Action == null ) e.Monitor.Warn( "Received a null Action. Ignoring it." );
        else
        {
            e.Monitor.Info( "Received Action and executing it." );
            e.Action( e.Monitor, 3712 );
        }
    }
}
```

`ActionEvent` is an [`EventMonitoredArgs`](EventMonitoredArgs.cs), which is what lets the handler log
into the monitor of the event rather than into one it captured.
## Standard LogFilter names
A [LogFilter](LogFilter.cs) must apply to Groups of logs and to Lines of logs. It can be expressed as **{Group,Line}** strings
like `"{Error,Trace}"` or predefined couples `"Debug"`.

A `LogFilter` is usually used as a "Minimal Filter": multiple participants declare their "MinimalFilter" and the resulting filter
is the lowest one: combining 2 LogFilters results in a `LogFilter` that satisfies both of them: “{Error,Trace}” combined
with “{Warn,Warn}” is “{Warn,Trace}”.

[LogClamper](LogClamper.cs) enables a `LogFilter` to act as a strong filter.
More on this [here](ActivityMonitor/TagFiltering.md).

### .Net standard conventions
The standard LogFilter have been defined based on the [recommended verbosity option for command lines](https://learn.microsoft.com/en-us/dotnet/standard/commandline/syntax#the---verbosity-option).

|   Name    |     Description   |
|-----------|-------------------|
|`Diagnostic` |`{Debug,Debug}` Everything is required.|
|`Detailed`   |`{Trace,Trace}` OpenTrace and Trace appear but no OpenDebug nor Debug lines.|
|`Normal`     |`{Info,Info}` OpenInfo and Fatal, Error, Warn or Info lines appear. This may be still a little "verbose" but captures key information. |
|`Minimal`    |`{Info,Warn}` OpenInfo and Fatal, Error or Warn lines appear. Minimal should have enough context to understand issues. |
|`Quiet`      |`{Error,Error}` Only errors are considered. This is our "minimal": errors should never be hidden. |

To this list, the `Fatal` that is the strongest filter (`{Fatal,Fatal}`) is added. It should be avoided.

### Other names
Before this recommendation appears we used:

|   Name    |     Description    |
|-----------|--------------------|
|`Debug`      | `{Debug,Debug}` |
|`Trace`      | `{Trace,Trace}`|
|`Verbose`    | `{Trace,Info}` |
|`Monitor`    | `{Trace,Warn}` |
|`Terse`      | `{Info,Warn}`  |
|`Release`    | `{Error,Error}`|

Those names are supported but the .Net standard ones should be preferred.

## IStaticLogger, IParallelLogger and IActivityMonitor
The `IStaticLogger` is exposed by the static `ActivityMonitor.StaticLogger` property. It is close to
more classical logging solutions: only lines can be emitted by any thread at any time and its emitted lines
are not structured (their Depth is always 0).
Since it derives from `IActivityLineEmitter` - it declares nothing of its own - it carries an
`AutoTags` and an `ActualFilter`, so any filtering or sender extension method written against that
interface works on it.


The `IParallelLogger` is like the static logger but is bound to a monitor: lines emitted by a parallel logger
are bound to the originating `IActivityMonitor`: even if this is a thread safe logger, the log time
and the depth (in opened groups) are synchronized with the monitor states.
- The LogTime uniquely identify the log line for the monitor identifier.
- The line Depth is accurate regarding the opened groups at the time the log is emitted.
It sends its log lines through the `ActivityMonitor.OnStaticLog` event,
not through the `IActivityMonitor.Output`: the logs ordering relative to the monitor is preserved BUT these thread safe
logs cannot be observed by the monitor's output `IActivityMonitorClient`.

A `IParallelLogger` can create an `ActivityMonitor.Token`, because it derives from
`IActivityDependentTokenFactory` - the very interface `IActivityMonitor` also derives from. The
binding to a monitor is what makes its log time and depth meaningful, not what grants it the token.


The [`IActivityMonitor`](CoreModel/IActivityMonitor.cs) interface is a logger that groups and, more importantly, the `Output`
where `IActivityMonitorClient` listeners/sinks can be registered and unregistered.

```mermaid
classDiagram
    direction TB

    class IActivityLineEmitter {
        <<interface>>
        +CKTrait AutoTags
        +LogLevelFilter ActualFilter
        +CreateActivityMonitorLogData(level, ...)
        +UnfilteredLog(ref data)
    }
    class IStaticLogger {
        <<interface>>
    }
    class IActivityDependentTokenFactory {
        <<interface>>
        +CreateToken(message) ActivityMonitor.Token
    }
    class IActivityMonitor {
        <<interface>>
        +string UniqueId
        +CKTrait AutoTags
        +LogFilter MinimalFilter
        +LogFilter ActualFilter
        +string Topic
        +IActivityMonitorOutput Output
        +IParallelLogger ParallelLogger
        +SetTopic(newTopic)
        +UnfilteredOpenGroup(ref data) IDisposableGroup
        +CloseGroup(userConclusion) bool
    }
    class IParallelLogger {
        <<interface>>
        +string UniqueId
    }
    class IActivityMonitorOutput {
        <<interface>>
        +IActivityMonitorClient[] Clients
        +int MaxInitialReplayCount
        +RegisterClient(client, out added, replayInitialLogs)
        +RegisterUniqueClient~T~(tester, factory, replayInitialLogs)
        +UnregisterClient(client)
        +UnregisterClient~T~(predicate)
    }
    class IActivityMonitorClient {
        <<interface>>
        +OnUnfilteredLog(ref data)
        +OnOpenGroup(group)
        +OnGroupClosing(group, ref conclusions)
        +OnGroupClosed(group, conclusions)
        +OnTopicChanged(newTopic, fileName, lineNumber)
        +OnAutoTagsChanged(newTrait)
    }
    class IActivityMonitorBoundClient {
        <<interface>>
        +LogFilter MinimalFilter
        +bool IsDead
        +SetMonitor(source, forceBuggyRemove)
    }

    IActivityLineEmitter <|-- IStaticLogger
    IActivityLineEmitter <|-- IActivityMonitor
    IActivityLineEmitter <|-- IParallelLogger
    IActivityDependentTokenFactory <|-- IActivityMonitor
    IActivityDependentTokenFactory <|-- IParallelLogger
    IActivityMonitorClient <|-- IActivityMonitorBoundClient

    IActivityMonitor --> IActivityMonitorOutput : Output
    IActivityMonitor --> IParallelLogger : ParallelLogger
    IActivityMonitorOutput --> IActivityMonitorClient : Clients
```

`ActualFilter` appears twice on purpose. On `IActivityLineEmitter` it is a `LogLevelFilter`, because at
that level only lines exist. `IActivityMonitor` shadows it with a `LogFilter`, which is the
`{Line,Group}` couple of `LogLevelFilter` - a monitor filters groups too.


## What this package contains

The core abstractions and the default `ActivityMonitor` implementation, plus:

- Standard but basic [Clients](Client/README.md).
- The `LogFile` static class that exposes the `RootLogPath` property.
- The [EventMonitoredArgs](EventMonitoredArgs.cs), an `EventArgs` carrying a monitor.
- The [AsyncLock](AsyncLock.md), which detects, handles or rejects asynchronous lock reentrancy without
  any awful [AsyncLocal](https://docs.microsoft.com/en-us/dotnet/api/system.threading.asynclocal-1),
  thanks to the ubiquitous `IActivityMonitor` parameter.
- The [StaticGate](StaticGate/README.md), to control log emission optimally - in a hot path it removes
  the logging overhead entirely while keeping the ability to turn it back on.
- [`ActivityMonitor.StaticLogger`](ActivityMonitor/ActivityMonitor.StaticLogger.cs), a static
  contextless API for the low level zones that have no monitor yet - a timer callback, for instance.
  Its events carry the monitor identifier `§§§§` - `ActivityMonitor.StaticLogMonitorUniqueId`; the
  distinct `§ext` is the identifier of the *external* logger, not this one. They are not seen by the
  monitor output clients: they are raised on the static `ActivityMonitor.OnStaticLog` event, so collecting them requires an external
  subscriber.
- [`ActivityMonitorLogData`](ActivityMonitorLogData.md), the log entry itself, and
  [tag filtering](ActivityMonitor/TagFiltering.md).
- The [DotNetEventSource](DotNetEventSource/README.md) bridge.

`StaticLogger` exposes no `OpenGroup`: `IStaticLogger` derives from `IActivityLineEmitter`, which has
no group member. Note that it goes further than "not offered" - the implementation passes
`isOpenGroup: false` unconditionally, so the `isOpenGroup` argument of the line-emitter contract is
silently ignored. Without a monitor to follow the execution path, an open and a close from two
different call sites would interleave with no way to tell which close matches which open.

## Topics

A monitor has a Topic that aims to describe what it is, or what it is currently doing. The constructor
can initialize it - `m = new ActivityMonitor( "My topic" )` - and `SetTopic( "My new topic." )` changes
it at any time.

Setting a topic does three things, not one: it stores the value (the `Topic` property), it notifies
every client through the dedicated `IActivityMonitorClient.OnTopicChanged` callback, and it sends a
log line carrying a special tag. So a topic is both queryable at any time and visible in the log
stream at the exact point it changed.
