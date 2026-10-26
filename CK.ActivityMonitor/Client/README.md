# ActivityMonitor clients

The `IActivityMonitor` is the collector of the logs that
are routed to any number of clients that can be registered onto its [Output](../CoreModel/IActivityMonitorOutput.cs).

The design of the Output and its Clients allows very different kind of
clients to coexist, that can support funny patterns like counting errors that may
occur anywhere in subordinated calls:

```csharp
int errorCount = 0;
using( monitor.OnError( () => ++errorCount ) )
{
    monitor.Info( "This is not an error." );
    await SafeCodeAsync( monitor );
    monitor.Error( "Ouch! (I'm the only error)." );
}
errorCount.ShouldBe( 1 );

```

The `OnError` extension method above registers an `ErrorTracker` client - a small client nested
in [ActivityMonitorExtension](../ActivityMonitor/ActivityMonitorExtension.cs) - onto the `Output` and unregisters it
when leaving the using scope. Overloads accept an `Action<string>` to receive the error message, and a distinct
action for `Fatal`. When the numbers matter rather than the reaction, the
[ActivityMonitorErrorCounter](ActivityMonitorErrorCounter.cs) client counts the fatals, errors and warnings and can
automatically conclude the closed groups with the totals.

Other clients like the [ColoredActivityMonitorConsoleClient](ColoredActivityMonitorConsoleClient.cs) routes
and/or displays the received stream of logs:

![Colored console sample](../docs/ColoredConsoleOutput.png)

```csharp
static void Main( string[] args )
{
    var m = new ActivityMonitor( topic: "I'm full of colors (and I'm playing with levels and filters)." );

    m.Output.RegisterClient( new ColoredActivityMonitorConsoleClient() );

    m.MinimalFilter = LogFilter.Terse;
    using( m.OpenInfo( $"Let's go: the actual filter is {m.ActualFilter}." ) )
    {
        m.Info( "In Terse filter, only Info (and above) groups and only Warn, Error or Fatal lines are logged. So you won't see this." );

        using( m.OpenError( "A whole group of error." ) )
        {
            m.Debug( "Debug is the lowest level." );
            m.Debug( $"In an Error or Fatal group, the minimal filter is automatically set to {m.ActualFilter} (that's why you can see this line)." );
            m.Trace( "Trace is low level." );
            m.Info( "Info is interesting." );
            m.Warn( "Warning is not serious." );
            m.Error( "Error should be investigated." );
            m.Fatal( "Fatal ruins the process." );

            m.CloseGroup( "This concludes the error (the using } close below will do nothing)." );
        }
        m.MinimalFilter = LogFilter.Verbose;
        m.Info( "Verbose filter let's the Info lines be logged." );
        m.Info( "Note that closing a group restores the minimal filter level that was set on the opening." );
    }
    Throw.DebugAssert( m.ActualFilter == LogFilter.Terse );
    m.MinimalFilter = LogFilter.Monitor;
    m.Warn( "Using Monitor filter, only Warn, Error and Fatal lines are logged." );

    System.Console.ReadLine();
}
```

This sample is available in the [CK-Sample-Monitoring](https://github.com/signature-opensource/CK-Sample-Monitoring/tree/stable) repository.

## Using a client to assert on what the code logged

The local client architecture enables an interesting pattern for tests. [`CollectTexts`](../ActivityMonitor/ActivityMonitorExtension.cs) registers a
client that captures what was logged, so the assertion is on the *story* the code told, not on its
return values. This is
[`demo_using_CollectTexts`](../../Tests/CK.ActivityMonitor.Tests/DocumentationCodeSnippets.cs), verbatim:

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

`ActionEvent` is an [`EventMonitoredArgs`](../EventMonitoredArgs.cs), which is what lets the handler log
into the monitor of the event rather than into one it captured.
