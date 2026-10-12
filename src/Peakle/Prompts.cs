using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Zorro.ControllerSupport;

namespace Peakle;

/// <summary>
/// PEAK draws every button prompt (hotbar slot labels, "use", "drop", "pick up"...) from
/// InputSpriteData.GetSpriteTag. These are swapped for Peakle's controls: each prompt shows the current binding of
/// the PEAK action that does the job in Peakle (see Controls), on keyboard and gamepad, rebinds included.
/// </summary>
[HarmonyPatch(typeof(InputSpriteData), nameof(InputSpriteData.GetSpriteTag))]
internal static class InputSpriteData_GetSpriteTag
{
    // Not PEAK's: Peakle's hotbar slot labels (each slot's label shows its control, on both devices).
    internal const InputSpriteData.InputAction RightHandSlot = (InputSpriteData.InputAction)100;
    internal const InputSpriteData.InputAction LeftHandSlot = (InputSpriteData.InputAction)101;
    internal const InputSpriteData.InputAction LeftPocketSlot = (InputSpriteData.InputAction)102;
    internal const InputSpriteData.InputAction RightPocketSlot = (InputSpriteData.InputAction)103;
    internal const InputSpriteData.InputAction BackpackSlot = (InputSpriteData.InputAction)104;

    private static readonly Dictionary<InputSpriteData.InputAction, string> SlotControls = new()
    {
        { RightHandSlot, Controls.RightHand },
        { LeftHandSlot, Controls.LeftHand },
        { LeftPocketSlot, Controls.LeftPocket },
        { RightPocketSlot, Controls.RightPocket },
        { BackpackSlot, Controls.Backpack },
    };

    private static bool Prefix(InputSpriteData __instance, InputSpriteData.InputAction action, InputScheme scheme, ref string __result)
    {
        if (scheme != InputScheme.KeyboardMouse && scheme != InputScheme.Gamepad)
        {
            return true;
        }
        Character local = Character.localCharacter;
        PeakleHands hands = local != null ? HandsOf.Get(local) : null;
        // Never "": PEAK keeps a label's old text when given none, drawn with the other device's sprites.
        string T(string name)
        {
            string tag = Controls.Tag(__instance, name, scheme);
            return tag.Length > 0 ? tag : " ";
        }
        if (SlotControls.TryGetValue(action, out string slotControl))
        {
            // Never empty: PEAK keeps a label's old text when given none, drawn with the other device's sprites.
            __result = T(slotControl);
            if (__result.Length == 0)
            {
                __result = " ";
            }
            return false;
        }
        if (hands == null)
        {
            return true;
        }
        bool rightActive = hands.ActiveHand?.id == PeakleHands.Right;
        switch (action)
        {
            case InputSpriteData.InputAction.Interact:
            case InputSpriteData.InputAction.HoldInteract:
            {
                if (local.data.fullyPassedOut)
                {
                    return true; // knocked out, PEAK's own interact key is "give up"
                }
                // Each free hand can grab it; side by side, like White Knuckle's per-hand icons.
                IInteractible target = Interaction.instance != null ? Interaction.instance.bestInteractable : null;
                bool left = PeakleHands.CanInteractWith(hands.hands[PeakleHands.Left], target);
                bool right = PeakleHands.CanInteractWith(hands.hands[PeakleHands.Right], target);
                __result = "<nobr>" + (left ? T(Controls.LeftHand) : "") + (left && right ? "\\" : "") + (right ? T(Controls.RightHand) : "") + "</nobr>";
                return false;
            }
            case InputSpriteData.InputAction.UsePrimary:
                __result = T(rightActive ? Controls.RightHand : Controls.LeftHand);
                return false;
            case InputSpriteData.InputAction.Drop:
            case InputSpriteData.InputAction.Throw:
                __result = T(rightActive ? Controls.DropRight : Controls.DropLeft);
                return false;
            case InputSpriteData.InputAction.Slot1:
                __result = T(Controls.LeftHand);
                return false;
            case InputSpriteData.InputAction.Slot2:
                __result = T(Controls.LeftPocket);
                return false;
            case InputSpriteData.InputAction.Slot3:
                __result = T(Controls.RightPocket);
                return false;
            case InputSpriteData.InputAction.Slot4:
            case InputSpriteData.InputAction.DeselectSlot:
                __result = T(Controls.Backpack);
                return false;
            case InputSpriteData.InputAction.SlotLeft:
            case InputSpriteData.InputAction.SlotRight:
                __result = " "; // no slot cycling in Peakle (not "": PEAK would keep the label's default sprite)
                return false;
            case InputSpriteData.InputAction.UseSecondary:
            {
                // Feeding / using an item on a teammate: the hand that holds it.
                Item feed = hands.FeedItem(out PeakleHands.Hand feedHand);
                bool feeding = feed != null && Interaction.instance != null && Interaction.instance.hasValidTargetCharacter;
                __result = feeding ? T(feedHand.id == PeakleHands.Right ? Controls.RightHand : Controls.LeftHand) : " ";
                return false;
            }
            default:
                return true;
        }
    }
}

/// <summary>Prompts that depend on which hand is in use are redrawn when it changes.</summary>
internal static class PromptRefresher
{
    private static readonly HashSet<InputSpriteData.InputAction> HandDependent = new()
    {
        InputSpriteData.InputAction.UsePrimary,
        InputSpriteData.InputAction.Drop,
        InputSpriteData.InputAction.Throw,
        InputSpriteData.InputAction.UseSecondary,
    };

    internal static void Init()
    {
        PeakleHands.LocalActiveHandChanged += Refresh;
    }

    private static bool schemeHooked;

    // Switching between keyboard and gamepad, and rebinding (PEAK announces both the same way), redraws Peakle's labels.
    private static void HookSchemeChanges()
    {
        InputHandler handler = Zorro.Core.RetrievableResourceSingleton<InputHandler>.Instance;
        if (schemeHooked || handler == null)
        {
            return;
        }
        schemeHooked = true;
        handler.InputSchemeChanged += _ => RefreshAll();
    }

    /// <summary>
    /// Prompts work out their key sprite when they're enabled, which for the HUD is before the local scout exists.
    /// Redraw them all once the local hands are up, and relabel the hotbar's number labels too.
    /// </summary>
    internal static void RefreshAll()
    {
        HookSchemeChanges();
        InputScheme scheme = InputHandler.GetCurrentUsedInputScheme();
        foreach (InputIcon icon in Object.FindObjectsByType<InputIcon>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (icon.text != null)
            {
                if (InteractActions.Contains(icon.action))
                {
                    FitHandIcons(icon);
                }
                icon.SetText(scheme);
            }
        }
        foreach (InLineInputPrompts prompt in Object.FindObjectsByType<InLineInputPrompts>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (prompt.text != null && prompt.originalText != null)
            {
                prompt.OnDeviceChange(scheme);
            }
        }
        RelabelHotbar();
    }

    // Hotbar labels that are plain text rather than InputIcons, with the control each one shows.
    private static readonly List<(TMPro.TMP_Text label, string control)> plainLabels = new();
    private static readonly string[] SlotControls = { Controls.LeftHand, Controls.LeftPocket, Controls.RightPocket };

    /// <summary>
    /// Each hotbar slot shows its Peakle control on keyboard and gamepad: its first key label is switched to that
    /// control, and any other labels it has (PEAK's gamepad-only ones) are switched off.
    /// </summary>
    private static void RelabelHotbar()
    {
        GUIManager gui = GUIManager.instance;
        if (gui == null || gui.items == null)
        {
            return;
        }
        InputScheme scheme = InputHandler.GetCurrentUsedInputScheme();
        if (gui.items.Length > 2)
        {
            TakeOver(gui.items[0], InputSpriteData_GetSpriteTag.LeftHandSlot, null, scheme);
            TakeOver(gui.items[1], InputSpriteData_GetSpriteTag.LeftPocketSlot, null, scheme);
            TakeOver(gui.items[2], InputSpriteData_GetSpriteTag.RightPocketSlot, null, scheme);
        }
        TakeOver(gui.temporaryItem, InputSpriteData_GetSpriteTag.RightHandSlot, "Peakle RMB Label", scheme);
        TakeOver(gui.backpack, InputSpriteData_GetSpriteTag.BackpackSlot, null, scheme);

        plainLabels.RemoveAll(l => l.label == null);
        if (gui.backpack != null)
        {
            foreach (TMPro.TMP_Text label in gui.backpack.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                if (label.text.Trim() == "4" && label.GetComponent<InputIcon>() == null)
                {
                    plainLabels.Add((label, Controls.Backpack));
                }
            }
        }
        for (int i = 0; i < gui.items.Length && i < SlotControls.Length; i++)
        {
            if (gui.items[i] == null)
            {
                continue;
            }
            foreach (TMPro.TMP_Text label in gui.items[i].GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                if (label.text.Trim() == (i + 1).ToString() && label.GetComponent<InputIcon>() == null)
                {
                    plainLabels.Add((label, SlotControls[i]));
                }
            }
        }
        TMPro.TMP_SpriteAsset sprites = Controls.SpriteAsset();
        foreach ((TMPro.TMP_Text label, string control) in plainLabels)
        {
            if (sprites != null)
            {
                label.spriteAsset = sprites;
            }
            label.text = Controls.Tag(control);
        }
    }

    private static void TakeOver(Component slot, InputSpriteData.InputAction action, string preferredName, InputScheme scheme)
    {
        if (slot == null)
        {
            return;
        }
        InputIcon[] icons = slot.GetComponentsInChildren<InputIcon>(true);
        InputIcon main = null;
        foreach (InputIcon icon in icons)
        {
            if (preferredName != null && icon.name == preferredName)
            {
                main = icon;
            }
        }
        if (main == null && icons.Length > 0)
        {
            main = icons[0];
        }
        foreach (InputIcon icon in icons)
        {
            if (icon == main)
            {
                icon.action = action;
                icon.disableIfController = false;
                icon.disableIfKeyboard = false;
            }
            else
            {
                icon.disableIfController = true; // PEAK's other labels (e.g. its gamepad slot hints)
                icon.disableIfKeyboard = true;
            }
            if (icon.text != null && icon.isActiveAndEnabled)
            {
                icon.OnDeviceChange(scheme);
            }
        }
    }

    private static readonly HashSet<InputSpriteData.InputAction> InteractActions = new()
    {
        InputSpriteData.InputAction.Interact,
        InputSpriteData.InputAction.HoldInteract,
    };

    /// <summary>Redraw "grab" prompts when which hands are free changes.</summary>
    internal static void RefreshInteract()
    {
        InputScheme scheme = InputHandler.GetCurrentUsedInputScheme();
        foreach (InputIcon icon in Object.FindObjectsByType<InputIcon>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (InteractActions.Contains(icon.action) && icon.text != null)
            {
                FitHandIcons(icon);
                icon.SetText(scheme);
            }
        }
        foreach (InLineInputPrompts prompt in Object.FindObjectsByType<InLineInputPrompts>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (prompt.text != null && prompt.originalText != null && prompt.originalText.ToUpperInvariant().Contains("INTERACT"))
            {
                prompt.OnDeviceChange(scheme);
            }
        }
    }

    // Two hand icons side by side in a box made for one: keep them on one line and let the extra icon grow
    // away from the prompt's text (to the left) instead of over it.
    private static void FitHandIcons(InputIcon icon)
    {
        icon.text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        icon.text.overflowMode = TMPro.TextOverflowModes.Overflow;
        icon.text.horizontalAlignment = TMPro.HorizontalAlignmentOptions.Right;
    }

    private static void Refresh()
    {
        InputScheme scheme = InputHandler.GetCurrentUsedInputScheme();
        foreach (InputIcon icon in Object.FindObjectsByType<InputIcon>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (HandDependent.Contains(icon.action) && icon.text != null)
            {
                icon.SetText(scheme);
            }
        }
    }
}
