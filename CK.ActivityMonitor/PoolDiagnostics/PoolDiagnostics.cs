using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace CK.Core;

/// <summary>
/// Health and leak diagnostics for a pool of reference counted objects.
/// <para>
/// A pool owns one instance of this class (typically in a static readonly field) and calls
/// <see cref="OnAcquire()"/> and <see cref="OnRelease()"/> on its hot path, <see cref="OnCapacityIncreased()"/>
/// and <see cref="OnSaturated()"/> when its capacity is exceeded and <see cref="OnLeaked(string?)"/> from the
/// finalizer of a pooled object that is collected while still alive.
/// </para>
/// <para>
/// Two independent signals are handled here because a pool can be abused in two very different ways:
/// <list type="bullet">
///   <item>
///   <term>Dropped objects</term>
///   <description>
///   An object is acquired and its last reference is lost without <see cref="IRefCounted.Release()"/> being called.
///   <see cref="OnLeaked(string?)"/> detects this exactly (no false positive): a pooled object is rooted by its pool,
///   so it can only ever be finalized when it is alive, and being alive when unreachable is the definition of this bug.
///   </description>
///   </item>
///   <item>
///   <term>Retained objects</term>
///   <description>
///   An object is acquired (or <see cref="IRefCounted.AddRef()"/>-ed) and kept forever by an ever growing container.
///   Such an object stays reachable: no finalizer will ever run for it. The only observable symptom is that the
///   <em>floor</em> of <see cref="AliveCount"/> rises: see <see cref="CurrentFloor"/>.
///   </description>
///   </item>
/// </list>
/// </para>
/// <para>
/// Only leaks are ever logged, and a leak report cannot be turned off: it always denotes a bug. Pool saturation and
/// capacity growth are deliberately <em>not</em> leak signals: they
/// are the high-water mark of the concurrently alive objects, that is a peak of activity. They are counted
/// (<see cref="SaturatedCount"/>, <see cref="CapacityIncreaseCount"/>, <see cref="PeakAliveCount"/>) so that metrics
/// can expose them, but nothing is written to the log about them.
/// </para>
/// <para>
/// This class is thread safe. Counters are maintained with interlocked operations, the windowed analysis is
/// intentionally racy (it is a heuristic, not an accounting).
/// </para>
/// </summary>
public sealed class PoolDiagnostics
{
    /// <summary>
    /// The default duration of an observation window in milliseconds.
    /// </summary>
    public const int DefaultWindowMilliseconds = 10_000;

    /// <summary>
    /// The default number of observation windows that are kept to detect a rising <see cref="CurrentFloor"/>.
    /// Must be even: the oldest half is compared to the newest half.
    /// </summary>
    public const int DefaultFloorHistoryCount = 6;

    /// <summary>
    /// The default minimal rise of the <see cref="CurrentFloor"/> across the whole history that is
    /// considered to be a leak.
    /// </summary>
    public const int DefaultLeakGrowthThreshold = 64;

    /// <summary>
    /// The maximal number of leak descriptions that are kept in <see cref="LeakSamples"/>.
    /// </summary>
    public const int MaxLeakSampleCount = 10;

    /// <summary>
    /// The first back off delay in milliseconds: a repeated leak report is emitted at most once every 5 seconds,
    /// then 10, 20... up to <see cref="MaximalReportDelay"/>.
    /// </summary>
    public const int InitialReportDelay = 5_000;

    /// <summary>
    /// The maximal back off delay in milliseconds (10 minutes).
    /// </summary>
    public const int MaximalReportDelay = 10 * 60_000;

    // Reports are emitted through ActivityMonitor.StaticLogger. In a full CK.Monitoring setup this
    // reenters the very pool that is being diagnosed: this guard keeps the recursion to one level.
    [ThreadStatic] static bool _isReporting;
    static bool _shutdown;

    readonly string _poolName;
    readonly int[] _floors;
    readonly int[] _peaks;
    readonly string[] _leakSamples;
    // The window lock guards the analysis. The sample lock guards _leakSamples/_leakSampleCount only: it is
    // taken by the finalizer thread, so it must never be held while anything else is done (and in particular
    // never while reporting, which invokes the OnStaticLog handlers).
    readonly object _windowLock;
    readonly object _sampleLock;
    readonly int _windowMilliseconds;
    readonly int _leakGrowthThreshold;

    // Hot path counters.
    int _aliveCount;
    int _peakAliveCount;

    // Window sampling (racy by design).
    int _windowMinAlive;
    int _windowMaxAlive;
    long _nextWindowTime;

    // Guarded by _windowLock.
    int _floorIndex;
    int _floorCount;
    int _currentFloor;
    int _lastWindowPeak;
    int _recentPeak;

    // Guarded by _sampleLock.
    int _leakSampleCount;

    // Leak/abuse counters.
    int _leakedCount;
    int _reportedLeakedCount;
    int _overReleaseCount;

    // Capacity counters. These are observation only: nothing is ever logged about them.
    int _saturatedCount;
    int _capacityIncreaseCount;

    // Exponential back off of the leak reports.
    long _nextLeakReportTime;
    int _leakReportDelay;

    static PoolDiagnostics()
    {
        AppDomain.CurrentDomain.ProcessExit += static ( _, _ ) => _shutdown = true;
        AppDomain.CurrentDomain.DomainUnload += static ( _, _ ) => _shutdown = true;
    }

    /// <summary>
    /// Initializes a new <see cref="PoolDiagnostics"/>.
    /// </summary>
    /// <param name="poolName">Name of the pool. Used in the emitted logs.</param>
    /// <param name="windowMilliseconds">Duration of an observation window. Defaults to <see cref="DefaultWindowMilliseconds"/>.</param>
    /// <param name="floorHistoryCount">
    /// Number of observation windows to keep. Must be even and at least 2.
    /// Defaults to <see cref="DefaultFloorHistoryCount"/>.
    /// </param>
    /// <param name="leakGrowthThreshold">
    /// Minimal rise of the <see cref="CurrentFloor"/> across the whole history that is considered to be a leak.
    /// Defaults to <see cref="DefaultLeakGrowthThreshold"/>.
    /// </param>
    public PoolDiagnostics( string poolName,
                            int windowMilliseconds = DefaultWindowMilliseconds,
                            int floorHistoryCount = DefaultFloorHistoryCount,
                            int leakGrowthThreshold = DefaultLeakGrowthThreshold )
    {
        Throw.CheckNotNullOrWhiteSpaceArgument( poolName );
        Throw.CheckOutOfRangeArgument( windowMilliseconds > 0 );
        Throw.CheckOutOfRangeArgument( floorHistoryCount >= 2 && (floorHistoryCount & 1) == 0 );
        Throw.CheckOutOfRangeArgument( leakGrowthThreshold > 0 );
        _poolName = poolName;
        _windowMilliseconds = windowMilliseconds;
        _leakGrowthThreshold = leakGrowthThreshold;
        _floors = new int[floorHistoryCount];
        _peaks = new int[floorHistoryCount];
        _leakSamples = new string[MaxLeakSampleCount];
        _windowLock = new object();
        _sampleLock = new object();
        _windowMinAlive = int.MaxValue;
        _nextWindowTime = Environment.TickCount64 + windowMilliseconds;
        _leakReportDelay = InitialReportDelay;
    }

    /// <summary>
    /// Gets the name of the diagnosed pool.
    /// </summary>
    public string PoolName => _poolName;

    /// <summary>
    /// Gets the duration of an observation window in milliseconds.
    /// </summary>
    public int WindowMilliseconds => _windowMilliseconds;

    /// <summary>
    /// Gets the current number of objects that are alive (acquired and not yet released).
    /// When there is no activity and nothing is retained, this must be 0.
    /// </summary>
    public int AliveCount => _aliveCount;

    /// <summary>
    /// Gets the greatest <see cref="AliveCount"/> ever observed.
    /// This is the concurrency peak: it is what sizes the pool, it is not a leak indicator.
    /// </summary>
    public int PeakAliveCount => _peakAliveCount;

    /// <summary>
    /// Gets the minimal <see cref="AliveCount"/> observed during the last closed observation window.
    /// <para>
    /// This is the actual leak indicator: a burst of activity is a saw tooth that comes back to its baseline,
    /// a leak lifts the baseline. See <see cref="LastLeakReport"/>.
    /// </para>
    /// </summary>
    public int CurrentFloor => _currentFloor;

    /// <summary>
    /// Gets the greatest <see cref="AliveCount"/> observed during the last closed observation window.
    /// </summary>
    public int LastWindowPeakAliveCount => _lastWindowPeak;

    /// <summary>
    /// Gets the greatest <see cref="AliveCount"/> observed during the whole window history (the last
    /// <see cref="WindowMilliseconds"/> times the floor history count, one minute with the defaults).
    /// <para>
    /// This is the demand a pool should size itself for: trimming on the last window alone makes the pool
    /// oscillate (shrink then immediately grow back, each growth emitting a warning), this does not.
    /// </para>
    /// </summary>
    public int RecentPeakAliveCount => _recentPeak;

    /// <summary>
    /// Gets the number of times the pool capacity has been increased. Never logged.
    /// </summary>
    public int CapacityIncreaseCount => _capacityIncreaseCount;

    /// <summary>
    /// Gets the number of objects that have been garbage collected while still alive.
    /// Any non zero value here is a bug: a <see cref="IRefCounted.Release()"/> call is missing.
    /// </summary>
    public int LeakedCount => _leakedCount;

    /// <summary>
    /// Gets the number of detected extraneous <see cref="IRefCounted.Release()"/> calls.
    /// Any non zero value here is a bug. This detection is best effort: an over release that happens after the
    /// object has been reacquired by another thread cannot be distinguished from a legitimate release.
    /// </summary>
    public int OverReleaseCount => _overReleaseCount;

    /// <summary>
    /// Gets the number of times the pool has been saturated (an object had to be dropped because the pool was full).
    /// This is a capacity indicator, not a leak indicator: it is never logged.
    /// </summary>
    public int SaturatedCount => _saturatedCount;

    /// <summary>
    /// Gets the last emitted leak report, null if no leak has been detected yet.
    /// </summary>
    public string? LastLeakReport { get; private set; }

    /// <summary>
    /// Gets up to <see cref="MaxLeakSampleCount"/> descriptions of objects that leaked since the last
    /// emitted report (a report consumes the samples it shows).
    /// </summary>
    public IReadOnlyList<string> LeakSamples
    {
        get
        {
            lock( _sampleLock )
            {
                var r = new string[_leakSampleCount];
                Array.Copy( _leakSamples, r, _leakSampleCount );
                return r;
            }
        }
    }

    /// <summary>
    /// Raised when an observation window closes, on the thread that happened to release an object at that moment.
    /// A pool typically uses this to trim itself down to <see cref="RecentPeakAliveCount"/>.
    /// Handlers must be fast and must not throw.
    /// </summary>
    public event Action<PoolDiagnostics>? OnWindowClosed;

    /// <summary>
    /// Must be called by the pool once an object has been obtained (from the pool or newly created).
    /// </summary>
    public void OnAcquire()
    {
        int alive = Interlocked.Increment( ref _aliveCount );
        // Racy on purpose: a lost update on a peak is irrelevant.
        if( alive > _peakAliveCount ) _peakAliveCount = alive;
        if( alive > _windowMaxAlive ) _windowMaxAlive = alive;
        // The worst possible leak releases nothing at all. If windows could only close from OnRelease,
        // that leak would grow forever without ever being reported.
        TryCloseWindow( alive );
    }

    /// <summary>
    /// Must be called by the pool when an object is released (its reference count reached 0), regardless of
    /// whether the object is actually returned to the pool or dropped because the pool is full.
    /// </summary>
    public void OnRelease()
    {
        int alive = Interlocked.Decrement( ref _aliveCount );
        // Racy on purpose: the floor is a heuristic.
        if( alive < _windowMinAlive ) _windowMinAlive = alive;
        TryCloseWindow( alive );
    }

    void TryCloseWindow( int alive )
    {
        long now = Environment.TickCount64;
        long next = _nextWindowTime;
        if( now >= next && Interlocked.CompareExchange( ref _nextWindowTime, now + _windowMilliseconds, next ) == next )
        {
            CloseWindow( alive, now );
        }
    }

    /// <summary>
    /// Must be called from the finalizer of a pooled object when it is collected while still alive.
    /// This is an exact leak detection: the caller is responsible for the "is alive" test (typically a non zero
    /// reference count), this method handles the process shutdown case where finalizers run on live objects.
    /// <para>
    /// This is called on the finalizer thread: nothing is logged from here, the report is emitted later
    /// from <see cref="OnRelease()"/>.
    /// </para>
    /// </summary>
    /// <param name="description">
    /// Optional description of the leaked object (that still holds its data at this point). Use
    /// <see cref="NeedLeakSample"/> to avoid building it when enough samples have been collected.
    /// </param>
    public void OnLeaked( string? description )
    {
        if( _shutdown || Environment.HasShutdownStarted ) return;
        Interlocked.Increment( ref _leakedCount );
        if( description != null )
        {
            lock( _sampleLock )
            {
                if( _leakSampleCount < MaxLeakSampleCount ) _leakSamples[_leakSampleCount++] = description;
            }
        }
    }

    /// <summary>
    /// Gets whether <see cref="OnLeaked(string?)"/> still needs a description: a finalizer should not pay for
    /// building one once <see cref="MaxLeakSampleCount"/> samples have been collected for the current period.
    /// </summary>
    public bool NeedLeakSample => _leakSampleCount < MaxLeakSampleCount && !_shutdown && !Environment.HasShutdownStarted;

    /// <summary>
    /// Must be called when an extraneous release is detected.
    /// </summary>
    public void OnOverRelease()
    {
        Interlocked.Increment( ref _overReleaseCount );
    }

    /// <summary>
    /// Must be called when the pool capacity has been increased. Increments <see cref="CapacityIncreaseCount"/>.
    /// Nothing is logged: a self tuning pool resizing itself is not an event anyone needs to read about.
    /// </summary>
    public void OnCapacityIncreased()
    {
        Interlocked.Increment( ref _capacityIncreaseCount );
    }

    /// <summary>
    /// Must be called when the pool is full and an object has to be dropped (and garbage collected).
    /// Increments <see cref="SaturatedCount"/>.
    /// <para>
    /// Nothing is logged. Saturation is a peak of concurrent activity: nothing is lost (the dropped objects are
    /// simply garbage collected), the pool capacity is not configurable, and the log would be emitted exactly when
    /// the log pipeline is the most loaded, adding to the very pressure it describes. The one case that would
    /// deserve attention, a durable backlog rather than a burst, raises the alive floor and is reported by
    /// <see cref="CurrentFloor"/>. This counter is here to be observed (metrics, health endpoint, tests), not read.
    /// </para>
    /// </summary>
    public void OnSaturated()
    {
        Interlocked.Increment( ref _saturatedCount );
    }

    /// <summary>
    /// Forces the current observation window to close. This is intended for tests: the window normally
    /// closes by itself from <see cref="OnRelease()"/> once <see cref="WindowMilliseconds"/> has elapsed.
    /// </summary>
    public void CloseWindow()
    {
        long now = Environment.TickCount64;
        Interlocked.Exchange( ref _nextWindowTime, now + _windowMilliseconds );
        CloseWindow( _aliveCount, now );
    }

    /// <summary>
    /// Resets all the counters and the observation history. <see cref="AliveCount"/> is preserved
    /// unless <paramref name="resetAliveCount"/> is true.
    /// </summary>
    /// <param name="resetAliveCount">
    /// True to also reset <see cref="AliveCount"/> to 0. This must be done only when the objects that are
    /// accounted for are known to be gone (typically leaked objects that have been collected), otherwise
    /// their future release will drive the count below 0.
    /// </param>
    public void Reset( bool resetAliveCount = false )
    {
        lock( _windowLock )
        {
            if( resetAliveCount ) Interlocked.Exchange( ref _aliveCount, 0 );
            _peakAliveCount = _aliveCount;
            _windowMinAlive = int.MaxValue;
            _windowMaxAlive = _aliveCount;
            _nextWindowTime = Environment.TickCount64 + _windowMilliseconds;
            _floorIndex = _floorCount = _currentFloor = _lastWindowPeak = _recentPeak = 0;
            Array.Clear( _floors );
            Array.Clear( _peaks );
            lock( _sampleLock )
            {
                _leakSampleCount = 0;
                Array.Clear( _leakSamples );
            }
            _leakedCount = _reportedLeakedCount = 0;
            _overReleaseCount = 0;
            _saturatedCount = 0;
            _capacityIncreaseCount = 0;
            _nextLeakReportTime = 0;
            _leakReportDelay = InitialReportDelay;
            LastLeakReport = null;
        }
    }

    void CloseWindow( int currentAlive, long now )
    {
        Action<PoolDiagnostics>? windowClosed;
        string? finalized, risingFloor;
        lock( _windowLock )
        {
            int floor = _windowMinAlive;
            if( floor == int.MaxValue ) floor = currentAlive;
            _windowMinAlive = int.MaxValue;
            _currentFloor = floor;
            _lastWindowPeak = Math.Max( _windowMaxAlive, currentAlive );
            _windowMaxAlive = currentAlive;

            _floors[_floorIndex] = floor;
            _peaks[_floorIndex] = _lastWindowPeak;
            _floorIndex = (_floorIndex + 1) % _floors.Length;
            if( _floorCount < _floors.Length ) ++_floorCount;

            int recent = 0;
            for( int i = 0; i < _peaks.Length; ++i ) if( _peaks[i] > recent ) recent = _peaks[i];
            _recentPeak = recent;

            finalized = CheckFinalizedLeaks( now );
            risingFloor = CheckRisingFloor( now );
            windowClosed = OnWindowClosed;
        }
        // Reporting invokes the ActivityMonitor.OnStaticLog handlers: never do that while holding a lock
        // that the finalizer thread (OnLeaked) or another releasing thread may be waiting for.
        if( finalized != null ) Report( LogLevel.Error | LogLevel.IsFiltered, ActivityMonitor.Tags.ToBeInvestigated, finalized );
        if( risingFloor != null ) Report( LogLevel.Error | LogLevel.IsFiltered, ActivityMonitor.Tags.ToBeInvestigated, risingFloor );
        windowClosed?.Invoke( this );
    }

    // Called under _windowLock.
    string? CheckFinalizedLeaks( long now )
    {
        int n = _leakedCount;
        if( n == _reportedLeakedCount || now < _nextLeakReportTime ) return null;
        _nextLeakReportTime = now + _leakReportDelay;
        _leakReportDelay = Math.Min( _leakReportDelay * 2, MaximalReportDelay );
        int since = n - _reportedLeakedCount;
        _reportedLeakedCount = n;
        var b = new StringBuilder();
        b.Append( "Leak detected in the " ).Append( _poolName ).Append( " pool: " )
         .Append( since ).Append( " object(s) have been garbage collected while still alive since the last report (" )
         .Append( n ).Append( " in total). A Release() call is missing." );
        AppendContext( b );
        AppendSamples( b );
        return LastLeakReport = b.ToString();
    }

    // Called under _windowLock.
    string? CheckRisingFloor( long now )
    {
        if( _floorCount < _floors.Length ) return null;
        // The ring is full: _floorIndex is the oldest slot.
        int n = _floors.Length, half = n / 2;
        int maxOld = int.MinValue, minNew = int.MaxValue;
        for( int i = 0; i < n; ++i )
        {
            int v = _floors[(_floorIndex + i) % n];
            if( i < half ) { if( v > maxOld ) maxOld = v; }
            else if( v < minNew ) minNew = v;
        }
        // The whole recent half must be above the whole older half: a burst cannot do that,
        // it comes back to its baseline.
        int growth = minNew - maxOld;
        if( growth < _leakGrowthThreshold ) return null;
        // Restart the observation from this new baseline so that a stable (but higher) floor is reported only once.
        _floorCount = 0;
        if( now < _nextLeakReportTime ) return null;
        _nextLeakReportTime = now + _leakReportDelay;
        _leakReportDelay = Math.Min( _leakReportDelay * 2, MaximalReportDelay );
        var b = new StringBuilder();
        b.Append( "Leak suspected in the " ).Append( _poolName )
         .Append( " pool: the minimal number of alive objects rose from " )
         .Append( maxOld ).Append( " to " ).Append( minNew ).Append( " (+" ).Append( growth )
         .Append( ") over the last " ).Append( (long)n * _windowMilliseconds / 1000 )
         .Append( " seconds. Objects are acquired and retained, not released. Floors (oldest first): " );
        for( int i = 0; i < n; ++i )
        {
            if( i > 0 ) b.Append( ", " );
            b.Append( _floors[(_floorIndex + i) % n] );
        }
        b.Append( '.' );
        AppendContext( b );
        AppendSamples( b );
        return LastLeakReport = b.ToString();
    }

    // Called under _windowLock. The capacity numbers are never logged on their own: they only appear here,
    // as context for an actual problem.
    void AppendContext( StringBuilder b )
    {
        b.Append( " [alive: " ).Append( _aliveCount )
         .Append( ", floor: " ).Append( _currentFloor )
         .Append( ", peak: " ).Append( _peakAliveCount )
         .Append( " (recent: " ).Append( _recentPeak )
         .Append( "), collected while alive: " ).Append( _leakedCount )
         .Append( ", over releases: " ).Append( _overReleaseCount )
         .Append( ", pool saturations: " ).Append( _saturatedCount ).Append( ']' );
    }

    // Samples are consumed by the report that shows them: each report describes the leaks observed since
    // the previous one, and sampling resumes for the next period. Without this, a report would repeat stale
    // samples and no leak appearing after the first MaxLeakSampleCount would ever be described.
    void AppendSamples( StringBuilder b )
    {
        lock( _sampleLock )
        {
            if( _leakSampleCount == 0 ) return;
            b.Append( Environment.NewLine ).Append( "Leaked object samples (since the last report):" );
            for( int i = 0; i < _leakSampleCount; ++i )
            {
                b.Append( Environment.NewLine ).Append( " - " ).Append( _leakSamples[i] );
            }
            _leakSampleCount = 0;
            Array.Clear( _leakSamples );
        }
    }

    static void Report( LogLevel level, CKTrait? tags, string text )
    {
        // There is deliberately no StaticGate here: a leak is always a bug and must never be silenceable.
        if( _isReporting ) return;
        _isReporting = true;
        try
        {
            ActivityMonitor.StaticLogger.UnfilteredLog( level, tags, text, null );
        }
        finally
        {
            _isReporting = false;
        }
    }
}
