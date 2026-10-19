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
    return root;
}

string OutsideCache(string name)
{
    string path = Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(path);
    File.WriteAllText(Path.Combine(path, "marker.txt"), "x");
    return path;
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
Check("help rust", help.Stdout.Contains("--rust", StringComparison.Ordinal), help.Stdout + help.Stderr);
Check("help nuget", help.Stdout.Contains("--nuget", StringComparison.Ordinal), help.Stdout + help.Stderr);
Check("help node", help.Stdout.Contains("--node", StringComparison.Ordinal), help.Stdout + help.Stderr);
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
});

Scenario("node", "--node", root =>
{
    Expect(root, "bin", false);
    Expect(root, "obj", false);
    Expect(root, "node_modules", false);
    Expect(root, "publish", true);
    Expect(root, "artifacts", true);
});

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
    Expect(root, Path.Combine("cargo", "target"), true);
});

Scenario("rust", "--rust", root =>
{
    Expect(root, Path.Combine("cargo", "target"), false);
    Expect(root, Path.Combine("notcargo", "target", "keep.txt"), true);
    Expect(root, "bin", false);
    Expect(root, "publish", true);
});

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
        Check("all nuget packages removed", !Directory.Exists(allPackages), allPackages);
        Check("all nuget http removed", !Directory.Exists(allHttp), allHttp);
    }, new Dictionary<string, string>
    {
        ["NUGET_PACKAGES"] = allPackages,
        ["NUGET_HTTP_CACHE_PATH"] = allHttp,
    });
}
finally
{
    if (Directory.Exists(allPackages)) Directory.Delete(allPackages, true);
    if (Directory.Exists(allHttp)) Directory.Delete(allHttp, true);
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
try
{
    Scenario("dry-run all", "--dry-run --node --publish --artifacts --rust --nuget", root =>
    {
        Expect(root, "bin", true);
        Expect(root, "obj", true);
        Expect(root, "node_modules", true);
        Expect(root, "publish", true);
        Expect(root, "artifacts", true);
        Expect(root, Path.Combine("cargo", "target", "debug", "app.txt"), true);
        Expect(root, Path.Combine("notcargo", "target", "keep.txt"), true);
        Expect(root, Path.Combine(".hidden", "bin"), true);
        Check("dry nuget packages kept", Directory.Exists(dryPackages), dryPackages);
        Check("dry nuget http kept", Directory.Exists(dryHttp), dryHttp);
    }, new Dictionary<string, string>
    {
        ["NUGET_PACKAGES"] = dryPackages,
        ["NUGET_HTTP_CACHE_PATH"] = dryHttp,
    });
}
finally
{
    if (Directory.Exists(dryPackages)) Directory.Delete(dryPackages, true);
    if (Directory.Exists(dryHttp)) Directory.Delete(dryHttp, true);
}

File.WriteAllText(Path.Combine(logDir, "clean-flags.log"), flagsLog.ToString());

string left = Fixture();
string right = Fixture();
var first = Run(left, "--node --publish --artifacts --rust");
var second = Run(right, "--node --publish --artifacts --rust");
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
