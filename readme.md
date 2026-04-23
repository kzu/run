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

## GitHub Copilot BYOK launcher

```pwsh
dnx runfile kzu/run:copilot.cs
```

Runs [GitHub Copilot CLI](https://docs.github.com/en/copilot/using-github-copilot/using-github-copilot-in-the-command-line) 
with your own API keys for third-party LLM providers (OpenAI, Anthropic, xAI, OpenRouter, NVIDIA, etc.).

### First run / alias setup

```pwsh
dnx runfile kzu/run@main:copilot.cs --alias copilot
```

Use specific SHA, branch or tag to pin version (i.e. `kzu/run@main:copilot.cs` or `kzu/run@0ec029d80:copilot.cs`)
After aliasing, subsequent invocations are simply:

```pwsh
dnx runfile copilot [script args] -- [copilot CLI args]
```

### Commands

| Command | Description |
|---------|-------------|
| `run` *(default)* | Launch Copilot with a configured BYOK provider and model |
| `add` | Interactively add a new provider from a remote catalog |

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