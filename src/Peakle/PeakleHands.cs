using Photon.Pun;
using UnityEngine;
using Zorro.Core;
using Zorro.Core.Serizalization;

namespace Peakle;

/// <summary>
/// White Knuckle-style hands for one PEAK player character.
///
/// Controls (taken from White Knuckle):
/// - LMB / RMB: the left / right hand. Holding it on a climbable surface grabs it; on an interactable
///   (an item, luggage, a campfire...) it holds the interaction; with an item in that hand, it uses the item.
/// - Q / E: put the left / right hand's item in that side's pocket, or take it out again. No swapping.
/// - 1 / 3: drop the left / right hand's item; hold to throw.
/// - 2: swap the items in the two hands.
/// - Jump while hanging: grab jump.
///
/// Inventory slots: left hand = slot 0, right hand = PEAK's temporary "hands" slot (250),
/// left pocket = slot 1, right pocket = slot 2. Both hand items are real networked items carried
/// kinematically beside their hand; PEAK only supports one equipped item, so "currentItem" just points
/// at whichever hand was used last (that's the one PEAK's use/drop/throw code acts on).
///
/// Hanging isn't PEAK's climbing state: the body stays a free, upright physics body and White Knuckle's
/// hang physics (ENT_Player.Movement/Hang) run in White Knuckle's own units, converted to PEAK metres.
/// </summary>
// Early, so hand items are placed before item scripts adjust their own parts in LateUpdate (e.g. the guidebook).
[DefaultExecutionOrder(-50)]
public partial class PeakleHands : MonoBehaviour
{
    public const int Left = 0;
    public const int Right = 1;

    /// <summary>What a hand is holding on to.</summary>
    public enum HoldKind : byte
    {
        Surface, // anything PEAK lets you climb
        Rope,    // PEAK ropes (RopeSegment): grip drains at PEAK's rope-vs-wall stamina ratio
        Vine,    // vines and chains (JungleVine): grip drains at PEAK's vine-vs-wall stamina ratio
        Piton,   // pitons / climbing handles (ClimbHandle): grip regenerates as if standing
        Scout,   // another scout's body (dragging them, or hanging from them)
    }

    public class Hand
    {
        public int id;
        public HoldKind kind;
        public Transform anchor;   // owner: what the hold is attached to (ropes swing), or null
        public ClimbModifierSurface surface; // owner: PEAK's special-surface settings of the hold (slide-only, slippery, poison...)
        public CollisionModifier hazard;     // owner: a harmful surface (thorns, poison vines): hurts the hand that holds it
        public Item collidersOf;             // all clients: the item whose colliders are listed below
        public Collider[] colliders;
        public bool offUsing;                // all clients: this hand eats at the same time as the other one
        public float offUseStart;
        public Character target;   // Scout holds: who, which body part, where on it (all clients)
        public Bodypart targetPart;
        public Vector3 targetLocal;
        public Vector3 anchorLocal;
        public float anchorSync;

        // wall
        public bool grabbing;
        public Vector3 point;
        public Vector3 normal;
        public float holdTime;
        public float slide;        // m/s the hold is sliding down the surface (catching a fall)
        public float slideSync;    // owner: time until the sliding hold point is sent again

        // grip (White Knuckle units, 0..10)
        public float grip = Wk.GripStrengthTimer;
        public float grabWait;
        public float regenWait;

        // interaction (owner only)
        public bool interacting;
        public bool cooking;       // this hand's item is on the fire

        // empty hand reaching out while its button is held (all clients, for the arm pose)
        public bool reaching;

        // item carried in this hand (all clients)
        public Item item;
        public bool spawnPending;

        // input this frame (owner only)
        public bool down, held, up;

        public byte HandSlot => id == Left ? (byte)0 : TemporaryItemSlot.SLOT_INDEX;
        public byte PocketSlot => id == Left ? (byte)1 : (byte)2;
        public BodypartType Bodypart => id == Left ? BodypartType.Hand_L : BodypartType.Hand_R;
        public string GripName => id == Left ? "Hand_L" : "Hand_R";
        public float Side => id == Left ? -1f : 1f;
    }

    public readonly Hand[] hands = { new Hand { id = Left }, new Hand { id = Right } };

    internal Character character;
    private PhotonView view;

    /// <summary>Hand whose item is PEAK's currentItem, or null.</summary>
    public Hand ActiveHand
    {
        get
        {
            Item current = character != null ? character.data.currentItem : null;
            if (current == null)
            {
                return null;
            }
            foreach (Hand hand in hands)
            {
                if (hand.item == current)
                {
                    return hand;
                }
            }
            return null;
        }
    }

    // Holding on to another scout only hangs you from them while you're off the ground, and only if they are held up
    // themselves (standing, or holding the world): two scouts holding each other in the air can't lift each other.
    private bool Holds(Hand hand) => hand.grabbing && (hand.kind != HoldKind.Scout || (!character.data.isGrounded && IsAnchored(hand.target)));

    /// <summary>Standing, or holding on to the world (not just to other scouts).</summary>
    internal bool Anchored
    {
        get
        {
            if (character.data.isGrounded)
            {
                return true;
            }
            foreach (Hand hand in hands)
            {
                if (hand.grabbing && hand.kind != HoldKind.Scout)
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static bool IsAnchored(Character scout)
    {
        if (scout == null)
        {
            return false;
        }
        PeakleHands other = HandsOf.Get(scout);
        return other != null ? other.Anchored : scout.data.isGrounded;
    }

    public bool Hanging => Holds(hands[Left]) || Holds(hands[Right]);
    public bool HangingAirborne => Hanging && !BodyGrounded;
    public int HangingCount => (Holds(hands[Left]) ? 1 : 0) + (Holds(hands[Right]) ? 1 : 0);

    internal bool Disabled => character == null || character.isBot || character.isZombie;

    // Master only: which hand the owner asked to pick the next item up with.
    internal int pickupHand = -1;

    internal static event System.Action LocalActiveHandChanged;

    private Hand useHand;    // hand currently driving PEAK's "use item" input
    private Hand dropHand;   // hand currently driving PEAK's drop/throw input
    private float reconcileTimer;
    private float swapGraceUntil;

    private float Scale => Plugin.WkScale.Value;

    private void Awake()
    {
        character = GetComponent<Character>();
        view = GetComponent<PhotonView>();
        body = new WkBody(character);
    }

    private WkBody body;

    private void Start()
    {
        if (!Disabled && view.IsMine)
        {
            PromptRefresher.RefreshAll();
        }
    }

    // ================================================================ input (called right after PEAK samples it)

    /// <summary>
    /// Called from a postfix on CharacterInput.Sample, before anything else this frame reads the input.
    /// Turns raw mouse/keys into per-hand actions and rewrites PEAK's use/drop/interact inputs to match.
    /// </summary>
    internal void OnInputSampled(bool active)
    {
        if (Disabled || !view.IsMine)
        {
            return;
        }
        CharacterInput input = character.input;
        Hand left = hands[Left];
        Hand right = hands[Right];
        // Peakle's own hand buttons (Controls); PEAK's use buttons are hidden from PEAK below.
        left.down = active && Controls.Down(Controls.LeftHand);
        left.held = active && Controls.Held(Controls.LeftHand);
        left.up = Controls.Up(Controls.LeftHand);
        right.down = active && Controls.Down(Controls.RightHand);
        right.held = active && Controls.Held(Controls.RightHand);
        right.up = Controls.Up(Controls.RightHand);
        if (active && Hanging && Controls.Held(Controls.HoldStill))
        {
            input.crouchIsPressed = true; // White Knuckle's crouch-to-hold-still, on its own button
        }
        bool jump = input.jumpWasPressed;

        bool interactDown = input.interactWasPressed, interactHeld = input.interactIsPressed, interactUp = input.interactWasReleased;

        // PEAK no longer sees its own use / interact / drop buttons; hands decide what they mean.
        input.usePrimaryWasPressed = input.usePrimaryIsPressed = input.usePrimaryWasReleased = false;
        input.useSecondaryWasPressed = input.useSecondaryIsPressed = input.useSecondaryWasReleased = false;
        input.interactWasPressed = input.interactIsPressed = input.interactWasReleased = false;
        input.dropWasPressed = input.dropIsPressed = input.dropWasReleased = false;

        bool canAct = active && character.data.fullyConscious && !character.data.dead && character.CanDoInput();
        if (!canAct)
        {
            if (character.data.fullyPassedOut)
            {
                // Knocked out, PEAK's interact key is "give up" (hold to let the death timer run faster).
                input.interactWasPressed = interactDown;
                input.interactIsPressed = interactHeld;
                input.interactWasReleased = interactUp;
            }
            foreach (Hand hand in hands)
            {
                hand.down = hand.held = false;
                StopInteracting(hand);
                if (hand.reaching)
                {
                    view.RPC(nameof(RPCA_PeakleReach), RpcTarget.All, (byte)hand.id, false);
                }
            }
            useHand = null;
            dropHand = null;
            backpackHand = null;
            tabHeld = false;
            return;
        }

        UpdateBackpackInput();

        // PEAK has no emotes while climbing (GUIManager.UpdateEmoteWheel); none while a hand holds on either.
        if (hands[Left].grabbing || hands[Right].grabbing)
        {
            input.emoteIsPressed = false;
        }

        // Pockets (Q / E, LB / RB). On a gamepad PEAK scrolls items with the same bumpers: an item that's scrolling
        // right now (open guidebook, binoculars up) keeps them; otherwise they're pockets only.
        bool pockets = true;
        if (Controls.OnGamepad)
        {
            if (ItemScrolling(character.data.currentItem))
            {
                pockets = false;
            }
            else
            {
                input.scrollBackwardWasPressed = input.scrollForwardWasPressed = false;
                input.scrollBackwardIsPressed = input.scrollForwardIsPressed = false;
            }
        }
        if (pockets && Controls.Down(Controls.LeftPocket))
        {
            TryPocket(left);
        }
        if (pockets && Controls.Down(Controls.RightPocket))
        {
            TryPocket(right);
        }
        if (Controls.Down(Controls.SwapHands))
        {
            TrySwapHands();
        }

        // Drop / throw (1 / 3, d-pad left / right). PEAK's own drop logic does the rest (tap drops, hold throws).
        if (!HandlePairThrow())
        {
            HandleDropKey(left, Controls.DropLeft);
            HandleDropKey(right, Controls.DropRight);
        }

        foreach (Hand hand in hands)
        {
            HandleHandButton(hand);
        }

        UpdateOffHandUse();
        if (useHand != null)
        {
            if (useFeeds)
            {
                input.useSecondaryWasPressed = useHand.down;
                input.useSecondaryIsPressed = useHand.held;
                input.useSecondaryWasReleased = useHand.up;
            }
            else
            {
                input.usePrimaryWasPressed = useHand.down;
                input.usePrimaryIsPressed = useHand.held;
                input.usePrimaryWasReleased = useHand.up;
            }
            if (!useHand.held)
            {
                useHand = null;
                useFeeds = false;
            }
        }

        if (left.interacting || right.interacting)
        {
            bool held = (left.interacting && left.held) || (right.interacting && right.held);
            input.interactWasPressed = !interactionActive;
            input.interactIsPressed = held;
            input.interactWasReleased = !held;
            interactionActive = held;
            foreach (Hand hand in hands)
            {
                if (hand.interacting && (!hand.held || !held))
                {
                    StopInteracting(hand);
                }
            }
        }
        else
        {
            interactionActive = false;
        }

        if (jump && Hanging)
        {
            input.jumpWasPressed = false; // PEAK's ground jump must not fire as well
            GrabJump();
        }
    }

    private void HandleHandButton(Hand hand)
    {
        bool reach = hand.held && hand.item == null && !hand.grabbing;
        if (reach != hand.reaching)
        {
            view.RPC(nameof(RPCA_PeakleReach), RpcTarget.All, (byte)hand.id, reach);
        }
        if (hand.grabbing)
        {
            if (!hand.held)
            {
                view.RPC(nameof(RPCA_PeakleRelease), RpcTarget.All, (byte)hand.id);
            }
            return;
        }
        if (HandleBackpackButton(hand))
        {
            return;
        }
        if (hand.down)
        {
            Hand other = hands[1 - hand.id];
            if (hand.item != null && IsCookingTarget(hand.item) && (!other.interacting || other.cooking))
            {
                // Cooking is one-handed: hold the item's hand on the fire. The other hand can join in to
                // cook its item at the same time; it's done when the fire's timer finishes.
                if (!other.cooking)
                {
                    SetActiveHand(hand);
                }
                else
                {
                    hand.item.GetComponent<ItemCooking>()?.StartCookingVisuals();
                }
                hand.interacting = true;
                hand.cooking = true;
                return;
            }
            if (hand.item != null)
            {
                // White Knuckle: clicking a hand that holds an item uses it.
                if (useHand == null && dropHand == null)
                {
                    SetActiveHand(hand);
                    useHand = hand;
                    // Looking at a teammate with something they can be given: feed / use it on them (PEAK's secondary use).
                    useFeeds = hand.item.canUseOnFriend && Interaction.instance != null && Interaction.instance.hasValidTargetCharacter;
                }
                else if (useHand != null && useHand != hand && offUseHand == null && CanUseOffHand(hand.item))
                {
                    // Akimbo eating: the other hand eats too, on its own.
                    offUseHand = hand;
                    hand.item.StartUsePrimary();
                    view.RPC(nameof(RPCA_PeakleOffUse), RpcTarget.All, (byte)hand.id, true);
                }
                return;
            }
            if (Interaction.instance != null && Interaction.instance.bestInteractable != null && !hands[1 - hand.id].interacting
                && !IsLitCampfire(Interaction.instance.bestInteractable) && !IsHoldInteractable(Interaction.instance.bestInteractable))
            {
                // (An empty hand never cooks: a lit fire is only for the hand holding what's cooked.)
                // Grab the thing to interact with it: picks items up into this hand, holds luggage open, etc.
                hand.interacting = true;
                SetPickupHand(hand.id);
                return;
            }
        }
        if (hand.held && !hand.interacting && hand.item == null)
        {
            TryGrab(hand);
        }
    }

    private bool interactionActive;

    private bool useFeeds;   // the hand in use feeds / uses its item on a teammate
    private Hand offUseHand; // the other hand, eating at the same time

    // Only things that get eaten / used up (and flares) can be used by the second hand at once: PEAK runs everything else
    // through the one item "in hand".
    private static bool CanUseOffHand(Item item)
    {
        return item != null && item.progress <= 0f && item.CanUsePrimary()
            && (item.GetComponent<Action_Consume>() != null || item.GetComponent<Action_ConsumeAndSpawn>() != null || item.GetComponent<Action_Flare>() != null);
    }

    [PunRPC]
    public void RPCA_PeakleOffUse(byte id, bool on)
    {
        hands[id].offUsing = on;
        hands[id].offUseStart = Time.time;
    }

    private void EndOffHandUse(Hand hand)
    {
        offUseHand = null;
        if (hand.offUsing)
        {
            view.RPC(nameof(RPCA_PeakleOffUse), RpcTarget.All, (byte)hand.id, false);
        }
    }

    /// <summary>How far through its use an item is, for the hand bringing it to the mouth (others only see the RPC).</summary>
    internal float UseProgress(Hand hand, Item item)
    {
        if (hand.offUsing && item.usingTimePrimary > 0f)
        {
            return view.IsMine ? item.progress : Mathf.Clamp01((Time.time - hand.offUseStart) / item.usingTimePrimary);
        }
        return item.progress;
    }

    private void UpdateOffHandUse()
    {
        Hand hand = offUseHand;
        if (hand == null)
        {
            return;
        }
        Item item = hand.item;
        if (item == null || !item.gameObject.activeInHierarchy)
        {
            EndOffHandUse(hand); // eaten
            return;
        }
        if (item == character.data.currentItem)
        {
            // The first hand finished and this one became the item in hand: PEAK carries on with it.
            EndOffHandUse(hand);
            if (hand.held && item.isUsingPrimary)
            {
                useHand = hand;
                useFeeds = false;
            }
            return;
        }
        if (!hand.held || !item.CanUsePrimary())
        {
            if (item.isUsingPrimary && !item.finishedCast)
            {
                item.CancelUsePrimary();
            }
            EndOffHandUse(hand);
            return;
        }
        item.ContinueUsePrimary();
    }

    /// <summary>Local: an item of this scout was eaten. PEAK only empties the slot of the item in hand; the other hand's too.</summary>
    internal void OnItemConsumed(Item item, bool wasInHand)
    {
        if (wasInHand)
        {
            return;
        }
        foreach (Hand hand in hands)
        {
            if (hand.item == item)
            {
                character.player.EmptySlot(Optionable<byte>.Some(hand.HandSlot));
                Reconcile();
                return;
            }
        }
    }

    /// <summary>The held item a teammate in view could be given: the one in use first, else the other hand's.</summary>
    internal Item FeedItem(out Hand feedHand)
    {
        Hand active = ActiveHand;
        foreach (Hand hand in active != null ? new[] { active, hands[1 - active.id] } : hands)
        {
            if (hand.item != null && hand.item.canUseOnFriend)
            {
                feedHand = hand;
                return hand.item;
            }
        }
        feedHand = null;
        return null;
    }

    /// <summary>Owner: a push from the world (wind, blasts, knock back, a teammate's pull) while White Knuckle physics run.</summary>
    internal void AddWorldPush(Vector3 deltaVelocity)
    {
        if (!view.IsMine || !body.Active)
        {
            return;
        }
        body.vel += deltaVelocity * Plugin.WorldPushStrength.Value / WkBody.Metres;
    }

    private void StopInteracting(Hand hand)
    {
        if (hand.cooking && hand.item != null && hand.item != character.data.currentItem)
        {
            hand.item.GetComponent<ItemCooking>()?.CancelCookingVisuals(); // a joined hand let go early
        }
        hand.interacting = false;
        hand.cooking = false;
    }

    /// <summary>Items on the fire right now: those of hands still held in a cooking interaction.</summary>
    internal System.Collections.Generic.IEnumerable<Item> CookingItems()
    {
        foreach (Hand hand in hands)
        {
            if (hand.cooking && hand.held && hand.item != null)
            {
                yield return hand.item;
            }
        }
    }

    private static bool IsCookingTarget(Item item)
    {
        return Interaction.instance != null && IsLitCampfire(Interaction.instance.bestInteractable) && CanCook(item);
    }

    internal static bool IsLitCampfire(IInteractible interactible) => interactible is Campfire fire && fire.Lit;

    internal static bool CanCook(Item item) => item != null && item.cooking != null && item.cooking.canBeCooked;

    /// <summary>Whether this hand can act on the currently hovered interactable (for the prompt icons).</summary>
    internal static bool CanInteractWith(Hand hand, IInteractible target)
    {
        if (IsLitCampfire(target))
        {
            return CanCook(hand.item);
        }
        return IsFree(hand);
    }

    private Hand pairHand; // pressed its drop key while the other hand was already dropping: both go together

    // An item that scrolls while in use (guidebook pages, binocular zoom, scout cannon), being used right now.
    private static bool ItemScrolling(Item item)
    {
        if (item == null)
        {
            return false;
        }
        bool scrolls = item.OnScrollBackwardPressed != null || item.OnScrollForwardPressed != null
            || item.OnScrollBackwardHeld != null || item.OnScrollForwardHeld != null;
        return scrolls && (item.isUsingPrimary || item is Guidebook { isOpen: true });
    }

    // Akimbo throwing: press the other hand's drop key while holding the first; both charge together and both
    // fly when either key is let go.
    private bool HandlePairThrow()
    {
        const string leftKey = Controls.DropLeft, rightKey = Controls.DropRight;
        CharacterInput input = character.input;
        if (pairHand == null)
        {
            if (dropHand == null)
            {
                return false;
            }
            Hand other = hands[1 - dropHand.id];
            string otherKey = other.id == Left ? leftKey : rightKey;
            if (!Controls.Held(otherKey) || other.item == null || !other.item.UIData.canDrop || other.item.progress > 0f || other.item is Backpack)
            {
                return false;
            }
            pairHand = other;
        }
        string a = dropHand.id == Left ? leftKey : rightKey;
        string b = pairHand.id == Left ? leftKey : rightKey;
        input.dropWasPressed = false;
        input.dropWasReleased = false;
        if (Controls.Held(a) && Controls.Held(b) && dropHand.item != null && pairHand.item != null)
        {
            input.dropIsPressed = true; // keep PEAK charging the throw
            return true;
        }
        input.dropIsPressed = false;
        CharacterItems items = character.refs.items;
        float charge = items.throwChargeLevel;
        ThrowFromHand(dropHand, charge);
        ThrowFromHand(pairHand, charge);
        items.throwChargeLevel = 0f;
        items.pressedDrop = false;
        if (charge > 0.1f && character.refs.animations != null)
        {
            character.refs.animations.throwTime = 0.125f;
        }
        items.EquipSlot(Optionable<byte>.None);
        dropHand = null;
        pairHand = null;
        return true;
    }

    // PEAK's drop (CharacterItems.DoDropping) for one hand: make it the item in hand, then drop / throw it.
    private void ThrowFromHand(Hand hand, float charge)
    {
        Item item = hand.item;
        ItemSlot slot = character.player.GetItemSlot(hand.HandSlot);
        if (item == null || slot == null || slot.IsEmpty())
        {
            return;
        }
        SetActiveHand(hand);
        if (character.data.currentItem != item)
        {
            return;
        }
        character.refs.items.photonView.RPC("DropItemRpc", RpcTarget.All, charge, hand.HandSlot, item.transform.position,
            item.rig != null ? item.rig.linearVelocity : Vector3.zero, item.transform.rotation, slot.data, false);
    }

    private void HandleDropKey(Hand hand, string key)
    {
        bool down = Controls.Down(key), held = Controls.Held(key), up = Controls.Up(key);
        if (TryDropHeldBackpack(hand, down))
        {
            return;
        }
        if (down && hand.item != null && dropHand == null && useHand == null)
        {
            SetActiveHand(hand);
            dropHand = hand;
        }
        if (dropHand != hand)
        {
            return;
        }
        CharacterInput input = character.input;
        input.dropWasPressed = down;
        input.dropIsPressed = held;
        input.dropWasReleased = up;
        if (!held)
        {
            dropHand = null;
        }
    }

    // ================================================================ grabbing

    private void TryGrab(Hand hand)
    {
        if (hand.grabWait > 0f || hand.grip <= 0.1f || character.data.currentClimbHandle != null
            || character.data.isRopeClimbing || character.data.isVineClimbing || character.data.carrier != null)
        {
            return;
        }
        if (TryGrabScout(hand) || TryGrabSpecial(hand))
        {
            return;
        }
        Vector3 from = MainCamera.instance.transform.position;
        Vector3 to = from + character.data.lookDirection * (Wk.InteractDistance * Scale);
        RaycastHit hit = HelperFunctions.LineCheck(from, to, HelperFunctions.LayerType.TerrainMap);
        if (!hit.transform)
        {
            hit = HelperFunctions.LineCheck(from, to, HelperFunctions.LayerType.TerrainMap, 0.15f * Scale);
        }
        // Anything PEAK would let you climb is a hold.
        if (!hit.transform || !character.refs.climbing.AcceptableGrabAngle(hit.normal, hit.collider))
        {
            return;
        }
        view.RPC(nameof(RPCA_PeakleGrab), RpcTarget.All, (byte)hand.id, hit.point, hit.normal, (byte)HoldKind.Surface);
        RememberSurface(hand, hit.collider);
        // PEAK: catching a wall mid-fall turns the fall into a slide down the wall that costs stamina.
        hand.kind = HoldKind.Surface;
        hand.anchor = null;
        float fall = -(body.Active ? body.VelocityMetres.y : AverageVelocity().y);
        hand.slide = Mathf.Max(0f, fall - Plugin.SlideMinFallSpeed.Value);
    }

    private static readonly RaycastHit[] grabHits = new RaycastHit[32];

    /// <summary>Ropes, vines, chains and pitons in front of the camera (and not behind a wall) are holds too.</summary>
    private bool TryGrabSpecial(Hand hand)
    {
        Vector3 from = MainCamera.instance.transform.position;
        Vector3 dir = character.data.lookDirection;
        float reach = Wk.InteractDistance * Scale;
        int n = Physics.SphereCastNonAlloc(from, 0.08f * Scale, dir, grabHits, reach, HelperFunctions.GetMask(HelperFunctions.LayerType.AllPhysical), QueryTriggerInteraction.Collide);
        System.Array.Sort(grabHits, 0, n, HitDistance.Instance);
        for (int i = 0; i < n; i++)
        {
            RaycastHit hit = grabHits[i];
            Collider c = hit.collider;
            if (c == null || character.refs.ragdoll.colliderList.Contains(c) || (hit.distance <= 0f && hit.point == Vector3.zero))
            {
                continue;
            }
            HoldKind? kind = SpecialKind(c);
            if (kind.HasValue)
            {
                Vector3 point = c.ClosestPoint(from + dir * hit.distance);
                hand.anchor = c.transform;
                hand.anchorLocal = c.transform.InverseTransformPoint(point);
                view.RPC(nameof(RPCA_PeakleGrab), RpcTarget.All, (byte)hand.id, point, -dir, (byte)kind.Value);
                RememberSurface(hand, c);
                return true;
            }
            if (!c.isTrigger && Item.TryGetItemFromCollider(c, out Item item) && item != null && (item == hands[Left].item || item == hands[Right].item))
            {
                continue;
            }
            if (!c.isTrigger)
            {
                return false; // a wall (or anything solid) is in the way
            }
        }
        return false;
    }

    private static HoldKind? SpecialKind(Collider c)
    {
        if (c.GetComponentInParent<RopeSegment>() != null || c.GetComponentInParent<Rope>() != null)
        {
            return HoldKind.Rope;
        }
        if (c.GetComponentInParent<JungleVine>() != null)
        {
            return HoldKind.Vine;
        }
        if (c.GetComponentInParent<ClimbHandle>() != null)
        {
            return HoldKind.Piton;
        }
        return null;
    }

    // Things hands deal with themselves instead of through PEAK's interaction: holds, and backpacks on backs (slot panel).
    internal static bool IsHoldInteractable(IInteractible target) => target is RopeSegment || target is JungleVine || target is ClimbHandle
        || target is BackpackOnBackVisuals || target is CharacterInteractible; // scouts: grabbed with hands, carried with Tab

    private class HitDistance : System.Collections.Generic.IComparer<RaycastHit>
    {
        public static readonly HitDistance Instance = new();
        public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
    }

    /// <summary>Owner: holds on moving things (ropes) follow them; other clients get the point a few times a second.</summary>
    private void FollowAnchors(float dt)
    {
        foreach (Hand hand in hands)
        {
            if (!hand.grabbing || hand.kind == HoldKind.Surface || hand.kind == HoldKind.Scout)
            {
                continue;
            }
            if (hand.anchor == null || !hand.anchor.gameObject.activeInHierarchy)
            {
                view.RPC(nameof(RPCA_PeakleRelease), RpcTarget.All, (byte)hand.id); // the rope or vine is gone
                continue;
            }
            Vector3 point = hand.anchor.TransformPoint(hand.anchorLocal);
            hand.anchorSync -= dt;
            if (hand.anchorSync <= 0f && (point - hand.point).sqrMagnitude > 0.0004f)
            {
                hand.anchorSync = 0.1f;
                view.RPC(nameof(RPCA_PeakleMoveHold), RpcTarget.Others, (byte)hand.id, point, hand.normal);
            }
            hand.point = point;
        }
    }

    // How much less grip a rope or vine costs than a wall, using PEAK's own stamina rates for each.
    private float GripCostMultiplier(Hand hand)
    {
        float wall = Mathf.Max(character.refs.climbing.maxStaminaUsage, 0.0001f);
        return hand.kind switch
        {
            HoldKind.Rope => Mathf.Clamp01(character.refs.ropeHandling.staminaUsageUp / wall),
            HoldKind.Vine => Mathf.Clamp01(character.refs.vineClimbing.staminaUsage / wall),
            _ => 1f,
        };
    }

    // White Knuckle ENT_Player.Jump while hanging: every hanging hand lets go and pays grip, and the
    // body is launched along the surface's "up" at White Knuckle's jump speed (jumpHeight * -gravity,
    // in WK velocity units). The flight that follows also runs on White Knuckle physics (see Fly).
    private void GrabJump()
    {
        float strain = HangingCount == 2 ? Wk.BothHandsJumpStrain : 1f;
        foreach (Hand hand in hands)
        {
            if (hand.grabbing)
            {
                hand.grip -= Wk.ClimbJumpDrain * strain;
            }
        }
        Vector3 jump = ClimbDir().normalized * (Wk.JumpHeight * -Wk.Gravity * Plugin.JumpStrength.Value);
        view.RPC(nameof(RPCA_PeakleGrabJump), RpcTarget.All, jump);
    }

    // ENT_Player.Hang: the direction "up the wall" from the last surface the body touched.
    private Vector3 ClimbDir()
    {
        if (BodyGrounded)
        {
            return Vector3.up;
        }
        Vector3 normal = body.hitNormal;
        Vector3 dir = body.hasHit ? Vector3.ProjectOnPlane(Vector3.up, normal).normalized : Vector3.up;
        if (body.hasHit && Vector3.Angle(Vector3.down, normal) < 10f)
        {
            return Vector3.down * 0.15f;
        }
        if (dir != Vector3.up)
        {
            dir += normal * 0.1f;
        }
        return dir;
    }

    /// <summary>Grounded as White Knuckle sees it while its body is in charge, PEAK's otherwise.</summary>
    private bool BodyGrounded => body.Active ? body.IsGrounded : character.data.isGrounded;

    // ================================================================ per-frame

    private void Update()
    {
        if (Disabled || !view.IsMine)
        {
            return;
        }
        if (Hanging && ShouldForceRelease())
        {
            view.RPC(nameof(RPCA_PeakleReleaseAll), RpcTarget.All);
        }
        foreach (Hand hand in hands)
        {
            hand.grabWait -= Time.deltaTime;
        }
        SurfaceEffects(Time.deltaTime);
        UpdateGrip(Time.deltaTime);
        reconcileTimer -= Time.deltaTime;
        if (reconcileTimer <= 0f)
        {
            Reconcile();
        }
        IInteractible hovered = Interaction.instance != null ? Interaction.instance.bestInteractable : null;
        int freeMask = (CanInteractWith(hands[Left], hovered) ? 1 : 0) | (CanInteractWith(hands[Right], hovered) ? 2 : 0);
        if (freeMask != lastFreeMask)
        {
            lastFreeMask = freeMask;
            PromptRefresher.RefreshInteract();
        }
    }

    internal static bool IsFree(Hand hand) => hand.item == null && !hand.grabbing;

    private int lastFreeMask = -1;

    private bool ShouldForceRelease()
    {
        CharacterData d = character.data;
        return d.dead || !d.fullyConscious || d.currentRagdollControll < 0.25f || d.carrier != null
            || d.isRopeClimbing || d.isVineClimbing || d.currentClimbHandle != null || d.isClimbing;
    }

    private const float SlipGrip = 0.1f; // White Knuckle: a hand with this little grip left lets go

    private float windCost = 1f;
    private float nextHazardTime;

    /// <summary>
    /// Owner: PEAK's surface and weather effects on holds, which PEAK applies through its own wall-crawl:
    /// special surfaces (ClimbModifierSurface: their status effects, slide-only, slippery, stamina cost), harmful
    /// surfaces (CollisionModifier: thorns, poison vines hurt the hand holding them, on their cooldown), and storms
    /// (wet holds slide, wind makes holding cost more).
    /// </summary>
    private void SurfaceEffects(float dt)
    {
        // Storm zones set this every frame while you're in them; PEAK resets it itself only while crawling.
        CharacterClimbing climbing = character.refs.climbing;
        windCost = Mathf.Max(climbing.climbingStamMinimumMultiplier, 1f);
        climbing.climbingStamMinimumMultiplier = 1f;

        float wet = Mathf.Clamp01(character.data.slippy);
        foreach (Hand hand in hands)
        {
            if (!hand.grabbing)
            {
                continue;
            }
            float slip = 0f;
            if (hand.surface != null)
            {
                hand.surface.OnClimb(character); // PEAK's own status effect, with its own cooldown
                slip += hand.surface.onlySlideDown ? Plugin.SlideOnlySpeed.Value : 0f;
                slip += Mathf.Max(hand.surface.slipModifier, 0f) * Plugin.WetSlideSpeed.Value;
            }
            if (hand.kind == HoldKind.Surface)
            {
                slip += wet * Plugin.WetSlideSpeed.Value;
                if (slip > 0f)
                {
                    hand.slide = Mathf.Max(hand.slide, slip);
                }
            }
            if (hand.hazard != null && hand.hazard.applyEffects && Time.time >= nextHazardTime)
            {
                // One timer for both hands, kept across grabs: letting go and grabbing again doesn't hurt sooner.
                nextHazardTime = Time.time + Mathf.Max(hand.hazard.cooldown, 0.5f);
                HurtHand(hand.hazard);
            }
        }
    }

    // What touching it with the body does (CollisionModifier.Collide), minus the knock back.
    private void HurtHand(CollisionModifier hazard)
    {
        CharacterAfflictions afflictions = character.refs.afflictions;
        foreach (CollisionMod mod in hazard.additionalMods)
        {
            afflictions.AddStatus(mod.statusType, mod.amount);
        }
        if (hazard.applyPetrify)
        {
            if (RunSettings.GetValue(RunSettings.SETTINGTYPE.Hazard_PetrifyingStones) != 0)
            {
                afflictions.AddPetrify(Mathf.RoundToInt(hazard.damage));
            }
        }
        else if (hazard.damage != 0f)
        {
            afflictions.AddStatus(hazard.statusType, hazard.damage);
        }
    }

    private static void RememberSurface(Hand hand, Collider c)
    {
        hand.surface = c != null ? c.GetComponentInParent<ClimbModifierSurface>() : null;
        hand.hazard = c != null ? c.GetComponentInParent<CollisionModifier>() : null;
    }

    private static float SurfaceCostMultiplier(Hand hand) => hand.surface != null ? Mathf.Max(hand.surface.staminaUsageMultiplier, 0f) : 1f;

    // White Knuckle ENT_Player.HandStamina. PEAK's extra (bonus) stamina is one pool shared by both hands:
    // a hanging hand that has run out of grip burns it to keep holding.
    private void UpdateGrip(float dt)
    {
        // PEAK's afflictions shrink the stamina bar; they shrink grip the same way.
        float max = Wk.GripStrengthTimer * character.GetMaxStamina();
        float lossRate = HangingCount == 2 ? 0.5f : 1f;
        bool grounded = BodyGrounded;
        foreach (Hand hand in hands)
        {
            if (character.infiniteStam)
            {
                hand.grip = max;
                continue;
            }
            if (hand.grabbing)
            {
                hand.holdTime += dt;
                if (grounded || hand.kind == HoldKind.Piton)
                {
                    hand.grip += dt * Wk.RegenWhileHangingGrounded; // pitons rest the hand like standing does
                }
                else
                {
                    hand.grip -= dt * lossRate * GripCostMultiplier(hand) * SurfaceCostMultiplier(hand) * windCost;
                    // Spent hand: the shared extra pool keeps it holding for as long as it lasts.
                    float deficit = SlipGrip + 0.05f - hand.grip;
                    float extra = character.data.extraStamina * Wk.GripStrengthTimer;
                    if (deficit > 0f && extra > 0f)
                    {
                        float burned = Mathf.Min(extra, deficit);
                        character.AddExtraStamina(-burned / Wk.GripStrengthTimer);
                        hand.grip += burned;
                    }
                }
            }
            else
            {
                hand.holdTime = 0f;
                if (grounded)
                {
                    hand.grip += dt * Wk.RegenGrounded;
                }
                else if (hand.regenWait <= 0f)
                {
                    hand.grip += dt * Wk.RegenAirborne;
                }
                hand.regenWait -= dt;
            }
            hand.grip = Mathf.Clamp(hand.grip, 0f, max);
            if (hand.grabbing && hand.grip <= SlipGrip && hand.grabWait < 0.1f)
            {
                view.RPC(nameof(RPCA_PeakleRelease), RpcTarget.All, (byte)hand.id);
                hand.grabWait = 1f; // a slipped hand can't regrab for a second
            }
        }
    }

    private void FixedUpdate()
    {
        if (Disabled)
        {
            return;
        }
        float dt = Time.fixedDeltaTime;
        PlaceHandItems(physicsStep: true);
        FollowScoutHolds();
        if (!view.IsMine)
        {
            foreach (Hand hand in hands)
            {
                if (hand.grabbing)
                {
                    hand.holdTime += dt;
                }
            }
            if (wkFlight && (Hanging || character.data.isGrounded || !character.data.fullyConscious))
            {
                wkFlight = false;
            }
            FollowRemoteWkBody(dt);
            return;
        }

        if (wkFlight && (!character.data.fullyConscious || character.data.isRopeClimbing || character.data.isVineClimbing
            || character.data.currentClimbHandle != null || character.data.isClimbing))
        {
            wkFlight = false;
        }
        ApplyDrags(dt);
        // Stuck to something (cactus thorns, ...): PEAK's own physics hold you there and push you off.
        bool wanted = (Hanging || wkFlight) && !character.IsStuck();
        if (wanted && !body.Active)
        {
            body.Enter(AverageVelocity());
        }
        else if (!wanted && body.Active)
        {
            ExitBody();
        }
        if (body.Active != sentWk)
        {
            sentWk = body.Active;
            view.RPC(nameof(RPCA_PeakleWkBody), RpcTarget.Others, sentWk);
            Plugin.Log.LogInfo($"WK physics {(sentWk ? "on" : "off")}: left {HoldName(hands[Left])}, right {HoldName(hands[Right])}, "
                + $"flight {wkFlight}, grounded {character.data.isGrounded}, crouching {character.data.isCrouching}");
        }
        if (!body.Active)
        {
            return;
        }
        if (Hanging)
        {
            landedOnWk = false; // a new fall starts from here
        }

        // White Knuckle runs in its own, paced time; everything else (grip, slides) in real time.
        float wkDt = dt * Plugin.Pace.Value;
        if (Hanging)
        {
            wkFlight = false;
            FollowAnchors(dt);
            SlideHolds(dt);
            Hang(wkDt);
        }
        else
        {
            Fly(wkDt);
        }
        body.SetCrouched(character.input.crouchIsPressed);
        Vector3 beforeMove = body.VelocityMetres;
        body.Move(wkDt);
        body.DriveRagdoll(dt);

        if (Hanging)
        {
            // Hanging counts as "just grounded" to PEAK: the scout keeps its upright standing pose, the
            // fall-gravity ramp restarts when you let go, and hanging time never counts as falling.
            character.data.sinceGrounded = 0f;
            character.data.sinceJump = 0f;
            character.data.isJumping = false;
        }
        else if (body.IsGrounded)
        {
            wkFlight = false; // landed: PEAK walks again
            LandOnWk(beforeMove);
            ExitBody();
        }
    }

    private static string HoldName(Hand hand) => hand.grabbing ? hand.kind.ToString() : "free";

    private void ExitBody()
    {
        Vector3 v = body.VelocityMetres;
        body.Exit();
        AddBodyVelocity(v - AverageVelocity());
    }

    private void OnDestroy()
    {
        body?.Destroy();
    }

    private void LateUpdate()
    {
        if (Disabled)
        {
            return;
        }
        PlaceHandItems(physicsStep: false);
        ArmVisuals.Stretch(character, this);
        if (view.IsMine)
        {
            HostSettings.Update();
            HandHud.Update(character, this);
        }
    }

    /// <summary>True from a grab jump until landing or grabbing again: the body flies on White Knuckle physics.</summary>
    internal bool wkFlight;

    // Camera hand-over between PEAK's head camera and the White Knuckle capsule camera (see the CharacterCam patch).
    internal float camWeight;
    internal Vector3 camOffsetFromHip;

    /// <summary>Local player only: where the camera goes while the White Knuckle body is in charge, or null.</summary>
    internal Vector3? WkEyePosition => view.IsMine && body.Active ? body.EyePosition(character.data.lookDirection_Flat) : null;

    /// <summary>White Knuckle's physics are in charge (PEAK's gravity, drag, standing and movement are off).</summary>
    // Owner: own state. Others: what the owner last said (their own guess drifts, e.g. after a landing they missed).
    public bool OnWkPhysics => view.IsMine ? Hanging || wkFlight : remoteWk;

    private bool remoteWk;     // others: the owner's scout is on White Knuckle physics
    private bool sentWk;       // owner: what others were last told
    private bool landedOnWk;   // owner: PEAK already ran its landing (fall damage) during this White Knuckle flight
    private CharacterSyncer syncer;

    [PunRPC]
    public void RPCA_PeakleWkBody(bool active)
    {
        remoteWk = active;
    }

    /// <summary>
    /// Others: while the owner's scout runs on White Knuckle physics (no PEAK gravity or standing), PEAK's own sync
    /// only nudges height when it's 0.3 m off, so the body floated. Pull it onto the owner's synced hip instead.
    /// </summary>
    private void FollowRemoteWkBody(float dt)
    {
        if (!remoteWk || character.data.carrier != null)
        {
            return;
        }
        syncer ??= character.GetComponent<CharacterSyncer>();
        if (syncer == null || syncer.RemoteValue.IsNone)
        {
            return;
        }
        Vector3 velocity = character.data.avarageVelocity;
        Vector3 target = (Vector3)syncer.RemoteValue.Value.hipLocation + velocity * Mathf.Min(syncer.sinceLastPackageUpdate, 0.25f);
        Vector3 hip = character.GetBodypart(BodypartType.Hip).Rig.position;
        character.refs.ragdoll.MoveAllRigsInDirection((target - hip) * Mathf.Clamp01(dt * 15f));
        foreach (Bodypart part in character.refs.ragdoll.partList)
        {
            if (!part.Rig.isKinematic)
            {
                part.Rig.linearVelocity = velocity;
            }
        }
    }

    /// <summary>
    /// Owner: PEAK's landing (fall damage) when it happens on White Knuckle physics. PEAK measures the landing speed
    /// from the ragdoll, which the capsule has already stopped, so it's given the speed from just before the impact.
    /// </summary>
    internal void BeforePeakLanding()
    {
        if (!view.IsMine || !body.Active)
        {
            return;
        }
        Vector3 v = body.VelocityMetres;
        character.data.avarageLastFrameVelocity = v;
        character.data.avarageVelocity = v;
        landedOnWk = true;
    }

    private void LandOnWk(Vector3 impact)
    {
        if (landedOnWk)
        {
            return;
        }
        landedOnWk = true;
        character.data.avarageLastFrameVelocity = impact;
        character.data.avarageVelocity = impact;
        character.refs.movement.Land();
        character.data.sinceGrounded = 0f; // landed: PEAK's own landing must not count this fall again
    }

    // ================================================================ catching a fall

    /// <summary>
    /// Owner: a hold caught while falling slides down the surface, slowing down, and the sliding costs that hand
    /// grip per metre (PEAK's fall-catch slide). It lets go if it slides off the surface.
    /// </summary>
    private void SlideHolds(float dt)
    {
        foreach (Hand hand in hands)
        {
            if (!hand.grabbing || hand.slide <= 0f)
            {
                continue;
            }
            Vector3 down = Vector3.ProjectOnPlane(Vector3.down, hand.normal);
            if (down.sqrMagnitude < 0.01f)
            {
                hand.slide = 0f; // flat ceiling or floor: nothing to slide down
                continue;
            }
            float step = hand.slide * dt;
            Vector3 next = hand.point + down.normalized * step;
            RaycastHit hit = HelperFunctions.LineCheck(next + hand.normal * 0.3f, next - hand.normal * 0.5f, HelperFunctions.LayerType.TerrainMap);
            if (!hit.transform || !character.refs.climbing.AcceptableGrabAngle(hit.normal, hit.collider))
            {
                view.RPC(nameof(RPCA_PeakleRelease), RpcTarget.All, (byte)hand.id);
                continue;
            }
            hand.point = hit.point;
            hand.normal = hit.normal;
            hand.grip -= step * Plugin.SlideGripPerMetre.Value;
            hand.slide = Mathf.MoveTowards(hand.slide * Mathf.Pow(Plugin.SlideDamping.Value, dt), 0f, Plugin.SlideDeceleration.Value * dt);
            hand.slideSync -= dt;
            if (hand.slideSync <= 0f || hand.slide <= 0f)
            {
                hand.slideSync = 0.1f;
                view.RPC(nameof(RPCA_PeakleMoveHold), RpcTarget.Others, (byte)hand.id, hand.point, hand.normal);
            }
        }
    }

    // ================================================================ White Knuckle movement

    // ENT_Player.Movement, airborne and not hanging: gravity, air control, quadratic drag.
    private void Fly(float dt)
    {
        CharacterData d = character.data;
        Vector3 vel = body.vel;
        if (!body.hasHit || body.hitTime <= 0f)
        {
            body.hitNormal = Vector3.up;
        }
        vel += Vector3.up * (Wk.Gravity * dt);
        Vector2 input = character.input.movementInput;
        Vector3 moveAxis = Vector3.ClampMagnitude(d.lookDirection_Flat.normalized * input.y + d.lookDirection_Right * input.x, 1f);
        vel += moveAxis * (Wk.Speed * Wk.AirControl) * dt * 0.15f;
        vel = ApplyDrag(vel, Wk.DragCoefficient, dt);
        body.vel = vel;
    }

    /// <summary>
    /// ENT_Player.Movement + Hang for a hanging player, on White Knuckle's body velocity.
    /// </summary>
    private void Hang(float dt)
    {
        CharacterData d = character.data;
        float s = Scale;
        Vector3 center = body.Position;
        Vector2 input = character.input.movementInput;
        Vector3 camForward = d.lookDirection;
        Vector3 camRight = d.lookDirection_Right;
        bool grounded = body.IsGrounded;
        bool crouch = character.input.crouchIsPressed;
        Vector3 vel = body.vel;

        vel += Vector3.up * (Wk.Gravity * dt);

        // PEAK climb speed bonuses (energy drink) act like White Knuckle's "addClimb" buff.
        float addClimb = Mathf.Max(character.refs.climbing.climbSpeedMod - 1f, 0f);

        float curDrag = Wk.DragCoefficient;
        if (grounded)
        {
            vel.y = Mathf.Max(vel.y, -0.05f);
            curDrag *= input.magnitude > Mathf.Epsilon ? 15f : Wk.FrictionMultiplier;
        }
        else if (body.hitTime > 0f)
        {
            // White Knuckle: hanging against a wall is grippy, crouching against it grips hard.
            curDrag *= crouch ? Wk.WallFrictionMultiplier * 30f : Wk.WallFrictionMultiplier * 3f;
            if (crouch)
            {
                vel = Vector3.Lerp(vel, Vector3.zero, dt * 5f);
            }
        }

        foreach (Hand hand in hands)
        {
            if (hand.grabbing && (hand.kind == HoldKind.Rope || hand.kind == HoldKind.Vine))
            {
                // White Knuckle handholds carry a drag multiplier (Hang: curDrag = max(curDrag, drag * dragMult)).
                curDrag = Mathf.Max(curDrag, Wk.DragCoefficient * Plugin.RopeDrag.Value);
            }
        }

        Vector3 climbDir = ClimbDir();
        Vector3 moveAxis;
        bool wallAhead = Physics.Raycast(MainCamera.instance.transform.position, d.lookDirection_Flat.normalized,
            Wk.ControllerRadius * 2.5f * s, HelperFunctions.GetMask(HelperFunctions.LayerType.TerrainMap));
        if (wallAhead)
        {
            moveAxis = camRight * (input.x * Wk.SideMove) + input.y * Vector3.Lerp(camForward, -climbDir, 0.3f).normalized;
        }
        else
        {
            moveAxis = camRight * (input.x * Wk.SideMove) + input.y * (camForward + climbDir);
        }

        float reach = Wk.InteractDistance;
        Vector3 pull = Vector3.zero;
        float lookUp = Vector3.Dot(camForward, Vector3.up);
        foreach (Hand hand in hands)
        {
            if (!hand.grabbing)
            {
                continue;
            }
            Vector3 toHand = (hand.point - center) / s;
            float magnitude = toHand.magnitude;
            toHand = magnitude <= Wk.HangSlack ? Vector3.zero : toHand.normalized * (magnitude - Wk.HangSlack);
            if (magnitude > reach * 1.25f)
            {
                toHand *= 4f;
                if (magnitude > reach * 2f)
                {
                    view.RPC(nameof(RPCA_PeakleRelease), RpcTarget.All, (byte)hand.id);
                }
            }
            pull += toHand;
            if (body.hitNormal != Vector3.zero)
            {
                pull += body.hitNormal * 0.09f;
            }

            float climb = lookUp >= 0f
                ? Mathf.Clamp(input.y * 0.015f, 0f, 0.02f)
                : Mathf.Clamp(Mathf.Lerp(input.y, -input.y, Mathf.Abs(lookUp)) * 0.015f, 0f, 0.02f);
            if (lookUp > 0.5f)
            {
                climb *= 1f + addClimb;
            }
            if (hand.surface != null)
            {
                climb *= hand.surface.onlySlideDown ? 0f : Mathf.Max(hand.surface.speedMultiplier, 0f);
            }
            vel.x += climbDir.x * climb * dt;
            vel.y = Mathf.Lerp(vel.y, climb * climbDir.y, dt * 8f * Mathf.Clamp(hand.holdTime, 0f, 2f));
            vel.z += climbDir.z * climb * dt;
            if (hand.slide > 0f)
            {
                // A sliding hold carries the body down the wall with it.
                Vector3 slideVel = Vector3.ProjectOnPlane(Vector3.down, hand.normal).normalized * (hand.slide / WkBody.Metres);
                vel = Vector3.Lerp(vel, slideVel, dt * 10f);
            }
            // White Knuckle: crouching with no movement input holds you still.
            if (moveAxis.magnitude < 0.05f && crouch)
            {
                vel.y = 0f;
                vel = Vector3.Lerp(vel, Vector3.zero, dt * 5f);
            }
        }
        if (grounded)
        {
            climbDir = Vector3.up;
        }

        vel += (moveAxis * (Wk.Speed * (1f + addClimb)) + pull * 1.5f) * dt * 0.15f;
        vel = ApplyDrag(vel, curDrag, dt);
        body.vel = vel;
    }

    private static Vector3 ApplyDrag(Vector3 vel, float drag, float dt)
    {
        Vector3 force = -vel.normalized * (drag * vel.sqrMagnitude);
        return vel + Vector3.ClampMagnitude(force * dt, vel.magnitude / 2f);
    }

    private Vector3 AverageVelocity()
    {
        Vector3 sum = Vector3.zero;
        int count = 0;
        foreach (Bodypart part in character.refs.ragdoll.partList)
        {
            if (!part.Rig.isKinematic)
            {
                sum += part.Rig.linearVelocity;
                count++;
            }
        }
        return count > 0 ? sum / count : Vector3.zero;
    }

    private void AddBodyVelocity(Vector3 delta)
    {
        foreach (Bodypart part in character.refs.ragdoll.partList)
        {
            if (!part.Rig.isKinematic)
            {
                part.Rig.linearVelocity += delta;
            }
        }
    }

    // ================================================================ items

    /// <summary>Owner: make the slots and the spawned hand items agree.</summary>
    internal void Reconcile()
    {
        reconcileTimer = 0.25f;
        if (Disabled || !view.IsMine || character.player == null || Time.time < swapGraceUntil)
        {
            return;
        }
        foreach (Hand hand in hands)
        {
            ItemSlot slot = character.player.GetItemSlot(hand.HandSlot);
            bool hasData = slot != null && !slot.IsEmpty();
            if (hand.spawnPending)
            {
                continue;
            }
            if (hasData && (hand.item == null || hand.item.itemID != slot.prefab.itemID))
            {
                if (hand.item != null)
                {
                    DespawnHandItem(hand);
                }
                SpawnHandItem(hand, slot);
            }
            else if (!hasData && hand.item != null)
            {
                DespawnHandItem(hand);
            }
        }
        if (ActiveHand == null && character.data.currentItem == null)
        {
            Hand fallback = hands[Left].item != null ? hands[Left] : hands[Right].item != null ? hands[Right] : null;
            if (fallback != null)
            {
                SetActiveHand(fallback);
            }
        }
    }

    private void SpawnHandItem(Hand hand, ItemSlot slot)
    {
        Vector3 pos = character.GetBodypartRig(hand.Bodypart).position;
        PhotonView itemView = PhotonNetwork.Instantiate("0_Items/" + slot.GetPrefabName(), pos, Quaternion.LookRotation(character.data.lookDirection), 0).GetComponent<PhotonView>();
        itemView.RPC("SetItemInstanceDataRPC", RpcTarget.All, slot.data);
        hand.spawnPending = true;
        view.RPC(nameof(RPCA_PeakleHandItem), RpcTarget.All, (byte)hand.id, itemView.ViewID);
    }

    private void DespawnHandItem(Hand hand)
    {
        Item item = hand.item;
        view.RPC(nameof(RPCA_PeakleHandItem), RpcTarget.All, (byte)hand.id, -1);
        if (item != null && item.photonView.IsMine)
        {
            item.OnStash();
            PhotonNetwork.Destroy(item.gameObject);
        }
    }

    internal void SetActiveHand(Hand hand)
    {
        if (hand.item == null || character.data.currentItem == hand.item)
        {
            return;
        }
        Item current = character.data.currentItem;
        if (current != null && (current.progress > 0f || current.isUsingPrimary))
        {
            return; // don't switch mid-use
        }
        view.RPC(nameof(RPCA_PeakleSetActive), RpcTarget.All, (byte)hand.id);
    }

    // White Knuckle Inventory.TryPocketItemInHand: an empty hand takes the item out of its pocket, a full hand
    // puts its item in an empty pocket. Items are never swapped.
    private void TryPocket(Hand hand)
    {
        if (hand.item is Backpack)
        {
            return; // backpacks and fanny packs go on your back, not in a pocket
        }
        Player player = character.player;
        ItemSlot handSlot = player.GetItemSlot(hand.HandSlot);
        ItemSlot pocket = player.GetItemSlot(hand.PocketSlot);
        if (!handSlot.IsEmpty() && pocket.IsEmpty())
        {
            if (hand.item != null && (hand.item.progress > 0f || !hand.item.UIData.canPocket))
            {
                return;
            }
            view.RPC(nameof(RPCM_PeakleMoveSlots), RpcTarget.MasterClient, hand.HandSlot, hand.PocketSlot, false);
        }
        else if (handSlot.IsEmpty() && !pocket.IsEmpty() && !hand.grabbing)
        {
            view.RPC(nameof(RPCM_PeakleMoveSlots), RpcTarget.MasterClient, hand.PocketSlot, hand.HandSlot, false);
        }
    }

    // White Knuckle's Hand-Swap: the two hands trade items, without respawning them.
    private void TrySwapHands()
    {
        Hand left = hands[Left];
        Hand right = hands[Right];
        if ((left.item == null && right.item == null) || left.spawnPending || right.spawnPending || left.grabbing || right.grabbing)
        {
            return;
        }
        Item current = character.data.currentItem;
        if (current != null && (current.progress > 0f || current.isUsingPrimary))
        {
            return;
        }
        swapGraceUntil = Time.time + 1f;
        view.RPC(nameof(RPCA_PeakleSwapHands), RpcTarget.All);
        view.RPC(nameof(RPCM_PeakleMoveSlots), RpcTarget.MasterClient, left.HandSlot, right.HandSlot, true);
    }

    private void SetPickupHand(int id)
    {
        pickupHand = id;
        if (!PhotonNetwork.IsMasterClient)
        {
            view.RPC(nameof(RPCM_PeakleSetPickupHand), RpcTarget.MasterClient, (byte)id);
        }
    }

    /// <summary>
    /// All clients: each hand item is glued to its hand, with its grip point in the palm and facing where the
    /// scout looks. Done every physics step and again every rendered frame from the interpolated hand, so the
    /// item moves exactly with the visible hand.
    /// </summary>
    private void PlaceHandItems(bool physicsStep)
    {
        Quaternion rot = Quaternion.LookRotation(character.data.lookDirection, character.data.lookDirection_Up);
        foreach (Hand hand in hands)
        {
            Item item = hand.item;
            if (item == null)
            {
                continue;
            }
            if (!item.rig.isKinematic)
            {
                MakeCarried(item);
            }
            if (physicsStep)
            {
                if (hand.collidersOf != item)
                {
                    hand.collidersOf = item;
                    hand.colliders = item.GetComponentsInChildren<Collider>(true);
                }
                foreach (Collider c in hand.colliders)
                {
                    if (c != null && c.enabled && !c.isTrigger)
                    {
                        c.enabled = false; // some items switch their own colliders back on (guidebook on close)
                    }
                }
            }
            Rigidbody handRig = character.GetBodypartRig(hand.Bodypart);
            Vector3 handPos = physicsStep ? handRig.position : handRig.transform.position;
            Vector3 pos = handPos - rot * GripOffset(hand, item);
            if (physicsStep)
            {
                item.rig.MovePosition(pos);
                item.rig.MoveRotation(rot);
            }
            else
            {
                item.transform.SetPositionAndRotation(pos, rot);
            }
        }
    }

    // Where the item's grip for this hand sits, relative to the item, in item-rotation space.
    private static Vector3 GripOffset(Hand hand, Item item)
    {
        Transform grip = item.transform.Find(hand.GripName) ?? item.transform.Find("Hand_R");
        return grip != null ? Vector3.Scale(grip.localPosition, item.transform.lossyScale) : Vector3.zero;
    }

    /// <summary>
    /// World position the hand reaches for while holding an item: the item's grip, with the item placed where
    /// PEAK would hold it (CharacterItems.GetItemHoldPos, including use animations that move defaultPos),
    /// shifted to this hand's side.
    /// </summary>
    internal Vector3 ItemGripTarget(Hand hand, Item item)
    {
        CharacterData d = character.data;
        Transform look = character.refs.animationLookTransform;
        Vector3 anim = look.TransformPoint(item.defaultPos) + d.lookDirection_Right * (hand.Side * Plugin.ItemSideOffset.Value);
        Vector3 itemPos = character.refs.hip.Rig.position + (anim - character.refs.animationHipTransform.position);
        Quaternion rot = Quaternion.LookRotation(d.lookDirection, d.lookDirection_Up);
        return itemPos + rot * GripOffset(hand, item);
    }

    /// <summary>The hand whose item is being used (eaten, drunk, played...), or null.</summary>
    internal Hand UsingHand
    {
        get
        {
            Hand active = ActiveHand;
            if (active == null)
            {
                return null;
            }
            Item item = active.item;
            return item.isUsingPrimary || item.progress > 0f || character.data.overrideIKForSeconds > 0f ? active : null;
        }
    }

    /// <summary>
    /// While its item is in use, the using hand brings it towards the mouth over the use time (PEAK animates
    /// this on both arms; here only the using arm moves).
    /// </summary>
    internal Vector3 UseTarget(Hand hand, Item item)
    {
        Vector3 grip = ItemGripTarget(hand, item);
        if (item.usingTimePrimary <= 0f)
        {
            return grip;
        }
        CharacterData d = character.data;
        Vector3 head = character.GetBodypart(BodypartType.Head).Rig.position;
        Vector3 mouth = head + d.lookDirection * 0.3f - d.lookDirection_Up * 0.15f + d.lookDirection_Right * (hand.Side * 0.05f);
        float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(UseProgress(hand, item) * 3f));
        return Vector3.Lerp(grip, mouth, t);
    }

    /// <summary>Where an empty, idle arm rests while the other hand uses an item (so it doesn't play the use animation).</summary>
    internal Vector3 RestTarget(Hand hand)
    {
        CharacterData d = character.data;
        return character.Center + d.lookDirection_Right * (hand.Side * 0.3f) - Vector3.up * 0.45f + d.lookDirection_Flat.normalized * 0.05f;
    }

    /// <summary>World position an empty hand reaches for while its button is held (like PEAK's RMB reach).</summary>
    internal Vector3 ReachTarget(Hand hand)
    {
        CharacterData d = character.data;
        Vector3 head = character.GetBodypart(BodypartType.Head).Rig.position;
        return head + d.lookDirection * 0.75f + d.lookDirection_Right * (hand.Side * 0.18f) - d.lookDirection_Up * 0.1f;
    }

    private static void MakeCarried(Item item)
    {
        item.SetKinematic(true);
        item.SetColliders(false, false, true);
        item.rig.interpolation = RigidbodyInterpolation.None;
    }

    // ================================================================ RPCs (run on every client)

    [PunRPC]
    public void RPCA_PeakleGrab(byte id, Vector3 point, Vector3 normal, byte kind)
    {
        Hand hand = hands[id];
        hand.kind = (HoldKind)kind;
        if (hand.kind != HoldKind.Surface)
        {
            hand.slide = 0f;
        }
        hand.grabbing = true;
        hand.point = point;
        hand.normal = normal;
        hand.holdTime = 0f;
    }

    [PunRPC]
    public void RPCA_PeakleReach(byte id, bool reaching)
    {
        hands[id].reaching = reaching;
    }

    [PunRPC]
    public void RPCA_PeakleMoveHold(byte id, Vector3 point, Vector3 normal)
    {
        Hand hand = hands[id];
        if (hand.grabbing)
        {
            hand.point = point;
            hand.normal = normal;
        }
    }

    [PunRPC]
    public void RPCA_PeakleRelease(byte id)
    {
        ReleaseHand(hands[id]);
    }

    [PunRPC]
    public void RPCA_PeakleReleaseAll()
    {
        ReleaseHand(hands[Left]);
        ReleaseHand(hands[Right]);
    }

    /// <summary>
    /// Let go with every hand and jump, exactly like White Knuckle's ENT_Player.Jump: downward speed is zeroed,
    /// vertical speed becomes at least the jump's, horizontal jump speed is added. <paramref name="jump"/> is in
    /// White Knuckle velocity units. Costs grip, not stamina.
    /// </summary>
    [PunRPC]
    public void RPCA_PeakleGrabJump(Vector3 jump)
    {
        ReleaseHand(hands[Left]);
        ReleaseHand(hands[Right]);
        CharacterData d = character.data;
        d.isJumping = true;
        d.sinceJump = 0f;
        d.sinceGrounded = 0f;
        landedOnWk = false;
        character.OnStartJump();
        character.OnJump();
        wkFlight = true;
        if (!view.IsMine)
        {
            return;
        }
        if (!body.Active)
        {
            body.Enter(AverageVelocity());
        }
        Vector3 vel = body.vel;
        if (vel.y < 0f)
        {
            vel.y = 0f;
        }
        vel.y = Mathf.Max(vel.y, jump.y);
        vel.x += jump.x;
        vel.z += jump.z;
        body.vel = vel;
    }

    [PunRPC]
    public void RPCA_PeakleHandItem(byte id, int viewID)
    {
        Hand hand = hands[id];
        hand.spawnPending = false;
        Item old = hand.item;
        if (old != null && character.data.currentItem == old)
        {
            ClearCurrentItem();
        }
        hand.item = null;
        if (viewID >= 0)
        {
            PhotonView itemView = PhotonNetwork.GetPhotonView(viewID);
            Item item = itemView != null ? itemView.GetComponent<Item>() : null;
            if (item != null)
            {
                hand.item = item;
                item.SetState(ItemState.Held, character);
                item.holderCharacter = character;
                MakeCarried(item);
                if (character.data.currentItem == null)
                {
                    PointCurrentItemAt(hand);
                }
            }
        }
        character.player?.itemsChangedAction?.Invoke(character.player.itemSlots);
    }

    [PunRPC]
    public void RPCA_PeakleSetActive(byte id)
    {
        Hand hand = hands[id];
        if (hand.item != null && character.data.currentItem != hand.item)
        {
            PointCurrentItemAt(hand);
        }
    }

    [PunRPC]
    public void RPCA_PeakleSwapHands()
    {
        Hand left = hands[Left];
        Hand right = hands[Right];
        (left.item, right.item) = (right.item, left.item);
        Hand active = ActiveHand;
        if (active != null)
        {
            PointCurrentItemAt(active); // its slot changed
        }
        else if (character.data.currentItem != null)
        {
            ClearCurrentItem();
        }
        if (character.data.currentItem == null)
        {
            Hand any = left.item != null ? left : right.item != null ? right : null;
            if (any != null)
            {
                PointCurrentItemAt(any);
            }
        }
    }

    private void PointCurrentItemAt(Hand hand)
    {
        CharacterItems items = character.refs.items;
        items.currentSelectedSlot = Optionable<byte>.Some(hand.HandSlot);
        items.lastSelectedSlot = items.currentSelectedSlot;
        character.data.currentItem = hand.item;
        hand.item.holderCharacter = character;
        character.player?.itemsChangedAction?.Invoke(character.player.itemSlots);
        items.onSlotEquipped?.Invoke();
        if (view.IsMine)
        {
            LocalActiveHandChanged?.Invoke();
        }
    }

    private void ClearCurrentItem()
    {
        character.data.currentItem = null;
        character.refs.items.currentSelectedSlot = Optionable<byte>.None;
    }

    [PunRPC]
    public void RPCM_PeakleSetPickupHand(byte id)
    {
        pickupHand = id;
    }

    /// <summary>Master: move an item between two of this player's slots, or swap them, and broadcast.</summary>
    [PunRPC]
    public void RPCM_PeakleMoveSlots(byte from, byte to, bool swap)
    {
        if (!PhotonNetwork.IsMasterClient || character.player == null)
        {
            return;
        }
        Player player = character.player;
        ItemSlot a = player.GetItemSlot(from);
        ItemSlot b = player.GetItemSlot(to);
        if (a == null || b == null)
        {
            return;
        }
        if (swap)
        {
            Item prefabA = a.IsEmpty() ? null : a.prefab;
            ItemInstanceData dataA = a.data;
            Item prefabB = b.IsEmpty() ? null : b.prefab;
            ItemInstanceData dataB = b.data;
            a.EmptyOut();
            b.EmptyOut();
            if (prefabB != null)
            {
                a.SetItem(prefabB, dataB);
            }
            if (prefabA != null)
            {
                b.SetItem(prefabA, dataA);
            }
        }
        else
        {
            if (a.IsEmpty() || !b.IsEmpty())
            {
                return;
            }
            if (b is BackpackSlot back)
            {
                // Putting on a backpack from a hand (Tab): the back slot needs to know what kind it is.
                if (a.prefab is not Backpack backpack)
                {
                    return;
                }
                back.backpackType = backpack.backpackType;
            }
            Item prefab = a.prefab;
            if (prefab == null && a is BackpackSlot fromBack && ItemDatabase.TryGetItem(fromBack.GetPrefabName(), out Item backpackPrefab))
            {
                prefab = backpackPrefab; // the back slot may only know its backpack by name
            }
            if (prefab == null)
            {
                return;
            }
            b.SetItem(prefab, a.data);
            a.EmptyOut();
        }
        byte[] sync = IBinarySerializable.ToManagedArray(new InventorySyncData(player.itemSlots, player.backpackSlot, player.tempFullSlot));
        player.photonView.RPC("SyncInventoryRPC", RpcTarget.Others, sync, false);
        player.itemsChangedAction?.Invoke(player.itemSlots);
        if (view.IsMine)
        {
            swapGraceUntil = 0f;
            Reconcile();
        }
    }

    internal void OnInventorySynced()
    {
        swapGraceUntil = 0f;
        Reconcile();
    }

    private void ReleaseHand(Hand hand)
    {
        if (!hand.grabbing)
        {
            return;
        }
        hand.grabbing = false;
        hand.holdTime = 0f;
        hand.slide = 0f;
        hand.target = null;
        hand.targetPart = null;
        hand.grabWait = Mathf.Max(hand.grabWait, 0f) + 0.25f; // White Knuckle StopInteraction: grabWait += 0.25
        hand.regenWait = 0.17f;                                 // and SetRegenWait(0.17)
        hand.surface = null;
        hand.hazard = null;
    }

    // ================================================================ temporary HUD

}
