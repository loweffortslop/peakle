using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace Peakle;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BaseUnityPlugin
{
    public const string Guid = "peakle.hands";
    public const string Name = "Peakle";
    public const string Version = "1.0.0";

    internal static ManualLogSource Log;

    internal static HostSetting WkScale; // the host's value in multiplayer
    internal static ConfigEntry<float> HandPullForce;
    internal static ConfigEntry<float> HandSurfaceOffset;
    internal static ConfigEntry<float> ItemSideOffset;

    internal static HostSetting JumpStrength; // the host's value in multiplayer
    internal static HostSetting Pace; // the host's value in multiplayer

    internal static ConfigEntry<bool> ShowDebugHud;
    internal static ConfigEntry<bool> ReplaceMenuLogo;
    internal static ConfigEntry<bool> StretchArms;
    internal static ConfigEntry<bool> SlotOrbs;
    internal static ConfigEntry<float> RingScale;
    internal static ConfigEntry<float> CrosshairRingScale;
    internal static ConfigEntry<float> HandSlotGap;
    internal static ConfigEntry<float> GrabDotScale;
    internal static ConfigEntry<float> GrabDotOffset;
    internal static ConfigEntry<float> HandSlotHeight;
    internal static HostSetting RopeDrag; // the host's value in multiplayer
    internal static HostSetting DragHoldDistance; // the host's value in multiplayer
    internal static ConfigEntry<float> PanelSlotAngle;
    internal static ConfigEntry<float> TabDistance;
    internal static HostSetting ScoutMass; // the host's value in multiplayer
    internal static HostSetting ConsciousDragGive; // the host's value in multiplayer

    internal static HostSetting SlideMinFallSpeed; // the host's value in multiplayer
    internal static HostSetting SlideGripPerMetre; // the host's value in multiplayer
    internal static HostSetting SlideDamping; // the host's value in multiplayer
    internal static HostSetting SlideDeceleration; // the host's value in multiplayer
    internal static HostSetting WetSlideSpeed; // the host's value in multiplayer
    internal static HostSetting WorldPushStrength; // the host's value in multiplayer
    internal static HostSetting SlideOnlySpeed; // the host's value in multiplayer

    private void Awake()
    {
        Log = Logger;

        // Every number has a range, so config menus (PEAKModding ModConfig) show sliders.
        WkScale = Gameplay("Feel", "World Scale", 0.85f, 0.4f, 1.5f,
            "PEAK metres per White Knuckle metre. Every White Knuckle distance and speed (reach 2.5, arm length 1.2, climb speed...) is multiplied by this.");
        Pace = Gameplay("Feel", "Pace", 0.8f, 0.3f, 1.5f,
            "Speed of White Knuckle's motion in time (1 = White Knuckle exactly). Lower is slower climbing, swinging and jumps along the same paths.");
        JumpStrength = Gameplay("Feel", "Grab Jump Strength", 1f, 0f, 3f, "Grab jump strength multiplier (1 = White Knuckle's jump).");
        RopeDrag = Gameplay("Feel", "Rope Damping", 6f, 1f, 30f, "Extra damping while a hand holds a rope, vine or chain (x White Knuckle's air drag), so grabbing one doesn't slingshot and keep swinging.");

        SlideMinFallSpeed = Gameplay("Catch Slide", "Min Fall Speed", 3f, 0f, 20f, "Falling faster than this (m/s) when a hand grabs makes the hold slide down the wall.");
        SlideGripPerMetre = Gameplay("Catch Slide", "Grip Cost Per Metre", 1f, 0f, 5f, "Grip a sliding hand loses per metre slid (a full hand has 10).");
        SlideDamping = Gameplay("Catch Slide", "Slow Down", 0.2f, 0.01f, 1f, "Fraction of slide speed kept after one second (exponential slow-down).");
        SlideDeceleration = Gameplay("Catch Slide", "Braking", 4f, 0f, 30f, "Constant slow-down of the slide (m/s per second).");

        WetSlideSpeed = Gameplay("Catch Slide", "Wet Slide Speed", 0.6f, 0f, 5f, "How fast holds slide down in storms/rain at full wetness (m/s), and on slippery surfaces.");
        SlideOnlySpeed = Gameplay("Catch Slide", "Slide-Only Surface Speed", 1.5f, 0f, 6f, "How fast holds slide on surfaces PEAK only lets you slide down (m/s).");

        WorldPushStrength = Gameplay("Feel", "World Push Strength", 1f, 0f, 2f, "How much wind, blasts and knock back push you while hanging or grab jumping (1 = like PEAK).");

        HandPullForce = Number("Visual", "Hand Pull", 15f, 0f, 200f, "How hard a holding hand's ragdoll part is pulled onto its hold (gentle; the arm IK does the placing).");
        HandSurfaceOffset = Number("Visual", "Hand Distance From Wall", 0.06f, 0f, 0.3f, "How far off the surface a holding hand sits (m).");
        ItemSideOffset = Number("Visual", "Held Item Spread", 0.22f, 0f, 0.6f, "How far to the side of centre a held item sits (m).");
        StretchArms = Config.Bind("Visual", "Stretch Arms", true, "Holding arms stretch to reach their hold, like White Knuckle's hands (only how it looks).");
        ReplaceMenuLogo = Config.Bind("Visual", "Peakle Menu Logo", true, "Show the Peakle logo instead of PEAK's on the main menu.");

        ShowDebugHud = Config.Bind("HUD", "Grip Rings", true, "Show each hand's grip rings (on the hands and around the crosshair).");
        RingScale = Number("HUD", "Hand Ring Size", 0.5f, 0.1f, 2f, "Size of the grip ring on each hand, relative to PEAK's crosshair progress ring.");
        CrosshairRingScale = Number("HUD", "Crosshair Ring Size", 0.4f, 0.1f, 2f, "Size of the split grip ring around the crosshair (left half = left hand, right half = right hand).");
        GrabDotScale = Number("HUD", "Grab Dot Size", 0.12f, 0.02f, 1f, "Size of the dot beside the crosshair that shows a hand is holding on to something.");
        GrabDotOffset = Number("HUD", "Grab Dot Distance", 30f, 0f, 200f, "Distance of the grab dots from the crosshair (screen pixels).");
        HandSlotGap = Number("HUD", "Hand Slot Gap", 24f, 0f, 400f, "Gap between the left and right hand slots at the bottom of the screen (UI units).");
        HandSlotHeight = Number("HUD", "Hand Slot Height", 0.09f, 0f, 0.5f, "Height of the hand slots above the bottom of the screen (fraction of screen height).");

        PanelSlotAngle = Number("Backpack", "Slot Size", 7f, 2f, 20f, "Size of a floating backpack slot, in degrees of view (they look the same size near or far).");
        SlotOrbs = Config.Bind("Backpack", "Slot Outlines", true, "Draw the white outline around each floating backpack slot.");
        TabDistance = Number("Backpack", "Tab Distance", 0.75f, 0.3f, 2f, "How far in front of your eyes Tab shows your backpack (m).");

        Controls.Bind(Config);

        DragHoldDistance = Gameplay("Scouts", "Drag Distance", 1.2f, 0.4f, 3f, "How far in front of your eyes a grabbed scout is pulled to (White Knuckle props: 2).");
        ScoutMass = Gameplay("Scouts", "Scout Weight", 2.5f, 0.5f, 10f, "How heavy a grabbed scout is to drag (White Knuckle prop mass; higher = slower to move).");
        ConsciousDragGive = Gameplay("Scouts", "Conscious Give", 0.25f, 0f, 1f, "How much a conscious scout gives in to being dragged each physics step (knocked-out scouts: fully).");

        // Patch class by class: one patch that no longer fits the game must not take all the others down with it.
        Harmony harmony = new(Guid);
        int failed = 0;
        foreach (System.Type type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (System.Exception e)
            {
                failed++;
                Log.LogError($"Patch {type.Name} failed: {e.GetBaseException().Message}");
            }
        }
        if (failed > 0)
        {
            Log.LogError($"{failed} patch(es) failed; those features won't work.");
        }
        PromptRefresher.Init();
        MenuLogo.Init(gameObject);
        Log.LogInfo($"{Name} {Version} loaded");
    }

    // A gameplay setting: in multiplayer everyone uses the host's value (HostSettings).
    private HostSetting Gameplay(string section, string key, float value, float min, float max, string description)
    {
        return HostSettings.Add(Number(section, key, value, min, max, description + " Multiplayer: the host's value is used."));
    }

    private ConfigEntry<float> Number(string section, string key, float value, float min, float max, string description)
    {
        return Config.Bind(section, key, value, new ConfigDescription(description, new AcceptableValueRange<float>(min, max)));
    }
}
