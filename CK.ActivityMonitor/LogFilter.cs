using System;
using System.Runtime.CompilerServices;

namespace CK.Core;

/// <summary>
/// Immutable capture of a double <see cref="LogLevelFilter"/>. One for <see cref="Line"/> and one for <see cref="Group"/>.
/// This value type exposes predefined configured couples: <see cref="Debug"/>, <see cref="Trace"/> (full trace), <see cref="Verbose"/>, <see cref="Monitor"/>, 
/// <see cref="Terse"/>, <see cref="Release"/> and <see cref="Fatal"/> (absolute minimal logging).
/// <para>
/// Combining two filters <see cref="Combine(LogFilter)"/> lowers it so that both can be satisfied. 
/// </para>
/// </summary>
[System.ComponentModel.TypeConverter( typeof( LogFilterTypeConverter ) )]
[Serializable]
public readonly struct LogFilter : IEquatable<LogFilter>
{
    /// <summary>
    /// Undefined filter is <see cref="LogLevelFilter.None"/> for both <see cref="Line"/> and <see cref="Group"/>.
    /// This is the same as using the default constructor for this structure (it is exposed here for clarity).
    /// </summary>
    static public readonly LogFilter Undefined = default;

    /// <summary>
    /// Debug filter enables full <see cref="LogLevelFilter.Debug"/> for both <see cref="Group"/> and <see cref="Line"/>.
    /// </summary>
    static public readonly LogFilter Debug = new LogFilter( LogLevelFilter.Debug, LogLevelFilter.Debug );

    /// <summary>
    /// Trace filter enables full <see cref="LogLevelFilter.Trace"/> for both <see cref="Group"/> and <see cref="Line"/>.
    /// </summary>
    static public readonly LogFilter Trace = new LogFilter( LogLevelFilter.Trace, LogLevelFilter.Trace );

    /// <summary>
    /// Verbose <see cref="LogLevelFilter.Trace"/> all <see cref="Group"/>s but limits <see cref="Line"/> to <see cref="LogLevelFilter.Info"/> level.
    /// </summary>
    static public readonly LogFilter Verbose = new LogFilter( LogLevelFilter.Trace, LogLevelFilter.Info );

    /// <summary>
    /// While monitoring all <see cref="Group"/>s appear (Trace) to get the detailed structure of the activity but for <see cref="Line"/> only
    /// errors and warnings are captured.
    /// </summary>
    static public readonly LogFilter Monitor = new LogFilter( LogLevelFilter.Trace, LogLevelFilter.Warn );

    /// <summary>
    /// Terse filter limits <see cref="Group"/>s to <see cref="LogLevelFilter.Info"/> and captures errors and warnings for <see cref="Line"/>.
    /// </summary>
    static public readonly LogFilter Terse = new LogFilter( LogLevelFilter.Info, LogLevelFilter.Warn );

    /// <summary>
    /// Release filter captures only <see cref="LogLevelFilter.Error"/>s for both <see cref="Group"/> and <see cref="Line"/>.
    /// </summary>
    static public readonly LogFilter Release = new LogFilter( LogLevelFilter.Error, LogLevelFilter.Error );

    /// <summary>
    /// Fatal filter is the strongest: {Fatal,Fatal}.
    /// </summary>
    static public readonly LogFilter Fatal = new LogFilter( LogLevelFilter.Fatal, LogLevelFilter.Fatal );

    /// <summary>
    /// Diagnostic is {Debug,Debug}.
    /// See https://learn.microsoft.com/en-us/dotnet/standard/commandline/syntax#the---verbosity-option.
    /// </summary>
    static public LogFilter Diagnostic => Debug;

    /// <summary>
    /// Detailed is {Trace,Trace}.
    /// See https://learn.microsoft.com/en-us/dotnet/standard/commandline/syntax#the---verbosity-option.
    /// </summary>
    static public LogFilter Detailed => Trace;

    /// <summary>
    /// Normal is {Info,Info}.
    /// See https://learn.microsoft.com/en-us/dotnet/standard/commandline/syntax#the---verbosity-option.
    /// </summary>
    static public LogFilter Normal => new LogFilter( LogLevelFilter.Info, LogLevelFilter.Info );

    /// <summary>
    /// Minimal is {Info,Warn}.
    /// See https://learn.microsoft.com/en-us/dotnet/standard/commandline/syntax#the---verbosity-option.
    /// </summary>
    static public LogFilter Minimal => Terse;

    /// <summary>
    /// Quiet is {Error,Error}.
    /// See https://learn.microsoft.com/en-us/dotnet/standard/commandline/syntax#the---verbosity-option.
    /// </summary>
    static public LogFilter Quiet => Release;

    /// <summary>
    /// Invalid must be used as a special value. It is <see cref="LogLevelFilter.Invalid"/> for both <see cref="Line"/> and <see cref="Group"/>.
    /// </summary>
    static public readonly LogFilter Invalid = new LogFilter( LogLevelFilter.Invalid, LogLevelFilter.Invalid );

    /// <summary>
    /// The filter that applies to log lines (Debug, Trace, Info, Warn, Error and Fatal). 
    /// </summary>
    public readonly LogLevelFilter Line;

    /// <summary>
    /// The filter that applies to groups. 
    /// </summary>
    public readonly LogLevelFilter Group;

    /// <summary>
    /// Initializes a new <see cref="LogFilter"/> with a level for <see cref="Group"/>s and <see cref="Line"/> logs.
    /// </summary>
    /// <param name="group">Filter for groups.</param>
    /// <param name="line">Filter for lines.</param>
    public LogFilter( LogLevelFilter group, LogLevelFilter line )
    {
        Line = line;
        Group = group;
    }

    /// <summary>
    /// Combines this filter with another one. <see cref="Line"/> and <see cref="Group"/> level filters
    /// are combined with <see cref="Combine(LogLevelFilter,LogLevelFilter)"/>.
    /// </summary>
    /// <param name="other">The other filter to combine with this one.</param>
    /// <returns>The resulting filter.</returns>
    public LogFilter Combine( LogFilter other )
    {
        return new LogFilter( Combine( Group, other.Group ), Combine( Line, other.Line ) );
    }

    /// <summary>
    /// Combines this filter with another one only if <see cref="Line"/> or <see cref="Group"/> is <see cref="LogLevelFilter.None"/>.
    /// </summary>
    /// <param name="other">The other filter to combine with this one.</param>
    /// <returns>The resulting filter.</returns>
    public LogFilter CombineNoneOnly( LogFilter other )
    {
        var l = Line == LogLevelFilter.None ? other.Line : Line;
        var g = Group == LogLevelFilter.None ? other.Group : Group;
        return new LogFilter( g, l );
    }
    /// <summary>
    /// Gets whether this filter is None or Debug for both line and group.
    /// </summary>
    public bool IsNoneOrDebug => (int)Line <= 1 && (int)Group <= 1;

    /// <summary>
    /// Returns a <see cref="LogFilter"/> with a given <see cref="LogLevelFilter"/> for the <see cref="Line"/>.
    /// </summary>
    /// <param name="line">Filter for the line.</param>
    /// <returns>The filter with the line level.</returns>
    public LogFilter SetLine( LogLevelFilter line )
    {
        return new LogFilter( Group, line );
    }

    /// <summary>
    /// Returns a <see cref="LogFilter"/> with a given <see cref="LogLevelFilter"/> for the <see cref="Group"/>.
    /// </summary>
    /// <param name="group">Filter for the group.</param>
    /// <returns>The filter with the group level.</returns>
    public LogFilter SetGroup( LogLevelFilter group )
    {
        return new LogFilter( group, Line );
    }

    /// <summary>
    /// Tests if <see cref="Combine(LogFilter)">combining</see> this and <paramref name="x"/> will result in a different filter than x.
    /// </summary>
    /// <param name="x">The other filter.</param>
    /// <returns>True if combining this filter and <paramref name="x"/> will change x.</returns>
    [MethodImpl( MethodImplOptions.AggressiveInlining )]
    public bool HasImpactOn( LogFilter x )
    {
        return (Line != LogLevelFilter.None && Line < x.Line) || (Group != LogLevelFilter.None && Group < x.Group);
    }

    /// <summary>
    /// Combines two enums <see cref="LogLevelFilter"/> into one.
    /// The resulting filter is the more verbose one (the smallest level). 
    /// <see cref="LogLevelFilter.Invalid"/> is considered as <see cref="LogLevelFilter.None"/> (it has no impact).
    /// This operation is commutative and associative: different order of combination always give the same result.
    /// </summary>
    /// <param name="x">First filter level.</param>
    /// <param name="y">Second filter level.</param>
    /// <returns>The resulting level.</returns>
    [MethodImpl( MethodImplOptions.AggressiveInlining )]
    static public LogLevelFilter Combine( LogLevelFilter x, LogLevelFilter y )
    {
        if( x <= 0 ) return y;
        if( y <= 0 ) return x;
        if( y < x ) return y;
        return x;
    }

    /// <summary>
    /// Overridden to compare <see cref="Line"/> and <see cref="Group"/>.
    /// </summary>
    /// <param name="obj">Other object.</param>
    /// <returns>True if Line and Group are equal.</returns>
    public override bool Equals( object? obj ) => obj is LogFilter filter && Equals( filter );

    /// <summary>
    /// <see cref="Line"/> and <see cref="Group"/> must be the same.
    /// </summary>
    /// <param name="x">Other filter.</param>
    /// <returns>True if Line and Group are equal.</returns>
    public bool Equals( LogFilter x ) => x.Line == Line && x.Group == Group;

    /// <summary>
    /// Overridden to compute hash based on <see cref="Line"/> and <see cref="Group"/> values.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => ((int)Line) << 16 + (int)Group;

    /// <summary>
    /// Overridden to show the group and the line level.
    /// </summary>
    /// <returns>A {group,line} string.</returns>
    public override string ToString()
    {
        if( this == LogFilter.Undefined ) return "Undefined";
        if( this == LogFilter.Debug ) return "Debug";
        if( this == LogFilter.Trace ) return "Trace";
        if( this == LogFilter.Verbose ) return "Verbose";
        if( this == LogFilter.Monitor ) return "Monitor";
        if( this == LogFilter.Terse ) return "Terse";
        if( this == LogFilter.Release ) return "Release";
        if( this == LogFilter.Fatal ) return "Fatal";
        if( this == LogFilter.Invalid ) return "Invalid";
        return $"{{{Group},{Line}}}";
    }

    /// <summary>
    /// Equality operator.
    /// </summary>
    /// <param name="x">First filter.</param>
    /// <param name="y">Second filter.</param>
    /// <returns>True if <see cref="Line"/> and <see cref="Group"/> are the same for the two filters.</returns>
    public static bool operator ==( LogFilter x, LogFilter y )
    {
        return x.Line == y.Line && x.Group == y.Group;
    }

    /// <summary>
    /// Inequality operator.
    /// </summary>
    /// <param name="x">First filter.</param>
    /// <param name="y">Second filter.</param>
    /// <returns>True if <see cref="Line"/> and <see cref="Group"/> are the same for the two filters.</returns>
    public static bool operator !=( LogFilter x, LogFilter y )
    {
        return x.Line != y.Line || x.Group != y.Group;
    }

    /// <summary>
    /// Parses the filter: it can be a predefined filter as ("Undefined", "Debug", "Trace", "Verbose", etc.) 
    /// or as {GroupLogLevelFilter,LineLogLevelFilter} pairs like "{None,None}", "{Error,Debug}".
    /// </summary>
    /// <param name="s">
    /// Predefined filter as (Undefined, Debug, Verbose, etc.) or as {GroupLogLevelFilter,LineLogLevelFilter} like {None,None}, {Error,Trace}.
    /// Must not be null.
    /// </param>
    /// <returns>The filter.</returns>
    public static LogFilter Parse( string s )
    {
        if( !TryParse( s, out var f ) ) Throw.ArgumentException( $"Invalid LogFilter: '{s}'.", nameof( s ) );
        return f;
    }

    /// <summary>
    /// Tries to parse a <see cref="LogFilter"/>: it can be a predefined filter as ("Undefined", "Debug", "Verbose", etc.)  
    /// or as {GroupLogLevelFilter,LineLogLevelFilter} pairs like "{None,None}", "{Error,Trace}".
    /// </summary>
    /// <param name="s">The string to parse.</param>
    /// <param name="f">Resulting filter.</param>
    /// <returns>True on success, false on error.</returns>
    public static bool TryParse( string s, out LogFilter f )
    {
        Throw.CheckNotNullArgument( s );
        var m = new ROSpanCharMatcher( s ) { SingleExpectationMode = true };
        return m.TryMatchLogFilter( out f ) && m.Head.IsEmpty;
    }

}
