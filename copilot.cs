#:package Spectre.Console@0.55.*
#:package Spectre.Console.Cli@0.55.*
#:package Devlooped.CredentialManager@2.7.*
#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("copilot");

    config.AddCommand<AddCommand>("add")
        .WithDescription("Add a new BYOK provider configuration");

    config.AddCommand<RunCommand>("run")
        .WithDescription("Run copilot with BYOK configuration (default)");
});

app.SetDefaultCommand<RunCommand>();

return await app.RunAsync(args);

// ============================================================================
// Data Models
// ============================================================================

class ByokConfig
{
    [JsonPropertyName("providers")]
    public List<ProviderConfig> Providers { get; set; } = new();
}

class ProviderConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("models")]
    public List<ModelInfo> Models { get; set; } = new();
}

class ModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("max_prompt_tokens")]
    public int? MaxPromptTokens { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    public override string ToString() => Id;
}

// ============================================================================
// Commands
// ============================================================================

class AddCommand : AsyncCommand
{
    internal Task<int> ExecuteAsync([NotNull] CommandContext context)
    {
        return ExecuteAsync(context, CancellationToken.None);
    }

    protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, CancellationToken cancellationToken = default(CancellationToken))
    {
        AnsiConsole.MarkupLine("[bold cyan]Add BYOK Provider Configuration[/]\n");

        var providerType = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select [green]provider type[/]:")
                .AddChoices(new[]
                {
                    "OpenAI",
                    "Azure",
                    "Anthropic",
                    "xAI"
                }));

        // Determine defaults and type mapping
        string providerTypeKey = providerType switch
        {
            "OpenAI" => "openai",
            "Azure" => "azure",
            "Anthropic" => "anthropic",
            "xAI" => "openai", // xAI uses OpenAI-compatible endpoint
            _ => "openai"
        };

        string defaultBaseUrl = providerType switch
        {
            "OpenAI" => "https://api.openai.com/v1",
            "xAI" => "https://api.x.ai/v1",
            "Anthropic" => "https://api.anthropic.com",
            _ => ""
        };

        // Prompt for base URL
        var baseUrl = AnsiConsole.Prompt(
            new TextPrompt<string>($"Enter [green]Base URL[/]:")
                .DefaultValue(defaultBaseUrl)
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = defaultBaseUrl;

        // Determine if we need a friendly name
        string providerName = providerType;
        bool needsFriendlyName = providerType switch
        {
            "OpenAI" when baseUrl != "https://api.openai.com/v1" => true,
            "Azure" => true,
            _ => false
        };

        if (needsFriendlyName)
        {
            providerName = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]friendly name[/] for this provider:")
                    .DefaultValue(providerType));
        }

        // Check for existing API key
        var store = GitCredentialManager.CredentialManager.Create("copilot-byok");
        var existingCredential = store.Get(baseUrl, providerName);
        string apiKey;

        if (existingCredential != null)
        {
            AnsiConsole.MarkupLine($"\n[green]✓[/] Found existing API key for [cyan]{providerName}[/]");
            apiKey = existingCredential.Password;
        }
        else
        {
            // Prompt for API key
            apiKey = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]API Key[/]:")
                    .Secret());
        }

        AnsiConsole.MarkupLine($"\n[green]✓[/] Provider: {providerName}");
        AnsiConsole.MarkupLine($"[green]✓[/] Type: {providerTypeKey}");
        AnsiConsole.MarkupLine($"[green]✓[/] Base URL: {baseUrl}");
        if (existingCredential != null)
            AnsiConsole.MarkupLine($"[green]✓[/] API Key: [dim](loaded from secure storage)[/]");
        else
            AnsiConsole.MarkupLine($"[green]✓[/] API Key: [dim]***[/]");

        // Load existing config to check for existing models
        var configPath = GetConfigPath();
        var config = LoadConfig(configPath);
        var existingProvider = config.Providers.FirstOrDefault(p => p.Name == providerName);

        // Fetch models if supported
        List<ModelInfo> selectedModels = new();
        bool canFetchModels = providerType is "OpenAI" or "xAI" or "Anthropic";

        if (canFetchModels)
        {
            AnsiConsole.MarkupLine("\n[yellow]Fetching available models...[/]");

            var models = await AnsiConsole.Status()
                .StartAsync("Querying models endpoint...", async ctx =>
                {
                    return await FetchModelsAsync(baseUrl, apiKey, providerTypeKey);
                });

            if (models.Count > 0)
            {
                var selectionPrompt = new MultiSelectionPrompt<ModelInfo>()
                        .Title("Select [green]models[/] to use:")
                        .PageSize(10)
                        .MoreChoicesText("[grey](Move up and down to reveal more models)[/]")
                        .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]");

                foreach (var m in models)
                {
                    var choice = selectionPrompt.AddChoice(m);
                    if (existingProvider != null && existingProvider.Models.Any(em => em.Id == m.Id))
                    {
                        choice.Select();
                    }
                }

                selectedModels = AnsiConsole.Prompt(selectionPrompt);

                AnsiConsole.MarkupLine($"[green]✓[/] Selected {selectedModels.Count} model(s)");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Could not fetch models from endpoint.[/]");

                var manualModel = AnsiConsole.Prompt(
                    new TextPrompt<string>("Enter [green]model name[/] manually:")
                        .AllowEmpty());

                if (!string.IsNullOrWhiteSpace(manualModel))
                    selectedModels.Add(new ModelInfo { Id = manualModel });
            }
        }
        else
        {
            // Manual model entry for Azure and custom endpoints
            var manualModel = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]model name[/]:")
                    .AllowEmpty());

            if (!string.IsNullOrWhiteSpace(manualModel))
                selectedModels.Add(new ModelInfo { Id = manualModel });
        }

        // Save configuration
        AnsiConsole.MarkupLine("\n[yellow]Saving configuration...[/]");

        // Remove existing provider with the same name
        config.Providers.RemoveAll(p => p.Name == providerName);

        // Add new provider
        config.Providers.Add(new ProviderConfig
        {
            Name = providerName,
            Type = providerTypeKey,
            BaseUrl = baseUrl,
            Models = selectedModels
        });

        // Save config file
        var configDir = Path.GetDirectoryName(configPath)!;
        Directory.CreateDirectory(configDir);

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(configPath, json);

        // Save API key to credential manager
        store.AddOrUpdate(baseUrl, providerName, apiKey);

        AnsiConsole.MarkupLine($"[green]✓[/] Configuration saved to {configPath}");
        AnsiConsole.MarkupLine($"[green]✓[/] API key saved securely");

        return 0;
    }

    private static string GetConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".copilot", "byok.json");
    }

    private static ByokConfig LoadConfig(string path)
    {
        if (!File.Exists(path))
            return new ByokConfig();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ByokConfig>(json) ?? new ByokConfig();
        }
        catch
        {
            return new ByokConfig();
        }
    }

    private static async Task<List<ModelInfo>> FetchModelsAsync(string baseUrl, string apiKey, string providerType)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            if (providerType == "anthropic")
            {
                client.DefaultRequestHeaders.Add("x-api-key", apiKey);
                client.DefaultRequestHeaders.Remove("Authorization");
                client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            }

            var modelsUrl = baseUrl.TrimEnd('/') + "/v1/models";
            var response = await client.GetStringAsync(modelsUrl);

            using var doc = JsonDocument.Parse(response);
            var models = new List<ModelInfo>();

            if (doc.RootElement.TryGetProperty("data", out var dataArray))
            {
                foreach (var item in dataArray.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    {
                        var id = idProp.GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(id)) continue;

                        var modelInfo = new ModelInfo { Id = id };

                        if (item.TryGetProperty("max_prompt_tokens", out var promptTokens) && promptTokens.ValueKind == JsonValueKind.Number)
                        {
                            modelInfo.MaxPromptTokens = promptTokens.GetInt32();
                        }
                        else if (item.TryGetProperty("max_input_tokens", out var inputTokens) && inputTokens.ValueKind == JsonValueKind.Number)
                        {
                            modelInfo.MaxPromptTokens = inputTokens.GetInt32();
                        }
                        if (item.TryGetProperty("max_output_tokens", out var outputTokens) && outputTokens.ValueKind == JsonValueKind.Number)
                        {
                            modelInfo.MaxOutputTokens = outputTokens.GetInt32();
                        }
                        else if (item.TryGetProperty("max_tokens", out var maxTokens) && maxTokens.ValueKind == JsonValueKind.Number)
                        {
                            modelInfo.MaxOutputTokens = maxTokens.GetInt32();
                        }

                        models.Add(modelInfo);
                    }
                }
            }

            return models;
        }
        catch
        {
            return new List<ModelInfo>();
        }
    }
}

class RunCommand : AsyncCommand<RunCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("-p|--provider")]
        [Description("Provider to use")]
        public string? Provider { get; set; }

        [CommandOption("-m|--model")]
        [Description("Model to use")]
        public string? Model { get; set; }

        [CommandArgument(0, "[args]")]
        public string[]? RemainingArgs { get; set; }
    }

    protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings, CancellationToken cancellationToken = default(CancellationToken))
    {
        var configPath = GetConfigPath();
        var config = LoadConfig(configPath);

        string? selectedProvider = settings.Provider;
        string? selectedModel = settings.Model;

        // If provider not specified, prompt
        if (selectedProvider == null)
        {
            while (true)
            {
                var providerChoices = new List<string> { "Copilot (Default)" };
                providerChoices.AddRange(config.Providers.Select(p => p.Name));
                providerChoices.Add("Add BYOK...");
                providerChoices.Add("Edit BYOK...");

                selectedProvider = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select [green]provider[/]:")
                        .AddChoices(providerChoices));

                if (selectedProvider == "Add BYOK...")
                {
                    var addCommand = new AddCommand();
                    await addCommand.ExecuteAsync(context);

                    // Reload config after Add
                    config = LoadConfig(configPath);
                    selectedProvider = null; // force prompt again loop
                }
                else if (selectedProvider == "Edit BYOK...")
                {
                    Process.Start(new ProcessStartInfo(configPath) { UseShellExecute = true });
                    return 0;
                }
                else
                {
                    break;
                }
            }
        }

        // Handle default Copilot
        if (selectedProvider == "Copilot (Default)")
        {
            AnsiConsole.MarkupLine("[cyan]Using GitHub Copilot (Default)[/]");
            return LaunchCopilot(null, null, null, null, settings.RemainingArgs);
        }

        // Find provider config
        var providerConfig = config.Providers.FirstOrDefault(p => p.Name == selectedProvider);
        if (providerConfig == null)
        {
            AnsiConsole.MarkupLine($"[red]Provider '{selectedProvider}' not found in configuration.[/]");
            return 1;
        }

        // If model not specified, prompt
        ModelInfo? selectedModelInfo = null;

        if (selectedModel != null)
        {
            selectedModelInfo = providerConfig.Models.FirstOrDefault(m => m.Id == selectedModel);
            if (selectedModelInfo == null)
                selectedModelInfo = new ModelInfo { Id = selectedModel };
        }
        else if (providerConfig.Models.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No models configured for provider '{selectedProvider}'.[/]");
            return 1;
        }
        else if (providerConfig.Models.Count == 1)
        {
            selectedModelInfo = providerConfig.Models[0];
            selectedModel = selectedModelInfo.Id;
        }
        else
        {
            selectedModelInfo = AnsiConsole.Prompt(
                new SelectionPrompt<ModelInfo>()
                    .Title($"Select [green]model[/] from {selectedProvider}:")
                    .AddChoices(providerConfig.Models));
            selectedModel = selectedModelInfo.Id;
        }

        AnsiConsole.MarkupLine($"[cyan]Using {selectedProvider} / {selectedModel}[/]");

        // Retrieve API key from credential manager
        var store = GitCredentialManager.CredentialManager.Create("copilot-byok");
        var credential = store.Get(providerConfig.BaseUrl, selectedProvider);
        if (credential == null)
        {
            AnsiConsole.MarkupLine($"[red]API key not found for provider '{selectedProvider}'. Run 'copilot add' to configure.[/]");
            return 1;
        }

        return LaunchCopilot(
            providerConfig.Type,
            providerConfig.BaseUrl,
            credential.Password,
            selectedModelInfo,
            settings.RemainingArgs);
    }

    private static string GetConfigPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".copilot", "byok.json");
    }

    private static ByokConfig LoadConfig(string path)
    {
        if (!File.Exists(path))
            return new ByokConfig();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ByokConfig>(json) ?? new ByokConfig();
        }
        catch
        {
            return new ByokConfig();
        }
    }

    private static int LaunchCopilot(
        string? providerType,
        string? baseUrl,
        string? apiKey,
        ModelInfo? modelInfo,
        string[]? remainingArgs)
    {
        // Build arguments for copilot command
        var args = new List<string>();

        if (remainingArgs != null && remainingArgs.Length > 0)
        {
            // Remove explicit "--" separator if present
            var filteredArgs = remainingArgs.Where(arg => arg != "--").ToList();
            args.AddRange(filteredArgs);
        }

        var argumentString = string.Join(" ", args);

        var startInfo = new ProcessStartInfo
        {
            FileName = "copilot",
            Arguments = argumentString,
            UseShellExecute = false
        };

        // Set or clear environment variables
        if (providerType == null)
        {
            // Using default Copilot - clear BYOK environment variables
            startInfo.Environment["COPILOT_PROVIDER_TYPE"] = "";
            startInfo.Environment["COPILOT_PROVIDER_BASE_URL"] = "";
            startInfo.Environment["COPILOT_PROVIDER_API_KEY"] = "";
            startInfo.Environment["COPILOT_MODEL"] = "";
            startInfo.Environment["COPILOT_PROVIDER_MAX_PROMPT_TOKENS"] = "";
            startInfo.Environment["COPILOT_PROVIDER_MAX_OUTPUT_TOKENS"] = "";

            AnsiConsole.MarkupLine("[dim]Cleared BYOK environment variables[/]");
        }
        else
        {
            // Set BYOK environment variables
            startInfo.Environment["COPILOT_PROVIDER_TYPE"] = providerType;
            startInfo.Environment["COPILOT_PROVIDER_BASE_URL"] = baseUrl ?? "";
            startInfo.Environment["COPILOT_PROVIDER_API_KEY"] = apiKey ?? "";
            startInfo.Environment["COPILOT_MODEL"] = modelInfo?.Id ?? "";

            if (modelInfo?.MaxPromptTokens != null)
                startInfo.Environment["COPILOT_PROVIDER_MAX_PROMPT_TOKENS"] = modelInfo.MaxPromptTokens.ToString();
            else
                startInfo.Environment["COPILOT_PROVIDER_MAX_PROMPT_TOKENS"] = "";

            if (modelInfo?.MaxOutputTokens != null)
                startInfo.Environment["COPILOT_PROVIDER_MAX_OUTPUT_TOKENS"] = modelInfo.MaxOutputTokens.ToString();
            else
                startInfo.Environment["COPILOT_PROVIDER_MAX_OUTPUT_TOKENS"] = "";

            AnsiConsole.MarkupLine($"[dim]Set COPILOT_PROVIDER_TYPE={providerType}[/]");
            AnsiConsole.MarkupLine($"[dim]Set COPILOT_PROVIDER_BASE_URL={baseUrl}[/]");
            AnsiConsole.MarkupLine($"[dim]Set COPILOT_MODEL={modelInfo?.Id}[/]");

            if (modelInfo?.MaxPromptTokens != null)
                AnsiConsole.MarkupLine($"[dim]Set COPILOT_PROVIDER_MAX_PROMPT_TOKENS={modelInfo.MaxPromptTokens}[/]");
            if (modelInfo?.MaxOutputTokens != null)
                AnsiConsole.MarkupLine($"[dim]Set COPILOT_PROVIDER_MAX_OUTPUT_TOKENS={modelInfo.MaxOutputTokens}[/]");
        }

        AnsiConsole.MarkupLine($"\n[cyan]Launching: copilot {argumentString}[/]\n");

        try
        {
            var process = Process.Start(startInfo);
            if (process == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to start copilot process.[/]");
                return 1;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error launching copilot: {ex.Message}[/]");
            return 1;
        }
    }
}