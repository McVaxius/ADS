using System.Reflection;

namespace ADS;

internal static class PluginInfo
{
    public const string DisplayName = "AI Duty Solver";
    public const string InternalName = "ADS";
    public const string ShortDisplayName = "ADS";
    public const string Command = "/ads";
    public const string AliasCommand = "/aids";
    public const string SecondaryAliasCommand = "/aisolver";
    public const string SupportUrl = "https://ko-fi.com/mcvaxius";
    public const string DiscordUrl = "https://discord.gg/VsXqydsvpu";
    public const string RepoUrl = "https://github.com/McVaxius/ADS";
    public const string DiscordFeedbackNote = "Scroll down to \"The Dumpster Fire\" in Discord for plugin-specific ADS notes.";
    public const string Summary = "Observer-first dungeon solver shell: passive observation, planner explanation, duty catalog, ownership controls, staged execution phases, immediate dead/opened ghosting, human-edited object-priority JSON, and IPC before broader live execution expands.";
    public const string PilotDutySummary = "Pilot active wave: Tam-Tara, Toto-Rak, Brayflox, and Stone Vigil.";

    public static string GetVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
}
