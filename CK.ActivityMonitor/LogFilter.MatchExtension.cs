
using System;

namespace CK.Core;

/// <summary>
/// Supports <see cref="LogFilter"/>, <see cref="LogClamper"/> and <see cref="LogLevelFilter"/> extension methods.
/// </summary>
public static class LogFilterMatcherExtension
{
    /// <summary>
    /// Matches a <see cref="LogClamper"/>: it can be a predefined filter (like "Undefined", "Debug", "Verbose", etc.)  
    /// or as {GroupLogLevelFilter,LineLogLevelFilter} pairs like "{None,None}", "{Error,Trace}" and an
    /// optional '!' suffix for the <see cref="LogClamper.Clamp"/>.
    /// </summary>
    /// <param name="m">This matcher.</param>
    /// <param name="c">Resulting clamper.</param>
    /// <returns>True on success, false on error.</returns>
    public static bool TryMatchLogClamper( this ref ROSpanCharMatcher m, out LogClamper c )
    {
        c = LogClamper.Undefined;
        if( !m.TryMatchLogFilter( out var f ) ) return false;
        c = new LogClamper( f, m.Head.TryMatch( '!' ) );
        return true;
    }

    /// <summary>
    /// Tries to match a <see cref="LogFilter"/>: it can be a predefined filter (like "Undefined", "Debug", "Verbose", etc.)  
    /// or as {GroupLogLevelFilter,LineLogLevelFilter} pairs like "{None,None}", "{Error,Trace}".
    /// </summary>
    /// <param name="m">This matcher.</param>
    /// <param name="f">Resulting filter.</param>
    /// <returns>True on success, false on error.</returns>
    public static bool TryMatchLogFilter( this ref ROSpanCharMatcher m, out LogFilter f )
    {
        f = LogFilter.Undefined;
        using( m.OpenExpectations( "LogFilter" ) )
        {
            if( !m.TryMatch( "Undefined", StringComparison.OrdinalIgnoreCase ) )
            {
                if( m.TryMatch( "Diagnostic", StringComparison.OrdinalIgnoreCase ) || m.TryMatch( "Debug", StringComparison.OrdinalIgnoreCase ) )
                {
                    Throw.DebugAssert( LogFilter.Diagnostic == LogFilter.Debug );
                    f = LogFilter.Diagnostic;
                }
                else if( m.TryMatch( "Detailed", StringComparison.OrdinalIgnoreCase ) || m.TryMatch( "Trace", StringComparison.OrdinalIgnoreCase ) )
                {
                    Throw.DebugAssert( LogFilter.Detailed == LogFilter.Trace );
                    f = LogFilter.Detailed;
                }
                else if( m.TryMatch( "Verbose", StringComparison.OrdinalIgnoreCase ) )
                {
                    f = LogFilter.Verbose;
                }
                else if( m.TryMatch( "Normal", StringComparison.OrdinalIgnoreCase ) )
                {
                    f = LogFilter.Normal;
                }
                else if( m.TryMatch( "Monitor", StringComparison.OrdinalIgnoreCase ) )
                {
                    f = LogFilter.Monitor;
                }
                else if( m.TryMatch( "Minimal", StringComparison.OrdinalIgnoreCase ) || m.TryMatch( "Terse", StringComparison.OrdinalIgnoreCase ) )
                {
                    Throw.DebugAssert( LogFilter.Minimal == LogFilter.Terse );
                    f = LogFilter.Terse;
                }
                else if( m.TryMatch( "Quiet", StringComparison.OrdinalIgnoreCase ) || m.TryMatch( "Release", StringComparison.OrdinalIgnoreCase ) )
                {
                    Throw.DebugAssert( LogFilter.Quiet == LogFilter.Release );
                    f = LogFilter.Quiet;
                }
                else if( m.TryMatch( "Fatal", StringComparison.OrdinalIgnoreCase ) )
                {
                    f = LogFilter.Fatal;
                }
                else if( m.TryMatch( "Invalid", StringComparison.OrdinalIgnoreCase ) )
                {
                    f = LogFilter.Invalid;
                }
                else
                {
                    var savedHead = m.Head;
                    if( m.TryMatch( '{' )
                        && m.TrySkipWhiteSpaces( 0 )
                        && m.TryMatchLogLevelFilter( out var group )
                        && m.TrySkipWhiteSpaces( 0 )
                        && m.TryMatch( ',' )
                        && m.TrySkipWhiteSpaces( 0 )
                        && m.TryMatchLogLevelFilter( out var line )
                        && m.TrySkipWhiteSpaces( 0 )
                        && m.TryMatch( '}' ) )
                    {
                        f = new LogFilter( group, line );
                    }
                    else
                    {
                        m.Head = savedHead;
                        return false;
                    }
                }
            }
            return m.SetSuccess();
        }
    }

    /// <summary>
    /// Tries to match a <see cref="LogLevelFilter"/>.
    /// </summary>
    /// <param name="m">This matcher.</param>
    /// <param name="level">Resulting level.</param>
    /// <returns>True on success, false on error.</returns>
    public static bool TryMatchLogLevelFilter( this ref ROSpanCharMatcher m, out LogLevelFilter level )
        => m.Head.TryMatchLogLevelFilter( out level )
            ? m.SetSuccess()
            : m.AddExpectation( "LogLevelFilter" );

    /// <summary>
    /// Tries to match a <see cref="LogLevelFilter"/>.
    /// </summary>
    /// <param name="head">This head.</param>
    /// <param name="level">Resulting level.</param>
    /// <returns>True on success, false on error.</returns>
    public static bool TryMatchLogLevelFilter( this ref ReadOnlySpan<char> head, out LogLevelFilter level )
    {
        level = LogLevelFilter.None;
        if( !head.TryMatch( "None", StringComparison.OrdinalIgnoreCase ) )
        {
            if( head.TryMatch( "Debug", StringComparison.OrdinalIgnoreCase ) )
            {
                level = LogLevelFilter.Debug;
            }
            else if( head.TryMatch( "Trace", StringComparison.OrdinalIgnoreCase ) )
            {
                level = LogLevelFilter.Trace;
            }
            else if( head.TryMatch( "Info", StringComparison.OrdinalIgnoreCase ) )
            {
                level = LogLevelFilter.Info;
            }
            else if( head.TryMatch( "Warn", StringComparison.OrdinalIgnoreCase ) )
            {
                level = LogLevelFilter.Warn;
            }
            else if( head.TryMatch( "Error", StringComparison.OrdinalIgnoreCase ) )
            {
                level = LogLevelFilter.Error;
            }
            else if( head.TryMatch( "Fatal", StringComparison.OrdinalIgnoreCase ) )
            {
                level = LogLevelFilter.Fatal;
            }
            else if( head.TryMatch( "Invalid", StringComparison.OrdinalIgnoreCase ) )
            {
                level = LogLevelFilter.Invalid;
            }
            else return false;
        }
        return true;
    }
}
