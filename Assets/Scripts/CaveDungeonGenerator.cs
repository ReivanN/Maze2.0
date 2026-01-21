using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public class CaveDungeonGenerator : NetworkBehaviour
{
    [Header("Размер лабиринта")]
    public int width = 21;
    public int height = 21;
    public float cellSize = 2f;

    [Header("Префабы")]
    public GameObject floorPrefab;
    public GameObject wallPrefab;
    public GameObject startPrefab;
    public GameObject endPrefab;
    public GameObject doorPrefab;
    public GameObject buttonPrefab;

    [Header("Player")]
    public float playerHeightOffset = 1f;

    private int[,] map;

    private Vector2Int startA;
    private Vector2Int startB;
    private Vector2Int exitCell;

private List<GameObject> spawnedObjects = new List<GameObject>();

    // ================= NETWORK =================

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        Generate();
        Build();
        PositionPlayers();
    }

    // ================= GENERATION =================

    void Generate()
    {
        map = new int[width, height];

        // Всё — стены
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                map[x, y] = 0;

        int centerX = width / 2;
        int centerY = height / 2;

        // Старты
        startA = new Vector2Int(1, centerY);
        startB = new Vector2Int(width - 2, centerY);

        // Левая и правая генерация отдельно
        CarveSide(startA.x, startA.y, 1, centerX - 2);
        CarveSide(startB.x, startB.y, centerX + 2, width - 2);

        // Центральная зона
        CreateCentralZone(centerX, centerY);

        exitCell = new Vector2Int(centerX, centerY);
    }

    void CarveSide(int x, int y, int minX, int maxX)
    {
        map[x, y] = 1;

        Vector2Int[] dirs =
        {
            Vector2Int.up, Vector2Int.down,
            Vector2Int.left, Vector2Int.right
        };
        Shuffle(dirs);

        foreach (var d in dirs)
        {
            int nx = x + d.x * 2;
            int ny = y + d.y * 2;

            if (nx < minX || nx > maxX) continue;
            if (!IsInside(nx, ny)) continue;
            if (map[nx, ny] != 0) continue;

            map[x + d.x, y + d.y] = 1;
            CarveSide(nx, ny, minX, maxX);
        }
    }

    void CreateCentralZone(int cx, int cy)
    {
        // Центральный проход 3x3
        for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
                map[x, y] = 1;

        // Выход — строго одна клетка
        map[cx, cy] = 2;

        // Стена по центру
        for (int y = 0; y < height; y++)
        {
            if (y >= cy - 1 && y <= cy + 1) continue;
            map[cx, y] = 0;
        }
    }

    bool IsInside(int x, int y)
    {
        return x > 0 && y > 0 && x < width - 1 && y < height - 1;
    }

    // ================= BUILD =================

    void Build()
    {
        Cleanup();

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector3 pos = CellToWorld(x, y);

                if (map[x, y] == 0)
                    Spawn(wallPrefab, pos);
                else
                    Spawn(floorPrefab, pos);
            }
        }

        Spawn(startPrefab, CellToWorld(startA));
        Spawn(startPrefab, CellToWorld(startB));
        Spawn(endPrefab, CellToWorld(exitCell));

        PlaceDoorsAndButtons();
    }

    // ================= DOORS & BUTTONS =================

    void PlaceDoorsAndButtons()
    {
        Vector2Int doorA = FindRandomCell(true);
        Vector2Int buttonA = FindRandomCell(false);

        Vector2Int doorB = FindRandomCell(false);
        Vector2Int buttonB = FindRandomCell(true);

        Spawn(doorPrefab, CellToWorld(doorA));
        Spawn(buttonPrefab, CellToWorld(buttonA));

        Spawn(doorPrefab, CellToWorld(doorB));
        Spawn(buttonPrefab, CellToWorld(buttonB));
    }

    Vector2Int FindRandomCell(bool left)
    {
        List<Vector2Int> cells = new();
        int cx = width / 2;

        for (int x = left ? 1 : cx + 2; x < (left ? cx - 1 : width - 1); x++)
            for (int y = 1; y < height - 1; y++)
                if (map[x, y] == 1)
                    cells.Add(new Vector2Int(x, y));

        return cells[Random.Range(0, cells.Count)];
    }

    // ================= PLAYERS =================

    void PositionPlayers()
    {
        int i = 0;
        foreach (var c in NetworkManager.Singleton.ConnectedClientsList)
        {
            var p = c.PlayerObject;
            if (!p) continue;

            Vector3 pos = (i == 0 ? CellToWorld(startA) : CellToWorld(startB));
            pos.y = playerHeightOffset;
            p.transform.position = pos;
            i++;
        }
    }

    // ================= UTILS =================

    Vector3 CellToWorld(int x, int y)
    {
        return new Vector3(x * cellSize, 0, y * cellSize);
    }

    Vector3 CellToWorld(Vector2Int cell)
    {
        return new Vector3(cell.x * cellSize, 0, cell.y * cellSize);
    }

    void Spawn(GameObject prefab, Vector3 pos)
    {
        var obj = Instantiate(prefab, pos, Quaternion.identity);

        var netObj = obj.GetComponent<NetworkObject>();
        if (netObj != null && !netObj.IsSpawned)
        {
            netObj.Spawn();
        }

        spawnedObjects.Add(obj);
    }

    void Cleanup()
    {
        foreach (var o in spawnedObjects)
        {
            if (!o) continue;

            if (o.TryGetComponent<NetworkObject>(out var netObj) && netObj.IsSpawned)
                netObj.Despawn();
        }
        spawnedObjects.Clear();
    }

    void Shuffle(Vector2Int[] arr)
    {
        for (int i = 0; i < arr.Length; i++)
        {
            int r = Random.Range(0, arr.Length);
            (arr[i], arr[r]) = (arr[r], arr[i]);
        }
    }
}