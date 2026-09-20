using System;
using System.Collections.Concurrent;
using System.Threading;

namespace CK.Core;

public sealed partial class ActivityMonitorExternalLogData
{
    /// <summary>
    /// The initial pool capacity. <see cref="CurrentPoolCapacity"/> never shrinks below this.
    /// </summary>
    public const int InitialPoolCapacity = 200;

    /// <summary>
    /// Gets the current pool capacity. It starts at <see cref="InitialPoolCapacity"/> and increases silently
    /// until <see cref="MaximalCapacity"/> is reached (above which released data is garbage collected instead of
    /// being pooled). This self tuning is never logged: see <see cref="PoolDiagnostics.OnSaturated()"/>.
    /// <para>
    /// It shrinks back towards the recently observed demand: see <see cref="PoolDiagnostics.LastWindowPeakAliveCount"/>.
    /// </para>
    /// </summary>
    public static int CurrentPoolCapacity => _currentCapacity;

    /// <summary>
    /// The current pool capacity increment until <see cref="CurrentPoolCapacity"/> reaches <see cref="MaximalCapacity"/>.
    /// This is also the margin that is kept above the observed demand when the pool trims itself.
    /// </summary>
    public const int PoolCapacityIncrement = 10;

    /// <summary>
    /// The maximal capacity. Once reached, newly released <see cref="ActivityMonitorExternalLogData"/> are garbage
    /// collected instead of returned to the pool.
    /// </summary>
    public const int MaximalCapacity = 2000;

    /// <summary>
    /// Gets the health and leak diagnostics of this pool.
    /// <para>
    /// Pool saturation is <em>not</em> a leak: it is a peak of activity. Leaks are reported by
    /// <see cref="PoolDiagnostics.LeakedCount"/> (a <see cref="Release()"/> call is missing) and by
    /// <see cref="PoolDiagnostics.CurrentFloor"/> (data is acquired and retained forever).
    /// </para>
    /// </summary>
    public static PoolDiagnostics PoolDiagnostics => _diagnostics;

    /// <summary>
    /// Gets the current number of <see cref="ActivityMonitorExternalLogData"/> that are alive (not yet released).
    /// When there is no log activity and no entries have been cached, this must be 0.
    /// </summary>
    public static int AliveCount => _diagnostics.AliveCount;

    /// <summary>
    /// Gets the current number of cached entries.
    /// This is an approximate value because of concurency.
    /// </summary>
    public static int PooledEntryCount => _numItems + (_fastItem != null ? 1 : 0);

    static readonly ConcurrentQueue<ActivityMonitorExternalLogData> _items = new();
    static readonly PoolDiagnostics _diagnostics;
    static ActivityMonitorExternalLogData? _fastItem;
    static int _numItems;
    static int _currentCapacity = InitialPoolCapacity;

    static ActivityMonitorExternalLogData()
    {
        _diagnostics = new PoolDiagnostics( nameof( ActivityMonitorExternalLogData ) );
        _diagnostics.OnWindowClosed += Trim;
    }

    internal static ActivityMonitorExternalLogData CreateNonPooled( ref ActivityMonitorLogData data )
    {
        var e = new ActivityMonitorExternalLogData();
        // A non pooled data is never released: it must not be tracked by the leak detection.
        GC.SuppressFinalize( e );
        e.Initialize( ref data, false );
        return e;
    }

    internal static ActivityMonitorExternalLogData Acquire( ref ActivityMonitorLogData data )
    {
        _diagnostics.OnAcquire();
        var item = _fastItem;
        if( item == null || Interlocked.CompareExchange( ref _fastItem, null, item ) != item )
        {
            if( _items.TryDequeue( out item ) )
            {
                Interlocked.Decrement( ref _numItems );
            }
            else
            {
                item = new ActivityMonitorExternalLogData();
            }
        }
        item.Initialize( ref data, true );
        return item;
    }

    static void Release( ActivityMonitorExternalLogData c )
    {
        // Must be done before anything else: this feeds the alive count floor that detects retained data
        // and may close an observation window (that trims this pool).
        _diagnostics.OnRelease();
        if( _fastItem != null || Interlocked.CompareExchange( ref _fastItem, c, null ) != null )
        {
            int poolCount = Interlocked.Increment( ref _numItems );
            // Strictly lower than to account for the _fastItem.
            if( poolCount < _currentCapacity )
            {
                _items.Enqueue( c );
                return;
            }
            // Current capacity is reached: increase it (with a warning) until MaximalCapacity.
            // Above it, the data is dropped and garbage collected. This is a capacity event (a peak of
            // concurrent activity), NOT a leak: a leaked data never comes back here in the first place.
            if( poolCount >= MaximalCapacity )
            {
                // Adjust the pool count and drop the data.
                Interlocked.Decrement( ref _numItems );
                GC.SuppressFinalize( c );
                _diagnostics.OnSaturated();
            }
            else
            {
                Interlocked.Add( ref _currentCapacity, PoolCapacityIncrement );
                _diagnostics.OnCapacityIncreased();
                _items.Enqueue( c );
            }
        }
    }

    /// <summary>
    /// Shrinks the pool back towards the demand observed over the whole recent history. Without this, a single peak
    /// of activity would keep the pool at its maximal capacity forever and every subsequent Release() that loses the
    /// _fastItem race would report a saturation: this is what made the previous detection permanently noisy.
    /// <para>
    /// <see cref="PoolDiagnostics.RecentPeakAliveCount"/> (and not the last window alone) is what avoids shrinking
    /// during a lull just to grow back on the next burst.
    /// </para>
    /// </summary>
    static void Trim( PoolDiagnostics d )
    {
        int target = Math.Max( InitialPoolCapacity, d.RecentPeakAliveCount + PoolCapacityIncrement );
        if( target >= _currentCapacity ) return;
        Interlocked.Exchange( ref _currentCapacity, target );
        while( _numItems > target && _items.TryDequeue( out var e ) )
        {
            Interlocked.Decrement( ref _numItems );
            // Dropped on purpose: this is not a leak.
            GC.SuppressFinalize( e );
        }
    }

}
