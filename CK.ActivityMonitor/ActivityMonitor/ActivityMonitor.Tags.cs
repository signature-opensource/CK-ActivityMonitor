using CK.Core.Impl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CK.Core;

public sealed partial class ActivityMonitor
{
    /// <summary>
    /// Thread-safe context for tags used to categorize log entries (and group conclusions).
    /// All tags used in monitoring must be <see cref="Register"/>ed here.
    /// <para>
    /// This also manages tags filtering.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Tags used for conclusions should start with "c:".
    /// </remarks>
    public static class Tags
    {
        /// <summary>
        /// The central, unique, context of all monitoring related tags used in the application domain.
        /// </summary>
        public static readonly CKTraitContext Context;

        /// <summary>
        /// Shortcut to <see cref="CKTraitContext.EmptyTrait">Context.EmptyTrait</see>.
        /// </summary>
        static public readonly CKTrait Empty;

        /// <summary>
        /// Creation of <see cref="Token"/> are marked with "CreateToken".
        /// </summary>
        static public readonly CKTrait CreateToken;

        /// <summary>
        /// Start of dependent activities are marked with "StartActivity".
        /// </summary>
        static public readonly CKTrait StartActivity;

        /// <summary>
        /// Used to signal a weird behavior that should be investigated.
        /// </summary>
        static public readonly CKTrait ToBeInvestigated;

        /// <summary>
        /// Conclusions provided to IActivityMonitor.Close(string) are marked with "c:User".
        /// </summary>
        static public readonly CKTrait UserConclusion;

        /// <summary>
        /// Conclusions returned by the optional function when a group is opened (see <see cref="IActivityMonitor.UnfilteredOpenGroup"/>) are marked with "c:GetText".
        /// </summary>
        static public readonly CKTrait GetTextConclusion;

        /// <summary>
        /// Whenever <see cref="Topic"/> changed, a <see cref="LogLevel.Info"/> is emitted marked with "TopicChanged".
        /// </summary>
        static public readonly CKTrait TopicChanged;

        /// <summary>
        /// A "MonitorEnd" tag is emitted by <see cref="ActivityMonitorExtension.MonitorEnd"/>.
        /// This indicates the logical end of life of the monitor. It should not be used anymore (but technically can
        /// be used).
        /// </summary>
        static public readonly CKTrait MonitorEnd;

        /// <summary>
        /// A "InternalMonitor" tag is used while replaying <see cref="IActivityMonitorImpl.InternalMonitor"/>
        /// logs.
        /// </summary>
        static public readonly CKTrait InternalMonitor;

        /// <summary>
        /// A "StackTrace" tag activates stack trace tracking and dumping when a concurrent access is detected.
        /// logs.
        /// </summary>
        static public readonly CKTrait StackTrace;

        /// <summary>
        /// Tag that describes a critical security info: any log tagged with this MUST NOT be sent to
        /// any remote. Such logs must be displayed locally and collected only by local stores.
        /// </summary>
        static public readonly CKTrait SecurityCritical;

        /// <summary>
        /// Simple shortcut to <see cref="CKTraitContext.FindOrCreate(string)"/>.
        /// </summary>
        /// <param name="tags">Atomic tag or multiple tags separated by pipes (|).</param>
        /// <returns>Registered tags.</returns>
        static public CKTrait Register( string tags ) => Context.FindOrCreate( tags );

        static Tags()
        {
            Context = CKTraitContext.Create( "ActivityMonitor" );
            Empty = Context.EmptyTrait;
            TopicChanged = Context.FindOrCreate( "TopicChanged" );
            CreateToken = Context.FindOrCreate( "CreateToken" );
            StartActivity = Context.FindOrCreate( "StartActivity" );
            MonitorEnd = Context.FindOrCreate( "MonitorEnd" );
            ToBeInvestigated = Context.FindOrCreate( "ToBeInvestigated" );
            SecurityCritical = Context.FindOrCreate( "SecurityCritical" );
            InternalMonitor = Context.FindOrCreate( "InternalMonitor" );

            StackTrace = Context.FindOrCreate( "StackTrace" );
            UserConclusion = Context.FindOrCreate( "c:User" );
            GetTextConclusion = Context.FindOrCreate( "c:GetText" );
            _finalFiltersLockAndEmptyArray = _defaultFilters = _filters = _finalFilters = Array.Empty<(CKTrait, LogClamper)>();
        }

        static (CKTrait T, LogClamper F)[] _filters;
        static (CKTrait T, LogClamper F)[] _defaultFilters;
        static (CKTrait T, LogClamper F)[] _finalFilters;
        // We take no risk here: since final filters are updated by filters and defaultFilters
        // we may (very unlikely) have a race condition if we use interlocked functions so we use a lock
        // (that is also the empty array).
        static readonly (CKTrait T, LogClamper F)[] _finalFiltersLockAndEmptyArray;

        /// <summary>
        /// Gets the current filters that are used to filter the logs.
        /// </summary>
        public static IReadOnlyList<(CKTrait T, LogClamper F)> Filters => _finalFilters;

        /// <summary>
        /// Gets the current default filters.
        /// These default filters appears at the bottom of the <see cref="Filters"/> (possibly optimized).
        /// </summary>
        public static IReadOnlyList<(CKTrait T, LogClamper F)> DefaultFilters => _defaultFilters;

        /// <summary>
        /// Clears existing filters (note that the <see cref="DefaultFilters"/> are kept).
        /// </summary>
        public static void ClearFilters()
        {
            Interlocked.Exchange( ref _filters, _finalFiltersLockAndEmptyArray );
            Interlocked.Exchange( ref _finalFilters, _defaultFilters );
        }

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
        public static IReadOnlyList<(CKTrait T, LogClamper F)> AddFilter( CKTrait tag, LogClamper c )
        {
            Util.InterlockedSet( ref _filters, (tag, c), ( filters, f ) => Add( filters, f ) );
            return UpdateFinalFilters();
        }

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
        public static bool RemoveFilter( CKTrait tag )
        {
            if( InterlockedRemove( ref _filters, tag ) )
            {
                UpdateFinalFilters();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Updates all filters at once.
        /// <para>
        /// <see cref="DefaultFilters"/> are appended to this list and any useless filters
        /// are optimized out.
        /// </para>
        /// </summary>
        /// <param name="filters">Ordered final set of tags and associated clamper including the <see cref="DefaultFilters"/>.</param>
        public static IReadOnlyList<(CKTrait T, LogClamper F)> SetFilters( (CKTrait, LogClamper)[] filters )
        {
            // Cleanup candidates once for all first. 
            filters = SkipUselessFilters( filters ).ToArray();
            Interlocked.Exchange( ref _filters, filters );
            return UpdateFinalFilters();
        }

        static IReadOnlyList<(CKTrait T, LogClamper F)> UpdateFinalFilters()
        {
            lock( _finalFiltersLockAndEmptyArray )
            {
                return _finalFilters = SkipUselessFilters( _filters.Concat( _defaultFilters ).ToArray() ).ToArray();
            }
        }

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
        public static IReadOnlyList<(CKTrait T, LogClamper F)> AddDefaultFilter( CKTrait tag, LogClamper c )
        {
            var def = Util.InterlockedSet( ref _defaultFilters, (tag, c), ( filters, f ) => Add( filters, f ) );
            UpdateFinalFilters();
            return def;
        }

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
        public static bool RemoveDefaultFilter( CKTrait tag )
        {
            if( InterlockedRemove( ref _defaultFilters, tag ) )
            {
                UpdateFinalFilters();
                return true;
            }
            return false;
        }

        // The removal is detected by the update itself: comparing with a previously read
        // value would also see the concurrent updates of other threads.
        static bool InterlockedRemove( ref (CKTrait, LogClamper)[] target, CKTrait tag )
        {
            bool removed = false;
            Util.InterlockedSet( ref target, tag, ( filters, t ) =>
            {
                var r = Remove( filters, t );
                removed = r != filters;
                return r;
            } );
            return removed;
        }

        static (CKTrait, LogClamper)[] Remove( (CKTrait, LogClamper)[] filters, CKTrait t )
        {
            for( int i = 0; i < filters.Length; i++ )
            {
                if( filters[i].Item1 == t )
                {
                    var n = new (CKTrait, LogClamper)[filters.Length - 1];
                    Array.Copy( filters, 0, n, 0, i );
                    Array.Copy( filters, i + 1, n, i, n.Length - i );
                    return n;
                }
            }
            return filters;
        }

        static (CKTrait, LogClamper)[] Add( (CKTrait, LogClamper)[] filters, (CKTrait, LogClamper) f )
        {
            var n = new (CKTrait, LogClamper)[filters.Length + 1];
            n[0] = f;
            Array.Copy( filters, 0, n, 1, filters.Length );
            return SkipUselessFilters( n ).ToArray();
        }

        static IEnumerable<(CKTrait tag, LogClamper c)> SkipUselessFilters( (CKTrait, LogClamper)[] filters )
        {
            for( int i = 0; i < filters.Length; i++ )
            {
                var f = filters[i];
                if( f.Item2.Filter == default ) continue;

                bool skipLine = false;
                bool skipGroup = false;
                for( int j = 0; j < i; j++ )
                {
                    var above = filters[j];
                    // This is the key: the first subset decides (if its Line and Group are not none).
                    // If f is a super set of above then a tag t will necessarily first match on above.
                    if( f.Item1.IsSupersetOf( above.Item1 ) )
                    {
                        if( above.Item2.Filter.Line != LogLevelFilter.None )
                        {
                            skipLine = true;
                            // Line is defined by above.
                            // If above also defines Group, f is useless.
                            // Even if above Group is none, if f Group is also none, then f is useless.
                            skipGroup |= above.Item2.Filter.Group != LogLevelFilter.None || f.Item2.Filter.Group == LogLevelFilter.None;
                        }
                        else
                        {
                            // Above Line is not defined.
                            Throw.DebugAssert( above.Item2.Filter.Group != LogLevelFilter.None, "Otherwise it would be 'default' already filtered out." );
                            skipGroup = true;
                            // If f Line is also undefined, then f is useless.
                            skipLine |= f.Item2.Filter.Line == LogLevelFilter.None;
                        }
                    }
                    if( skipLine && skipGroup ) break;
                }
                if( skipLine && skipGroup ) continue;
                yield return f;
            }
        }

        /// <summary>
        /// Finds a <see cref="LogClamper"/> to consider for a line that has tags and a current filter
        /// and computes whether the log must be emitted or not.
        /// </summary>
        /// <param name="logLevel">The current log level.</param>
        /// <param name="finalTags">The log's tags.</param>
        /// <param name="filter">The current filter applied to the line.</param>
        /// <returns>Whether the log must be emitted or not.</returns>
        public static bool ApplyForLine( LogLevel logLevel, CKTrait finalTags, LogLevelFilter filter )
        {
            Throw.DebugAssert( finalTags != null );
            var filters = _finalFilters;
            if( !finalTags.IsEmpty )
            {
                foreach( var (T, F) in filters )
                {
                    LogLevelFilter iTag;
                    if( finalTags.IsSupersetOf( T ) && (iTag = F.Filter.Line) > 0 )
                    {
                        if( iTag != filter )
                        {
                            if( F.Clamp )
                            {
                                filter = iTag;
                            }
                            else
                            {
                                if( filter <= 0 ) filter = ActivityMonitor.DefaultFilter.Line;
                                filter = (LogLevelFilter)Math.Min( (int)filter, (int)iTag );
                            }
                        }
                        return (int)(logLevel & LogLevel.Mask) >= (int)filter;
                    }
                }
            }
            if( filter <= 0 ) filter = ActivityMonitor.DefaultFilter.Line;
            return (int)(logLevel & LogLevel.Mask) >= (int)filter;
        }

        /// <summary>
        /// Finds a <see cref="LogClamper"/> to consider for a group that has tags and a current filter
        /// and computes whether the log must be emitted or not.
        /// </summary>
        /// <param name="logLevel">The current log level.</param>
        /// <param name="finalTags">The groups's tags.</param>
        /// <param name="filter">The current filter applied to the group.</param>
        /// <returns>Whether the log must be emitted or not.</returns>
        public static bool ApplyForGroup( LogLevel logLevel, CKTrait finalTags, LogLevelFilter filter )
        {
            var filters = _finalFilters;
            foreach( var (T, F) in filters )
            {
                LogLevelFilter iTag;
                if( finalTags.IsSupersetOf( T ) && (iTag = F.Filter.Group) > 0 )
                {
                    if( iTag != filter )
                    {
                        if( iTag != filter )
                        {
                            if( F.Clamp )
                            {
                                filter = iTag;
                            }
                            else
                            {
                                if( filter <= 0 ) filter = ActivityMonitor.DefaultFilter.Group;
                                filter = (LogLevelFilter)Math.Min( (int)filter, (int)iTag );
                            }
                        }
                    }
                    return (int)(logLevel & LogLevel.Mask) >= (int)filter;
                }
            }
            if( filter <= 0 ) filter = ActivityMonitor.DefaultFilter.Group;
            return (int)(logLevel & LogLevel.Mask) >= (int)filter;
        }
    }
}
