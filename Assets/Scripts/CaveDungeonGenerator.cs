using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public class CaveDungeonGenerator : NetworkBehaviour
{
    [Header("Размер лабиринта (в клетках)")]
    public int width = 21;
    public int height = 21;

    [Header("Размер клетки в Unity")]
    public float cellSize = 2f;

    [Header("Префабы (NetworkObject)")]
    public GameObject floorPrefab;
    public GameObject wallPrefab;
    public GameObject startPrefab;
    public GameObject endPrefab;
    public GameObject fallingPlatformPrefab;

    [Header("Player")]
    public float playerHeightOffset = 1f;

    [Header("Pool Settings")]
    public int initialPoolSize = 50;
    public bool usePooling = true;
    
    [Header("Прогресс")]
    public DungeonProgressManager progressManager;

    private int[,] map;
    private HashSet<Vector2Int> fallingPlatformCells = new HashSet<Vector2Int>();
    private Vector2Int startCell;
    private Vector2Int endCell;
    
    private List<GameObject> spawnedObjects = new List<GameObject>();

    // ===================== NETWORK =====================

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // Инициализируем пул перед генерацией
            if (usePooling)
            {
                NetworkObjectPoolManager.Instance.InitializePools(
                    floorPrefab, wallPrefab, startPrefab, endPrefab, 
                    initialPoolSize
                );
            }
            
            Generate();
            Build();
            PositionAllPlayers();
        }
    }

    public override void OnNetworkDespawn()
    {
        // Очищаем все объекты при деспавне
        Cleanup();
    }

    // ===================== GENERATION =====================

    void Generate()
    {
        map = new int[width, height];

        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                map[x, y] = 0;

        startCell = new Vector2Int(1, 1);
        Carve(startCell.x, startCell.y);

        endCell = FindFarthestCell(startCell);
    }

    void Carve(int x, int y)
    {
        map[x, y] = 1;

        Vector2Int[] dirs =
        {
            Vector2Int.up,
            Vector2Int.down,
            Vector2Int.left,
            Vector2Int.right
        };

        Shuffle(dirs);

        foreach (var d in dirs)
        {
            int nx = x + d.x * 2;
            int ny = y + d.y * 2;

            if (IsInside(nx, ny) && map[nx, ny] == 0)
            {
                map[x + d.x, y + d.y] = 1;
                Carve(nx, ny);
            }
        }
    }

    bool IsInside(int x, int y)
    {
        return x > 0 && y > 0 && x < width - 1 && y < height - 1;
    }

    // ===================== START / END =====================

    Vector2Int FindFarthestCell(Vector2Int from)
    {
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        Dictionary<Vector2Int, int> distances = new Dictionary<Vector2Int, int>();

        queue.Enqueue(from);
        distances[from] = 0;

        Vector2Int farthest = from;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var d in Directions)
            {
                Vector2Int next = current + d;

                if (IsInside(next.x, next.y) &&
                    map[next.x, next.y] == 1 &&
                    !distances.ContainsKey(next))
                {
                    distances[next] = distances[current] + 1;
                    queue.Enqueue(next);

                    if (distances[next] > distances[farthest])
                        farthest = next;
                }
            }
        }

        return farthest;
    }

    static readonly Vector2Int[] Directions =
    {
        Vector2Int.up,
        Vector2Int.down,
        Vector2Int.left,
        Vector2Int.right
    };

    // ===================== BUILD (SERVER ONLY) =====================

    void Build()
    {
        // Очищаем предыдущие объекты
        Cleanup();
        fallingPlatformCells.Clear();

        // ПЕРВОЕ: Спавним падающие платформы
        SpawnFallingPlatforms();

        // ВТОРОЕ: Спавним обычные полы и стены, исключая клетки с падающими платформами
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector3 pos = new Vector3(x * cellSize, 0, y * cellSize);
                var cell = new Vector2Int(x, y);

                // Пропускаем клетки с падающими платформами
                if (fallingPlatformCells.Contains(cell))
                    continue;

                // Проверяем, что это пол (не стена)
                if (map[x, y] == 1)
                {
                    // Спавним обычный пол
                    SpawnNetwork(floorPrefab, pos);
                }
                else
                {
                    // Спавним стену
                    SpawnNetwork(wallPrefab, pos);
                }
            }
        }

        // ТРЕТЬЕ: Спавним старт и финиш (проверяем конфликты)
        if (!fallingPlatformCells.Contains(startCell))
        {
            SpawnNetwork(startPrefab, CellToWorld(startCell));
        }
        else
        {
            // Если старт попал на падающую платформу, ищем ближайшую безопасную клетку
            var safeStart = FindNearestSafeCell(startCell);
            startCell = safeStart;
            SpawnNetwork(startPrefab, CellToWorld(safeStart));
        }

        if (!fallingPlatformCells.Contains(endCell))
        {
            SpawnNetwork(endPrefab, CellToWorld(endCell));
        }
        else
        {
            // Если финиш попал на падающую платформу, ищем ближайшую безопасную клетку
            var safeEnd = FindNearestSafeCell(endCell);
            endCell = safeEnd;
            SpawnNetwork(endPrefab, CellToWorld(safeEnd));
        }
        
        if (progressManager != null)
        {
            // Сообщаем менеджеру прогресса о новой конечной точке
            progressManager.Invoke("FindEndZone", 0.1f);
        }
    }

    void SpawnFallingPlatforms()
    {
        if (fallingPlatformPrefab == null) return;

        int chainCount = Random.Range(3, 6); // от 3 до 5 подряд
        bool horizontal = Random.value > 0.5f;

        // Пытаемся найти подходящее место
        for (int attempt = 0; attempt < 50; attempt++)
        {
            int x = Random.Range(1, width - 1);
            int y = Random.Range(1, height - 1);

            bool valid = true;
            List<Vector2Int> potentialCells = new List<Vector2Int>();

            // Проверяем всю цепочку
            for (int i = 0; i < chainCount; i++)
            {
                int cx = horizontal ? x + i : x;
                int cy = horizontal ? y : y + i;

                if (!IsInside(cx, cy) || map[cx, cy] != 1)
                {
                    valid = false;
                    break;
                }

                potentialCells.Add(new Vector2Int(cx, cy));
            }

            if (!valid) continue;

            // Спавним подряд
            for (int i = 0; i < chainCount; i++)
            {
                int cx = horizontal ? x + i : x;
                int cy = horizontal ? y : y + i;

                var cell = new Vector2Int(cx, cy);
                fallingPlatformCells.Add(cell);

                Vector3 pos = new Vector3(cx * cellSize, 0, cy * cellSize);
                SpawnNetwork(fallingPlatformPrefab, pos);
            }

            break;
        }
    }

    Vector2Int FindNearestSafeCell(Vector2Int targetCell)
    {
        // Поиск ближайшей безопасной клетки (не падающая платформа)
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        HashSet<Vector2Int> visited = new HashSet<Vector2Int>();
        
        queue.Enqueue(targetCell);
        visited.Add(targetCell);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            
            // Если эта клетка не падающая платформа и это пол - возвращаем
            if (!fallingPlatformCells.Contains(current) && 
                IsInside(current.x, current.y) && 
                map[current.x, current.y] == 1)
            {
                return current;
            }

            // Ищем дальше
            foreach (var d in Directions)
            {
                Vector2Int next = current + d;
                
                if (IsInside(next.x, next.y) && 
                    !visited.Contains(next) && 
                    map[next.x, next.y] == 1)
                {
                    queue.Enqueue(next);
                    visited.Add(next);
                }
            }
        }

        // Если не нашли безопасную клетку, возвращаем стартовую (1,1)
        return new Vector2Int(1, 1);
    }

    void SpawnNetwork(GameObject prefab, Vector3 position)
    {
        GameObject obj;
        
        if (usePooling && NetworkObjectPoolManager.Instance != null)
        {
            // Используем пул
            obj = NetworkObjectPoolManager.Instance.GetFromPool(prefab, position, Quaternion.identity);
        }
        else
        {
            // Стандартное создание
            obj = Instantiate(prefab, position, Quaternion.identity, transform);
        }
        
        var netObj = obj.GetComponent<NetworkObject>();
        if (!netObj.IsSpawned)
        {
            netObj.Spawn();
        }
        
        spawnedObjects.Add(obj);
    }

    Vector3 CellToWorld(Vector2Int cell)
    {
        return new Vector3(cell.x * cellSize, 0, cell.y * cellSize);
    }

    // ===================== PLAYERS =====================

    void PositionAllPlayers()
    {
        foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
        {
            var playerObject = client.PlayerObject;
            if (playerObject == null) continue;

            Vector3 spawnPos = CellToWorld(startCell);
            spawnPos.y = playerHeightOffset;

            playerObject.transform.position = spawnPos;
        }
    }

    // ===================== CLEANUP =====================

    void Cleanup()
    {
        if (usePooling && NetworkObjectPoolManager.Instance != null)
        {
            // Возвращаем объекты в пул
            foreach (var obj in spawnedObjects)
            {
                if (obj != null)
                {
                    NetworkObjectPoolManager.Instance.ReturnToPool(obj);
                }
            }
        }
        else
        {
            // Уничтожаем объекты стандартным способом
            foreach (var obj in spawnedObjects)
            {
                if (obj != null)
                {
                    if (obj.TryGetComponent<NetworkObject>(out var netObj))
                    {
                        netObj.Despawn();
                    }
                    Destroy(obj);
                }
            }
        }
        
        spawnedObjects.Clear();
    }

    // ===================== UTILITY =====================

    void Shuffle(Vector2Int[] array)
    {
        for (int i = 0; i < array.Length; i++)
        {
            int rnd = Random.Range(0, array.Length);
            (array[i], array[rnd]) = (array[rnd], array[i]);
        }
    }

    // ===================== DEBUG VISUALIZATION =====================

    void OnDrawGizmos()
    {
        if (map == null) return;

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector3 pos = new Vector3(x * cellSize, 0, y * cellSize);
                var cell = new Vector2Int(x, y);

                if (fallingPlatformCells.Contains(cell))
                {
                    Gizmos.color = Color.yellow;
                    Gizmos.DrawWireCube(pos + Vector3.up * 0.5f, Vector3.one * cellSize * 0.8f);
                }
                else if (map[x, y] == 1)
                {
                    Gizmos.color = Color.green;
                    Gizmos.DrawWireCube(pos, Vector3.one * cellSize * 0.5f);
                }
            }
        }

        // Старт
        Gizmos.color = Color.blue;
        Gizmos.DrawWireCube(CellToWorld(startCell) + Vector3.up, Vector3.one * cellSize * 0.7f);
        
        // Финиш
        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(CellToWorld(endCell) + Vector3.up, Vector3.one * cellSize * 0.7f);
    }

    // ===================== PUBLIC API =====================

    [ServerRpc(RequireOwnership = false)]
    public void RegenerateDungeonServerRpc()
    {
        if (!IsServer) return;
        
        Generate();
        Build();
        PositionAllPlayers();
    }

    [ClientRpc]
    public void UpdateDungeonClientRpc()
    {
        // Можно добавить визуальные эффекты или логику для клиентов
        Debug.Log("Данж обновлен на клиенте");
    }
}