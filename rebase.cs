#:package LibGit2Sharp@0.*
#:package Spectre.Console@0.*
#nullable enable
using System;
using System.Linq;
using LibGit2Sharp;
using Spectre.Console;

try
{
    using var repo = new Repository(Environment.CurrentDirectory);
    var signature = repo.Config.BuildSignature(DateTimeOffset.Now);
    if (signature == null)
    {
        AnsiConsole.MarkupLine("[red]:cross_mark: Git user.name and/or user.email are not configured.[/]");
        return 1;
    }

    var status = repo.RetrieveStatus(new StatusOptions());
    var hasChanges = status.IsDirty;

    if (hasChanges)
    {
        AnsiConsole.Status()
            .Start("Stashing local changes...", ctx =>
            {
                repo.Stashes.Add(signature, "Temporary stash before fetch+rebase");
            });
    }

    var conflicts = false;
    var remoteName = "origin";
    string? remoteBranch = default;
    Branch? upstreamBranch = default;

    if (args.Length == 1)
    {
        var parts = args[0].Split('/');
        if (parts.Length == 2)
        {
            remoteName = parts[0];
            remoteBranch = parts[1];
        }
        else
        {
            remoteBranch = parts[0];
        }
    }

    try
    {
        var remote = repo.Network.Remotes[remoteName];
        if (remote == null)
        {
            AnsiConsole.MarkupLine($"[red]:cross_mark: No '{remoteName}' remote found.[/]");
            return 1;
        }

        var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);

        AnsiConsole.Status()
            .Start($"Fetching from {remote.Name}...", ctx =>
            {
                Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions(), null);
            });

        var currentBranch = repo.Head;
        if (remoteBranch == null)
        {
            upstreamBranch = currentBranch.TrackedBranch;
        }
        else
        {
            var branchRef = $"/{remoteName}/{remoteBranch}";
            var branches = repo.Branches.OfType<Branch>()
                .Where(x => x.CanonicalName.EndsWith(branchRef))
                .ToDictionary(x => x.CanonicalName);

            upstreamBranch = remote.FetchRefSpecs
                .Where(x => x.Destination.EndsWith($"/{remoteName}/*"))
                .Select(x => x.Destination.TrimEnd('*') + remoteBranch)
                .Where(x => branches.ContainsKey(x))
                .Select(x => branches[x])
                .FirstOrDefault();
        }

        if (upstreamBranch == null)
        {
            AnsiConsole.MarkupLine("[red]:cross_mark: No upstream tracking branch to rebase.[/]");
            return 1;
        }

        var identity = new Identity(signature.Name, signature.Email);

        conflicts = AnsiConsole.Status()
            .Start($"Rebasing onto {upstreamBranch.FriendlyName}...", ctx =>
            {
                var rebaseResult = repo.Rebase.Start(
                    branch: currentBranch,
                    upstream: upstreamBranch,
                    onto: null,
                    committer: identity,
                    options: new RebaseOptions());

                if (rebaseResult.Status != RebaseStatus.Complete)
                {
                    repo.Rebase.Abort();
                    return true;
                }

                return false;
            });
    }
    catch (Exception ex)
    {
        conflicts = true;
        AnsiConsole.MarkupLine($"[red]:cross_mark: Error during fetch/rebase: {ex.Message.EscapeMarkup()}[/]");
    }

    if (hasChanges)
    {
        AnsiConsole.Status()
            .Start("Applying stashed changes...", ctx =>
            {
                try
                {
                    repo.Stashes.Pop(0, new StashApplyOptions());
                }
                catch
                {
                    throw new Exception("Failed to apply stash — possible conflicts with restored changes");
                }
            });
    }

    if (conflicts)
    {
        AnsiConsole.MarkupLine("[bold red]:cross_mark: Operation aborted due to conflicts.[/]");
        return 1;
    }
    else
    {
        AnsiConsole.MarkupLine($"[bold green]:check_mark_button: {repo.Head} << {upstreamBranch}[/]");
        return 0;
    }
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]:cross_mark: Fatal error: {ex.Message.EscapeMarkup()}[/]");
    return 1;
}