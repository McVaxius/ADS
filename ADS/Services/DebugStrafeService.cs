using System.Reflection;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ADS.Services;

public sealed class DebugStrafeService
{
    private const int MaxRawVirtualKeyCode = 240;
    private const string WriterMode = "raw-key-state";
    private const string RawWriterUnavailableReason = "raw key-state writer is unavailable.";

    private delegate ref int GetRawKeyStateRefDelegate(int vkCode);

    private enum DebugStrafeDirection
    {
        None,
        Left,
        Right,
    }

    private readonly IKeyState keyState;
    private readonly IPluginLog? log;
    private readonly GetRawKeyStateRefDelegate? getRawKeyStateRef;
    private DebugStrafeDirection heldDirection = DebugStrafeDirection.None;
    private SeVirtualKey heldKey = SeVirtualKey.NO_KEY;
    private string lastUnavailableLogKey = string.Empty;
    private string lastRawWriterLogKey = string.Empty;

    public DebugStrafeService(IKeyState keyState, IPluginLog? log = null)
    {
        this.keyState = keyState;
        this.log = log;
        getRawKeyStateRef = CreateRawKeyStateRefDelegate(keyState, log);
    }

    public bool Enabled { get; private set; }

    public bool IsHoldingLeft
        => heldDirection == DebugStrafeDirection.Left;

    public bool IsHoldingRight
        => heldDirection == DebugStrafeDirection.Right;

    public string Status { get; private set; } = BuildStatus(enabled: false, DebugStrafeDirection.None, SeVirtualKey.NO_KEY);

    public string Enable()
    {
        Enabled = true;
        Status = BuildIdleStatus();
        log?.Information("[ADS][DebugStrafe] Debug mode enabled.");
        return Status;
    }

    public string Disable(string reason)
    {
        Enabled = false;
        ReleaseInternal(reason, BuildIdleStatus());
        log?.Information($"[ADS][DebugStrafe] Debug mode disabled by {reason}.");
        return Status;
    }

    public string Release(string reason)
    {
        ReleaseInternal(reason, BuildIdleStatus());
        return Status;
    }

    public string ToggleLeft(bool isLoggedIn, bool pluginEnabled)
        => Toggle(DebugStrafeDirection.Left, isLoggedIn, pluginEnabled);

    public string ToggleRight(bool isLoggedIn, bool pluginEnabled)
        => Toggle(DebugStrafeDirection.Right, isLoggedIn, pluginEnabled);

    public void Update(bool isLoggedIn, bool pluginEnabled)
    {
        if (!pluginEnabled)
        {
            if (heldDirection != DebugStrafeDirection.None)
                ReleaseInternal("plugin disabled", BuildIdleStatus());
            return;
        }

        if (!isLoggedIn)
        {
            if (heldDirection != DebugStrafeDirection.None)
                ReleaseInternal("logout", BuildIdleStatus());
            return;
        }

        if (heldDirection == DebugStrafeDirection.None || heldKey == SeVirtualKey.NO_KEY)
            return;

        var direction = heldDirection;
        if (!TryResolveKey(direction, out var currentKey, out var unavailableReason))
        {
            ReleaseInternal($"debug {FormatDirection(direction)} keybind unavailable", BuildIdleStatus());
            Status = BuildUnavailableStatus(direction, unavailableReason);
            LogUnavailable(direction, unavailableReason);
            return;
        }

        if (heldKey != currentKey)
        {
            TrySetKeyState(heldKey, KeyStateFlags.None);
            heldKey = currentKey;
            Status = BuildStatus(enabled: true, direction, currentKey);
            log?.Information($"[ADS][DebugStrafe] {FormatDirection(direction)} keybind changed while held; now using {FormatKey(currentKey)}.");
        }

        if (TrySetKeyState(heldKey, KeyStateFlags.Down | KeyStateFlags.Held))
            return;

        ReleaseInternal($"debug {FormatDirection(direction)} input state unavailable", BuildIdleStatus());
        Status = BuildUnavailableStatus(direction, RawWriterUnavailableReason);
        log?.Warning($"[ADS][DebugStrafe] Released {FormatDirection(direction)} because raw key-state writer was unavailable during update.");
    }

    private string Toggle(DebugStrafeDirection direction, bool isLoggedIn, bool pluginEnabled)
    {
        if (!Enabled)
        {
            Status = $"{BuildStatus(enabled: false, DebugStrafeDirection.None, SeVirtualKey.NO_KEY)} Run /ads debug on first.";
            return Status;
        }

        if (heldDirection == direction)
        {
            ReleaseInternal($"debug {FormatDirection(direction)} toggle", BuildIdleStatus());
            return Status;
        }

        if (heldDirection != DebugStrafeDirection.None)
            ReleaseInternal($"debug {FormatDirection(direction)} opposite toggle", BuildIdleStatus());

        if (!pluginEnabled)
        {
            Status = BuildUnavailableStatus(direction, "Strafe unavailable while ADS is disabled.");
            return Status;
        }

        if (!isLoggedIn)
        {
            Status = BuildUnavailableStatus(direction, "Strafe unavailable while not logged in.");
            return Status;
        }

        if (!TryResolveKey(direction, out var key, out var unavailableReason))
        {
            Status = BuildUnavailableStatus(direction, unavailableReason);
            LogUnavailable(direction, unavailableReason);
            return Status;
        }

        if (!TrySetKeyState(key, KeyStateFlags.Down | KeyStateFlags.Pressed | KeyStateFlags.Held))
        {
            Status = BuildUnavailableStatus(direction, RawWriterUnavailableReason);
            LogRawWriterFailure(key, RawWriterUnavailableReason);
            return Status;
        }

        heldDirection = direction;
        heldKey = key;
        Status = BuildStatus(enabled: true, direction, key);
        log?.Information($"[ADS][DebugStrafe] Holding {FormatDirection(direction)} via {FormatKey(key)}.");
        return Status;
    }

    private void ReleaseInternal(string reason, string releasedStatus)
    {
        var previousDirection = heldDirection;
        var previousKey = heldKey;
        heldDirection = DebugStrafeDirection.None;
        heldKey = SeVirtualKey.NO_KEY;
        Status = releasedStatus;

        if (previousKey != SeVirtualKey.NO_KEY)
            TrySetKeyState(previousKey, KeyStateFlags.None);

        if (previousDirection != DebugStrafeDirection.None)
            log?.Information($"[ADS][DebugStrafe] Released {FormatDirection(previousDirection)} during {reason}.");
    }

    private unsafe bool TryResolveKey(DebugStrafeDirection direction, out SeVirtualKey key, out string unavailableReason)
    {
        key = SeVirtualKey.NO_KEY;
        unavailableReason = string.Empty;

        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            unavailableReason = "UI module is unavailable.";
            return false;
        }

        var inputData = uiModule->GetUIInputData();
        if (inputData == null)
        {
            unavailableReason = "UI input data is unavailable.";
            return false;
        }

        var inputId = GetInputId(direction);
        var keybind = inputData->GetKeybind(inputId);
        if (keybind == null)
        {
            unavailableReason = $"{inputId} has no keybind data.";
            return false;
        }

        var sawKeyboardBind = false;
        var sawModifiedKeyboardBind = false;
        var sawIncompatibleKeyboardBind = false;
        foreach (var setting in keybind->KeySettings)
        {
            if (setting.Key == SeVirtualKey.NO_KEY)
                continue;

            sawKeyboardBind = true;
            if (setting.KeyModifier != KeyModifierFlag.None)
            {
                sawModifiedKeyboardBind = true;
                continue;
            }

            if (!IsKeyboardCompatible(setting.Key))
            {
                sawIncompatibleKeyboardBind = true;
                continue;
            }

            key = setting.Key;
            return true;
        }

        unavailableReason = sawModifiedKeyboardBind
            ? $"{inputId} only has modified keyboard bind(s)."
            : sawIncompatibleKeyboardBind
                ? $"{inputId} has no raw key-state-compatible bind."
                : sawKeyboardBind
                    ? $"{inputId} has no unmodified keyboard-compatible bind."
                    : $"{inputId} has no keyboard bind.";
        return false;
    }

    private bool TrySetKeyState(SeVirtualKey key, KeyStateFlags state)
    {
        var rawWritten = TrySetRawKeyState(key, state);
        TrySetDiagnosticUiInputState(key, state);
        return rawWritten;
    }

    private bool TrySetRawKeyState(SeVirtualKey key, KeyStateFlags state)
    {
        if (!IsKeyboardCompatible(key))
            return false;

        try
        {
            var keyIndex = (int)key;
            if (!keyState.IsVirtualKeyValid(keyIndex))
                return false;

            if (state == KeyStateFlags.None)
            {
                keyState.SetRawValue(keyIndex, 0);
                return true;
            }

            var writer = getRawKeyStateRef;
            if (writer == null)
                return false;

            writer(keyIndex) = (int)state;
            return true;
        }
        catch (Exception ex)
        {
            LogRawWriterFailure(key, ex.Message);
            return false;
        }
    }

    private unsafe void TrySetDiagnosticUiInputState(SeVirtualKey key, KeyStateFlags state)
    {
        if (!IsKeyboardKey(key))
            return;

        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return;

        var inputData = uiModule->GetUIInputData();
        if (inputData == null)
            return;

        var diagnosticKeyState = inputData->KeyboardInputs.KeyState;
        var keyIndex = (int)key;
        if (keyIndex < 0 || keyIndex >= diagnosticKeyState.Length)
            return;

        diagnosticKeyState[keyIndex] = state;
        inputData->KeyboardInputsChanged = true;
    }

    private bool IsKeyboardCompatible(SeVirtualKey key)
    {
        if (!IsKeyboardKey(key))
            return false;

        try
        {
            return keyState.IsVirtualKeyValid((int)key);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsKeyboardKey(SeVirtualKey key)
    {
        if (key is SeVirtualKey.NO_KEY
            or SeVirtualKey.LBUTTON
            or SeVirtualKey.RBUTTON
            or SeVirtualKey.MBUTTON
            or SeVirtualKey.XBUTTON1
            or SeVirtualKey.XBUTTON2)
        {
            return false;
        }

        var keyIndex = (int)key;
        return keyIndex is > 0 and < MaxRawVirtualKeyCode;
    }

    private static GetRawKeyStateRefDelegate? CreateRawKeyStateRefDelegate(IKeyState keyState, IPluginLog? log)
    {
        try
        {
            var method = keyState.GetType().GetMethod(
                "GetRefValue",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(int) },
                modifiers: null);
            if (method == null)
            {
                log?.Warning("[ADS][DebugStrafe] Raw key-state writer unavailable: IKeyState.GetRefValue(int) was not found.");
                return null;
            }

            var writer = (GetRawKeyStateRefDelegate?)Delegate.CreateDelegate(
                typeof(GetRawKeyStateRefDelegate),
                keyState,
                method,
                throwOnBindFailure: false);
            if (writer == null)
                log?.Warning("[ADS][DebugStrafe] Raw key-state writer unavailable: IKeyState.GetRefValue(int) delegate binding failed.");

            return writer;
        }
        catch (Exception ex)
        {
            log?.Warning($"[ADS][DebugStrafe] Raw key-state writer unavailable: {ex.Message}");
            return null;
        }
    }

    private void LogUnavailable(DebugStrafeDirection direction, string reason)
    {
        var key = $"{direction}|{reason}";
        if (string.Equals(lastUnavailableLogKey, key, StringComparison.Ordinal))
            return;

        lastUnavailableLogKey = key;
        log?.Warning($"[ADS][DebugStrafe] {FormatDirection(direction)} unavailable: {reason}");
    }

    private void LogRawWriterFailure(SeVirtualKey key, string reason)
    {
        var logKey = $"{key}|{reason}";
        if (string.Equals(lastRawWriterLogKey, logKey, StringComparison.Ordinal))
            return;

        lastRawWriterLogKey = logKey;
        log?.Warning($"[ADS][DebugStrafe] Raw key-state write failed for {FormatKey(key)}: {reason}");
    }

    private string BuildIdleStatus()
        => BuildStatus(Enabled, DebugStrafeDirection.None, SeVirtualKey.NO_KEY);

    private static string BuildUnavailableStatus(DebugStrafeDirection direction, string reason)
        => $"{BuildStatus(enabled: true, direction, SeVirtualKey.NO_KEY)} unavailable: {reason}";

    private static string BuildStatus(bool enabled, DebugStrafeDirection direction, SeVirtualKey key)
    {
        var mode = enabled ? "on" : "off";
        var directionText = direction == DebugStrafeDirection.None ? "none" : FormatDirection(direction);
        var keyText = key == SeVirtualKey.NO_KEY ? "none" : FormatKey(key);
        return $"ADS debug {mode}. direction={directionText} key={keyText} writer={WriterMode}.";
    }

    private static InputId GetInputId(DebugStrafeDirection direction)
        => direction == DebugStrafeDirection.Left ? InputId.MOVE_STRIFE_L : InputId.MOVE_STRIFE_R;

    private static string FormatDirection(DebugStrafeDirection direction)
        => direction switch
        {
            DebugStrafeDirection.Left => "Strafe Left",
            DebugStrafeDirection.Right => "Strafe Right",
            _ => "Strafe",
        };

    private static string FormatKey(SeVirtualKey key)
        => key.ToString();
}
