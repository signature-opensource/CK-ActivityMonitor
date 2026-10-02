using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace CK.Core.Tests;

/// <summary>
/// Runs the solution folder search against real git repositories created with the git command line
/// in a temporary folder.
/// </summary>
[TestFixture]
public class LocalDevSolutionTests
{
    NormalizedPath _root;
    string _emptyGlobalConfig = null!;

    [OneTimeSetUp]
    public void CheckGit()
    {
        try
        {
            using var p = Process.Start( new ProcessStartInfo( "git", "--version" ) { RedirectStandardOutput = true, UseShellExecute = false } );
            p!.WaitForExit();
            if( p.ExitCode != 0 ) Assert.Ignore( "The git command line does not work." );
        }
        catch( Exception ex )
        {
            Assert.Ignore( $"The git command line is not available: {ex.Message}" );
        }
    }

    [SetUp]
    public void CreateRoot()
    {
        _root = Path.Combine( Path.GetTempPath(), "CK-LDS-" + Guid.NewGuid().ToString( "N" ).Substring( 0, 8 ) );
        Directory.CreateDirectory( _root );
        // The tests must not depend on the global git configuration of the machine.
        _emptyGlobalConfig = _root.AppendPart( "empty.gitconfig" );
        File.WriteAllText( _emptyGlobalConfig, "" );
    }

    [TearDown]
    public void DeleteRoot()
    {
        DeleteFolder( _root );
    }

    [Test]
    public void this_repository_has_local_projects()
    {
        LocalDevSolution.SolutionFolder.IsEmptyPath.ShouldBeFalse();
        LocalDevSolution.SolutionName.ShouldBe( "CK-ActivityMonitor" );
        LocalDevSolution.HasLocalProjects.ShouldBeTrue();
        File.Exists( LocalDevSolution.SolutionFolder.AppendPart( "CK-ActivityMonitor.slnx" ) ).ShouldBeTrue();
        // The id is null in a main checkout (a ".git" directory).
        bool isMainCheckout = Directory.Exists( LocalDevSolution.SolutionFolder.AppendPart( ".git" ) );
        (LocalDevSolution.WorktreeId == null).ShouldBe( isMainCheckout );
    }

    [Test]
    public void null_or_empty_start_path()
    {
        Should.Throw<ArgumentNullException>( () => LocalDevSolution.TryFindSolutionFolder( null!, out _, out _, out _ ) );
        LocalDevSolution.TryFindSolutionFolder( "", out var folder, out var name, out var id ).ShouldBeFalse();
        folder.IsEmptyPath.ShouldBeTrue();
        name.ShouldBeNull();
        id.ShouldBeNull();
    }

    [Test]
    public void main_checkout()
    {
        var main = CreateRepository( "Main" );
        var deep = CreateFolder( main.Combine( "src/Proj/bin/Debug/net10.0" ) );

        Check( deep, main, "Main", null );
        // The start folder itself is a candidate, with or without a trailing separator.
        Check( main, main, "Main", null );
        Check( main.Path + Path.DirectorySeparatorChar, main, "Main", null );
    }

    [TestCase( false )]
    [TestCase( true )]
    public void worktree_outside_the_repository( bool relativePaths )
    {
        var main = CreateRepository( "Main" );
        var wt = _root.AppendPart( "Other" );
        if( relativePaths ) Git( main, "worktree", "add", "--detach", "--relative-paths", wt );
        else Git( main, "worktree", "add", "--detach", wt );
        File.ReadAllText( wt.AppendPart( ".git" ) ).ShouldStartWith( relativePaths ? "gitdir: ../" : "gitdir: " );

        Check( CreateFolder( wt.AppendPart( "src" ) ), wt, "Main", "Other" );
        Check( main, main, "Main", null );
    }

    [Test]
    public void worktree_nested_in_the_repository()
    {
        var main = CreateRepository( "Main" );
        var wt = main.Combine( ".claude/worktrees/nested" );
        Git( main, "worktree", "add", "--detach", wt );

        Check( CreateFolder( wt.AppendPart( "src" ) ), wt, "Main", "nested" );
        Check( CreateFolder( main.AppendPart( "src" ) ), main, "Main", null );
    }

    [Test]
    public void worktree_ids_are_unique_and_can_contain_any_character()
    {
        var main = CreateRepository( "Main" );
        var wt1 = _root.Combine( "a/feat-x" );
        var wt2 = _root.Combine( "b/feat-x" );
        var wt3 = _root.AppendPart( "Feat X é" );
        Git( main, "worktree", "add", "--detach", wt1 );
        Git( main, "worktree", "add", "--detach", wt2 );
        Git( main, "worktree", "add", "--detach", wt3 );

        var ids = new[] { wt1, wt2, wt3 }.Select( wt =>
        {
            LocalDevSolution.TryFindSolutionFolder( wt, out var folder, out var name, out var id ).ShouldBeTrue();
            folder.ShouldBe( wt );
            name.ShouldBe( "Main" );
            return id;
        } ).ToList();

        // Git 2.51.2 gives "feat-x", "feat-x1" and "Feat-X-é". Check only what consumers can rely on.
        ids[0].ShouldBe( "feat-x" );
        ids.Distinct().Count().ShouldBe( 3 );
        var gitIds = Directory.GetDirectories( main.Combine( ".git/worktrees" ) ).Select( Path.GetFileName );
        ids.ShouldBe( gitIds, ignoreOrder: true );
    }

    [Test]
    public void worktree_after_git_worktree_move()
    {
        var main = CreateRepository( "Main" );
        var wt = _root.AppendPart( "Other" );
        var moved = _root.AppendPart( "Moved" );
        Git( main, "worktree", "add", "--detach", wt );
        Git( main, "worktree", "move", wt, moved );

        // The id does not change when the worktree moves.
        Check( moved, moved, "Main", "Other" );
    }

    [Test]
    public void stale_worktree_after_the_main_checkout_moved_then_repaired()
    {
        var main = CreateRepository( "Main" );
        var wt = _root.AppendPart( "Other" );
        Git( main, "worktree", "add", "--detach", wt );
        var movedMain = _root.AppendPart( "MovedMain" );
        Directory.Move( main, movedMain );

        // The ".git" file of the worktree points to a missing git directory: it is ignored.
        bool found = LocalDevSolution.TryFindSolutionFolder( wt, out var folder, out _, out _ );
        if( found ) folder.ShouldNotBe( wt );

        Git( movedMain, "worktree", "repair", wt );
        Check( wt, wt, "MovedMain", "Other" );
    }

    [Test]
    public void git_file_with_bom_and_crlf()
    {
        var main = CreateRepository( "Main" );
        var wt = _root.AppendPart( "Other" );
        Git( main, "worktree", "add", "--detach", wt );
        var dotGit = wt.AppendPart( ".git" );
        var content = File.ReadAllText( dotGit ).TrimEnd();
        RewriteGitFile( dotGit, content + "\r\n", new UTF8Encoding( encoderShouldEmitUTF8Identifier: true ) );
        File.ReadAllBytes( dotGit )[0].ShouldBe( (byte)0xEF );

        Check( wt, wt, "Main", "Other" );
    }

    [Test]
    public void common_dir_name_in_upper_case()
    {
        if( !OperatingSystem.IsWindows() ) Assert.Ignore( "This test needs a case insensitive file system." );
        var main = CreateRepository( "Main" );
        var wt = _root.AppendPart( "Other" );
        Git( main, "worktree", "add", "--detach", wt );
        // The common dir is then "Main/.GIT": its name starts with '.'.
        RewriteGitFile( wt.AppendPart( ".git" ), $"gitdir: {main.Combine( ".GIT/worktrees/Other" )}\n", Encoding.UTF8 );

        Check( wt, wt, "Main", "Other" );
    }

    [Test]
    public void bare_layout()
    {
        var main = CreateRepository( "Main" );
        var layout = CreateFolder( _root.AppendPart( "Layout" ) );
        Git( _root, "clone", "--bare", main, layout.AppendPart( ".bare" ) );
        File.WriteAllText( layout.AppendPart( ".git" ), "gitdir: ./.bare\n" );
        var wt = layout.AppendPart( "wt1" );
        Git( layout, "worktree", "add", "--detach", wt );

        Check( CreateFolder( wt.AppendPart( "src" ) ), wt, "Layout", "wt1" );
        // The ".git" file of the layout folder points to the bare repository: this is not a worktree.
        Check( layout, layout, "Layout", null );
    }

    [Test]
    public void bare_repository_with_a_worktree()
    {
        var main = CreateRepository( "Main" );
        var bare = _root.AppendPart( "Product.git" );
        Git( _root, "clone", "--bare", main, bare );
        var wt = _root.AppendPart( "wt" );
        Git( bare, "worktree", "add", "--detach", wt );

        Check( wt, wt, "Product", "wt" );
    }

    [TestCase( "Sub", "Sub" )]
    [TestCase( "subname", "libs/sub" )]
    public void submodule_is_its_own_solution( string name, string path )
    {
        var main = CreateRepositoryWithSubmodule( name, path );
        var sub = main.Combine( path );

        Check( CreateFolder( sub.AppendPart( "src" ) ), sub, sub.LastPart, null );
        Check( CreateFolder( main.AppendPart( "src" ) ), main, "Main", null );
    }

    [TestCase( "Sub", "Sub" )]
    [TestCase( "subname", "libs/sub" )]
    public void worktree_of_a_submodule_is_named_by_the_submodule_folder( string name, string path )
    {
        var main = CreateRepositoryWithSubmodule( name, path );
        var sub = main.Combine( path );
        var wt = _root.AppendPart( "SubWt" );
        Git( sub, "worktree", "add", "--detach", wt );

        // The name comes from "core.worktree" in the config of "Main/.git/modules/<name>".
        Check( wt, wt, sub.LastPart, "SubWt" );
    }

    [Test]
    public void worktree_of_a_submodule_with_worktreeConfig()
    {
        var main = CreateRepositoryWithSubmodule( "subname", "libs/sub" );
        var sub = main.Combine( "libs/sub" );
        var wt = _root.AppendPart( "SubWt" );
        Git( sub, "worktree", "add", "--detach", wt );
        // With extensions.worktreeConfig, core.worktree must move to the "config.worktree" file.
        var commonDir = main.Combine( ".git/modules/subname" );
        Git( _root, "--git-dir", commonDir, "config", "extensions.worktreeConfig", "true" );
        Git( _root, "--git-dir", commonDir, "config", "--unset", "core.worktree" );
        Git( _root, "config", "--file", commonDir.AppendPart( "config.worktree" ), "core.worktree", "../../../libs/sub" );

        Check( wt, wt, "sub", "SubWt" );
    }

    [Test]
    public void separate_git_dir_clone()
    {
        var main = CreateRepository( "Main" );
        var clone = _root.AppendPart( "Clone" );
        Git( _root, "clone", "--separate-git-dir", _root.AppendPart( "separate-dir" ), main, clone );
        Check( clone, clone, "Clone", null );

        // Known limit: git stores no link from the git directory to the "Clone" folder.
        // The name is the worktree folder name.
        var wt = _root.AppendPart( "CloneWt" );
        Git( clone, "worktree", "add", "--detach", wt );
        Check( wt, wt, "CloneWt", "CloneWt" );
    }

    [TestCase( "" )]
    [TestCase( "hello" )]
    [TestCase( "gitdir:" )]
    [TestCase( "gitdir: does/not/exist" )]
    [TestCase( "gitdir: ../NotAGitDir" )]
    [TestCase( "GITDIR: ../.git" )]
    [TestCase( "\0\u0001binaryÿ" )]
    public void stray_or_malformed_git_file_is_ignored( string content )
    {
        var main = CreateRepository( "Main" );
        CreateFolder( main.AppendPart( "NotAGitDir" ) );
        var src = CreateFolder( main.AppendPart( "src" ) );
        File.WriteAllText( src.AppendPart( ".git" ), content );

        Check( CreateFolder( src.AppendPart( "Proj" ) ), main, "Main", null );
        Check( src, main, "Main", null );
    }

    [Test]
    public void git_file_that_targets_a_working_folder_is_ignored()
    {
        // The ".git" file targets the working folder of the main checkout, not its git directory.
        var main = CreateRepository( "Main" );
        var src = CreateFolder( main.AppendPart( "src" ) );
        File.WriteAllText( src.AppendPart( ".git" ), $"gitdir: {main}" );

        Check( src, main, "Main", null );
    }

    [Test]
    public void solution_file_is_found_in_a_worktree_with_another_folder_name()
    {
        var main = CreateFolder( _root.AppendPart( "Main" ) );
        Git( main, "init", "--quiet" );
        CreateFolder( main.AppendPart( "P" ) );
        File.WriteAllText( main.Combine( "P/P.csproj" ), "<Project Sdk=\"Microsoft.NET.Sdk\" />" );
        File.WriteAllText( main.AppendPart( "Main.slnx" ), "<Solution>\n  <Project Path=\"P/P.csproj\" />\n</Solution>\n" );
        Git( main, "add", "." );
        Git( main, "commit", "--quiet", "-m", "Initial." );
        var wt = _root.AppendPart( "Other" );
        Git( main, "worktree", "add", "--detach", wt );

        LocalDevSolution.TryFindSolutionFolder( wt, out var folder, out var name, out _ ).ShouldBeTrue();
        folder.ShouldBe( wt );
        name.ShouldBe( "Main" );
        var projects = ReadLocalProjects( folder, name );
        projects.ShouldNotBeNull();
        projects.Count.ShouldBe( 1 );
        projects.ShouldContain( FileUtil.NormalizePathSeparator( "P/P.csproj", ensureTrailingBackslash: false ) );
        // The worktree folder name does not name a solution file.
        ReadLocalProjects( folder, folder.LastPart ).ShouldBeNull();
    }

    static HashSet<string>? ReadLocalProjects( NormalizedPath solutionFolder, string solutionName )
    {
        return (HashSet<string>?)typeof( LocalDevSolution ).GetMethod( "ReadLocalProjects", BindingFlags.NonPublic | BindingFlags.Static )!
                                                           .Invoke( null, [solutionFolder, solutionName] );
    }

    static void Check( string start, NormalizedPath expectedFolder, string expectedName, string? expectedWorktreeId )
    {
        LocalDevSolution.TryFindSolutionFolder( start, out var folder, out var name, out var worktreeId ).ShouldBeTrue();
        folder.ShouldBe( expectedFolder );
        name.ShouldBe( expectedName );
        worktreeId.ShouldBe( expectedWorktreeId );
    }

    NormalizedPath CreateRepository( string name )
    {
        var repo = CreateFolder( _root.AppendPart( name ) );
        Git( repo, "init", "--quiet" );
        File.WriteAllText( repo.AppendPart( "README.md" ), name );
        Git( repo, "add", "." );
        Git( repo, "commit", "--quiet", "-m", "Initial." );
        return repo;
    }

    NormalizedPath CreateRepositoryWithSubmodule( string name, string path )
    {
        var subSource = CreateRepository( "SubSource" );
        var main = CreateRepository( "Main" );
        Git( main, "submodule", "add", "--quiet", "--name", name, subSource, path );
        Git( main, "commit", "--quiet", "-m", "Submodule." );
        File.Exists( main.Combine( path ).AppendPart( ".git" ) ).ShouldBeTrue();
        return main;
    }

    // Git for Windows hides the ".git" files: File.WriteAllText fails on a hidden file.
    static void RewriteGitFile( string path, string content, Encoding encoding )
    {
        File.SetAttributes( path, FileAttributes.Normal );
        File.WriteAllText( path, content, encoding );
    }

    static NormalizedPath CreateFolder( NormalizedPath path )
    {
        Directory.CreateDirectory( path );
        return path;
    }

    string Git( NormalizedPath workingFolder, params string[] args )
    {
        var info = new ProcessStartInfo( "git" )
        {
            WorkingDirectory = workingFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach( var c in new[] { "user.name=Test", "user.email=test@example.com", "init.defaultBranch=main",
                                  "protocol.file.allow=always", "core.autocrlf=false", "commit.gpgsign=false" } )
        {
            info.ArgumentList.Add( "-c" );
            info.ArgumentList.Add( c );
        }
        foreach( var a in args ) info.ArgumentList.Add( a );
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = _emptyGlobalConfig;
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach( var v in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE" } ) info.Environment.Remove( v );
        using var p = Process.Start( info )!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if( p.ExitCode != 0 )
        {
            Assert.Fail( $"'git {string.Join( ' ', args )}' failed in '{workingFolder}' (exit code {p.ExitCode}): {error}" );
        }
        return output.Result;
    }

    static void DeleteFolder( string path )
    {
        for( int tryCount = 0; ; ++tryCount )
        {
            try
            {
                if( !Directory.Exists( path ) ) return;
                // Git creates read-only object files: Directory.Delete fails on them.
                foreach( var f in Directory.EnumerateFiles( path, "*", SearchOption.AllDirectories ) )
                {
                    File.SetAttributes( f, FileAttributes.Normal );
                }
                Directory.Delete( path, recursive: true );
                return;
            }
            catch( Exception ) when( tryCount < 10 )
            {
                System.Threading.Thread.Sleep( 100 );
            }
        }
    }
}
