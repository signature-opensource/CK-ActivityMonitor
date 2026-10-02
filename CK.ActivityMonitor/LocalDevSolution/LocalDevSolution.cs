using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CK.Core;

/// <summary>
/// Detects a solution that is "under development" and locates its local projects.
/// <para>
/// The solution folder is a git working folder: a main checkout, a linked git worktree or a submodule.
/// See <see cref="TryFindSolutionFolder(string, out NormalizedPath, out string?, out string?)"/> for the search rules.
/// </para>
/// </summary>
public static partial class LocalDevSolution
{
    static readonly HashSet<string>? _paths;

    /// <summary>
    /// Gets the solution folder found from <see cref="AppContext.BaseDirectory"/>
    /// by <see cref="TryFindSolutionFolder(string, out NormalizedPath, out string?, out string?)"/>.
    /// This is the empty path when no solution folder is found.
    /// <para>
    /// In a linked git worktree, this is the worktree folder, not the folder of the main checkout.
    /// </para>
    /// </summary>
    public static readonly NormalizedPath SolutionFolder;

    /// <summary>
    /// Gets the solution name. This is null when <see cref="SolutionFolder"/> is the empty path.
    /// <para>
    /// This is usually the last part of <see cref="SolutionFolder"/>. In a linked git worktree, this is the name
    /// of the main repository: the worktree folder can have any name.
    /// </para>
    /// </summary>
    public static readonly string? SolutionName;

    /// <summary>
    /// Gets the identifier of the linked git worktree: the "&lt;id&gt;" of the "&lt;common git dir&gt;/worktrees/&lt;id&gt;" folder.
    /// This is null in a main checkout, in a submodule and when <see cref="SolutionFolder"/> is the empty path.
    /// <para>
    /// The identifier is unique in a repository, but it can contain any character that git accepts in a folder name
    /// (git replaces the spaces with '-'). A caller that uses it in a name must clean it.
    /// </para>
    /// </summary>
    public static readonly string? WorktreeId;

    /// <summary>
    /// Gets whether the <see cref="SolutionFolder"/> has been found and a "<see cref="SolutionName"/>.sln" or ".slnx" file
    /// exists in it and contains at least one C# project. For example "CK-EmbeddedResources/CK-EmbeddedResources.slnx".
    /// <para>
    /// When false, it is useless to call <see cref="FindLocalProjectPath(Assembly, out NormalizedPath)"/>.
    /// </para>
    /// </summary>
    public static bool HasLocalProjects => _paths != null;

    /// <summary>
    /// Tries to find the local full path project folder (the folder that contains the ".csproj" file
    /// that is the source code of the <paramref name="assembly"/>).
    /// <para>
    /// The assembly must have a "SolutionRelativeProjectPath" <see cref="AssemblyMetadataAttribute"/>: the path of its
    /// ".csproj" file, relative to the solution folder. The "HandleCKEmbeddedResource" target of the
    /// CK.EmbeddedResources.Abstractions package (file "MSBuild/CK.EmbeddedResources.Abstractions.targets") writes it
    /// from the MSBuild <c>$(SolutionDir)</c> property. This path must also be declared in the solution file.
    /// </para>
    /// <para>
    /// <see cref="HasLocalProjects"/> must be true.
    /// </para>
    /// </summary>
    /// <param name="assembly">The assembly that may be a locally defined one.</param>
    /// <param name="projectPath">Contains the local project folder on success.</param>
    /// <returns>Whether a local folder has been found for the assembly.</returns>
    public static bool FindLocalProjectPath( Assembly assembly, out NormalizedPath projectPath )
    {
        projectPath = default;
        if( _paths != null )
        {
            var path = (string?)assembly.CustomAttributes.FirstOrDefault( a => a.AttributeType == typeof( AssemblyMetadataAttribute )
                                                                               && (string?)a.ConstructorArguments[0].Value == "SolutionRelativeProjectPath" )?
                                                         .ConstructorArguments[1].Value;
            if( path != null )
            {
                path = FileUtil.NormalizePathSeparator( path, ensureTrailingBackslash: false );
                if( _paths.Contains( path ) )
                {
                    projectPath = SolutionFolder.Combine( Path.GetDirectoryName( path ) );
                    return true;
                }
            }
        }
        return false;
    }

    static LocalDevSolution()
    {
        if( TryFindSolutionFolder( AppContext.BaseDirectory, out var folder, out var name, out var worktreeId ) )
        {
            SolutionFolder = folder;
            SolutionName = name;
            WorktreeId = worktreeId;
            _paths = ReadLocalProjects( folder, name );
        }
    }

    /// <summary>
    /// Reads the "<paramref name="solutionName"/>.sln" or ".slnx" file in the <paramref name="solutionFolder"/>
    /// and returns the normalized relative paths of the C# projects that exist.
    /// </summary>
    /// <param name="solutionFolder">The solution folder.</param>
    /// <param name="solutionName">The solution name.</param>
    /// <returns>The project paths, or null if no solution file or no project is found.</returns>
    static HashSet<string>? ReadLocalProjects( NormalizedPath solutionFolder, string solutionName )
    {
        try
        {
            var slnText = ReadSlnFile( solutionFolder, solutionName );
            if( slnText == null ) return null;
            HashSet<string>? projectsPath = null;
            // One time regex. Don't cache.
            var projects = Regex.Matches( slnText, @"(?<="")[^""]*\.csproj(?="")" );
            foreach( Match project in projects )
            {
                var path = project.Value;
                var fullPath = Path.Combine( solutionFolder, project.Value );
                if( !File.Exists( fullPath ) )
                {
                    Warn( $"Project file '{fullPath}' declared in solution file not found. Ignoring project." );
                }
                else
                {
                    projectsPath ??= new HashSet<string>();
                    path = FileUtil.NormalizePathSeparator( path, ensureTrailingBackslash: false );
                    if( !projectsPath.Add( path ) )
                    {
                        Warn( $"Found duplicate project '{path}' in solution file. Ignoring project '{fullPath}'." );
                    }
                }
            }
            if( projectsPath == null )
            {
                Warn( $"No project found in solution file:{Environment.NewLine}{slnText}." );
            }
            return projectsPath;
        }
        catch( Exception ex )
        {
            // This runs in the type initializer: an exception here would make this type unusable.
            Warn( $"Unable to read the local projects of the solution '{solutionFolder}'. No local projects can be handled.", ex );
            return null;
        }

        static string? ReadSlnFile( NormalizedPath solutionFolder, string solutionName )
        {
            var slnPath = solutionFolder.AppendPart( solutionName + ".sln" );
            if( File.Exists( slnPath ) )
            {
                return File.ReadAllText( slnPath );
            }
            var slnxPath = slnPath.Path + 'x';
            if( File.Exists( slnxPath ) )
            {
                return File.ReadAllText( slnxPath );
            }
            Warn( $"Unable to find expected '{slnPath}' file. No local projects can be handled." );
            return null;
        }
    }

    static void Warn( string text, Exception? ex = null )
    {
        var logger = ActivityMonitor.StaticLogger;
        if( logger.ShouldLogLine( LogLevel.Warn, null, out var finalTags ) )
        {
            var d = logger.CreateActivityMonitorLogData( LogLevel.Warn | LogLevel.IsFiltered, finalTags, text, ex, null, 0, false );
            logger.UnfilteredLog( ref d );
        }
    }
}
