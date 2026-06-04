using System.Reflection;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ADS.Services;

public enum StrafeDirection
{
    None,
    Left,
    Right,
}

public sealed class TreasureDoorStrafeInputService
{
    private const int MaxRawVirtualKeyCode = 240;
    private const string Owner = "treasure-door-nudge";
    private const string WriterMode = "raw-key-state";
    private const string RawWriterUnavailableReason = "raw key-state writer is unavailable.";

    private delegate ref int GetRawKeyStateRefDelegate(int vkCode);

    private readonly IKeyState keyState;
    private readonly IPluginLog? log;
    private readonly GetRawKeyStateRefDelegate? getRawKeyStateRef;
    private StrafeDirection heldDirection = StrafeDirection.None;
    private SeVirtualKey heldKey = SeVirtualKey.NO_KEY;
    private string lastStatus = BuildStatus(StrafeDirection.None, SeVirtualKey.NO_KEY);
    private string lastUnavailableLogKey = string.Empty;
    private string lastRawWriterLogKey = string.Empty;

    public TreasureDoorStrafeInputService(IKeyState keyState, IPluginLog? log = null)
    {
        this.keyState = keyState;
        this.log = log;
        getRawKeyStateRef = CreateRawKeyStateRefDelegate(keyState, log);
    }

    public bool Hold(StrafeDirection direction, bool includePressed, out string status)
    {
        if (direction == StrafeDirection.None)
        {
            Release("none");
            status = DescribeHeldState();
            return true;
        }

        if (!TryResolveKey(direction, out var key, out var unavailableReason))
        {
            if (heldDirection != StrafeDirection.None)
                ReleaseInternal($"{FormatDirection(direction)} keybind unavailable", updateStatus: false);

            status = BuildUnavailableStatus(direction, unavailableReason);
            lastStatus = status;
            LogUnavailable(direction, unavailableReason);
            return false;
        }

        var previousDirection = heldDirection;
        var previousKey = heldKey;
        var keyChanged = heldDirection != StrafeDirection.None && heldKey != key;
        if (keyChanged && previousKey != SeVirtualKey.NO_KEY)
            TrySetKeyState(previousKey, KeyStateFlags.None);

        var state = KeyStateFlags.Down | KeyStateFlags.Held;
        if (includePressed)
            state |= KeyStateFlags.Pressed;

        if (!TrySetKeyState(key, state))
        {
            if (heldDirection != StrafeDirection.None)
                ReleaseInternal($"{FormatDirection(direction)} input state unavailable", updateStatus: false);

            status = BuildUnavailableStatus(direction, RawWriterUnavailableReason);
            lastStatus = status;
            LogRawWriterFailure(key, RawWriterUnavailableReason);
            return false;
        }

        if (keyChanged)
        {
            log?.Information(
                $"[ADS][TreasureDoorStrafe] {FormatDirection(direction)} keybind changed while held for {Owner}; now using {FormatKey(key)}.");
        }

        var wasAlreadyHeld = previousDirection == direction && previousKey == key;
        heldDirection = direction;
        heldKey = key;
        status = BuildStatus(direction, key);
        lastStatus = status;

        if (!wasAlreadyHeld)
            log?.Information($"[ADS][TreasureDoorStrafe] Holding {FormatDirection(direction)} via {FormatKey(key)} for {Owner}.");

        return true;
    }

    public void Release(string reason)
    {
        if (heldDirection == StrafeDirection.None)
            return;

        ReleaseInternal(reason, updateStatus: true);
    }

    public string DescribeHeldState()
        => lastStatus;

    public bool IsHolding(StrafeDirection direction)
        => heldDirection == direction;

    private void ReleaseInternal(string reason, bool updateStatus)
    {
        var previousDirection = heldDirection;
        var previousKey = heldKey;
        heldDirection = StrafeDirection.None;
        heldKey = SeVirtualKey.NO_KEY;

        if (previousKey != SeVirtualKey.NO_KEY)
            TrySetKeyState(previousKey, KeyStateFlags.None);

        if (updateStatus)
            lastStatus = BuildStatus(StrafeDirection.None, SeVirtualKey.NO_KEY);

        log?.Information($"[ADS][TreasureDoorStrafe] Released {FormatDirection(previousDirection)} for {Owner} during {reason}.");
    }

    private unsafe bool TryResolveKey(StrafeDirection direction, out SeVirtualKey key, out string unavailableReason)
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
                    : $"{inputId} has no keyboard bind (unbound or gamepad-only).";
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
                types: [typeof(int)],
                modifiers: null);
            if (method == null)
            {
                log?.Warning("[ADS][TreasureDoorStrafe] Raw key-state writer unavailable: IKeyState.GetRefValue(int) was not found.");
                return null;
            }

            var writer = (GetRawKeyStateRefDelegate?)Delegate.CreateDelegate(
                typeof(GetRawKeyStateRefDelegate),
                keyState,
                method,
                throwOnBindFailure: false);
            if (writer == null)
                log?.Warning("[ADS][TreasureDoorStrafe] Raw key-state writer unavailable: IKeyState.GetRefValue(int) delegate binding failed.");

            return writer;
        }
        catch (Exception ex)
        {
            log?.Warning($"[ADS][TreasureDoorStrafe] Raw key-state writer unavailable: {ex.Message}");
            return null;
        }
    }

    private void LogUnavailable(StrafeDirection direction, string reason)
    {
        var key = $"{direction}|{reason}";
        if (string.Equals(lastUnavailableLogKey, key, StringComparison.Ordinal))
            return;

        lastUnavailableLogKey = key;
        log?.Warning($"[ADS][TreasureDoorStrafe] {FormatDirection(direction)} unavailable: {reason}");
    }

    private void LogRawWriterFailure(SeVirtualKey key, string reason)
    {
        var logKey = $"{key}|{reason}";
        if (string.Equals(lastRawWriterLogKey, logKey, StringComparison.Ordinal))
            return;

        lastRawWriterLogKey = logKey;
        log?.Warning($"[ADS][TreasureDoorStrafe] Raw key-state write failed for {FormatKey(key)}: {reason}");
    }

    private static string BuildUnavailableStatus(StrafeDirection direction, string reason)
        => $"{BuildStatus(direction, SeVirtualKey.NO_KEY)} unavailable: {reason}";

    private static string BuildStatus(StrafeDirection direction, SeVirtualKey key)
    {
        var directionText = FormatDirection(direction);
        var keyText = key == SeVirtualKey.NO_KEY ? "none" : FormatKey(key);
        return $"direction={directionText} key={keyText} writer={WriterMode}.";
    }

    private static InputId GetInputId(StrafeDirection direction)
        => direction == StrafeDirection.Left ? InputId.MOVE_STRIFE_L : InputId.MOVE_STRIFE_R;

    public static string FormatDirection(StrafeDirection direction)
        => direction switch
        {
            StrafeDirection.Left => "strafe-left",
            StrafeDirection.Right => "strafe-right",
            _ => "none",
        };

    private static string FormatKey(SeVirtualKey key)
        => key.ToString();
}
