using UnityEngine;
using UnityEngine.UI;

namespace Peakle;

/// <summary>
/// Grip readouts, built from copies of PEAK's own crosshair progress ring (UI_UseItemProgress), so they live in
/// PEAK's HUD and hide under menus and fades like it:
/// - a ring on each hand, following the hand on screen (White Knuckle shows grip on the hands themselves);
/// - a small split ring around the crosshair, Half-Life 2 style: the left half is the left hand's grip and the
///   right half the right hand's, each filling from the bottom up.
/// Fills show grip in the stamina bar's colour; past the grip a hand can have, each affliction takes its share of the
/// ring in its colour from PEAK's bar, in the bar's order (segments are copies of the background, turned to start
/// where the last one ends); low grip turns red.
/// </summary>
internal static class HandHud
{
    private class Ring
    {
        public RectTransform rt;
        public Image fill;
        public Image empty;
        public Color fillColor;
        public Color emptyColor;
        public float shown;
        public bool clockwise;
        public Image[] segments; // one per affliction on PEAK's bar, same order
    }

    // PEAK's stamina bar: its afflictions (in bar order) and their colours, and the stamina colour.
    private static BarAffliction[] barAfflictions;
    private static Color[] barColors;
    private static Color? staminaColor;

    private static UI_UseItemProgress source;
    private static RectTransform parent;
    private static Camera canvasCamera;
    private static Ring handLeft, handRight, halfLeft, halfRight, dotLeft, dotRight, carryRing;
    private static TMPro.TextMeshProUGUI carryLabel;
    private static bool hooked;
    private static PeakleHands owner;

    private static readonly Color LowColor = new(0.9f, 0.25f, 0.2f);

    /// <summary>Called by the local hands each frame; the drawing itself happens just before the UI renders.</summary>
    public static void Update(Character character, PeakleHands hands)
    {
        owner = hands;
        if (!hooked)
        {
            hooked = true;
            // After every LateUpdate (camera included), so hand rings sit exactly on this frame's hands.
            Canvas.willRenderCanvases += Draw;
        }
    }

    private static void Draw()
    {
        if (owner == null || owner.character == null || !owner.character.IsLocal)
        {
            return;
        }
        HotbarLayout.Apply();
        owner.RenderBackpacks();
        if (source == null || handLeft == null || handLeft.rt == null)
        {
            if (!Build())
            {
                return;
            }
        }
        if (barAfflictions == null && ReadBar())
        {
            AddSegments(handLeft);
            AddSegments(handRight);
            AddSegments(halfLeft);
            AddSegments(halfRight);
        }
        Character character = owner.character;
        float max = Mathf.Max(character.GetMaxStamina(), 0.0001f);
        PeakleHands.Hand l = owner.hands[PeakleHands.Left];
        PeakleHands.Hand r = owner.hands[PeakleHands.Right];
        bool on = Plugin.ShowDebugHud.Value;

        // Sizes follow the config live (config menus change them in game).
        handLeft.rt.localScale = handRight.rt.localScale = Vector3.one * Plugin.RingScale.Value;
        halfLeft.rt.localScale = halfRight.rt.localScale = Vector3.one * Plugin.CrosshairRingScale.Value;
        dotLeft.rt.localScale = dotRight.rt.localScale = Vector3.one * Plugin.GrabDotScale.Value;
        carryRing.rt.localScale = Vector3.one * Plugin.CrosshairRingScale.Value * 1.4f;
        DrawRing(handLeft, l, max, 1f, on && Showing(l, max), HandScreenPoint(character, l));
        DrawRing(handRight, r, max, 1f, on && Showing(r, max), HandScreenPoint(character, r));
        bool center = on && (Showing(l, max) || Showing(r, max));
        Vector2 crosshair = new(Screen.width * 0.5f, Screen.height * 0.5f);
        DrawRing(halfLeft, l, max, 0.5f, center, crosshair);
        DrawRing(halfRight, r, max, 0.5f, center, crosshair);
        DrawCarryPrompt(crosshair);
        // A dot beside the crosshair ring for each hand that is actually holding on to something.
        float dotOffset = Plugin.GrabDotOffset.Value;
        DrawDot(dotLeft, on && l.grabbing, crosshair + new Vector2(-dotOffset, 0f));
        DrawDot(dotRight, on && r.grabbing, crosshair + new Vector2(dotOffset, 0f));
    }

    // "Hold [Tab] to carry" under the crosshair, with a ring filling while Tab is held.
    private static void DrawCarryPrompt(Vector2 crosshair)
    {
        string prompt = owner.carryPrompt;
        bool active = prompt != null;
        if (carryLabel != null)
        {
            carryLabel.enabled = active;
            if (active)
            {
                TMPro.TMP_SpriteAsset sprites = Controls.SpriteAsset();
                if (sprites != null && carryLabel.spriteAsset != sprites)
                {
                    carryLabel.spriteAsset = sprites; // keyboard or this gamepad's icons
                }
                carryLabel.text = "Hold " + Controls.Tag(Controls.Backpack) + " to " + prompt;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, crosshair - new Vector2(0f, Screen.height * 0.07f), canvasCamera, out Vector2 local))
                {
                    carryLabel.rectTransform.localPosition = local;
                }
            }
        }
        bool filling = active && owner.carryProgress > 0f;
        carryRing.shown = filling ? 1f : 0f;
        carryRing.fill.enabled = filling;
        carryRing.empty.enabled = filling;
        if (filling)
        {
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, crosshair, canvasCamera, out Vector2 local))
            {
                carryRing.rt.localPosition = local;
            }
            carryRing.fill.fillAmount = owner.carryProgress;
            carryRing.empty.fillAmount = 1f;
            carryRing.fill.color = carryRing.fillColor;
            carryRing.empty.color = carryRing.emptyColor;
        }
    }

    private static TMPro.TextMeshProUGUI BuildLabel()
    {
        GameObject go = new("Peakle Carry Prompt", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
        RectTransform rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.sizeDelta = new Vector2(600f, 60f);
        TMPro.TextMeshProUGUI label = go.GetComponent<TMPro.TextMeshProUGUI>();
        TMPro.TextMeshProUGUI like = GUIManager.instance != null ? GUIManager.instance.interactPromptText : null;
        if (like != null)
        {
            label.font = like.font;
            label.fontSize = like.fontSize;
            label.color = like.color;
        }
        InputIcon anyIcon = Object.FindFirstObjectByType<InputIcon>(FindObjectsInactive.Include);
        if (anyIcon != null && anyIcon.keyboardSprites != null)
        {
            label.spriteAsset = anyIcon.keyboardSprites;
        }
        label.alignment = TMPro.TextAlignmentOptions.Center;
        label.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        label.raycastTarget = false;
        label.enabled = false;
        return label;
    }

    private static void DrawDot(Ring dot, bool active, Vector2 screen)
    {
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen, canvasCamera, out Vector2 local))
        {
            dot.rt.localPosition = local;
        }
        dot.shown = Mathf.MoveTowards(dot.shown, active ? 1f : 0f, Time.unscaledDeltaTime * 8f);
        bool visible = dot.shown > 0.001f;
        dot.fill.enabled = visible;
        dot.empty.enabled = false;
        if (visible)
        {
            dot.fill.fillAmount = 1f;
            Color c = dot.fillColor;
            c.a *= dot.shown;
            dot.fill.color = c;
        }
    }

    private static bool Showing(PeakleHands.Hand hand, float max)
    {
        return hand.grabbing || hand.grip / Wk.GripStrengthTimer < max - 0.005f;
    }

    private static void DrawRing(Ring ring, PeakleHands.Hand hand, float max, float portion, bool active, Vector2? screen)
    {
        if (screen.HasValue)
        {
            if (float.IsNaN(screen.Value.x))
            {
                active = false; // hand is behind the camera
            }
            else if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen.Value, canvasCamera, out Vector2 local))
            {
                ring.rt.localPosition = local;
            }
        }
        ring.shown = Mathf.MoveTowards(ring.shown, active ? 1f : 0f, Time.unscaledDeltaTime * 4f);
        bool visible = ring.shown > 0.001f;
        ring.fill.enabled = visible;
        ring.empty.enabled = visible;
        if (!visible)
        {
            if (ring.segments != null)
            {
                foreach (Image seg in ring.segments)
                {
                    seg.enabled = false;
                }
            }
            return;
        }
        float grip = Mathf.Clamp01(hand.grip / Wk.GripStrengthTimer);
        ring.fill.fillAmount = grip * portion;
        ring.empty.fillAmount = max * portion;
        DrawSegments(ring, max, portion);
        float low = grip / max < 0.3f ? 1f - grip / max / 0.3f : 0f;
        Color c = Color.Lerp(ring.fillColor, LowColor, low);
        c.a *= ring.shown;
        ring.fill.color = c;
        Color e = ring.emptyColor;
        e.a *= ring.shown;
        ring.empty.color = e;
    }

    private static void DrawSegments(Ring ring, float max, float portion)
    {
        if (ring.segments == null)
        {
            return;
        }
        CharacterAfflictions afflictions = owner.character.refs.afflictions;
        float at = Mathf.Clamp01(max);
        for (int i = 0; i < ring.segments.Length; i++)
        {
            Image seg = ring.segments[i];
            float amount = Mathf.Min(Mathf.Max(afflictions.GetCurrentStatus(barAfflictions[i].afflictionType), 0f), 1f - at);
            seg.enabled = amount > 0.001f;
            if (!seg.enabled)
            {
                continue;
            }
            seg.fillAmount = amount * portion;
            // Start where the previous one ended: turn the copy round the ring (positive z turns anticlockwise).
            float angle = at * portion * 360f;
            seg.rectTransform.localRotation = Quaternion.Euler(0f, 0f, ring.clockwise ? -angle : angle);
            Color c = barColors[i];
            c.a *= ring.shown;
            seg.color = c;
            at += amount;
        }
    }

    // Reads PEAK's stamina bar once it exists: which afflictions it shows, and in what colours.
    private static bool ReadBar()
    {
        if (barAfflictions != null)
        {
            return true;
        }
        StaminaBar bar = GUIManager.instance != null ? GUIManager.instance.bar : null;
        if (bar == null)
        {
            return false;
        }
        BarAffliction[] all = bar.transform.parent.GetComponentsInChildren<BarAffliction>(true);
        System.Collections.Generic.List<BarAffliction> found = new();
        System.Collections.Generic.List<Color> colors = new();
        foreach (BarAffliction a in all)
        {
            if (a == null || a.isPetrify)
            {
                continue; // petrification isn't part of the stamina cap
            }
            Image image = SegmentImage(a.gameObject, a.icon);
            if (image == null)
            {
                continue;
            }
            found.Add(a);
            Color solid = image.color;
            solid.a = 1f; // PEAK's bar colours are see-through over a backing; the rings have none
            colors.Add(solid);
            Plugin.Log.LogInfo($"Grip rings: {a.afflictionType} colour {image.color}");
        }
        Image stamina = bar.staminaBar != null ? SegmentImage(bar.staminaBar.gameObject, null) : null;
        staminaColor = stamina != null ? new Color(stamina.color.r, stamina.color.g, stamina.color.b, 1f) : null;
        barAfflictions = found.ToArray();
        barColors = colors.ToArray();
        return true;
    }

    // The coloured block of a bar segment: its own Image, or the first child Image that isn't its icon.
    private static Image SegmentImage(GameObject go, Image icon)
    {
        Image own = go.GetComponent<Image>();
        if (own != null && own != icon)
        {
            return own;
        }
        foreach (Image image in go.GetComponentsInChildren<Image>(true))
        {
            if (image != icon && (icon == null || !image.transform.IsChildOf(icon.transform)))
            {
                return image;
            }
        }
        return null;
    }

    private static void AddSegments(Ring ring)
    {
        if (ring == null || ring.segments != null || barAfflictions == null)
        {
            return;
        }
        if (staminaColor.HasValue)
        {
            ring.fillColor = staminaColor.Value;
        }
        ring.segments = new Image[barAfflictions.Length];
        int at = ring.empty.transform.GetSiblingIndex();
        for (int i = 0; i < ring.segments.Length; i++)
        {
            GameObject copy = Object.Instantiate(ring.empty.gameObject, ring.empty.transform.parent);
            copy.name = "Peakle Affliction " + barAfflictions[i].afflictionType;
            for (int c = copy.transform.childCount - 1; c >= 0; c--)
            {
                Object.Destroy(copy.transform.GetChild(c).gameObject);
            }
            copy.transform.SetSiblingIndex(at); // under the grip fill
            Image seg = copy.GetComponent<Image>();
            seg.enabled = false;
            ring.segments[i] = seg;
        }
    }

    // Where the hand is on screen: its hold while holding (that's where the stretched hand is drawn), else the hand.
    private static Vector2 HandScreenPoint(Character character, PeakleHands.Hand hand)
    {
        Camera cam = MainCamera.instance != null ? MainCamera.instance.cam : Camera.main;
        if (cam == null)
        {
            return new Vector2(float.NaN, float.NaN);
        }
        Vector3 world = hand.grabbing ? hand.point : character.GetBodypartRig(hand.Bodypart).transform.position;
        Vector3 sp = cam.WorldToScreenPoint(world);
        return sp.z <= 0.05f ? new Vector2(float.NaN, float.NaN) : new Vector2(sp.x, sp.y);
    }

    private static bool Build()
    {
        source = Object.FindFirstObjectByType<UI_UseItemProgress>(FindObjectsInactive.Include);
        if (source == null || source.fill == null)
        {
            return false;
        }
        parent = (RectTransform)source.transform.parent;
        Canvas canvas = source.GetComponentInParent<Canvas>();
        canvasCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;

        handLeft = Clone("Peakle Hand Ring L", Plugin.RingScale.Value, clockwise: false);
        handRight = Clone("Peakle Hand Ring R", Plugin.RingScale.Value, clockwise: true);
        // Radial fill from the bottom: clockwise runs up the left side, anticlockwise up the right side.
        halfLeft = Clone("Peakle Grip Half L", Plugin.CrosshairRingScale.Value, clockwise: true);
        halfRight = Clone("Peakle Grip Half R", Plugin.CrosshairRingScale.Value, clockwise: false);
        carryRing = Clone("Peakle Carry Ring", Plugin.CrosshairRingScale.Value * 1.4f, clockwise: true);
        carryLabel = BuildLabel();
        dotLeft = Clone("Peakle Grab Dot L", Plugin.GrabDotScale.Value, clockwise: true);
        dotRight = Clone("Peakle Grab Dot R", Plugin.GrabDotScale.Value, clockwise: true);
        return handLeft != null && handRight != null && halfLeft != null && halfRight != null && dotLeft != null && dotRight != null && carryRing != null;
    }

    private static Ring Clone(string name, float scale, bool clockwise)
    {
        GameObject copy = Object.Instantiate(source.gameObject, source.transform.parent);
        copy.name = name;
        UI_UseItemProgress script = copy.GetComponent<UI_UseItemProgress>();
        Ring ring = new() { rt = (RectTransform)copy.transform, fill = script.fill, empty = script.empty, clockwise = clockwise };
        Object.DestroyImmediate(script); // the copy is driven by Peakle, not by item use
        if (ring.fill == null || ring.empty == null)
        {
            Object.Destroy(copy);
            return null;
        }
        ring.fillColor = ring.fill.color;
        ring.emptyColor = ring.empty.color;
        // PEAK pops its ring in from scale 0, so the copy can't take the source's current scale.
        ring.rt.localScale = Vector3.one * scale;
        foreach (Image image in new[] { ring.fill, ring.empty })
        {
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Radial360;
            image.fillOrigin = (int)Image.Origin360.Bottom;
            image.fillClockwise = clockwise;
            image.enabled = false;
            image.raycastTarget = false;
        }
        return ring;
    }
}
