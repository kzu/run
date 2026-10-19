// Cleans bin/obj recursively
#:property PackageId=cleanr
#:property PackageVersion=0.1.1
#:property ProjectProperty=PackageVersion
#:property Description=Recursively clean bin/obj and optionally node_modules, publish, artifacts, Cargo target, and NuGet caches.
#:property ToolPackageRuntimeIdentifiers=win-x64;linux-x64;osx-arm64;any

#:package Spectre.Console@0.51.*
#:property Nullable=enable
#:property ImplicitUsings=enable
#:package ConsoleAppFramework@5.*
#:package ThisAssembly.Project@2.*
#:package ThisAssembly.Git@2.*

using System.Diagnostics;
using ConsoleAppFramework;
using Spectre.Console;

ConsoleApp.Version = $"v{ThisAssembly.Project.PackageVersion} ({ThisAssembly.Git.Sha})";

ConsoleApp.Run(args, Clean);

/// <summary>Recursively cleans bin/obj and optionally node_modules, publish, artifacts, Cargo target, and NuGet caches.</summary>
/// <param name="dir">Optional directory to start the clean. Defaults to current directory.</param>
/// <param name="dryRun">List directories but don't actually delete them.</param>
/// <param name="node">Opt-in to clean node_modules directories.</param>
/// <param name="dotted">Opt-in to clean inside hidden directories.</param>
/// <param name="publish">Opt-in to clean publish directories.</param>
/// <param name="artifacts">Opt-in to clean artifacts directories.</param>
/// <param name="rust">Opt-in to clean Cargo target directories.</param>
/// <param name="nuget">Opt-in to clean NuGet http-cache and global-packages directories.</param>
static void Clean(
    bool dryRun,
    [Argument] string? dir = default,
    bool node = false,
    bool dotted = false,
    bool publish = false,
    bool artifacts = false,
    bool rust = false,
    bool nuget = false,
    CancellationToken cancellation = default)
{
    if (nuget && !cancellation.IsCancellationRequested)
    {
        foreach (string cache in ResolveNuGetCaches())
            TryDeleteDirectory(cache, dryRun);
    }

    DeleteDirectories(dir ?? Directory.GetCurrentDirectory(), dryRun, node, dotted, publish, artifacts, rust, cancellation);
}

static void DeleteDirectories(
    string dir,
    bool dryRun,
    bool cleanNode,
    bool cleanDotted,
    bool cleanPublish,
    bool cleanArtifacts,
    bool cleanRust,
    CancellationToken cancellation)
{
    if (cancellation.IsCancellationRequested)
        return;

    TryDeleteDirectory(Path.Combine(dir, "bin"), dryRun);
    TryDeleteDirectory(Path.Combine(dir, "obj"), dryRun);
    if (cleanNode)
        TryDeleteDirectory(Path.Combine(dir, "node_modules"), dryRun);
    if (cleanPublish)
        TryDeleteDirectory(Path.Combine(dir, "publish"), dryRun);
    if (cleanArtifacts)
        TryDeleteDirectory(Path.Combine(dir, "artifacts"), dryRun);
    if (cleanRust && IsCargoProject(dir))
        TryDeleteDirectory(Path.Combine(dir, "target"), dryRun);

    foreach (string subDir in Directory.GetDirectories(dir))
    {
        string name = Path.GetFileName(subDir);
        if (!cleanDotted && name.StartsWith('.'))
            continue;

        if (name == "node_modules")
            continue;

        if (cleanPublish && name == "publish")
            continue;

        if (cleanArtifacts && name == "artifacts")
            continue;

        if (cleanRust && name == "target" && IsCargoProject(dir))
            continue;

        if (cancellation.IsCancellationRequested)
            break;

        DeleteDirectories(subDir, dryRun, cleanNode, cleanDotted, cleanPublish, cleanArtifacts, cleanRust, cancellation);
    }
}

static bool IsCargoProject(string dir) => File.Exists(Path.Combine(dir, "Cargo.toml"));

static IEnumerable<string> ResolveNuGetCaches()
{
    foreach (string resource in new[] { "http-cache", "global-packages" })
    {
        if (TryResolveNuGetLocal(resource, out string? path, out string? error) && path is not null)
        {
            if (IsSafeCachePath(path))
                yield return path;
            continue;
        }

        AnsiConsole.MarkupLine($":cross_mark: nuget {Markup.Escape(resource)}: {Markup.Escape(error ?? "unknown error")}");
    }
}

static bool TryResolveNuGetLocal(string resource, out string? path, out string? error)
{
    string? fromEnv = NuGetEnvOverride(resource);
    if (!string.IsNullOrWhiteSpace(fromEnv))
    {
        path = fromEnv;
        error = null;
        return true;
    }

    path = null;
    error = null;
    try
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"nuget locals {resource} --list")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };

        if (!process.Start())
        {
            error = "failed to start dotnet";
            return false;
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string stdout = stdoutTask.Result;
        string stderr = stderrTask.Result;
        if (process.ExitCode == 0 && TryParseNuGetLocal(resource, stdout, out path))
            return true;

        string? fallback = NuGetEnvOverride(resource);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            path = fallback;
            return true;
        }

        error = string.IsNullOrWhiteSpace(stderr) ? $"dotnet nuget locals exited {process.ExitCode}" : stderr.Trim();
        return false;
    }
    catch (Exception ex)
    {
        string? fallback = NuGetEnvOverride(resource);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            path = fallback;
            return true;
        }

        error = ex.Message;
        return false;
    }
}

static bool TryParseNuGetLocal(string resource, string output, out string? path)
{
    path = null;
    string prefix = resource + ":";
    foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        string trimmed = line.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            continue;

        path = trimmed[prefix.Length..].Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Length > 0;
    }

    return false;
}

static string? NuGetEnvOverride(string resource) => resource switch
{
    "http-cache" => Environment.GetEnvironmentVariable("NUGET_HTTP_CACHE_PATH"),
    "global-packages" => Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
    _ => null,
};

static bool IsSafeCachePath(string path)
{
    string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string? root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return !string.IsNullOrEmpty(root) && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
}

static void TryDeleteDirectory(string dir, bool dryRun)
{
    if (!Directory.Exists(dir))
        return;

    try
    {
        if (!dryRun)
            Directory.Delete(dir, true);

        AnsiConsole.MarkupLine($":check_mark_button:{(dryRun ? ":ghost:" : "")} {Markup.Escape(DisplayPath(dir))}");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($":cross_mark: {Markup.Escape(DisplayPath(dir))}: {Markup.Escape(ex.Message)}");
    }
}

static string DisplayPath(string dir)
{
    string cwd = Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string full = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (full.Equals(cwd, StringComparison.OrdinalIgnoreCase))
        return ".";

    string prefix = cwd + Path.DirectorySeparatorChar;
    if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        return "." + full[cwd.Length..];

    return dir;
}
