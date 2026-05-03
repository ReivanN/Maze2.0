using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    public GameObject fallingPlatformPrefab;
    public GameObject laserTrapPrefab;
    public GameObject laserSwitchPrefab;

    [Header("Падающие платформы")]
    [SerializeField] private int fallingPlatformRowsPerSideMin = 2;
    [SerializeField] private int fallingPlatformRowsPerSideMax = 3;
    [SerializeField] private int fallingPlatformRowLengthMin = 2;
    [SerializeField] private int fallingPlatformRowLengthMax = 4;

    [Header("Лазерная ловушка")]
    [SerializeField] private float laserHeightOffset = 1f;
    [SerializeField] private float laserSwitchHeightOffset = 1f;

    [Header("Player")]
    public float playerHeightOffset = 1f;

    private int[,] map;

    private Vector2Int startA;
    private Vector2Int startB;
    private Vector2Int exitCell;
    private HashSet<Vector2Int> fallingPlatformCells = new HashSet<Vector2Int>();

    private List<GameObject> spawnedObjects = new List<GameObject>();
    private Task generationTask;

    // ================= NETWORK =================

    public override async void OnNetworkSpawn()
    {
        if (!IsServer) return;

        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;

        generationTask = GenerateDungeonAsync();
        await generationTask;
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
    }

    private async Task GenerateDungeonAsync()
    {
        await GenerateAsync();
        await BuildAsync();
        await PositionPlayersAsync();
    }

    async void OnClientConnected(ulong clientId)
    {
        if (!IsServer) return;

        if (generationTask != null)
            await generationTask;

        await PositionPlayerAsync(clientId);
    }

    // ================= GENERATION =================

    async Task GenerateAsync()
    {
        await Task.Yield();
        Generate();
        await Task.Yield();
    }

    void Generate()
    {
        fallingPlatformCells.Clear();
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

        ChooseFallingPlatformCells();
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

    void ChooseFallingPlatformCells()
    {
        if (fallingPlatformPrefab == null) return;

        int minRows = Mathf.Min(fallingPlatformRowsPerSideMin, fallingPlatformRowsPerSideMax);
        int maxRows = Mathf.Max(fallingPlatformRowsPerSideMin, fallingPlatformRowsPerSideMax);
        int minLength = Mathf.Min(fallingPlatformRowLengthMin, fallingPlatformRowLengthMax);
        int maxLength = Mathf.Max(fallingPlatformRowLengthMin, fallingPlatformRowLengthMax);

        minRows = Mathf.Max(0, minRows);
        maxRows = Mathf.Max(0, maxRows);
        minLength = Mathf.Max(1, minLength);
        maxLength = Mathf.Max(1, maxLength);

        int centerX = width / 2;

        PlaceFallingPlatformRows(1, centerX - 2, minRows, maxRows, minLength, maxLength);
        PlaceFallingPlatformRows(centerX + 2, width - 2, minRows, maxRows, minLength, maxLength);
    }

    void PlaceFallingPlatformRows(int minX, int maxX, int minRows, int maxRows, int minLength, int maxLength)
    {
        if (minX > maxX) return;

        int rowsToPlace = Random.Range(minRows, maxRows + 1);
        int attempts = rowsToPlace * 20;

        while (rowsToPlace > 0 && attempts-- > 0)
        {
            int rowLength = Random.Range(minLength, maxLength + 1);
            Vector2Int direction = Random.value > 0.5f ? Vector2Int.right : Vector2Int.up;
            Vector2Int startCell = new Vector2Int(Random.Range(minX, maxX + 1), Random.Range(1, height - 1));

            if (!CanPlaceFallingPlatformRow(startCell, direction, rowLength, minX, maxX))
                continue;

            for (int i = 0; i < rowLength; i++)
                fallingPlatformCells.Add(startCell + direction * i);

            rowsToPlace--;
        }
    }

    bool CanPlaceFallingPlatformRow(Vector2Int startCell, Vector2Int direction, int length, int minX, int maxX)
    {
        for (int i = 0; i < length; i++)
        {
            Vector2Int cell = startCell + direction * i;

            if (cell.x < minX || cell.x > maxX)
                return false;

            if (!IsWalkableFallingPlatformCell(cell))
                return false;

            if (fallingPlatformCells.Contains(cell))
                return false;
        }

        return true;
    }

    bool IsWalkableFallingPlatformCell(Vector2Int cell)
    {
        if (cell.x <= 0 || cell.y <= 0 || cell.x >= width - 1 || cell.y >= height - 1)
            return false;

        if (map[cell.x, cell.y] != 1)
            return false;

        if (cell == startA || cell == startB || cell == exitCell)
            return false;

        int centerX = width / 2;
        int centerY = height / 2;

        if (Mathf.Abs(cell.x - centerX) <= 1 && Mathf.Abs(cell.y - centerY) <= 1)
            return false;

        return true;
    }

    // ================= BUILD =================

    async Task BuildAsync()
    {
        Cleanup();

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector3 pos = CellToWorld(x, y);

                if (map[x, y] == 0)
                {
                    Spawn(wallPrefab, pos + Vector3.up * 3f);
                }
                else if (fallingPlatformCells.Contains(new Vector2Int(x, y)))
                {
                    Spawn(fallingPlatformPrefab, pos);
                }
                else
                {
                    Spawn(floorPrefab, pos);
                }
            }

            await Task.Yield();
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

        leftDoor.SetDoorColor(DoorColor.Red);
        rightDoor.SetDoorColor(DoorColor.Blue);

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

            leftDeadEnds.Remove(leftButtonPos);
            rightDeadEnds.Remove(rightButtonPos);

            TrySpawnLaserPuzzle(leftButtonPos, rightButtonPos, leftDeadEnds, rightDeadEnds);

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

        await Task.Yield();
    }

    void TrySpawnLaserPuzzle(
        Vector2Int leftKeyCell,
        Vector2Int rightKeyCell,
        List<Vector2Int> leftFreeDeadEnds,
        List<Vector2Int> rightFreeDeadEnds)
    {
        if (laserTrapPrefab == null || laserSwitchPrefab == null)
            return;

        bool canBlockLeftKey = rightFreeDeadEnds.Count > 0;
        bool canBlockRightKey = leftFreeDeadEnds.Count > 0;

        if (!canBlockLeftKey && !canBlockRightKey)
            return;

        bool blockLeftSide = canBlockLeftKey && (!canBlockRightKey || Random.value > 0.5f);

        Vector2Int protectedCell = blockLeftSide ? leftKeyCell : rightKeyCell;
        List<Vector2Int> switchCandidates = blockLeftSide ? rightFreeDeadEnds : leftFreeDeadEnds;

        if (!TryGetDeadEndEntry(protectedCell, out Vector2Int entryCell))
            return;

        Vector2Int switchCell = switchCandidates[Random.Range(0, switchCandidates.Count)];

        Vector3 laserPosition = CellToWorld(protectedCell);
        laserPosition.y = laserHeightOffset;

        Vector3 laserDirection = new Vector3(
            protectedCell.x - entryCell.x,
            0f,
            protectedCell.y - entryCell.y
        ).normalized;

        GameObject laserObj = Spawn(laserTrapPrefab, laserPosition);
        laserObj.transform.rotation = Quaternion.LookRotation(laserDirection, Vector3.up);

        GameObject switchObj = Spawn(laserSwitchPrefab, CellToWorld(switchCell) + Vector3.up * laserSwitchHeightOffset);

        LaserTrap laserTrap = laserObj.GetComponent<LaserTrap>();
        LaserSwitch laserSwitch = switchObj.GetComponent<LaserSwitch>();

        if (laserTrap != null && laserSwitch != null)
            laserSwitch.SetTargetLaser(laserTrap);
    }

    bool TryGetDeadEndEntry(Vector2Int deadEndCell, out Vector2Int entryCell)
    {
        Vector2Int[] directions =
        {
            Vector2Int.right,
            Vector2Int.left,
            Vector2Int.up,
            Vector2Int.down
        };

        foreach (Vector2Int direction in directions)
        {
            Vector2Int candidate = deadEndCell + direction;

            if (candidate.x <= 0 || candidate.y <= 0 || candidate.x >= width - 1 || candidate.y >= height - 1)
                continue;

            if (map[candidate.x, candidate.y] == 1)
            {
                entryCell = candidate;
                return true;
            }
        }

        entryCell = default;
        return false;
    }

    // ================= PLAYERS =================

    async Task PositionPlayersAsync()
    {
        await Task.Yield();

        foreach (var c in NetworkManager.Singleton.ConnectedClientsList)
        {
            await PositionPlayerAsync(c.ClientId);
        }
    }

    async Task PositionPlayerAsync(ulong clientId)
    {
        int waitFrames = 0;

        while (NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client) &&
               client.PlayerObject == null)
        {
            if (waitFrames++ > 120)
                return;

            await Task.Yield();
        }

        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var connectedClient))
            return;

        var player = connectedClient.PlayerObject;
        if (player == null) return;

        Vector2Int spawnCell = GetSpawnCell(clientId);
        Vector3 spawnPos = CellToWorld(spawnCell);
        spawnPos.y = playerHeightOffset;

        player.transform.position = spawnPos;

        TeleportPlayerClientRpc(spawnPos, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { clientId }
            }
        });
    }

    Vector2Int GetSpawnCell(ulong clientId)
    {
        return clientId == NetworkManager.ServerClientId ? startA : startB;
    }

    public Vector3 GetSpawnWorldPosition(ulong clientId)
    {
        return CellToWorld(GetSpawnCell(clientId));
    }

    [ClientRpc]
    void TeleportPlayerClientRpc(Vector3 spawnPos, ClientRpcParams clientRpcParams = default)
    {
        StartCoroutine(TeleportLocalPlayerWhenReady(spawnPos));
    }

    IEnumerator TeleportLocalPlayerWhenReady(Vector3 spawnPos)
    {
        int waitFrames = 0;

        while ((NetworkManager.Singleton == null ||
                NetworkManager.Singleton.LocalClient == null ||
                NetworkManager.Singleton.LocalClient.PlayerObject == null) &&
               waitFrames++ < 120)
        {
            yield return null;
        }

        if (NetworkManager.Singleton == null ||
            NetworkManager.Singleton.LocalClient == null ||
            NetworkManager.Singleton.LocalClient.PlayerObject == null)
        {
            yield break;
        }

        Transform playerTransform = NetworkManager.Singleton.LocalClient.PlayerObject.transform;
        CharacterController characterController = playerTransform.GetComponent<CharacterController>();

        if (characterController != null)
            characterController.enabled = false;

        playerTransform.position = spawnPos;

        if (characterController != null)
            characterController.enabled = true;
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
