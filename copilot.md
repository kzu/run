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
