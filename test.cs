using System.Diagnostics;

Process.Start(new ProcessStartInfo("copilot")
{
    Environment =
    {
        ["COPILOT_PROVIDER_TYPE"] = "",
        ["COPILOT_PROVIDER_BASE_URL"] = "",
        ["COPILOT_PROVIDER_API_KEY"] = "",
        ["COPILOT_MODEL"] = "",
    }
});