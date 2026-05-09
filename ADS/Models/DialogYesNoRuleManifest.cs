namespace ADS.Models;

public sealed class DialogYesNoRuleManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Description { get; set; } = string.Empty;
    public List<DialogYesNoRule> Rules { get; set; } = [];
}

public sealed class DialogYesNoRule
{
    public bool Enabled { get; set; } = true;
    public string Addon { get; set; } = "SelectYesno";
    public string PromptPattern { get; set; } = string.Empty;
    public string MatchMode { get; set; } = "Contains";
    public string Response { get; set; } = "Yes";
    public double Delay { get; set; } = 0;
    public string Notification { get; set; } = string.Empty;
    public string NotificationCB { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
