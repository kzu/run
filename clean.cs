// Cleans bin/obj recursively
#:package Spectre.Console@0.51.*
#:property Nullable=enable
#:property ImplicitUsings=enable
#:package ConsoleAppFramework@5.*

using ConsoleAppFramework;
using Spectre.Console;

ConsoleApp.Run(args, Clean);

/// <summary>Recursively cleans bin/obj and optionally node_modules directories.</summary>
/// <param name="dir">Optional directory to start the clean. Defaults to current directory.</param>
/// <param name="dryRun">List directories but don't actually delete them.</param>
/// <param name="node">Opt-in to clean node_modules directories.</param>
/// <param name="dotted">Opt-in to clean inside hidden directories.</param>
static void Clean(bool dryRun, [Argument] string? dir = default, bool node = false, bool dotted = false, CancellationToken cancellation = default)
    => DeleteDirectories(dir ?? Directory.GetCurrentDirectory(), dryRun, node, dotted, cancellation);

static void DeleteDirectories(string dir, bool dryRun, bool cleanNode, bool cleanDotted, CancellationToken cancellation)
{
    if (cancellation.IsCancellationRequested)
        return;

    TryDeleteDirectory(Path.Combine(dir, "bin"), dryRun);
    TryDeleteDirectory(Path.Combine(dir, "obj"), dryRun);
    if (cleanNode)
        TryDeleteDirectory(Path.Combine(dir, "node_modules"), dryRun);

    foreach (string subDir in Directory.GetDirectories(dir))
    {
        if (!cleanDotted && Path.GetFileName(subDir).StartsWith('.'))
            continue;

        if (Path.GetFileName(subDir) == "node_modules")
            continue;

        if (cancellation.IsCancellationRequested)
            break;

        DeleteDirectories(subDir, dryRun, cleanNode, cleanDotted, cancellation);
    }
}

static void TryDeleteDirectory(string dir, bool dryRun)
{
    if (!Directory.Exists(dir))
        return;

    try
    {
        if (!dryRun)
            Directory.Delete(dir, true);

        AnsiConsole.MarkupLine($":check_mark_button:{(dryRun ? ":ghost:" : "")} .{dir.Substring(Directory.GetCurrentDirectory().Length)}");
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($":cross_mark: .{dir.Substring(Directory.GetCurrentDirectory().Length)}: {ex.Message}");
    }
}