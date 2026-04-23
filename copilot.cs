// Copilot BYOK (Bring Your Own Key) CLI
//
// A CLI tool that lets you run GitHub Copilot with your own API keys for
// third-party LLM providers (OpenAI, Anthropic, xAI, OpenRouter, NVIDIA, etc.).
//
// Commands:
//   run (default) - Launch Copilot with a configured BYOK provider and model.
//                   Sets COPILOT_PROVIDER_* and COPILOT_MODEL environment variables
//                   before spawning the `copilot` process.
//   add           - Interactively add a new provider: pick from a remote catalog
//                   (or built-in fallback list), enter/confirm the base URL, supply
//                   an API key, and select which models to enable.
//
// Configuration:
//   Provider/model settings are stored in ~/.copilot/byok.json (plain JSON, no secrets).
//
// API Key Security:
//   API keys are never written to the config file. They are stored and retrieved
//   using Git Credential Manager (via the Devlooped.CredentialManager package),
//   which delegates to the OS-native credential store:
//     - Windows: Windows Credential Manager (DPAPI-encrypted)
//     - macOS:   Keychain
//     - Linux:   libsecret / GPG-encrypted credential store
//   Keys are saved with the provider base URL as the target and the provider name
//   as the account, so each provider/endpoint pair gets its own credential entry.
//   At runtime the key is read from the credential store and passed to Copilot
//   solely through the COPILOT_PROVIDER_API_KEY environment variable of the child
//   process — it is never logged or persisted to disk.

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
using CommandContext = Spectre.Console.Cli.CommandContext;

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

class AddCommand : AsyncCommand
{
    internal Task<int> ExecuteAsync([NotNull] CommandContext context)
    {
        return ExecuteAsync(context, CancellationToken.None);
    }

    protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, CancellationToken cancellationToken = default(CancellationToken))
    {
        AnsiConsole.MarkupLine("[bold cyan]Add BYOK Provider Configuration[/]\n");

        List<ProviderDefinition> providerDefinitions;
        try
        {
            providerDefinitions = await ByokSupport.LoadProviderDefinitionsAsync();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Could not load remote provider catalog: {Markup.Escape(ex.Message)}. Falling back to built-in providers.[/]");
            providerDefinitions = ByokSupport.GetFallbackProviderDefinitions();
        }

        var provider = AnsiConsole.Prompt(
            new SelectionPrompt<ProviderDefinition>()
                .Title("Select [green]provider[/]:")
                .AddChoices(providerDefinitions));

        var providerTypeKey = provider.Type;
        var defaultBaseUrl = provider.DefaultBaseUrl;

        var baseUrl = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter [green]Base URL[/]:")
                .DefaultValue(defaultBaseUrl)
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = defaultBaseUrl;

        baseUrl = ByokSupport.NormalizeBaseUrl(baseUrl);

        // Preserve custom aliases when the user overrides a known default endpoint.
        var providerName = provider.Name;
        var needsFriendlyName = !string.IsNullOrWhiteSpace(defaultBaseUrl) &&
            !string.Equals(baseUrl, ByokSupport.NormalizeBaseUrl(defaultBaseUrl), StringComparison.OrdinalIgnoreCase);

        if (needsFriendlyName)
        {
            providerName = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]friendly name[/] for this provider:")
                    .DefaultValue(provider.Name));
        }

        var store = GitCredentialManager.CredentialManager.Create("copilot-byok");
        var existingCredential = ByokSupport.GetCredentialLookupUrls(baseUrl)
            .Select(url => store.Get(url, providerName))
            .FirstOrDefault(credential => credential != null);

        string apiKey;
        if (existingCredential != null)
        {
            AnsiConsole.MarkupLine($"\n[green]✓[/] Found existing API key for [cyan]{providerName}[/]");
            apiKey = existingCredential.Password;
        }
        else
        {
            apiKey = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]API Key[/]:")
                    .Secret());
        }

        AnsiConsole.MarkupLine($"\n[green]✓[/] Provider: {providerName} [dim]({provider.Id})[/]");
        AnsiConsole.MarkupLine($"[green]✓[/] Type: {providerTypeKey}");
        AnsiConsole.MarkupLine($"[green]✓[/] Base URL: {baseUrl}");
        if (existingCredential != null)
            AnsiConsole.MarkupLine("[green]✓[/] API Key: [dim](loaded from secure storage)[/]");
        else
            AnsiConsole.MarkupLine("[green]✓[/] API Key: [dim]***[/]");

        var configPath = GetConfigPath();
        var config = LoadConfig(configPath);
        var existingProvider = config.Providers.FirstOrDefault(p =>
            string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase));

        List<ModelInfo> selectedModels = [];

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            AnsiConsole.MarkupLine("\n[yellow]Fetching available models...[/]");

            var models = await AnsiConsole.Status()
                .StartAsync("Querying model specifications...", async _ =>
                {
                    return await ByokSupport.FetchModelsAsync(baseUrl, apiKey, providerTypeKey);
                });

            if (models.Count > 0)
            {
                var selectionPrompt = new MultiSelectionPrompt<ModelInfo>()
                    .Title("Select [green]models[/] to use:")
                    .PageSize(10)
                    .MoreChoicesText("[grey](Move up and down to reveal more models)[/]")
                    .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]");

                foreach (var model in models)
                {
                    var choice = selectionPrompt.AddChoice(model);
                    if (existingProvider != null && existingProvider.Models.Any(existing => ByokSupport.ModelIdsMatch(existing.Id, model.Id)))
                        choice.Select();
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
            var manualModel = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]model name[/]:")
                    .AllowEmpty());

            if (!string.IsNullOrWhiteSpace(manualModel))
                selectedModels.Add(new ModelInfo { Id = manualModel });
        }

        AnsiConsole.MarkupLine("\n[yellow]Saving configuration...[/]");

        config.Providers.RemoveAll(p =>
            string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(providerName, provider.Name, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase)));

        config.Providers.Add(new ProviderConfig
        {
            Name = providerName,
            Id = provider.Id,
            Type = providerTypeKey,
            BaseUrl = baseUrl,
            Models = selectedModels
        });

        var configDir = Path.GetDirectoryName(configPath)!;
        Directory.CreateDirectory(configDir);

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(configPath, json);

        store.AddOrUpdate(baseUrl, providerName, apiKey);

        AnsiConsole.MarkupLine($"[green]✓[/] Configuration saved to {configPath}");
        AnsiConsole.MarkupLine("[green]✓[/] API key saved securely");

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
            var config = JsonSerializer.Deserialize<ByokConfig>(json) ?? new ByokConfig();
            return ByokSupport.NormalizeConfig(config);
        }
        catch
        {
            return new ByokConfig();
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
                    config = LoadConfig(configPath);
                    selectedProvider = null;
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

        if (selectedProvider == "Copilot (Default)")
        {
            AnsiConsole.MarkupLine("[cyan]Using GitHub Copilot (Default)[/]");
            return LaunchCopilot(null, null, null, null, settings.RemainingArgs);
        }

        var providerConfig = config.Providers.FirstOrDefault(p => p.Name == selectedProvider);
        if (providerConfig == null)
        {
            AnsiConsole.MarkupLine($"[red]Provider '{selectedProvider}' not found in configuration.[/]");
            return 1;
        }

        ModelInfo? selectedModelInfo = null;

        if (selectedModel != null)
        {
            selectedModelInfo = ByokSupport.FindMatchingModel(providerConfig.Models, selectedModel);
            if (selectedModelInfo == null)
                selectedModelInfo = new ModelInfo { Id = selectedModel };
            else
                selectedModel = selectedModelInfo.Id;
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

        var store = GitCredentialManager.CredentialManager.Create("copilot-byok");
        var credential = ByokSupport.GetCredentialLookupUrls(providerConfig.BaseUrl)
            .Select(url => store.Get(url, selectedProvider))
            .FirstOrDefault(found => found != null);
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
            var config = JsonSerializer.Deserialize<ByokConfig>(json) ?? new ByokConfig();
            return ByokSupport.NormalizeConfig(config);
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
        var args = new List<string>();

        if (remainingArgs != null && remainingArgs.Length > 0)
        {
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

        if (providerType == null)
        {
            startInfo.Environment["COPILOT_PROVIDER_TYPE"] = "";
            startInfo.Environment["COPILOT_PROVIDER_BASE_URL"] = "";
            startInfo.Environment["COPILOT_PROVIDER_API_KEY"] = "";
            startInfo.Environment["COPILOT_MODEL"] = "";
            startInfo.Environment["COPILOT_PROVIDER_MAX_PROMPT_TOKENS"] = "";

            AnsiConsole.MarkupLine("[dim]Cleared BYOK environment variables[/]");
        }
        else
        {
            startInfo.Environment["COPILOT_PROVIDER_TYPE"] = providerType;
            startInfo.Environment["COPILOT_PROVIDER_BASE_URL"] = baseUrl ?? "";
            startInfo.Environment["COPILOT_PROVIDER_API_KEY"] = apiKey ?? "";
            startInfo.Environment["COPILOT_MODEL"] = modelInfo?.Id ?? "";
            startInfo.Environment["COPILOT_PROVIDER_MAX_PROMPT_TOKENS"] = modelInfo?.ContextLength?.ToString() ?? "";

            AnsiConsole.MarkupLine($"[dim]Set COPILOT_PROVIDER_TYPE={providerType}[/]");
            AnsiConsole.MarkupLine($"[dim]Set COPILOT_PROVIDER_BASE_URL={baseUrl}[/]");
            AnsiConsole.MarkupLine($"[dim]Set COPILOT_MODEL={modelInfo?.Id}[/]");

            if (modelInfo?.ContextLength != null)
                AnsiConsole.MarkupLine($"[dim]Set COPILOT_PROVIDER_MAX_PROMPT_TOKENS={modelInfo.ContextLength}[/]");
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

#region Data Models

record ByokConfig
{
    public List<ProviderConfig> Providers { get; set; } = [];
}

record ProviderConfig
{
    public string Name { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public List<ModelInfo> Models { get; set; } = [];
}

record ModelInfo
{
    public string Id { get; set; } = string.Empty;
    public int? ContextLength { get; set; }
    public override string ToString() => Id;
}

record ModelSlug(string CleanId = "", string PersistedId = "", string Name = "", string NameKey = "", string Family = "", string FamilyKey = "", string Flavor = "", string FlavorKey = "", ModelVersion? Version = null)
{
    public string BaseKey => string.IsNullOrWhiteSpace(FamilyKey)
        ? NameKey
        : string.IsNullOrWhiteSpace(NameKey)
            ? FamilyKey
            : NameKey + "|" + FamilyKey;

    public string FullKey => string.IsNullOrWhiteSpace(FlavorKey) ? BaseKey : BaseKey + "|" + FlavorKey;
}

record ModelVersion(string Raw = "", int? Year = null, int Month = 0, int Day = 0)
{
    public bool HasYear => Year != null;
    public string SortKey => HasYear ? $"{Year:0000}{Month:00}{Day:00}" : $"{Month:00}{Day:00}";
}

record ModelsApiResponse(List<ModelEntry> Data);
record ModelEntry(string Id = "", int? ContextLength = null, int? MaxInputTokens = null, int? MaxPromptTokens = null, int? MaxOutputTokens = null, int? MaxCompletionTokens = null);
record ProviderDefinition(string Name = "", string Id = "", string Type = "openai", string DefaultBaseUrl = "")
{
    public override string ToString() => $"{Name} ({Id})";
}

record ProviderCatalogResponse(List<ProviderCatalogModel> Data);
record ProviderCatalogModel(ProviderCreator? ModelCreator = null);
record ProviderCreator(string Name = "", string Slug = "");

record OpenRouterModelsResponse(List<OpenRouterModelEntry> Data);
record OpenRouterModelEntry(string Id = "", string? CanonicalSlug = null, int? ContextLength = null, OpenRouterTopProvider? TopProvider = null);
record OpenRouterTopProvider(int? ContextLength = null, int? MaxCompletionTokens = null);

record OpenRouterModelMetadata(string Id = "", int? ContextLength = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ModelInfo))]
[JsonSerializable(typeof(List<ModelInfo>))]
[JsonSerializable(typeof(ModelEntry))]
[JsonSerializable(typeof(ModelsApiResponse))]
[JsonSerializable(typeof(ProviderConfig))]
[JsonSerializable(typeof(ByokConfig))]
[JsonSerializable(typeof(ProviderCatalogResponse))]
[JsonSerializable(typeof(OpenRouterModelsResponse))]
partial class JsonContext : JsonSerializerContext { }

static class ByokSupport
{
    private const string ProviderCatalogUrl = "https://github.com/kzu/run/raw/refs/heads/main/.github/copilot.json";
    private const string OpenRouterModelsUrl = "https://openrouter.ai/api/v1/models";

    private static readonly List<ProviderDefinition> KnownProviders =
    [
        new("OpenAI",     "openai",    "openai",    "https://api.openai.com"),
        new("Anthropic",  "anthropic", "anthropic", "https://api.anthropic.com"),
        new("xAI",        "xai",       "openai",    "https://api.x.ai"),
        new("OpenRouter", "openrouter", "openai",   "https://openrouter.ai/api"),
        new("NVIDIA",     "nvidia",    "openai",    "https://integrate.api.nvidia.com"),
    ];

    public static List<ProviderDefinition> GetFallbackProviderDefinitions() => [.. KnownProviders.Select(CloneProvider)];

    public static async Task<List<ProviderDefinition>> LoadProviderDefinitionsAsync()
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync(ProviderCatalogUrl);
        var payload = JsonSerializer.Deserialize(json, JsonContext.Default.ProviderCatalogResponse);

        var providers = payload?.Data
            .Select(entry => entry.ModelCreator)
            .OfType<ProviderCreator>()
            .Where(creator => !string.IsNullOrWhiteSpace(creator.Name) && !string.IsNullOrWhiteSpace(creator.Slug))
            .GroupBy(creator => NormalizeProviderId(creator.Slug), StringComparer.OrdinalIgnoreCase)
            .Select(group => CreateProviderDefinition(group.First().Name, group.Key))
            .ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ProviderDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in KnownProviders)
            providers[provider.Id] = CloneProvider(provider);

        return [.. providers.Values.OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public static ByokConfig NormalizeConfig(ByokConfig config)
    {
        foreach (var provider in config.Providers)
        {
            provider.Id = InferProviderId(provider);
            provider.BaseUrl = NormalizeBaseUrl(provider.BaseUrl);
        }
        return config;
    }

    public static async Task<List<ModelInfo>> FetchModelsAsync(string baseUrl, string apiKey, string providerType)
    {
        using var http = new HttpClient();
        var metadataTask = FetchOpenRouterMetadataAsync(http);
        var providerModels = await FetchProviderModelsAsync(http, baseUrl, apiKey, providerType);

        if (providerModels.Count == 0)
            return [];

        var metadata = await metadataTask;

        return [.. providerModels
            .Select(model => EnrichModel(model, metadata))
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ModelInfo
            {
                Id = group.Key,
                ContextLength = group
                    .Select(model => model.ContextLength)
                    .Aggregate((int?)null, MaxNullable)
            })
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public static IEnumerable<string> GetCredentialLookupUrls(string baseUrl)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[]
        {
            NormalizeBaseUrl(baseUrl),
            NormalizeProviderRoot(baseUrl),
            NormalizeLegacyBaseUrl(baseUrl),
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
                yield return candidate;
        }
    }

    public static string InferProviderId(ProviderConfig provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.Id))
            return NormalizeProviderId(provider.Id);

        var byName = KnownProviders.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, provider.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Id, provider.Name, StringComparison.OrdinalIgnoreCase));
        if (byName != null)
            return byName.Id;

        var byUrl = KnownProviders.FirstOrDefault(candidate => BaseUrlMatches(provider.BaseUrl, candidate.DefaultBaseUrl));
        if (byUrl != null)
            return byUrl.Id;

        return provider.Type switch
        {
            "anthropic" => "anthropic",
            "azure" => "azure",
            _ => NormalizeProviderId(provider.Name)
        };
    }

    public static ProviderDefinition CreateProviderDefinition(string name, string id)
    {
        var normalizedId = NormalizeProviderId(id);
        var known = KnownProviders.FirstOrDefault(provider => string.Equals(provider.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
        if (known != null)
            return CloneProvider(known);

        return new ProviderDefinition(
            Name: string.IsNullOrWhiteSpace(name) ? normalizedId : name.Trim(),
            Id: normalizedId,
            Type: normalizedId == "anthropic" ? "anthropic" : "openai");
    }

    public static string NormalizeBaseUrl(string? baseUrl) => string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.Trim().TrimEnd('/');

    public static string BuildModelsEndpoint(string baseUrl)
    {
        var root = NormalizeProviderRoot(baseUrl);
        return string.IsNullOrWhiteSpace(root) ? string.Empty : root + "/v1/models";
    }

    public static bool ModelIdsMatch(string? left, string? right)
    {
        var parsedLeft = ParseModelSlug(left);
        var parsedRight = ParseModelSlug(right);

        if (string.IsNullOrWhiteSpace(parsedLeft.CleanId) || string.IsNullOrWhiteSpace(parsedRight.CleanId))
            return false;

        if (string.Equals(parsedLeft.CleanId, parsedRight.CleanId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parsedLeft.PersistedId, parsedRight.PersistedId, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(parsedLeft.FullKey) &&
               string.Equals(parsedLeft.FullKey, parsedRight.FullKey, StringComparison.OrdinalIgnoreCase);
    }

    public static ModelInfo? FindMatchingModel(IEnumerable<ModelInfo> models, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var requested = ParseModelSlug(modelId);
        var ranked = models
            .Select(model => new
            {
                Model = model,
                Parsed = ParseModelSlug(model.Id),
                Score = GetConfiguredModelMatchScore(ParseModelSlug(model.Id), requested),
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Parsed.FlavorKey.Length)
            .ThenByDescending(match => match.Parsed.PersistedId.Length)
            .ToList();

        if (ranked.Count == 0)
            return null;

        if (ranked.Count > 1 &&
            ranked[0].Score == ranked[1].Score &&
            ranked[0].Score < 900 &&
            !string.Equals(ranked[0].Parsed.PersistedId, ranked[1].Parsed.PersistedId, StringComparison.OrdinalIgnoreCase))
            return null;

        return ranked[0].Model;
    }

    private static ProviderDefinition CloneProvider(ProviderDefinition provider) => provider with { };

    private static string NormalizeProviderRoot(string? baseUrl)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        return normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^3]
            : normalized;
    }

    private static string NormalizeLegacyBaseUrl(string? baseUrl)
    {
        var root = NormalizeProviderRoot(baseUrl);
        return string.IsNullOrWhiteSpace(root) ? string.Empty : root + "/v1";
    }

    private static bool BaseUrlMatches(string? current, string? expected)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(expected))
            return false;

        var normalizedCurrent = NormalizeBaseUrl(current);
        var normalizedExpected = NormalizeBaseUrl(expected);
        var legacyExpected = NormalizeLegacyBaseUrl(expected);

        return string.Equals(normalizedCurrent, normalizedExpected, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedCurrent, legacyExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<ModelEntry>> FetchProviderModelsAsync(HttpClient http, string baseUrl, string apiKey, string providerType)
    {
        try
        {
            var modelsEndpoint = BuildModelsEndpoint(baseUrl);
            if (string.IsNullOrWhiteSpace(modelsEndpoint))
                return [];

            using var request = new HttpRequestMessage(HttpMethod.Get, modelsEndpoint);
            ApplyAuthorization(request, apiKey, providerType);

            var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync();
            var payload = JsonSerializer.Deserialize(body, JsonContext.Default.ModelsApiResponse);

            return payload?.Data ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static async Task<List<OpenRouterModelMetadata>> FetchOpenRouterMetadataAsync(HttpClient http)
    {
        var index = new Dictionary<string, OpenRouterModelMetadata>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = await http.GetStringAsync(OpenRouterModelsUrl);
            var payload = JsonSerializer.Deserialize(json, JsonContext.Default.OpenRouterModelsResponse);

            foreach (var model in payload?.Data ?? [])
            {
                var openRouterModelId = ParseModelSlug(model.CanonicalSlug ?? model.Id);
                if (string.IsNullOrWhiteSpace(openRouterModelId.PersistedId))
                    continue;

                var metadata = new OpenRouterModelMetadata(
                    openRouterModelId.PersistedId,
                    model.TopProvider?.ContextLength ?? model.ContextLength);

                index[openRouterModelId.PersistedId] = MergeMetadata(index.TryGetValue(openRouterModelId.PersistedId, out var existing) ? existing : null, metadata);
            }
        }
        catch
        {
        }

        return [.. index.Values.OrderByDescending(model => model.Id.Length).ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)];
    }

    private static ModelInfo EnrichModel(ModelEntry model, IReadOnlyList<OpenRouterModelMetadata> metadata)
    {
        var parsedModel = ParseModelSlug(model.Id);
        var result = new ModelInfo
        {
            Id = string.IsNullOrWhiteSpace(parsedModel.PersistedId) ? GetCleanModelId(model.Id) : parsedModel.PersistedId,
            ContextLength = model.MaxInputTokens ?? model.MaxPromptTokens ?? model.ContextLength,
        };

        var match = FindOpenRouterModelMatch(model.Id, metadata);
        if (match != null)
        {
            result.ContextLength ??= match.ContextLength;
        }

        Debug.Assert(result.ContextLength != null, $"{model.Id} has no context length");
        return result;
    }

    private static OpenRouterModelMetadata? FindOpenRouterModelMatch(string providerModelId, IReadOnlyList<OpenRouterModelMetadata> metadata)
    {
        var providerModel = ParseModelSlug(providerModelId);
        if (string.IsNullOrWhiteSpace(providerModel.CleanId))
            return null;

        return metadata
            .Select(model => new
            {
                Model = model,
                Parsed = ParseModelSlug(model.Id),
                Score = GetMetadataMatchScore(ParseModelSlug(model.Id), providerModel),
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Parsed.FlavorKey.Length)
            .ThenByDescending(match => match.Parsed.Version, ModelVersionComparer.Instance)
            .ThenByDescending(match => match.Parsed.PersistedId.Length)
            .Select(match => match.Model)
            .FirstOrDefault();
    }

    private static string GetCleanModelId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim();
        var slash = normalized.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 && slash < normalized.Length - 1
            ? normalized[(slash + 1)..]
            : normalized;
    }

    private static ModelSlug ParseModelSlug(string? value)
    {
        var cleanId = GetCleanModelId(value);
        if (string.IsNullOrWhiteSpace(cleanId))
            return new();

        var tokens = cleanId
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return new(CleanId: cleanId, PersistedId: cleanId);

        var familyStart = Array.FindIndex(tokens, token => token.Any(char.IsDigit));
        if (familyStart < 0)
        {
            return new(
                CleanId: cleanId,
                PersistedId: cleanId,
                Name: cleanId,
                NameKey: NormalizeProviderId(cleanId));
        }

        var familyEnd = familyStart;
        while (familyEnd + 1 < tokens.Length && LooksLikeFamilyContinuation(tokens, familyEnd + 1))
            familyEnd++;

        var versionTokens = new bool[tokens.Length];
        ModelVersion? latestVersion = null;

        for (var i = familyEnd + 1; i < tokens.Length;)
        {
            if (TryParseModelVersion(tokens, i, out var version, out var consumed))
            {
                latestVersion = MaxVersion(latestVersion, version);

                for (var j = 0; j < consumed; j++)
                    versionTokens[i + j] = true;

                i += consumed;
                continue;
            }

            i++;
        }

        var nameTokens = tokens.Take(familyStart).ToArray();
        var familyTokens = tokens.Skip(familyStart).Take(familyEnd - familyStart + 1).ToArray();
        var flavorTokens = tokens
            .Skip(familyEnd + 1)
            .Where((_, index) => !versionTokens[familyEnd + 1 + index])
            .ToArray();

        var persistedTokens = new List<string>(nameTokens.Length + familyTokens.Length + flavorTokens.Length);
        persistedTokens.AddRange(nameTokens);
        persistedTokens.AddRange(familyTokens);
        persistedTokens.AddRange(flavorTokens);

        var name = string.Join("-", nameTokens);
        var family = string.Join("-", familyTokens);
        var flavor = string.Join("-", flavorTokens);
        var persistedId = persistedTokens.Count == 0 ? cleanId : string.Join("-", persistedTokens);

        return new(
            CleanId: cleanId,
            PersistedId: persistedId,
            Name: name,
            NameKey: NormalizeProviderId(name),
            Family: family,
            FamilyKey: NormalizeFamilyKey(family),
            Flavor: flavor,
            FlavorKey: NormalizeProviderId(flavor),
            Version: latestVersion);
    }

    private static bool LooksLikeFamilyContinuation(string[] tokens, int index)
    {
        if (TryParseModelVersion(tokens, index, out _, out _))
            return false;

        var token = tokens[index];
        return token.Length <= 2 && token.All(char.IsDigit);
    }

    private static bool TryParseModelVersion(string[] tokens, int startIndex, [NotNullWhen(true)] out ModelVersion? version, out int consumed)
    {
        version = null;
        consumed = 0;

        if (startIndex + 2 < tokens.Length &&
            TryParseFourDigitNumber(tokens[startIndex], out var year) &&
            TryParseBoundedNumber(tokens[startIndex + 1], 1, 12, out var month) &&
            TryParseBoundedNumber(tokens[startIndex + 2], 1, 31, out var day))
        {
            version = new($"{tokens[startIndex]}-{tokens[startIndex + 1]}-{tokens[startIndex + 2]}", year, month, day);
            consumed = 3;
            return true;
        }

        var token = tokens[startIndex];
        if (token.Length == 8 &&
            TryParseFourDigitNumber(token[..4], out year) &&
            TryParseBoundedNumber(token.Substring(4, 2), 1, 12, out month) &&
            TryParseBoundedNumber(token.Substring(6, 2), 1, 31, out day))
        {
            version = new(token, year, month, day);
            consumed = 1;
            return true;
        }

        if (token.Length == 4 &&
            TryParseBoundedNumber(token[..2], 1, 12, out month) &&
            TryParseBoundedNumber(token.Substring(2, 2), 1, 31, out day))
        {
            version = new(token, null, month, day);
            consumed = 1;
            return true;
        }

        return false;
    }

    private static bool TryParseFourDigitNumber(string value, out int number) =>
        TryParseBoundedNumber(value, 1000, 9999, out number);

    private static bool TryParseBoundedNumber(string value, int minimum, int maximum, out int number)
    {
        number = 0;
        return value.All(char.IsDigit) &&
               int.TryParse(value, out number) &&
               number >= minimum &&
               number <= maximum;
    }

    private static string NormalizeFamilyKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var buffer = new List<char>(value.Trim().Length);
        var previousWasSeparator = false;

        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Add(char.ToLowerInvariant(ch));
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator)
            {
                buffer.Add('.');
                previousWasSeparator = true;
            }
        }

        return new string(buffer.ToArray()).Trim('.');
    }

    private static int GetConfiguredModelMatchScore(ModelSlug candidate, ModelSlug requested)
    {
        if (string.IsNullOrWhiteSpace(candidate.CleanId) || string.IsNullOrWhiteSpace(requested.CleanId))
            return 0;

        if (string.Equals(candidate.CleanId, requested.CleanId, StringComparison.OrdinalIgnoreCase))
            return 1000;

        if (string.Equals(candidate.PersistedId, requested.PersistedId, StringComparison.OrdinalIgnoreCase))
            return 950;

        if (!string.IsNullOrWhiteSpace(candidate.FullKey) &&
            string.Equals(candidate.FullKey, requested.FullKey, StringComparison.OrdinalIgnoreCase))
            return 900;

        if (!string.IsNullOrWhiteSpace(candidate.BaseKey) &&
            string.Equals(candidate.BaseKey, requested.BaseKey, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(candidate.FlavorKey) && string.IsNullOrWhiteSpace(requested.FlavorKey))
                return 800;

            if (string.IsNullOrWhiteSpace(candidate.FlavorKey) || string.IsNullOrWhiteSpace(requested.FlavorKey))
                return 700;
        }

        return 0;
    }

    private static int GetMetadataMatchScore(ModelSlug candidate, ModelSlug requested)
    {
        var configuredScore = GetConfiguredModelMatchScore(candidate, requested);
        if (configuredScore > 0)
            return configuredScore;

        return !string.IsNullOrWhiteSpace(candidate.BaseKey) &&
               string.Equals(candidate.BaseKey, requested.BaseKey, StringComparison.OrdinalIgnoreCase)
            ? 600
            : 0;
    }

    private static OpenRouterModelMetadata MergeMetadata(OpenRouterModelMetadata? current, OpenRouterModelMetadata incoming) =>
        new(incoming.Id, MaxNullable(current?.ContextLength, incoming.ContextLength));

    private static ModelVersion? MaxVersion(ModelVersion? left, ModelVersion? right)
    {
        if (left == null)
            return right;
        if (right == null)
            return left;

        return ModelVersionComparer.Instance.Compare(left, right) >= 0 ? left : right;
    }

    private static int? MaxNullable(int? left, int? right)
    {
        if (left == null)
            return right;
        if (right == null)
            return left;

        return Math.Max(left.Value, right.Value);
    }

    private static void ApplyAuthorization(HttpRequestMessage request, string apiKey, string providerType)
    {
        if (string.Equals(providerType, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            return;
        }

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static string NormalizeProviderId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var buffer = new List<char>(value.Trim().Length);
        var previousWasSeparator = false;

        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Add(char.ToLowerInvariant(ch));
                previousWasSeparator = false;
                continue;
            }

            if (!previousWasSeparator)
            {
                buffer.Add('-');
                previousWasSeparator = true;
            }
        }

        return new string(buffer.ToArray()).Trim('-');
    }

    private sealed class ModelVersionComparer : IComparer<ModelVersion?>
    {
        public static ModelVersionComparer Instance { get; } = new();

        public int Compare(ModelVersion? left, ModelVersion? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left == null)
                return -1;
            if (right == null)
                return 1;

            if (left.HasYear != right.HasYear)
                return left.HasYear ? 1 : -1;

            if (left.Year != right.Year)
                return Nullable.Compare(left.Year, right.Year);

            var monthComparison = left.Month.CompareTo(right.Month);
            if (monthComparison != 0)
                return monthComparison;

            var dayComparison = left.Day.CompareTo(right.Day);
            if (dayComparison != 0)
                return dayComparison;

            return string.Compare(left.Raw, right.Raw, StringComparison.OrdinalIgnoreCase);
        }
    }
}

#endregion
