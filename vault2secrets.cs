// Saves all secrets from an Azure KeyVault to dotnet user-secrets
#:package CliWrap@3.*
#:package Spectre.Console@0.51.*
#:package ConsoleAppFramework@5.*
#:package Microsoft.Extensions.Configuration.UserSecrets@9.*
#:property Nullable=enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CliWrap;
using CliWrap.Buffered;
using ConsoleAppFramework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Spectre.Console;

await ConsoleApp.RunAsync(args, SyncSecrets);

/// <summary>Syncronizes secrets from an Azure KeyVault to dotnet user-secrets</summary>
/// <param name="id">Secrets ID to sync to, if different than the current directory project's.</param>
/// <param name="vault">Azure KeyVault to sync from. Selects interactively if not specified.</param>
async Task SyncSecrets(string? id = default, string? vault = default)
{
    var command = await AnsiConsole.Status().StartAsync("Listing Azure KeyVaults...", 
        async _ => await Cli.Wrap("az")
            .WithArguments("keyvault list --query \"[].name\"")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync());

    if (!command.IsSuccess)
    {
        AnsiConsole.MarkupLine("[red]Error:[/] Could not list Azure KeyVaults. Please ensure you are logged in with 'az login'.");
        return;
    }

    var names = JsonSerializer.Deserialize<List<string>>(command.StandardOutput);
    if (names is null)
        return;

    names.Sort();

    if (string.IsNullOrWhiteSpace(vault))
    {
        vault = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select an Azure KeyVault to import as dotnet user-secrets:")
                .AddChoices(names));
    }

    command = await AnsiConsole.Status().StartAsync($"Listing secrets in KeyVault {vault}...", 
        async _ => await Cli.Wrap("az")
            .WithArguments($"keyvault secret list --vault-name {vault} --query \"[].name\"")
            .ExecuteBufferedAsync());

    var secrets = JsonSerializer.Deserialize<string[]>(command.StandardOutput);
    if (secrets is null)
        return;

    var table = new Table();
    table.AddColumn("Action");
    table.AddColumn("Secret");

    if (id == null)
    {
        var secretsCmd = await Cli.Wrap("dotnet")
            .WithArguments("msbuild -getproperty:UserSecretsId")
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();

        if (secretsCmd.IsSuccess)
            id = secretsCmd.StandardOutput.Trim();
        else
            id = AnsiConsole.Prompt(new TextPrompt<string>("Could not determine UserSecretsId from the current directory's project. Please provide it as a parameter:")
                .PromptStyle("red")
                .ValidationErrorMessage("Invalid input")
                .Validate(id => !string.IsNullOrWhiteSpace(id)));
    }

    var configuration = new ConfigurationBuilder().AddUserSecrets(id).Build();

    await AnsiConsole.Status().StartAsync($"Importing secrets from KeyVault [lime]{vault}[/]...",
        async ctx =>
        {
            foreach (var secret in secrets)
            {
                ctx.Status($"Reading secret [lime]{secret}[/]...");
                command = await Cli.Wrap("az")
                    .WithArguments($"keyvault secret show --vault-name {vault} --name {secret} --query value")
                    .ExecuteBufferedAsync();

                var name = secret.Replace("--", ":");
                var existing = configuration[name];
                var value = command.StandardOutput.Trim().Trim('"');
                if (existing == value)
                {
                    table.AddRow(":check_mark_button:", name);
                }
                else if (string.IsNullOrEmpty(existing))
                {
                    await Cli.Wrap("dotnet")
                        .WithArguments($"user-secrets set \"{name}\" \"{value}\" --id {id}")
                        .ExecuteBufferedAsync();

                    table.AddRow(":plus:", name);
                }
                else
                {
                    await Cli.Wrap("dotnet")
                        .WithArguments($"user-secrets set \"{name}\" \"{value}\" --id {id}")
                        .ExecuteBufferedAsync();
                    
                    table.AddRow(":pencil:", name);
                }
            }
        });

    AnsiConsole.Write(table);

    // Unflatten the resulting secrets.json for easier reading
    AnsiConsole.Status().Start("Formatting secrets.json...", _ =>
    {
        var path = PathHelper.GetSecretsPathFromSecretsId(id);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var unflat = UnflattenJson(json);
        
        File.WriteAllText(path, JsonSerializer.Serialize(SortProperties(unflat), new JsonSerializerOptions {  WriteIndented = true }));
    });
}

static JsonObject UnflattenJson(JsonObject flatJson, char separator = ':')
{
    var result = new JsonObject();
    var properties = flatJson.OrderByDescending(p => p.Key.Split(separator).Length);
    foreach (var property in properties)
    {
        var keys = property.Key.Split(separator);
        var current = (JsonNode)result;
        bool conflict = false;
        for (int i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            if (i == keys.Length - 1)
            {
                var obj = current.AsObject();
                if (obj.ContainsKey(key))
                {
                    result.TryAdd(property.Key, property.Value?.DeepClone());
                }
                else
                {
                    obj.Add(key, property.Value?.DeepClone());
                }
            }
            else
            {
                if (!current.AsObject().TryGetPropertyValue(key, out var next) || next == null)
                {
                    next = new JsonObject();
                    current.AsObject().Add(key, next);
                }
                else if (next is not JsonObject)
                {
                    result.TryAdd(property.Key, property.Value?.DeepClone());
                    conflict = true;
                    break;
                }
                current = next;
            }
        }
        if (conflict) continue;
    }
    return result;
}

static JsonNode? SortProperties(JsonNode? node)
{
    if (node is JsonObject obj)
    {
        var sorted = new JsonObject();
        var properties = obj.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList(); 
        foreach (var property in properties)
        {
            sorted.Add(property.Key, SortProperties(property.Value)?.DeepClone());
        }
        return sorted;
    }
    else if (node is JsonArray arr)
    {
        var sortedArr = new JsonArray();
        foreach (var item in arr)
        {
            sortedArr.Add(SortProperties(item)?.DeepClone());
        }
        return sortedArr;
    }
    else
    {
        return node?.DeepClone();
    }
}