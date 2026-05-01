using System.Collections;
using UnityEngine;

public class OscillatingPlatform : MonoBehaviour
{
    [Header("Activation")]
    [SerializeField] private string playerTag = "Player";
    [SerializeField] private bool requirePlayerOnTop = true;
    [SerializeField] private bool useOverlapDetection = true;
    [SerializeField] private float topDetectionHeight = 0.35f;
    [SerializeField] private float topDetectionPadding = 0.1f;

    [Header("Oscillation")]
    [SerializeField] private float timeToFall = 2.5f;
    [SerializeField] private float swaySpeed = 8f;
    [SerializeField] private float maxAngle = 28f;
    [SerializeField] private Vector3 rotationAxis = Vector3.forward;

    [Header("Fall")]
    [SerializeField] private float disappearDelay = 0.15f;
    [SerializeField] private float respawnDelay = 4f;
    [SerializeField] private bool hideOnFall = true;

    private Quaternion initialRotation;
    private Vector3 initialPosition;
    private Collider[] platformColliders;
    private Renderer[] platformRenderers;

    private Coroutine fallRoutine;
    private float shakeTimer;
    private bool isUnavailable;

    private void Awake()
    {
        initialPosition = transform.position;
        initialRotation = transform.rotation;
        platformColliders = GetComponentsInChildren<Collider>();
        platformRenderers = GetComponentsInChildren<Renderer>();

        rotationAxis = rotationAxis.sqrMagnitude > 0f ? rotationAxis.normalized : Vector3.forward;
    }

    private void Update()
    {
        if (useOverlapDetection)
            CheckPlayerOnTop();
    }

    private void OnCollisionEnter(Collision collision)
    {
        TryActivate(collision.collider, collision);
    }

    private void OnTriggerEnter(Collider other)
    {
        TryActivate(other, null);
    }

    private void TryActivate(Collider playerCollider, Collision collision)
    {
        if (isUnavailable || fallRoutine != null) return;
        if (!playerCollider.CompareTag(playerTag)) return;
        if (requirePlayerOnTop && collision != null && !IsStandingOnTop(collision)) return;

        fallRoutine = StartCoroutine(FallRoutine());
    }

    private void CheckPlayerOnTop()
    {
        if (isUnavailable || fallRoutine != null) return;
        if (!TryGetMainColliderBounds(out Bounds bounds)) return;

        Vector3 center = bounds.center + Vector3.up * (bounds.extents.y + topDetectionHeight * 0.5f);
        Vector3 halfExtents = new Vector3(
            Mathf.Max(0.05f, bounds.extents.x - topDetectionPadding),
            topDetectionHeight * 0.5f,
            Mathf.Max(0.05f, bounds.extents.z - topDetectionPadding)
        );

        Collider[] hits = Physics.OverlapBox(center, halfExtents, Quaternion.identity);
        foreach (Collider hit in hits)
        {
            if (hit.CompareTag(playerTag) || hit.GetComponentInParent<CharacterController>() != null)
            {
                fallRoutine = StartCoroutine(FallRoutine());
                return;
            }
        }
    }

    private bool TryGetMainColliderBounds(out Bounds bounds)
    {
        foreach (Collider platformCollider in platformColliders)
        {
            if (platformCollider == null || platformCollider.isTrigger)
                continue;

            bounds = platformCollider.bounds;
            return true;
        }

        bounds = default;
        return false;
    }

    private bool IsStandingOnTop(Collision collision)
    {
        foreach (ContactPoint contact in collision.contacts)
        {
            if (Vector3.Dot(contact.normal, Vector3.up) > 0.45f)
                return true;
        }

        return false;
    }

    private IEnumerator FallRoutine()
    {
        shakeTimer = 0f;

        while (shakeTimer < timeToFall)
        {
            shakeTimer += Time.deltaTime;

            float progress = Mathf.Clamp01(shakeTimer / timeToFall);
            float angle = Mathf.Sin(shakeTimer * swaySpeed) * maxAngle * progress;
            transform.rotation = initialRotation * Quaternion.AngleAxis(angle, rotationAxis);

            yield return null;
        }

        yield return new WaitForSeconds(disappearDelay);

        SetAvailable(false);

        yield return new WaitForSeconds(respawnDelay);

        Respawn();
    }

    private void SetAvailable(bool available)
    {
        isUnavailable = !available;

        foreach (Collider platformCollider in platformColliders)
        {
            if (platformCollider != null)
                platformCollider.enabled = available;
        }

        if (!hideOnFall) return;

        foreach (Renderer platformRenderer in platformRenderers)
        {
            if (platformRenderer != null)
                platformRenderer.enabled = available;
        }
    }

    private void Respawn()
    {
        transform.SetPositionAndRotation(initialPosition, initialRotation);
        shakeTimer = 0f;
        fallRoutine = null;

        SetAvailable(true);
    }

    [ContextMenu("Activate Fall")]
    public void ActivateFall()
    {
        if (fallRoutine == null && !isUnavailable)
            fallRoutine = StartCoroutine(FallRoutine());
    }

    [ContextMenu("Force Respawn")]
    public void ForceRespawn()
    {
        if (fallRoutine != null)
            StopCoroutine(fallRoutine);

        Respawn();
    }

    public float GetDangerProgress()
    {
        return Mathf.Clamp01(shakeTimer / timeToFall);
    }
}
