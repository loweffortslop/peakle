using UnityEngine;
using UnityEngine.UI;

namespace Peakle;

/// <summary>
/// Floating backpack slots, placed every frame at points in the world: up to four around a backpack (two on each side,
/// leaving the middle for the backpack itself) plus an optional middle slot (a button: wear it, light it).
///
/// Each slot mocks a PEAK hotbar slot out of parts known to draw in PEAK's HUD: the hotbar slot's own rounded outline
/// sprite (with the normal UI material; its "Scouts/UI" shader and the procedural shadow don't draw outside the
/// hotbar), a translucent dark fill, PEAK's item icon, and the item name in PEAK's font. (Textures, sprites and render
/// textures made at runtime never showed in PEAK's HUD.) The slots are the first thing in the HUD, so they draw over
/// the world but under the rest of the interface. Sizes follow distance so they look attached to the backpack.
/// Hands pick a slot by looking at it (HitTest). One panel is shared by every backpack the local player deals with.
/// </summary>
internal class BackpackPanel
{
    public const int Middle = BackpackData.slots; // index of the middle slot

    /// <summary>One slot: its item (prefab and instance data), or a bare icon; null icon = empty slot.</summary>
    public struct Cell
    {
        public Item prefab;
        public ItemInstanceData data;
        public Texture icon;
        public bool faded; // a placeholder (e.g. where a held backpack would go)
        public string label; // always shown under the slot (e.g. a jetpack's fuel)
    }

    private class SlotUI
    {
        public RectTransform rt;
        public Image fill;
        public Image outline;
        public RawImage icon;
        public TMPro.TextMeshProUGUI name;
    }

    private const float SlotPx = 100f; // a slot's size in canvas units

    private static readonly Color FillColor = new(0f, 0f, 0f, 0.45f);
    private static readonly Color HoverFill = new(1f, 0.85f, 0.35f, 0.45f);
    private static readonly Color Yellow = new(1f, 0.85f, 0.35f, 1f);

    private readonly Canvas hud;
    private readonly RectTransform hudRect;
    private readonly RectTransform container;
    private readonly RectTransform names; // drawn after every slot, so a name is never under another slot
    private readonly SlotUI[] slots = new SlotUI[BackpackData.slots + 1];
    private readonly bool[] shown = new bool[BackpackData.slots + 1];

    // Where the panel is in the world this frame.
    private Vector3 center;
    private Quaternion facing = Quaternion.identity;
    private float metresPerPx;
    private float spread;

    public bool Visible => container != null && container.gameObject.activeSelf;

    /// <summary>False once PEAK's HUD has been rebuilt (e.g. a new scene): the panel is rebuilt with it.</summary>
    public bool Alive => container != null && hud != null;

    private BackpackPanel(Canvas hudCanvas, InventoryItemUI hotbarSlot, TMPro.TextMeshProUGUI fontLike)
    {
        hud = hudCanvas;
        hudRect = (RectTransform)hud.transform;
        container = NewRect("Peakle Backpack Slots", hudRect, Vector2.zero);
        names = NewRect("Names", container, Vector2.zero);
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = Build(i, hotbarSlot, fontLike);
        }
        names.SetAsLastSibling();
        container.gameObject.SetActive(false);
    }

    /// <summary>Built once PEAK's HUD exists.</summary>
    public static BackpackPanel Create()
    {
        GUIManager gui = GUIManager.instance;
        if (gui == null || gui.items == null || gui.items.Length < 2 || gui.items[1] == null)
        {
            return null;
        }
        Canvas hudCanvas = gui.items[1].GetComponentInParent<Canvas>();
        return hudCanvas != null ? new BackpackPanel(hudCanvas.rootCanvas, gui.items[1], gui.interactNameText ?? gui.interactPromptText) : null;
    }

    /// <summary>Gets rid of the panel's own objects.</summary>
    public void Destroy()
    {
        if (container != null)
        {
            Object.Destroy(container.gameObject);
        }
    }

    private SlotUI Build(int index, InventoryItemUI hotbarSlot, TMPro.TextMeshProUGUI fontLike)
    {
        RectTransform rt = NewRect($"Slot {index}", container, Vector2.one * SlotPx);
        SlotUI slot = new() { rt = rt };

        // Plain colour, no sprite: tucked inside the outline's rounded corners.
        slot.fill = NewRect("Fill", rt, Vector2.one * SlotPx * 0.86f).gameObject.AddComponent<Image>();
        slot.fill.raycastTarget = false;

        Image outlineLike = hotbarSlot.outline;
        slot.outline = NewRect("Outline", rt, Vector2.one * SlotPx).gameObject.AddComponent<Image>();
        slot.outline.raycastTarget = false;
        if (outlineLike != null && outlineLike.sprite != null)
        {
            slot.outline.sprite = outlineLike.sprite;
            slot.outline.type = outlineLike.type;
            slot.outline.pixelsPerUnitMultiplier = outlineLike.pixelsPerUnitMultiplier;
            slot.outline.fillCenter = false;
        }

        slot.icon = NewRect("Icon", rt, Vector2.one * SlotPx * 0.85f).gameObject.AddComponent<RawImage>();
        slot.icon.raycastTarget = false;

        RectTransform nameRt = NewRect($"Name {index}", names, new Vector2(SlotPx * 4f, SlotPx * 0.5f));
        slot.name = nameRt.gameObject.AddComponent<TMPro.TextMeshProUGUI>();
        if (fontLike != null)
        {
            slot.name.font = fontLike.font;
            slot.name.fontSharedMaterial = fontLike.fontSharedMaterial;
        }
        slot.name.color = Color.white;
        slot.name.fontSize = SlotPx * 0.3f;
        slot.name.alignment = TMPro.TextAlignmentOptions.Center;
        slot.name.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        slot.name.raycastTarget = false;
        return slot;
    }

    private static RectTransform NewRect(string name, Transform parent, Vector2 size)
    {
        GameObject go = new(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        RectTransform rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        return rt;
    }

    // In panel units (canvas units at real size): 0 and 1 left of the backpack (top, bottom), 2 and 3 right; Middle in the middle.
    private Vector2 SlotOffset(int i)
    {
        if (i == Middle)
        {
            return Vector2.zero;
        }
        float x = SlotPx * spread;
        float y = SlotPx * 0.62f;
        return new Vector2(i < 2 ? -x : x, i % 2 == 0 ? y : -y);
    }

    private Vector3 SlotWorld(int i)
    {
        Vector2 o = SlotOffset(i) * metresPerPx;
        return center + facing * new Vector3(o.x, o.y, 0f);
    }

    /// <summary>
    /// A backpack's item slots around <paramref name="worldCenter"/> (spread = how many slot widths out), and an
    /// optional middle slot. <paramref name="worldFacing"/>'s forward points away from the viewer.
    /// </summary>
    public void Show(Vector3 worldCenter, Quaternion worldFacing, BackpackData data, int slotCount, Cell? middle, int hovered, float spread)
    {
        Cell[] cells = new Cell[Mathf.Clamp(slotCount, 0, BackpackData.slots)];
        for (int i = 0; i < cells.Length; i++)
        {
            ItemSlot slot = data != null ? data.itemSlots[i] : null;
            if (slot != null && !slot.IsEmpty())
            {
                cells[i].prefab = slot.prefab;
                cells[i].data = slot.data;
                cells[i].icon = slot.prefab.UIData.GetIcon();
            }
        }
        Show(worldCenter, worldFacing, cells, middle, hovered, spread);
    }

    public void Show(Vector3 worldCenter, Quaternion worldFacing, Cell[] aroundCells, Cell? middle, int hovered, float spread)
    {
        Camera cam = MainCamera.instance != null ? MainCamera.instance.cam : Camera.main;
        if (cam == null || hud == null || !hud.isActiveAndEnabled)
        {
            Hide();
            return;
        }
        if (!container.gameObject.activeSelf)
        {
            container.gameObject.SetActive(true);
        }
        container.SetAsFirstSibling(); // over the world, under the rest of the interface
        this.spread = spread;
        center = worldCenter;
        facing = worldFacing;
        // Same apparent size at any distance: a slot spans a fixed angle of view.
        float distance = Mathf.Max(Vector3.Distance(cam.transform.position, center), 0.2f);
        float metres = 2f * distance * Mathf.Tan(Plugin.PanelSlotAngle.Value * 0.5f * Mathf.Deg2Rad);
        metresPerPx = metres / SlotPx;

        Camera uiCam = hud.renderMode == RenderMode.ScreenSpaceOverlay ? null : hud.worldCamera;
        // A slot spans a fixed angle of view, so its size on screen is the same wherever it is.
        float px = Screen.height * Mathf.Tan(Plugin.PanelSlotAngle.Value * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float scaleFactor = Mathf.Max(hud.scaleFactor, 0.0001f);
        int around = Mathf.Clamp(aroundCells.Length, 0, BackpackData.slots);
        for (int i = 0; i < slots.Length; i++)
        {
            bool active = i == Middle ? middle.HasValue : i < around;
            SlotUI ui = slots[i];
            Vector3 world = SlotWorld(i);
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0.05f)
            {
                active = false; // behind the camera
            }
            // Off to the side of the view: hidden (projection blows sizes up near the screen's edge plane).
            if (Vector3.Angle(cam.transform.forward, world - cam.transform.position) > 70f)
            {
                active = false;
            }
            shown[i] = active;
            if (ui.rt.gameObject.activeSelf != active)
            {
                ui.rt.gameObject.SetActive(active);
            }
            if (!active)
            {
                ui.name.enabled = false;
                continue;
            }
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(hudRect, sp, uiCam, out Vector2 local))
            {
                ui.rt.localPosition = local;
            }
            bool hover = i == hovered;
            float scale = px / scaleFactor / SlotPx * (hover ? 1.15f : 1f);
            ui.rt.localScale = Vector3.one * scale;
            ui.name.rectTransform.localPosition = local + new Vector2(0f, -SlotPx * 0.8f * scale);
            ui.name.rectTransform.localScale = Vector3.one * scale;
            Draw(ui, i == Middle ? middle.Value : aroundCells[i], hover);
        }
    }

    private static void Draw(SlotUI ui, Cell cell, bool hover)
    {
        ui.fill.color = hover ? HoverFill : FillColor;
        ui.outline.enabled = Plugin.SlotOrbs.Value;
        ui.outline.color = hover ? Yellow : Color.white;

        bool icon = cell.icon != null;
        ui.icon.enabled = icon;
        if (icon)
        {
            ui.icon.texture = cell.icon;
            ui.icon.color = cell.data != null && cell.data.TryGetDataEntry(DataEntryKey.CookedAmount, out IntItemData cooked)
                ? ItemCooking.GetCookColor(cooked.Value) : Color.white;
        }
        ui.name.enabled = cell.label != null || (hover && cell.prefab != null);
        if (ui.name.enabled)
        {
            ui.name.text = cell.label ?? cell.prefab.GetItemName(cell.data);
        }
    }

    public void Hide()
    {
        if (container != null && container.gameObject.activeSelf)
        {
            container.gameObject.SetActive(false);
        }
    }

    /// <summary>The slot the ray points at (Middle for the middle one), or -1.</summary>
    public int HitTest(Ray ray)
    {
        if (!Visible || metresPerPx <= 0f)
        {
            return -1;
        }
        Plane plane = new(facing * Vector3.forward, center);
        if (!plane.Raycast(ray, out float distance))
        {
            return -1;
        }
        Vector3 local = Quaternion.Inverse(facing) * (ray.GetPoint(distance) - center) / metresPerPx;
        for (int i = 0; i < slots.Length; i++)
        {
            if (!shown[i])
            {
                continue;
            }
            Vector2 c = SlotOffset(i);
            if (Mathf.Abs(local.x - c.x) <= SlotPx * 0.55f && Mathf.Abs(local.y - c.y) <= SlotPx * 0.55f)
            {
                return i;
            }
        }
        return -1;
    }
}
