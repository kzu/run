﻿// Cleans bin/obj recursively
#:package Spectre.Console@0.51.*
#:property Nullable=enable
#:property ImplicitUsings=enable
#:package ConsoleAppFramework@5.*

using Spectre.Console;
using ConsoleAppFramework;

ConsoleApp.Run(args, (bool dryRun) => DeleteDirectories(Directory.GetCurrentDirectory(), dryRun));

static void DeleteDirectories(string dir, bool dryRun)
{
    TryDeleteDirectory(Path.Combine(dir, "bin"), dryRun);
    TryDeleteDirectory(Path.Combine(dir, "obj"), dryRun);

    foreach (string subDir in Directory.GetDirectories(dir))
        DeleteDirectories(subDir, dryRun);
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