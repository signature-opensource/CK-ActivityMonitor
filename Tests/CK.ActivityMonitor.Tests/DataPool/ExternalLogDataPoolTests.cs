using Shouldly;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CK.Core.Tests.Monitoring;

[TestFixture]
public class ExternalLogDataPoolTests
{
    [TearDown]
    public void CheckNoMoreAliveExternalLogData()
    {
        ActivityMonitorExternalLogData.AliveCount.ShouldBe( 0 );
    }

    static ActivityMonitorLogData CreateData( string text = "text", string? fileName = "fileName", int lineNumber = 0 )
        => ActivityMonitor.StaticLogger.CreateActivityMonitorLogData( LogLevel.Info, ActivityMonitor.Tags.Empty, text, null, fileName, lineNumber, false );

    [Test]
    public void ActivityMonitorExternalLogData_LogTime_Text_and_Tags_cannot_be_changed_after_AcquireExternalData_call()
    {
        var d = CreateData();

        d.SetLogTime( DateTimeStamp.UtcNow );
        d.SetTags( ActivityMonitor.Tags.SecurityCritical );
        d.SetText( "Another..." );

        ActivityMonitorExternalLogData e = d.AcquireExternalData();
        ActivityMonitorExternalLogData.AliveCount.ShouldBe( 1 );

        Util.Invokable( () => d.SetLogTime( DateTimeStamp.UtcNow ) ).ShouldThrow<InvalidOperationException>();
        Util.Invokable( () => d.SetTags( ActivityMonitor.Tags.SecurityCritical ) ).ShouldThrow<InvalidOperationException>();
        Util.Invokable( () => d.SetText( "Another..." ) ).ShouldThrow<InvalidOperationException>();

        ActivityMonitorExternalLogData e2 = d.AcquireExternalData();
        e2.ShouldBeSameAs( e );
        ActivityMonitorExternalLogData.AliveCount.ShouldBe( 1 );
        e.Release();
        ActivityMonitorExternalLogData.AliveCount.ShouldBe( 1 );
        e.Release();
    }

    [Test]
    public void pool_saturation_is_silent_and_is_not_a_leak()
    {
        var diag = ActivityMonitorExternalLogData.PoolDiagnostics;
        diag.Reset();

        var levels = new List<LogLevel>();
        var texts = new List<string>();
        ActivityMonitor.StaticLogHandler h = delegate ( ref ActivityMonitorLogData d )
        {
            levels.Add( d.MaskedLevel );
            texts.Add( d.Text );
        };
        ActivityMonitor.OnStaticLog += h;
        try
        {
            int initialCapacity = ActivityMonitorExternalLogData.CurrentPoolCapacity;
            int max = ActivityMonitorExternalLogData.MaximalCapacity;

            // 10 more than the maximal capacity are simultaneously alive: this is a peak of activity.
            var alive = new List<ActivityMonitorExternalLogData>();
            for( int i = 0; i < max + 10; ++i ) alive.Add( CreateData().AcquireExternalData() );
            ActivityMonitorExternalLogData.PooledEntryCount.ShouldBe( 0, "The pool has been drained." );
            texts.ShouldBeEmpty( "Acquiring says nothing: only Release() can see the pool filling up." );

            // Returning them fills the pool up to its maximal capacity: the 10 in excess are dropped.
            foreach( var e in alive ) e.Release();
            alive.Clear();

            ActivityMonitorExternalLogData.CurrentPoolCapacity.ShouldBe( max );
            ActivityMonitorExternalLogData.PooledEntryCount.ShouldBe( max );

            // Growing from 200 to 2000 by increments of 10 is 180 capacity increases, then 10 objects are
            // dropped. The pool capacity is a const, nothing is lost, and this happens exactly when the log
            // pipeline is the most loaded: not one line is emitted about any of it.
            int expectedIncrements = (max - initialCapacity) / ActivityMonitorExternalLogData.PoolCapacityIncrement;
            diag.CapacityIncreaseCount.ShouldBe( expectedIncrements );
            diag.SaturatedCount.ShouldBe( 10 );
            diag.PeakAliveCount.ShouldBe( max + 10, "It is observable..." );
            texts.ShouldBeEmpty( "...but it is not logged." );

            diag.CloseWindow();
            texts.ShouldBeEmpty( "Closing the observation window says nothing either." );
            levels.ShouldBeEmpty();

            // And above all: this is not reported as a leak.
            diag.LeakedCount.ShouldBe( 0 );
            diag.LastLeakReport.ShouldBeNull();

            // Saturating a lot more stays just as silent.
            for( int i = 0; i < max + 50; ++i ) alive.Add( CreateData().AcquireExternalData() );
            foreach( var e in alive ) e.Release();
            alive.Clear();
            diag.CloseWindow();
            diag.SaturatedCount.ShouldBe( 60 );
            texts.ShouldBeEmpty();
        }
        finally
        {
            ActivityMonitor.OnStaticLog -= h;
        }
    }

    [Test]
    public void the_pool_trims_itself_back_to_the_observed_demand()
    {
        var diag = ActivityMonitorExternalLogData.PoolDiagnostics;
        diag.Reset();
        int max = ActivityMonitorExternalLogData.MaximalCapacity;

        // A peak of activity grows the pool up to its maximal capacity.
        var alive = new List<ActivityMonitorExternalLogData>();
        for( int i = 0; i < max; ++i ) alive.Add( CreateData().AcquireExternalData() );
        foreach( var e in alive ) e.Release();
        alive.Clear();
        ActivityMonitorExternalLogData.CurrentPoolCapacity.ShouldBe( max );
        ActivityMonitorExternalLogData.PooledEntryCount.ShouldBe( max );

        // Closing the window of the peak itself must not trim anything.
        diag.CloseWindow();
        ActivityMonitorExternalLogData.CurrentPoolCapacity.ShouldBe( max );

        // The burst stays in the history for the whole window history: a lull must not shrink the pool
        // only to have it grow back (and warn) on the next burst.
        for( int i = 0; i < 5; ++i )
        {
            QuietWindow();
            ActivityMonitorExternalLogData.CurrentPoolCapacity.ShouldBe( max, "The recent peak still remembers the burst." );
        }

        // This one evicts the burst from the history: the pool goes back to its initial capacity.
        QuietWindow();
        ActivityMonitorExternalLogData.CurrentPoolCapacity.ShouldBe( ActivityMonitorExternalLogData.InitialPoolCapacity );
        ActivityMonitorExternalLogData.PooledEntryCount.ShouldBeLessThanOrEqualTo( ActivityMonitorExternalLogData.InitialPoolCapacity + 1 );

        void QuietWindow()
        {
            var e = CreateData().AcquireExternalData();
            e.Release();
            diag.CloseWindow();
        }
    }

    [Test]
    public void a_data_that_is_never_released_is_detected_when_it_is_collected()
    {
        var diag = ActivityMonitorExternalLogData.PoolDiagnostics;
        diag.Reset();

        Leak();

        for( int i = 0; i < 5 && diag.LeakedCount < 3; ++i )
        {
            GC.Collect( 2, GCCollectionMode.Forced, blocking: true );
            GC.WaitForPendingFinalizers();
        }

        diag.LeakedCount.ShouldBe( 3 );
        var samples = diag.LeakSamples;
        samples.Count.ShouldBe( 3 );

        // Finalization order is not specified: find the samples instead of indexing them.
        var leaked = samples.Single( s => s.Contains( "I'm leaked" ) );
        leaked.ShouldContain( "Info" );
        leaked.ShouldContain( "@Some/Source/File.cs:3712",
                              customMessage: "The source address of the log that leaked: this is what identifies the missing Release()." );

        // Whatever is logged, a sample stays small: the text is truncated.
        var big = samples.Single( s => s.Contains( "@Other/File.cs:12" ) );
        big.ShouldContain( new string( 'x', 64 ) );
        big.ShouldNotContain( new string( 'x', 65 ) );
        big.ShouldEndWith( "..." );

        // An ExternalLog has no [CallerFilePath]: it must not render as "@:0".
        var external = samples.Single( s => s.Contains( "I'm an external log" ) );
        external.ShouldContain( "<external>" );
        external.ShouldNotContain( "@" );

        // A leaked data never comes back: it holds the alive count up forever. That IS the leak,
        // and it is why the pool saturation can never see it.
        diag.AliveCount.ShouldBe( 3 );
        diag.Reset( resetAliveCount: true );

        // Not inlined and in its own method so that no reference to the data survives on the stack.
        [MethodImpl( MethodImplOptions.NoInlining )]
        static void Leak()
        {
            var d = CreateData( "I'm leaked", "Some/Source/File.cs", 3712 );
            d.AcquireExternalData();
            var big = CreateData( new string( 'x', 500 ), "Other/File.cs", 12 );
            big.AcquireExternalData();
            var external = CreateData( "I'm an external log", fileName: null );
            external.AcquireExternalData();
            // Release() is deliberately not called.
        }
    }

    [Test]
    public void a_non_pooled_data_is_never_seen_as_a_leak()
    {
        var diag = ActivityMonitorExternalLogData.PoolDiagnostics;
        diag.Reset();

        CreateNonPooled();

        for( int i = 0; i < 3; ++i )
        {
            GC.Collect( 2, GCCollectionMode.Forced, blocking: true );
            GC.WaitForPendingFinalizers();
        }
        diag.LeakedCount.ShouldBe( 0 );
        diag.AliveCount.ShouldBe( 0, "A non pooled data is not accounted for." );

        [MethodImpl( MethodImplOptions.NoInlining )]
        static void CreateNonPooled()
        {
            var d = CreateData( "Not pooled" );
            d.CreateNonPooledData();
        }
    }
}
