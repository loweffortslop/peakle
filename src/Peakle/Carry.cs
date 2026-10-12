using Photon.Pun;
using UnityEngine;

namespace Peakle;

/// <summary>
/// Scouts, as opposed to items.
///
/// Carrying: look at a knocked-out scout and hold Tab to put them on your back (PEAK's own carry: a worn backpack
/// is dropped, as in vanilla). While carrying, hold Tab to put them down. A prompt and a progress ring show it.
///
/// Dragging: a hand can grab any other scout's body, White Knuckle prop-grab style (ENT_Player.GrabPropUpdate):
/// the grabbed point is pulled towards a spot in front of the grabber's eyes, and if the scout can't follow, the
/// grabber hangs from them like from a rope. Each client moves its own scout, so the pull is applied by the
/// grabbed scout's owner, reading where the grabbers are looking.
/// </summary>
public partial class PeakleHands
{
    private const float CarryHoldTime = 0.6f;

    /// <summary>"carry" / "put down" while a carry prompt should show, else null.</summary>
    internal string carryPrompt;
    internal float carryProgress;
    private bool carryNeedsRelease;

    // ---------------------------------------------------------------- carrying

    /// <summary>Owner, before backpack handling: returns true if Tab is about carrying right now.</summary>
    private bool UpdateCarry(bool tab, IInteractible looked)
    {
        Character carried = character.data.carriedPlayer;
        Character candidate = null;
        if (carried == null && looked is CharacterInteractible ci && ci.character != null && ci.character != character
            && ci.character.data.fullyPassedOut && !ci.character.data.dead && ci.character.data.carrier == null)
        {
            candidate = ci.character;
        }
        if (carried == null && candidate == null)
        {
            carryPrompt = null;
            carryProgress = 0f;
            carryNeedsRelease &= tab;
            return false;
        }
        carryPrompt = carried != null ? "put down" : "carry";
        if (!tab)
        {
            carryNeedsRelease = false;
        }
        if (tab && !carryNeedsRelease)
        {
            carryProgress += Time.deltaTime / CarryHoldTime;
            if (carryProgress >= 1f)
            {
                carryProgress = 0f;
                carryNeedsRelease = true;
                if (carried != null)
                {
                    character.refs.carriying.Drop(carried);
                }
                else
                {
                    character.refs.carriying.StartCarry(candidate);
                }
            }
        }
        else
        {
            carryProgress = 0f;
        }
        return true;
    }

    // ---------------------------------------------------------------- dragging: the grabber

    private bool TryGrabScout(Hand hand)
    {
        Vector3 from = MainCamera.instance.transform.position;
        Vector3 dir = character.data.lookDirection;
        float reach = Wk.InteractDistance * Scale;
        int n = Physics.SphereCastNonAlloc(from, 0.08f * Scale, dir, grabHits, reach, HelperFunctions.GetMask(HelperFunctions.LayerType.AllPhysical), QueryTriggerInteraction.Ignore);
        System.Array.Sort(grabHits, 0, n, HitDistance.Instance);
        for (int i = 0; i < n; i++)
        {
            Collider c = grabHits[i].collider;
            if (c == null || character.refs.ragdoll.colliderList.Contains(c))
            {
                continue;
            }
            if (!CharacterRagdoll.TryGetCharacterFromCollider(c, out Character other) || other == character || other.data.dead)
            {
                return false; // the first thing in the way isn't a scout
            }
            Bodypart part = c.GetComponentInParent<Bodypart>();
            if (part == null)
            {
                return false;
            }
            Vector3 point = grabHits[i].distance > 0f ? grabHits[i].point : c.ClosestPoint(from);
            Vector3 local = part.transform.InverseTransformPoint(point);
            view.RPC(nameof(RPCA_PeakleGrabScout), RpcTarget.All, (byte)hand.id, other.photonView.ViewID, (byte)part.partType, local);
            return true;
        }
        return false;
    }

    /// <summary>All clients: a hold on a scout follows that scout's body part.</summary>
    private void FollowScoutHolds()
    {
        foreach (Hand hand in hands)
        {
            if (!hand.grabbing || hand.kind != HoldKind.Scout)
            {
                continue;
            }
            if (hand.target == null || hand.targetPart == null || hand.target.data.dead)
            {
                if (view.IsMine)
                {
                    view.RPC(nameof(RPCA_PeakleRelease), RpcTarget.All, (byte)hand.id);
                }
                continue;
            }
            hand.point = hand.targetPart.transform.TransformPoint(hand.targetLocal);
            Vector3 center = body.Active ? body.Position : character.Center;
            hand.normal = (center - hand.point).normalized;
            // White Knuckle drops a grabbed prop that ends up too far away.
            if (view.IsMine && Vector3.Distance(center, hand.point) > Wk.InteractDistance * Scale * 1.5f)
            {
                view.RPC(nameof(RPCA_PeakleRelease), RpcTarget.All, (byte)hand.id);
            }
        }
    }

    [PunRPC]
    public void RPCA_PeakleGrabScout(byte id, int viewID, byte part, Vector3 local)
    {
        PhotonView targetView = PhotonView.Find(viewID);
        Character target = targetView != null ? targetView.GetComponent<Character>() : null;
        if (target == null)
        {
            return;
        }
        Hand hand = hands[id];
        hand.grabbing = true;
        hand.kind = HoldKind.Scout;
        hand.slide = 0f;
        hand.holdTime = 0f;
        hand.anchor = null;
        hand.target = target;
        hand.targetPart = target.GetBodypart((BodypartType)part);
        hand.targetLocal = local;
        hand.point = hand.targetPart.transform.TransformPoint(local);
    }

    // ---------------------------------------------------------------- dragging: the grabbed scout (its owner)

    /// <summary>
    /// Owner of this scout: every hand on another scout that holds this one pulls the held point towards a spot
    /// in front of that scout's eyes, at White Knuckle's prop speed (gap / mass * 8). Knocked-out scouts go limp
    /// with it; conscious ones only partly give in.
    /// </summary>
    private void ApplyDrags(float dt)
    {
        foreach (Character other in Character.AllCharacters)
        {
            if (other == null || other == character)
            {
                continue;
            }
            PeakleHands grabber = HandsOf.Get(other);
            if (grabber == null)
            {
                continue;
            }
            foreach (Hand hand in grabber.hands)
            {
                if (!hand.grabbing || hand.kind != HoldKind.Scout || hand.target != character || hand.targetPart == null)
                {
                    continue;
                }
                // What PEAK's reach did (CharacterGrabbing.Reach): a teammate's hand frees you from webs, fly traps
                // and whatever you're stuck in.
                if (character.IsStuck())
                {
                    character.UnStick();
                }
                character.refs.afflictions.SubtractStatus(CharacterAfflictions.STATUSTYPE.Web, 1f);
                character.refs.afflictions.SubtractStatus(CharacterAfflictions.STATUSTYPE.FlyTrap, 1f);
                character.data.sinceGrabFriend = 0f;
                if (!grabber.Anchored)
                {
                    continue; // a scout who is only held up by scouts can't haul anyone (no lifting each other into the air)
                }
                character.LimitFalling(); // caught: the fall so far doesn't count (PEAK's reach does the same)
                Vector3 eyes = other.GetBodypart(BodypartType.Head).Rig.position;
                Vector3 holdAt = eyes + other.data.lookDirection * (Plugin.DragHoldDistance.Value * Scale)
                    + other.data.lookDirection_Right * (hand.Side * 0.2f * Scale);
                Vector3 grabbed = hand.targetPart.transform.TransformPoint(hand.targetLocal);
                Vector3 wanted = (holdAt - grabbed) / Plugin.ScoutMass.Value * 8f;
                float give = character.data.fullyConscious ? Plugin.ConsciousDragGive.Value : 1f;
                if (body.Active)
                {
                    body.vel = Vector3.Lerp(body.vel, wanted / WkBody.Metres, give);
                }
                else
                {
                    AddBodyVelocity((wanted - AverageVelocity()) * give);
                }
            }
        }
    }
}
