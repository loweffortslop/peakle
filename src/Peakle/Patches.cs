using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using Zorro.Core;
using Zorro.Core.Serizalization;

namespace Peakle;

internal static class HandsOf
{
    public static PeakleHands Get(Component c)
    {
        if (c == null || !c.TryGetComponent(out PeakleHands hands) || hands.Disabled)
        {
            return null;
        }
        return hands;
    }
}

// Give every player character a pair of hands. Bots and zombies stay vanilla.
[HarmonyPatch(typeof(Character), nameof(Character.Start))]
internal static class Character_Start
{
    private static void Postfix(Character __instance)
    {
        if (__instance.isBot || __instance.GetComponent<PeakleHands>() != null)
        {
            return;
        }
        __instance.gameObject.AddComponent<PeakleHands>();
        __instance.refs.view.RefreshRpcMonoBehaviourCache();
    }
}

// ------------------------------------------------------------------ input

// Hands reinterpret the mouse buttons, E, Q, 1 and 3 right after PEAK samples input.
[HarmonyPatch(typeof(CharacterInput), nameof(CharacterInput.Sample))]
internal static class CharacterInput_Sample
{
    private static void Postfix(CharacterInput __instance, bool playerMovementActive)
    {
        HandsOf.Get(__instance)?.OnInputSampled(playerMovementActive);
    }
}

// ------------------------------------------------------------------ climbing / movement

// PEAK's wall-crawl is never started by players; hands replace it. Bots keep it.
[HarmonyPatch(typeof(CharacterClimbing), nameof(CharacterClimbing.TryToStartWallClimb))]
internal static class CharacterClimbing_TryToStartWallClimb
{
    private static bool Prefix(CharacterClimbing __instance, bool botGrab)
    {
        return botGrab || HandsOf.Get(__instance) == null;
    }
}

// While hanging in the air, or flying after a grab jump, White Knuckle's physics replace PEAK's gravity and air movement.
[HarmonyPatch(typeof(Character), nameof(Character.CheckGravity))]
internal static class Character_CheckGravity
{
    private static void Postfix(Character __instance, ref bool __result)
    {
        PeakleHands hands = HandsOf.Get(__instance);
        if (hands != null && hands.OnWkPhysics)
        {
            __result = false;
        }
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.CheckMovement))]
internal static class Character_CheckMovement
{
    private static void Postfix(Character __instance, ref bool __result)
    {
        PeakleHands hands = HandsOf.Get(__instance);
        if (hands != null && hands.OnWkPhysics)
        {
            __result = false;
        }
    }
}

// PEAK damps every ragdoll part's velocity each physics step (x0.85 at 60 Hz). White Knuckle's physics bring
// their own drag, so PEAK's is skipped while they run, or it would eat the climb and jump speed.
[HarmonyPatch(typeof(Bodypart), nameof(Bodypart.Drag))]
internal static class Bodypart_Drag
{
    private static bool Prefix(Bodypart __instance)
    {
        Character character = __instance.character;
        PeakleHands hands = character != null ? HandsOf.Get(character) : null;
        return hands == null || !hands.OnWkPhysics;
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.CheckStand))]
internal static class Character_CheckStand
{
    private static void Postfix(Character __instance, ref bool __result)
    {
        PeakleHands hands = HandsOf.Get(__instance);
        if (hands != null && hands.OnWkPhysics)
        {
            __result = false;
        }
    }
}

// PEAK plays a two-armed "use" animation for the equipped item. Players never get it: the using hand
// alone brings the item up with IK (HandIK), the other arm stays as it is.
[HarmonyPatch(typeof(ItemAudioManager), "Update")]
internal static class ItemAudioManager_Update
{
    private static void Postfix(ItemAudioManager __instance)
    {
        Character character = __instance.character;
        if (character == null || HandsOf.Get(character) == null || character.data.currentItem == null)
        {
            return;
        }
        if (character.data.currentItem.TryGetComponent(out ItemUseFeedback feedback) && !string.IsNullOrEmpty(feedback.useAnimation))
        {
            character.refs.animator.SetBool(feedback.useAnimation, false);
        }
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.CheckJump))]
internal static class Character_CheckJump
{
    private static void Postfix(Character __instance, ref bool __result)
    {
        PeakleHands hands = HandsOf.Get(__instance);
        if (hands != null && hands.Hanging)
        {
            __result = false; // the grab jump handles it
        }
    }
}

// ------------------------------------------------------------------ items

// Players never "equip slots" any more: whatever is in the hand slots is held. Every vanilla call that
// would (re)equip (picking up, eating, stashing...) just makes the hands re-check their slots.
[HarmonyPatch(typeof(CharacterItems), nameof(CharacterItems.EquipSlot))]
internal static class CharacterItems_EquipSlot
{
    private static bool Prefix(CharacterItems __instance)
    {
        PeakleHands hands = HandsOf.Get(__instance);
        if (hands == null)
        {
            return true;
        }
        if (__instance.photonView.IsMine)
        {
            hands.Reconcile();
        }
        return false;
    }
}

// No hotbar switching: hands and pockets are fixed.
[HarmonyPatch(typeof(CharacterItems), nameof(CharacterItems.DoSwitching))]
internal static class CharacterItems_DoSwitching
{
    private static bool Prefix(CharacterItems __instance)
    {
        return HandsOf.Get(__instance) == null;
    }
}

// Hand items are carried kinematically by PeakleHands; no joints to the hands, ever.
[HarmonyPatch(typeof(CharacterItems), nameof(CharacterItems.AttachItem))]
internal static class CharacterItems_AttachItem
{
    private static bool Prefix(CharacterItems __instance)
    {
        return HandsOf.Get(__instance) == null;
    }
}

[HarmonyPatch(typeof(CharacterItems), nameof(CharacterItems.UpdateAttachedItem))]
internal static class CharacterItems_UpdateAttachedItem
{
    private static bool Prefix(CharacterItems __instance)
    {
        return HandsOf.Get(__instance) == null;
    }
}

[HarmonyPatch(typeof(CharacterItems), nameof(CharacterItems.UnAttachItem))]
internal static class CharacterItems_UnAttachItem
{
    private static bool Prefix(CharacterItems __instance)
    {
        if (HandsOf.Get(__instance) == null)
        {
            return true;
        }
        // Either hand may hold the joint.
        foreach (BodypartType part in new[] { BodypartType.Hand_L, BodypartType.Hand_R })
        {
            Rigidbody rig = __instance.character.GetBodypartRig(part);
            if (rig != null && rig.TryGetComponent(out FixedJoint joint))
            {
                Object.Destroy(joint);
            }
        }
        return false;
    }
}

// Picked-up items land in the hand that grabbed them (master decides the slot).
[HarmonyPatch(typeof(Player), nameof(Player.AddItem))]
internal static class Player_AddItem
{
    private static bool Prefix(Player __instance, ushort itemID, ItemInstanceData instanceData, ref ItemSlot slot, ref bool __result)
    {
        Character character = __instance.character;
        PeakleHands hands = character != null ? HandsOf.Get(character) : null;
        if (hands == null || !PhotonNetwork.IsMasterClient || !ItemDatabase.TryGetItem(itemID, out Item prefab))
        {
            return true;
        }
        if (instanceData == null)
        {
            instanceData = new ItemInstanceData(System.Guid.NewGuid());
            ItemInstanceDataHandler.AddInstanceData(instanceData);
        }
        slot = null;
        __result = false;
        int handId = hands.pickupHand;
        hands.pickupHand = -1;
        if (handId < 0)
        {
            // Not grabbed by a hand: the game is giving it (e.g. the passport in the lobby). Left hand first.
            handId = __instance.GetItemSlot(hands.hands[PeakleHands.Left].HandSlot).IsEmpty() ? PeakleHands.Left : PeakleHands.Right;
        }
        ItemSlot target = __instance.GetItemSlot(hands.hands[handId].HandSlot);
        if (target == null || !target.IsEmpty())
        {
            return false;
        }
        target.SetItem(prefab, instanceData);
        slot = target;
        __result = true;
        byte[] sync = IBinarySerializable.ToManagedArray(new InventorySyncData(__instance.itemSlots, __instance.backpackSlot, __instance.tempFullSlot));
        __instance.view.RPC("SyncInventoryRPC", RpcTarget.Others, sync, false);
        return false;
    }
}

// Interaction's "can I pick this up" check looks for any empty slot; for players only the hands count.
[HarmonyPatch(typeof(Player), nameof(Player.HasEmptySlot))]
internal static class Player_HasEmptySlot
{
    private static void Postfix(Player __instance, ushort itemID, ref bool __result)
    {
        Character character = __instance.character;
        PeakleHands hands = character != null ? HandsOf.Get(character) : null;
        if (hands == null || !ItemDatabase.TryGetItem(itemID, out Item prefab))
        {
            return;
        }
        __result = __instance.GetItemSlot(0).IsEmpty() || __instance.GetItemSlot(TemporaryItemSlot.SLOT_INDEX).IsEmpty();
    }
}

[HarmonyPatch(typeof(CharacterItems), nameof(CharacterItems.OnPickupAccepted))]
internal static class CharacterItems_OnPickupAccepted
{
    private static bool Prefix(CharacterItems __instance, byte slotID)
    {
        PeakleHands hands = HandsOf.Get(__instance);
        if (hands == null || slotID == 3)
        {
            return true;
        }
        hands.Reconcile();
        __instance.RefreshAllCharacterCarryWeight();
        return false;
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.SyncInventoryRPC))]
internal static class Player_SyncInventoryRPC
{
    private static void Postfix(Player __instance)
    {
        Character character = __instance.character;
        PeakleHands hands = character != null ? HandsOf.Get(character) : null;
        if (hands != null && character.photonView.IsMine)
        {
            hands.OnInventorySynced();
        }
    }
}

// While White Knuckle's body is in charge, the first-person camera rides it rigidly (no head sway, no lag).
[HarmonyPatch(typeof(MainCameraMovement), nameof(MainCameraMovement.CharacterCam))]
internal static class MainCameraMovement_CharacterCam
{
    private static void Postfix(MainCameraMovement __instance)
    {
        Character local = Character.localCharacter;
        PeakleHands hands = local != null ? HandsOf.Get(local) : null;
        if (hands == null)
        {
            return;
        }
        // Blend between PEAK's camera and the capsule camera over a moment, both ways, so starting and
        // stopping a climb never snaps the view.
        Vector3? wkEye = local.data.currentRagdollControll > 0.9f ? hands.WkEyePosition : null;
        Vector3 hip = local.refs.hip.Rig.transform.position;
        Vector3 eye;
        if (wkEye.HasValue)
        {
            eye = wkEye.Value;
            hands.camOffsetFromHip = eye - hip;
        }
        else
        {
            eye = hip + hands.camOffsetFromHip;
        }
        hands.camWeight = Mathf.MoveTowards(hands.camWeight, wkEye.HasValue ? 1f : 0f, Time.deltaTime / 0.3f);
        if (hands.camWeight > 0f)
        {
            __instance.transform.position = Vector3.Lerp(__instance.transform.position, eye, Mathf.SmoothStep(0f, 1f, hands.camWeight));
        }
    }
}

// A lit campfire is a target whenever either hand holds something cookable (PEAK only checks the equipped item).
[HarmonyPatch(typeof(Campfire), nameof(Campfire.IsInteractible))]
internal static class Campfire_IsInteractible
{
    private static void Postfix(Campfire __instance, Character interactor, ref bool __result)
    {
        CampfireCooking.Adjust(__instance, interactor, ref __result);
    }
}

[HarmonyPatch(typeof(Campfire), nameof(Campfire.IsConstantlyInteractable))]
internal static class Campfire_IsConstantlyInteractable
{
    private static void Postfix(Campfire __instance, Character interactor, ref bool __result)
    {
        CampfireCooking.Adjust(__instance, interactor, ref __result);
    }
}

// Two-handed cooking: when the fire's timer finishes, every item still held on it is cooked, not only the
// one that started the cook (which is skipped if its hand let go first).
[HarmonyPatch(typeof(Campfire), nameof(Campfire.Interact_CastFinished))]
internal static class Campfire_Interact_CastFinished
{
    private static void Prefix(Campfire __instance, Character interactor)
    {
        PeakleHands hands = interactor != null ? HandsOf.Get(interactor) : null;
        if (hands == null || !__instance.Lit)
        {
            return;
        }
        Item first = __instance.currentlyCookingItem;
        bool firstStillHeld = false;
        foreach (Item item in hands.CookingItems())
        {
            if (item == first)
            {
                firstStillHeld = true;
                continue;
            }
            item.GetComponent<ItemCooking>()?.FinishCooking();
        }
        if (first != null && !firstStillHeld)
        {
            first.GetComponent<ItemCooking>()?.CancelCookingVisuals();
            __instance.currentlyCookingItem = null;
        }
    }
}

internal static class CampfireCooking
{
    public static void Adjust(Campfire fire, Character interactor, ref bool result)
    {
        PeakleHands hands = interactor != null ? HandsOf.Get(interactor) : null;
        if (hands == null || !fire.Lit)
        {
            return;
        }
        result = PeakleHands.CanCook(hands.hands[PeakleHands.Left].item) || PeakleHands.CanCook(hands.hands[PeakleHands.Right].item);
    }
}

// Energy drink and the big lollipop hold their timers until you start climbing, which PEAK checks with its own
// climbing state. Hanging from the hands counts as climbing for them.
[HarmonyPatch(typeof(Peak.Afflictions.Affliction_FasterBoi), "UpdateEffect")]
internal static class Affliction_FasterBoi_UpdateEffect
{
    private static void Postfix(Peak.Afflictions.Affliction_FasterBoi __instance)
    {
        PeakleHands hands = __instance.character != null ? HandsOf.Get(__instance.character) : null;
        if (hands != null && hands.Hanging)
        {
            __instance.climbDelay = 0f;
            __instance.bonusTime = 0f;
        }
    }
}

[HarmonyPatch(typeof(Peak.Afflictions.Affliction_InfiniteStamina), "UpdateEffect")]
internal static class Affliction_InfiniteStamina_UpdateEffect
{
    private static void Postfix(Peak.Afflictions.Affliction_InfiniteStamina __instance)
    {
        PeakleHands hands = __instance.character != null ? HandsOf.Get(__instance.character) : null;
        if (hands != null && hands.Hanging)
        {
            __instance.climbDelay = 0f;
            __instance.bonusTime = 0f;
        }
    }
}

// Grabbing a backpack lying on the ground picks it up into that hand (PEAK opens its backpack wheel instead).
// Its slots are used through the floating panel, and Tab puts it on (see Backpacks.cs).
[HarmonyPatch(typeof(Backpack), nameof(Backpack.Interact))]
internal static class Backpack_Interact
{
    private static bool Prefix(Backpack __instance, Character interactor)
    {
        if (interactor == null || HandsOf.Get(interactor) == null)
        {
            return true;
        }
        __instance.Wear(interactor); // = Item.Interact, the normal pickup
        return false;
    }
}

// PEAK finds pitons only in slots 1-3; the right hand is the "hands" slot (250). Count both hands, the hand in use first,
// so a piton in either hand works.
[HarmonyPatch(typeof(CharacterItems), nameof(CharacterItems.UpdateClimbingSpikeCount))]
internal static class CharacterItems_UpdateClimbingSpikeCount
{
    private static void Postfix(CharacterItems __instance)
    {
        Character character = __instance.character;
        PeakleHands hands = character != null ? HandsOf.Get(character) : null;
        if (hands == null || character.player == null)
        {
            return;
        }
        Player player = character.player;
        System.Collections.Generic.List<ItemSlot> order = new();
        if (__instance.currentSelectedSlot.IsSome)
        {
            order.Add(player.GetItemSlot(__instance.currentSelectedSlot.Value));
        }
        foreach (byte id in new byte[] { 0, TemporaryItemSlot.SLOT_INDEX, 1, 2 })
        {
            ItemSlot s = player.GetItemSlot(id);
            if (!order.Contains(s))
            {
                order.Add(s);
            }
        }
        int count = 0;
        __instance.currentClimbingSpikeComponent = null;
        __instance.currentClimbingSpikeItemSlot = null;
        foreach (ItemSlot slot in order)
        {
            if (slot == null || slot.prefab == null)
            {
                continue;
            }
            ClimbingSpikeComponent spike = slot.prefab.GetComponent<ClimbingSpikeComponent>();
            if (spike != null && (!slot.data.TryGetDataEntry(DataEntryKey.CookedAmount, out IntItemData cooked) || cooked.Value <= 0))
            {
                count++;
                if (__instance.currentClimbingSpikeComponent == null)
                {
                    __instance.currentClimbingSpikeComponent = spike;
                    __instance.currentClimbingSpikeItemSlot = slot;
                }
            }
        }
        character.data.climbingSpikeCount = count;
    }
}

// A backpack (or fanny pack) carried in a hand weighs what's in it too, as it does on your back.
[HarmonyPatch(typeof(CharacterAfflictions), nameof(CharacterAfflictions.UpdateWeight))]
internal static class CharacterAfflictions_UpdateWeight
{
    private static void Postfix(CharacterAfflictions __instance)
    {
        Character character = __instance.character;
        if (character == null || character.player == null || HandsOf.Get(character) == null)
        {
            return;
        }
        int extra = 0;
        foreach (byte id in new byte[] { 0, TemporaryItemSlot.SLOT_INDEX })
        {
            ItemSlot slot = character.player.GetItemSlot(id);
            if (slot == null || slot.IsEmpty() || slot.prefab is not Backpack || !slot.data.TryGetDataEntry(DataEntryKey.BackpackData, out BackpackData contents))
            {
                continue;
            }
            foreach (ItemSlot inside in contents.itemSlots)
            {
                if (inside != null && !inside.IsEmpty())
                {
                    extra += inside.prefab.CarryWeight;
                }
            }
        }
        if (extra > 0)
        {
            __instance.SetStatus(CharacterAfflictions.STATUSTYPE.Weight, __instance.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Weight) + 0.025f * extra);
        }
    }
}

// Eating with the other hand: PEAK only empties the slot of the item in hand.
[HarmonyPatch(typeof(Item), nameof(Item.Consume))]
internal static class Item_Consume
{
    private static void Prefix(Item __instance, out bool __state)
    {
        Character holder = __instance.holderCharacter;
        __state = holder != null && holder.data.currentItem == __instance;
    }

    private static void Postfix(Item __instance, bool __state)
    {
        Character holder = __instance.holderCharacter;
        PeakleHands hands = holder != null && holder.IsLocal ? HandsOf.Get(holder) : null;
        hands?.OnItemConsumed(__instance, __state);
    }
}

// A teammate can be fed with whichever hand holds something for them, not only the item in hand.
[HarmonyPatch(typeof(CharacterInteractible), "HasItemCanUseOnFriend")]
internal static class CharacterInteractible_HasItemCanUseOnFriend
{
    private static bool Prefix(CharacterInteractible __instance, ref Item currentItem, ref bool __result)
    {
        Character local = Character.localCharacter;
        PeakleHands hands = local != null ? HandsOf.Get(local) : null;
        if (hands == null)
        {
            return true;
        }
        Item item = hands.FeedItem(out _);
        Character target = __instance.character;
        __result = item != null && target != null && !target.data.dead && target != local;
        currentItem = __result ? item : null;
        return false;
    }
}

// Pushes from the world reach a scout on White Knuckle physics (hanging, grab jumps) too.
[HarmonyPatch(typeof(Character), nameof(Character.AddForce), new[] { typeof(Vector3), typeof(float), typeof(float) })]
internal static class Character_AddForce
{
    private static void Prefix(Character __instance, Vector3 move, float minRandomMultiplier, float maxRandomMultiplier)
    {
        PeakleHands hands = HandsOf.Get(__instance);
        hands?.AddWorldPush(move * ((minRandomMultiplier + maxRandomMultiplier) * 0.5f) * Time.fixedDeltaTime);
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.RPCA_AddForceAtPosition))]
internal static class Character_AddForceAtPosition
{
    private static void Prefix(Character __instance, Vector3 force, Vector3 point, float radius)
    {
        PeakleHands hands = HandsOf.Get(__instance);
        if (hands == null)
        {
            return;
        }
        // The same impulse PEAK gives each body part, for the body as a whole.
        Vector3 total = Vector3.zero;
        float mass = 0f;
        foreach (Bodypart part in __instance.refs.ragdoll.partList)
        {
            float distance = Vector3.Distance(part.Rig.worldCenterOfMass, point);
            total += force * Mathf.InverseLerp(radius, radius * 0.1f, distance);
            mass += part.Rig.mass;
        }
        if (mass > 0f)
        {
            hands.AddWorldPush(total / mass);
        }
    }
}

// Landing while on White Knuckle physics: PEAK gets the speed from before the impact (fall damage).
[HarmonyPatch(typeof(CharacterMovement), nameof(CharacterMovement.Land))]
internal static class CharacterMovement_Land
{
    private static void Prefix(CharacterMovement __instance)
    {
        HandsOf.Get(__instance.character)?.BeforePeakLanding();
    }
}

// On death PEAK drops the item in hand and slots 1-3 and the back; the other hand (the "hands" slot) too.
[HarmonyPatch(typeof(CharacterItems), nameof(CharacterItems.DropAllItems))]
internal static class CharacterItems_DropAllItems
{
    private static void Postfix(CharacterItems __instance)
    {
        Character character = __instance.character;
        if (character == null || !character.IsLocal || character.player == null || HandsOf.Get(character) == null)
        {
            return;
        }
        ItemSlot slot = character.player.GetItemSlot(TemporaryItemSlot.SLOT_INDEX);
        if (slot != null && !slot.IsEmpty() && slot.prefab.UIData.canDrop)
        {
            Transform hip = character.GetBodypart(BodypartType.Hip).transform;
            __instance.photonView.RPC("DropItemFromSlotRPC", RpcTarget.All, TemporaryItemSlot.SLOT_INDEX, hip.position + Vector3.up * 1.5f);
        }
    }
}

// ------------------------------------------------------------------ animation / IK

// While hanging the scout keeps its upright standing pose: no walk cycle from WASD in the air.
[HarmonyPatch(typeof(CharacterAnimations), "Update")]
internal static class CharacterAnimations_Update
{
    private static readonly int InputX = Animator.StringToHash("Input X");
    private static readonly int InputY = Animator.StringToHash("Input Y");
    private static readonly int Sprint = Animator.StringToHash("Sprint");

    private static void Postfix(CharacterAnimations __instance)
    {
        PeakleHands hands = HandsOf.Get(__instance);
        if (hands != null && hands.HangingAirborne)
        {
            Animator animator = __instance.character.refs.animator;
            animator.SetFloat(InputX, 0f);
            animator.SetFloat(InputY, 0f);
            animator.SetFloat(Sprint, 0f);
        }
    }
}

// Crouching works while hanging (PEAK only allows it on the ground), to squeeze into tight spots.
[HarmonyPatch(typeof(CharacterMovement), nameof(CharacterMovement.SetMovementState))]
internal static class CharacterMovement_SetMovementState
{
    private static void Postfix(CharacterMovement __instance)
    {
        PeakleHands hands = HandsOf.Get(__instance);
        if (hands != null && hands.HangingAirborne && __instance.character.refs.view.IsMine)
        {
            __instance.SetCrouch(__instance.character.input.crouchIsPressed || __instance.crouchToggleEnabled);
        }
    }
}

// Each hand gets its own IK target: its hold, the thing it's interacting with, or its item's grip.
[HarmonyPatch(typeof(CharacterAnimations), nameof(CharacterAnimations.HandleIK))]
internal static class CharacterAnimations_HandleIK
{
    private static void Postfix(CharacterAnimations __instance)
    {
        Character character = __instance.character;
        PeakleHands hands = HandsOf.Get(__instance);
        if (hands == null || !character.refs.ikRight)
        {
            return;
        }
        bool left = HandIK.Wants(character, hands, hands.hands[PeakleHands.Left]);
        bool right = HandIK.Wants(character, hands, hands.hands[PeakleHands.Right]);
        if (__instance.ReachIK())
        {
            return;
        }
        character.refs.ikRig.weight = left || right ? 1f : 0f;
        character.refs.ikLeft.weight = left ? 1f : 0f;
        character.refs.ikRight.weight = right ? 1f : 0f;
    }
}

[HarmonyPatch(typeof(CharacterAnimations), nameof(CharacterAnimations.ConfigureIK))]
internal static class CharacterAnimations_ConfigureIK
{
    private static void Postfix(CharacterAnimations __instance)
    {
        Character character = __instance.character;
        PeakleHands hands = HandsOf.Get(__instance);
        if (hands == null || character.refs.IKHandTargetLeft == null)
        {
            return;
        }
        HandIK.SetTarget(character, hands, hands.hands[PeakleHands.Left], character.refs.IKHandTargetLeft);
        HandIK.SetTarget(character, hands, hands.hands[PeakleHands.Right], character.refs.IKHandTargetRight);
    }
}

internal static class HandIK
{
    public static bool Wants(Character character, PeakleHands hands, PeakleHands.Hand hand)
    {
        return hand.grabbing || hand.item != null || hand.reaching || hand == hands.backpackHand;
    }

    public static void SetTarget(Character character, PeakleHands hands, PeakleHands.Hand hand, Transform target)
    {
        Vector3 world;
        PeakleHands.Hand usingHand = hands.UsingHand;
        if (hand.grabbing)
        {
            world = hand.point + hand.normal * Plugin.HandSurfaceOffset.Value;
        }
        else if (hand.item != null)
        {
            if (hand.item is Guidebook book && book.isOpen && book.bookTransform != null)
            {
                world = book.bookTransform.position + character.data.lookDirection_Right * (hand.Side * 0.14f) - character.data.lookDirection_Up * 0.04f;
            }
            else
            {
                world = hand == usingHand || hand.offUsing ? hands.UseTarget(hand, hand.item) : hands.ItemGripTarget(hand, hand.item);
            }
        }
        else if (hand == hands.backpackHand)
        {
            world = hands.BackpackGripPoint(hand);
        }
        else if (hand.reaching)
        {
            world = hands.ReachTarget(hand);
        }
        else
        {
            return;
        }
        target.SetPositionAndRotation(ToAnim(character, world), character.GetBodypart(hand.Bodypart).transform.rotation);
    }

    // IK targets live in the animation rig's space, which is offset from the physical body.
    // Inverse of Character.GetAnimationRelativePosition.
    public static Vector3 ToAnim(Character character, Vector3 world)
    {
        return world - character.refs.hip.Rig.position + character.GetBodypart(BodypartType.Hip).transform.position;
    }
}
