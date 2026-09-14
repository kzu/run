#:property PackageId=windom
#:property PackageVersion=0.1.0
#:property Description=Dumps UI automation model of an app
#:property ToolPackageRuntimeIdentifiers=win-x64
#:property EnableWindowsTargeting=true

#:property TargetFramework=net10.0-windows
#:property Nullable=enable
#:property ImplicitUsings=true
#:property UseWPF=true
#:property PublishAot=false

#:package ConsoleAppFramework@5.*
#:package Spectre.Console@0.55.*

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using ConsoleAppFramework;
using Spectre.Console;
using Spectre.Console.Rendering;

ConsoleApp.Run(args, Dump);

/// <summary>Dumps the UI automation model of an app.</summary>
/// <param name="processId">Process ID of the app. Lists UI processes when omitted.</param>
/// <param name="output">-o, JSON file to write. Writes to stdout when omitted.</param>
static int Dump([Argument] int? processId = null, string? output = null)
{
    Process process;
    if (processId is { } parsedId)
    {
        process = Process.GetProcessById(parsedId);
    }
    else
    {
        if (SelectUiProcess() is not { } selected)
            return 1;

        process = selected;
        processId = process.Id;
    }

    if (process.MainWindowHandle == IntPtr.Zero)
        throw new ArgumentException("No main window found");

    var root = AutomationElement.FromHandle(process.MainWindowHandle);
    Dictionary<AutomationProperty, string> properties = typeof(AutomationElement)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Select(x => x.GetValue(null))
        .OfType<AutomationProperty>()
        .ToDictionary(x => x, x =>
        {
            var name = x.ProgrammaticName.Split('.')[^1];
            return name.EndsWith("Property") ? name[..^8] : name;
        });

    var tree = BuildElementTree(root, properties);
    tree["ProcessId"] = processId;

    using Stream stream = output is { } path
        ? File.Create(path)
        : Console.OpenStandardOutput();
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
    JsonSerializer.Serialize(writer, tree, new JsonSerializerOptions { DefaultBufferSize = 1024 });
    writer.Flush();

    return 0;
}

static Dictionary<string, object> BuildElementTree(AutomationElement element, Dictionary<AutomationProperty, string> properties)
{
    var node = new Dictionary<string, object>();

    foreach (var property in properties)
    {
        var value = element.GetCurrentPropertyValue(property.Key, true);
        if (value != null && Type.GetTypeCode(value.GetType()) is var type &&
            type != TypeCode.Object &&
            (type != TypeCode.String || !string.IsNullOrEmpty((string)value)))
            node[property.Value] = value;
    }

    if (element.Current.IsContentElement && GetText(element) is { } text)
        node["Content"] = text;

    // TODO: there's no context menu pattern to allow the model to inspect available contextual actions. 
    // TODO: top-level menus in some apps (i.e. VS) also enable/disable according to current selection.
    // var patterns = element.GetSupportedPatterns();

    var expanded = false;
    ExpandCollapsePattern? expandPattern = null;

    // Avoid expanding this since it's not really part of the app's UI
    var isSystemMenu = element.Current.Name == "System" &&
        element.Current.ControlType == ControlType.MenuItem &&
        element.Current.FrameworkId == "Win32";

    if (!isSystemMenu &&
        (bool)element.GetCurrentPropertyValue(AutomationElement.IsExpandCollapsePatternAvailableProperty) &&
        element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var patternObj) &&
        (expandPattern = patternObj as ExpandCollapsePattern) != null &&
        expandPattern.Current.ExpandCollapseState != ExpandCollapseState.Expanded &&
        expandPattern.Current.ExpandCollapseState != ExpandCollapseState.LeafNode)
    {
        try
        {
            expandPattern.Expand();
            expanded = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Expand failed: {ex.Message}");
        }
    }

    var children = new List<Dictionary<string, object>>();
    var childrenWalker = TreeWalker.ControlViewWalker.GetFirstChild(element);

    while (childrenWalker != null)
    {
        children.Add(BuildElementTree(childrenWalker, properties));
        childrenWalker = TreeWalker.ControlViewWalker.GetNextSibling(childrenWalker);
    }

    if (children.Count > 0)
        node["Children"] = children;

    if (expanded)
        expandPattern?.Collapse();

    return node;
}

static string? GetText(AutomationElement element)
{
    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object patternObj))
        return ((ValuePattern)patternObj).Current.Value ?? default;

    if (element.TryGetCurrentPattern(TextPattern.Pattern, out patternObj))
        return ((TextPattern)patternObj).DocumentRange.GetText(-1).TrimEnd('\r');

    return default;
}

static Process? SelectUiProcess()
{
    var processes = Process.GetProcesses()
        .Where(p =>
        {
            try { return p.MainWindowHandle != IntPtr.Zero; }
            catch { return false; }
        })
        .OrderBy(p =>
        {
            try { return p.ProcessName; }
            catch { return ""; }
        }, StringComparer.OrdinalIgnoreCase)
        .ThenBy(p => p.Id)
        .ToArray();

    if (processes.Length == 0)
    {
        Console.Error.WriteLine("No processes with a UI found.");
        return null;
    }

    var console = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(Console.Error)
    });

    if (!console.Profile.Capabilities.Interactive)
    {
        Console.Error.WriteLine("A process ID is required when not running interactively.");
        return null;
    }

    var filter = "";
    var index = 0;
    Process? chosen = null;

    console.Live(BuildProcessList(processes, filter, index))
        .AutoClear(true)
        .Start(ctx =>
        {
            ctx.Refresh();
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape)
                    return;

                if (key.Key == ConsoleKey.Enter)
                {
                    var current = FilterProcesses(processes, filter);
                    if (current.Length == 0)
                        continue;

                    chosen = current[index];
                    return;
                }

                if (key.Key == ConsoleKey.Backspace && filter.Length > 0)
                {
                    filter = filter[..^1];
                    index = 0;
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    filter += key.KeyChar;
                    index = 0;
                }
                else if (key.Key == ConsoleKey.UpArrow)
                    index--;
                else if (key.Key == ConsoleKey.DownArrow)
                    index++;

                var filtered = FilterProcesses(processes, filter);
                index = filtered.Length == 0 ? 0 : Math.Clamp(index, 0, filtered.Length - 1);
                ctx.UpdateTarget(BuildProcessList(processes, filter, index));
            }
        });

    return chosen;
}

static Process[] FilterProcesses(Process[] processes, string filter) =>
    filter.Length == 0
        ? processes
        : [.. processes.Where(p => ProcessLabel(p).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)];

static string ProcessLabel(Process process)
{
    try { return $"{process.ProcessName} ({process.Id})"; }
    catch { return $"unknown ({process.Id})"; }
}

static IRenderable BuildProcessList(Process[] processes, string filter, int index)
{
    var filtered = FilterProcesses(processes, filter);
    const int page = 15;
    var start = filtered.Length == 0
        ? 0
        : Math.Clamp(index - page / 2, 0, Math.Max(0, filtered.Length - page));

    var items = filtered.Length == 0
        ? "[grey]No matching processes[/]"
        : string.Join('\n', filtered.Skip(start).Take(page).Select((p, i) =>
        {
            var text = ProcessLabel(p).EscapeMarkup();
            return start + i == index ? $"[blue]>[/] [blue]{text}[/]" : $"  {text}";
        }));

    var footer = filter.Length > 0
        ? $"[grey]Filter:[/] {filter.EscapeMarkup()}_"
        : "[grey]Type to filter[/]";

    return new Markup($"Select a process:\n{items}\n{footer}");
}
