using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using System.Xml.Linq;
using System.Collections.Generic;
using Shouldly;
using CK.Core.Impl;

namespace CK.Core.Tests.Monitoring;

[TestFixture]
public class ActivityMonitorTests
{
    [SetUp]
    public void ResetGlobalState()
    {
        TestHelper.Monitor.MinimalFilter = LogFilter.Undefined;
    }

    [Test]
    public void automatic_configuration_of_monitors_just_uses_ActivityMonitor_AutoConfiguration_delegate()
    {
        StupidStringClient c = new StupidStringClient();

        ActivityMonitor.AutoConfiguration = null;
        ActivityMonitor.AutoConfiguration += m => m.Output.RegisterClient( c );
        int i = 0;
        ActivityMonitor.AutoConfiguration += m => m.UnfilteredLog( LogLevel.Info, null, $"This monitors has been created at {DateTime.UtcNow:O}, n°{++i}", null );

        ActivityMonitor monitor1 = new ActivityMonitor();
        ActivityMonitor monitor2 = new ActivityMonitor();

        c.ToString().ShouldContain( "This monitors has been created at" );
        c.ToString().ShouldContain( "n°1" );
        c.ToString().ShouldContain( "n°2" );

        ActivityMonitor.AutoConfiguration = null;
    }

    [Test]
    public void registering_multiple_times_the_same_client_is_an_error()
    {
        ActivityMonitor.AutoConfiguration = null;
        IActivityMonitor monitor = new ActivityMonitor();
        monitor.Output.Clients.Length.ShouldBe( 0 );

        var counter = new ActivityMonitorErrorCounter();
        monitor.Output.RegisterClient( counter );
        monitor.Output.Clients.Length.ShouldBe( 1 );
        Action fail = () => TestHelper.Monitor.Output.RegisterClient( counter );
        fail.ShouldThrow<InvalidOperationException>( "Counter can be registered in one source at a time." );

        var pathCatcher = new ActivityMonitorPathCatcher();
        monitor.Output.RegisterClient( pathCatcher );
        monitor.Output.Clients.Length.ShouldBe( 2 );
        fail = () => TestHelper.Monitor.Output.RegisterClient( pathCatcher );
        fail.ShouldThrow<InvalidOperationException>( "PathCatcher can be registered in one source at a time." );

        monitor.Output.UnregisterClient( counter );
        monitor.Output.UnregisterClient( pathCatcher );
        monitor.Output.Clients.Length.ShouldBe( 0 );
    }

    [Test]
    public void registering_a_null_client_is_an_error()
    {
        IActivityMonitor monitor = new ActivityMonitor();
        Action fail = () => monitor.Output.RegisterClient( null! );
        fail.ShouldThrow<ArgumentNullException>();
        fail = () => monitor.Output.UnregisterClient( null! );
        fail.ShouldThrow<ArgumentNullException>();
    }

    [Test]
    public void RegisterUniqueClient_skips_null_client_from_factory_and_returns_null()
    {
        var monitor = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        monitor.Output.RegisterUniqueClient<ActivityMonitorConsoleClient>( c => false, () => null ).ShouldBeNull();
    }

    [Test]
    public void closing_group_restores_previous_AutoTags_and_MinimalFilter()
    {
        ActivityMonitor monitor = new ActivityMonitor();
        using( monitor.OpenTrace( "G1" ) )
        {
            monitor.AutoTags = ActivityMonitor.Tags.Register( "Tag" );
            monitor.MinimalFilter = LogFilter.Monitor;
            using( monitor.OpenWarn( "G2" ) )
            {
                monitor.AutoTags = ActivityMonitor.Tags.Register( "A|B|C" );
                monitor.MinimalFilter = LogFilter.Release;
                monitor.AutoTags.ToString().ShouldBe( "A|B|C" );
                monitor.MinimalFilter.ShouldBe( LogFilter.Release );
            }
            monitor.AutoTags.ToString().ShouldBe( "Tag" );
            monitor.MinimalFilter.ShouldBe( LogFilter.Monitor );
        }
        monitor.AutoTags.ShouldBeSameAs( ActivityMonitor.Tags.Empty );
        monitor.MinimalFilter.ShouldBe( LogFilter.Undefined );
    }

    [Test]
    public void sending_a_null_or_empty_text_is_transformed_into_no_log_text()
    {
        var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var c = m.Output.RegisterClient( new StupidStringClient() );
        m.Trace( "" );
        m.UnfilteredLog( LogLevel.Error, null, null, null );
        m.OpenTrace( (Exception?)null! );
        m.OpenInfo( "" );

        c.Entries.Count.ShouldBe( 4 );
        c.Entries.All( e => e.Data.Text == ActivityMonitor.NoLogText );
    }

    [Test]
    public void display_conclusions()
    {
        IActivityMonitor monitor = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        monitor.Output.RegisterClient( new StupidStringClient() );
        monitor.Output.RegisterClient( new StupidXmlClient( new StringWriter() ) );
        monitor.Output.Clients.Length.ShouldBe( 2 );

        var tag1 = ActivityMonitor.Tags.Register( "Product" );
        var tag2 = ActivityMonitor.Tags.Register( "Sql" );
        var tag3 = ActivityMonitor.Tags.Register( "Combined Tag|Sql|Engine V2|Product" );

        using( monitor.OpenError( "MainGroupError" ).ConcludeWith( () => "EndMainGroupError" ) )
        {
            using( monitor.OpenTrace( "MainGroup" ).ConcludeWith( () => "EndMainGroup" ) )
            {
                monitor.Trace( tag1, "First" );
                using( monitor.TemporarilySetAutoTags( tag1 ) )
                {
                    monitor.Trace( "Second" );
                    monitor.Trace( tag3, "Third" );
                    using( monitor.TemporarilySetAutoTags( tag2 ) )
                    {
                        monitor.Info( "First" );
                    }
                }
                using( monitor.OpenInfo( "InfoGroup" ).ConcludeWith( () => "Conclusion of Info Group (no newline)." ) )
                {
                    monitor.Info( "Second" );
                    monitor.Trace( "Fourth" );

                    string warnConclusion = "Conclusion of Warn Group" + Environment.NewLine + "with more than one line int it.";
                    using( monitor.OpenWarn( $"WarnGroup {4} - Now = {DateTime.UtcNow}" ).ConcludeWith( () => warnConclusion ) )
                    {
                        monitor.Info( "Warn!" );
                        monitor.CloseGroup( "User conclusion with multiple lines."
                            + Environment.NewLine + "It will be displayed on "
                            + Environment.NewLine + "multiple lines." );
                    }
                    monitor.CloseGroup( "Conclusions on one line are displayed separated by dash." );
                }
            }

            if( TestHelper.LogsToConsole )
            {
                Console.WriteLine( monitor.Output.Clients.OfType<StupidStringClient>().Single().Writer );
                Console.WriteLine( monitor.Output.Clients.OfType<StupidXmlClient>().Single().InnerWriter );
            }
        }

        IReadOnlyList<XElement> elements = monitor.Output.Clients.OfType<StupidXmlClient>().Single().XElements;

        elements.Descendants( "Info" ).Count().ShouldBe( 3 );
        elements.Descendants( "Trace" ).Count().ShouldBe( 2 );
    }

    [Test]
    public void exceptions_are_deeply_dumped()
    {
        IActivityMonitor l = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var wLogLovely = new StringBuilder();
        var rawLog = new StupidStringClient();
        l.Output.RegisterClient( rawLog );
        var logLovely = new ActivityMonitorTextWriterClient( ( s ) => wLogLovely.Append( s ) );
        l.Output.RegisterClient( logLovely );

        l.Error( new Exception( "EXERROR-1" ) );
        using( l.OpenFatal( "EXERROR-TEXT2", new Exception( "EXERROR-2" ) ) )
        {
            try
            {
                throw new Exception( "EXERROR-3" );
            }
            catch( Exception ex )
            {
                l.Trace( "EXERROR-TEXT3", ex );
            }
        }
        rawLog.ToString().ShouldContain( "EXERROR-1" );
        rawLog.ToString().ShouldContain( "EXERROR-2" ).ShouldContain( "EXERROR-TEXT2" );
        rawLog.ToString().ShouldContain( "EXERROR-3" ).ShouldContain( "EXERROR-TEXT3" );

        string text = wLogLovely.ToString();
        text.ShouldContain( "EXERROR-1" );
        text.ShouldContain( "EXERROR-2" ).ShouldContain( "EXERROR-TEXT2" );
        text.ShouldContain( "EXERROR-3" ).ShouldContain( "EXERROR-TEXT3" );
        text.ShouldContain( "Stack:" );
    }

    [Test]
    public void ending_a_monitor_send_an_unfilitered_MonitorEnd_tagged_info()
    {
        IActivityMonitor m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var rawLog = new StupidStringClient();
        m.Output.RegisterClient( rawLog );
        m.OpenFatal( "a group" );
        // OpenFatal or OpenError sets their scoped filter to Debug.
        m.MinimalFilter = LogFilter.Release;
        m.OpenInfo( "a (filtered) group" );
        m.Fatal( "a line" );
        m.Info( "a (filtered) line" );
        m.MonitorEnd();
        m.CloseGroup().ShouldBeFalse();
        string logs = rawLog.ToString();
        logs.ShouldNotContain( "(filtered)" )
            .ShouldMatch( "a group.*a line.*Done", "We used the default 'Done.' end text." );
    }

    [Test]
    public void AggregatedException_are_handled_specifically()
    {
        IActivityMonitor l = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var wLogLovely = new StringBuilder();
        var logLovely = new ActivityMonitorTextWriterClient( ( s ) => wLogLovely.Append( s ) );
        l.Output.RegisterClient( logLovely );


        l.Error( new Exception( "EXERROR-1" ) );
        using( l.OpenFatal( "EXERROR-TEXT2", new Exception( "EXERROR-2" ) ) )
        {
            try
            {
                throw new AggregateException(
                    new Exception( "EXERROR-Aggreg-1" ),
                    new AggregateException(
                        new Exception( "EXERROR-Aggreg-2-1" ),
                        new Exception( "EXERROR-Aggreg-2-2" )
                    ),
                    new Exception( "EXERROR-Aggreg-3" ) );
            }
            catch( Exception ex )
            {
                l.Error( "EXERROR-TEXT3", ex );
            }
        }
        string text = wLogLovely.ToString();
        text.ShouldContain( "EXERROR-Aggreg-1" );
        text.ShouldContain( "EXERROR-Aggreg-2-1" );
        text.ShouldContain( "EXERROR-Aggreg-2-2" );
        text.ShouldContain( "EXERROR-Aggreg-3" );
    }

    [Test]
    public void testing_filtering_levels()
    {
        LogFilter FatalFatal = new LogFilter( LogLevelFilter.Fatal, LogLevelFilter.Fatal );
        LogFilter WarnWarn = new LogFilter( LogLevelFilter.Warn, LogLevelFilter.Warn );

        IActivityMonitor l = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var log = l.Output.RegisterClient( new StupidStringClient() );
        using( l.TemporarilySetMinimalFilter( LogLevelFilter.Error, LogLevelFilter.Error ) )
        {
            l.Debug( "NO SHOW" );
            l.Trace( "NO SHOW" );
            l.Info( "NO SHOW" );
            l.Warn( "NO SHOW" );
            l.Error( "Error n°1." );
            using( l.TemporarilySetMinimalFilter( WarnWarn ) )
            {
                l.Debug( "NO SHOW" );
                l.Trace( "NO SHOW" );
                l.Info( "NO SHOW" );
                l.Warn( "Warn n°1." );
                l.Error( "Error n°2." );
                using( l.OpenWarn( "GroupWarn: this appears." ) )
                {
                    l.MinimalFilter.ShouldBe( WarnWarn, "Groups does not change the current filter level." );
                    l.Debug( "NO SHOW" );
                    l.Trace( "NO SHOW" );
                    l.Info( "NO SHOW" );
                    l.Warn( "Warn n°2." );
                    l.Error( "Error n°3." );
                    // Changing the level inside a Group.
                    l.MinimalFilter = FatalFatal;
                    l.Error( "NO SHOW" );
                    l.Fatal( "Fatal n°1." );
                }
                using( l.OpenInfo( "GroupInfo: NO SHOW." ) )
                {
                    l.MinimalFilter.ShouldBe( WarnWarn, "Groups does not change the current filter level." );
                    l.Debug( "NO SHOW" );
                    l.Trace( "NO SHOW" );
                    l.Info( "NO SHOW" );
                    l.Warn( "Warn n°2-bis." );
                    l.Error( "Error n°3-bis." );
                    // Changing the level inside a Group.
                    l.MinimalFilter = FatalFatal;
                    l.Error( "NO SHOW" );
                    l.Fatal( "Fatal n°1." );
                    using( l.OpenError( "GroupError: NO SHOW." ) )
                    {
                    }
                }
                l.MinimalFilter.ShouldBe( WarnWarn, "But Groups restores the original filter level when closed." );
                l.Debug( "NO SHOW" );
                l.Trace( "NO SHOW" );
                l.Info( "NO SHOW" );
                l.Warn( "Warn n°3." );
                l.Error( "Error n°4." );
                l.Fatal( "Fatal n°2." );
            }
            l.Debug( "NO SHOW" );
            l.Trace( "NO SHOW" );
            l.Info( "NO SHOW" );
            l.Warn( "NO SHOW" );
            l.Error( "Error n°5." );
        }
        string result = log.Writer.ToString();
        result.ShouldNotContain( "NO SHOW" );
        result.ShouldContain( "Error n°1." )
                    .ShouldContain( "Error n°2." )
                    .ShouldContain( "Error n°3." )
                    .ShouldContain( "Error n°3-bis." )
                    .ShouldContain( "Error n°4." )
                    .ShouldContain( "Error n°5." );
        result.ShouldContain( "Warn n°1." )
                    .ShouldContain( "Warn n°2." )
                    .ShouldContain( "Warn n°2-bis." )
                    .ShouldContain( "Warn n°3." );
        result.ShouldContain( "Fatal n°1." )
                    .ShouldContain( "Fatal n°2." );
    }

    [Test]
    public void mismatch_of_explicit_Group_disposing_is_handled()
    {
        IActivityMonitor l = new ActivityMonitor();
        var log = l.Output.RegisterClient( new StupidStringClient() );
        {
            IDisposable g0 = l.OpenTrace( "First" );
            IDisposable g1 = l.OpenTrace( "Second" );
            IDisposable g2 = l.OpenTrace( "Third" );

            g1.Dispose();
            l.Trace( "Inside First" );
            g0.Dispose();
            l.Trace( "At root" );

            var end = "Trace: Inside First" + Environment.NewLine + "-" + Environment.NewLine + "Trace: At root";
            log.Writer.ToString().ShouldEndWith( end );
        }
        {
            // g2 is closed after g1.
            IDisposable g0 = l.OpenTrace( "First" );
            IDisposable g1 = l.OpenTrace( "Second" );
            IDisposable g2 = l.OpenTrace( "Third" );
            log.Writer.GetStringBuilder().Clear();
            g1.Dispose();
            // g2 has already been disposed by g1. 
            // Nothing changed.
            g2.Dispose();
            l.Trace( "Inside First" );
            g0.Dispose();
            l.Trace( "At root" );

            var end = "Trace: Inside First" + Environment.NewLine + "-" + Environment.NewLine + "Trace: At root";
            log.Writer.ToString().ShouldEndWith( end );
        }
    }

    class ObjectAsConclusion
    {
        public override string ToString()
        {
            return "Explicit User Conclusion";
        }
    }

    [Test]
    public void appending_multiple_conclusions_to_a_group_is_possible()
    {
        IActivityMonitor l = new ActivityMonitor();
        l.Output.RegisterClient( new ActivityMonitorErrorCounter( true ) );
        var log = l.Output.RegisterClient( new StupidStringClient() );

        // No explicit close conclusion: Success!
        using( l.OpenTrace( "G" ).ConcludeWith( () => "From Opener" ) )
        {
            l.Error( "Pouf" );
            l.CloseGroup( new ObjectAsConclusion() );
        }
        log.Writer.ToString().ShouldContain( "Explicit User Conclusion, From Opener, 1 Error" );
    }

    [Test]
    public void ActivityMonitorPathCatcher_is_aClient_that_maintains_the_current_Group_path()
    {
        var monitor = new ActivityMonitor();
        ActivityMonitorPathCatcher p = monitor.Output.RegisterClient( new ActivityMonitorPathCatcher() );
        monitor.MinimalFilter = LogFilter.Debug;

        using( monitor.OpenDebug( "!D" ) )
        using( monitor.OpenTrace( "!T" ) )
        using( monitor.OpenInfo( "!I" ) )
        using( monitor.OpenWarn( "!W" ) )
        using( monitor.OpenError( "!E" ) )
        using( monitor.OpenFatal( "!F" ) )
        {
            p.DynamicPath.ToStringPath()
               .ShouldContain( "!D" ).ShouldContain( "!T" ).ShouldContain( "!I" ).ShouldContain( "!W" ).ShouldContain( "!E" ).ShouldContain( "!F" );
        }
    }

    [Test]
    public void ActivityMonitorPathCatcher_tests()
    {

        var monitor = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        ActivityMonitorPathCatcher p = new ActivityMonitorPathCatcher();
        monitor.Output.RegisterClient( p );

        monitor.Trace( "Trace n°1" );
        p.DynamicPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ).Single().ShouldBe( "Trace|Trace n°1" );
        p.LastErrorPath.ShouldBeEmpty();
        p.LastWarnOrErrorPath.ShouldBeEmpty();

        monitor.Trace( "Trace n°2" );
        p.DynamicPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ).Single().ShouldBe( "Trace|Trace n°2" );
        p.LastErrorPath.ShouldBeEmpty();
        p.LastWarnOrErrorPath.ShouldBeEmpty();

        monitor.Warn( "W1" );
        p.DynamicPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ).Single().ShouldBe( "Warn|W1" );
        p.LastErrorPath.ShouldBeEmpty();
        p.LastWarnOrErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ).Single().ShouldBe( "Warn|W1" );

        monitor.Error( "E2" );
        monitor.Warn( "W1bis" );
        p.DynamicPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ).Single().ShouldBe( "Warn|W1bis" );
        p.LastErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ).Single().ShouldBe( "Error|E2" );
        p.LastWarnOrErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ).Single().ShouldBe( "Warn|W1bis" );

        p.ClearLastWarnPath();
        p.LastErrorPath.ShouldNotBeNull();
        p.LastWarnOrErrorPath.ShouldBeEmpty();

        p.ClearLastErrorPath();
        p.LastErrorPath.ShouldBeEmpty();

        using( monitor.OpenTrace( "G1" ) )
        {
            using( monitor.OpenInfo( "G2" ) )
            {
                String.Join( ">", p.DynamicPath.Select( e => e.Text ) ).ShouldBe( "G1>G2" );
                p.LastErrorPath.ShouldBeEmpty();
                using( monitor.OpenTrace( "G3" ) )
                {
                    using( monitor.OpenInfo( "G4" ) )
                    {
                        monitor.Warn( "W1" );

                        String.Join( ">", p.DynamicPath.Select( e => e.Text ) ).ShouldBe( "G1>G2>G3>G4>W1" );

                        monitor.Info(
                            "Test With an exception: a Group is created. Since the text of the log is given, the Exception.Message must be displayed explicitly.",
                            new Exception( "An exception logged as an Info.",
                                new Exception( "With an inner exception. Since these exceptions have not been thrown, there is no stack trace." ) )
                                );

                        string.Join( ">", p.DynamicPath.Select( e => e.Text ) ).ShouldBe( "G1>G2>G3>G4>Test With an exception: a Group is created. Since the text of the log is given, the Exception.Message must be displayed explicitly." );

                        try
                        {
                            try
                            {
                                try
                                {
                                    try
                                    {
                                        throw new Exception( "Deepest exception." );
                                    }
                                    catch( Exception ex )
                                    {
                                        throw new Exception( "Yet another inner with inner Exception.", ex );
                                    }
                                }
                                catch( Exception ex )
                                {
                                    throw new Exception( "Exception with inner Exception.", ex );
                                }
                            }
                            catch( Exception ex )
                            {
                                throw new Exception( "Log without log text: the text of the entry is the Exception.Message.", ex );
                            }
                        }
                        catch( Exception ex )
                        {
                            monitor.Trace( ex );
                            p.DynamicPath.ToStringPath().Length.ShouldBeGreaterThan( 0 );
                        }

                        p.LastErrorPath.ShouldBeEmpty();
                        string.Join( ">", p.LastWarnOrErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Info|G2>Trace|G3>Info|G4>Warn|W1" );
                    }
                    String.Join( ">", p.DynamicPath.Select( e => e.ToString() ) ).ShouldBe( "G1>G2>G3>G4" );
                    p.LastErrorPath.ShouldBeEmpty();
                    String.Join( ">", p.LastWarnOrErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Info|G2>Trace|G3>Info|G4>Warn|W1" );

                    monitor.Error( "E1" );
                    String.Join( ">", p.DynamicPath.Select( e => e.Text ) ).ShouldBe( "G1>G2>G3>E1" );
                    String.Join( ">", p.LastErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Info|G2>Trace|G3>Error|E1" );
                    String.Join( ">", p.LastWarnOrErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Info|G2>Trace|G3>Error|E1" );
                }
                String.Join( ">", p.DynamicPath.Select( e => e.Text ) ).ShouldBe( "G1>G2>G3" );
                String.Join( ">", p.LastErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Info|G2>Trace|G3>Error|E1" );
                String.Join( ">", p.LastWarnOrErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Info|G2>Trace|G3>Error|E1" );
            }
            String.Join( ">", p.DynamicPath.Select( e => e.Text ) ).ShouldBe( "G1>G2" );
            using( monitor.OpenTrace( "G2Bis" ) )
            {
                String.Join( ">", p.DynamicPath.Select( e => e.Text ) ).ShouldBe( "G1>G2Bis" );
                String.Join( ">", p.LastErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Info|G2>Trace|G3>Error|E1" );
                String.Join( ">", p.LastWarnOrErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Info|G2>Trace|G3>Error|E1" );

                monitor.Warn( "W2" );
                String.Join( ">", p.DynamicPath.Select( e => e.Text ) ).ShouldBe( "G1>G2Bis>W2" );
                String.Join( ">", p.LastWarnOrErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Trace|G2Bis>Warn|W2" );
                String.Join( ">", p.LastErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Info|G2>Trace|G3>Error|E1" );
            }
            monitor.Fatal( "F1" );
            String.Join( ">", p.DynamicPath.Select( e => e.Text ) ).ShouldBe( "G1>F1" );
            String.Join( ">", p.LastWarnOrErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Fatal|F1" );
            String.Join( ">", p.LastErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Fatal|F1" );

            // Extraneous closing are ignored.
            monitor.CloseGroup( null );

            monitor.Warn( "W3" );
            String.Join( ">", p.DynamicPath.Select( e => e.Text ) ).ShouldBe( "W3" );
            String.Join( ">", p.LastWarnOrErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Warn|W3" );
            String.Join( ">", p.LastErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Fatal|F1" );

            // Extraneous closing are ignored.
            monitor.CloseGroup( null );

            monitor.Warn( "W4" );
            String.Join( ">", p.DynamicPath.Select( e => e.Text ) ).ShouldBe( "W4" );
            String.Join( ">", p.LastWarnOrErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Warn|W4" );
            String.Join( ">", p.LastErrorPath.Select( e => e.MaskedLevel.ToString() + '|' + e.Text ) ).ShouldBe( "Trace|G1>Fatal|F1" );

            p.ClearLastWarnPath( true );
            p.LastErrorPath.ShouldBeEmpty();
            p.LastWarnOrErrorPath.ShouldBeEmpty();
        }
    }

    [Test]
    public void ActivityMonitorErrorCounter_and_ActivityMonitorPathCatcher_Clients_work_together()
    {
        var monitor = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        // Registers the ErrorCounter first: it will be the last one to be called, but
        // this does not prevent the PathCatcher to work: the path elements reference the group
        // so that any conclusion arriving after PathCatcher.OnClosing are available.
        ActivityMonitorErrorCounter c = new ActivityMonitorErrorCounter();
        monitor.Output.RegisterClient( c );

        // Registers the PathCatcher now: it will be called BEFORE the ErrorCounter.
        ActivityMonitorPathCatcher p = new ActivityMonitorPathCatcher();
        monitor.Output.RegisterClient( p );

        c.GenerateConclusion.ShouldBeFalse( "False by default." );
        c.GenerateConclusion = true;
        c.Root.MaxLogLevel.ShouldBe( LogLevel.None );

        monitor.Trace( "T1" );
        c.Root.HasWarnOrError.ShouldBeFalse();
        c.Root.HasError.ShouldBeFalse();
        c.Root.MaxLogLevel.ShouldBe( LogLevel.Trace );
        c.Root.ToString().ShouldBeNull();

        monitor.Warn( "W1" );
        c.Root.HasWarnOrError.ShouldBeTrue();
        c.Root.HasError.ShouldBeFalse();
        c.Root.MaxLogLevel.ShouldBe( LogLevel.Warn );
        c.Root.ToString().ShouldNotBeNullOrEmpty();

        monitor.Error( "E2" );
        c.Root.HasWarnOrError.ShouldBeTrue();
        c.Root.HasError.ShouldBeTrue();
        c.Root.ErrorCount.ShouldBe( 1 );
        c.Root.MaxLogLevel.ShouldBe( LogLevel.Error );
        c.Root.ToString().ShouldNotBeNullOrEmpty();

        c.Root.ClearError();
        c.Root.HasWarnOrError.ShouldBeTrue();
        c.Root.HasError.ShouldBeFalse();
        c.Root.ErrorCount.ShouldBe( 0 );
        c.Root.MaxLogLevel.ShouldBe( LogLevel.Warn );
        c.Root.ToString().ShouldNotBeNull();

        c.Root.ClearWarn();
        c.Root.HasWarnOrError.ShouldBeFalse();
        c.Root.HasError.ShouldBeFalse();
        c.Root.MaxLogLevel.ShouldBe( LogLevel.Info );
        c.Root.ToString().ShouldBeNull();

        using( monitor.OpenTrace( "G1" ) )
        {
            string errorMessage;
            using( monitor.OpenInfo( "G2" ) )
            {
                monitor.Error( "E1" );
                monitor.Fatal( "F1" );
                c.Root.HasWarnOrError.ShouldBeTrue();
                c.Root.HasError.ShouldBeTrue();
                c.Root.ErrorCount.ShouldBe( 1 );
                c.Root.FatalCount.ShouldBe( 1 );
                c.Root.WarnCount.ShouldBe( 0 );

                using( monitor.OpenInfo( "G3" ) )
                {
                    c.Current.HasWarnOrError.ShouldBeFalse();
                    c.Current.HasError.ShouldBeFalse();
                    c.Current.ErrorCount.ShouldBe( 0 );
                    c.Current.FatalCount.ShouldBe( 0 );
                    c.Current.WarnCount.ShouldBe( 0 );

                    monitor.Error( "An error..." );

                    c.Current.HasWarnOrError.ShouldBeTrue();
                    c.Current.HasError.ShouldBeTrue();
                    c.Current.ErrorCount.ShouldBe( 1 );
                    c.Current.FatalCount.ShouldBe( 0 );
                    c.Current.WarnCount.ShouldBe( 0 );

                    errorMessage = String.Join( "|", p.LastErrorPath.Select( e => e.Text + '-' + e.GroupConclusion?.ToStringGroupConclusion() ) );
                    errorMessage.ShouldBe( "G1-|G2-|G3-|An error...-", "Groups are not closed: no conclusion exist yet." );
                }
                errorMessage = String.Join( "|", p.LastErrorPath.Select( e => e.Text + '-' + e.GroupConclusion?.ToStringGroupConclusion() ) );
                errorMessage.ShouldBe( "G1-|G2-|G3-1 Error|An error...-", "G3 is closed: its conclusion is available." );
            }
            errorMessage = String.Join( "|", p.LastErrorPath.Select( e => e.Text + '-' + e.GroupConclusion?.ToStringGroupConclusion() ) );
            errorMessage.ShouldBe( "G1-|G2-1 Fatal error, 2 Errors|G3-1 Error|An error...-" );
            monitor.Error( "E3" );
            monitor.Fatal( "F2" );
            monitor.Warn( "W2" );
            c.Root.HasWarnOrError.ShouldBeTrue();
            c.Root.HasError.ShouldBeTrue();
            c.Root.FatalCount.ShouldBe( 2 );
            c.Root.ErrorCount.ShouldBe( 3 );
            c.Root.MaxLogLevel.ShouldBe( LogLevel.Fatal );
        }
        String.Join( ">", p.LastErrorPath.Select( e => e.Text + '-' + e.GroupConclusion?.ToStringGroupConclusion() ) ).ShouldBe( "G1-2 Fatal errors, 3 Errors, 1 Warning>F2-" );
        String.Join( ">", p.LastWarnOrErrorPath.Select( e => e.Text + '-' + e.GroupConclusion?.ToStringGroupConclusion() ) ).ShouldBe( "G1-2 Fatal errors, 3 Errors, 1 Warning>W2-" );
    }

    [Test]
    public void ActivityMonitorSimpleCollector_is_a_Client_that_filters_and_stores_its_Capacity_count_of_last_log_entries()
    {
        IActivityMonitor d = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var c = new ActivityMonitorSimpleCollector();
        d.Output.RegisterClient( c );
        d.Warn( "1" );
        d.Error( "2" );
        d.Fatal( "3" );
        d.Trace( "4" );
        d.Info( "5" );
        d.Warn( "6" );
        String.Join( ",", c.Entries.Select( e => e.Text ) ).ShouldBe( "2,3" );

        c.MinimalFilter = LogLevelFilter.Fatal;
        String.Join( ",", c.Entries.Select( e => e.Text ) ).ShouldBe( "3" );

        c.MinimalFilter = LogLevelFilter.Warn;
        using( d.OpenWarn( "A1" ) )
        {
            d.Error( "A2" );
            using( d.OpenFatal( "A3" ) )
            {
                d.Trace( "A4" );
                d.Info( "A5" );
            }
        }
        d.Warn( "A6" );
        String.Join( ",", c.Entries.Select( e => e.Text ) ).ShouldBe( "3,A1,A2,A3,A6" );

        c.MinimalFilter = LogLevelFilter.Fatal;
        String.Join( ",", c.Entries.Select( e => e.Text ) ).ShouldBe( "3,A3" );

        c.MinimalFilter = LogLevelFilter.Debug;
        d.MinimalFilter = LogFilter.Debug;
        using( d.OpenDebug( "d1" ) )
        {
            d.Debug( "d2" );
            using( d.OpenFatal( "f1" ) )
            {
                d.Debug( "d3" );
                d.Info( "i1" );
            }
        }
        String.Join( ",", c.Entries.Select( e => e.Text ) ).ShouldBe( "3,A3,d1,d2,f1,d3,i1" );
    }

    [Test]
    public void ActivityMonitorTextWriterClient_has_its_own_LogFilter()
    {
        StringBuilder sb = new StringBuilder();

        IActivityMonitor d = new ActivityMonitor();
        d.MinimalFilter = LogFilter.Trace;

        var c = new ActivityMonitorTextWriterClient( s => sb.Append( s ), new LogClamper( LogFilter.Release, true ) );
        d.Output.RegisterClient( c );

        d.Trace( "NO SHOW" );
        d.Trace( "NO SHOW" );
        using( d.OpenTrace( "NO SHOW" ) )
        {
            d.Info( "NO SHOW" );
            d.Info( "NO SHOW" );
        }

        d.Error( "Error line at root" );
        using( d.OpenInfo( "NO SHOW" ) )
        {
            d.Warn( "NO SHOW" );
            d.Error( "Send error line inside group" );
            using( d.OpenError( "Open error group" ) )
            {
                d.Error( "Send error line inside sub group" );
            }
        }

        sb.ToString().ShouldNotContain( "NO SHOW" );
        sb.ToString().ShouldContain( "Error line at root" );
        sb.ToString().ShouldContain( "Send error line inside group" );
        sb.ToString().ShouldContain( "Open error group" );
        sb.ToString().ShouldContain( "Send error line inside sub group" );
    }

    [Test]
    public void OnError_fires_synchronously()
    {
        var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        bool hasError = false;
        using( m.OnError( () => hasError = true ) )
        using( m.OpenInfo( "Handling StObj objects." ) )
        {
            m.Fatal( "Oops!" );
            hasError.ShouldBeTrue();
            hasError = false;
            m.OpenFatal( "Oops! (Group)" ).Dispose();
            hasError.ShouldBeTrue();
            hasError = false;
        }
        hasError = false;
        m.Fatal( "Oops!" );
        hasError.ShouldBeFalse();

        bool hasFatal = false;
        using( m.OnError( () => hasFatal = true, () => hasError = true ) )
        {
            m.Fatal( "Big Oops!" );
            hasFatal.ShouldBeTrue();
            hasError.ShouldBeFalse();
            m.Error( "Oops!" );
            hasFatal.ShouldBeTrue();
            hasError.ShouldBeTrue();
            hasFatal = hasError = false;
            m.OpenError( "Oops! (Group)" ).Dispose();
            hasFatal.ShouldBeFalse();
            hasError.ShouldBeTrue();
            m.OpenFatal( "Oops! (Group)" ).Dispose();
            hasFatal.ShouldBeTrue(); hasError.ShouldBeTrue();
            hasFatal = hasError = false;
        }
        m.Fatal( "Oops!" );
        hasFatal.ShouldBeFalse();
        hasError.ShouldBeFalse();
    }

    [Test]
    public void OnError_Tracker_Enabled_and_ToggleEnabled()
    {
        var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        bool hasError = false;
        var tracker = m.OnError( () => hasError = true );
        hasError.ShouldBeFalse();
        tracker.Enabled.ShouldBeTrue();
        using( tracker.ToggleEnable() )
        {
            tracker.Enabled.ShouldBeFalse();
            m.Error( "e" );
            hasError.ShouldBeFalse();
        }
        tracker.Enabled.ShouldBeTrue();
        m.Error( "e" );
        hasError.ShouldBeTrue();
    }

    [Test]
    public void OnError_TrackerMessage_Enabled_and_ToggleEnabled()
    {
        var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        string? e = null;

        var tracker = m.OnError( error => e = error, disabled: true );
        tracker.Enabled.ShouldBeFalse();
        m.Error( "e" );
        e.ShouldBeNull();

        using( tracker.ToggleEnable() )
        {
            tracker.Enabled.ShouldBeTrue();
            m.Error( "e" );
            e.ShouldBe( "e" );
        }
        tracker.Enabled.ShouldBeFalse();
        e = null;
        m.Error( "e" );
        e.ShouldBeNull();
    }

    [Test]
    public void setting_the_MininimalFilter_of_a_bound_Client_is_thread_safe()
    {
        ActivityMonitor m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var tester = m.Output.RegisterClient( new ActivityMonitorClientTester() );

        m.ActualFilter.ShouldBe( LogFilter.Undefined );
        tester.AsyncSetMinimalFilterAndWait( LogFilter.Monitor );
        m.ActualFilter.ShouldBe( LogFilter.Monitor );
    }

    [Test]
    public void BoundClient_IsDead_flag_is_handled()
    {
        ActivityMonitor m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var tester = m.Output.RegisterClient( new ActivityMonitorClientTester() );

        m.Error( "Hello World!" );
        tester.ReceivedTexts.ShouldNotBeNull().ShouldContain( s => s.Contains( "Hello World!" ) );
        // This is totally artificial since IsDead is normally hidden 
        // by IActivityMonitorBoundClients.
        tester.IsDead = true;
        tester.MinimalFilter = LogFilter.Debug;
        // This triggers the processing of the clients (since the level is lowered).
        m.Error( "NEVER RECEIVED" );

        m.Output.Clients.ShouldBeEmpty();
        tester.ReceivedTexts.ShouldNotContain( s => s.Contains( "NEVER RECEIVED" ) );

    }

    [Test]
    public void BoundClient_IsDead_flag_must_use_SignalChange_to_signal_its_change()
    {
        ActivityMonitor m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var tester = m.Output.RegisterClient( new ActivityMonitorClientTester() );

        m.Error( "Hello World!" );
        tester.ReceivedTexts.ShouldNotBeNull().ShouldContain( s => s.Contains( "Hello World!" ) );
        tester.AsyncDieAndWait( 20 );
        m.Error( "NEVER RECEIVED" );

        m.Output.Clients.ShouldBeEmpty();
        tester.ReceivedTexts.ShouldNotContain( s => s.Contains( "NEVER RECEIVED" ) );

    }

    class CheckAlwaysFilteredClient : ActivityMonitorClient
    {
        protected override void OnOpenGroup( IActivityLogGroup group )
        {
            (group.Data.Level & LogLevel.IsFiltered).ShouldNotBe( LogLevel.None );
        }

        protected override void OnUnfilteredLog( ref ActivityMonitorLogData data )
        {
            data.IsFilteredLog.ShouldBeTrue();
        }
    }


    [Test]
    public void in_a_OpenError_or_OpenFatal_group_level_is_automatically_sets_to_Debug()
    {
        ActivityMonitor m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var tester = m.Output.RegisterClient( new ActivityMonitorClientTester() );
        m.MinimalFilter = LogFilter.Release;
        m.Debug( "Here I am in NOT in Debug." );
        using( m.OpenError( "An error." ) )
        {
            m.Debug( "Here I am in Debug." );
        }
        tester.ReceivedTexts.ShouldNotBeNull()
            .ShouldMatch( e => e.Any( t => t.Contains( "Here I am in Debug." ) ) )
            .ShouldMatch( e => !e.All( t => t.Contains( "Here I am NOT in Debug." ) ) );
    }

    [Test]
    [SetCulture( "en-US" )]
    public void an_empty_exception_message_is_handled_with_no_log_string()
    {
        var ex = new Exception( null );
        ex.Message.ShouldBe( "Exception of type 'System.Exception' was thrown." );

        ActivityMonitor m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var tester = m.Output.RegisterClient( new ActivityMonitorClientTester() );
        m.Fatal( new Exception( "" ) );
        tester.ReceivedTexts.ShouldNotBeNull()
            .ShouldMatch( e => e.Any( t => t.Contains( "[no-log]" ) ) );

    }

    [Test]
    public void rejected_groups_test()
    {
        ActivityMonitor m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        m.MinimalFilter = LogFilter.Minimal;
        using( m.CollectTexts( out var logs ) )
        {
            using( m.OpenTrace( "NOSHOW" ) )
            {
                using( m.OpenInfo( "G1" ) )
                {
                    using( m.OpenTrace( "NOSHOW" ) )
                    {
                        m.Warn( "Warn" );
                    }
                }
            }
            logs.Concatenate().ShouldBe( "G1, Warn" );
        }
    }


    class TestClient : IActivityMonitorBoundClient
    {
        public LogFilter MinimalFilter => LogFilter.Debug;

        public bool IsDead => false;

        public void OnAutoTagsChanged( CKTrait newTrait )
        {
        }

        public void OnGroupClosed( IActivityLogGroup group, IReadOnlyList<ActivityLogGroupConclusion> conclusions )
        {
            LastData = group.Data;
        }

        public void OnGroupClosing( IActivityLogGroup group, ref List<ActivityLogGroupConclusion>? conclusions )
        {
            LastData = group.Data;
        }

        public void OnOpenGroup( IActivityLogGroup group )
        {
            LastData = group.Data;
        }

        public void OnTopicChanged( string newTopic, string? fileName, int lineNumber )
        {
        }
        public ActivityMonitorLogData LastData { get; private set; }
        public void OnUnfilteredLog( ref ActivityMonitorLogData data )
        {
            LastData = data;
        }

        public void SetMonitor( IActivityMonitorImpl? source, bool forceBuggyRemove )
        {
        }
    }

    [Test]
    public void open_group_is_tagged()
    {
        ActivityMonitor m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        var tester = m.Output.RegisterClient( new TestClient() );
        using( m.OpenTrace( "Hello" ) )
        {
            tester.LastData.IsOpenGroup.ShouldBeTrue();
        }
    }

}
