using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Peakle;

/// <summary>
/// Stretches a holding arm so the hand visibly reaches its hold, like White Knuckle's hands do, even when the
/// hold is beyond the scout's arm length. The scout's visible skeleton is its physics ragdoll, so the stretch
/// is applied only for rendering: bones are moved after everything else in LateUpdate and put back as soon as
/// the frame has been rendered, so the physics bodies never see it.
/// </summary>
internal static class ArmVisuals
{
    private struct Saved
    {
        public Transform t;
        public Vector3 pos;
        public Quaternion rot;
    }

    private static readonly List<Saved> saved = new();
    private static bool hooked;

    public static void Stretch(Character character, PeakleHands hands)
    {
        if (!Plugin.StretchArms.Value)
        {
            return;
        }
        Hook();
        foreach (PeakleHands.Hand hand in hands.hands)
        {
            if (hand.grabbing)
            {
                StretchArm(character, hand, hand.point + hand.normal * Plugin.HandSurfaceOffset.Value);
            }
        }
    }

    private static void StretchArm(Character character, PeakleHands.Hand hand, Vector3 target)
    {
        bool left = hand.id == PeakleHands.Left;
        Transform arm = character.GetBodypartRig(left ? BodypartType.Arm_L : BodypartType.Arm_R).transform;
        Transform elbow = character.GetBodypartRig(left ? BodypartType.Elbow_L : BodypartType.Elbow_R).transform;
        Transform wrist = character.GetBodypartRig(left ? BodypartType.Hand_L : BodypartType.Hand_R).transform;

        Vector3 shoulder = arm.position;
        Vector3 elbowPos = elbow.position;
        Vector3 handPos = wrist.position;
        if ((handPos - target).sqrMagnitude < 0.0004f)
        {
            return;
        }
        Quaternion armRot = arm.rotation;
        Quaternion elbowRot = elbow.rotation;
        Quaternion handRot = wrist.rotation;
        Save(arm);
        Save(elbow);
        Save(wrist);

        // Keep the elbow's bend, but spread the arm over the full shoulder-to-hold distance.
        Vector3 bend = elbowPos - Vector3.Lerp(shoulder, handPos, 0.5f);
        Vector3 newElbow = Vector3.Lerp(shoulder, target, 0.5f) + bend;

        arm.rotation = Quaternion.FromToRotation(elbowPos - shoulder, newElbow - shoulder) * armRot;
        elbow.SetPositionAndRotation(newElbow, Quaternion.FromToRotation(handPos - elbowPos, target - newElbow) * elbowRot);
        wrist.SetPositionAndRotation(target, handRot);
    }

    /// <summary>Remember a transform so it is put back as soon as the current frame has rendered.</summary>
    internal static void SaveForRender(Transform t)
    {
        Hook();
        Save(t);
    }

    private static void Save(Transform t)
    {
        saved.Add(new Saved { t = t, pos = t.position, rot = t.rotation });
    }

    private static void Hook()
    {
        if (hooked)
        {
            return;
        }
        hooked = true;
        RenderPipelineManager.endContextRendering += (_, _) => Restore();
    }

    // In save order: the upper arm first, so its children are restored on top of it.
    private static void Restore()
    {
        for (int i = 0; i < saved.Count; i++)
        {
            Saved s = saved[i];
            if (s.t != null)
            {
                s.t.SetPositionAndRotation(s.pos, s.rot);
            }
        }
        saved.Clear();
    }
}
