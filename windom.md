## Windows UI automation dump CLI

Dumps the UI Automation tree of a running Windows app as JSON.
Windows-only (UI Automation).

Examples:
```
  windom
  windom 1234
  windom 1234 -o notepad.json
  windom -o ui.json
  windom 1234 | jq
```

### Usage

```pwsh
# Pick a process with a UI (type to filter, Enter to select)
windom

# Dump a known process ID to stdout
windom 1234

# Write JSON to a file
windom 1234 -o notepad.json

# Pick a process, then write JSON to a file
windom -o ui.json
```

When no process ID is given, processes that have a main window are listed as `name (pid)`.
Type to filter, use the arrow keys to move, Enter to dump, Escape to cancel.

### Arguments

| Argument | Short | Description |
|----------|-------|-------------|
| `processId` | | Process ID of the app. Lists UI processes for selection when omitted. |
| `--output` | `-o` | JSON file to write. Writes to stdout when omitted. |

### Source-based distribution

First-run: 

```
ndx runfile kzu/run:windom.cs --alias windom
```

Use specific SHA, branch or tag to pin version (i.e. kzu/run@main:windom.cs or kzu/run@asdf01234:windom.cs)
Use whichever alias you want to avoid entering the full ref (or don't specify an alias at all and run 
with the ref each time).

Subsequent runs: 

```
ndx runfile windom [args]
```

See [runfile](https://github.com/devlooped/runfile) for additional source-based options.
Install [`ndx`](https://github.com/devlooped/ndx#install) if you don't have it yet.

### Package-based distribution

Run without installing via [`ndx`](https://github.com/devlooped/ndx#install) (uses latest version from NuGet):

```
ndx windom [args]
```

Install globally:

```
dotnet tool install -g windom
```

Then run with `windom [args]`.
