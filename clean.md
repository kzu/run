## Clean recursively

[![Version](https://img.shields.io/nuget/vpre/cleanr.svg?color=royalblue)](https://www.nuget.org/packages/cleanr)
[![Downloads](https://img.shields.io/nuget/dt/cleanr.svg?color=darkmagenta)](https://www.nuget.org/packages/cleanr)

What you would expect `dotnet clean` to do, but it doesn't :)

Recursively deletes `bin` and `obj` directories. Optionally also cleans `node_modules` plus the npm and pnpm caches (`--node`), `publish` (`--publish`), `artifacts` (`--artifacts`), Visual Studio `.vs` caches (`--vs`), Cargo `target` directories and the `CARGO_HOME` registry and git caches (`--rust`), the Go build and module caches (`--go`), the user-level Yarn cache (`--yarn`), the pip cache (`--pip`), the uv cache (`--uv`), NuGet http and global-package caches (`--nuget`), and hidden directories (`--dotted`). `--all` turns every one of those opt-ins on.

![clean](https://raw.githubusercontent.com/kzu/run/main/img/clean.png)

### Usage

```pwsh
# Clean recursively from the current directory
cleanr

# Preview what would be deleted
cleanr --dry-run

# Start from a specific directory
cleanr ./src

# Also delete node_modules and the npm and pnpm caches
cleanr --node

# Also recurse into hidden directories
cleanr --dotted

# Also delete publish, artifacts, Visual Studio .vs caches, and Cargo target plus CARGO_HOME registry/git
cleanr --publish --artifacts --vs --rust

# Also delete the NuGet http-cache and global-packages folders
cleanr --nuget

# User-level Go, Yarn, pip, and uv caches
cleanr --go --yarn --pip --uv

# Every opt-in at once
cleanr --all
```

### Arguments

| Argument | Description |
|----------|-------------|
| `dir` | Optional directory to start the clean. Defaults to the current directory. |
| `--dry-run` | List directories but don't actually delete them. |
| `--node` | Also clean `node_modules` directories, the npm cache, and the pnpm store and metadata cache. |
| `--publish` | Also clean `publish` directories. |
| `--artifacts` | Also clean `artifacts` directories. |
| `--vs` | Also clean `.vs` directories (Visual Studio caches). |
| `--rust` | Also clean Cargo `target` directories (a `Cargo.toml` sits next to `target`) and the `CARGO_HOME` `registry` and `git` caches. `bin`, config, and credentials stay. |
| `--nuget` | Also clean the NuGet `http-cache` and `global-packages` directories. |
| `--go` | Also clean the Go build cache (`GOCACHE`) and module cache (`GOMODCACHE`). |
| `--yarn` | Also clean the user-level Yarn cache. A project `.yarn/cache` is left in place. |
| `--pip` | Also clean the pip cache. |
| `--uv` | Also clean the uv cache. |
| `--dotted` | Also clean inside hidden directories. |
| `--all` | Turn on `--node`, `--dotted`, `--publish`, `--artifacts`, `--vs`, `--rust`, `--nuget`, `--go`, `--yarn`, `--pip`, and `--uv`. |

User-level caches follow the tool's own override, then the tool's reported path, then the usual default directory. A set but unusable value (`off`, or a relative path) does not fall through to the default.

| Cache | Override | Otherwise |
|-------|----------|-----------|
| npm | `npm_config_cache` | `npm config get cache` |
| pnpm store | `pnpm_config_store_dir` | `pnpm store path` |
| pnpm metadata | `pnpm_config_cache_dir` | `pnpm config get cache-dir` |
| Cargo | `CARGO_HOME` | `~/.cargo` |
| Go build | `GOCACHE` | `go env GOCACHE` |
| Go modules | `GOMODCACHE` | `go env GOMODCACHE` |
| Yarn | `YARN_CACHE_FOLDER` | `yarn cache dir`, Yarn's global folder `cache`, and the classic cache directory |
| pip | `PIP_CACHE_DIR` | `pip cache dir` |
| uv | `UV_CACHE_DIR` | `uv cache dir` |

### Source-based distribution

First-run: 

```
dnx runfile kzu/run:clean.cs --alias clean
```

Use specific SHA, branch or tag to pin version (i.e. kzu/run@main:clean.cs or kzu/run@asdf01234:clean.cs)
Use whichever alias you want to avoid entering the full ref (or don't specify an alias at all and run 
with the ref each time).

Subsequent runs: 

```
dnx runfile clean [args]
```

See [runfile](https://github.com/devlooped/runfile) for additional source-based options.

### Package-based distribution

Fastest path (native AOT, no .NET runtime or SDK required): [`ndx`](https://github.com/devlooped/ndx#install)

```pwsh
# Windows
irm https://github.com/devlooped/ndx/releases/latest/download/install.ps1 | iex

# macOS / Linux
curl -fsSL https://github.com/devlooped/ndx/releases/latest/download/install.sh | sh
```

Then:

```
ndx cleanr [args]
```

Run with the .NET SDK (uses latest version from NuGet):

```
dnx cleanr [args]
```

Install globally:

```
dotnet tool install -g cleanr
```

Then run with `cleanr [args]`.
