﻿// Cleans bin/obj recursively
#:package Spectre.Console@0.51.*
#:property Nullable=enable
#:property ImplicitUsings=enable
#:package ConsoleAppFramework@5.*

using Spectre.Console;
using ConsoleAppFramework;

ConsoleApp.Run(args, Clean);

/// <summary>Recursively cleans bin/obj</summary>
/// <param name="dir">Optional directory to start the clean. Defaults to current directory.</param>
/// <param name="dryRun">List directories but don't actually delete them.</param>
static void Clean(bool dryRun, [Argument] string? dir = default, CancellationToken cancellation = default)
    => DeleteDirectories(dir ?? Directory.GetCurrentDirectory(), dryRun, cancellation);

static void DeleteDirectories(string dir, bool dryRun, CancellationToken cancellation)
{
    if (cancellation.IsCancellationRequested)
        return;

    TryDeleteDirectory(Path.Combine(dir, "bin"), dryRun);
    TryDeleteDirectory(Path.Combine(dir, "obj"), dryRun);

    foreach (string subDir in Directory.GetDirectories(dir))
    {
        if (cancellation.IsCancellationRequested)
            break;
            
        DeleteDirectories(subDir, dryRun, cancellation);
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