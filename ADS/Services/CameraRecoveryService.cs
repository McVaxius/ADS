using System.Reflection;
using ADS.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ADS.Services;

internal interface ICameraRecoveryClock
{
    DateTime UtcNow { get; }
}

internal sealed class SystemCameraRecoveryClock : ICameraRecoveryClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

internal readonly record struct CameraRecoveryState(bool FirstPersonActive, bool IdleCameraActive);

internal interface ICameraRecoveryRuntime
{
    bool TryReadState(out CameraRecoveryState state, out string unavailableReason);
    bool TryStopIdleCamera(out string unavailableReason);
    bool TryTapCameraMode(out string unavailableReason);
    void ReleaseInjectedKey(string reason);
}

internal sealed class CameraRecoveryService : IDisposable
{
    internal static readonly TimeSpan AttemptCooldown = TimeSpan.FromSeconds(10);

    private readonly ICameraRecoveryRuntime runtime;
    private readonly ICameraRecoveryClock clock;
    private readonly Action<string>? logWarning;
    private readonly HashSet<string> loggedUnavailableReasons = new(StringComparer.Ordinal);
    private DateTime lastAttemptUtc = DateTime.MinValue;

    public CameraRecoveryService(
        ICameraRecoveryRuntime runtime,
        ICameraRecoveryClock clock,
        Action<string>? logWarning = null)
    {
        this.runtime = runtime;
        this.clock = clock;
        this.logWarning = logWarning;
    }

    public string Status { get; private set; } = "Camera recovery idle.";
    public DateTime LastAttemptUtc => lastAttemptUtc;

    public void Update(DutyContextSnapshot context, bool executionOwned)
    {
        runtime.ReleaseInjectedKey("next framework tick or gate refresh");

        if (!CanCorrect(context, executionOwned))
        {
            Status = "Camera recovery gated.";
            return;
        }

        if (!runtime.TryReadState(out var state, out var readFailure))
        {
            Status = $"Camera recovery unavailable: {readFailure}.";
            LogUnavailableOnce(readFailure);
            return;
        }

        if (!state.IdleCameraActive && !state.FirstPersonActive)
        {
            Status = "Camera state is normal.";
            return;
        }

        var now = clock.UtcNow;
        if (lastAttemptUtc != DateTime.MinValue && now - lastAttemptUtc < AttemptCooldown)
        {
            Status = state.IdleCameraActive
                ? "Idle-camera recovery is cooling down."
                : "First-person recovery is cooling down.";
            return;
        }

        // The shared cooldown begins before the action so rejected commands, missing
        // bindings, held user keys, and raw-input failures cannot retry every frame.
        lastAttemptUtc = now;
        if (state.IdleCameraActive)
        {
            if (runtime.TryStopIdleCamera(out var idleFailure))
            {
                Status = "Sent /icam to stop idle camera.";
                return;
            }

            Status = $"Idle-camera recovery attempt failed: {idleFailure}.";
            LogUnavailableOnce(idleFailure);
            return;
        }

        if (runtime.TryTapCameraMode(out var firstPersonFailure))
        {
            Status = "Tapped the configured camera-mode key.";
            return;
        }

        Status = $"First-person recovery attempt failed: {firstPersonFailure}.";
        LogUnavailableOnce(firstPersonFailure);
    }

    public void Release(string reason)
    {
        runtime.ReleaseInjectedKey(reason);
        Status = $"Camera recovery released during {reason}.";
    }

    public void Dispose()
        => Release("service disposal");

    internal static bool CanCorrect(DutyContextSnapshot context, bool executionOwned)
        => context.PluginEnabled
           && context.IsLoggedIn
           && context.InInstancedDuty
           && context.CurrentDuty is not null
           && executionOwned
           && !context.IsUnsafeTransition
           && !context.Occupied33
           && !context.OccupiedInQuestEvent
           && !context.OccupiedInEvent
           && !context.OccupiedInCutSceneEvent
           && !context.WatchingCutscene;

    private void LogUnavailableOnce(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || !loggedUnavailableReasons.Add(reason))
            return;

        logWarning?.Invoke($"[ADS][CameraRecovery] {reason}.");
    }
}

internal sealed class DalamudCameraRecoveryRuntime : ICameraRecoveryRuntime
{
    private const int MaxRawVirtualKeyCode = 240;
    private delegate ref int GetRawKeyStateRefDelegate(int vkCode);

    private readonly IKeyState keyState;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog? log;
    private readonly GetRawKeyStateRefDelegate? getRawKeyStateRef;
    private SeVirtualKey injectedKey = SeVirtualKey.NO_KEY;
    private int injectedState;

    public DalamudCameraRecoveryRuntime(IKeyState keyState, ICommandManager commandManager, IPluginLog? log = null)
    {
        this.keyState = keyState;
        this.commandManager = commandManager;
        this.log = log;
        getRawKeyStateRef = CreateRawKeyStateRefDelegate(keyState);
    }

    public unsafe bool TryReadState(out CameraRecoveryState state, out string unavailableReason)
    {
        state = default;
        unavailableReason = string.Empty;
        try
        {
            var cameraManager = CameraManager.Instance();
            var camera = cameraManager == null ? null : cameraManager->Camera;
            if (camera == null)
            {
                unavailableReason = "normal camera pointer unavailable";
                return false;
            }

            var eventFramework = EventFramework.Instance();
            if (eventFramework == null)
            {
                unavailableReason = "event framework pointer unavailable";
                return false;
            }

            var firstPerson = IsFirstPersonControlModeValue((int)camera->ControlMode);
            var idleController = &eventFramework->EventSceneModule.EventIdleCamController;
            var idleCamera = camera->IsEventCameraAutoControl
                             && idleController->CurrentTargetObjectId.Id != 0;
            state = new CameraRecoveryState(firstPerson, idleCamera);
            return true;
        }
        catch (Exception ex)
        {
            unavailableReason = $"camera state read failed: {ex.Message}";
            return false;
        }
    }

    public bool TryStopIdleCamera(out string unavailableReason)
    {
        if (GameInteractionHelper.TrySendChatCommand(commandManager, "/icam", log))
        {
            unavailableReason = string.Empty;
            return true;
        }

        unavailableReason = "/icam command was not accepted";
        return false;
    }

    public unsafe bool TryTapCameraMode(out string unavailableReason)
    {
        unavailableReason = string.Empty;
        try
        {
            var uiModule = UIModule.Instance();
            var inputData = uiModule == null ? null : uiModule->GetUIInputData();
            if (inputData == null)
            {
                unavailableReason = "UI input data unavailable";
                return false;
            }

            var keybind = inputData->GetKeybind(InputId.CAMERA_MODE);
            if (keybind == null)
            {
                unavailableReason = "camera-mode keybind unavailable";
                return false;
            }

            SeVirtualKey boundKey = SeVirtualKey.NO_KEY;
            foreach (var setting in keybind->KeySettings)
            {
                if (setting.Key != SeVirtualKey.NO_KEY
                    && setting.KeyModifier == KeyModifierFlag.None
                    && IsKeyboardCompatible(setting.Key))
                {
                    boundKey = setting.Key;
                    break;
                }
            }

            if (boundKey == SeVirtualKey.NO_KEY)
            {
                unavailableReason = "camera mode has no unmodified keyboard bind";
                return false;
            }

            var keyIndex = (int)boundKey;
            if (keyState.GetRawValue(keyIndex) != 0)
            {
                unavailableReason = $"camera-mode key {boundKey} is already down";
                return false;
            }

            if (getRawKeyStateRef == null)
            {
                unavailableReason = "raw key-state writer unavailable";
                return false;
            }

            injectedState = (int)(KeyStateFlags.Pressed | KeyStateFlags.Down);
            getRawKeyStateRef(keyIndex) = injectedState;
            injectedKey = boundKey;
            return true;
        }
        catch (Exception ex)
        {
            unavailableReason = $"camera-mode key injection failed: {ex.Message}";
            return false;
        }
    }

    public void ReleaseInjectedKey(string reason)
    {
        var key = injectedKey;
        var state = injectedState;
        injectedKey = SeVirtualKey.NO_KEY;
        injectedState = 0;
        if (key == SeVirtualKey.NO_KEY)
            return;

        try
        {
            var keyIndex = (int)key;
            if (keyState.GetRawValue(keyIndex) == state)
                keyState.SetRawValue(keyIndex, 0);
            log?.Debug($"[ADS][CameraRecovery] Released ADS-owned {key} state during {reason}.");
        }
        catch (Exception ex)
        {
            log?.Warning($"[ADS][CameraRecovery] Failed to release ADS-owned {key}: {ex.Message}");
        }
    }

    private bool IsKeyboardCompatible(SeVirtualKey key)
    {
        try
        {
            var keyIndex = (int)key;
            return keyIndex is > 0 and < MaxRawVirtualKeyCode && keyState.IsVirtualKeyValid(keyIndex);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsFirstPersonControlModeValue(int controlMode)
        => controlMode is (int)CameraControlMode.FirstPerson
            or (int)CameraControlMode.LockonFirstPerson
            or (int)CameraControlMode.FirstPersonUnk;

    private static GetRawKeyStateRefDelegate? CreateRawKeyStateRefDelegate(IKeyState keyState)
    {
        var method = keyState.GetType().GetMethod(
            "GetRefValue",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(int)],
            modifiers: null);
        return method == null
            ? null
            : (GetRawKeyStateRefDelegate?)Delegate.CreateDelegate(
                typeof(GetRawKeyStateRefDelegate),
                keyState,
                method,
                throwOnBindFailure: false);
    }
}
