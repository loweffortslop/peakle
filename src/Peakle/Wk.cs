namespace Peakle;

/// <summary>
/// White Knuckle's player tuning, read from its ENT_Player prefab (see tools/wk_player_values.py),
/// and constants hard-coded in ENT_Player. Velocities here are in White Knuckle's internal units:
/// the player moves vel * fixedDeltaTime * 175 per physics step, so 1 unit = 175 m/s.
/// </summary>
internal static class Wk
{
    public const float VelToMetres = 175f;

    public const float Gravity = -0.2f;
    public const float JumpHeight = 0.4f;
    public const float AirControl = 0.3f;
    public const float Speed = 0.85f;
    public const float DragCoefficient = 8f;
    public const float WallFrictionMultiplier = 10f;

    // CharacterController on CL_Player, and ENT_Player fields
    public const float StandingHeight = 1.5f;
    public const float CrouchHeight = 1f;
    public const float ControllerRadius = 0.3f;
    public const float StepOffset = 0.15f;
    public const float SkinWidth = 0.05f;
    public const float SlopeLimit = 45f;
    public const float FrictionMultiplier = 100f;

    public const float InteractDistance = 2.5f; // grab reach from the camera
    public const float HangSlack = 1.2f;        // a hand only pulls once the body is further than this
    public const float SideMove = 0.5f;         // sideways input is halved while hanging

    public const float GripStrengthTimer = 10f; // full grip = 10 s hanging on one hand
    public const float ClimbJumpDrain = 1f;
    public const float BothHandsJumpStrain = 0.48f;
    public const float RegenGrounded = 0.9f;
    public const float RegenAirborne = 0.75f;
    public const float RegenWhileHangingGrounded = 0.75f;
}
