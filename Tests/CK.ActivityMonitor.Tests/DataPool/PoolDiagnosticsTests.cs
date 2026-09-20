using Shouldly;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading;

namespace CK.Core.Tests.Monitoring;

[TestFixture]
public class PoolDiagnosticsTests
{
    // Windows are closed explicitly by CloseWindow(): the duration is irrelevant here, but it must be
    // big enough for no window to close by itself while the test runs.
    static PoolDiagnostics CreateDiagnostics( int growthThreshold = 10 )
        => new PoolDiagnostics( "Test", windowMilliseconds: 600_000, floorHistoryCount: 6, leakGrowthThreshold: growthThreshold );

    static List<string> CatchStaticLogs( out ActivityMonitor.StaticLogHandler handler )
    {
        var logs = new List<string>();
        handler = delegate ( ref ActivityMonitorLogData d ) { logs.Add( d.Text ); };
        ActivityMonitor.OnStaticLog += handler;
        return logs;
    }

    // Simulates one observation window: goes up to 'peak' alive objects then back down to 'floor'.
    static void Window( PoolDiagnostics d, int peak, int floor )
    {
        int toAcquire = peak - d.AliveCount;
        for( int i = 0; i < toAcquire; ++i ) d.OnAcquire();
        while( d.AliveCount > floor ) d.OnRelease();
        d.CloseWindow();
    }

    [Test]
    public void a_burst_of_activity_is_not_a_leak()
    {
        var d = CreateDiagnostics();
        var logs = CatchStaticLogs( out var h );
        try
        {
            // Sharp bursts, each one coming back to the same baseline: this is exactly what a peak of
            // logging activity looks like and it must NOT be reported.
            for( int i = 0; i < 20; ++i )
            {
                Window( d, peak: 5 + (i % 2 == 0 ? 2000 : 0), floor: 5 );
            }
            d.CurrentFloor.ShouldBe( 5 );
            d.PeakAliveCount.ShouldBe( 2005, "The peak is observed..." );
            d.LastLeakReport.ShouldBeNull( "...but the floor never moved: no leak." );
            logs.ShouldBeEmpty();
        }
        finally
        {
            ActivityMonitor.OnStaticLog -= h;
        }
    }

    [Test]
    public void a_rising_alive_floor_is_reported_as_a_leak()
    {
        var d = CreateDiagnostics( growthThreshold: 10 );
        var logs = CatchStaticLogs( out var h );
        try
        {
            // Retains 5 more objects per window: the floor rises while the peaks stay the same.
            for( int i = 0; i < 6; ++i )
            {
                Window( d, peak: 100, floor: 5 * i );
            }
            // Floors are 0, 5, 10, 15, 20, 25: min(15,20,25) - max(0,5,10) = 5, below the threshold.
            d.LastLeakReport.ShouldBeNull();

            for( int i = 6; i < 10; ++i )
            {
                Window( d, peak: 100, floor: 5 * i );
            }
            // Floors are 20, 25, 30, 35, 40, 45: min(35,40,45) - max(20,25,30) = 5... still below.
            // The detection needs the two halves to be separated by the threshold.
            d.LastLeakReport.ShouldBeNull();

            // A faster leak (20 per window) separates the halves.
            for( int i = 0; i < 6; ++i )
            {
                Window( d, peak: 200, floor: d.AliveCount + 20 );
            }
            d.LastLeakReport.ShouldNotBeNull();
            d.LastLeakReport.ShouldContain( "Leak suspected in the Test pool" );
            d.LastLeakReport.ShouldContain( "Floors (oldest first)" );
            logs.Count.ShouldBe( 1 );
            logs[0].ShouldBe( d.LastLeakReport );
        }
        finally
        {
            ActivityMonitor.OnStaticLog -= h;
        }
    }

    [Test]
    public void a_stable_but_higher_floor_is_reported_only_once()
    {
        var d = CreateDiagnostics( growthThreshold: 10 );
        var logs = CatchStaticLogs( out var h );
        try
        {
            // A step: 500 objects are retained once and for all (a legitimate cache, or a one shot leak).
            for( int i = 0; i < 3; ++i ) Window( d, peak: 600, floor: 0 );
            for( int i = 0; i < 20; ++i ) Window( d, peak: 600, floor: 500 );

            logs.Count.ShouldBe( 1, "Reported when the step is observed, then the new baseline is the reference." );
            logs[0].ShouldContain( "rose from 0 to 500 (+500)" );
        }
        finally
        {
            ActivityMonitor.OnStaticLog -= h;
        }
    }

    [Test]
    public void OnLeaked_reports_the_count_and_the_samples()
    {
        var d = CreateDiagnostics();
        var logs = CatchStaticLogs( out var h );
        try
        {
            d.NeedLeakSample.ShouldBeTrue();
            d.OnLeaked( "Info m1.2026-09-20 - the leaked text" );
            d.OnLeaked( null );
            d.OnLeaked( null );
            d.LeakedCount.ShouldBe( 3 );
            d.LeakSamples.Count.ShouldBe( 1 );

            logs.ShouldBeEmpty( "Nothing is logged from the finalizer thread." );

            // The report is emitted when an observation window closes.
            d.OnAcquire();
            d.OnRelease();
            d.CloseWindow();

            logs.Count.ShouldBe( 1 );
            logs[0].ShouldContain( "Leak detected in the Test pool: 3 object(s) have been garbage collected while still alive" );
            logs[0].ShouldContain( "the leaked text" );

            // Already reported: closing more windows says nothing more.
            d.CloseWindow();
            d.CloseWindow();
            logs.Count.ShouldBe( 1 );
        }
        finally
        {
            ActivityMonitor.OnStaticLog -= h;
        }
    }

    [Test]
    public void capacity_growth_and_saturation_are_counted_but_never_logged()
    {
        var d = CreateDiagnostics();
        var logs = CatchStaticLogs( out var h );
        try
        {
            // Growing a pool from 200 to 2000 by increments of 10 is 180 capacity increases, followed here
            // by 10000 drops. The user cannot configure any of it and nothing is lost: it is not worth a line.
            for( int i = 0; i < 180; ++i ) d.OnCapacityIncreased();
            for( int i = 0; i < 10_000; ++i ) d.OnSaturated();
            d.CapacityIncreaseCount.ShouldBe( 180 );
            d.SaturatedCount.ShouldBe( 10_000 );

            d.CloseWindow();
            d.CloseWindow();
            logs.ShouldBeEmpty( "A peak of activity is observable, not loggable." );
        }
        finally
        {
            ActivityMonitor.OnStaticLog -= h;
        }
    }

    [Test]
    public void a_leak_report_carries_the_capacity_numbers_as_context()
    {
        var d = CreateDiagnostics();
        var logs = CatchStaticLogs( out var h );
        try
        {
            for( int i = 0; i < 7; ++i ) d.OnSaturated();
            d.OnLeaked( null );
            d.OnAcquire();
            d.OnRelease();
            d.CloseWindow();

            logs.Count.ShouldBe( 1 );
            logs[0].ShouldContain( "Leak detected in the Test pool" );
            logs[0].ShouldContain( "pool saturations: 7", customMessage: "The numbers that are never logged on their own show up where they help." );
        }
        finally
        {
            ActivityMonitor.OnStaticLog -= h;
        }
    }

    [Test]
    public void RecentPeakAliveCount_remembers_the_whole_history()
    {
        var d = CreateDiagnostics();
        Window( d, peak: 1000, floor: 0 );
        d.LastWindowPeakAliveCount.ShouldBe( 1000 );
        d.RecentPeakAliveCount.ShouldBe( 1000 );

        // 5 quiet windows: the burst is still remembered (this is what keeps a pool from oscillating).
        for( int i = 0; i < 5; ++i )
        {
            Window( d, peak: 3, floor: 0 );
            d.LastWindowPeakAliveCount.ShouldBe( 3 );
            d.RecentPeakAliveCount.ShouldBe( 1000 );
        }
        // The 6th one evicts it.
        Window( d, peak: 3, floor: 0 );
        d.RecentPeakAliveCount.ShouldBe( 3 );
    }

    [Test]
    public void a_report_consumes_the_samples_it_shows()
    {
        // A short back off so that a second report can be emitted: the default is 5 seconds.
        var d = new PoolDiagnostics( "Test", windowMilliseconds: 600_000, floorHistoryCount: 6, leakGrowthThreshold: 10 );
        var logs = CatchStaticLogs( out var h );
        try
        {
            d.OnLeaked( "first leak" );
            d.LeakSamples.Count.ShouldBe( 1 );
            d.CloseWindow();
            logs.Count.ShouldBe( 1 );
            logs[0].ShouldContain( "first leak" );
            d.LeakSamples.ShouldBeEmpty( "The report consumed them." );
            d.NeedLeakSample.ShouldBeTrue( "Sampling resumes for the next period." );

            // A leak appearing later must be described too, not hidden behind the first ones.
            for( int i = 0; i < PoolDiagnostics.MaxLeakSampleCount; ++i ) d.OnLeaked( $"later leak {i}" );
            d.LeakSamples.Count.ShouldBe( PoolDiagnostics.MaxLeakSampleCount );
            d.NeedLeakSample.ShouldBeFalse( "The period is full." );

            // The back off has not elapsed: nothing new is reported yet, and the samples are kept.
            d.CloseWindow();
            logs.Count.ShouldBe( 1 );
            d.LeakSamples.Count.ShouldBe( PoolDiagnostics.MaxLeakSampleCount, "Kept for the next report." );
        }
        finally
        {
            ActivityMonitor.OnStaticLog -= h;
        }
    }

    [Test]
    public void a_leak_that_never_releases_anything_is_still_reported()
    {
        // 1ms windows: the acquire path alone must be able to close them.
        var d = new PoolDiagnostics( "Test", windowMilliseconds: 1, floorHistoryCount: 6, leakGrowthThreshold: 10 );
        var logs = CatchStaticLogs( out var h );
        try
        {
            // The pathological leak: everything is acquired, nothing is ever released.
            d.OnLeaked( "never released" );
            for( int i = 0; i < 100 && logs.Count == 0; ++i )
            {
                Thread.Sleep( 2 );
                d.OnAcquire();
            }
            logs.Count.ShouldBe( 1, "OnRelease() was never called, yet the leak came out." );
            logs[0].ShouldContain( "never released" );
        }
        finally
        {
            ActivityMonitor.OnStaticLog -= h;
        }
    }

    [Test]
    public void Reset_clears_the_history()
    {
        var d = CreateDiagnostics();
        d.OnAcquire();
        d.OnAcquire();
        d.OnSaturated();
        d.OnCapacityIncreased();
        d.OnLeaked( "sample" );
        d.OnOverRelease();
        d.LeakSamples.Count.ShouldBe( 1 );
        d.CloseWindow();
        d.LeakSamples.ShouldBeEmpty( "Consumed by the report that showed it." );

        // Give Reset() something left to clear.
        d.OnLeaked( "another sample" );

        d.AliveCount.ShouldBe( 2 );
        d.PeakAliveCount.ShouldBe( 2 );
        d.RecentPeakAliveCount.ShouldBe( 2 );
        d.LeakedCount.ShouldBe( 2 );
        d.OverReleaseCount.ShouldBe( 1 );
        d.SaturatedCount.ShouldBe( 1 );
        d.CapacityIncreaseCount.ShouldBe( 1 );
        d.LeakSamples.Count.ShouldBe( 1 );

        d.Reset();
        d.AliveCount.ShouldBe( 2, "The alive count is real accounting: preserved by default." );
        d.LeakedCount.ShouldBe( 0 );
        d.OverReleaseCount.ShouldBe( 0 );
        d.SaturatedCount.ShouldBe( 0 );
        d.CapacityIncreaseCount.ShouldBe( 0 );
        d.RecentPeakAliveCount.ShouldBe( 0 );
        d.LeakSamples.ShouldBeEmpty();
        d.LastLeakReport.ShouldBeNull();

        d.Reset( resetAliveCount: true );
        d.AliveCount.ShouldBe( 0 );
    }
}
