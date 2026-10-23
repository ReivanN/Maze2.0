using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Extra animation for the low-poly knight (added by LowPolyCharacter), on top of the Animator, on every client:
///   - the glowing eyes blink and make faces: hurt when the knight loses a heart, happy when it heals or a level is
///     done, strained while it carries something; a thin mouth appears for those faces;
///   - both arms go up and hold the item above the head while this knight carries a cube or a heart.
/// Everything is driven by state that is already synced (hearts, who carries what), so nothing extra is sent.
/// Runs in LateUpdate, after the Animator has posed the bones and before the meshes are skinned.
/// </summary>
public class KnightAnimations : MonoBehaviour
{
    private enum Face { Neutral, Strained, Hurt, Happy }

    private const float FaceSpeed = 14f;
    private const float ArmBlendSpeed = 5f;
    private const float HurtTime = 1.6f;
    private const float HappyTime = 2f;
    private const float BlinkTime = 0.12f;

    private Transform leftEye, rightEye, mouth;
    private Transform leftUpperArm, leftLowerArm, leftHand;
    private Transform rightUpperArm, rightLowerArm, rightHand;

    private Vector3 leftEyeBase, rightEyeBase;
    private Vector3 eyeScale = Vector3.one;
    private float eyeTilt;
    private float eyeLift;
    private Vector3 mouthScale = Vector3.zero;

    private NetworkObject owner;
    private DungeonProgressManager manager;
    private int lastHearts = -1;
    private Face timedFace = Face.Neutral;
    private float timedFaceUntil;
    private float nextBlinkTime;
    private float blinkUntil;
    private float armWeight;
    private Vector3 lastItemCenter;     // relative to the knight, for lowering the arms after letting go
    private float lastHalfSize = 0.3f;
    private bool ready;

    public void Setup(Transform leftEyeBone, Transform rightEyeBone, Transform mouthBone,
        Transform leftUpper, Transform leftLower, Transform leftHandBone,
        Transform rightUpper, Transform rightLower, Transform rightHandBone)
    {
        leftEye = leftEyeBone;
        rightEye = rightEyeBone;
        mouth = mouthBone;
        leftUpperArm = leftUpper;
        leftLowerArm = leftLower;
        leftHand = leftHandBone;
        rightUpperArm = rightUpper;
        rightLowerArm = rightLower;
        rightHand = rightHandBone;

        leftEyeBase = leftEye.localPosition;
        rightEyeBase = rightEye.localPosition;
        mouth.localScale = Vector3.zero;

        owner = GetComponentInParent<NetworkObject>();
        ScheduleBlink();
        ready = true;
    }

    private void OnDestroy()
    {
        if (manager != null)
            manager.OnLevelCompleted -= OnLevelCompleted;
    }

    private void LateUpdate()
    {
        if (!ready)
            return;

        WatchGameEvents();

        CarryableItem carried = FindCarriedItem();
        UpdateArms(carried);
        UpdateFace(carried != null);
    }

    // ================= EVENTS =================

    private void WatchGameEvents()
    {
        if (manager == null && DungeonProgressManager.Instance != null)
        {
            manager = DungeonProgressManager.Instance;
            manager.OnLevelCompleted += OnLevelCompleted;
        }

        if (manager == null || owner == null || !owner.IsSpawned)
            return;

        int hearts = manager.GetHearts(owner.OwnerClientId);
        if (lastHearts >= 0)
        {
            if (hearts < lastHearts)
                ShowFace(Face.Hurt, HurtTime);
            else if (hearts > lastHearts)
                ShowFace(Face.Happy, HappyTime);
        }

        lastHearts = hearts;
    }

    private void OnLevelCompleted()
    {
        ShowFace(Face.Happy, HappyTime + 1f);
    }

    private void ShowFace(Face face, float duration)
    {
        timedFace = face;
        timedFaceUntil = Time.time + duration;
    }

    private CarryableItem FindCarriedItem()
    {
        if (owner == null || !owner.IsSpawned)
            return null;

        foreach (CarryableItem item in CarryableItem.Instances)
        {
            if (item != null && item.IsCarried && item.CarrierId == owner.OwnerClientId)
                return item;
        }

        return null;
    }

    // ================= FACE =================

    private void UpdateFace(bool carrying)
    {
        Face face = Time.time < timedFaceUntil ? timedFace : carrying ? Face.Strained : Face.Neutral;

        // Target look of the eyes for the face. Tilt > 0 lowers the inner corners (angry / straining),
        // tilt < 0 raises them (happy squint).
        Vector3 targetEye = Vector3.one;
        float targetTilt = 0f;
        float targetLift = 0f;
        Vector3 targetMouth = Vector3.zero;

        switch (face)
        {
            case Face.Strained:
                targetEye = new Vector3(1f, 0.55f, 1f);
                targetTilt = 12f;
                targetMouth = new Vector3(0.8f, 1f, 1f);
                break;
            case Face.Hurt:
                targetEye = new Vector3(0.9f, 0.35f, 1f);
                targetTilt = 22f;
                targetMouth = new Vector3(0.45f, 4f, 1f); // a small open "o"
                break;
            case Face.Happy:
                targetEye = new Vector3(1.1f, 0.45f, 1f);
                targetTilt = -14f;
                targetLift = 0.006f;
                targetMouth = new Vector3(1.3f, 1f, 1f);
                break;
        }

        // Blink every few seconds, except while squeezing the eyes anyway.
        if (Time.time >= nextBlinkTime)
        {
            blinkUntil = Time.time + BlinkTime;
            ScheduleBlink();
        }

        if (Time.time < blinkUntil && face != Face.Hurt)
            targetEye.y = 0.08f;

        float k = 1f - Mathf.Exp(-FaceSpeed * Time.deltaTime);
        eyeScale = Vector3.Lerp(eyeScale, targetEye, k);
        eyeTilt = Mathf.Lerp(eyeTilt, targetTilt, k);
        eyeLift = Mathf.Lerp(eyeLift, targetLift, k);
        mouthScale = Vector3.Lerp(mouthScale, targetMouth, k);

        ApplyEye(leftEye, leftEyeBase, -eyeTilt);
        ApplyEye(rightEye, rightEyeBase, eyeTilt);
        mouth.localScale = mouthScale;
    }

    // The face bones are set up with the character's axes: x right, y up, z forward. Turning the left eye by a negative
    // angle and the right one by a positive angle around z lowers both inner corners.
    private void ApplyEye(Transform eye, Vector3 basePosition, float tilt)
    {
        eye.localScale = eyeScale;
        eye.localRotation = Quaternion.Euler(0f, 0f, tilt);
        eye.localPosition = basePosition + Vector3.up * eyeLift;
    }

    private void ScheduleBlink()
    {
        nextBlinkTime = Time.time + Random.Range(2.5f, 5.5f);
    }

    // ================= ARMS =================

    private void UpdateArms(CarryableItem carried)
    {
        armWeight = Mathf.MoveTowards(armWeight, carried != null ? 1f : 0f, ArmBlendSpeed * Time.deltaTime);
        if (armWeight <= 0f)
            return;

        // While letting go the item is gone already: keep the hands where the item was, over the head.
        Vector3 itemCenter;
        float halfSize;
        if (carried != null)
        {
            itemCenter = carried.transform.position;
            halfSize = carried.HalfHeight;
            lastItemCenter = itemCenter - transform.position;
            lastHalfSize = halfSize;
        }
        else
        {
            itemCenter = transform.position + lastItemCenter;
            halfSize = lastHalfSize;
        }

        Vector3 right = transform.right;
        Vector3 under = itemCenter - Vector3.up * (halfSize * 0.7f);

        HoldWithArm(leftUpperArm, leftLowerArm, leftHand, under, right, halfSize);
        HoldWithArm(rightUpperArm, rightLowerArm, rightHand, under, right, halfSize);
    }

    // Points the arm at the side of the item: the hand ends up under the item's edge, palms up.
    private void HoldWithArm(Transform upper, Transform lower, Transform hand, Vector3 under, Vector3 right, float halfSize)
    {
        if (upper == null || lower == null || hand == null)
            return;

        float side = Mathf.Sign(Vector3.Dot(upper.position - transform.position, right));
        Vector3 target = under + right * (side * (halfSize + 0.05f));

        AimBone(upper, lower.position, target);
        AimBone(lower, hand.position, target);
    }

    // Turns the bone so that its child (elbow / hand) moves towards the target, blended with the Animator pose.
    private void AimBone(Transform bone, Vector3 childPosition, Vector3 target)
    {
        Vector3 current = childPosition - bone.position;
        Vector3 wanted = target - bone.position;
        if (current.sqrMagnitude < 0.0001f || wanted.sqrMagnitude < 0.0001f)
            return;

        Quaternion turn = Quaternion.FromToRotation(current, wanted);
        bone.rotation = Quaternion.Slerp(Quaternion.identity, turn, armWeight) * bone.rotation;
    }
}
