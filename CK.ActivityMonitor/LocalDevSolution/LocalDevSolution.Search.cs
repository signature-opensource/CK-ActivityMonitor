using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;

namespace CK.Core;

public static partial class LocalDevSolution
{
    /// <summary>
    /// Finds the solution folder: the closest git working folder at or above <paramref name="startPath"/>.
    /// <list type="number">
    ///   <item>The search walks up from the start folder.</item>
    ///   <item>It stops at a folder that contains a ".git" directory (a main checkout).</item>
    ///   <item>
    ///   It stops at a folder that contains a ".git" file only if this file is "gitdir: &lt;path&gt;" and &lt;path&gt;
    ///   is a git directory: a linked worktree (the git directory contains a "commondir" file) or a submodule
    ///   (the git directory contains a "HEAD" file and no "commondir" file). Any other ".git" file
    ///   (stray file, bad content, missing target) is ignored and the search continues.
    ///   </item>
    /// </list>
    /// The solution name is the name of the solution folder, except in a linked worktree. The name of a linked
    /// worktree is the name of its main repository. It is found from the common git directory, in this order:
    /// <list type="number">
    ///   <item>
    ///   If the "config" file of the common git directory has a "core.worktree" value, the name of this folder.
    ///   This value is relative to the common git directory, or absolute. This handles a submodule whose name
    ///   differs from its path.
    ///   </item>
    ///   <item>If the common git directory name starts with '.' (".git", ".bare"), the name of its parent folder.</item>
    ///   <item>If the common git directory name ends with ".git" (a bare repository), this name without the suffix.</item>
    ///   <item>Else, the name of the solution folder.</item>
    /// </list>
    /// This method never throws (except when <paramref name="startPath"/> is null). If a step fails, the name
    /// of the solution folder is used.
    /// <para>
    /// Known limit: the main folder of a "git clone --separate-git-dir" clone cannot be found from one of its
    /// linked worktrees, because git stores no link from the git directory to this folder. The name of the
    /// worktree folder is used.
    /// </para>
    /// </summary>
    /// <param name="startPath">The start folder. A relative path is relative to the current directory.</param>
    /// <param name="solutionFolder">The solution folder on success, the empty path otherwise.</param>
    /// <param name="solutionName">The solution name on success, null otherwise.</param>
    /// <param name="worktreeId">
    /// The identifier of the linked worktree (the "&lt;id&gt;" of "&lt;common git dir&gt;/worktrees/&lt;id&gt;").
    /// Null in a main checkout, in a submodule, and when no solution folder is found.
    /// </param>
    /// <returns>True if a solution folder is found, false otherwise.</returns>
    public static bool TryFindSolutionFolder( string startPath,
                                              out NormalizedPath solutionFolder,
                                              [NotNullWhen( true )] out string? solutionName,
                                              out string? worktreeId )
    {
        Throw.CheckNotNullArgument( startPath );
        solutionFolder = default;
        solutionName = null;
        worktreeId = null;
        if( string.IsNullOrWhiteSpace( startPath ) ) return false;
        string? p;
        try
        {
            p = Path.TrimEndingDirectorySeparator( Path.GetFullPath( startPath ) );
        }
        catch( Exception ex )
        {
            Warn( $"Invalid start path '{startPath}' to find a solution folder.", ex );
            return false;
        }
        while( !string.IsNullOrEmpty( p ) )
        {
            var dotGit = Path.Combine( p, ".git" );
            if( Directory.Exists( dotGit ) )
            {
                solutionFolder = p;
                solutionName = GetFolderName( solutionFolder );
                return true;
            }
            if( File.Exists( dotGit ) && TryReadGitFile( dotGit, p, out var gitDir, out bool isWorktree ) )
            {
                solutionFolder = p;
                if( isWorktree )
                {
                    solutionName = GetWorktreeName( gitDir, solutionFolder );
                    worktreeId = Path.GetFileName( gitDir );
                }
                else
                {
                    solutionName = GetFolderName( solutionFolder );
                }
                return true;
            }
            p = Path.GetDirectoryName( p );
        }
        return false;
    }

    static string GetFolderName( NormalizedPath folder ) => folder.LastPart;

    /// <summary>
    /// Reads a ".git" file. Returns true if it points to a git directory of a linked worktree or of a submodule.
    /// </summary>
    static bool TryReadGitFile( string dotGitFile, string folder, [NotNullWhen( true )] out string? gitDir, out bool isWorktree )
    {
        gitDir = null;
        isWorktree = false;
        try
        {
            // A ".git" file is one short line. Do not read a large file.
            if( new FileInfo( dotGitFile ).Length > 64 * 1024 )
            {
                Warn( $"Ignoring '{dotGitFile}': this file is too large to be a git link." );
                return false;
            }
            // File.ReadAllText removes a byte order mark.
            var line = FirstLine( File.ReadAllText( dotGitFile ) );
            const string prefix = "gitdir:";
            if( !line.StartsWith( prefix, StringComparison.Ordinal ) )
            {
                Warn( $"Ignoring '{dotGitFile}': it does not start with '{prefix}'." );
                return false;
            }
            var path = line.Substring( prefix.Length ).Trim();
            if( path.Length == 0 )
            {
                Warn( $"Ignoring '{dotGitFile}': its '{prefix}' path is empty." );
                return false;
            }
            // A relative path is relative to the folder that contains the ".git" file.
            var candidate = Path.TrimEndingDirectorySeparator( Path.GetFullPath( path, folder ) );
            if( File.Exists( Path.Combine( candidate, "commondir" ) ) )
            {
                gitDir = candidate;
                isWorktree = true;
                return true;
            }
            if( File.Exists( Path.Combine( candidate, "HEAD" ) ) )
            {
                gitDir = candidate;
                return true;
            }
            Warn( $"Ignoring '{dotGitFile}': '{candidate}' is not a git directory." );
        }
        catch( Exception ex )
        {
            Warn( $"Ignoring '{dotGitFile}': unable to read it.", ex );
        }
        return false;
    }

    /// <summary>
    /// Gets the name of the main repository of a linked worktree. Never throws: on failure, this is the
    /// name of the worktree folder.
    /// </summary>
    static string GetWorktreeName( string gitDir, NormalizedPath worktreeFolder )
    {
        var fallback = GetFolderName( worktreeFolder );
        try
        {
            var commonDirFile = Path.Combine( gitDir, "commondir" );
            var commonDirPath = FirstLine( File.ReadAllText( commonDirFile ) ).Trim();
            if( commonDirPath.Length == 0 )
            {
                Warn( $"File '{commonDirFile}' is empty. Using the worktree folder name '{fallback}' as the solution name." );
                return fallback;
            }
            // A relative path is relative to the git directory of the worktree.
            var commonDir = Path.TrimEndingDirectorySeparator( Path.GetFullPath( commonDirPath, gitDir ) );
            // Rule 1: core.worktree is the main working folder. It is relative to the common git directory.
            var coreWorktree = ReadCoreWorktree( commonDir );
            if( coreWorktree != null )
            {
                var mainFolder = Path.TrimEndingDirectorySeparator( Path.GetFullPath( coreWorktree, commonDir ) );
                return NameOrFallback( Path.GetFileName( mainFolder ), fallback );
            }
            var commonDirName = Path.GetFileName( commonDir );
            // Rule 2: "<main>/.git" or "<main>/.bare".
            if( commonDirName.StartsWith( '.' ) )
            {
                return NameOrFallback( Path.GetFileName( Path.GetDirectoryName( commonDir ) ), fallback );
            }
            // Rule 3: a bare repository "<main>.git".
            if( commonDirName.EndsWith( ".git", StringComparison.OrdinalIgnoreCase ) )
            {
                return NameOrFallback( commonDirName.Substring( 0, commonDirName.Length - 4 ), fallback );
            }
        }
        catch( Exception ex )
        {
            Warn( $"Unable to find the main repository name of the git worktree '{worktreeFolder}'. Using the worktree folder name '{fallback}' as the solution name.", ex );
        }
        // Rule 4: the worktree folder name.
        return fallback;

        static string NameOrFallback( string? name, string fallback ) => string.IsNullOrWhiteSpace( name ) ? fallback : name;
    }

    static string FirstLine( string text )
    {
        int eol = text.AsSpan().IndexOfAny( '\r', '\n' );
        return eol < 0 ? text : text.Substring( 0, eol );
    }

    /// <summary>
    /// Reads the "core.worktree" value of a common git directory. When "extensions.worktreeConfig" is true,
    /// the value can be in the "config.worktree" file: this file wins.
    /// </summary>
    static string? ReadCoreWorktree( string commonDir )
    {
        string? coreWorktree = null;
        bool worktreeConfig = false;
        foreach( var (section, key, value) in ReadGitConfig( Path.Combine( commonDir, "config" ) ) )
        {
            if( section.Equals( "core", StringComparison.OrdinalIgnoreCase ) && key.Equals( "worktree", StringComparison.OrdinalIgnoreCase ) )
            {
                coreWorktree = value;
            }
            else if( section.Equals( "extensions", StringComparison.OrdinalIgnoreCase ) && key.Equals( "worktreeConfig", StringComparison.OrdinalIgnoreCase ) )
            {
                worktreeConfig = IsTrue( value );
            }
        }
        if( worktreeConfig )
        {
            foreach( var (section, key, value) in ReadGitConfig( Path.Combine( commonDir, "config.worktree" ) ) )
            {
                if( section.Equals( "core", StringComparison.OrdinalIgnoreCase ) && key.Equals( "worktree", StringComparison.OrdinalIgnoreCase ) )
                {
                    coreWorktree = value;
                }
            }
        }
        return string.IsNullOrWhiteSpace( coreWorktree ) ? null : coreWorktree;

        // A key without '=' is true. Git also accepts integers: they are not handled here.
        static bool IsTrue( string? value ) => value == null
                                               || value.Equals( "true", StringComparison.OrdinalIgnoreCase )
                                               || value.Equals( "yes", StringComparison.OrdinalIgnoreCase )
                                               || value.Equals( "on", StringComparison.OrdinalIgnoreCase )
                                               || value == "1";
    }

    /// <summary>
    /// Minimal git config file reader. It handles sections, comments, quotes and escape sequences.
    /// It does not handle line continuations and include directives. The section of "[remote "origin"]"
    /// is "remote "origin"": it never matches a simple section name.
    /// A missing file gives no entry. A key without '=' has a null value.
    /// </summary>
    static IEnumerable<(string Section, string Key, string? Value)> ReadGitConfig( string path )
    {
        if( !File.Exists( path ) ) yield break;
        string section = string.Empty;
        foreach( var rawLine in File.ReadLines( path ) )
        {
            var line = rawLine.Trim();
            if( line.Length > 0 && line[0] == '[' )
            {
                int close = line.IndexOf( ']' );
                if( close < 0 )
                {
                    section = string.Empty;
                    continue;
                }
                section = line.Substring( 1, close - 1 ).Trim();
                // A "[section] key = value" line is valid.
                line = line.Substring( close + 1 ).TrimStart();
            }
            if( line.Length == 0 || line[0] == '#' || line[0] == ';' ) continue;
            int eq = line.IndexOf( '=' );
            if( eq < 0 )
            {
                var name = StripComment( line ).Trim();
                if( name.Length > 0 ) yield return (section, name, null);
            }
            else
            {
                var name = line.Substring( 0, eq ).Trim();
                if( name.Length > 0 ) yield return (section, name, ParseValue( line.Substring( eq + 1 ) ));
            }
        }

        static string StripComment( string s )
        {
            int c = s.IndexOfAny( ['#', ';'] );
            return c < 0 ? s : s.Substring( 0, c );
        }

        static string ParseValue( string raw )
        {
            var b = new StringBuilder();
            bool inQuotes = false;
            // Trailing white spaces outside quotes are removed. This keeps the length without them.
            int trimmedLength = 0;
            for( int i = 0; i < raw.Length; ++i )
            {
                char c = raw[i];
                if( c == '"' )
                {
                    inQuotes = !inQuotes;
                    trimmedLength = b.Length;
                    continue;
                }
                if( !inQuotes && (c == '#' || c == ';') ) break;
                if( c == '\\' && i + 1 < raw.Length )
                {
                    char n = raw[++i];
                    b.Append( n switch { 'n' => '\n', 't' => '\t', 'b' => '\b', _ => n } );
                    trimmedLength = b.Length;
                    continue;
                }
                if( !inQuotes && char.IsWhiteSpace( c ) )
                {
                    // Leading white spaces are skipped.
                    if( b.Length > 0 ) b.Append( c );
                    continue;
                }
                b.Append( c );
                trimmedLength = b.Length;
            }
            b.Length = trimmedLength;
            return b.ToString();
        }
    }
}
