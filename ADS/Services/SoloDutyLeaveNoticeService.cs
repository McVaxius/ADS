using ADS.Models;

namespace ADS.Services;

internal sealed class SoloDutyLeaveNoticeService
{
    internal const string NoticeText = "If the duty gets stuck, you can leave with /ads leave.";

    private readonly Action<string> showNormalToast;
    private readonly Action<string>? logWarning;
    private bool continuousEntryActive;
    private bool noticeAttemptedForEntry;
    private bool toastFailureLogged;

    public SoloDutyLeaveNoticeService(Action<string> showNormalToast, Action<string>? logWarning = null)
    {
        this.showNormalToast = showNormalToast;
        this.logWarning = logWarning;
    }

    public string LastDiagnosticKey { get; private set; } = string.Empty;
    public bool NoticeAttemptedForEntry => noticeAttemptedForEntry;

    public void Update(DutyContextSnapshot context)
    {
        if (!context.IsLoggedIn)
        {
            ResetEntry();
            return;
        }

        if (!context.InInstancedDuty)
        {
            if (!context.IsUnsafeTransition)
                ResetEntry();
            return;
        }

        if (context.IsUnsafeTransition)
            return;

        continuousEntryActive = true;
        if (!context.PluginEnabled
            || noticeAttemptedForEntry
            || context.CurrentDuty is null
            || context.CurrentDuty.Category != DutyCategory.Solo)
        {
            return;
        }

        noticeAttemptedForEntry = true;
        LastDiagnosticKey = context.ContentFinderConditionId != 0
            ? $"cfc:{context.ContentFinderConditionId}"
            : $"territory:{context.TerritoryTypeId}";
        try
        {
            showNormalToast(NoticeText);
        }
        catch (Exception ex)
        {
            if (toastFailureLogged)
                return;

            toastFailureLogged = true;
            logWarning?.Invoke($"[ADS][SoloDutyNotice] Failed to show notice for {LastDiagnosticKey}: {ex.Message}");
        }
    }

    private void ResetEntry()
    {
        if (!continuousEntryActive && !noticeAttemptedForEntry)
            return;

        continuousEntryActive = false;
        noticeAttemptedForEntry = false;
        LastDiagnosticKey = string.Empty;
    }
}
