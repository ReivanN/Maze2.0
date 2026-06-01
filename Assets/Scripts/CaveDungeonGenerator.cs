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
    [SerializeField] private float laserBottomHeightOffset = 0.35f;
    [SerializeField] private float laserBarrierHeight = 3f;
    [SerializeField] private float laserBarrierThickness = 0.45f;
    [SerializeField] private float laserWallPadding = 0.15f;
    [SerializeField] private float laserSwitchHeightOffset = 0.1f;

    [Header("Player")]
    public float playerHeightOffset = 1f;

    private int[,] map;

    private Vector2Int startA;
    private Vector2Int startB;
    private Vector2Int exitCell;
    private HashSet<Vector2Int> fallingPlatformCells = new HashSet<Vector2Int>();

    private List<GameObject> spawnedObjects = new List<GameObject>();
    private Task generationTask;
    private int baseWidth;
    private int baseHeight;
    private bool baseDimensionsInitialized;

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
        EnsureBaseDimensions();

        await GenerateAsync();
        await BuildAsync();
        await PositionPlayersAsync();
    }

    public async Task RegenerateDungeonForLevelAsync(
        int level,
        int widthIncreasePerLevel,
        int heightIncreasePerLevel,
        int maxWidth,
        int maxHeight)
    {
        if (!IsServer) return;

        EnsureBaseDimensions();
        ApplyLevelSize(level, widthIncreasePerLevel, heightIncreasePerLevel, maxWidth, maxHeight);

        generationTask = GenerateDungeonAsync();
        await generationTask;
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
        NormalizeMazeDimensions();
        fallingPlatformCells.Clear();
        map = new int[width, height];

        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                map[x, y] = 0;

        int cx = width / 2;
        int cy = height / 2;

        exitCell = new Vector2Int(cx, cy);
        Vector2Int doorLeft = new Vector2Int(cx - 1, cy);
        Vector2Int doorRight = new Vector2Int(cx + 1, cy);

        startA = new Vector2Int(1, cy);
        startB = new Vector2Int(width - 2, cy);

        GenerateSideGrowingTree(startA, 1, cx - 1);
        GenerateSideGrowingTree(startB, cx + 1, width - 2);

        map[exitCell.x, exitCell.y] = 1;
        map[doorLeft.x, doorLeft.y] = 1;
        map[doorRight.x, doorRight.y] = 1;

        ConnectAnchorToSide(doorLeft, 1, cx - 1);
        ConnectAnchorToSide(doorRight, cx + 1, width - 2);

        ChooseFallingPlatformCells();
    }

    void NormalizeMazeDimensions()
    {
        width = Mathf.Max(13, width);
        height = Mathf.Max(9, height);

        if (width % 2 == 0)
            width++;

        if (height % 2 == 0)
            height++;

        while (width % 4 != 1)
            width += 2;

        while (height % 4 != 1)
            height += 2;
    }

    void EnsureBaseDimensions()
    {
        if (baseDimensionsInitialized)
            return;

        NormalizeMazeDimensions();
        baseWidth = width;
        baseHeight = height;
        baseDimensionsInitialized = true;
    }

    void ApplyLevelSize(int level, int widthIncreasePerLevel, int heightIncreasePerLevel, int maxWidth, int maxHeight)
    {
        int progressionLevel = Mathf.Max(1, level) - 1;
        width = ClampToProgressionSize(baseWidth + progressionLevel * Mathf.Max(0, widthIncreasePerLevel), 13, maxWidth);
        height = ClampToProgressionSize(baseHeight + progressionLevel * Mathf.Max(0, heightIncreasePerLevel), 9, maxHeight);
    }

    int ClampToProgressionSize(int value, int minValue, int maxValue)
    {
        int normalized = NormalizeProgressionDimension(value, minValue);

        if (maxValue <= 0)
            return normalized;

        int normalizedMax = NormalizeProgressionDimension(maxValue, minValue);

        while (normalized > normalizedMax && normalized > minValue)
            normalized -= 4;

        return Mathf.Max(minValue, normalized);
    }

    int NormalizeProgressionDimension(int value, int minValue)
    {
        value = Mathf.Max(minValue, value);

        if (value % 2 == 0)
            value++;

        while (value % 4 != 1)
            value += 2;

        return value;
    }

    void GenerateSideGrowingTree(Vector2Int seed, int minX, int maxX)
    {
        List<Vector2Int> active = new List<Vector2Int>();
        map[seed.x, seed.y] = 1;
        active.Add(seed);

        while (active.Count > 0)
        {
            int currentIndex = ChooseGrowingTreeIndex(active.Count);
            Vector2Int current = active[currentIndex];

            List<Vector2Int> availableDirections = GetAvailableDirections(current, minX, maxX);

            if (availableDirections.Count == 0)
            {
                active.RemoveAt(currentIndex);
                continue;
            }

            Vector2Int direction = availableDirections[Random.Range(0, availableDirections.Count)];
            Vector2Int between = current + direction;
            Vector2Int next = current + direction * 2;

            map[between.x, between.y] = 1;
            map[next.x, next.y] = 1;
            active.Add(next);
        }
    }

    int ChooseGrowingTreeIndex(int count)
    {
        if (count <= 1)
            return 0;

        if (Random.value < 0.7f)
            return count - 1;

        return Random.Range(0, count);
    }

    List<Vector2Int> GetAvailableDirections(Vector2Int current, int minX, int maxX)
    {
        List<Vector2Int> directions = new List<Vector2Int>(4);
        Vector2Int[] candidates =
        {
            Vector2Int.up,
            Vector2Int.down,
            Vector2Int.left,
            Vector2Int.right
        };

        Shuffle(candidates);

        foreach (Vector2Int direction in candidates)
        {
            Vector2Int next = current + direction * 2;

            if (next.x < minX || next.x > maxX)
                continue;

            if (!IsInside(next.x, next.y))
                continue;

            if (map[next.x, next.y] != 0)
                continue;

            directions.Add(direction);
        }

        return directions;
    }

    void ConnectAnchorToSide(Vector2Int anchorCell, int minX, int maxX)
    {
        Vector2Int[] directions =
        {
            Vector2Int.left,
            Vector2Int.right,
            Vector2Int.up,
            Vector2Int.down
        };

        foreach (Vector2Int direction in directions)
        {
            Vector2Int neighbour = anchorCell + direction;

            if (neighbour.x < minX || neighbour.x > maxX)
                continue;

            if (!IsInside(neighbour.x, neighbour.y))
                continue;

            if (map[neighbour.x, neighbour.y] == 1)
                return;
        }

        List<Vector2Int> candidates = new List<Vector2Int>(4);

        foreach (Vector2Int direction in directions)
        {
            Vector2Int neighbour = anchorCell + direction;
            Vector2Int target = anchorCell + direction * 2;

            if (neighbour.x < minX || neighbour.x > maxX)
                continue;

            if (target.x < minX || target.x > maxX)
                continue;

            if (!IsInside(target.x, target.y))
                continue;

            if (map[target.x, target.y] != 1)
                continue;

            candidates.Add(direction);
        }

        if (candidates.Count > 0)
        {
            Vector2Int direction = candidates[Random.Range(0, candidates.Count)];
            Vector2Int neighbour = anchorCell + direction;
            map[neighbour.x, neighbour.y] = 1;
            return;
        }

        CarveStraightToNearestPath(anchorCell, minX, maxX);
    }

    void CarveStraightToNearestPath(Vector2Int anchorCell, int minX, int maxX)
    {
        Vector2Int horizontal = anchorCell.x < width / 2 ? Vector2Int.left : Vector2Int.right;
        Vector2Int current = anchorCell;

        while (true)
        {
            Vector2Int next = current + horizontal;

            if (next.x < minX || next.x > maxX || !IsInside(next.x, next.y))
                return;

            map[next.x, next.y] = 1;

            if (HasWalkableNeighbourExcept(next, current))
                return;

            current = next;
        }
    }

    bool HasWalkableNeighbourExcept(Vector2Int cell, Vector2Int excluded)
    {
        Vector2Int[] directions =
        {
            Vector2Int.left,
            Vector2Int.right,
            Vector2Int.up,
            Vector2Int.down
        };

        foreach (Vector2Int direction in directions)
        {
            Vector2Int neighbour = cell + direction;

            if (neighbour == excluded)
                continue;

            if (!IsInside(neighbour.x, neighbour.y))
                continue;

            if (map[neighbour.x, neighbour.y] == 1)
                return true;
        }

        return false;
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

        GameObject leftDoorObj = Spawn(doorPrefab, CellToWorld(leftDoorCell) + Vector3.up * 1.5f);
        GameObject rightDoorObj = Spawn(doorPrefab, CellToWorld(rightDoorCell) + Vector3.up * 1.5f);

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

            GameObject leftButtonObj = Spawn(buttonPrefab, CellToWorld(leftButtonPos) + Vector3.up * 0.01f);
            GameObject rightButtonObj = Spawn(buttonPrefab, CellToWorld(rightButtonPos) + Vector3.up * 0.01f);

            Button leftButton = leftButtonObj.GetComponent<Button>();
            Button rightButton = rightButtonObj.GetComponent<Button>();

            if (leftButton == null || rightButton == null)
            {
                Debug.LogError("Button prefab must have a Button component.");
                return;
            }

            // Перекрёстная логика
            leftButton.SetButtonColor(DoorColor.Blue);
            rightButton.SetButtonColor(DoorColor.Red);
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

        bool canBlockLeftKey = HasSwitchCandidate(rightFreeDeadEnds, leftKeyCell, rightKeyCell, true);
        bool canBlockRightKey = HasSwitchCandidate(leftFreeDeadEnds, leftKeyCell, rightKeyCell, false);

        if (!canBlockLeftKey && !canBlockRightKey)
            return;

        bool blockLeftSide = canBlockLeftKey && (!canBlockRightKey || Random.value > 0.5f);

        Vector2Int protectedCell = blockLeftSide ? leftKeyCell : rightKeyCell;
        List<Vector2Int> switchCandidates = GetSwitchCandidates(
            blockLeftSide ? rightFreeDeadEnds : leftFreeDeadEnds,
            leftKeyCell,
            rightKeyCell,
            blockLeftSide);

        if (!TryGetDeadEndEntry(protectedCell, out Vector2Int entryCell))
            return;

        if (switchCandidates.Count == 0)
            return;

        Vector2Int switchCell = switchCandidates[Random.Range(0, switchCandidates.Count)];
        Vector2Int corridorDirection = protectedCell - entryCell;
        Vector2Int laserBeamDirection = new Vector2Int(-corridorDirection.y, corridorDirection.x);

        if (laserBeamDirection == Vector2Int.zero)
            return;

        Vector3 laserPosition = CellToWorld(entryCell);
        laserPosition.y = laserBottomHeightOffset;

        Vector3 laserDirection = CellDirectionToWorld(laserBeamDirection).normalized;

        GameObject laserObj = Spawn(laserTrapPrefab, laserPosition);
        laserObj.transform.rotation = Quaternion.LookRotation(laserDirection, Vector3.up);

        GameObject switchObj = Spawn(laserSwitchPrefab, CellToWorld(switchCell) + Vector3.up * laserSwitchHeightOffset);

        LaserTrap laserTrap = laserObj.GetComponent<LaserTrap>();
        LaserSwitch laserSwitch = switchObj.GetComponent<LaserSwitch>();

        if (laserTrap != null && TryGetLaserSegment(entryCell, laserBeamDirection, out Vector3 segmentStart, out Vector3 segmentEnd))
            laserTrap.ConfigureBarrier(segmentStart, segmentEnd, laserBarrierHeight, laserBarrierThickness);

        if (laserTrap != null && laserSwitch != null)
            laserSwitch.SetTargetLaser(laserTrap);
    }

    bool HasSwitchCandidate(List<Vector2Int> preferredCandidates, Vector2Int leftKeyCell, Vector2Int rightKeyCell, bool useRightSide)
    {
        return GetSwitchCandidates(preferredCandidates, leftKeyCell, rightKeyCell, useRightSide).Count > 0;
    }

    List<Vector2Int> GetSwitchCandidates(List<Vector2Int> preferredCandidates, Vector2Int leftKeyCell, Vector2Int rightKeyCell, bool useRightSide)
    {
        HashSet<Vector2Int> excluded = new HashSet<Vector2Int>
        {
            leftKeyCell,
            rightKeyCell,
            startA,
            startB,
            exitCell
        };

        List<Vector2Int> candidates = new List<Vector2Int>();
        foreach (Vector2Int candidate in preferredCandidates)
        {
            if (IsValidLaserSwitchCell(candidate, useRightSide, excluded))
                candidates.Add(candidate);
        }

        if (candidates.Count > 0)
            return candidates;

        int centerX = width / 2;
        int minX = useRightSide ? centerX + 1 : 1;
        int maxX = useRightSide ? width - 2 : centerX - 1;

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = 1; y < height - 1; y++)
            {
                Vector2Int cell = new Vector2Int(x, y);
                if (IsValidLaserSwitchCell(cell, useRightSide, excluded))
                    candidates.Add(cell);
            }
        }

        return candidates;
    }

    bool IsValidLaserSwitchCell(Vector2Int cell, bool useRightSide, HashSet<Vector2Int> excluded)
    {
        if (excluded.Contains(cell))
            return false;

        if (!IsInside(cell.x, cell.y))
            return false;

        if (map[cell.x, cell.y] != 1)
            return false;

        int centerX = width / 2;
        if (useRightSide && cell.x <= centerX)
            return false;

        if (!useRightSide && cell.x >= centerX)
            return false;

        if (fallingPlatformCells.Contains(cell))
            return false;

        return true;
    }

    bool TryGetLaserSegment(Vector2Int originCell, Vector2Int beamDirection, out Vector3 segmentStart, out Vector3 segmentEnd)
    {
        segmentStart = default;
        segmentEnd = default;

        if (beamDirection == Vector2Int.zero)
            return false;

        Vector2Int negativeCell = FindLastWalkableCell(originCell, -beamDirection);
        Vector2Int positiveCell = FindLastWalkableCell(originCell, beamDirection);
        Vector3 worldDirection = CellDirectionToWorld(beamDirection).normalized;
        float edgeOffset = Mathf.Max(0f, cellSize * 0.5f - laserWallPadding);

        segmentStart = CellToWorld(negativeCell) - worldDirection * edgeOffset;
        segmentEnd = CellToWorld(positiveCell) + worldDirection * edgeOffset;
        float floorY = CellToWorld(originCell).y;
        segmentStart.y = floorY + laserBottomHeightOffset;
        segmentEnd.y = floorY + laserBottomHeightOffset;

        return true;
    }

    Vector2Int FindLastWalkableCell(Vector2Int originCell, Vector2Int direction)
    {
        Vector2Int current = originCell;

        while (true)
        {
            Vector2Int next = current + direction;

            if (next.x <= 0 || next.y <= 0 || next.x >= width - 1 || next.y >= height - 1)
                return current;

            if (map[next.x, next.y] != 1)
                return current;

            current = next;
        }
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

    Vector3 CellCenterToWorld(Vector2Int cell)
    {
        float halfCell = cellSize * 0.5f;
        return new Vector3(cell.x * cellSize + halfCell, 0f, cell.y * cellSize + halfCell);
    }

    Vector3 CellDirectionToWorld(Vector2Int direction)
    {
        return new Vector3(direction.x, 0f, direction.y);
    }

    GameObject Spawn(GameObject prefab, Vector3 pos)
    {
        var obj = Instantiate(prefab, pos, Quaternion.identity);

        var netObj = obj.GetComponent<NetworkObject>();
        if (netObj != null && !netObj.IsSpawned)
        {
            netObj.Spawn();
            ShowToConnectedClients(netObj);
        }

        spawnedObjects.Add(obj);
        return obj;
    }

    void ShowToConnectedClients(NetworkObject netObj)
    {
        if (NetworkManager.Singleton == null)
            return;

        foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            if (!netObj.IsNetworkVisibleTo(clientId))
                netObj.NetworkShow(clientId);
        }
    }

    void Cleanup()
    {
        foreach (var o in spawnedObjects)
        {
            if (!o) continue;

            if (o.TryGetComponent<NetworkObject>(out var netObj) && netObj.IsSpawned)
            {
                netObj.Despawn();
            }
            else
            {
                Destroy(o);
            }
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
