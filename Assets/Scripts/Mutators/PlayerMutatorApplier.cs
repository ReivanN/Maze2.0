using StarterAssets;
using UnityEngine;

/// <summary>
/// Applies the active level mutator to the LOCAL player only.
/// Player position is client-authoritative (ClientNetworkTransform), so each client moves its own
/// character; running this on every client is what makes the mutator affect both players.
///
/// Added at runtime by LevelMutatorManager, so the player prefab does not need to be edited.
/// Gravity, jump height and ice momentum are public fields of ThirdPersonController,
/// mirrored input and wind are handled by running before it.
/// </summary>
[DefaultExecutionOrder(-100)] // run before ThirdPersonController.Update
public class PlayerMutatorApplier : MonoBehaviour
{
    private ThirdPersonController controller;
    private StarterAssetsInputs inputs;
    private CharacterController characterController;
    private LevelMutatorManager manager;

    private float baseGravity;
    private float baseJumpHeight;
    private float baseSpeedChangeRate;
    private bool baseUseMomentum;
    private bool baseCaptured;

    private LevelMutatorType current = LevelMutatorType.None;

    // Mirror controls: input is flipped for the controller's Update and restored in LateUpdate.
    private bool moveSwapped;
    private Vector2 realMove;

    private void Awake()
    {
        controller = GetComponentInChildren<ThirdPersonController>(true);
        inputs = GetComponentInChildren<StarterAssetsInputs>(true);

        if (controller != null)
        {
            characterController = controller.GetComponent<CharacterController>();

            baseGravity = controller.Gravity;
            baseJumpHeight = controller.JumpHeight;
            baseSpeedChangeRate = controller.SpeedChangeRate;
            baseUseMomentum = controller.UseMomentum;
            baseCaptured = true;

            // Walk animation must not play while the character is only sliding (ice) or being pushed (wind).
            if (controller.GetComponent<PlayerSlideAnimationFix>() == null)
                controller.gameObject.AddComponent<PlayerSlideAnimationFix>();
        }
    }

    public void Configure(LevelMutatorManager owner)
    {
        manager = owner;
    }

    public void Apply(LevelMutatorType type)
    {
        ResetValues();
        current = type;

        if (controller == null || manager == null)
            return;

        LevelMutatorSettings s = manager.Settings;

        switch (type)
        {
            case LevelMutatorType.LowGravity:
                // Jump height stays the same (the controller derives jump speed from gravity),
                // only the time in the air grows, so jumps become longer but not higher.
                controller.Gravity = baseGravity * s.lowGravityMultiplier;
                break;

            case LevelMutatorType.HeavyWorld:
                controller.Gravity = baseGravity * s.heavyGravityMultiplier;
                controller.JumpHeight = baseJumpHeight * s.heavyJumpHeightMultiplier;
                break;

            case LevelMutatorType.Ice:
                // The velocity is kept between frames; input only slowly changes it (see ThirdPersonController.UpdateMomentum).
                controller.UseMomentum = true;
                controller.MomentumAcceleration = s.iceAcceleration;
                controller.MomentumFriction = s.iceFriction;
                break;
        }
    }

    private void Update()
    {
        if (current == LevelMutatorType.MirrorControls)
            SwapMoveInput();
        else if (current == LevelMutatorType.Wind)
            ApplyWind();
    }

    private void LateUpdate()
    {
        RestoreMoveInput();
    }

    private void SwapMoveInput()
    {
        if (inputs == null || !inputs.enabled || manager == null)
            return;

        LevelMutatorSettings s = manager.Settings;

        realMove = inputs.move;
        inputs.move = new Vector2(
            s.mirrorHorizontal ? -realMove.x : realMove.x,
            s.mirrorVertical ? -realMove.y : realMove.y);
        moveSwapped = true;
    }

    private void RestoreMoveInput()
    {
        if (!moveSwapped)
            return;

        if (inputs != null)
            inputs.move = realMove;

        moveSwapped = false;
    }

    private void ApplyWind()
    {
        // Do not push the player while movement is disabled (lobby, waiting for the timer).
        if (manager == null || controller == null || !controller.enabled ||
            characterController == null || !characterController.enabled)
        {
            return;
        }

        Vector3 direction = Quaternion.Euler(0f, manager.WindAngleDegrees, 0f) * Vector3.forward;

        // Called before ThirdPersonController.Update, so the controller's own Move() is the last one in the
        // frame and CharacterController.velocity (which it reads for acceleration) stays correct.
        characterController.Move(direction * (manager.Settings.windStrength * Time.deltaTime));
    }

    private void ResetValues()
    {
        RestoreMoveInput();

        if (!baseCaptured || controller == null)
            return;

        controller.Gravity = baseGravity;
        controller.JumpHeight = baseJumpHeight;
        controller.SpeedChangeRate = baseSpeedChangeRate;
        controller.UseMomentum = baseUseMomentum;
    }

    private void OnDestroy()
    {
        ResetValues();
    }
}

/// <summary>
/// ThirdPersonController sets the animator "Speed" from its target speed with a smoothing lerp, so on ice
/// (low SpeedChangeRate) the walk animation keeps playing while the character slides without any input,
/// and wind pushes the character without any input at all.
///
/// This runs after ThirdPersonController.Update and before the animator is evaluated: with no movement
/// input the walk blend is forced to idle, so a sliding character keeps its idle pose.
/// </summary>
[DefaultExecutionOrder(100)]
public class PlayerSlideAnimationFix : MonoBehaviour
{
    private static readonly int SpeedId = Animator.StringToHash("Speed");
    private static readonly int MotionSpeedId = Animator.StringToHash("MotionSpeed");

    private Animator animator;
    private ThirdPersonController controller;
    private StarterAssetsInputs inputs;

    private void Awake()
    {
        animator = GetComponent<Animator>();
        controller = GetComponent<ThirdPersonController>();
        inputs = GetComponentInParent<StarterAssetsInputs>();

        if (inputs == null)
            inputs = GetComponent<StarterAssetsInputs>();
    }

    private void Update()
    {
        if (animator == null || controller == null || inputs == null || !controller.enabled)
            return;

        // Keys are held: normal walking / running animation.
        if (inputs.move != Vector2.zero)
            return;

        animator.SetFloat(SpeedId, 0f);
        animator.SetFloat(MotionSpeedId, 1f);
    }
}
