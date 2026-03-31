using System;
using System.IO;
using System.Linq;
using System.Reflection;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

namespace CK.Core.Tests;

static class TestHelper
{
    static string _testFolder;
    static string _solutionFolder;

    static IActivityMonitor _monitor;
    static ActivityMonitorConsoleClient _console;

    static TestHelper()
    {
        _monitor = new ActivityMonitor();
        // Do not pollute the console by default... LogsToConsole does the job.
        _console = new ActivityMonitorConsoleClient();
    }

    public static CKTrait Tag1 = ActivityMonitor.Tags.Register( "Tag1" );
    public static CKTrait Tag2 = ActivityMonitor.Tags.Register( "Tag2" );
    public static CKTrait Tag3 = ActivityMonitor.Tags.Register( "Tag3" );
    public static CKTrait Tag4 = ActivityMonitor.Tags.Register( "Tag4" );
    public static CKTrait Tag5 = ActivityMonitor.Tags.Register( "Tag5" );
    public static CKTrait Tag6 = ActivityMonitor.Tags.Register( "Tag6" );


    public static IActivityMonitor Monitor => _monitor;

    public static bool LogsToConsole
    {
        get { return _monitor.Output.Clients.Contains( _console ); }
        set
        {
            if( value != LogsToConsole )
            {
                if( value ) _monitor.Output.RegisterClient( _console );
                else _monitor.Output.UnregisterClient( _console );
            }
        }
    }

    public static string TestFolder
    {
        get
        {
            if( _testFolder == null ) InitializePaths();
            return _testFolder!;
        }
    }

    public static string SolutionFolder
    {
        get
        {
            if( _solutionFolder == null ) InitializePaths();
            return _solutionFolder!;
        }
    }

    public static void CleanupTestFolder()
    {
        CleanupFolder( TestFolder );
    }

    public static void CleanupFolder( string folder )
    {
        int tryCount = 0;
        for(; ; )
        {
            try
            {
                if( Directory.Exists( folder ) ) Directory.Delete( folder, true );
                Directory.CreateDirectory( folder );
                File.WriteAllText( Path.Combine( folder, "TestWrite.txt" ), "Test write works." );
                File.Delete( Path.Combine( folder, "TestWrite.txt" ) );
                return;
            }
            catch( Exception ex )
            {
                if( ++tryCount == 20 ) throw;
                Monitor.Info( "While cleaning up test directory. Retrying.", ex );
                System.Threading.Thread.Sleep( 100 );
            }
        }
    }

    static public void InitializePaths()
    {
        if( _solutionFolder != null ) return;
        NormalizedPath path = AppContext.BaseDirectory;
        var s = path.PathsToFirstPart( null, ["CK-ActivityMonitor.slnx", "CK-ActivityMonitor.sln"] ).FirstOrDefault( p => File.Exists( p ) );
        if( s.IsEmptyPath ) throw new InvalidOperationException( $"Unable to find CK-ActivityMonitor.sln above '{AppContext.BaseDirectory}'." );
        _solutionFolder = s.RemoveLastPart();
        _testFolder = Path.Combine( _solutionFolder, "Tests", "CK.ActivityMonitor.Tests", "TestFolder" );
        CleanupTestFolder();
        LogFile.RootLogPath = Path.Combine( _testFolder, "Logs" );
        Console.WriteLine( $"SolutionFolder is: {_solutionFolder}." );
        Console.WriteLine( $"TestFolder is: {_testFolder}." );
        Console.WriteLine( $"Core path: {typeof( string ).GetTypeInfo().Assembly.Location}." );
    }

}
