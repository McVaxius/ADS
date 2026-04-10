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
    public string PromptPattern { get; set; } = string.Empty;
    public string MatchMode { get; set; } = "Contains";
    public string Response { get; set; } = "Yes";
    public string Notes { get; set; } = string.Empty;
}
