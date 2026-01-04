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

    [Header("Player")]
    public float playerHeightOffset = 1f;

    [Header("Pool Settings")]
    public int initialPoolSize = 50;
    public bool usePooling = true;
    
    [Header("Прогресс")]
    public DungeonProgressManager progressManager;

    private int[,] map;
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

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector3 pos = new Vector3(x * cellSize, 0, y * cellSize);
                GameObject prefab = map[x, y] == 1 ? floorPrefab : wallPrefab;

                SpawnNetwork(prefab, pos);
            }
        }

        SpawnNetwork(startPrefab, CellToWorld(startCell));
        SpawnNetwork(endPrefab, CellToWorld(endCell));
        
        if (progressManager != null)
        {
            // Сообщаем менеджеру прогресса о новой конечной точке
            progressManager.Invoke("FindEndZone", 0.1f);
        }
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