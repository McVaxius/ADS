using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class TreasurePortalOpenerRelayService : IDisposable
{
    private const int Capacity = 8192;
    private const string MapName = @"Local\ADS.TreasurePortalOpenerRelay.v1";
    private const string MutexName = @"Local\ADS.TreasurePortalOpenerRelay.v1.Mutex";
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FutureClockSkewTolerance = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IPluginLog log;
    private MemoryMappedFile? memoryMappedFile;
    private Mutex? mutex;
    private bool disposed;

    public TreasurePortalOpenerRelayService(IPluginLog log)
    {
        this.log = log;
    }

    public string Status { get; private set; } = "Relay idle.";

    public DateTime LastPublishUtc { get; private set; } = DateTime.MinValue;

    public DateTime LastImportUtc { get; private set; } = DateTime.MinValue;

    public bool TryPublish(TreasurePortalOpenerSnapshot opener, TreasurePortalRelayContext context)
    {
        if (disposed)
            return false;

        if (string.IsNullOrWhiteSpace(context.PartyKey))
        {
            Status = "Relay publish skipped: party discriminator unavailable.";
            return false;
        }

        var snapshot = new TreasurePortalRelaySnapshot(
            opener.OpenerName,
            opener.ContentId,
            opener.PartySlot,
            opener.Source,
            opener.CapturedUtc,
            opener.ChatText,
            context.LocalPlayerName,
            context.LocalContentId,
            context.PartyKey,
            context.InInstancedDuty,
            context.TerritoryTypeId,
            context.ContentFinderConditionId,
            DateTime.UtcNow);

        if (!TryEnsureHandles())
            return false;

        var acquired = false;
        try
        {
            acquired = TryAcquireMutex();
            if (!acquired)
            {
                Status = "Relay publish skipped: relay mutex busy.";
                return false;
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > Capacity - sizeof(int))
            {
                Status = $"Relay publish skipped: snapshot too large ({bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes).";
                return false;
            }

            using var accessor = memoryMappedFile!.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.Write);
            accessor.Write(0, bytes.Length);
            accessor.WriteArray(sizeof(int), bytes, 0, bytes.Length);
            LastPublishUtc = snapshot.PublishedUtc;
            Status = $"Published relay opener '{snapshot.OpenerName}' from {snapshot.Source}.";
            log.Information(
                $"[ADS] Published treasure portal opener relay: source={snapshot.Source}, opener='{snapshot.OpenerName}', slot={FormatSlot(snapshot.PartySlot)}, contentId={FormatId(snapshot.ContentId)}, local='{snapshot.PublisherLocalName}', partyKey={snapshot.PartyKey}, duty={FormatDuty(snapshot)}.");
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Relay publish failed: {ex.Message}";
            log.Debug($"[ADS] Treasure portal opener relay publish failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (acquired)
                mutex!.ReleaseMutex();
        }
    }

    public bool TryReadFresh(TreasurePortalRelayContext context, out TreasurePortalRelaySnapshot snapshot)
    {
        snapshot = default!;
        if (disposed)
            return false;

        if (string.IsNullOrWhiteSpace(context.PartyKey))
        {
            Status = "Relay import skipped: party discriminator unavailable.";
            return false;
        }

        if (!TryEnsureHandles())
            return false;

        var acquired = false;
        try
        {
            acquired = TryAcquireMutex();
            if (!acquired)
            {
                Status = "Relay import skipped: relay mutex busy.";
                return false;
            }

            using var accessor = memoryMappedFile!.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.Read);
            var length = accessor.ReadInt32(0);
            if (length <= 0)
            {
                Status = "Relay empty.";
                return false;
            }

            if (length > Capacity - sizeof(int))
            {
                Status = $"Relay ignored: invalid snapshot length {length.ToString(CultureInfo.InvariantCulture)}.";
                return false;
            }

            var bytes = new byte[length];
            accessor.ReadArray(sizeof(int), bytes, 0, length);
            snapshot = JsonSerializer.Deserialize<TreasurePortalRelaySnapshot>(bytes, JsonOptions)!;
            if (snapshot is null)
            {
                Status = "Relay ignored: snapshot JSON was empty.";
                return false;
            }

            if (!IsFresh(snapshot, DateTime.UtcNow, out var freshnessStatus))
            {
                Status = freshnessStatus;
                return false;
            }

            if (!string.Equals(snapshot.PartyKey, context.PartyKey, StringComparison.Ordinal))
            {
                Status = "Relay ignored: party discriminator mismatch.";
                return false;
            }

            if (!DutyMatches(snapshot, context))
            {
                Status = $"Relay ignored: duty mismatch snapshot={FormatDuty(snapshot)} current={FormatDuty(context)}.";
                return false;
            }

            LastImportUtc = DateTime.UtcNow;
            Status = $"Fresh relay opener '{snapshot.OpenerName}' from {snapshot.Source}.";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Relay import failed: {ex.Message}";
            log.Debug($"[ADS] Treasure portal opener relay import failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (acquired)
                mutex!.ReleaseMutex();
        }
    }

    public void Clear(string reason)
    {
        if (disposed || !TryEnsureHandles())
            return;

        var acquired = false;
        try
        {
            acquired = TryAcquireMutex();
            if (!acquired)
            {
                Status = "Relay clear skipped: relay mutex busy.";
                return;
            }

            using var accessor = memoryMappedFile!.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.Write);
            accessor.Write(0, 0);
            Status = $"Relay cleared after {reason}.";
            log.Information($"[ADS] Cleared treasure portal opener relay after {reason}.");
        }
        catch (Exception ex)
        {
            Status = $"Relay clear failed: {ex.Message}";
            log.Debug($"[ADS] Treasure portal opener relay clear failed: {ex.Message}");
        }
        finally
        {
            if (acquired)
                mutex!.ReleaseMutex();
        }
    }

    public void Dispose()
    {
        disposed = true;
        memoryMappedFile?.Dispose();
        mutex?.Dispose();
    }

    private bool TryEnsureHandles()
    {
        try
        {
            memoryMappedFile ??= MemoryMappedFile.CreateOrOpen(MapName, Capacity, MemoryMappedFileAccess.ReadWrite);
            mutex ??= new Mutex(false, MutexName);
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Relay unavailable: {ex.Message}";
            log.Debug($"[ADS] Treasure portal opener relay unavailable: {ex.Message}");
            return false;
        }
    }

    private bool TryAcquireMutex()
    {
        try
        {
            return mutex!.WaitOne(TimeSpan.FromMilliseconds(5));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static bool IsFresh(TreasurePortalRelaySnapshot snapshot, DateTime now, out string status)
    {
        var capturedAge = now - snapshot.CapturedUtc;
        var publishAge = now - snapshot.PublishedUtc;
        if (capturedAge < -FutureClockSkewTolerance || publishAge < -FutureClockSkewTolerance)
        {
            status = "Relay ignored: snapshot timestamp is from the future.";
            return false;
        }

        if (capturedAge > SnapshotTtl || publishAge > SnapshotTtl)
        {
            status = $"Relay ignored: stale snapshot capturedAge={capturedAge.TotalSeconds:0}s publishedAge={publishAge.TotalSeconds:0}s.";
            return false;
        }

        status = "Relay snapshot fresh.";
        return true;
    }

    private static bool DutyMatches(TreasurePortalRelaySnapshot snapshot, TreasurePortalRelayContext context)
    {
        if (!snapshot.InInstancedDuty)
            return true;

        return context.InInstancedDuty
               && snapshot.TerritoryTypeId == context.TerritoryTypeId
               && (snapshot.ContentFinderConditionId == 0
                   || context.ContentFinderConditionId == 0
                   || snapshot.ContentFinderConditionId == context.ContentFinderConditionId);
    }

    private static string FormatSlot(int? slot)
        => slot.HasValue ? slot.Value.ToString(CultureInfo.InvariantCulture) : "Unknown";

    private static string FormatId(ulong? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "Unknown";

    private static string FormatDuty(TreasurePortalRelaySnapshot snapshot)
        => snapshot.InInstancedDuty
            ? $"{snapshot.TerritoryTypeId.ToString(CultureInfo.InvariantCulture)}:{snapshot.ContentFinderConditionId.ToString(CultureInfo.InvariantCulture)}"
            : "outside";

    private static string FormatDuty(TreasurePortalRelayContext context)
        => context.InInstancedDuty
            ? $"{context.TerritoryTypeId.ToString(CultureInfo.InvariantCulture)}:{context.ContentFinderConditionId.ToString(CultureInfo.InvariantCulture)}"
            : "outside";
}

public sealed record TreasurePortalRelayContext(
    string PartyKey,
    uint TerritoryTypeId,
    uint ContentFinderConditionId,
    bool InInstancedDuty,
    string LocalPlayerName,
    ulong? LocalContentId);

public sealed record TreasurePortalRelaySnapshot(
    string OpenerName,
    ulong? ContentId,
    int? PartySlot,
    string Source,
    DateTime CapturedUtc,
    string ChatText,
    string PublisherLocalName,
    ulong? PublisherLocalContentId,
    string PartyKey,
    bool InInstancedDuty,
    uint TerritoryTypeId,
    uint ContentFinderConditionId,
    DateTime PublishedUtc);
