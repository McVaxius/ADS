using System.Reflection;
using ADS.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ADS.Services;

public enum RelativeMovementDirection
{
    None,
    Forward,
    Right,
    Back,
    Left,
}

public static class CardinalDirectionMapper
{
    public static RelativeMovementDirection Resolve(CardinalDirection direction, float playerRotation)
    {
        var desiredRotation = direction switch
        {
            CardinalDirection.North => MathF.PI,
            CardinalDirection.East => MathF.PI / 2f,
            CardinalDirection.South => 0f,
            CardinalDirection.West => -MathF.PI / 2f,
            _ => 0f,
        };
        var delta = Normalize(desiredRotation - playerRotation);
        if (delta >= -MathF.PI / 4f && delta < MathF.PI / 4f)
            return RelativeMovementDirection.Forward;
        if (delta >= MathF.PI / 4f && delta < 3f * MathF.PI / 4f)
            return RelativeMovementDirection.Right;
        if (delta <= -MathF.PI / 4f && delta > -3f * MathF.PI / 4f)
            return RelativeMovementDirection.Left;
        return RelativeMovementDirection.Back;
    }

    private static float Normalize(float angle)
    {
        while (angle > MathF.PI)
            angle -= MathF.Tau;
        while (angle <= -MathF.PI)
            angle += MathF.Tau;
        return angle;
    }
}

public sealed class CardinalHoldInputService
{
    private const int MaxRawVirtualKeyCode = 240;
    private delegate ref int GetRawKeyStateRefDelegate(int vkCode);

    private readonly IKeyState keyState;
    private readonly IPluginLog? log;
    private readonly GetRawKeyStateRefDelegate? getRawKeyStateRef;
    private RelativeMovementDirection heldDirection;
    private SeVirtualKey heldKey = SeVirtualKey.NO_KEY;

    public CardinalHoldInputService(IKeyState keyState, IPluginLog? log = null)
    {
        this.keyState = keyState;
        this.log = log;
        getRawKeyStateRef = CreateRawKeyStateRefDelegate(keyState);
    }

    public bool Hold(CardinalDirection direction, float playerRotation, out string status)
    {
        try
        {
            var relativeDirection = CardinalDirectionMapper.Resolve(direction, playerRotation);
            if (!TryResolveKey(relativeDirection, out var key, out var reason))
            {
                Release($"input unavailable: {reason}");
                status = reason;
                return false;
            }

            if (heldKey != SeVirtualKey.NO_KEY && heldKey != key)
                TrySetKeyState(heldKey, KeyStateFlags.None);

            var includePressed = heldDirection != relativeDirection || heldKey != key;
            var state = KeyStateFlags.Down | KeyStateFlags.Held;
            if (includePressed)
                state |= KeyStateFlags.Pressed;
            if (!TrySetKeyState(key, state))
            {
                Release("raw key-state write failed");
                status = $"raw key-state write failed for {key}";
                return false;
            }

            heldDirection = relativeDirection;
            heldKey = key;
            status = $"{direction} via {relativeDirection}/{key}";
            return true;
        }
        catch (Exception ex)
        {
            Release($"input exception: {ex.Message}");
            status = $"input exception: {ex.Message}";
            return false;
        }
    }

    public void Release(string reason)
    {
        var previousDirection = heldDirection;
        var previousKey = heldKey;
        heldDirection = RelativeMovementDirection.None;
        heldKey = SeVirtualKey.NO_KEY;
        if (previousKey != SeVirtualKey.NO_KEY)
            TrySetKeyState(previousKey, KeyStateFlags.None);
        if (previousDirection != RelativeMovementDirection.None)
            log?.Information($"[ADS][CardinalHold] Released {previousDirection} during {reason}.");
    }

    private unsafe bool TryResolveKey(RelativeMovementDirection direction, out SeVirtualKey key, out string reason)
    {
        key = SeVirtualKey.NO_KEY;
        reason = string.Empty;
        var uiModule = UIModule.Instance();
        var inputData = uiModule == null ? null : uiModule->GetUIInputData();
        if (inputData == null)
        {
            reason = "UI input data unavailable";
            return false;
        }

        var keybind = inputData->GetKeybind(GetInputId(direction));
        if (keybind == null)
        {
            reason = $"{direction} keybind unavailable";
            return false;
        }

        foreach (var setting in keybind->KeySettings)
        {
            if (setting.Key != SeVirtualKey.NO_KEY
                && setting.KeyModifier == KeyModifierFlag.None
                && IsKeyboardCompatible(setting.Key))
            {
                key = setting.Key;
                return true;
            }
        }

        reason = $"{direction} has no unmodified keyboard bind";
        return false;
    }

    private bool TrySetKeyState(SeVirtualKey key, KeyStateFlags state)
    {
        if (!IsKeyboardCompatible(key))
            return false;
        try
        {
            var keyIndex = (int)key;
            if (state == KeyStateFlags.None)
            {
                keyState.SetRawValue(keyIndex, 0);
                return true;
            }

            if (getRawKeyStateRef == null)
                return false;
            getRawKeyStateRef(keyIndex) = (int)state;
            return true;
        }
        catch (Exception ex)
        {
            log?.Warning($"[ADS][CardinalHold] Raw key-state write failed for {key}: {ex.Message}");
            return false;
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

    private static InputId GetInputId(RelativeMovementDirection direction)
        => direction switch
        {
            RelativeMovementDirection.Forward => InputId.MOVE_FORE,
            RelativeMovementDirection.Right => InputId.MOVE_STRIFE_R,
            RelativeMovementDirection.Back => InputId.MOVE_BACK,
            RelativeMovementDirection.Left => InputId.MOVE_STRIFE_L,
            _ => InputId.MOVE_FORE,
        };

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
