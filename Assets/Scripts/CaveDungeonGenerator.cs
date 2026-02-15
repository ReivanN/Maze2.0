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
    private bool startAUsed = false;
    private bool startBUsed = false;

    private List<GameObject> spawnedObjects = new List<GameObject>();

    // ================= NETWORK =================

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        startAUsed = false;
        startBUsed = false;

        Generate();
        Build();
        PositionPlayers();
    }
    void OnClientConnected(ulong clientId)
    {
        if (!IsServer) return;
        PositionPlayers();
    }

    // ================= GENERATION =================

    void Generate()
    {
        map = new int[width, height];

        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                map[x, y] = 0;

        int cx = width / 2;
        int cy = height / 2;

        // --- Центральный вертикальный туннель (1 клетка шириной) ---
        for (int y = 1; y < height - 1; y++)
            map[cx, y] = 1;

        // --- Выход в центре туннеля ---
        exitCell = new Vector2Int(cx, cy);

        // --- Двери в туннель (слева и справа) ---
        Vector2Int doorLeft = new Vector2Int(cx - 1, cy);
        Vector2Int doorRight = new Vector2Int(cx + 1, cy);

        map[doorLeft.x, doorLeft.y] = 1;
        map[doorRight.x, doorRight.y] = 1;

        // --- Старты ---
        startA = new Vector2Int(1, cy);
        startB = new Vector2Int(width - 2, cy);

        // --- Генерация лабиринтов ---
        CarveSide(startA.x, startA.y, 1, cx - 2);
        CarveSide(startB.x, startB.y, cx + 2, width - 2);
    }

    void CarveSide(int x, int y, int minX, int maxX)
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

            if (nx < minX || nx > maxX) continue;
            if (!IsInside(nx, ny)) continue;
            if (map[nx, ny] != 0) continue;

            map[x + d.x, y + d.y] = 1;
            CarveSide(nx, ny, minX, maxX);
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
                    Spawn(wallPrefab, pos + Vector3.up * 3f);
                else
                    Spawn(floorPrefab, pos);
            }
        }

        Spawn(startPrefab, CellToWorld(startA));
        Spawn(startPrefab, CellToWorld(startB));
        Spawn(endPrefab, CellToWorld(exitCell));

        // --- Двери ---
        Vector2Int leftDoorCell = new Vector2Int(width / 2 - 1, height / 2);
        Vector2Int rightDoorCell = new Vector2Int(width / 2 + 1, height / 2);

        GameObject leftDoorObj = Spawn(doorPrefab, CellToWorld(leftDoorCell) + Vector3.up * 3f);
        GameObject rightDoorObj = Spawn(doorPrefab, CellToWorld(rightDoorCell) + Vector3.up * 3f);

        Door leftDoor = leftDoorObj.GetComponent<Door>();
        Door rightDoor = rightDoorObj.GetComponent<Door>();

        leftDoor.doorColor = DoorColor.Red;
        rightDoor.doorColor = DoorColor.Blue;

        // --- Поиск тупиков для кнопок ---
        List<Vector2Int> leftDeadEnds = new List<Vector2Int>();
        List<Vector2Int> rightDeadEnds = new List<Vector2Int>();

        int centerX = width / 2;

        for (int x = 1; x < width - 1; x++)
        {
            for (int y = 1; y < height - 1; y++)
            {
                if (map[x, y] != 1)
                    continue;

                int neighbours = 0;

                if (map[x + 1, y] == 1) neighbours++;
                if (map[x - 1, y] == 1) neighbours++;
                if (map[x, y + 1] == 1) neighbours++;
                if (map[x, y - 1] == 1) neighbours++;

                if (neighbours == 1)
                {
                    Vector2Int cell = new Vector2Int(x, y);

                    if (cell == startA || cell == startB || cell == exitCell)
                        continue;

                    if (x < centerX)
                        leftDeadEnds.Add(cell);
                    else if (x > centerX)
                        rightDeadEnds.Add(cell);
                }
            }
        }

        if (buttonPrefab != null && leftDeadEnds.Count > 0 && rightDeadEnds.Count > 0)
        {
            Vector2Int leftButtonPos = leftDeadEnds[Random.Range(0, leftDeadEnds.Count)];
            Vector2Int rightButtonPos = rightDeadEnds[Random.Range(0, rightDeadEnds.Count)];

            GameObject leftButtonObj = Spawn(buttonPrefab, CellToWorld(leftButtonPos) + Vector3.up * 1f);
            GameObject rightButtonObj = Spawn(buttonPrefab, CellToWorld(rightButtonPos) + Vector3.up * 1f);

            Button leftButton = leftButtonObj.GetComponent<Button>();
            Button rightButton = rightButtonObj.GetComponent<Button>();

            // Перекрёстная логика
            leftButton.buttonColor = DoorColor.Blue;
            rightButton.buttonColor = DoorColor.Red;

            leftButton.SetTargetDoor(rightDoor);
            rightButton.SetTargetDoor(leftDoor);
        }
    }

    // ================= PLAYERS =================

    void PositionPlayers()
    {
        // Сначала сервер (host), затем клиенты
        foreach (var c in NetworkManager.Singleton.ConnectedClientsList)
        {
            var player = c.PlayerObject;
            if (player == null) continue;

            Vector3 spawnPos;

            if (!startAUsed)
            {
                spawnPos = CellToWorld(startA);
                startAUsed = true;
            }
            else if (!startBUsed)
            {
                spawnPos = CellToWorld(startB);
                startBUsed = true;
            }
            else
            {
                // fallback: если стартов больше нет
                spawnPos = CellToWorld(startA);
            }

            spawnPos.y = playerHeightOffset;
            player.transform.position = spawnPos;
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

    GameObject Spawn(GameObject prefab, Vector3 pos)
    {
        var obj = Instantiate(prefab, pos, Quaternion.identity);

        var netObj = obj.GetComponent<NetworkObject>();
        if (netObj != null && !netObj.IsSpawned)
        {
            netObj.Spawn();
        }

        spawnedObjects.Add(obj);
        return obj;
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