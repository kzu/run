// xAI Text-to-Speech CLI
// Generate MP3 audio from text using the xAI TTS API (https://docs.x.ai/developers/model-capabilities/audio/text-to-speech)
// Uses the bidirectional WebSocket API (wss://api.x.ai/v1/tts) — no 15,000 character limit.
//
// Examples:
//   tts -t "Hello from xAI in a confident voice." -o hello.mp3
//   tts -f input.txt -o speech.mp3 -v rex
//   echo "Text via stdin works great too." | tts -o stdin.mp3 -v rex -l en
//   tts -t "Bonjour le monde" --language fr --voice ara -o bonjour.mp3

#:package ConsoleAppFramework@5.*
#:package Spectre.Console@0.51.*
#:property Nullable=enable
#:property ImplicitUsings=enable

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Spectre.Console;

if (OperatingSystem.IsWindows())
    Console.InputEncoding = Console.OutputEncoding = Encoding.UTF8;

await ConsoleApp.RunAsync(args, Tts);

/// <summary>Convert text to high-quality MP3 speech using the xAI TTS API.</summary>
/// <param name="text">-t, Text to synthesize. Reads from stdin when omitted. No length limit (uses WebSocket streaming).</param>
/// <param name="file">-f, Path to a UTF-8 text file to read as input (alternative to -t or stdin).</param>
/// <param name="output">-o, Output .mp3 file path (directories created if needed).</param>
/// <param name="voice">-v, Voice ID: rex (default), eve, ara, sal, leo, or a custom voice ID.</param>
/// <param name="language">-l, BCP-47 code (e.g. en, fr, zh, pt-BR) or "auto".</param>
static async Task<int> Tts(
    string? text = null,
    string? file = null,
    string output = "speech.mp3",
    string voice = "rex",
    string language = "en",
    CancellationToken cancellationToken = default)
{
    string? inputText = text;

    if (!string.IsNullOrWhiteSpace(file))
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Cannot use both [yellow]-t[/] and [yellow]-f[/] at the same time.");
            return 1;
        }

        if (!File.Exists(file))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Input file not found: [yellow]{file}[/]");
            return 1;
        }

        try
        {
            inputText = (await File.ReadAllTextAsync(file, cancellationToken)).Trim();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Failed to read input file '{file}': {ex.Message}");
            return 1;
        }
    }

    if (string.IsNullOrWhiteSpace(inputText))
    {
        if (!Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No text provided via [yellow]-t \"...\"[/], [yellow]-f <file>[/], or stdin.");
            AnsiConsole.MarkupLine("[dim]Tip:[/] pipe input, e.g.  [grey]echo \"Hello\" | tts -o out.mp3[/]  or  [grey]tts -f input.txt -o out.mp3[/]");
            return 1;
        }

        inputText = (await Console.In.ReadToEndAsync(cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(inputText))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No text received from stdin.");
            return 1;
        }
    }

    var apiKey = Environment.GetEnvironmentVariable("XAI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        AnsiConsole.MarkupLine("[red]Error:[/] [bold]XAI_API_KEY[/] environment variable is not set.");
        AnsiConsole.MarkupLine("Get an API key at [link=https://console.x.ai/team/default/api-keys]https://console.x.ai[/] and set the env var.");
        return 1;
    }

    // Create output directory if it doesn't exist
    var fullOutputPath = Path.GetFullPath(output);
    var outDir = Path.GetDirectoryName(fullOutputPath);
    if (!string.IsNullOrEmpty(outDir))
        Directory.CreateDirectory(outDir);

    var tempPath = fullOutputPath + ".part";

    try
    {
        long totalBytes = 0;

        await AnsiConsole.Status()
            .StartAsync("Generating speech with xAI TTS (WebSocket)...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                ctx.Status($"voice=[cyan]{voice}[/]  lang=[cyan]{language}[/]  {inputText!.Length:N0} chars");

                var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                // Explicit transport-level keep-alive (PING/PONG) to survive long synthesis periods
                // through NATs, proxies, and load balancers. .NET defaults are usually ~30s but we make it explicit.
                ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(90);

                var wsUri = $"wss://api.x.ai/v1/tts?voice={Uri.EscapeDataString(voice)}&language={Uri.EscapeDataString(language)}&codec=mp3&sample_rate=24000&bit_rate=96000";
                await ws.ConnectAsync(new Uri(wsUri), cancellationToken);

                try
                {
                    // IMPORTANT: keep the output stream scoped so it is disposed before publishing.
                    // On Windows, an open handle prevents replacing/moving the .part file.
                    await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 65536, useAsync: true);
                    var buffer = new byte[32 * 1024];

                    // For lengthy inputs we must synthesize in multiple utterances.
                    // The server enforces a "Session permit TTL" of 600 seconds per synthesis pipeline.
                    // One giant text.done can exceed this for very long files → "TTS pipeline timed out after 600s".
                    // The WS connection itself has no timeout and supports multi-utterance sessions.
                    var utterances = SplitIntoUtterances(inputText!);

                    foreach (var utterance in utterances)
                    {
                        if (string.IsNullOrWhiteSpace(utterance))
                            continue;

                        await SendTextDeltasAsync(ws, utterance, cancellationToken);
                        await SendJsonAsync(ws, new { type = "text.done" }, cancellationToken);

                        // Receive audio for *this* utterance only. Break on its audio.done.
                        bool utteranceComplete = false;
                        while (ws.State == WebSocketState.Open && !utteranceComplete)
                        {
                            string json = await ReceiveFullTextMessageAsync(ws, buffer, cancellationToken);
                            if (string.IsNullOrWhiteSpace(json))
                                continue;

                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;

                            if (!root.TryGetProperty("type", out var typeProp))
                                continue;

                            string? type = typeProp.GetString();

                            if (type == "audio.delta" && root.TryGetProperty("delta", out var deltaProp))
                            {
                                var b64 = deltaProp.GetString();
                                if (!string.IsNullOrEmpty(b64))
                                {
                                    var chunk = Convert.FromBase64String(b64);
                                    await fs.WriteAsync(chunk, cancellationToken);
                                    totalBytes += chunk.Length;
                                    ctx.Status($"voice=[cyan]{voice}[/]  lang=[cyan]{language}[/]  Receiving [green]{totalBytes / 1024.0:F1} KB[/]");
                                }
                            }
                            else if (type == "audio.done")
                            {
                                await fs.FlushAsync(cancellationToken);
                                utteranceComplete = true; // move to next utterance on same connection
                            }
                            else if (type == "error")
                            {
                                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
                                throw new InvalidOperationException($"TTS error: {msg}");
                            }
                            // Ignore other messages (e.g. audio.clear from a previous clear) for now.
                        }
                    }

                    if (totalBytes == 0)
                        throw new InvalidOperationException("No audio data received from the TTS service.");

                    await fs.FlushAsync(cancellationToken);
                }
                finally
                {
                    // Ensure WS is closed
                    if (ws.State == WebSocketState.Open)
                    {
                        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
                        catch { }
                    }
                    ws.Dispose();
                }

                // Successful completion: atomically publish the file.
                // The stream is guaranteed closed at this point.
                if (File.Exists(fullOutputPath))
                    File.Delete(fullOutputPath);

                File.Move(tempPath, fullOutputPath, overwrite: true);
            });

        // Status block completed successfully
        var kb = totalBytes / 1024.0;
        AnsiConsole.MarkupLine($":check_mark_button: Saved [green]{kb:F1} KB[/] to [cyan]{output}[/]");
        AnsiConsole.MarkupLine($"[dim]voice: {voice}  •  language: {language}  •  mp3 24 kHz / 96 kbps (WebSocket)[/]");

        Process.Start(new ProcessStartInfo
        {
            FileName = fullOutputPath,
            UseShellExecute = true
        });

        return 0;
    }
    catch (InvalidOperationException ex)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
        if (File.Exists(tempPath))
            AnsiConsole.MarkupLine($"[yellow]Partial audio saved to[/] [cyan]{Path.GetFileName(tempPath)}[/] (you may keep or rename it).");
        return 1;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        AnsiConsole.MarkupLine("[yellow]Cancelled by user.[/]");
        if (File.Exists(tempPath))
            AnsiConsole.MarkupLine($"[dim]Partial audio saved to[/] [cyan]{Path.GetFileName(tempPath)}[/]");
        return 130;
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
        if (File.Exists(tempPath))
            AnsiConsole.MarkupLine($"[yellow]Partial audio saved to[/] [cyan]{Path.GetFileName(tempPath)}[/] (you may keep or rename it).");
        return 1;
    }
}

// WebSocket helpers for the bidirectional TTS API
static async Task SendJsonAsync(ClientWebSocket ws, object payload, CancellationToken ct)
{
    string json = JsonSerializer.Serialize(payload);
    byte[] bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
}

static async Task SendTextDeltasAsync(ClientWebSocket ws, string text, CancellationToken ct)
{
    const int maxDelta = 12000; // safely under the 15k per-delta server limit

    if (text.Length <= maxDelta)
    {
        await SendJsonAsync(ws, new { type = "text.delta", delta = text }, ct);
        return;
    }

    // Chunk long text. Prefer breaking at sentence boundaries or whitespace when possible.
    int pos = 0;
    while (pos < text.Length)
    {
        int end = Math.Min(pos + maxDelta, text.Length);

        if (end < text.Length)
        {
            // Look backwards for a nice break point within the last ~300 chars
            int searchStart = Math.Max(pos, end - 300);
            int best = -1;
            for (int i = end - 1; i >= searchStart; i--)
            {
                char c = text[i];
                if (c is '.' or '!' or '?' or '\n')
                {
                    best = i + 1;
                    break;
                }
                if (best < 0 && c == ' ')
                    best = i + 1;
            }
            if (best > pos + 200)
                end = best;
        }

        string chunk = text[pos..end];
        await SendJsonAsync(ws, new { type = "text.delta", delta = chunk }, ct);
        pos = end;
    }
}

static async Task<string> ReceiveFullTextMessageAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
{
    using var ms = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await ws.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close)
            return string.Empty;
        ms.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage && !ct.IsCancellationRequested);

    return Encoding.UTF8.GetString(ms.ToArray());
}

/// <summary>
/// Splits long text into multiple utterances so each synthesis stays well under the server's
/// 600-second "Session permit TTL" per pipeline. We reuse the same nice-break logic as the
/// per-delta chunker but at a larger granularity (~7-8k chars per utterance is safe for most content).
/// The WebSocket connection remains open across utterances (multi-utterance session).
/// </summary>
static List<string> SplitIntoUtterances(string text, int maxUtteranceChars = 7500)
{
    if (string.IsNullOrWhiteSpace(text))
        return [];

    if (text.Length <= maxUtteranceChars)
        return [text];

    var utterances = new List<string>();
    int pos = 0;

    while (pos < text.Length)
    {
        int end = Math.Min(pos + maxUtteranceChars, text.Length);

        if (end < text.Length)
        {
            // Prefer nice break points near the end of the window (mirrors SendTextDeltasAsync logic)
            int searchStart = Math.Max(pos, end - 400);
            int best = -1;

            for (int i = end - 1; i >= searchStart; i--)
            {
                char c = text[i];
                // Strong breaks
                if (c is '.' or '!' or '?' or '\n')
                {
                    // For newlines, prefer double-newline (paragraph) when possible
                    if (c == '\n' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        best = i + 2;
                        break;
                    }
                    best = i + 1;
                    break;
                }
                if (best < 0 && c == ' ')
                    best = i + 1;
            }

            // Only accept the break if it doesn't make the chunk too small
            if (best > pos + (maxUtteranceChars / 3))
                end = best;
        }

        string chunk = text[pos..end].Trim();
        if (chunk.Length > 0)
            utterances.Add(chunk);

        pos = end;
        // Skip leading whitespace for next chunk
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            pos++;
    }

    return utterances;
}
