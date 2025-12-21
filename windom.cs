#:property TargetFramework=net10.0-windows
#:property Nullable=enable
#:property ImplicitUsings=true
#:property UseWPF=true
#:property PublishAot=false

using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;

if (args.Length == 0 || !int.TryParse(args[0], out var processId))
{
    Console.Error.WriteLine("Usage: windom <PROCESS_ID>");
    return 1;
}

var process = Process.GetProcessById(processId);
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

using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });
JsonSerializer.Serialize(writer, tree, new JsonSerializerOptions { DefaultBufferSize = 1024 });
writer.Flush();

return 0;

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