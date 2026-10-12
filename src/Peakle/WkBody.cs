using UnityEngine;

namespace Peakle;

/// <summary>
/// White Knuckle's player body: a CharacterController with White Knuckle's exact settings (scaled to the scout),
/// moved exactly the way ENT_Player.Movement moves it. While the local scout hangs or flies after a grab jump,
/// this capsule is the only thing deciding where the scout goes; the ragdoll's hip is driven onto it every
/// physics step and PEAK's own gravity, drag, standing and movement forces are switched off.
/// Only exists for the local player; other clients see the result through PEAK's normal position sync.
/// </summary>
internal class WkBody
{
    private readonly Character character;
    private GameObject go;
    private CharacterController cc;

    /// <summary>Velocity in White Knuckle units (1 = 175 WK metres per second).</summary>
    public Vector3 vel;
    public Vector3 hitNormal = Vector3.up;
    public float hitTime;
    public bool hasHit;
    public bool verticalSlide;

    // Capsule position at the last two physics steps, for smooth camera placement between them.
    private Vector3 prevPos;
    private Vector3 currPos;
    // Camera offset from the capsule centre, in the scout's yaw frame, taken from PEAK's camera on entry.
    private Vector3 eyeOffset;

    public WkBody(Character character)
    {
        this.character = character;
    }

    public bool Active => cc != null && cc.enabled;
    public bool IsGrounded => Active && cc.isGrounded;
    public Vector3 Position => go.transform.position;

    private static float Scale => Plugin.WkScale.Value;

    /// <summary>
    /// PEAK metres per second for one White Knuckle velocity unit: White Knuckle's 175, times the world scale,
    /// times the pace (pace slows White Knuckle's motion down in time without changing its shape).
    /// </summary>
    public static float Metres => Wk.VelToMetres * Plugin.WkScale.Value * Plugin.Pace.Value;

    public void Enter(Vector3 velocityMetres)
    {
        Ensure();
        float s = Scale;
        cc.height = Wk.StandingHeight * s;
        cc.radius = Wk.ControllerRadius * s;
        cc.stepOffset = Wk.StepOffset * s;
        cc.skinWidth = Wk.SkinWidth * s;
        cc.slopeLimit = 90f;
        cc.minMoveDistance = 0f;
        Teleport(character.refs.hip.Rig.position);
        Vector3 flat = character.data.lookDirection_Flat;
        Quaternion yaw = Quaternion.LookRotation(flat.sqrMagnitude > 0.001f ? flat : Vector3.forward);
        Vector3 cam = MainCamera.instance != null ? MainCamera.instance.transform.position : character.GetCameraPos(-0.5f);
        // Only the height is kept: like White Knuckle's, the eyes sit on the capsule's axis.
        eyeOffset = new Vector3(0f, Mathf.Clamp((cam - Position).y, 0f, Wk.StandingHeight * 0.5f * s), 0f);
        vel = velocityMetres / Metres;
        hitTime = 0f;
        hasHit = false;
        hitNormal = Vector3.up;
    }

    public void Exit()
    {
        if (cc != null)
        {
            cc.enabled = false;
        }
    }

    public void Destroy()
    {
        if (go != null)
        {
            Object.Destroy(go);
        }
    }

    public void SetCrouched(bool crouched)
    {
        if (Active)
        {
            cc.height = (crouched ? Wk.CrouchHeight : Wk.StandingHeight) * Scale;
        }
    }

    private void Ensure()
    {
        if (go != null)
        {
            return;
        }
        go = new GameObject("Peakle WK Body");
        Object.DontDestroyOnLoad(go);
        int layer = character.refs.hip.gameObject.layer;
        go.layer = layer;
        cc = go.AddComponent<CharacterController>();
        // Collide with the world, not with any scout (its own ragdoll included).
        cc.excludeLayers = 1 << layer;
        cc.enabled = false;
        go.AddComponent<WkBodyHits>().body = this;
    }

    private void Teleport(Vector3 position)
    {
        cc.enabled = false;
        go.transform.position = position;
        cc.enabled = true;
        prevPos = currPos = position;
    }

    /// <summary>
    /// The camera rides the capsule rigidly, interpolated between physics steps the way White Knuckle's
    /// ENT_Player.InterpolateCamera does, instead of following the ragdoll's head.
    /// </summary>
    public Vector3 EyePosition(Vector3 lookFlat)
    {
        float t = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
        Quaternion yaw = Quaternion.LookRotation(lookFlat.sqrMagnitude > 0.001f ? lookFlat : Vector3.forward);
        Vector3 offset = eyeOffset;
        offset.y += (cc.height - Wk.StandingHeight * Scale) * 0.5f; // crouching lowers the eyes with the capsule top
        Vector3 center = Vector3.Lerp(prevPos, currPos, t);
        Vector3 eye = center + yaw * offset;
        // Never let the camera poke through a ceiling or wall the capsule is touching.
        float radius = 0.12f * Scale;
        Vector3 toEye = eye - center;
        if (toEye.sqrMagnitude > 0.0001f && Physics.SphereCast(center, radius, toEye.normalized, out RaycastHit hit, toEye.magnitude,
            HelperFunctions.GetMask(HelperFunctions.LayerType.TerrainMap), QueryTriggerInteraction.Ignore))
        {
            eye = center + toEye.normalized * Mathf.Max(hit.distance - 0.02f, 0f);
        }
        return eye;
    }

    /// <summary>
    /// The tail of ENT_Player.Movement: slide off steep walls, move, clamp on the ground.
    /// <paramref name="dt"/> is White Knuckle time (the physics step times the pace).
    /// </summary>
    public void Move(float dt)
    {
        float s = Scale;
        hitTime -= dt;
        verticalSlide = false;
        float slopeAngle = Vector3.Angle(Vector3.up, hitNormal);
        if (hitTime >= 0f && Vector3.Dot(vel.normalized, hitNormal) <= 0f && slopeAngle >= 85f)
        {
            vel = Vector3.ProjectOnPlane(vel, hitNormal);
            verticalSlide = true;
        }
        prevPos = Position;
        cc.Move(vel * (dt * Wk.VelToMetres * s));  // dt is already paced
        currPos = Position;
        if (!verticalSlide && cc.isGrounded)
        {
            Vector3 moved = cc.velocity * Time.fixedDeltaTime / s;
            vel.x = Mathf.Clamp(vel.x, -Mathf.Abs(moved.x), Mathf.Abs(moved.x));
            vel.z = Mathf.Clamp(vel.z, -Mathf.Abs(moved.z), Mathf.Abs(moved.z));
            vel.y = Mathf.Clamp(vel.y, -Mathf.Abs(moved.y), Mathf.Abs(moved.y));
        }
    }

    // ENT_Player.OnControllerColliderHit
    public void OnHit(ControllerColliderHit hit)
    {
        hitNormal = hit.normal;
        hasHit = true;
        hitTime = 0.1f;
        float angle = Vector3.Angle(Vector3.up, hitNormal);
        if (Vector3.Dot(vel.normalized, hitNormal) <= 0f && angle > Wk.SlopeLimit)
        {
            vel = Vector3.ProjectOnPlane(vel, hitNormal);
            verticalSlide = true;
        }
    }

    /// <summary>Drive the ragdoll's hip onto the capsule this step, keeping the limbs' own motion.</summary>
    public void DriveRagdoll(float dt)
    {
        Rigidbody hip = character.refs.hip.Rig;
        Vector3 offset = Position - hip.position;
        if (offset.sqrMagnitude > 4f)
        {
            // The ragdoll got stuck somewhere the capsule didn't: bring the capsule back to it.
            Teleport(hip.position);
            vel = Vector3.zero;
            offset = Vector3.zero;
        }
        Vector3 delta = offset / dt - hip.linearVelocity;
        foreach (Bodypart part in character.refs.ragdoll.partList)
        {
            if (!part.Rig.isKinematic)
            {
                part.Rig.linearVelocity += delta;
            }
        }
    }

    /// <summary>The capsule's speed in m/s, for handing over to PEAK's physics on exit.</summary>
    public Vector3 VelocityMetres => vel * Metres;
}

/// <summary>Forwards the CharacterController's collision callbacks (they only reach components on its GameObject).</summary>
internal class WkBodyHits : MonoBehaviour
{
    public WkBody body;

    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        body?.OnHit(hit);
    }
}
