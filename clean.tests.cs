#:property Nullable=enable
#:property ImplicitUsings=enable

using System.Diagnostics;
using System.Text;

string repo = Directory.GetCurrentDirectory();
string clean = Path.Combine(repo, "clean.cs");
if (!File.Exists(clean))
{
    Console.Error.WriteLine($"missing {clean}");
    return 1;
}

string logDir = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(Path.GetTempPath(), "clean-tests");
Directory.CreateDirectory(logDir);

int failures = 0;
var flagsLog = new StringBuilder();

void Check(string name, bool condition, string detail)
{
    if (condition)
    {
        Console.WriteLine($"ok {name}");
        return;
    }

    failures++;
    Console.Error.WriteLine($"FAIL {name}: {detail}");
}

string Fixture()
{
    string root = Path.Combine(Path.GetTempPath(), "clean-fixture-" + Guid.NewGuid().ToString("n"));
    foreach (string relative in new[]
    {
        "bin",
        "obj",
        "node_modules",
        "publish",
        "artifacts",
        Path.Combine("cargo", "target", "debug"),
        Path.Combine("notcargo", "target"),
        Path.Combine(".hidden", "publish"),
        Path.Combine(".hidden", "bin"),
        ".vs",
        Path.Combine(".hidden", ".vs"),
    })
        Directory.CreateDirectory(Path.Combine(root, relative));

    File.WriteAllText(Path.Combine(root, "cargo", "Cargo.toml"), "[package]\nname = \"demo\"\nversion = \"0.1.0\"\n");
    File.WriteAllText(Path.Combine(root, "cargo", "target", "debug", "app.txt"), "artifact");
    File.WriteAllText(Path.Combine(root, "notcargo", "target", "keep.txt"), "keep");
    File.WriteAllText(Path.Combine(root, "bin", "marker.txt"), "x");
    File.WriteAllText(Path.Combine(root, "obj", "marker.txt"), "x");
    File.WriteAllText(Path.Combine(root, "node_modules", "marker.txt"), "x");
    File.WriteAllText(Path.Combine(root, "publish", "marker.txt"), "x");
    File.WriteAllText(Path.Combine(root, "artifacts", "marker.txt"), "x");
    File.WriteAllText(Path.Combine(root, ".hidden", "publish", "marker.txt"), "x");
    File.WriteAllText(Path.Combine(root, ".hidden", "bin", "marker.txt"), "x");
    File.WriteAllText(Path.Combine(root, ".vs", "marker.txt"), "x");
    File.WriteAllText(Path.Combine(root, ".hidden", ".vs", "marker.txt"), "x");
    return root;
}

string OutsideCache(string name)
{
    string path = Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(path);
    File.WriteAllText(Path.Combine(path, "marker.txt"), "x");
    return path;
}

ToolCacheSet ToolCaches()
{
    string cargoHome = OutsideCache("clean-cargo");
    Directory.CreateDirectory(Path.Combine(cargoHome, "registry"));
    Directory.CreateDirectory(Path.Combine(cargoHome, "git"));
    Directory.CreateDirectory(Path.Combine(cargoHome, "bin"));
    File.WriteAllText(Path.Combine(cargoHome, "bin", "keep.txt"), "x");
    File.WriteAllText(Path.Combine(cargoHome, "config.toml"), "keep");
    return new ToolCacheSet(
        cargoHome,
        OutsideCache("clean-npm"),
        OutsideCache("clean-pnpm-store"),
        OutsideCache("clean-pnpm-cache"),
        OutsideCache("clean-yarn"),
        OutsideCache("clean-gocache"),
        OutsideCache("clean-gomod"),
        OutsideCache("clean-pip"),
        OutsideCache("clean-uv"));
}

(int Code, string Stdout, string Stderr) Run(string root, string arguments, IDictionary<string, string>? env = null, bool clearNuGetEnv = false)
{
    var psi = new ProcessStartInfo("dotnet", $"\"{clean}\" -- {arguments} \"{root}\"")
    {
        WorkingDirectory = repo,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    if (clearNuGetEnv)
    {
        psi.Environment.Remove("NUGET_PACKAGES");
        psi.Environment.Remove("NUGET_HTTP_CACHE_PATH");
    }

    if (env is not null)
    {
        foreach ((string key, string value) in env)
            psi.Environment[key] = value;
    }

    using Process process = Process.Start(psi)!;
    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
}

string Snapshot(string root)
{
    if (!Directory.Exists(root))
        return "(missing)";

    return string.Join("\n", Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
}

void Expect(string root, string relative, bool exists)
{
    bool actual = Directory.Exists(Path.Combine(root, relative)) || File.Exists(Path.Combine(root, relative));
    Check(relative + (exists ? " kept" : " removed"), actual == exists, $"exists={actual}");
}

void Scenario(string name, string arguments, Action<string> assert, IDictionary<string, string>? env = null)
{
    string root = Fixture();
    string before = Snapshot(root);
    var run = Run(root, arguments, env);
    string after = Snapshot(root);
    flagsLog.AppendLine($"== {name} ==");
    flagsLog.AppendLine("before:");
    flagsLog.AppendLine(before);
    flagsLog.AppendLine("stdout:");
    flagsLog.AppendLine(run.Stdout);
    flagsLog.AppendLine("stderr:");
    flagsLog.AppendLine(run.Stderr);
    flagsLog.AppendLine("after:");
    flagsLog.AppendLine(after);
    flagsLog.AppendLine();
    Check(name + " exit", run.Code == 0, run.Stderr + run.Stdout);
    assert(root);
    try { Directory.Delete(root, true); } catch { /* leftover fixture is fine */ }
}

var help = RunProcess("dotnet", $"\"{clean}\" -- --help", clearNuGetEnv: false);
Check("help publish", help.Stdout.Contains("--publish", StringComparison.Ordinal), help.Stdout + help.Stderr);
Check("help artifacts", help.Stdout.Contains("--artifacts", StringComparison.Ordinal), help.Stdout + help.Stderr);
Check("help vs", help.Stdout.Contains("--vs", StringComparison.Ordinal), help.Stdout + help.Stderr);
Check("help rust", help.Stdout.Contains("--rust", StringComparison.Ordinal), help.Stdout + help.Stderr);
Check("help nuget", help.Stdout.Contains("--nuget", StringComparison.Ordinal), help.Stdout + help.Stderr);
Check("help node", help.Stdout.Contains("--node", StringComparison.Ordinal), help.Stdout + help.Stderr);
Check("help go", help.Stdout.Contains("--go", StringComparison.Ordinal), help.Stdout + help.Stderr);
Check("help yarn", help.Stdout.Contains("--yarn", StringComparison.Ordinal), help.Stdout + help.Stderr);
Check("help pip", help.Stdout.Contains("--pip", StringComparison.Ordinal), help.Stdout + help.Stderr);
Check("help uv", help.Stdout.Contains("--uv", StringComparison.Ordinal), help.Stdout + help.Stderr);
Check("help all", help.Stdout.Contains("--all", StringComparison.Ordinal), help.Stdout + help.Stderr);

Scenario("no flags", "", root =>
{
    Expect(root, "bin", false);
    Expect(root, "obj", false);
    Expect(root, "node_modules", true);
    Expect(root, "publish", true);
    Expect(root, "artifacts", true);
    Expect(root, Path.Combine("cargo", "target"), true);
    Expect(root, Path.Combine("notcargo", "target"), true);
    Expect(root, Path.Combine(".hidden", "publish"), true);
    Expect(root, Path.Combine(".hidden", "bin"), true);
    Expect(root, ".vs", true);
    Expect(root, Path.Combine(".hidden", ".vs"), true);
});

var nodeCaches = ToolCaches();
try
{
    Scenario("node", "--node", root =>
    {
        Expect(root, "bin", false);
        Expect(root, "obj", false);
        Expect(root, "node_modules", false);
        Expect(root, "publish", true);
        Expect(root, "artifacts", true);
        Check("npm cache removed", !Directory.Exists(nodeCaches.Npm), nodeCaches.Npm);
        Check("pnpm store removed", !Directory.Exists(nodeCaches.PnpmStore), nodeCaches.PnpmStore);
        Check("pnpm cache removed", !Directory.Exists(nodeCaches.PnpmCache), nodeCaches.PnpmCache);
        Check("node kept cargo registry", Directory.Exists(Path.Combine(nodeCaches.CargoHome, "registry")), nodeCaches.CargoHome);
        Check("node kept yarn", Directory.Exists(nodeCaches.Yarn), nodeCaches.Yarn);
    }, nodeCaches.Env);
}
finally
{
    nodeCaches.Cleanup();
}

Scenario("publish", "--publish", root =>
{
    Expect(root, "publish", false);
    Expect(root, "artifacts", true);
    Expect(root, "node_modules", true);
    Expect(root, Path.Combine(".hidden", "publish"), true);
    Expect(root, "bin", false);
});

Scenario("artifacts", "--artifacts", root =>
{
    Expect(root, "artifacts", false);
    Expect(root, "publish", true);
    Expect(root, ".vs", true);
    Expect(root, Path.Combine("cargo", "target"), true);
});

Scenario("vs", "--vs", root =>
{
    Expect(root, ".vs", false);
    Expect(root, Path.Combine(".hidden", ".vs"), true);
    Expect(root, Path.Combine(".hidden", "bin"), true);
    Expect(root, "bin", false);
    Expect(root, "publish", true);
    Expect(root, "node_modules", true);
});

var rustCaches = ToolCaches();
try
{
    Scenario("rust", "--rust", root =>
    {
        Expect(root, Path.Combine("cargo", "target"), false);
        Expect(root, Path.Combine("notcargo", "target", "keep.txt"), true);
        Expect(root, "bin", false);
        Expect(root, "publish", true);
        Check("cargo registry removed", !Directory.Exists(Path.Combine(rustCaches.CargoHome, "registry")), rustCaches.CargoHome);
        Check("cargo git removed", !Directory.Exists(Path.Combine(rustCaches.CargoHome, "git")), rustCaches.CargoHome);
        Check("cargo bin kept", File.Exists(Path.Combine(rustCaches.CargoHome, "bin", "keep.txt")), rustCaches.CargoHome);
        Check("cargo config kept", File.Exists(Path.Combine(rustCaches.CargoHome, "config.toml")), rustCaches.CargoHome);
        Check("rust kept npm cache", Directory.Exists(rustCaches.Npm), rustCaches.Npm);
    }, rustCaches.Env);
}
finally
{
    rustCaches.Cleanup();
}

string packages = OutsideCache("clean-nuget-packages");
string http = OutsideCache("clean-nuget-http");
try
{
    Scenario("nuget", "--nuget", root =>
    {
        Expect(root, "bin", false);
        Expect(root, "publish", true);
        Expect(root, Path.Combine("cargo", "target"), true);
        Check("nuget packages removed", !Directory.Exists(packages), packages);
        Check("nuget http removed", !Directory.Exists(http), http);
    }, new Dictionary<string, string>
    {
        ["NUGET_PACKAGES"] = packages,
        ["NUGET_HTTP_CACHE_PATH"] = http,
    });
}
finally
{
    if (Directory.Exists(packages)) Directory.Delete(packages, true);
    if (Directory.Exists(http)) Directory.Delete(http, true);
}

string allPackages = OutsideCache("clean-nuget-packages-all");
string allHttp = OutsideCache("clean-nuget-http-all");
var allCaches = ToolCaches();
var allEnv = new Dictionary<string, string>(allCaches.Env)
{
    ["NUGET_PACKAGES"] = allPackages,
    ["NUGET_HTTP_CACHE_PATH"] = allHttp,
};
try
{
    Scenario("all", "--all", root =>
    {
        Expect(root, "bin", false);
        Expect(root, "obj", false);
        Expect(root, "node_modules", false);
        Expect(root, "publish", false);
        Expect(root, "artifacts", false);
        Expect(root, Path.Combine("cargo", "target"), false);
        Expect(root, Path.Combine("notcargo", "target", "keep.txt"), true);
        Expect(root, Path.Combine(".hidden", "publish"), false);
        Expect(root, Path.Combine(".hidden", "bin"), false);
        Expect(root, ".vs", false);
        Expect(root, Path.Combine(".hidden", ".vs"), false);
        Check("all nuget packages removed", !Directory.Exists(allPackages), allPackages);
        Check("all nuget http removed", !Directory.Exists(allHttp), allHttp);
        Check("all npm removed", !Directory.Exists(allCaches.Npm), allCaches.Npm);
        Check("all pnpm store removed", !Directory.Exists(allCaches.PnpmStore), allCaches.PnpmStore);
        Check("all pnpm cache removed", !Directory.Exists(allCaches.PnpmCache), allCaches.PnpmCache);
        Check("all cargo registry removed", !Directory.Exists(Path.Combine(allCaches.CargoHome, "registry")), allCaches.CargoHome);
        Check("all cargo git removed", !Directory.Exists(Path.Combine(allCaches.CargoHome, "git")), allCaches.CargoHome);
        Check("all cargo bin kept", File.Exists(Path.Combine(allCaches.CargoHome, "bin", "keep.txt")), allCaches.CargoHome);
        Check("all go build removed", !Directory.Exists(allCaches.GoBuild), allCaches.GoBuild);
        Check("all go mod removed", !Directory.Exists(allCaches.GoMod), allCaches.GoMod);
        Check("all yarn removed", !Directory.Exists(allCaches.Yarn), allCaches.Yarn);
        Check("all pip removed", !Directory.Exists(allCaches.Pip), allCaches.Pip);
        Check("all uv removed", !Directory.Exists(allCaches.Uv), allCaches.Uv);
    }, allEnv);
}
finally
{
    if (Directory.Exists(allPackages)) Directory.Delete(allPackages, true);
    if (Directory.Exists(allHttp)) Directory.Delete(allHttp, true);
    allCaches.Cleanup();
}

var toolCaches = ToolCaches();
try
{
    Scenario("go yarn pip uv", "--go --yarn --pip --uv", root =>
    {
        Expect(root, "bin", false);
        Expect(root, "node_modules", true);
        Expect(root, Path.Combine("cargo", "target"), true);
        Check("go build removed", !Directory.Exists(toolCaches.GoBuild), toolCaches.GoBuild);
        Check("go mod removed", !Directory.Exists(toolCaches.GoMod), toolCaches.GoMod);
        Check("yarn removed", !Directory.Exists(toolCaches.Yarn), toolCaches.Yarn);
        Check("pip removed", !Directory.Exists(toolCaches.Pip), toolCaches.Pip);
        Check("uv removed", !Directory.Exists(toolCaches.Uv), toolCaches.Uv);
        Check("tools kept npm", Directory.Exists(toolCaches.Npm), toolCaches.Npm);
        Check("tools kept cargo registry", Directory.Exists(Path.Combine(toolCaches.CargoHome, "registry")), toolCaches.CargoHome);
    }, toolCaches.Env);
}
finally
{
    toolCaches.Cleanup();
}

if (OperatingSystem.IsWindows())
{
    string lockedRoot = Path.Combine(Path.GetTempPath(), "clean-locked-" + Guid.NewGuid().ToString("n"));
    string locked = Path.Combine(lockedRoot, "000-locked");
    string laterBin = Path.Combine(lockedRoot, "zzz-later", "bin");
    string account = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
    try
    {
        Directory.CreateDirectory(Path.Combine(lockedRoot, "bin"));
        File.WriteAllText(Path.Combine(lockedRoot, "bin", "marker.txt"), "x");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "secret.txt"), "x");
        Directory.CreateDirectory(laterBin);
        File.WriteAllText(Path.Combine(laterBin, "marker.txt"), "x");

        var deny = RunProcess("icacls", $"\"{locked}\" /deny \"{account}:(OI)(CI)F\"", clearNuGetEnv: false);
        Check("deny acl", deny.Code == 0, deny.Stdout + deny.Stderr);

        string[] order = Directory.GetDirectories(lockedRoot);
        int lockedIndex = Array.FindIndex(order, path => Path.GetFileName(path) == "000-locked");
        int laterIndex = Array.FindIndex(order, path => Path.GetFileName(path) == "zzz-later");

        var run = Run(lockedRoot, "");
        flagsLog.AppendLine("== locked ==");
        flagsLog.AppendLine(run.Stdout);
        flagsLog.AppendLine(run.Stderr);
        flagsLog.AppendLine();
        Check("locked exit", run.Code == 0, run.Stderr + run.Stdout);
        string flat = run.Stdout.Replace("\r", "").Replace("\n", "");
        Check("locked reported", flat.Contains("000-locked", StringComparison.OrdinalIgnoreCase) && flat.Contains("denied", StringComparison.OrdinalIgnoreCase), run.Stdout);
        Check("locked root bin removed", !Directory.Exists(Path.Combine(lockedRoot, "bin")), lockedRoot);
        if (laterIndex > lockedIndex)
            Check("locked sibling cleaned", !Directory.Exists(laterBin), laterBin);
    }
    finally
    {
        RunProcess("icacls", $"\"{locked}\" /remove:d \"{account}\"", clearNuGetEnv: false);
        if (Directory.Exists(lockedRoot))
        {
            try { Directory.Delete(lockedRoot, true); } catch { /* leftover fixture is fine */ }
        }
    }
}

string dryPackages = OutsideCache("clean-nuget-packages-dry");
string dryHttp = OutsideCache("clean-nuget-http-dry");
var dryCaches = ToolCaches();
var dryEnv = new Dictionary<string, string>(dryCaches.Env)
{
    ["NUGET_PACKAGES"] = dryPackages,
    ["NUGET_HTTP_CACHE_PATH"] = dryHttp,
};
try
{
    Scenario("dry-run all", "--dry-run --all", root =>
    {
        Expect(root, "bin", true);
        Expect(root, "obj", true);
        Expect(root, "node_modules", true);
        Expect(root, "publish", true);
        Expect(root, "artifacts", true);
        Expect(root, Path.Combine("cargo", "target", "debug", "app.txt"), true);
        Expect(root, Path.Combine("notcargo", "target", "keep.txt"), true);
        Expect(root, Path.Combine(".hidden", "bin"), true);
        Expect(root, ".vs", true);
        Expect(root, Path.Combine(".hidden", ".vs"), true);
        Check("dry nuget packages kept", Directory.Exists(dryPackages), dryPackages);
        Check("dry nuget http kept", Directory.Exists(dryHttp), dryHttp);
        Check("dry npm kept", Directory.Exists(dryCaches.Npm), dryCaches.Npm);
        Check("dry pnpm store kept", Directory.Exists(dryCaches.PnpmStore), dryCaches.PnpmStore);
        Check("dry cargo registry kept", Directory.Exists(Path.Combine(dryCaches.CargoHome, "registry")), dryCaches.CargoHome);
        Check("dry cargo bin kept", File.Exists(Path.Combine(dryCaches.CargoHome, "bin", "keep.txt")), dryCaches.CargoHome);
        Check("dry go build kept", Directory.Exists(dryCaches.GoBuild), dryCaches.GoBuild);
        Check("dry go mod kept", Directory.Exists(dryCaches.GoMod), dryCaches.GoMod);
        Check("dry yarn kept", Directory.Exists(dryCaches.Yarn), dryCaches.Yarn);
        Check("dry pip kept", Directory.Exists(dryCaches.Pip), dryCaches.Pip);
        Check("dry uv kept", Directory.Exists(dryCaches.Uv), dryCaches.Uv);
    }, dryEnv);
}
finally
{
    if (Directory.Exists(dryPackages)) Directory.Delete(dryPackages, true);
    if (Directory.Exists(dryHttp)) Directory.Delete(dryHttp, true);
    dryCaches.Cleanup();
}

File.WriteAllText(Path.Combine(logDir, "clean-flags.log"), flagsLog.ToString());

string left = Fixture();
string right = Fixture();
string absent = Path.Combine(Path.GetTempPath(), "clean-absent-" + Guid.NewGuid().ToString("n"));
var absentEnv = new Dictionary<string, string>
{
    ["CARGO_HOME"] = Path.Combine(absent, "cargo"),
    ["npm_config_cache"] = Path.Combine(absent, "npm"),
    ["pnpm_config_store_dir"] = Path.Combine(absent, "pnpm-store"),
    ["pnpm_config_cache_dir"] = Path.Combine(absent, "pnpm-cache"),
};
var first = Run(left, "--node --publish --artifacts --rust", absentEnv);
var second = Run(right, "--node --publish --artifacts --rust", absentEnv);
File.WriteAllText(Path.Combine(logDir, "clean-launch-1.log"), first.Stdout + "\n" + Snapshot(left));
File.WriteAllText(Path.Combine(logDir, "clean-launch-2.log"), second.Stdout + "\n" + Snapshot(right));
Check("launch stdout", Normalize(first.Stdout, left) == Normalize(second.Stdout, right), "normalized stdout differed");
Check("launch disk", Snapshot(left) == Snapshot(right), "on-disk listings differed");
Check("launch removed publish", !Directory.Exists(Path.Combine(left, "publish")) && !Directory.Exists(Path.Combine(right, "publish")), "publish remains");
Check("launch kept non-cargo target", File.Exists(Path.Combine(left, "notcargo", "target", "keep.txt")), "non-cargo target missing");
try { Directory.Delete(left, true); } catch { }
try { Directory.Delete(right, true); } catch { }

var httpList = RunProcess("dotnet", "nuget locals http-cache --list", clearNuGetEnv: true);
var packagesList = RunProcess("dotnet", "nuget locals global-packages --list", clearNuGetEnv: true);
var dryNuGet = Run(repo, "--nuget --dry-run", clearNuGetEnv: true);
var nugetLog = new StringBuilder();
nugetLog.AppendLine("http-cache:");
nugetLog.AppendLine(httpList.Stdout);
nugetLog.AppendLine(httpList.Stderr);
nugetLog.AppendLine("global-packages:");
nugetLog.AppendLine(packagesList.Stdout);
nugetLog.AppendLine(packagesList.Stderr);
nugetLog.AppendLine("clean --nuget --dry-run:");
nugetLog.AppendLine(dryNuGet.Stdout);
nugetLog.AppendLine(dryNuGet.Stderr);
File.WriteAllText(Path.Combine(logDir, "nuget-locals.log"), nugetLog.ToString());

if (httpList.Code != 0 || packagesList.Code != 0)
{
    Console.WriteLine("dotnet nuget locals failed; isolated nuget deletion is the bar");
}
else
{
    string? httpPath = ParseLocal("http-cache", httpList.Stdout);
    string? packagesPath = ParseLocal("global-packages", packagesList.Stdout);
    if (httpPath is not null && Directory.Exists(httpPath))
    {
        Check("lists http-cache", dryNuGet.Stdout.Contains(httpPath, StringComparison.OrdinalIgnoreCase), dryNuGet.Stdout);
        Check("keeps http-cache", Directory.Exists(httpPath), httpPath);
    }

    if (packagesPath is not null && Directory.Exists(packagesPath))
    {
        Check("lists global-packages", dryNuGet.Stdout.Contains(packagesPath, StringComparison.OrdinalIgnoreCase), dryNuGet.Stdout);
        Check("keeps global-packages", Directory.Exists(packagesPath), packagesPath);
    }
}

Console.WriteLine(failures == 0 ? "all passed" : $"{failures} failed");
return failures == 0 ? 0 : 1;

static string Normalize(string text, string root) =>
    SortLines(text.Replace(root, "{root}", StringComparison.OrdinalIgnoreCase));

static string SortLines(string text) =>
    string.Join("\n", text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).OrderBy(line => line, StringComparer.Ordinal));

static string? ParseLocal(string resource, string output)
{
    string prefix = resource + ":";
    foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return trimmed[prefix.Length..].Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    return null;
}

static (int Code, string Stdout, string Stderr) RunProcess(string file, string arguments, bool clearNuGetEnv)
{
    var psi = new ProcessStartInfo(file, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    if (clearNuGetEnv)
    {
        psi.Environment.Remove("NUGET_PACKAGES");
        psi.Environment.Remove("NUGET_HTTP_CACHE_PATH");
    }

    using Process process = Process.Start(psi)!;
    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
}

sealed class ToolCacheSet(
    string cargoHome,
    string npm,
    string pnpmStore,
    string pnpmCache,
    string yarn,
    string goBuild,
    string goMod,
    string pip,
    string uv)
{
    public string CargoHome { get; } = cargoHome;
    public string Npm { get; } = npm;
    public string PnpmStore { get; } = pnpmStore;
    public string PnpmCache { get; } = pnpmCache;
    public string Yarn { get; } = yarn;
    public string GoBuild { get; } = goBuild;
    public string GoMod { get; } = goMod;
    public string Pip { get; } = pip;
    public string Uv { get; } = uv;
    public Dictionary<string, string> Env { get; } = new()
    {
        ["CARGO_HOME"] = cargoHome,
        ["npm_config_cache"] = npm,
        ["pnpm_config_store_dir"] = pnpmStore,
        ["pnpm_config_cache_dir"] = pnpmCache,
        ["YARN_CACHE_FOLDER"] = yarn,
        ["GOCACHE"] = goBuild,
        ["GOMODCACHE"] = goMod,
        ["PIP_CACHE_DIR"] = pip,
        ["UV_CACHE_DIR"] = uv,
    };

    public void Cleanup()
    {
        foreach (string path in new[] { CargoHome, Npm, PnpmStore, PnpmCache, Yarn, GoBuild, GoMod, Pip, Uv })
        {
            if (!Directory.Exists(path))
                continue;

            try { Directory.Delete(path, true); } catch { /* leftover fixture is fine */ }
        }
    }
}
