#:package LibGit2Sharp@0.31.1
#:package Spectre.Console@0.*
#:property RestoreSources=https://api.nuget.org/v3/index.json;https://pkg.kzu.app/index.json
using System;
using LibGit2Sharp;
using Spectre.Console;

using var repo = new Repository(Environment.CurrentDirectory);
var signature = repo.Config.BuildSignature(DateTimeOffset.Now);

if (signature == null)
{
    AnsiConsole.MarkupLine("[red] Unexpected error: Git user.name and/or user.email are not configured.[/]");
    return 1;
}
else
{
    AnsiConsole.MarkupLine($"[lime]{signature}[/]");
    return 0;
}