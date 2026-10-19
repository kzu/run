// Cleans bin/obj recursively
#:property PackageId=cleanr
#:property PackageVersion=0.3.0
#:property ProjectProperty=PackageVersion
#:property Description=Recursively clean bin/obj and optionally node, Cargo, Go, Yarn, pip, uv, publish, artifacts, and NuGet caches. --all turns every opt-in on.
#:property ToolPackageRuntimeIdentifiers=win-x64;linux-x64;osx-arm64;any

#:package Spectre.Console@0.51.*
#:property Nullable=enable
#:property ImplicitUsings=enable
#:package ConsoleAppFramework@5.*
#:package ThisAssembly.Project@2.*
#:package ThisAssembly.Git@2.*

using System.ComponentModel;
using System.Diagnostics;
using ConsoleAppFramework;
using Spectre.Console;

ConsoleApp.Version = $"v{ThisAssembly.Project.PackageVersion} ({ThisAssembly.Git.Sha})";

ConsoleApp.Run(args, Clean);

/// <summary>Recursively cleans bin/obj and optionally node, Cargo, Go, Yarn, pip, uv, publish, artifacts, and NuGet caches.</summary>
/// <param name="dir">Optional directory to start the clean. Defaults to current directory.</param>
/// <param name="dryRun">List directories but don't actually delete them.</param>
/// <param name="node">Opt-in to clean node_modules directories and the npm and pnpm caches.</param>
/// <param name="dotted">Opt-in to clean inside hidden directories.</param>
/// <param name="publish">Opt-in to clean publish directories.</param>
/// <param name="artifacts">Opt-in to clean artifacts directories.</param>
/// <param name="rust">Opt-in to clean Cargo target directories and the CARGO_HOME registry and git caches.</param>
/// <param name="nuget">Opt-in to clean NuGet http-cache and global-packages directories.</param>
/// <param name="go">Opt-in to clean the Go build cache and module cache.</param>
/// <param name="yarn">Opt-in to clean the user-level Yarn cache.</param>
/// <param name="pip">Opt-in to clean the pip cache.</param>
/// <param name="uv">Opt-in to clean the uv cache.</param>
/// <param name="all">Turn on node, dotted, publish, artifacts, rust, nuget, go, yarn, pip, and uv.</param>
static void Clean(
    bool dryRun,
    [Argument] string? dir = default,
    bool node = false,
    bool dotted = false,
    bool publish = false,
    bool artifacts = false,
    bool rust = false,
    bool nuget = false,
    bool go = false,
    bool yarn = false,
    bool pip = false,
    bool uv = false,
    bool all = false,
    CancellationToken cancellation = default)
{
    if (all)
    {
        node = true;
        dotted = true;
        publish = true;
        artifacts = true;
        rust = true;
        nuget = true;
        go = true;
        yarn = true;
        pip = true;
        uv = true;
    }

    string root = dir ?? Directory.GetCurrentDirectory();
    if (nuget)
        DeleteCaches(dryRun, cancellation, ResolveNuGetCaches());
    if (node)
        DeleteCaches(dryRun, cancellation, ResolveNodeCaches());
    if (rust)
        DeleteCaches(dryRun, cancellation, ResolveCargoCaches());
    if (go)
        DeleteCaches(dryRun, cancellation, ResolveGoCaches());
    if (yarn)
        DeleteCaches(dryRun, cancellation, ResolveYarnCaches(root));
    if (pip)
        DeleteCaches(dryRun, cancellation, ResolvePipCaches());
    if (uv)
        DeleteCaches(dryRun, cancellation, ResolveUvCaches());

    DeleteDirectories(root, dryRun, node, dotted, publish, artifacts, rust, cancellation);
}

static void DeleteDirectories(
    string dir,
    bool dryRun,
    bool cleanNode,
    bool cleanDotted,
    bool cleanPublish,
    bool cleanArtifacts,
    bool cleanRust,
    CancellationToken cancellation)
{
    if (cancellation.IsCancellationRequested)
        return;

    TryDeleteDirectory(Path.Combine(dir, "bin"), dryRun);
    TryDeleteDirectory(Path.Combine(dir, "obj"), dryRun);
    if (cleanNode)
        TryDeleteDirectory(Path.Combine(dir, "node_modules"), dryRun);
    if (cleanPublish)
        TryDeleteDirectory(Path.Combine(dir, "publish"), dryRun);
    if (cleanArtifacts)
        TryDeleteDirectory(Path.Combine(dir, "artifacts"), dryRun);
    if (cleanRust && IsCargoProject(dir))
        TryDeleteDirectory(Path.Combine(dir, "target"), dryRun);

    string[] subDirs;
    try
    {
        subDirs = Directory.GetDirectories(dir);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
    {
        ReportFailure(dir, ex);
        return;
    }

    foreach (string subDir in subDirs)
    {
        string name = Path.GetFileName(subDir);
        if (!cleanDotted && name.StartsWith('.'))
            continue;

        if (name == "node_modules")
            continue;

        if (cleanPublish && name == "publish")
            continue;

        if (cleanArtifacts && name == "artifacts")
            continue;

        if (cleanRust && name == "target" && IsCargoProject(dir))
            continue;

        if (cancellation.IsCancellationRequested)
            break;

        DeleteDirectories(subDir, dryRun, cleanNode, cleanDotted, cleanPublish, cleanArtifacts, cleanRust, cancellation);
    }
}

static bool IsCargoProject(string dir) => File.Exists(Path.Combine(dir, "Cargo.toml"));

static void DeleteCaches(bool dryRun, CancellationToken cancellation, IEnumerable<string> paths)
{
    foreach (string path in paths)
    {
        if (cancellation.IsCancellationRequested)
            return;

        if (IsSafeCachePath(path))
            TryDeleteDirectory(path, dryRun);
    }
}

// registry and git are the download caches. bin, config, and credentials stay.
static IEnumerable<string> ResolveCargoCaches()
{
    if (EnvWins(["CARGO_HOME"], out string? home))
    {
        if (home is null)
            yield break;
    }
    else
    {
        home = HomeOrNull() is string homeDir ? Path.Combine(homeDir, ".cargo") : null;
        if (home is null || !Path.IsPathRooted(home))
            yield break;
    }

    if (!Path.IsPathRooted(home))
        yield break;

    yield return Path.Combine(home, "registry");
    yield return Path.Combine(home, "git");
}

static IEnumerable<string> ResolveNodeCaches()
{
    foreach (string path in One(["npm_config_cache", "NPM_CONFIG_CACHE"], "npm", "config get cache", NpmCacheDefault))
        yield return path;

    foreach (string path in One(
        ["pnpm_config_store_dir", "PNPM_CONFIG_STORE_DIR", "npm_config_store_dir", "NPM_CONFIG_STORE_DIR"],
        "pnpm",
        "store path",
        PnpmStoreDefault))
        yield return path;

    foreach (string path in One(
        ["pnpm_config_cache_dir", "PNPM_CONFIG_CACHE_DIR", "npm_config_cache_dir", "NPM_CONFIG_CACHE_DIR"],
        "pnpm",
        "config get cache-dir",
        PnpmCacheDefault))
        yield return path;
}

static IEnumerable<string> ResolveGoCaches()
{
    foreach (string path in One(["GOCACHE"], "go", "env GOCACHE", GoBuildDefault))
        yield return path;

    foreach (string path in One(["GOMODCACHE"], "go", "env GOMODCACHE", GoModDefault))
        yield return path;
}

static IEnumerable<string> ResolveYarnCaches(string tree)
{
    if (EnvWins(["YARN_CACHE_FOLDER"], out string? configured))
    {
        if (configured is not null)
            yield return configured;
        yield break;
    }

    var found = new List<string>();
    if (TryToolPath("yarn", "cache dir", out string? classic))
        AddOutside(found, classic, tree);
    if (TryToolPath("yarn", "config get globalFolder", out string? globalFolder))
        AddOutside(found, Path.Combine(globalFolder!, "cache"), tree);

    AddOutside(found, YarnClassicDefault(), tree);
    AddOutside(found, YarnBerryDefault(), tree);
    foreach (string path in found)
        yield return path;
}

static IEnumerable<string> ResolvePipCaches() =>
    One(["PIP_CACHE_DIR"], "pip", "cache dir", PipCacheDefault, ("python", "-m pip cache dir"));

static IEnumerable<string> ResolveUvCaches() =>
    One(["UV_CACHE_DIR"], "uv", "cache dir", UvCacheDefault);

static IEnumerable<string> One(
    string[] names,
    string file,
    string arguments,
    Func<string?> fallback,
    params (string File, string Arguments)[] extra)
{
    if (EnvWins(names, out string? path))
    {
        if (path is not null)
            yield return path;
        yield break;
    }

    if (TryToolPath(file, arguments, out path))
    {
        yield return path!;
        yield break;
    }

    foreach ((string extraFile, string extraArguments) in extra)
    {
        if (!TryToolPath(extraFile, extraArguments, out path))
            continue;

        yield return path!;
        yield break;
    }

    path = fallback();
    if (path is not null && Path.IsPathRooted(path))
        yield return path;
}

// A set but unusable value (off, relative) suppresses the tool and the default path.
static bool EnvWins(string[] names, out string? path)
{
    path = null;
    foreach (string name in names)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            continue;

        value = value.Trim();
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(value))
            return true;

        path = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return true;
    }

    return false;
}

static void AddOutside(List<string> found, string? path, string tree)
{
    if (string.IsNullOrWhiteSpace(path) || IsUnder(path, tree) || !IsSafeCachePath(path))
        return;

    string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (found.Exists(existing => string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)))
        return;

    found.Add(full);
}

static bool IsUnder(string path, string root)
{
    string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return full.Equals(basePath, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

static string? HomeOrNull()
{
    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    if (string.IsNullOrWhiteSpace(home))
        home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME") ?? "";
    return string.IsNullOrWhiteSpace(home) ? null : home;
}

static string LocalAppData()
{
    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (!string.IsNullOrWhiteSpace(local))
        return local;

    return Environment.GetEnvironmentVariable("LOCALAPPDATA")
        ?? Path.Combine(HomeOrNull() ?? "", "AppData", "Local");
}

static string? NpmCacheDefault()
{
    if (OperatingSystem.IsWindows())
        return Path.Combine(LocalAppData(), "npm-cache");

    return HomeOrNull() is string home ? Path.Combine(home, ".npm") : null;
}

static string? PnpmStoreDefault()
{
    string? pnpmHome = Environment.GetEnvironmentVariable("PNPM_HOME");
    if (!string.IsNullOrWhiteSpace(pnpmHome) && Path.IsPathRooted(pnpmHome))
        return Path.Combine(pnpmHome.Trim(), "store");

    string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg))
        return Path.Combine(xdg.Trim(), "pnpm", "store");

    if (OperatingSystem.IsWindows())
        return Path.Combine(LocalAppData(), "pnpm", "store");

    if (HomeOrNull() is not string home)
        return null;

    if (OperatingSystem.IsMacOS())
        return Path.Combine(home, "Library", "pnpm", "store");

    return Path.Combine(home, ".local", "share", "pnpm", "store");
}

static string? PnpmCacheDefault()
{
    string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
    if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg))
        return Path.Combine(xdg.Trim(), "pnpm");

    if (OperatingSystem.IsWindows())
        return Path.Combine(LocalAppData(), "pnpm-cache");

    if (HomeOrNull() is not string home)
        return null;

    if (OperatingSystem.IsMacOS())
        return Path.Combine(home, "Library", "Caches", "pnpm");

    return Path.Combine(home, ".cache", "pnpm");
}

static string? GoBuildDefault()
{
    if (OperatingSystem.IsWindows())
        return Path.Combine(LocalAppData(), "go-build");

    if (HomeOrNull() is not string home)
        return null;

    if (OperatingSystem.IsMacOS())
        return Path.Combine(home, "Library", "Caches", "go-build");

    string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
    string cache = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg) ? xdg.Trim() : Path.Combine(home, ".cache");
    return Path.Combine(cache, "go-build");
}

static string? GoModDefault()
{
    string? gopath = Environment.GetEnvironmentVariable("GOPATH");
    if (!string.IsNullOrWhiteSpace(gopath))
    {
        string[] parts = gopath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && Path.IsPathRooted(parts[0]))
            return Path.Combine(parts[0], "pkg", "mod");
    }

    return HomeOrNull() is string home ? Path.Combine(home, "go", "pkg", "mod") : null;
}

static string? YarnClassicDefault()
{
    if (OperatingSystem.IsWindows())
        return Path.Combine(LocalAppData(), "Yarn", "Cache");

    if (HomeOrNull() is not string home)
        return null;

    if (OperatingSystem.IsMacOS())
        return Path.Combine(home, "Library", "Caches", "Yarn");

    string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
    string cache = !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg) ? xdg.Trim() : Path.Combine(home, ".cache");
    return Path.Combine(cache, "yarn");
}

static string? YarnBerryDefault() =>
    HomeOrNull() is string home ? Path.Combine(home, ".yarn", "berry", "cache") : null;

static string? PipCacheDefault()
{
    if (OperatingSystem.IsWindows())
        return Path.Combine(LocalAppData(), "pip", "Cache");

    string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
    if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg))
        return Path.Combine(xdg.Trim(), "pip");

    if (HomeOrNull() is not string home)
        return null;

    if (OperatingSystem.IsMacOS())
        return Path.Combine(home, "Library", "Caches", "pip");

    return Path.Combine(home, ".cache", "pip");
}

static string? UvCacheDefault()
{
    if (OperatingSystem.IsWindows())
        return Path.Combine(LocalAppData(), "uv", "cache");

    string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
    if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg))
        return Path.Combine(xdg.Trim(), "uv");

    return HomeOrNull() is string home ? Path.Combine(home, ".cache", "uv") : null;
}

static bool TryToolPath(string file, string arguments, out string? path)
{
    path = null;
    if (!TryRun(file, arguments, out string stdout, out _, out int code) || code != 0)
        return false;

    foreach (string line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        string trimmed = line.Trim().Trim('"');
        if (trimmed.Length == 0
            || trimmed.Equals("undefined", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathRooted(trimmed))
            continue;

        path = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return true;
    }

    return false;
}

static bool TryRun(string file, string arguments, out string stdout, out string stderr, out int code)
{
    stdout = "";
    stderr = "";
    code = -1;
    try
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        if (!process.Start())
            return false;

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        stdout = stdoutTask.Result;
        stderr = stderrTask.Result;
        code = process.ExitCode;
        return true;
    }
    catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
    {
        return false;
    }
}

static IEnumerable<string> ResolveNuGetCaches()
{
    foreach (string resource in new[] { "http-cache", "global-packages" })
    {
        if (TryResolveNuGetLocal(resource, out string? path, out string? error) && path is not null)
        {
            if (IsSafeCachePath(path))
                yield return path;
            continue;
        }

        AnsiConsole.MarkupLine($":cross_mark: nuget {Markup.Escape(resource)}: {Markup.Escape(error ?? "unknown error")}");
    }
}

static bool TryResolveNuGetLocal(string resource, out string? path, out string? error)
{
    string? fromEnv = NuGetEnvOverride(resource);
    if (!string.IsNullOrWhiteSpace(fromEnv))
    {
        path = fromEnv;
        error = null;
        return true;
    }

    path = null;
    error = null;
    if (!TryRun("dotnet", $"nuget locals {resource} --list", out string stdout, out string stderr, out int code))
    {
        error = "failed to start dotnet";
        return false;
    }

    if (code == 0 && TryParseNuGetLocal(resource, stdout, out path))
        return true;

    error = string.IsNullOrWhiteSpace(stderr) ? $"dotnet nuget locals exited {code}" : stderr.Trim();
    return false;
}

static bool TryParseNuGetLocal(string resource, string output, out string? path)
{
    path = null;
    string prefix = resource + ":";
    foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        string trimmed = line.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            continue;

        path = trimmed[prefix.Length..].Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Length > 0;
    }

    return false;
}

static string? NuGetEnvOverride(string resource) => resource switch
{
    "http-cache" => Environment.GetEnvironmentVariable("NUGET_HTTP_CACHE_PATH"),
    "global-packages" => Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
    _ => null,
};

static bool IsSafeCachePath(string path)
{
    string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string? root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (string.IsNullOrEmpty(root) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        return false;

    return HomeOrNull() is not string home
        || !string.Equals(full, Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}

static void TryDeleteDirectory(string dir, bool dryRun)
{
    try
    {
        if (!Directory.Exists(dir))
            return;

        string path = Markup.Escape(DisplayPath(dir));
        if (!dryRun)
            DeleteDirectory(dir, path);

        AnsiConsole.MarkupLine($":check_mark_button:{(dryRun ? ":ghost:" : "")} {path}");
    }
    catch (Exception ex)
    {
        ReportFailure(dir, ex);
    }
}

static void DeleteDirectory(string dir, string path)
{
    Capabilities caps = AnsiConsole.Profile.Capabilities;
    if (caps.Interactive && caps.Ansi)
    {
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Default)
            .Start(path, _ => Directory.Delete(dir, true));
        return;
    }

    Directory.Delete(dir, true);
}

static void ReportFailure(string dir, Exception ex) =>
    AnsiConsole.MarkupLine($":cross_mark: {Markup.Escape(DisplayPath(dir))}: {Markup.Escape(ex.Message)}");

static string DisplayPath(string dir)
{
    string cwd = Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string full = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (full.Equals(cwd, StringComparison.OrdinalIgnoreCase))
        return ".";

    string prefix = cwd + Path.DirectorySeparatorChar;
    if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        return "." + full[cwd.Length..];

    return dir;
}
