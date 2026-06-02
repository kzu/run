# :runner: dnx runfile kzu/run:file.cs

Quickly and easily run any file in this repo using .NET 10 CLI: 

```pwsh
dnx runfile kzu/run:file.cs
```

## Overview

ALL .cs files in this repo are intented to be edited/authored as a file-based .NET 10 app, 
so all package references, msbuild properties and SDK MUST be placed in the same .cs file 
instead of the project file. The project file's only purpose is to serve as a top-level program 
selector for which to run, leveraging SmallSharp.

The following are the available scripts.

## Azure KeyVault to dotnet-secrets sync

```pwsh
dnx runfile kzu/run:vault2secrets.cs
```

Uses the `az` CLI to fetch secrets from an Azure KeyVault and sync them to 
`dotnet user-secrets` store (either current directory project's secret or 
a specific id specified by `--id` argument or interactively).

The summary of the run shows what action was taken for each secret:

:white_check_mark: local secret value matches KeyVault value, no change needed

:heavy_plus_sign: new KeyVault added to local secrets

:pencil: existing local secret value updated to match KeyVault value

![vault2secrets](https://raw.githubusercontent.com/kzu/run/main/img/vault2secrets.png)

The updated secrets JSON is formatted with nested sections as appropriate 
for easier reading/editing.

<!-- include copilot.md -->
## Copilot BYOK (Bring Your Own Key) CLI

A CLI tool that lets you run GitHub Copilot with your own API keys for
third-party LLM providers (OpenAI, Anthropic, xAI, OpenRouter, NVIDIA, etc.).

Model IDs: Uses exact ID from /v1/models endpoint for COPILOT_MODEL (preserves
composite "[VENDOR]/[MODEL]" format required by NVIDIA/OpenRouter).

### Source-based distribution

First-run: 

```
dnx runfile kzu/run:copilot.cs --alias copilot 
```

Use specific SHA, branch or tag to pin version (i.e. kzu/run@main:copilot.cs or kzu/run@0ec029d80:copilot.cs)
Use whichever alias you want to avoid entering the full ref (or don't specify an alias at all)

Subsequent runs: 

```
dnx runfile copilot [args]
```

See [runfile](https://github.com/devlooped/runfile) for additional source-based options.

### Package-based distribution

Run without installing (uses latest version from NuGet):

```
dnx copilot [args]
```

Install globally:

```
dotnet tool install -g copilot
```

Then run with `copilot [args]`.

### Usage

Commands:
  run (default) - Launch Copilot with a configured BYOK provider and model.
                  Sets COPILOT_PROVIDER_* and COPILOT_MODEL (full original ID)
                  environment variables before spawning the `copilot` process.
  add           - Interactively add a new provider: pick from a remote catalog
                  (or built-in fallback list), enter/confirm the base URL, supply
                  an API key, and select which models to enable.

Examples: 

```pwsh
# launch interactively (pick provider + model)
dnx runfile copilot

# launch with a specific provider and model
dnx runfile copilot --provider xai --model grok-4.20-reasoning

# add a new provider
dnx runfile copilot add

# pass extra arguments straight through to the copilot CLI
dnx runfile copilot -- chat -m "hello"
```

### Configuration

Provider, model, and wire-API settings are stored in `~/.copilot/byok.json`.

API keys are **never** written to disk. They are stored and retrieved via 
[Git Credential Manager](https://github.com/git-ecosystem/git-credential-manager), 
which delegates to the OS-native credential store:

- **Windows** — Windows Credential Manager (DPAPI-encrypted)
- **macOS** — Keychain
- **Linux** — libsecret / GPG-encrypted store

At runtime the key is passed to the `copilot` child process exclusively through 
the `COPILOT_PROVIDER_API_KEY` environment variable — it is never logged or 
persisted to disk.

The following environment variables are set before spawning `copilot`:

| Variable | Value |
|----------|-------|
| `COPILOT_PROVIDER_TYPE` | Provider wire type (`openai` / `anthropic`) |
| `COPILOT_PROVIDER_BASE_URL` | Provider base URL (with `/v1` suffix) |
| `COPILOT_PROVIDER_API_KEY` | API key (from secure credential store) |
| `COPILOT_MODEL` | Model ID from /v1/models |
| `COPILOT_PROVIDER_MAX_PROMPT_TOKENS` | Context length (if known) |
| `COPILOT_PROVIDER_WIRE_API` | Wire API (`responses` or `completions`) |

<!-- copilot.md -->

<!-- include tts.md -->
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

<!-- tts.md -->

## Clean bin/obj recursively

```pwsh
dnx runfile kzu/run:clean.cs
```

What you would expect `dotnet clean` to do, but it doesn't :)

![clean](https://raw.githubusercontent.com/kzu/run/main/img/clean.png)


## Contributing

You copy `Run.csproj.rename` to `Run.csproj` and then open it in VS. You just select 
the script you want to run as the startup file and hit F5.

The project file as well as the solution file are set to be ignored by git, so they 
are never committed where they can interfere with the [dnx runfile](https://www.nuget.org/packages/runfile) 
usage.
