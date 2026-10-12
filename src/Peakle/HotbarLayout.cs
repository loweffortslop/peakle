using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Zorro.ControllerSupport;

namespace Peakle;

/// <summary>
/// Hotbar overhaul, step 1: the two hand slots (slot 0 = left hand, PEAK's temporary slot = right hand) move to
/// the bottom middle of the screen to show what's held, and the right hand slot is always shown, even empty.
/// The pocket slots stay exactly where PEAK has them: an invisible placeholder takes the left slot's place in
/// PEAK's hotbar row. The moved slots stay inside PEAK's HUD (same parent as the hotbar row), so they hide and
/// fade with it.
/// </summary>
internal static class HotbarLayout
{
    private static GUIManager laidOut;
    private static RectTransform group;
    private static Canvas canvas;

    public static void Apply()
    {
        GUIManager gui = GUIManager.instance;
        if (gui == null || gui.items == null || gui.items.Length == 0 || gui.items[0] == null || gui.temporaryItem == null)
        {
            return;
        }
        if (laidOut != gui || group == null)
        {
            Build(gui);
        }
        Place();
    }

    private static void Build(GUIManager gui)
    {
        laidOut = gui;
        RectTransform left = gui.items[0].rectTransform;
        RectTransform right = gui.temporaryItem.rectTransform;
        RectTransform row = (RectTransform)left.parent;
        canvas = left.GetComponentInParent<Canvas>();

        // Keep the pockets where they are: something the size of the left slot stays in its place in the row.
        GameObject placeholder = new("Peakle Hand Slot Placeholder", typeof(RectTransform), typeof(LayoutElement));
        RectTransform ph = (RectTransform)placeholder.transform;
        ph.SetParent(row, false);
        ph.SetSiblingIndex(left.GetSiblingIndex());
        ph.anchorMin = left.anchorMin;
        ph.anchorMax = left.anchorMax;
        ph.pivot = left.pivot;
        ph.anchoredPosition = left.anchoredPosition;
        ph.sizeDelta = left.sizeDelta;
        LayoutElement le = placeholder.GetComponent<LayoutElement>();
        LayoutElement src = left.GetComponent<LayoutElement>();
        le.preferredWidth = src != null && src.preferredWidth > 0f ? src.preferredWidth : left.rect.width;
        le.preferredHeight = src != null && src.preferredHeight > 0f ? src.preferredHeight : left.rect.height;
        le.minWidth = src != null ? src.minWidth : -1f;
        le.minHeight = src != null ? src.minHeight : -1f;
        le.flexibleWidth = src != null ? src.flexibleWidth : -1f;

        group = new GameObject("Peakle Hand Slots", typeof(RectTransform)).GetComponent<RectTransform>();
        group.SetParent(row.parent, false);
        group.localScale = row.localScale;
        group.sizeDelta = Vector2.zero;

        // The left hand must stay sibling 0: PEAK highlights a slot by comparing its sibling index to the slot number.
        Attach(left, 0);
        Attach(right, 1);
        right.gameObject.SetActive(true);
        LabelRightHand(gui.items[0], gui.temporaryItem);
    }

    /// <summary>
    /// The right hand slot gets the same key label as the left one, showing the right hand's button, in place of PEAK's hand graphic
    /// (anything the temporary slot has that a normal slot doesn't).
    /// </summary>
    private static void LabelRightHand(InventoryItemUI left, InventoryItemUI right)
    {
        System.Collections.Generic.HashSet<string> normal = new();
        foreach (Transform t in left.GetComponentsInChildren<Transform>(true))
        {
            normal.Add(t.name);
        }
        foreach (Graphic g in right.GetComponentsInChildren<Graphic>(true))
        {
            bool partOfSlot = g == right.icon || g == right.fill || g == right.outline || g == right.selectedSlotIcon || g == right.nameText
                || (right.fuelBar != null && g.transform.IsChildOf(right.fuelBar.transform));
            if (!partOfSlot && !normal.Contains(g.name) && g.transform != right.transform)
            {
                g.gameObject.SetActive(false);
            }
        }

        TMPro.TMP_Text label = null;
        InputIcon icon = left.GetComponentInChildren<InputIcon>(true);
        if (icon != null)
        {
            label = icon.GetComponent<TMPro.TMP_Text>();
        }
        if (label == null)
        {
            foreach (TMPro.TMP_Text t in left.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                if (t.text.Contains("<sprite") || t.text.Trim() == "1")
                {
                    label = t;
                    break;
                }
            }
        }
        if (label == null)
        {
            Plugin.Log.LogWarning("Hotbar: couldn't find the left hand slot's key label to copy for the right hand");
            return;
        }
        Transform parent = FindCounterpart(left.transform, label.transform.parent, right.transform) ?? right.transform;
        GameObject copy = Object.Instantiate(label.gameObject, parent, false);
        copy.name = "Peakle RMB Label";
        copy.SetActive(true);
        foreach (InLineInputPrompts i in copy.GetComponents<InLineInputPrompts>())
        {
            Object.DestroyImmediate(i);
        }
        InputIcon copyIcon = copy.GetComponent<InputIcon>();
        if (copyIcon != null)
        {
            // PEAK keeps the label's sprites and text right for the device; Peakle's prompt patch answers "right hand".
            copyIcon.action = InputSpriteData_GetSpriteTag.RightHandSlot;
            copyIcon.SetText(InputHandler.GetCurrentUsedInputScheme());
        }
        else
        {
            copy.GetComponent<TMPro.TMP_Text>().text = Controls.Tag(Controls.RightHand);
        }
    }

    // The transform in 'other' at the same path below its root as 'node' is below 'root'.
    private static Transform FindCounterpart(Transform root, Transform node, Transform other)
    {
        if (node == root)
        {
            return other;
        }
        System.Collections.Generic.List<string> path = new();
        for (Transform t = node; t != null && t != root; t = t.parent)
        {
            path.Insert(0, t.name);
        }
        Transform found = other.Find(string.Join("/", path));
        return found;
    }

    private static void Attach(RectTransform slot, int index)
    {
        slot.SetParent(group, false);
        slot.SetSiblingIndex(index);
        slot.anchorMin = slot.anchorMax = new Vector2(0.5f, 0.5f);
        slot.pivot = new Vector2(0.5f, 0.5f);
        float spacing = Mathf.Max(slot.rect.width, 60f) * 0.5f + Plugin.HandSlotGap.Value * 0.5f;
        slot.anchoredPosition = new Vector2(index == 0 ? -spacing : spacing, 0f);
    }

    // Bottom middle of the screen, every frame (resolution changes, HUD scaling).
    private static void Place()
    {
        RectTransform parent = (RectTransform)group.parent;
        Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Vector2 screen = new(Screen.width * 0.5f, Screen.height * Plugin.HandSlotHeight.Value);
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen, cam, out Vector2 local))
        {
            group.localPosition = local;
        }
    }
}

// The right hand slot (PEAK's temporary slot) is shown even when empty, like every other slot.
[HarmonyPatch(typeof(GUIManager), nameof(GUIManager.UpdateItems))]
internal static class GUIManager_UpdateItems
{
    private static void Postfix(GUIManager __instance)
    {
        Character observed = Character.observedCharacter;
        if (observed == null || observed.player == null || HandsOf.Get(observed) == null || __instance.temporaryItem == null)
        {
            return;
        }
        if (!__instance.temporaryItem.gameObject.activeSelf)
        {
            __instance.temporaryItem.gameObject.SetActive(true);
        }
        __instance.temporaryItem.SetItem(observed.player.GetItemSlot(TemporaryItemSlot.SLOT_INDEX));
    }
}

// Both hand slots look "selected" exactly while their hand holds something. PEAK's slot UI forces that look
// for its temporary slot via isTemporarySlot, so the flag is set per hand right before each refresh.
[HarmonyPatch(typeof(InventoryItemUI), nameof(InventoryItemUI.SetSelected))]
internal static class InventoryItemUI_SetSelected
{
    private static void Prefix(InventoryItemUI __instance)
    {
        GUIManager gui = GUIManager.instance;
        Character observed = Character.observedCharacter;
        if (gui == null || observed == null || observed.player == null || HandsOf.Get(observed) == null)
        {
            return;
        }
        byte? slot = __instance == gui.temporaryItem ? TemporaryItemSlot.SLOT_INDEX
            : gui.items != null && gui.items.Length > 0 && __instance == gui.items[0] ? (byte)0 : null;
        if (slot.HasValue)
        {
            ItemSlot itemSlot = observed.player.GetItemSlot(slot.Value);
            __instance.isTemporarySlot = itemSlot != null && !itemSlot.IsEmpty();
        }
    }
}
