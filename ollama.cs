#:package Microsoft.Extensions.AI@9.*
#:package Microsoft.Extensions.DependencyInjection@9.*
#:package OllamaSharp@5.*
#:property Nullable enable
#:property ImplicitUsings enable

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;
using System.Diagnostics;

var services = new ServiceCollection();
services.AddChatClient(new OllamaApiClient("http://localhost:11434/", "gpt-oss:20b"));
var serviceProvider = services.BuildServiceProvider();

var client = serviceProvider.GetRequiredService<IChatClient>();

Console.WriteLine($"Starting chat with Ollama ({client.GetRequiredService<ChatClientMetadata>().DefaultModelId} model). Type 'quit' to exit.");

while (true)
{
    Console.Write("\nYou: ");
    string? userInput = Console.ReadLine();

    if (string.IsNullOrEmpty(userInput) || userInput.ToLower() == "quit")
    {
        Console.WriteLine("Exiting...");
        break;
    }

    try
    {
        ChatMessage[] messages = [new(ChatRole.User, userInput)];

        var stopwatch = Stopwatch.StartNew();

        var response = await client.GetResponseAsync(messages);

        stopwatch.Stop();

        double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;

        Console.WriteLine($"AI: {response.Text}");
        Console.WriteLine($"Time taken: {elapsedSeconds:F2} seconds");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}