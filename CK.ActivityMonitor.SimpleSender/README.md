# CK.ActivityMonitor.SimpleSender

The extension methods that actually send logs. `CK.ActivityMonitor` defines the monitor and its
clients; this package is what you call.

## Emitting logs

The `IActivityMonitor` is not a singleton, each ActivityMonitor instance must follow the execution
path. It can be a Scoped dependency for root objects, and for the vast majority of interactions, it
appears as an explicit method parameter. Cumbersome? Not that much actually but clear, explicit and
bug-free.

Create a new `CK.Core.ActivityMonitor` and start sending logs thanks to all the extension methods that
help to:

- Send a line with a given [LogLevel](../CK.ActivityMonitor/LogLevel.cs): Debug, Trace, Info, Warn,
  Error, Fatal.
- Open a group of logs (see all the available overloads
  [here](ActivityMonitorSimpleSenderExtension.Group.cs) and
  [here](ActivityMonitorSimpleSenderExtension.Group-Gen.cs)).

```csharp
using CK.Core;
public class MyClass
{
    public void MyMethod()
    {
        IActivityMonitor m = new ActivityMonitor();

        using( m.OpenInfo("My group") )
        {
            m.Debug( "My Debug log line" );
            m.Trace( "My Trace log line" );
            m.Info( "My Info log line" );
            m.Warn( "My Warn log line" );
            m.Error( "My Error log line" );
            m.Fatal( "My Fatal log line" );
        }
    }
}
```

A realistic loop, where the group structure is the point - the story of the run is readable from its
shape alone:

```csharp
var m = new ActivityMonitor();
int onError = 0, onSuccess = 0;
foreach( var f in Directory.GetFiles( Environment.CurrentDirectory ) )
{
    using( m.OpenTrace( $"Processing file '{f}'." ) )
    {
        try
        {
            if( ProcessFile( m, f ) ) ++onSuccess;
            else ++onError;
        }
        catch( Exception ex )
        {
            m.Error( $"Unhandled error while processing file '{f}'. Continuing.", ex );
            ++onError;
        }
    }
}
m.Info( $"Done: {onSuccess} files succeed and {onError} failed." );
```

## The interpolated message is not always evaluated

This is the reason the package exists in its current form. Thanks to the C# 10
[interpolated handlers](https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-10.0/improved-interpolated-strings#the-handler-pattern),
the sender can **skip the evaluation of the interpolated message** based on the log level and tags -
see [tag filtering](../CK.ActivityMonitor/ActivityMonitor/TagFiltering.md).

So `m.Debug( $"Ticks: {ticks} for '{f}'." )` costs nothing when Debug is filtered out: the string is
never built. Writing the same thing as `m.Debug( "Ticks: " + ticks + ...)` defeats it - the
concatenation happens before the call.

> An older, less intuitive set of "standard" extension methods used to exist, relying on a two-step
> approach precisely to avoid that cost. **Those are deprecated, not this package** - the interpolated
> handlers made them unnecessary.

## Bonus of the interpolated strings: .Net Type names format

We often have to log type names. Name types are not that easy: in .Net a Type has 3 different names.
Take a (stupid) nested generic class `class Nested<T> {}` and the actual type
`Nested<Dictionary<int, (string, int?)>>`. Its three .NET names are, respectively, the compact-ish
`ToString()` form with backticks and bracket nesting, the `FullName` that repeats the full assembly
identity of every type argument, and the `AssemblyQualifiedName` that adds the outer assembly on top -
the last two run to several hundred characters for this single type.

A `monitor.Warn( $"Type is {t}." )` uses the `ToString()` form. This is the "natural" C# default so we
won't change it even if, as above, it's not very satisfying.

Type *formats* give something readable. With `"C"`:

```
monitor.Warn( $"Type is {t:C}." );
Type is LogTextHandlerTests.Nested<Dictionary<int,(string,int?)>>.
```

With `"N"`, namespaces appear:

```
monitor.Warn( $"Type is {t:N}." );
Type is CK.Core.Tests.Monitoring.LogTextHandlerTests.Nested<System.Collections.Generic.Dictionary<int,(string,int?)>>.
```

| Format | Result |
|---|---|
| C | Compact, no namespace C# Type names. |
| N | C# Type names with their namespaces. |
| F | Type's FullName. |
| A | Type's AssemblyQualifiedName. |

The "C" or "N" definitely helps while reading logs. These names come from the
`Type.ToCSharpName( bool withNamespace = true, bool typeDeclaration = true, bool useValueTupleParentheses = true )`
extension method.

One asymmetry to remember: **when a type format is used, whatever it is, a `null` type logs as
`null`**; without a format it produces the empty string. Both behaviours are pinned by
`LogTextHandlerTests`.

## A note on the generated files

About four fifths of the C# in this package is generated from T4 templates (`*-Gen.tt` producing
`*-Gen.cs`, some 3500 lines of 4500): the overload sets
for lines and groups are combinatorial - level, tags, exception, interpolated or not, caller file and
line capture. Edit the `.tt`, never the `.cs`.
