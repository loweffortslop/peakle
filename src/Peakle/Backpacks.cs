using Photon.Pun;
using UnityEngine;
using UnityEngine.InputSystem;
using Zorro.Core.Serizalization;

namespace Peakle;

/// <summary>
/// White Knuckle-style backpack handling (local player), through one floating panel of PEAK hotbar-style slots.
/// Hands use a slot by looking at it and clicking: an empty hand takes the item out, a hand with an item puts it in.
///
/// - Own worn backpack: hold Tab. Like White Knuckle's inventory (Inventory.ShowInventory) it appears where you were
///   looking when you pressed Tab (halfway between body and camera direction), stays in that direction as you move,
///   and hides while you look more than 75 degrees away. It shows NO slots: as in vanilla, you can't get into the
///   backpack on your own back (teammates can) - a key co-op dependency. Grab it with a free hand to take it off
///   your back into that hand (vanilla: holding your backpack); then it's a held backpack like any other.
/// - A backpack in a hand, none worn: hold Tab and its slots appear the same way, with a middle slot for your back;
///   click that with the hand holding the backpack to put it on. The other hand uses its slots.
/// - Another scout's worn backpack, or one lying on the ground: look at it and its slots appear around it. Grabbing a
///   ground backpack itself (not a slot) picks it up into that hand.
/// - Another scout's rocket pack: a single slot that lights it (you can't light your own, as in vanilla).
///
/// Taking, stashing, wearing, dropping and lighting go through PEAK's own networked code.
/// </summary>
public partial class PeakleHands
{
    private enum PanelKind
    {
        None,
        OwnSlots,    // own worn backpack, summoned with Tab
        Held,        // the backpack in your hand: its slots, and a middle slot to put it on; summoned with Tab
        WornSlots,   // someone else's worn backpack
        ItemSlots,   // a backpack lying on the ground
        Rocket,      // light someone's rocket pack
    }

    private const float HideAngle = 75f; // White Knuckle hides its inventory past this

    /// <summary>Hand holding the local player's own worn backpack in front of them, or null.</summary>
    internal Hand backpackHand;

    private bool tabHeld;
    private bool tabWasHeld;
    private Vector3 openDirection; // world direction the Tab things float in, set when Tab is pressed
    private PanelKind panelKind;
    private BackpackOnBackVisuals panelWorn;
    private Backpack panelItem;
    private float panelGrace;
    private int hoveredSlot = -1;
    private PanelKind loggedKind;
    private static BackpackPanel panel;

    /// <summary>The own backpack model is shown floating (Tab or held), and in view.</summary>
    internal bool OwnBackpackShown => panelKind == PanelKind.OwnSlots && TabThingsInView;

    private bool TabThingsInView => Cam != null && Vector3.Angle(Cam.forward, openDirection) <= HideAngle;

    /// <summary>The visuals of whatever is on the local scout's back, or null.</summary>
    private BackpackOnBackVisuals OwnWornVisuals()
    {
        Player player = character.player;
        if (player == null || player.backpackSlot == null || player.backpackSlot.IsEmpty() || character.refs.backpackHandler == null)
        {
            return null;
        }
        return character.refs.backpackHandler.activeBackpackVisuals;
    }

    private static bool IsRocket(BackpackOnBackVisuals visuals) => visuals != null && visuals.backpackType == BackpackSlot.BackpackType.Rocketpack;
    private static bool IsJetpack(BackpackOnBackVisuals visuals) => visuals != null && visuals.backpackType == BackpackSlot.BackpackType.Jetpack;

    // The panel's backpack is a jetpack: its middle slot takes fuel (PEAK's jetpack slice on the backpack wheel).
    private bool PanelIsJetpack => panelKind == PanelKind.WornSlots ? IsJetpack(panelWorn)
        : panelKind == PanelKind.ItemSlots && panelItem != null && panelItem.backpackType == BackpackSlot.BackpackType.Jetpack;

    // Any pack (backpack, fanny pack, jetpack, rocket pack) comes off your back, as long as PEAK lets it be dropped.
    private bool CanTakeOff()
    {
        ItemSlot back = character.player != null ? character.player.backpackSlot : null;
        return back != null && !back.IsEmpty() && back.prefab != null && back.prefab.UIData.canDrop;
    }

    private Hand HandHoldingBackpack()
    {
        foreach (Hand hand in hands)
        {
            if (hand.item is Backpack)
            {
                return hand;
            }
        }
        return null;
    }

    /// <summary>Owner, from OnInputSampled: Tab, keeping hold of the own backpack, and what the panel shows.</summary>
    private void UpdateBackpackInput()
    {
        BackpackOnBackVisuals own = OwnWornVisuals();
        tabHeld = Controls.Held(Controls.Backpack);
        IInteractible looked = Interaction.instance != null ? Interaction.instance.bestInteractable : null;
        // Tab is about carrying while looking at a knocked-out scout or carrying one.
        if (UpdateCarry(tabHeld, looked))
        {
            tabHeld = false;
        }
        if (tabHeld && !tabWasHeld && Cam != null)
        {
            // White Knuckle: halfway between where the body faces and where the camera looks.
            openDirection = Vector3.Lerp(character.data.lookDirection_Flat.normalized, Cam.forward, 0.5f).normalized;
        }
        tabWasHeld = tabHeld;

        if (backpackHand != null && (!backpackHand.held || own == null || !CanTakeOff()
            || backpackHand.item != null || backpackHand.grabbing))
        {
            backpackHand = null;
        }

        PanelKind kind = PanelKind.None;
        BackpackOnBackVisuals worn = null;
        Backpack item = null;
        if ((tabHeld || backpackHand != null) && own != null && CanTakeOff())
        {
            kind = PanelKind.OwnSlots;
            worn = own;
        }
        else if (tabHeld && HandHoldingBackpack() != null)
        {
            kind = PanelKind.Held;
            item = (Backpack)HandHoldingBackpack().item;
        }
        else if (looked is BackpackOnBackVisuals other && other.character != null && other.character != character
            && other.character.player != null && !other.character.player.backpackSlot.IsEmpty())
        {
            worn = other;
            kind = IsRocket(other) ? PanelKind.Rocket : other.slotCount > 0 || IsJetpack(other) ? PanelKind.WornSlots : PanelKind.None;
        }
        else if (looked is Backpack ground && ground.itemState == ItemState.Ground
            && (ground.slotCount > 0 || ground.backpackType == BackpackSlot.BackpackType.Jetpack))
        {
            kind = PanelKind.ItemSlots;
            item = ground;
        }

        if (kind != PanelKind.None)
        {
            panelKind = kind;
            panelWorn = worn;
            panelItem = item;
            panelGrace = 0.3f;
        }
        else
        {
            panelGrace -= Time.deltaTime;
            // Tab things go away with Tab; looked-at things linger a moment so the crosshair can move onto a slot.
            if (panelGrace <= 0f || panelKind == PanelKind.OwnSlots || panelKind == PanelKind.Held)
            {
                panelKind = PanelKind.None;
                panelWorn = null;
                panelItem = null;
            }
        }
        if (panelKind != PanelKind.None && !PanelAnchor(out _, out _))
        {
            panelKind = PanelKind.None;
        }
        if (panelKind != loggedKind)
        {
            loggedKind = panelKind;
            Plugin.Log.LogInfo($"Backpack panel: {panelKind} (looking at {(looked == null ? "nothing" : looked.GetType().Name)})");
        }
        hoveredSlot = panel != null && panel.Visible && panelKind != PanelKind.None && Cam != null
            ? panel.HitTest(new Ray(Cam.position, Cam.forward))
            : -1;
    }

    /// <summary>
    /// Owner: the drop key of the hand holding the own backpack drops it, like any held item
    /// (PEAK's drop-from-slot for the back slot: the backpack lands with its contents).
    /// </summary>
    private bool TryDropHeldBackpack(Hand hand, bool dropPressed)
    {
        if (hand != backpackHand || !dropPressed || character.player.backpackSlot.IsEmpty())
        {
            return false;
        }
        Vector3 at = OwnTabPose(out Vector3 pos, out _) ? pos : character.Center + character.data.lookDirection_Flat * 0.5f;
        character.refs.items.photonView.RPC("DropItemFromSlotRPC", RpcTarget.All, (byte)3, at);
        backpackHand = null;
        return true;
    }

    /// <summary>Owner: a hand button press that's about a backpack. Returns true if it was.</summary>
    private bool HandleBackpackButton(Hand hand)
    {
        if (hand == backpackHand)
        {
            return true; // busy holding the backpack
        }
        if (!hand.down || hand.grabbing)
        {
            return false;
        }

        if (panelKind != PanelKind.None && hoveredSlot >= 0)
        {
            UseSlot(hand, hoveredSlot);
            return true;
        }

        // Grab your own floating backpack with a free hand: it comes off your back into that hand.
        if (panelKind == PanelKind.OwnSlots && IsFree(hand) && LookingAtOwnBackpack() && !character.player.backpackSlot.IsEmpty())
        {
            view.RPC(nameof(RPCM_PeakleMoveSlots), RpcTarget.MasterClient, (byte)3, hand.HandSlot, false);
            return true;
        }
        return false; // e.g. grabbing a ground backpack itself picks it up (Backpack.Interact patch)
    }

    private void UseSlot(Hand hand, int slotIndex)
    {
        if (slotIndex == BackpackPanel.Middle)
        {
            if (panelKind == PanelKind.Rocket)
            {
                panelWorn.character.refs.backpackHandler.LightRocket(3f);
            }
            else if (panelKind == PanelKind.Held && hand.item == panelItem && character.player.backpackSlot.IsEmpty())
            {
                view.RPC(nameof(RPCM_PeakleMoveSlots), RpcTarget.MasterClient, hand.HandSlot, (byte)3, false); // put it on
            }
            else if (PanelIsJetpack && hand.item != null && hand.item != panelItem && hand.item.progress <= 0f && hand.item.UIData.canDrop)
            {
                // PEAK's jetpack slice: whatever the hand holds is burnt as fuel.
                SetActiveHand(hand);
                if (panelKind == PanelKind.ItemSlots)
                {
                    panelItem.AddFuel(character);
                }
                else
                {
                    panelWorn.character.refs.backpackHandler.AddFuelToJetpack(character);
                }
            }
            return;
        }
        BackpackData data = PanelData();
        if (data == null)
        {
            return;
        }
        ItemSlot slot = data.itemSlots[slotIndex];
        BackpackVisuals visuals = panelKind == PanelKind.WornSlots || panelKind == PanelKind.OwnSlots ? panelWorn : panelItem.GetComponent<ItemBackpackVisuals>();
        if (!slot.IsEmpty() && hand.item == null)
        {
            if (panelKind == PanelKind.Held)
            {
                // A backpack in a hand doesn't hang its items on itself; the master moves the item across.
                view.RPC(nameof(RPCM_PeakleTakeFromBackpackItem), RpcTarget.MasterClient, panelItem.photonView.ViewID, (byte)slotIndex, hand.HandSlot);
            }
            else if (visuals != null && visuals.TryGetSpawnedItem((byte)slotIndex, out Item stored) && stored != null)
            {
                SetPickupHand(hand.id);
                stored.Interact(character);
            }
        }
        else if (slot.IsEmpty() && hand.item != null && hand.item.UIData.canBackpack && hand.item.progress <= 0f && hand.item != panelItem)
        {
            SetActiveHand(hand);
            if (panelKind == PanelKind.ItemSlots || panelKind == PanelKind.Held)
            {
                panelItem.Stash(character, (byte)slotIndex);
            }
            else
            {
                panelWorn.character.refs.backpackHandler.StashInBackpack(character, (byte)slotIndex);
            }
        }
    }

    /// <summary>Master: move an item out of a backpack item's slot into a player's hand slot, and tell everyone.</summary>
    [PunRPC]
    public void RPCM_PeakleTakeFromBackpackItem(int backpackViewID, byte backpackSlot, byte handSlot)
    {
        if (!PhotonNetwork.IsMasterClient || character.player == null)
        {
            return;
        }
        PhotonView bpView = PhotonView.Find(backpackViewID);
        Backpack backpack = bpView != null ? bpView.GetComponent<Backpack>() : null;
        if (backpack == null || backpack.data == null || !backpack.data.TryGetDataEntry(DataEntryKey.BackpackData, out BackpackData contents))
        {
            return;
        }
        ItemSlot from = contents.itemSlots[backpackSlot];
        ItemSlot to = character.player.GetItemSlot(handSlot);
        if (from == null || from.IsEmpty() || to == null || !to.IsEmpty())
        {
            return;
        }
        to.SetItem(from.prefab, from.data);
        from.EmptyOut();
        bpView.RPC("SetItemInstanceDataRPC", RpcTarget.Others, backpack.data);
        backpack.GetComponent<ItemBackpackVisuals>()?.RefreshVisuals();
        Player player = character.player;
        byte[] sync = IBinarySerializable.ToManagedArray(new InventorySyncData(player.itemSlots, player.backpackSlot, player.tempFullSlot));
        player.photonView.RPC("SyncInventoryRPC", RpcTarget.Others, sync, false);
        player.itemsChangedAction?.Invoke(player.itemSlots);
        if (view.IsMine)
        {
            Reconcile();
        }
    }

    // A jetpack's middle slot: its icon, and how full it is (PEAK's fuel gauge reads the same value).
    private BackpackPanel.Cell? FuelCell()
    {
        if (!PanelIsJetpack)
        {
            return null;
        }
        Item prefab = panelKind == PanelKind.ItemSlots ? panelItem : panelWorn.character.player.backpackSlot.prefab;
        ItemInstanceData data = panelKind == PanelKind.ItemSlots ? panelItem.data : panelWorn.character.player.backpackSlot.data;
        float fuel = data != null && data.TryGetDataEntry(DataEntryKey.UseRemainingPercentage, out FloatItemData left) ? left.Value : 0f;
        return new BackpackPanel.Cell
        {
            prefab = prefab,
            icon = prefab != null ? prefab.UIData.GetIcon() : null,
            label = $"Fuel {Mathf.RoundToInt(Mathf.Clamp01(fuel) * 100f)}%",
        };
    }

    private BackpackData PanelData()
    {
        if (panelKind == PanelKind.ItemSlots || panelKind == PanelKind.Held)
        {
            return panelItem != null && panelItem.data != null && panelItem.data.TryGetDataEntry(DataEntryKey.BackpackData, out BackpackData itemData) ? itemData : null;
        }
        Character owner = panelWorn != null ? panelWorn.character : null;
        if (owner == null || owner.player == null || owner.player.backpackSlot.IsEmpty())
        {
            return null;
        }
        return owner.player.backpackSlot.data.TryGetDataEntry(DataEntryKey.BackpackData, out BackpackData data) ? data : null;
    }

    // ---------------------------------------------------------------- placement

    private Transform Cam => MainCamera.instance != null ? MainCamera.instance.transform : null;

    /// <summary>Where Tab things float: in the direction set when Tab was pressed, in front of the eyes.</summary>
    internal bool OwnTabPose(out Vector3 position, out Quaternion rotation)
    {
        Transform cam = Cam;
        if (cam == null || openDirection == Vector3.zero)
        {
            position = default;
            rotation = default;
            return false;
        }
        position = cam.position + openDirection * Plugin.TabDistance.Value;
        // A worn backpack's forward is its wearer's facing, so this shows its outside to the camera.
        rotation = Quaternion.LookRotation(openDirection, Vector3.up);
        return true;
    }

    private Bounds ownPackBounds;
    private float ownPackBoundsTime = -1f;
    private Renderer[] ownPackRenderers;
    private BackpackOnBackVisuals ownPackRenderersOf;

    private bool LookingAtOwnBackpack()
    {
        Transform cam = Cam;
        if (cam == null || Time.time - ownPackBoundsTime > 0.5f)
        {
            return false;
        }
        Bounds b = ownPackBounds;
        b.Expand(0.06f);
        return b.IntersectRay(new Ray(cam.position, cam.forward));
    }

    // The visible model's bounds (its pivot is where it sits on the back, not its middle).
    private bool PackBounds(BackpackOnBackVisuals visuals, out Bounds bounds)
    {
        if (ownPackRenderersOf != visuals || ownPackRenderers == null)
        {
            ownPackRenderers = visuals.view != null ? visuals.view.GetComponentsInChildren<Renderer>(true) : visuals.GetComponentsInChildren<Renderer>(true);
            ownPackRenderersOf = visuals;
        }
        bounds = default;
        bool any = false;
        foreach (Renderer r in ownPackRenderers)
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
            {
                continue;
            }
            if (!any)
            {
                bounds = r.bounds;
                any = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }
        return any;
    }

    /// <summary>Where the hand holding the own backpack grips it.</summary>
    internal Vector3 BackpackGripPoint(Hand hand)
    {
        OwnTabPose(out Vector3 pos, out Quaternion rot);
        return pos + rot * Vector3.right * (hand.Side * 0.17f * Plugin.WkScale.Value);
    }

    // Where the panel goes and which way it faces.
    private bool PanelAnchor(out Vector3 center, out Quaternion facing)
    {
        Transform cam = Cam;
        center = default;
        facing = Quaternion.identity;
        if (cam == null)
        {
            return false;
        }
        if (panelKind == PanelKind.OwnSlots || panelKind == PanelKind.Held)
        {
            return OwnTabPose(out center, out facing);
        }
        Transform thing = panelWorn != null ? panelWorn.transform : panelItem != null ? panelItem.transform : null;
        if (thing == null || Vector3.Distance(thing.position, cam.position) > Wk.InteractDistance * Plugin.WkScale.Value)
        {
            return false;
        }
        // In front of the backpack (towards the camera), facing the camera.
        center = thing.position + (cam.position - thing.position).normalized * (0.15f * Plugin.WkScale.Value);
        facing = Quaternion.LookRotation(center - cam.position, Vector3.up);
        return true;
    }

    /// <summary>
    /// Local player, right before the frame renders: show the own backpack where it floats (its on-back model is
    /// otherwise hidden in first person) and place the panel. The backpack model goes back on the back after
    /// rendering, so nothing else ever sees it moved.
    /// </summary>
    internal void RenderBackpacks()
    {
        BackpackOnBackVisuals own = OwnWornVisuals();
        if (own != null && OwnBackpackShown && OwnTabPose(out Vector3 pos, out Quaternion rot))
        {
            own.SetViewActive(true);
            ArmVisuals.SaveForRender(own.transform);
            own.transform.SetPositionAndRotation(pos, rot);
            // Centre the visible model on the floating point, and remember where it is for grabbing it.
            if (PackBounds(own, out Bounds bounds))
            {
                own.transform.position += pos - bounds.center;
                bounds.center = pos;
                ownPackBounds = bounds;
                ownPackBoundsTime = Time.time;
            }
            // Stored items hang on the backpack's slot points; move them along.
            for (byte b = 0; b < own.backpackSlots.Length; b++)
            {
                if (own.TryGetSpawnedItem(b, out Item item) && item != null && own.backpackSlots[b] != null)
                {
                    Transform slotPoint = own.backpackSlots[b];
                    item.transform.SetPositionAndRotation(slotPoint.position - slotPoint.rotation * item.centerOfMass * 0.5f, slotPoint.rotation);
                }
            }
        }

        bool tabThing = panelKind == PanelKind.OwnSlots || panelKind == PanelKind.Held;
        if (panelKind == PanelKind.None || (tabThing && !TabThingsInView) || !PanelAnchor(out Vector3 center, out Quaternion facing))
        {
            panel?.Hide();
            return;
        }
        if (panel != null && !panel.Alive)
        {
            panel.Destroy(); // the HUD was rebuilt (new scene)
            panel = null;
        }
        // Not over menus, pause or loading screens.
        if (GUIManager.instance == null || GUIManager.instance.windowBlockingInput || !character.CanDoInput())
        {
            panel?.Hide();
            return;
        }
        panel ??= BackpackPanel.Create();
        if (panel == null)
        {
            return;
        }
        switch (panelKind)
        {
            case PanelKind.Rocket:
            {
                Item prefab = panelWorn.character.player.backpackSlot.prefab;
                BackpackPanel.Cell light = new() { prefab = prefab, icon = prefab != null ? prefab.UIData.GetIcon() : null };
                panel.Show(center, facing, System.Array.Empty<BackpackPanel.Cell>(), light, hoveredSlot, 0f);
                break;
            }
            case PanelKind.Held:
            {
                // Its slots around, and in the middle a faded copy of it: the spot on your back (if it's free).
                BackpackPanel.Cell? back = character.player.backpackSlot.IsEmpty()
                    ? new BackpackPanel.Cell { icon = panelItem.UIData.GetIcon(), faded = true }
                    : null;
                panel.Show(center, facing, PanelData(), panelItem.slotCount, back, hoveredSlot, 1.3f);
                break;
            }
            case PanelKind.ItemSlots:
                panel.Show(center, facing, PanelData(), panelItem.slotCount, FuelCell(), hoveredSlot, 1.3f);
                break;
            case PanelKind.OwnSlots:
                panel.Hide(); // vanilla: your own back is for teammates to rummage in
                break;
            default:
                panel.Show(center, facing, PanelData(), panelWorn.slotCount, FuelCell(), hoveredSlot, 1.3f);
                break;
        }
    }
}
