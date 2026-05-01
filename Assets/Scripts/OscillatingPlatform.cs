using UnityEngine;

public class OscillatingPlatform : MonoBehaviour
{
    [Header("Oscillation Settings")]
    [SerializeField] private float swaySpeed = 1.5f;
    [SerializeField] private float maxAngle = 28f;
    [SerializeField] private float timeToFall = 2.5f;

    [Header("Respawn Settings")]
    [SerializeField] private float respawnDelay = 4f;

    private float _shakeTimer = 0f;
    private float _currentAngle = 0f;
    private float _angleVelocity = 0f;
    private float _respawnTimer = 0f;

    private bool _playerOnPlatform = false;
    private bool _isShaking = false;
    private bool _isRespawning = false;

    private Quaternion _initialRotation;
    private Vector3 _initialPosition;
    private Collider _platformCollider;
    private Renderer _platformRenderer;

    private void Awake()
    {
        _initialRotation = transform.rotation;
        _initialPosition = transform.position;
        _platformCollider = GetComponent<Collider>();
        _platformRenderer = GetComponent<Renderer>();
    }

    private void Update()
    {
        if (_isRespawning)
        {
            HandleRespawn();
            return;
        }

        if (_isShaking)
        {
            HandleOscillation();
        }
        else
        {
            ReturnToIdle();
        }
    }

    private void HandleOscillation()
    {
        _shakeTimer += Time.deltaTime;

        // Нарастание амплитуды со временем
        float progress = Mathf.Clamp01(_shakeTimer / timeToFall);
        float currentMaxAngle = maxAngle * progress;

        _angleVelocity += Mathf.Sin(_shakeTimer * swaySpeed * 2f) * swaySpeed * Time.deltaTime * 2f;
        _currentAngle += _angleVelocity * Time.deltaTime * 60f;
        _currentAngle = Mathf.Clamp(_currentAngle, -currentMaxAngle, currentMaxAngle);

        transform.rotation = _initialRotation * Quaternion.Euler(0f, 0f, _currentAngle);

        // Игрок упал, если платформа достигла максимального угла
        if (_playerOnPlatform && Mathf.Abs(_currentAngle) >= maxAngle - 0.5f && _shakeTimer > 0.5f)
        {
            DropPlayer();
        }
    }

    private void ReturnToIdle()
    {
        _angleVelocity *= 0.985f;
        _currentAngle = Mathf.Lerp(_currentAngle, 0f, Time.deltaTime * 3f);
        transform.rotation = Quaternion.Slerp(transform.rotation, _initialRotation, Time.deltaTime * 3f);

        if (Mathf.Abs(_currentAngle) < 0.1f && Mathf.Abs(_angleVelocity) < 0.01f)
        {
            _currentAngle = 0f;
            _angleVelocity = 0f;
            transform.rotation = _initialRotation;
        }
    }

    private void HandleRespawn()
    {
        _respawnTimer -= Time.deltaTime;
        if (_respawnTimer <= 0f)
        {
            Respawn();
        }
    }

    private void DropPlayer()
    {
        _playerOnPlatform = false;

        // Отталкиваем Rigidbody игрока если он есть
        var player = FindPlayerOnPlatform();
        if (player != null)
        {
            var rb = player.GetComponent<Rigidbody>();
            if (rb != null)
            {
                Vector3 throwDirection = (transform.right * Mathf.Sign(_currentAngle) + Vector3.up * 0.3f).normalized;
                rb.AddForce(throwDirection * 5f, ForceMode.Impulse);
            }
        }

        StartRespawn();
    }

    private void StartRespawn()
    {
        _isShaking = false;
        _isRespawning = true;
        _respawnTimer = respawnDelay;

        _platformCollider.enabled = false;
        _platformRenderer.enabled = false;
    }

    private void Respawn()
    {
        _isRespawning = false;
        _shakeTimer = 0f;
        _currentAngle = 0f;
        _angleVelocity = 0f;

        transform.rotation = _initialRotation;
        transform.position = _initialPosition;

        _platformCollider.enabled = true;
        _platformRenderer.enabled = true;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.CompareTag("Player"))
        {
            _playerOnPlatform = true;
            _isShaking = true;
            _shakeTimer = 0f;
        }
    }

    private void OnCollisionExit(Collision collision)
    {
        if (collision.gameObject.CompareTag("Player"))
        {
            _playerOnPlatform = false;

            // Если игрок сошёл сам — постепенно останавливаем качание
            if (!_isRespawning)
            {
                _isShaking = false;
            }
        }
    }

    // Вспомогательный метод — можно заменить на свою систему
    private GameObject FindPlayerOnPlatform()
    {
        return GameObject.FindWithTag("Player");
    }

    // Публичный метод для счётчика опасности (например для UI)
    public float GetDangerProgress()
    {
        return Mathf.Clamp01(_shakeTimer / timeToFall);
    }
}