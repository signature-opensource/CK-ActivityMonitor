using Shouldly;
using System;
using System.Linq;
using NUnit.Framework;

namespace CK.Core.Tests.Monitoring;

[TestFixture]
public class TagFilteringTests
{
    [Test]
    public void RemoveFilter_and_RemoveDefaultFilter_return_whether_the_tag_has_been_removed()
    {
        ActivityMonitor.Tags.ClearFilters();
        ActivityMonitor.Tags.AddFilter( TestHelper.Tag1, LogClamper.Parse( "Release!" ) );
        try
        {
            ActivityMonitor.Tags.RemoveFilter( TestHelper.Tag2 ).ShouldBeFalse();
            ActivityMonitor.Tags.RemoveFilter( TestHelper.Tag1 ).ShouldBeTrue();
            ActivityMonitor.Tags.RemoveFilter( TestHelper.Tag1 ).ShouldBeFalse();
            ActivityMonitor.Tags.Filters.ShouldNotContain( f => f.T == TestHelper.Tag1 );

            ActivityMonitor.Tags.AddDefaultFilter( TestHelper.Tag3, LogClamper.Parse( "Release!" ) );
            ActivityMonitor.Tags.RemoveDefaultFilter( TestHelper.Tag2 ).ShouldBeFalse();
            ActivityMonitor.Tags.RemoveDefaultFilter( TestHelper.Tag3 ).ShouldBeTrue();
            ActivityMonitor.Tags.RemoveDefaultFilter( TestHelper.Tag3 ).ShouldBeFalse();
        }
        finally
        {
            ActivityMonitor.Tags.ClearFilters();
            ActivityMonitor.Tags.RemoveDefaultFilter( TestHelper.Tag3 );
        }
    }

    [Test]
    public void single_tag_configuration()
    {
        var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var c = m.Output.RegisterClient( new StupidStringClient() );

        ActivityMonitor.DefaultFilter.ShouldBe( LogFilter.Trace );

        m.ActualFilter.ShouldBe( LogFilter.Undefined );
        m.Trace( "Trace1" );
        m.OpenTrace( "OTrace1" );

        // For logs with Tag1, Release {Error, Error} is applied.
        ActivityMonitor.Tags.SetFilters( new[] { (TestHelper.Tag1, LogClamper.Parse( "Release!" )) } );

        m.Trace( TestHelper.Tag1, "NOSHOW" );
        m.OpenTrace( TestHelper.Tag1, "NOSHOW" );
        m.Trace( "Trace2" );
        m.Warn( TestHelper.Tag1 | TestHelper.Tag2, "NOSHOW" );
        m.OpenWarn( TestHelper.Tag1 | TestHelper.Tag2, "NOSHOW" );
        m.Warn( TestHelper.Tag1 | TestHelper.Tag2, $"NOSHOW{Environment.TickCount}" );
        m.OpenWarn( TestHelper.Tag1 | TestHelper.Tag2, $"NOSHOW{Environment.TickCount}" );
        m.OpenTrace( "OTrace2" );

        ActivityMonitor.Tags.ClearFilters();

        c.Entries.Select( e => e.Data.Text ).ToArray().ShouldBe( ["Trace1", "OTrace1", "Trace2", "OTrace2"] );
    }

    [Test]
    public void tags_configuration_orders_matters_since_first_subset_matched_wins()
    {
        var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var c = m.Output.RegisterClient( new StupidStringClient() );

        int hole = Environment.TickCount % 10;

        ActivityMonitor.DefaultFilter.ShouldBe( LogFilter.Trace );

        // ActualFilter is Terse, 
        m.MinimalFilter = LogFilter.Terse;
        m.ActualFilter.ShouldBe( LogFilter.Terse );

        m.Trace( "NOSHOW" );
        m.OpenTrace( "NOSHOW" );

        ActivityMonitor.Tags.SetFilters( new[]
        {
                // A multi-tag should be above its subsets otherwise it'll be optimized out.
                (TestHelper.Tag1 | TestHelper.Tag2 | TestHelper.Tag3, LogClamper.Parse( "Monitor!" )),
                // Tag2 => Debug is the top priority here (Clamping or not a Debug filter doesn't matter).
                (TestHelper.Tag2, LogClamper.Parse( "Debug" )),
                // Tag1 => Cuts to Trace: Tag1|Tag2 in Debug will appear because of the above,
                //         but Tag1 alone in Debug will not appear.
                (TestHelper.Tag1, LogClamper.Parse( "Trace!" )),
                // Tag3 (alone) will not log Error lines.
                (TestHelper.Tag3, LogClamper.Parse( "{Trace,Fatal}!" )),
        } );

        m.Trace( TestHelper.Tag1, "Trace1" );
        m.Debug( TestHelper.Tag1, "NOSHOW" );
        m.Debug( TestHelper.Tag1, $"NOSHOW{hole}" );
        m.Log( LogLevel.Debug, TestHelper.Tag1, "NOSHOW" );
        m.Log( LogLevel.Debug, TestHelper.Tag1, $"NOSHOW{hole}" );
        m.Trace( TestHelper.Tag1, $"Trace1{hole}" );

        m.Debug( TestHelper.Tag2, "Trace2" );
        m.Debug( TestHelper.Tag2, $"Trace{hole}" );
        m.Log( LogLevel.Debug, TestHelper.Tag2, "Log" );
        m.Log( LogLevel.Debug, TestHelper.Tag2, $"Log{hole}" );

        using( m.OpenTrace( TestHelper.Tag3, "OTrace1" ) )
        {
            m.MinimalFilter = LogFilter.Trace;
            m.Trace( "TraceNoTag" );
            m.Trace( $"TraceNoTag{hole}" );
        }
        m.MinimalFilter.ShouldBe( LogFilter.Terse );
        m.ActualFilter.ShouldBe( LogFilter.Terse );

        m.Error( TestHelper.Tag3, "NOSHOW" );
        m.Error( TestHelper.Tag3, $"NOSHOW{hole}" );

        m.Debug( TestHelper.Tag1 | TestHelper.Tag2, "Combined" );
        m.Debug( TestHelper.Tag1 | TestHelper.Tag2, $"Combined{hole}" );

        // Monitor! is {Trace,Warn}!
        m.Info( TestHelper.Tag1 | TestHelper.Tag2 | TestHelper.Tag3, "NOSHOW" );
        m.Warn( TestHelper.Tag1 | TestHelper.Tag2 | TestHelper.Tag3, "WarnInTerse" );

        ActivityMonitor.Tags.ClearFilters();
        m.Trace( "NOSHOW" );
        m.OpenTrace( "NOSHOW" );

        c.Entries.Select( e => e.Data.Text ).Concatenate()
            .ShouldBe( $"Trace1, Trace1{hole}, Trace2, Trace{hole}, Log, Log{hole}, OTrace1, TraceNoTag, TraceNoTag{hole}, Combined, Combined{hole}, WarnInTerse" );
    }

    [Test]
    public void filter_optimizations_on_DefaultFilters()
    {
        ActivityMonitor.Tags.DefaultFilters.ShouldBeEmpty();

        // None are always skipped.
        ActivityMonitor.Tags.AddDefaultFilter( TestHelper.Tag1, LogClamper.Parse( "{None,None}!" ) )
            .ShouldBeEmpty();
        ActivityMonitor.Tags.AddDefaultFilter( TestHelper.Tag1, LogClamper.Parse( "{None,None}" ) )
            .ShouldBeEmpty();

        var debugC = LogClamper.Parse( "Debug!" );
        var traceC = LogClamper.Parse( "Trace!" );
        var releaseC = LogClamper.Parse( "Release!" );

        ActivityMonitor.Tags.AddDefaultFilter( TestHelper.Tag1, debugC )
            .ShouldBe( new[] { (TestHelper.Tag1, debugC) } );
        ActivityMonitor.Tags.DefaultFilters.ShouldBe( new[] { (TestHelper.Tag1, debugC) } );
        ActivityMonitor.Tags.Filters.ShouldBe( ActivityMonitor.Tags.DefaultFilters );

        // This replaces the [Tag1,Debug!]
        ActivityMonitor.Tags.AddDefaultFilter( TestHelper.Tag1, traceC )
            .ShouldBe( new[] { (TestHelper.Tag1, traceC) } );
        ActivityMonitor.Tags.DefaultFilters.ShouldBe( new[] { (TestHelper.Tag1, traceC) } );
        ActivityMonitor.Tags.Filters.ShouldBe( ActivityMonitor.Tags.DefaultFilters );

        // New filters come above (highest priority).
        ActivityMonitor.Tags.AddDefaultFilter( TestHelper.Tag2, debugC )
            .ShouldBe( new[] { (TestHelper.Tag2, debugC), (TestHelper.Tag1, traceC) } );
        ActivityMonitor.Tags.DefaultFilters.ShouldBe( new[] { (TestHelper.Tag2, debugC), (TestHelper.Tag1, traceC) } );
        ActivityMonitor.Tags.Filters.ShouldBe( ActivityMonitor.Tags.DefaultFilters );

        ActivityMonitor.Tags.AddDefaultFilter( TestHelper.Tag2 | TestHelper.Tag1, releaseC )
            .ShouldBe( new[] { (TestHelper.Tag2 | TestHelper.Tag1, releaseC), (TestHelper.Tag2, debugC), (TestHelper.Tag1, traceC) } );
        ActivityMonitor.Tags.DefaultFilters.ShouldBe( new[] { (TestHelper.Tag2 | TestHelper.Tag1, releaseC), (TestHelper.Tag2, debugC), (TestHelper.Tag1, traceC) } );
        ActivityMonitor.Tags.Filters.ShouldBe( ActivityMonitor.Tags.DefaultFilters );

        // Adding Tag1 on top, removes Tag1|Tag2.
        ActivityMonitor.Tags.AddDefaultFilter( TestHelper.Tag1, debugC )
            .ShouldBe( new[] { (TestHelper.Tag1, debugC), (TestHelper.Tag2, debugC) } );
        ActivityMonitor.Tags.DefaultFilters.ShouldBe( new[] { (TestHelper.Tag1, debugC), (TestHelper.Tag2, debugC) } );
        ActivityMonitor.Tags.Filters.ShouldBe( ActivityMonitor.Tags.DefaultFilters );

        // Removing Tag2.
        ActivityMonitor.Tags.RemoveDefaultFilter( TestHelper.Tag2 ).ShouldBeTrue();
        ActivityMonitor.Tags.DefaultFilters.ShouldBe( new[] { (TestHelper.Tag1, debugC) } );
        ActivityMonitor.Tags.Filters.ShouldBe( ActivityMonitor.Tags.DefaultFilters );

        var traceNone = LogClamper.Parse( "{Trace,None}" );
        var noneTrace = LogClamper.Parse( "{None,Trace}" );

        // Using None for line or group keeps the two tags.
        ActivityMonitor.Tags.AddDefaultFilter( TestHelper.Tag1, traceNone );
        ActivityMonitor.Tags.DefaultFilters.ShouldBe( new[] { (TestHelper.Tag1, traceNone), (TestHelper.Tag1, debugC) } );
        ActivityMonitor.Tags.Filters.ShouldBe( ActivityMonitor.Tags.DefaultFilters );

        // As soon as line and group are both covered, remaining filters are removed.
        ActivityMonitor.Tags.AddDefaultFilter( TestHelper.Tag1, noneTrace );
        ActivityMonitor.Tags.DefaultFilters.ShouldBe( new[] { (TestHelper.Tag1, noneTrace), (TestHelper.Tag1, traceNone) } );
        ActivityMonitor.Tags.Filters.ShouldBe( ActivityMonitor.Tags.DefaultFilters );

        // Removes the first Tag1...
        ActivityMonitor.Tags.RemoveDefaultFilter( TestHelper.Tag1 ).ShouldBeTrue();
        ActivityMonitor.Tags.DefaultFilters.ShouldBe( new[] { (TestHelper.Tag1, traceNone) } );

        // ... and the second and last one.
        ActivityMonitor.Tags.RemoveDefaultFilter( TestHelper.Tag1 ).ShouldBeTrue();
        ActivityMonitor.Tags.DefaultFilters.ShouldBeEmpty();

        ActivityMonitor.Tags.RemoveDefaultFilter( TestHelper.Tag1 ).ShouldBeFalse();
    }

    [Test]
    public void final_filters_combines_filters_and_DefaultFilters()
    {
        ActivityMonitor.Tags.Filters.ShouldBeEmpty();

        var debugC = LogClamper.Parse( "Debug!" );
        var traceC = LogClamper.Parse( "Trace!" );
        var releaseC = LogClamper.Parse( "Release!" );

        ActivityMonitor.Tags.AddDefaultFilter( TestHelper.Tag1, debugC );
        ActivityMonitor.Tags.AddDefaultFilter( TestHelper.Tag2, traceC );
        ActivityMonitor.Tags.DefaultFilters.ShouldBe( new[] { (TestHelper.Tag2, traceC), (TestHelper.Tag1, debugC) } );
        ActivityMonitor.Tags.Filters.ShouldBe( ActivityMonitor.Tags.DefaultFilters );

        // Adding a filter to Filters that cancels a Default one.
        ActivityMonitor.Tags.AddFilter( TestHelper.Tag1, releaseC );
        // No change to the DefaultFilters.
        ActivityMonitor.Tags.DefaultFilters.ShouldBe( new[] { (TestHelper.Tag2, traceC), (TestHelper.Tag1, debugC) } );
        // But Filters doesn't contain it anymore.
        ActivityMonitor.Tags.Filters.ShouldBe( new[] { (TestHelper.Tag1, releaseC), (TestHelper.Tag2, traceC) } );

        // Canceling the last Default one.
        ActivityMonitor.Tags.AddFilter( TestHelper.Tag2, releaseC );
        // No change to the DefaultFilters.
        ActivityMonitor.Tags.DefaultFilters.ShouldBe( new[] { (TestHelper.Tag2, traceC), (TestHelper.Tag1, debugC) } );
        // No more default in Filters.
        ActivityMonitor.Tags.Filters.ShouldBe( new[] { (TestHelper.Tag2, releaseC), (TestHelper.Tag1, releaseC) } );

        // Back to defaults.
        ActivityMonitor.Tags.ClearFilters();
        ActivityMonitor.Tags.Filters.ShouldBe( ActivityMonitor.Tags.DefaultFilters );

        ActivityMonitor.Tags.RemoveDefaultFilter( TestHelper.Tag2 );
        ActivityMonitor.Tags.RemoveDefaultFilter( TestHelper.Tag1 );
        ActivityMonitor.Tags.DefaultFilters.ShouldBeEmpty();
        ActivityMonitor.Tags.Filters.ShouldBeEmpty();
    }
}
