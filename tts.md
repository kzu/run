## xAI Text-to-Speech CLI

Generate MP3 audio from text using the xAI TTS API (https://docs.x.ai/developers/model-capabilities/audio/text-to-speech)
Uses the bidirectional WebSocket API (wss://api.x.ai/v1/tts) — no 15,000 character limit.

Examples:
  tts -t "Hello from xAI in a confident voice." -o hello.mp3
  tts -f input.txt -o speech.mp3 -v rex
  echo "Text via stdin works great too." | tts -o stdin.mp3 -v rex -l en
  tts -t "Bonjour le monde" --language fr --voice ara -o bonjour.mp3

### Usage

```pwsh
# Synthesize text directly
tts -t "Hello from xAI" -o hello.mp3

# Read from a file
tts -f input.txt -o speech.mp3 -v rex

# Pipe text via stdin
echo "Hello world" | tts -o output.mp3

# Specify voice and language
tts -t "Bonjour" -o french.mp3 -v ara -l fr
```

### Arguments

| Argument | Short | Description |
|----------|-------|-------------|
| `--text` | `-t` | Text to synthesize. Reads from stdin when omitted. No length limit. |
| `--file` | `-f` | Path to UTF-8 text file to read as input (alternative to `-t` or stdin). |
| `--output` | `-o` | Output MP3 file path. Directories are created if needed. Default: `speech.mp3` |
| `--voice` | `-v` | Voice ID: `rex` (default), `eve`, `ara`, `sal`, `leo`, or custom voice ID. |
| `--language` | `-l` | BCP-47 language code (e.g. `en`, `fr`, `zh`, `pt-BR`) or `auto`. Default: `en` |

### Setup

Set the `XAI_API_KEY` environment variable with your xAI API key:

```pwsh
$env:XAI_API_KEY = "your-api-key-here"
```

Get an API key at [https://console.x.ai/team/default/api-keys](https://console.x.ai/team/default/api-keys).

### Source-based distribution

First-run: 

```
dnx runfile kzu/run:tts.cs --alias tts
```

Use specific SHA, branch or tag to pin version (i.e. kzu/run@main:tts.cs or kzu/run@asdf01234:tts.cs)
Use whichever alias you want to avoid entering the full ref (or don't specify an alias at all and run 
with the ref each time).

Subsequent runs: 

```
dnx runfile tts [args]
```

See [runfile](https://github.com/devlooped/runfile) for additional source-based options.

### Package-based distribution

Run without installing (uses latest version from NuGet):

```
dnx tts [args]
```

Install globally:

```
dotnet tool install -g tts
```

Then run with `tts [args]`.
