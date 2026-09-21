# Tag based Log Filtering

## The basics of levels, filters and tags.

First, a log line or group has a [`LogLevel`](../LogLevel.cs): `Debug`, `Trace`, `Info`, `Warn`, `Error`, `Fatal` (in this order).
This is a simple enum and this is decided by the developer (by the method she calls to emit it).

A [`LogLevelFilter`](../LogLevelFilter.cs) is another simple enum that defines a filter for such a level (`None`, `Debug`…`Fatal`).
Since there’s two kind of logs in the ActivityMonitor: Groups (`using( monitor.OpenTrace( … ) )`, etc.) and Line (`monitor.Debug( … )`, etc.),
a [`LogFilter`](../LogFilter.cs) (a simple struct) defines two `LogLevelFilter`: one for Group and one for Line.

LogFilter can be expressed as **{Group,Line}** strings like `"{Error,Trace}"` or predefined couples `"Debug"` is `"{Debug,Debug}"`,
`"Verbose"` is `"{Trace,Info}"` (see the [code](../LogFilter.cs)).

`LogFilter` is intensively used by the ActivityMonitor since they have a nice property: they can be combined together with
a simple rule. Combining 2 LogFilters results in a `LogFilter` that satisfies both of them: “{Error,Trace}” combined
with “{Warn,Warn}” is “{Warn,Trace}”.

They are used as **“MinimalFilters”**: an ActivityMonitor combines **multiple MinimalFilters** into one **ActualFilter**:
-	There is a `MinimalFilter` property on the `IActivityMonitor` itself. 
    A developer can set it at any time to request that the logs with the provided levels will be emitted.
-	ActivityMonitor clients can expose a MinimalFilter that participates to the final ActualFilter: any monitor 
    Client can ensure that it will receive the given logs level.

MinimalFilters lowers the level filters (from Fatal down to Debug) and a log is emitted if it’s at least equal to
the final monitor’s ActualFilter’s Group or Line filter.

This is how it works and this is logically sound (I skip here the “Undefined” filter that is ultimately resolved
by the static `ActivityMonitor.DefaultFilter` property).

A [`LogClamper`](../LogClamper.cs) is a Filter (that de facto can act as a MinimalFilter) with a `Clamp` boolean: when
Clamp is false it is just like a MinimaFilter. But when Clamp is true, it “cuts” the level.
A simple optional ‘!’ suffix to the LogFilter expresses it. For instance: 
-	“Minimal!” (same as “{Info,Warn}!”): only Warnings lines will be emitted (not Info, Trace or Debug).
-	“{Warn,Trace}!” will only emit Groups with Warn, Error or Fatal and Debug Lines will not be emitted.

Two `LogClamper` cannot be combined like `LogFilter` since a true `Clamp` cannot be reconciled.

Now the tags. Tags are `CKTrait` (from CK.Core): a tag is a set of 0 to n strings that is highly normalized (reference equality)
and provides set operations (Union, Intersection, Except, SymmetricExcept) in O(n).
A log can be for instance tagged with “Sql|Machine|Dangerous|Facebook” (tags can be combined with “|” in the string
or via the c# | operator). (Note: more performance improvements are possible here. You’re welcome 😉)

## TagFilter

A TagFilter is a `ValueTuple<CKTrait,LogClamper>` associates a Tag (that can of course be multiple) and a `LogClamper`.

The static [ActivityMonitor.Tags](ActivityMonitor.Tags.cs) maintains and exposes 2 list of TagFilters:

```csharp
  /// <summary>
  /// Gets the current filters that are used to filter the logs.
  /// </summary>
  public static IReadOnlyList<(CKTrait T, LogClamper F)> Filters => _finalFilters;

  /// <summary>
  /// Gets the current default filters.
  /// These default filters appears at the bottom of the <see cref="Filters"/> (possibly optimized).
  /// </summary>
  public static IReadOnlyList<(CKTrait T, LogClamper F)> DefaultFilters => _defaultFilters;
```
The `Filters` are challenged before calling the string interpolation.

Static thread-safe methods are available to modify these 2 lists: 

```csharp
  /// <summary>
  /// Clears existing filters (note that the <see cref="DefaultFilters"/> are kept).
  /// </summary>
  public static void ClearFilters();

  /// <summary>
  /// Adds a filter to the <see cref="Filters"/> list and returns the result
  /// that may already not be the same as Filters if another thread modified it.
  /// <para>
  /// The new filter is positioned above the existing ones: it may have removed one or more
  /// previous filters from the list if it overlaps them.
  /// </para>
  /// </summary>
  /// <param name="tag">The tag to filter.</param>
  /// <param name="c">The filter to apply.</param>
  /// <returns>The modified list of filters that is used to filter logs.</returns>
  public static IReadOnlyList<(CKTrait T, LogClamper F)> AddFilter( CKTrait tag, LogClamper c );

  /// <summary>
  /// Removes a filter from the <see cref="Filters"/> list (but not from the <see cref="DefaultFilters"/> one).
  /// <para>
  /// This removes the first occurrence (in priority order). Multiple occurrences may exist
  /// if <see cref="LogLevelFilter.None"/> is used for the line or group filters. To remove all occurrences
  /// simply loop while this returns true but recall that multiple threads can update this list concurrently.
  /// </para>
  /// </summary>
  /// <param name="tag">The exact tag for which filter must be removed.</param>
  /// <returns>True if an occurrence of the tag has been found and removed, false otherwise.</returns>
  public static bool RemoveFilter( CKTrait tag );

  /// <summary>
  /// Updates all filters at once.
  /// <para>
  /// <see cref="DefaultFilters"/> are appended to this list and any useless filters
  /// are optimized out.
  /// </para>
  /// </summary>
  /// <param name="filters">Ordered final set of tags and associated clamper including the <see cref="DefaultFilters"/>.</param>
  public static IReadOnlyList<(CKTrait T, LogClamper F)> SetFilters( (CKTrait, LogClamper)[] filters );

  /// <summary>
  /// Adds a filter to <see cref="DefaultFilters"/> list and returns the result
  /// that may already not be the same as DefaultFilters if another thread modified it.
  /// <para>
  /// The new filter is positioned above the existing ones: it may have removed one or more
  /// previous filters from the list if it overlaps them.
  /// </para>
  /// </summary>
  /// <param name="tag">The tag to filter.</param>
  /// <param name="c">The filter to apply.</param>
  /// <returns>The modified list of default filters.</returns>
  public static IReadOnlyList<(CKTrait T, LogClamper F)> AddDefaultFilter( CKTrait tag, LogClamper c );

  /// <summary>
  /// Removes a filter from the <see cref="DefaultFilters"/> list.
  /// <para>
  /// This removes the first occurrence (in priority order). Multiple occurrences may exist
  /// if <see cref="LogLevelFilter.None"/> is used for the line or group filters. To remove all occurrences
  /// simply loop while this returns true but recall that multiple threads can update this list concurrently.
  /// </para>
  /// </summary>
  /// <param name="tag">The exact tag for which filter must be removed.</param>
  /// <returns>True if an occurrence of the tag has been found and removed, false otherwise.</returns>
  public static bool RemoveDefaultFilter( CKTrait tag );
```
### Default Tag Filters
The `DefaultFilters` are typically registered by [static type initializers](https://docs.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/static-constructors)
of libraries that want, by default, that some of their logs should be filtered at a given level. Below an example
of such use:

The tag is defined by any object (here an interface) as a static readonly field or property:
```csharp
  [IsMultiple]
  public interface IDeviceHost : ISingletonAutoService
  {
      /// <summary>
      /// Log tag set on the command and any event loop monitors.
      /// This tag is configured to <see cref="LogFilter.Monitor"/> only in <see cref="ActivityMonitor.Tags.DefaultFilters"/>.
      /// </summary>
      public static CKTrait DeviceModel = ActivityMonitor.Tags.Register( "Device-Model" );

      //...
  }
``` 
And a static constructor of a class (here an internal one) configures its default behavior:
```csharp
  class DeviceHostLock
  {
      static DeviceHostLock()
      {
          ActivityMonitor.Tags.AddDefaultFilter( IDeviceHost.DeviceModel, new LogClamper( LogFilter.Monitor, true ) );
      }
  }
``` 

### Tag filtering: first subset wins

This default configuration is easily overridden by any other filter added for "Device-Model" since order matters: the
first tag in the final `Filters` array that combines the added filters and the default ones that appear in the logged
tags defines the `LogClamper` to use (in other words, the first tag's filter that is a subset of the log's tags wins).

This enables 2 scenarii depending on the boolean Clamp (the examples below uses the JSON syntax to represent the filters):
-	`[ ["Sql","Trace"] ]` will ensure that AT LEAST Trace and OpenTrace will be emitted for any log that has the "Sql" tag.
-	`[ ["Sql","Trace!"] ]` will ensure that Trace and OpenTrace will be emitted but NOT Debug for any log that has the "Sql" tag.

And when more than one TagFilters are used, the first (that is the latest added) wins:
-	`[ ["Sql","Debug"], ["Machine","Release!"] ]` will ensure all "Sql" will be logged, even if the log tag is "Sql|Machine".
-	`[ ["Machine","Release!"], ["Sql","Debug"] ]` only error "Machine" logs will be emitted, even if the "Sql" tag also 
appears. However, all logs tagged with "Sql" alone or combined with other tags than "Machine" will be emitted.

> Note: "Debug" is the same as "Debug!" since there is no more verbose level than Debug.




