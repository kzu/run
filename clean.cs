// Cleans bin/obj recursively
#:package Spectre.Console@0.51.*
#:property Nullable=enable
#:property ImplicitUsings=enable
#:package ConsoleAppFramework@5.*

using Spectre.Console;
using ConsoleAppFramework;

ConsoleApp.Run(args, () => DeleteDirectories(Directory.GetCurrentDirectory()));

static void DeleteDirectories(string dir)
{
    // Delete bin and obj in current directory
    TryDeleteDirectory(Path.Combine(dir, "bin"));
    TryDeleteDirectory(Path.Combine(dir, "obj"));

    // Recurse into subdirectories
    foreach (string subDir in Directory.GetDirectories(dir))
    {
        DeleteDirectories(subDir);
    }
}

static void TryDeleteDirectory(string dir)
{
    if (Directory.Exists(dir))
    {
        try
        {
            Directory.Delete(dir, true);
            AnsiConsole.MarkupLine($":check_mark_button: .{dir.Substring(Directory.GetCurrentDirectory().Length)}");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($":cross_mark: .{dir.Substring(Directory.GetCurrentDirectory().Length)}: {ex.Message}");
        }
    }
}