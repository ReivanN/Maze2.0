using System.Collections;
using Unity.Netcode;
using UnityEngine;

public class NetworkFallingPlatform : NetworkBehaviour
{
    [Header("Timing")]
    [SerializeField] private float shakeDuration = 3f;
    [SerializeField] private float fallDelay = 0.2f;
    [SerializeField] private float respawnDelay = 15f;

    [Header("Shake Settings")]
    [SerializeField] private float shakeIntensity = 0.1f;

    [Header("Trigger Settings")]
    [SerializeField] private GameObject triggerColliderObject;

    private Vector3 startPosition;
    private Quaternion startRotation;

    private Rigidbody rb;
    private Collider platformCollider;
    private Collider triggerCollider;

    private NetworkVariable<PlatformState> state =
        new NetworkVariable<PlatformState>(
            PlatformState.Idle,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    // Для отслеживания активации триггера
    private NetworkVariable<bool> isTriggerActive = new NetworkVariable<bool>(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        platformCollider = GetComponent<Collider>();

        // Находим дочерний триггер
        if (triggerColliderObject != null)
        {
            triggerCollider = triggerColliderObject.GetComponent<Collider>();
        }
        else
        {
            foreach (Transform child in transform)
            {
                if (child.CompareTag("Trigger"))
                {
                    triggerCollider = child.GetComponent<Collider>();
                    triggerColliderObject = child.gameObject;
                    break;
                }
            }
        }

        startPosition = transform.position;
        startRotation = transform.rotation;
    }

    public override void OnNetworkSpawn()
    {
        state.OnValueChanged += OnStateChanged;
        isTriggerActive.OnValueChanged += OnTriggerActiveChanged;
        
        // Применяем начальные состояния
        if (IsServer)
        {
            ResetPlatform();
        }
        else
        {
            ApplyPlatformState(state.Value);
            ApplyTriggerState(isTriggerActive.Value);
        }
    }

    public override void OnNetworkDespawn()
    {
        state.OnValueChanged -= OnStateChanged;
        isTriggerActive.OnValueChanged -= OnTriggerActiveChanged;
    }

    // Этот метод вызывается из дочернего триггера
    public void OnTriggerEntered(Collider other)
    {
        if (!IsServer) return;
        if (state.Value != PlatformState.Idle) return;
        if (!isTriggerActive.Value) return;

        if (other.gameObject.CompareTag("Player"))
        {
            // Отключаем триггер, чтобы нельзя было активировать повторно во время процесса
            isTriggerActive.Value = false;
            
            // Запускаем корутину падения платформы
            StartCoroutine(PlatformFallRoutine());
        }
    }

    // Этот метод вызывается из дочернего триггера
    public void OnTriggerExited(Collider other)
    {
        // Ничего не делаем при выходе
    }

    private IEnumerator PlatformFallRoutine()
    {
        if (!IsServer) yield break;

        // 1. Фаза тряски
        state.Value = PlatformState.Shaking;
        yield return new WaitForSeconds(shakeDuration);

        // 2. Фаза падения
        state.Value = PlatformState.Falling;
        yield return new WaitForSeconds(fallDelay);

        // 3. Отключаем основной коллайдер платформы
        platformCollider.enabled = false;
        
        // Делаем платформу некинематической
        rb.isKinematic = false;

        // 4. Ждем 15 секунд
        yield return new WaitForSeconds(respawnDelay);

        // 5. Полностью респавним платформу
        ResetPlatform();
        
        // Уведомляем клиентов о респавне
        RespawnPlatformClientRpc();
    }

    [ClientRpc]
    private void RespawnPlatformClientRpc()
    {
        if (!IsServer) // Только клиенты
        {
            // Клиенты визуально возвращают платформу
            transform.SetPositionAndRotation(startPosition, startRotation);
            state.Value = PlatformState.Idle;
            
            // Включаем коллайдер и возвращаем в кинематическое состояние
            platformCollider.enabled = true;
            rb.isKinematic = true;
        }
    }

    private void ResetPlatform()
    {
        if (!IsServer) return;

        // Сбрасываем физику
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = true;
        
        // Возвращаем на стартовую позицию
        transform.SetPositionAndRotation(startPosition, startRotation);
        
        // Включаем основной коллайдер платформы
        platformCollider.enabled = true;
        
        // Включаем триггер для повторной активации
        isTriggerActive.Value = true;
        
        // Возвращаем в исходное состояние
        state.Value = PlatformState.Idle;
    }

    private void OnStateChanged(PlatformState previous, PlatformState current)
    {
        ApplyPlatformState(current);
        
        // Запускаем визуальную тряску
        if (current == PlatformState.Shaking)
        {
            StartCoroutine(ShakeVisual());
        }
    }

    private void OnTriggerActiveChanged(bool previous, bool current)
    {
        ApplyTriggerState(current);
    }

    private void ApplyPlatformState(PlatformState platformState)
    {
        switch (platformState)
        {
            case PlatformState.Idle:
                // Включаем коллайдер и делаем кинематической
                if (platformCollider != null) platformCollider.enabled = true;
                if (rb != null) rb.isKinematic = true;
                break;
                
            case PlatformState.Shaking:
                // Платформа все еще твердая, но трясется
                if (platformCollider != null) platformCollider.enabled = true;
                if (rb != null) rb.isKinematic = true;
                break;
                
            case PlatformState.Falling:
                // Отключаем коллайдер и делаем некинематической
                if (platformCollider != null) platformCollider.enabled = false;
                if (rb != null) rb.isKinematic = false;
                break;
        }
    }

    private void ApplyTriggerState(bool isActive)
    {
        // Включаем/выключаем триггер
        if (triggerCollider != null)
        {
            triggerCollider.enabled = isActive;
        }
        
        // Визуальная индикация (опционально)
        if (triggerColliderObject != null)
        {
            var renderer = triggerColliderObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = isActive ? Color.green : Color.gray;
            }
        }
    }

    private IEnumerator ShakeVisual()
    {
        Vector3 originalPos = transform.position;
        float elapsed = 0f;

        while (elapsed < shakeDuration && state.Value == PlatformState.Shaking)
        {
            elapsed += Time.deltaTime;
            
            // Горизонтальная тряска
            Vector3 offset = new Vector3(
                Random.Range(-shakeIntensity, shakeIntensity),
                0,
                Random.Range(-shakeIntensity, shakeIntensity)
            );
            
            transform.position = originalPos + offset;
            yield return null;
        }

        // Возвращаем точно на место
        transform.position = originalPos;
    }

    // Метод для ручной активации (для тестирования)
    [ContextMenu("Activate Platform Fall")]
    public void ActivateFall()
    {
        if (IsServer && state.Value == PlatformState.Idle)
        {
            isTriggerActive.Value = false;
            StartCoroutine(PlatformFallRoutine());
        }
    }

    [ContextMenu("Reset Platform")]
    public void ForceReset()
    {
        if (IsServer)
        {
            ResetPlatform();
        }
    }

    private enum PlatformState
    {
        Idle,       // Ожидание активации
        Shaking,    // Тряска перед падением
        Falling     // Падение и неактивное состояние
    }
}