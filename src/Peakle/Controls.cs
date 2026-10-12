using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine.InputSystem;
using Zorro.ControllerSupport;
using Zorro.Core;

namespace Peakle;

/// <summary>
/// Peakle's own controls: one input action per control, with a keyboard / mouse binding and a gamepad binding, both
/// set in the config ("Controls - Keyboard", "Controls - Gamepad"). ModConfig shows them as rebind buttons (it does
/// that for string settings whose default is an Input System path). PEAK's own controls and Controls menu are left as
/// they are; Peakle reads its buttons itself and hides PEAK's use / interact / drop input from PEAK.
///
/// Defaults: White Knuckle's keys, and White Knuckle's gamepad layout (its PlayerInput asset: Hand-Left LT,
/// Hand-Right RT, Pocket LB / RB, Drop d-pad left / right, Hand-Swap Y, Inventory d-pad down).
/// </summary>
internal static class Controls
{
    public const string LeftHand = "Left Hand";
    public const string RightHand = "Right Hand";
    public const string LeftPocket = "Left Pocket";
    public const string RightPocket = "Right Pocket";
    public const string DropLeft = "Drop Left";
    public const string DropRight = "Drop Right";
    public const string SwapHands = "Swap Hands";
    public const string Backpack = "Backpack";
    public const string HoldStill = "Hold Still";

    private static readonly (string name, string keyboard, string gamepad, string description)[] Defaults =
    {
        (LeftHand, "<Mouse>/leftButton", "<Gamepad>/leftTrigger", "Grab, use the item, or interact with the left hand."),
        (RightHand, "<Mouse>/rightButton", "<Gamepad>/rightTrigger", "Grab, use the item, or interact with the right hand."),
        (LeftPocket, "<Keyboard>/q", "<Gamepad>/leftShoulder", "Put the left hand's item in the left pocket, or take it out."),
        (RightPocket, "<Keyboard>/e", "<Gamepad>/rightShoulder", "Put the right hand's item in the right pocket, or take it out."),
        (DropLeft, "<Keyboard>/1", "<Gamepad>/dpad/left", "Drop the left hand's item (hold to throw)."),
        (DropRight, "<Keyboard>/3", "<Gamepad>/dpad/right", "Drop the right hand's item (hold to throw)."),
        (SwapHands, "<Keyboard>/2", "<Gamepad>/buttonNorth", "Swap the items in your hands."),
        (Backpack, "<Keyboard>/tab", "<Gamepad>/dpad/down", "Show your backpack; hold to carry a knocked-out scout."),
        (HoldStill, "<Keyboard>/leftCtrl", "<Gamepad>/buttonEast", "Hold while hanging to stay still (White Knuckle's crouch). PEAK's own crouch works too."),
    };

    private static readonly Dictionary<string, (ConfigEntry<string> keyboard, ConfigEntry<string> gamepad)> entries = new();
    private static readonly Dictionary<string, InputAction> actions = new();
    private static InputActionMap map;

    public static void Bind(ConfigFile config)
    {
        foreach ((string name, string keyboard, string gamepad, string description) in Defaults)
        {
            ConfigEntry<string> k = config.Bind("Controls - Keyboard", name, keyboard, description);
            ConfigEntry<string> g = config.Bind("Controls - Gamepad", name, gamepad, description);
            k.SettingChanged += (_, _) => Rebuild();
            g.SettingChanged += (_, _) => Rebuild();
            entries[name] = (k, g);
        }
    }

    private static void Rebuild()
    {
        if (map != null)
        {
            map.Disable();
            map.Dispose();
            map = null;
        }
        actions.Clear();
        PromptRefresher.RefreshAll();
    }

    private static InputAction Action(string name)
    {
        if (map == null)
        {
            map = new InputActionMap("Peakle");
            foreach (KeyValuePair<string, (ConfigEntry<string> keyboard, ConfigEntry<string> gamepad)> e in entries)
            {
                InputAction action = map.AddAction(e.Key, InputActionType.Button);
                AddBinding(action, e.Value.keyboard.Value);
                AddBinding(action, e.Value.gamepad.Value);
                actions[e.Key] = action;
            }
            map.Enable();
        }
        return actions.TryGetValue(name, out InputAction a) ? a : null;
    }

    private static void AddBinding(InputAction action, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return; // cleared in the settings: unbound
        }
        try
        {
            action.AddBinding(path.Trim());
        }
        catch (System.Exception e)
        {
            Plugin.Log.LogWarning($"Controls: can't bind {action.name} to '{path}': {e.Message}");
        }
    }

    public static bool Down(string name) => Action(name)?.WasPressedThisFrame() ?? false;
    public static bool Held(string name) => Action(name)?.IsPressed() ?? false;
    public static bool Up(string name) => Action(name)?.WasReleasedThisFrame() ?? false;

    public static bool OnGamepad => InputHandler.GetCurrentUsedInputScheme() == InputScheme.Gamepad;

    /// <summary>The control's binding for a device ("" if unbound).</summary>
    public static string Path(string name, InputScheme scheme)
    {
        if (!entries.TryGetValue(name, out (ConfigEntry<string> keyboard, ConfigEntry<string> gamepad) e))
        {
            return "";
        }
        return (scheme == InputScheme.Gamepad ? e.gamepad.Value : e.keyboard.Value)?.Trim() ?? "";
    }

    /// <summary>The prompt sprite for a control on a device.</summary>
    public static string Tag(InputSpriteData data, string name, InputScheme scheme)
    {
        if (data == null)
        {
            return "";
        }
        string path = Path(name, scheme);
        if (path.Length == 0)
        {
            return "";
        }
        string tag = scheme == InputScheme.Gamepad ? data.GetSpriteTagFromInputPathGamepad(path) : data.GetSpriteTagFromInputPathKeyboard(path);
        return tag ?? "";
    }

    public static string Tag(string name) => Tag(SingletonAsset<InputSpriteData>.Instance, name, InputHandler.GetCurrentUsedInputScheme());

    /// <summary>The sprite set PEAK's prompts use on this device (InLineInputPrompts.UpdateSprites).</summary>
    public static TMPro.TMP_SpriteAsset SpriteAsset()
    {
        InputSpriteData data = SingletonAsset<InputSpriteData>.Instance;
        if (data == null)
        {
            return null;
        }
        ControllerIconSetting setting = GameHandler.Instance != null ? GameHandler.Instance.SettingsHandler.GetSetting<ControllerIconSetting>() : null;
        if (!OnGamepad || (setting != null && setting.Value == ControllerIconSetting.IconMode.KBM))
        {
            return data.keyboardSprites;
        }
        if (setting != null && setting.Value == ControllerIconSetting.IconMode.Style2)
        {
            return data.ps5Sprites;
        }
        if (setting != null && setting.Value == ControllerIconSetting.IconMode.Style1)
        {
            return data.xboxSprites;
        }
        return InputHandler.GetGamepadType() switch
        {
            GamepadType.Dualshock or GamepadType.Dualsense => data.ps5Sprites,
            GamepadType.Switch => data.switchSprites,
            _ => data.xboxSprites,
        };
    }
}
