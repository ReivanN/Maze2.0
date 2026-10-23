using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Netcode;

public class CaveDungeonGenerator : NetworkBehaviour
{
    [Header("Размер лабиринта")]
    public int width = 25;
    public int height = 23;
    [Tooltip("Ширина коридоров, м (клетки с нечётными координатами). Робот и игрок проходят рядом.")]
    public float corridorWidth = 4f;
    [Tooltip("Толщина стен, м (клетки с чётными координатами).")]
    public float wallThickness = 2f;
    [Tooltip("Под какой размер клетки сделаны префабы пола, стен, дверей и платформ (2 x 2 м).")]
    [SerializeField] private float prefabCellSize = 2f;
    [Tooltip("Высота префаба стены; стены становятся выше на высоту верхнего этажа.")]
    [SerializeField] private float wallPrefabHeight = 6f;

    [Header("Перепады высот")]
    [Tooltip("Сколько этажей над нижним: 2 = коридоры на трёх высотах.")]
    [SerializeField] private int maxFloorLevel = 2;
    [SerializeField] private float floorLevelHeight = 1.2f;
    [Tooltip("Шанс, что проход между двумя клетками станет пандусом вверх или вниз.")]
    [Range(0f, 1f)] [SerializeField] private float rampChance = 0.35f;

    [Header("Петли")]
    [Tooltip("Доля стен между соседними коридорами, которые убираются, чтобы появились обходные пути.")]
    [Range(0f, 1f)] [SerializeField] private float loopChance = 0.12f;

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
    [Tooltip("Cube pushed onto the laser plate. Empty = Resources/PushableCube.")]
    public GameObject pushableCubePrefab;
    [Tooltip("Floor spikes. Empty = Resources/SpikeTrap.")]
    public GameObject spikeTrapPrefab;
    [Tooltip("Item portal, one per half. Empty = Resources/ItemPortal.")]
    public GameObject itemPortalPrefab;
    [Tooltip("Heart pickup. Empty = Resources/HealthPickup.")]
    public GameObject healthPickupPrefab;
    [Tooltip("Patrolling robot. Empty = Resources/RobotEnemy.")]
    public GameObject robotEnemyPrefab;
    [Tooltip("Knife that knocks robots out. Empty = Resources/KnifePickup.")]
    public GameObject knifePickupPrefab;

    [Header("Роботы")]
    [SerializeField] private int robotsPerSide = 1;
    [Tooltip("Робот появляется не ближе этого расстояния (в клетках пути) к старту игрока.")]
    [SerializeField] private int robotMinDistanceFromStart = 6;

    [Header("Нож")]
    [SerializeField] private int knivesPerSide = 1;

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

    [Header("Куб для нажимной плиты")]
    [Tooltip("Минимальная длина пути (в клетках), который куб нужно протолкать до плиты.")]
    [SerializeField] private int cubeMinPathLength = 4;
    [SerializeField] private int cubeMaxPathLength = 10;
    [SerializeField] private float cubeSpawnHeight = 0.6f;
    [Tooltip("Шанс, что куб появится на другой половине лабиринта (тогда его передают через портал).")]
    [Range(0f, 1f)] [SerializeField] private float cubeOnOtherSideChance = 0.5f;

    [Header("Предметы")]
    [Tooltip("Сколько сердец-аптечек лежит на каждой половине.")]
    [SerializeField] private int healthPickupsPerSide = 1;
    [SerializeField] private float healthPickupSpawnHeight = 0.4f;

    private LaserSwitch laserPlate;
    private Vector2Int laserPlateCell;
    private bool laserPlateOnRightSide;
    private bool hasItemPortals;

    [Header("Шипы")]
    [SerializeField] private int spikeTrapsPerSideMin = 2;
    [SerializeField] private int spikeTrapsPerSideMax = 3;
    [Tooltip("Шипы не ставятся ближе этого расстояния (в клетках) к старту игрока.")]
    [SerializeField] private int spikeMinDistanceFromStart = 3;
    [SerializeField] private float spikeHeightOffset = 0.01f;

    // Cells taken by buttons, the laser, its plate and cube: no spikes there.
    private readonly HashSet<Vector2Int> reservedCells = new HashSet<Vector2Int>();

    [Header("Финиш")]
    [SerializeField] private float endHeightOffset = 0.01f;

    [Header("Player")]
    public float playerHeightOffset = 1f;

    [Header("Падение за карту")]
    [Tooltip("Игрок ниже этой высоты считается упавшим за карту и возрождается на своём старте, как после лазера.")]
    [SerializeField] private float fallRespawnHeight = -8f;
    [Tooltip("Пауза перед повторной проверкой: сервер видит новую позицию игрока только после его следующего обновления.")]
    [SerializeField] private float fallRespawnCooldown = 1.5f;

    private readonly Dictionary<ulong, float> lastFallRespawnTime = new Dictionary<ulong, float>();

    private int[,] map;

    // Floor height of every walkable cell, in levels (floorLevelHeight each). A ramp connects two levels along one
    // axis: rampNegLevel at its smaller-coordinate end, rampPosLevel at the other one.
    private int[,] floorLevel;
    private bool[,] isRamp;
    private bool[,] rampAlongX;
    private int[,] rampNegLevel;
    private int[,] rampPosLevel;

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

        // Door cells are kept out of the maze tree: otherwise a corridor can run through a door cell and
        // everything behind it is reachable only through the closed door. Marked as "taken" (2) here,
        // they become walkable below and get exactly one connection to their side (ConnectAnchorToSide).
        map[doorLeft.x, doorLeft.y] = 2;
        map[doorRight.x, doorRight.y] = 2;

        GenerateSideGrowingTree(startA, 1, cx - 1);
        GenerateSideGrowingTree(startB, cx + 1, width - 2);

        map[exitCell.x, exitCell.y] = 1;
        map[doorLeft.x, doorLeft.y] = 1;
        map[doorRight.x, doorRight.y] = 1;

        ConnectAnchorToSide(doorLeft, 1, cx - 1);
        ConnectAnchorToSide(doorRight, cx + 1, width - 2);

        AssignFloorLevels();
        AddLoops(false);
        AddLoops(true);

        ChooseFallingPlatformCells();
    }

    // ================= HEIGHTS AND LOOPS =================

    void AssignFloorLevels()
    {
        // The tree maze has no cycles, so heights spread from the doors never contradict each other. The corridor
        // carved to a door can in rare cases close a cycle; then a few new tries, and a flat maze as the last resort.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            SpreadFloorLevels(attempt < 5 ? rampChance : 0f);
            if (FloorLevelsConsistent())
                return;
        }
    }

    /// <summary>
    /// Breadth-first from the doors and the exit (level 0) over the whole maze. Every straight passage between two
    /// corridor cells may become a ramp one level up or down; everything after it continues at the new level.
    /// </summary>
    void SpreadFloorLevels(float chance)
    {
        floorLevel = new int[width, height];
        isRamp = new bool[width, height];
        rampAlongX = new bool[width, height];
        rampNegLevel = new int[width, height];
        rampPosLevel = new int[width, height];

        int cx = width / 2;
        int cy = height / 2;
        bool[,] visited = new bool[width, height];
        Queue<Vector2Int> queue = new Queue<Vector2Int>();

        foreach (Vector2Int seed in new[] { new Vector2Int(cx, cy), new Vector2Int(cx - 1, cy), new Vector2Int(cx + 1, cy) })
        {
            visited[seed.x, seed.y] = true;
            queue.Enqueue(seed);
        }

        int topLevel = Mathf.Max(0, maxFloorLevel);

        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();

            foreach (Vector2Int direction in CardinalDirections)
            {
                Vector2Int next = current + direction;
                if (!IsInside(next.x, next.y) || map[next.x, next.y] != 1 || visited[next.x, next.y])
                    continue;

                visited[next.x, next.y] = true;
                queue.Enqueue(next);

                int level = EdgeLevel(current, direction);
                bool ramp = topLevel > 0 && Random.value < chance && CanBeRamp(next, direction, visited);

                if (!ramp)
                {
                    floorLevel[next.x, next.y] = level;
                    continue;
                }

                int target = level == 0 ? 1 : level == topLevel ? level - 1 : level + (Random.value < 0.5f ? 1 : -1);
                bool positive = direction.x > 0 || direction.y > 0;

                isRamp[next.x, next.y] = true;
                rampAlongX[next.x, next.y] = direction.x != 0;
                rampNegLevel[next.x, next.y] = positive ? level : target;
                rampPosLevel[next.x, next.y] = positive ? target : level;
                floorLevel[next.x, next.y] = Mathf.Min(level, target);
            }
        }
    }

    /// <summary>Level of a cell at its edge facing the given direction (differs from the other edge on a ramp).</summary>
    int EdgeLevel(Vector2Int cell, Vector2Int direction)
    {
        if (!isRamp[cell.x, cell.y])
            return floorLevel[cell.x, cell.y];

        return direction.x > 0 || direction.y > 0 ? rampPosLevel[cell.x, cell.y] : rampNegLevel[cell.x, cell.y];
    }

    // A ramp is a passage between two corridor cells (thin wall slot along the way, corridor-wide across it),
    // walls on both of its sides, and the cell after it not reached yet.
    bool CanBeRamp(Vector2Int cell, Vector2Int direction, bool[,] visited)
    {
        bool alongX = direction.x != 0;
        bool passage = alongX ? cell.x % 2 == 0 && cell.y % 2 == 1 : cell.y % 2 == 0 && cell.x % 2 == 1;
        if (!passage)
            return false;

        Vector2Int after = cell + direction;
        if (!IsInside(after.x, after.y) || map[after.x, after.y] != 1 || visited[after.x, after.y])
            return false;

        Vector2Int side = alongX ? Vector2Int.up : Vector2Int.right;
        if (IsWalkableCell(cell + side) || IsWalkableCell(cell - side))
            return false;

        Vector2Int leftDoor = new Vector2Int(width / 2 - 1, height / 2);
        Vector2Int rightDoor = new Vector2Int(width / 2 + 1, height / 2);
        return after != leftDoor && after != rightDoor && after != startA && after != startB;
    }

    // Two walkable neighbours must meet at the same height, otherwise there is a cliff between them.
    bool FloorLevelsConsistent()
    {
        for (int x = 1; x < width - 1; x++)
        {
            for (int y = 1; y < height - 1; y++)
            {
                if (map[x, y] != 1)
                    continue;

                Vector2Int cell = new Vector2Int(x, y);
                foreach (Vector2Int direction in new[] { Vector2Int.right, Vector2Int.up })
                {
                    Vector2Int next = cell + direction;
                    if (IsWalkableCell(next) && EdgeLevel(cell, direction) != EdgeLevel(next, -direction))
                        return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Removes some walls between two corridor cells of one half, so there is more than one way around.
    /// Cells at the same height get a flat passage, one level apart a ramp; farther apart the wall stays.
    /// </summary>
    void AddLoops(bool useRightSide)
    {
        if (loopChance <= 0f)
            return;

        int cx = width / 2;
        int sideMin = useRightSide ? cx + 1 : 1;
        int sideMax = useRightSide ? width - 2 : cx - 1;
        Vector2Int leftDoor = new Vector2Int(cx - 1, height / 2);
        Vector2Int rightDoor = new Vector2Int(cx + 1, height / 2);

        for (int x = sideMin; x <= sideMax; x++)
        {
            for (int y = 1; y < height - 1; y++)
            {
                if (map[x, y] != 0)
                    continue;

                bool alongX = x % 2 == 0 && y % 2 == 1;
                bool alongY = x % 2 == 1 && y % 2 == 0;
                if (!alongX && !alongY)
                    continue;

                Vector2Int step = alongX ? Vector2Int.right : Vector2Int.up;
                Vector2Int a = new Vector2Int(x, y) - step;
                Vector2Int b = new Vector2Int(x, y) + step;

                if (a.x < sideMin || b.x > sideMax || !IsWalkableCell(a) || !IsWalkableCell(b))
                    continue;

                if (a == leftDoor || a == rightDoor || b == leftDoor || b == rightDoor || isRamp[a.x, a.y] || isRamp[b.x, b.y])
                    continue;

                int levelA = floorLevel[a.x, a.y];
                int levelB = floorLevel[b.x, b.y];
                if (Mathf.Abs(levelA - levelB) > 1 || Random.value >= loopChance)
                    continue;

                map[x, y] = 1;
                floorLevel[x, y] = Mathf.Min(levelA, levelB);

                if (levelA != levelB)
                {
                    isRamp[x, y] = true;
                    rampAlongX[x, y] = alongX;
                    rampNegLevel[x, y] = levelA;
                    rampPosLevel[x, y] = levelB;
                }
            }
        }
    }

    bool IsWalkableCell(Vector2Int cell)
    {
        return IsInside(cell.x, cell.y) && map[cell.x, cell.y] == 1;
    }

    bool IsRampCell(Vector2Int cell)
    {
        return isRamp != null && cell.x >= 0 && cell.y >= 0 && cell.x < width && cell.y < height && isRamp[cell.x, cell.y];
    }

    // Maze cells are at odd coordinates, walls between them at even ones. The middle row (starts, doors, exit)
    // is height / 2, so it must be odd: height % 4 == 3. With height % 4 == 1 the middle row was even, every
    // corridor ran on even rows and the rows next to the top and bottom border stayed solid (2-block walls).
    // The middle column (width / 2) is the wall between the halves, so it must be even: width % 4 == 1.
    const int WidthRemainder = 1;
    const int HeightRemainder = 3;

    void NormalizeMazeDimensions()
    {
        width = NormalizeProgressionDimension(width, 13, WidthRemainder);
        height = NormalizeProgressionDimension(height, 9, HeightRemainder);
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
        width = ClampToProgressionSize(baseWidth + progressionLevel * Mathf.Max(0, widthIncreasePerLevel), 13, maxWidth, WidthRemainder);
        height = ClampToProgressionSize(baseHeight + progressionLevel * Mathf.Max(0, heightIncreasePerLevel), 9, maxHeight, HeightRemainder);
    }

    int ClampToProgressionSize(int value, int minValue, int maxValue, int remainder)
    {
        int normalized = NormalizeProgressionDimension(value, minValue, remainder);

        if (maxValue <= 0)
            return normalized;

        int normalizedMax = NormalizeProgressionDimension(maxValue, minValue, remainder);
        int normalizedMin = NormalizeProgressionDimension(minValue, minValue, remainder);

        // Steps of 4 keep the remainder, so the result is always a valid size.
        while (normalized > normalizedMax && normalized - 4 >= normalizedMin)
            normalized -= 4;

        return normalized;
    }

    int NormalizeProgressionDimension(int value, int minValue, int remainder)
    {
        value = Mathf.Max(minValue, value);

        if (value % 2 == 0)
            value++;

        while (value % 4 != remainder)
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

        if (cell == startA || cell == startB || cell == exitCell || isRamp[cell.x, cell.y])
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
        reservedCells.Clear();
        laserPlate = null;
        hasItemPortals = false;

        // Walls are taller by the height of the top floor, so they stand as high above every corridor.
        float wallTotalHeight = wallPrefabHeight + Mathf.Max(0, maxFloorLevel) * floorLevelHeight;

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                Vector3 pos = CellToWorld(x, y);
                Vector3 cellScale = CellScale(x, y);

                if (map[x, y] == 0)
                {
                    Vector3 wallScale = new Vector3(cellScale.x, wallTotalHeight / wallPrefabHeight, cellScale.z);
                    Spawn(wallPrefab, new Vector3(pos.x, wallTotalHeight * 0.5f, pos.z), null, wallScale);
                }
                else if (isRamp[x, y])
                {
                    SpawnRamp(x, y);
                }
                else if (fallingPlatformCells.Contains(new Vector2Int(x, y)))
                {
                    Spawn(fallingPlatformPrefab, pos, null, cellScale);
                }
                else
                {
                    Spawn(floorPrefab, pos, null, cellScale);
                }
            }

            await Task.Yield();
        }

        Spawn(startPrefab, CellToWorld(startA));
        Spawn(startPrefab, CellToWorld(startB));
        Spawn(endPrefab, CellToWorld(exitCell) + Vector3.up * endHeightOffset);

        // --- Двери ---
        Vector2Int leftDoorCell = new Vector2Int(width / 2 - 1, height / 2);
        Vector2Int rightDoorCell = new Vector2Int(width / 2 + 1, height / 2);

        // A door fills its whole (corridor-wide) cell.
        GameObject leftDoorObj = Spawn(doorPrefab, CellToWorld(leftDoorCell) + Vector3.up * 1.5f, null, CellScale(leftDoorCell.x, leftDoorCell.y));
        GameObject rightDoorObj = Spawn(doorPrefab, CellToWorld(rightDoorCell) + Vector3.up * 1.5f, null, CellScale(rightDoorCell.x, rightDoorCell.y));

        Door leftDoor = leftDoorObj.GetComponent<Door>();
        Door rightDoor = rightDoorObj.GetComponent<Door>();

        leftDoor.SetDoorColor(DoorColor.Red);
        rightDoor.SetDoorColor(DoorColor.Blue);

        // The door camera shot looks from the door's half towards the exit in the middle.
        leftDoor.SetCutsceneViewDirection(Vector3.right);
        rightDoor.SetCutsceneViewDirection(Vector3.left);

        // --- Поиск тупиков для кнопок ---
        List<Vector2Int> leftDeadEnds = new List<Vector2Int>();
        List<Vector2Int> rightDeadEnds = new List<Vector2Int>();

        int centerX = width / 2;

        // Only dead ends the player of that side can actually walk to (doors are closed at the start).
        HashSet<Vector2Int> leftReachable = GetReachableCells(startA, false);
        HashSet<Vector2Int> rightReachable = GetReachableCells(startB, true);

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

                    // A button or a plate on a falling platform would fall with it.
                    if (fallingPlatformCells.Contains(cell))
                        continue;

                    if (x < centerX)
                    {
                        if (leftReachable.Contains(cell))
                            leftDeadEnds.Add(cell);
                    }
                    else if (x > centerX)
                    {
                        if (rightReachable.Contains(cell))
                            rightDeadEnds.Add(cell);
                    }
                }
            }
        }

        if (buttonPrefab != null && leftDeadEnds.Count > 0 && rightDeadEnds.Count > 0)
        {
            Vector2Int leftButtonPos = leftDeadEnds[Random.Range(0, leftDeadEnds.Count)];
            Vector2Int rightButtonPos = rightDeadEnds[Random.Range(0, rightDeadEnds.Count)];

            leftDeadEnds.Remove(leftButtonPos);
            rightDeadEnds.Remove(rightButtonPos);

            reservedCells.Add(leftButtonPos);
            reservedCells.Add(rightButtonPos);

            TrySpawnLaserPuzzle(leftButtonPos, rightButtonPos, leftDeadEnds, rightDeadEnds);
            SpawnItemPortals(leftDeadEnds, rightDeadEnds);
            SpawnCubeForPlate(leftButtonPos, rightButtonPos);
            SpawnHealthPickups(false, leftDeadEnds);
            SpawnHealthPickups(true, rightDeadEnds);

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

        SpawnSpikeTraps(false);
        SpawnSpikeTraps(true);

        SpawnKnives(false);
        SpawnKnives(true);

        SpawnRobots(false);
        SpawnRobots(true);

        await Task.Yield();
    }

    // ================= ROBOTS AND KNIVES =================

    /// <summary>Knives on random free cells of one half.</summary>
    void SpawnKnives(bool useRightSide)
    {
        GameObject prefab = knifePickupPrefab != null ? knifePickupPrefab : Resources.Load<GameObject>("KnifePickup");
        if (prefab == null)
            return;

        for (int i = 0; i < knivesPerSide; i++)
        {
            if (!TryTakeFreeCell(useRightSide, null, 3, out Vector2Int cell))
                return;

            Spawn(prefab, CellToWorld(cell));
        }
    }

    /// <summary>
    /// Robots patrolling one half: on cells a robot may walk on, far from the player's start
    /// and not next to each other.
    /// </summary>
    void SpawnRobots(bool useRightSide)
    {
        GameObject prefab = robotEnemyPrefab != null ? robotEnemyPrefab : Resources.Load<GameObject>("RobotEnemy");
        if (prefab == null)
            return;

        Vector2Int sideStart = useRightSide ? startB : startA;
        Dictionary<Vector2Int, int> distances = new Dictionary<Vector2Int, int>();
        Queue<Vector2Int> queue = new Queue<Vector2Int>();

        // Walking distance from the start over the cells the robot may use.
        distances[sideStart] = 0;
        queue.Enqueue(sideStart);

        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();

            foreach (Vector2Int direction in CardinalDirections)
            {
                Vector2Int next = current + direction;
                if (distances.ContainsKey(next) || !IsRobotWalkable(next, useRightSide))
                    continue;

                distances[next] = distances[current] + 1;
                queue.Enqueue(next);
            }
        }

        List<Vector2Int> candidates = new List<Vector2Int>();
        foreach (KeyValuePair<Vector2Int, int> pair in distances)
        {
            if (pair.Value >= robotMinDistanceFromStart && !reservedCells.Contains(pair.Key) && !IsRampCell(pair.Key))
                candidates.Add(pair.Key);
        }

        // A small half: the farthest cells from the start.
        if (candidates.Count == 0)
        {
            int farthest = 0;
            foreach (int distance in distances.Values)
                farthest = Mathf.Max(farthest, distance);

            foreach (KeyValuePair<Vector2Int, int> pair in distances)
            {
                if (farthest > 0 && pair.Value == farthest)
                    candidates.Add(pair.Key);
            }
        }

        ShuffleList(candidates);
        List<Vector2Int> placed = new List<Vector2Int>();

        foreach (Vector2Int cell in candidates)
        {
            if (placed.Count >= robotsPerSide)
                break;

            bool nextToOther = false;
            foreach (Vector2Int other in placed)
            {
                if (Mathf.Abs(cell.x - other.x) + Mathf.Abs(cell.y - other.y) < 4)
                {
                    nextToOther = true;
                    break;
                }
            }

            if (nextToOther)
                continue;

            GameObject robotObj = Spawn(prefab, CellToWorld(cell));
            robotObj.transform.rotation = Quaternion.Euler(0f, 90f * Random.Range(0, 4), 0f);

            RobotEnemy robot = robotObj.GetComponent<RobotEnemy>();
            if (robot != null)
                robot.ServerInit(this, useRightSide);
            else
                Debug.LogError("Robot enemy prefab must have a RobotEnemy component.");

            placed.Add(cell);
        }
    }

    /// <summary>Server: a cell the robot of one half may walk on (its half, no doors, no falling platforms).</summary>
    public bool IsRobotWalkable(Vector2Int cell, bool useRightSide)
    {
        return map != null && IsSideWalkable(cell, useRightSide) && !fallingPlatformCells.Contains(cell);
    }

    /// <summary>Server: the position is in that half of the maze (the doorways and the exit belong to neither).</summary>
    public bool IsOnSide(Vector3 position, bool useRightSide)
    {
        return map != null && IsSideWalkable(WorldToCell(position), useRightSide);
    }

    public Vector2Int WorldToCell(Vector3 position)
    {
        return new Vector2Int(WorldToAxisIndex(position.x), WorldToAxisIndex(position.z));
    }

    /// <summary>Center of a cell, standing on its floor (the middle of a ramp).</summary>
    public Vector3 GetCellWorldPosition(Vector2Int cell)
    {
        return CellToWorld(cell);
    }

    /// <summary>Height of the floor under a point: the level of its cell, on a ramp the slope under it.</summary>
    public float GetFloorHeight(Vector3 position)
    {
        Vector2Int cell = WorldToCell(position);
        if (map == null || floorLevel == null || cell.x < 0 || cell.y < 0 || cell.x >= width || cell.y >= height ||
            map[cell.x, cell.y] != 1)
        {
            return 0f;
        }

        if (!isRamp[cell.x, cell.y])
            return floorLevel[cell.x, cell.y] * floorLevelHeight;

        bool alongX = rampAlongX[cell.x, cell.y];
        int index = alongX ? cell.x : cell.y;
        float coordinate = alongX ? position.x : position.z;
        float t = Mathf.Clamp01((coordinate - (AxisCenter(index) - AxisSize(index) * 0.5f)) / AxisSize(index));
        return Mathf.Lerp(rampNegLevel[cell.x, cell.y], rampPosLevel[cell.x, cell.y], t) * floorLevelHeight;
    }

    /// <summary>
    /// Floor spikes in corridors of one half: not near the start, not on buttons / the laser puzzle,
    /// not on falling platforms and never next to another spike trap.
    /// </summary>
    void SpawnSpikeTraps(bool useRightSide)
    {
        GameObject prefab = spikeTrapPrefab != null ? spikeTrapPrefab : Resources.Load<GameObject>("SpikeTrap");
        if (prefab == null)
            return;

        Vector2Int sideStart = useRightSide ? startB : startA;
        HashSet<Vector2Int> reachable = GetReachableCells(sideStart, useRightSide);
        List<Vector2Int> candidates = new List<Vector2Int>();

        foreach (Vector2Int cell in reachable)
        {
            if (reservedCells.Contains(cell) || fallingPlatformCells.Contains(cell) || cell == exitCell)
                continue;

            // Square corridor cells only (not the thin passages and ramps between them): the trap covers the whole cell.
            if (cell.x % 2 == 0 || cell.y % 2 == 0)
                continue;

            if (Mathf.Abs(cell.x - sideStart.x) + Mathf.Abs(cell.y - sideStart.y) < spikeMinDistanceFromStart)
                continue;

            // Corridors and junctions only: a dead end with spikes is pointless.
            if (CountWalkableNeighbours(cell) < 2)
                continue;

            candidates.Add(cell);
        }

        ShuffleList(candidates);

        int minCount = Mathf.Max(0, Mathf.Min(spikeTrapsPerSideMin, spikeTrapsPerSideMax));
        int maxCount = Mathf.Max(minCount, spikeTrapsPerSideMax);
        int count = Random.Range(minCount, maxCount + 1);
        List<Vector2Int> placed = new List<Vector2Int>();

        foreach (Vector2Int cell in candidates)
        {
            if (placed.Count >= count)
                break;

            bool nextToOther = false;
            foreach (Vector2Int other in placed)
            {
                if (Mathf.Abs(cell.x - other.x) + Mathf.Abs(cell.y - other.y) <= 2)
                {
                    nextToOther = true;
                    break;
                }
            }

            if (nextToOther)
                continue;

            Vector3 trapScale = CellScale(cell.x, cell.y);
            trapScale.y = 1f;
            GameObject trapObj = Spawn(prefab, CellToWorld(cell) + Vector3.up * spikeHeightOffset, null, trapScale);
            SpikeTrap trap = trapObj.GetComponent<SpikeTrap>();
            if (trap != null)
                trap.SetPhaseOffset(Random.Range(0f, 3f));

            placed.Add(cell);
            reservedCells.Add(cell);
        }
    }

    void TrySpawnLaserPuzzle(
        Vector2Int leftKeyCell,
        Vector2Int rightKeyCell,
        List<Vector2Int> leftFreeDeadEnds,
        List<Vector2Int> rightFreeDeadEnds)
    {
        if (laserTrapPrefab == null || laserSwitchPrefab == null)
            return;

        // The laser blocks the key of one side; the plate is on the other side, so the other player has to solve it.
        // The cube is placed later (SpawnCubeForPlate), possibly on the far half: then it goes through the item portal.
        bool canBlockLeftKey = TryChoosePlate(rightFreeDeadEnds, leftKeyCell, rightKeyCell, true, false,
            out Vector2Int rightPlateCell, out _);
        bool canBlockRightKey = TryChoosePlate(leftFreeDeadEnds, leftKeyCell, rightKeyCell, false, false,
            out Vector2Int leftPlateCell, out _);

        if (!canBlockLeftKey && !canBlockRightKey)
            return;

        bool blockLeftSide = canBlockLeftKey && (!canBlockRightKey || Random.value > 0.5f);

        Vector2Int protectedCell = blockLeftSide ? leftKeyCell : rightKeyCell;
        Vector2Int switchCell = blockLeftSide ? rightPlateCell : leftPlateCell;

        // The barrier stands on a flat floor; a dead end entered over a ramp gets no laser.
        if (!TryGetDeadEndEntry(protectedCell, out Vector2Int entryCell) || IsRampCell(entryCell))
            return;

        reservedCells.Add(entryCell);
        reservedCells.Add(switchCell);
        (blockLeftSide ? rightFreeDeadEnds : leftFreeDeadEnds).Remove(switchCell);

        Vector2Int corridorDirection = protectedCell - entryCell;
        Vector2Int laserBeamDirection = new Vector2Int(-corridorDirection.y, corridorDirection.x);

        if (laserBeamDirection == Vector2Int.zero)
            return;

        Vector3 laserPosition = CellToWorld(entryCell);
        laserPosition.y += laserBottomHeightOffset;

        Vector3 laserDirection = CellDirectionToWorld(laserBeamDirection).normalized;

        GameObject laserObj = Spawn(laserTrapPrefab, laserPosition);
        laserObj.transform.rotation = Quaternion.LookRotation(laserDirection, Vector3.up);

        GameObject switchObj = Spawn(laserSwitchPrefab, CellToWorld(switchCell) + Vector3.up * laserSwitchHeightOffset);

        LaserTrap laserTrap = laserObj.GetComponent<LaserTrap>();
        LaserSwitch laserSwitch = switchObj.GetComponent<LaserSwitch>();

        if (laserTrap != null && TryGetLaserSegment(entryCell, laserBeamDirection, out Vector3 segmentStart, out Vector3 segmentEnd))
            laserTrap.ConfigureBarrier(segmentStart, segmentEnd, laserBarrierHeight, laserBarrierThickness);

        // The laser-off camera shot looks from the corridor into the blocked dead end.
        if (laserTrap != null && laserSwitch != null)
            laserSwitch.SetTargetLaser(laserTrap, CellDirectionToWorld(corridorDirection));

        if (laserSwitch != null)
        {
            laserPlate = laserSwitch;
            laserPlateCell = switchCell;
            laserPlateOnRightSide = blockLeftSide;
        }
    }

    // ================= ITEMS: PORTALS, CUBE, HEARTS =================

    /// <summary>One item portal on each half, linked together, preferably in a free dead end.</summary>
    void SpawnItemPortals(List<Vector2Int> leftFreeDeadEnds, List<Vector2Int> rightFreeDeadEnds)
    {
        hasItemPortals = false;

        GameObject prefab = itemPortalPrefab != null ? itemPortalPrefab : Resources.Load<GameObject>("ItemPortal");
        if (prefab == null)
            return;

        if (!TryTakeFreeCell(false, leftFreeDeadEnds, 2, out Vector2Int leftCell) ||
            !TryTakeFreeCell(true, rightFreeDeadEnds, 2, out Vector2Int rightCell))
        {
            return;
        }

        ItemPortal leftPortal = Spawn(prefab, CellToWorld(leftCell)).GetComponent<ItemPortal>();
        ItemPortal rightPortal = Spawn(prefab, CellToWorld(rightCell)).GetComponent<ItemPortal>();

        if (leftPortal == null || rightPortal == null)
        {
            Debug.LogError("Item portal prefab must have an ItemPortal component.");
            return;
        }

        leftPortal.Link(rightPortal);
        rightPortal.Link(leftPortal);
        hasItemPortals = true;
    }

    /// <summary>
    /// The cube for the laser plate: on the plate's half (some walk away from the plate) or, when there are portals,
    /// sometimes on the other half - then the other player has to send it through the portal.
    /// </summary>
    void SpawnCubeForPlate(Vector2Int leftKeyCell, Vector2Int rightKeyCell)
    {
        if (laserPlate == null)
            return;

        GameObject prefab = pushableCubePrefab != null ? pushableCubePrefab : Resources.Load<GameObject>("PushableCube");
        if (prefab == null)
            return;

        bool otherSide = hasItemPortals && Random.value < cubeOnOtherSideChance;
        bool cubeOnRightSide = otherSide ? !laserPlateOnRightSide : laserPlateOnRightSide;
        Vector2Int cubeCell;

        if (otherSide)
        {
            if (!TryTakeFreeCell(cubeOnRightSide, null, 2, out cubeCell))
                return;
        }
        else
        {
            if (!TryChooseCubeCell(laserPlateCell, cubeOnRightSide, leftKeyCell, rightKeyCell, out cubeCell) ||
                reservedCells.Contains(cubeCell))
            {
                if (!TryTakeFreeCell(cubeOnRightSide, null, 2, out cubeCell))
                    return;
            }

            reservedCells.Add(cubeCell);
        }

        PushableCube cube = Spawn(prefab, CellToWorld(cubeCell) + Vector3.up * cubeSpawnHeight).GetComponent<PushableCube>();
        if (cube != null)
            laserPlate.SetRequiredCube(cube);
        else
            Debug.LogError("Pushable cube prefab must have a PushableCube component.");
    }

    /// <summary>Heart pickups on one half, preferably in free dead ends.</summary>
    void SpawnHealthPickups(bool useRightSide, List<Vector2Int> freeDeadEnds)
    {
        GameObject prefab = healthPickupPrefab != null ? healthPickupPrefab : Resources.Load<GameObject>("HealthPickup");
        if (prefab == null)
            return;

        for (int i = 0; i < healthPickupsPerSide; i++)
        {
            if (!TryTakeFreeCell(useRightSide, freeDeadEnds, 3, out Vector2Int cell))
                return;

            Spawn(prefab, CellToWorld(cell) + Vector3.up * healthPickupSpawnHeight);
        }
    }

    /// <summary>
    /// A free cell on one half for an item: a free dead end from the list if there is one, otherwise a corridor cell.
    /// Never reserved, never on a falling platform, at least minDistanceFromStart cells from the player's start.
    /// The cell is reserved and removed from the list.
    /// </summary>
    bool TryTakeFreeCell(bool useRightSide, List<Vector2Int> preferredDeadEnds, int minDistanceFromStart, out Vector2Int cell)
    {
        Vector2Int sideStart = useRightSide ? startB : startA;
        List<Vector2Int> candidates = new List<Vector2Int>();

        bool IsFree(Vector2Int candidate) =>
            !reservedCells.Contains(candidate) && !fallingPlatformCells.Contains(candidate) && candidate != exitCell &&
            !IsRampCell(candidate) &&
            Mathf.Abs(candidate.x - sideStart.x) + Mathf.Abs(candidate.y - sideStart.y) >= minDistanceFromStart;

        if (preferredDeadEnds != null)
        {
            foreach (Vector2Int candidate in preferredDeadEnds)
            {
                if (IsFree(candidate))
                    candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            foreach (Vector2Int candidate in GetReachableCells(sideStart, useRightSide))
            {
                if (IsFree(candidate) && CountWalkableNeighbours(candidate) >= 2)
                    candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            cell = default;
            return false;
        }

        cell = candidates[Random.Range(0, candidates.Count)];
        reservedCells.Add(cell);
        preferredDeadEnds?.Remove(cell);
        return true;
    }

    /// <summary>
    /// Picks a free reachable dead end for the plate and, when a cube is needed, a cube cell on the same side
    /// from which the cube can be pushed to the plate.
    /// </summary>
    bool TryChoosePlate(
        List<Vector2Int> freeDeadEnds,
        Vector2Int leftKeyCell,
        Vector2Int rightKeyCell,
        bool useRightSide,
        bool needCube,
        out Vector2Int plateCell,
        out Vector2Int cubeCell)
    {
        List<Vector2Int> candidates = GetSwitchCandidates(freeDeadEnds, leftKeyCell, rightKeyCell, useRightSide);
        ShuffleList(candidates);

        foreach (Vector2Int candidate in candidates)
        {
            cubeCell = default;

            if (!needCube || TryChooseCubeCell(candidate, useRightSide, leftKeyCell, rightKeyCell, out cubeCell))
            {
                plateCell = candidate;
                return true;
            }
        }

        plateCell = default;
        cubeCell = default;
        return false;
    }

    /// <summary>
    /// Walks from the plate over the cells the cube can be pushed through (no falling platforms, no doors)
    /// and picks a corridor cell whose path to the plate is between cubeMinPathLength and cubeMaxPathLength.
    /// </summary>
    bool TryChooseCubeCell(Vector2Int plateCell, bool useRightSide, Vector2Int leftKeyCell, Vector2Int rightKeyCell, out Vector2Int cubeCell)
    {
        Dictionary<Vector2Int, int> distances = new Dictionary<Vector2Int, int> { { plateCell, 0 } };
        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        queue.Enqueue(plateCell);

        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();

            foreach (Vector2Int direction in CardinalDirections)
            {
                Vector2Int next = current + direction;

                if (distances.ContainsKey(next) || !IsSideWalkable(next, useRightSide) || fallingPlatformCells.Contains(next) ||
                    IsRampCell(next))
                    continue;

                distances[next] = distances[current] + 1;
                queue.Enqueue(next);
            }
        }

        Vector2Int sideStart = useRightSide ? startB : startA;
        int minLength = Mathf.Max(2, Mathf.Min(cubeMinPathLength, cubeMaxPathLength));
        int maxLength = Mathf.Max(minLength, cubeMaxPathLength);

        List<Vector2Int> inRange = new List<Vector2Int>();
        List<Vector2Int> fallback = new List<Vector2Int>();

        foreach (KeyValuePair<Vector2Int, int> pair in distances)
        {
            Vector2Int cell = pair.Key;

            if (pair.Value < 2 || cell == leftKeyCell || cell == rightKeyCell || cell == exitCell)
                continue;

            // Not on top of the spawning player.
            if (Mathf.Abs(cell.x - sideStart.x) + Mathf.Abs(cell.y - sideStart.y) < 2)
                continue;

            // A corridor or a junction, not a dead end: the player must be able to get behind the cube.
            if (CountWalkableNeighbours(cell) < 2)
                continue;

            if (pair.Value >= minLength && pair.Value <= maxLength)
                inRange.Add(cell);
            else
                fallback.Add(cell);
        }

        List<Vector2Int> pool = inRange.Count > 0 ? inRange : fallback;

        if (pool.Count == 0)
        {
            cubeCell = default;
            return false;
        }

        cubeCell = pool[Random.Range(0, pool.Count)];
        return true;
    }

    /// <summary>Cells the player of one side can walk to from their start while the doors are closed.</summary>
    HashSet<Vector2Int> GetReachableCells(Vector2Int start, bool useRightSide)
    {
        HashSet<Vector2Int> reachable = new HashSet<Vector2Int>();

        if (!IsSideWalkable(start, useRightSide))
            return reachable;

        Queue<Vector2Int> queue = new Queue<Vector2Int>();
        reachable.Add(start);
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();

            foreach (Vector2Int direction in CardinalDirections)
            {
                Vector2Int next = current + direction;

                if (!reachable.Contains(next) && IsSideWalkable(next, useRightSide))
                {
                    reachable.Add(next);
                    queue.Enqueue(next);
                }
            }
        }

        return reachable;
    }

    bool IsSideWalkable(Vector2Int cell, bool useRightSide)
    {
        if (!IsInside(cell.x, cell.y) || map[cell.x, cell.y] != 1)
            return false;

        int centerX = width / 2;
        if (useRightSide ? cell.x <= centerX : cell.x >= centerX)
            return false;

        // Door cells are blocked until the other player presses a button.
        Vector2Int leftDoorCell = new Vector2Int(centerX - 1, height / 2);
        Vector2Int rightDoorCell = new Vector2Int(centerX + 1, height / 2);
        return cell != leftDoorCell && cell != rightDoorCell;
    }

    int CountWalkableNeighbours(Vector2Int cell)
    {
        int count = 0;

        foreach (Vector2Int direction in CardinalDirections)
        {
            Vector2Int neighbour = cell + direction;
            if (IsInside(neighbour.x, neighbour.y) && map[neighbour.x, neighbour.y] == 1)
                count++;
        }

        return count;
    }

    static readonly Vector2Int[] CardinalDirections =
    {
        Vector2Int.up,
        Vector2Int.down,
        Vector2Int.left,
        Vector2Int.right
    };

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

        // Only free dead ends the player can reach. There used to be a fallback to any corridor cell,
        // which put the plate in the middle of a corridor; without a dead end there is no laser puzzle on this level.
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
        bool beamAlongX = beamDirection.x != 0;
        float negativeEdge = Mathf.Max(0f, AxisSize(beamAlongX ? negativeCell.x : negativeCell.y) * 0.5f - laserWallPadding);
        float positiveEdge = Mathf.Max(0f, AxisSize(beamAlongX ? positiveCell.x : positiveCell.y) * 0.5f - laserWallPadding);

        segmentStart = CellToWorld(negativeCell) - worldDirection * negativeEdge;
        segmentEnd = CellToWorld(positiveCell) + worldDirection * positiveEdge;
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
        spawnPos.y += playerHeightOffset;

        player.transform.position = spawnPos;

        TeleportPlayerClientRpc(spawnPos, new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { clientId }
            }
        });
    }

    // Server: a player who fell off the map (for example through a falling platform) goes back to their start,
    // the same way LaserTrap respawns a player hit by a laser.
    void Update()
    {
        if (!IsServer || !IsSpawned || map == null || NetworkManager.Singleton == null)
            return;

        foreach (NetworkClient client in NetworkManager.Singleton.ConnectedClientsList)
        {
            NetworkObject player = client.PlayerObject;
            if (player == null || player.transform.position.y > fallRespawnHeight)
                continue;

            if (lastFallRespawnTime.TryGetValue(client.ClientId, out float lastTime) &&
                Time.time - lastTime < fallRespawnCooldown)
            {
                continue;
            }

            lastFallRespawnTime[client.ClientId] = Time.time;
            RespawnFallenPlayer(client.ClientId, player);
        }
    }

    void RespawnFallenPlayer(ulong clientId, NetworkObject player)
    {
        if (DungeonProgressManager.Instance != null)
            DungeonProgressManager.Instance.DamagePlayer(clientId);

        Vector3 spawnPos = CellToWorld(GetSpawnCell(clientId));
        spawnPos.y += playerHeightOffset;

        player.transform.position = spawnPos;

        // The player position is client-authoritative, so the owner has to move its own character.
        FallRespawnClientRpc(spawnPos, new ClientRpcParams
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

    // Same teleport as at the level start, plus a sound so the player understands what happened.
    [ClientRpc]
    void FallRespawnClientRpc(Vector3 spawnPos, ClientRpcParams clientRpcParams = default)
    {
        AudioManager.Play(Sfx.FallRespawn);
        DamageVignette.Flash();
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

    // The grid is not uniform: odd rows and columns are corridors (corridorWidth), even ones walls (wallThickness).
    // A corridor cell is corridorWidth x corridorWidth, the passage between two of them wallThickness long.

    float AxisSize(int index)
    {
        return index % 2 == 1 ? corridorWidth : wallThickness;
    }

    float AxisCenter(int index)
    {
        int walls = (index + 1) / 2;
        int corridors = index / 2;
        return walls * wallThickness + corridors * corridorWidth + AxisSize(index) * 0.5f;
    }

    int WorldToAxisIndex(float coordinate)
    {
        float period = wallThickness + corridorWidth;
        int pair = Mathf.FloorToInt(coordinate / period);
        float rest = coordinate - pair * period;
        return pair * 2 + (rest < wallThickness ? 0 : 1);
    }

    /// <summary>Center of a cell on its floor: walls and walkable cells at their level, a ramp at its middle.</summary>
    Vector3 CellToWorld(int x, int y)
    {
        return new Vector3(AxisCenter(x), CellFloorHeight(x, y), AxisCenter(y));
    }

    Vector3 CellToWorld(Vector2Int cell)
    {
        return CellToWorld(cell.x, cell.y);
    }

    float CellFloorHeight(int x, int y)
    {
        if (map == null || floorLevel == null || x < 0 || y < 0 || x >= width || y >= height || map[x, y] != 1)
            return 0f;

        if (isRamp[x, y])
            return (rampNegLevel[x, y] + rampPosLevel[x, y]) * 0.5f * floorLevelHeight;

        return floorLevel[x, y] * floorLevelHeight;
    }

    /// <summary>Scale multiplier that stretches a prefab made for a prefabCellSize square to this cell.</summary>
    Vector3 CellScale(int x, int y)
    {
        return new Vector3(AxisSize(x) / prefabCellSize, 1f, AxisSize(y) / prefabCellSize);
    }

    /// <summary>A floor tile tilted from one level to the other, as long as the slope and as wide as the corridor.</summary>
    void SpawnRamp(int x, int y)
    {
        bool alongX = rampAlongX[x, y];
        float run = AxisSize(alongX ? x : y);
        float rise = (rampPosLevel[x, y] - rampNegLevel[x, y]) * floorLevelHeight;
        float length = Mathf.Sqrt(run * run + rise * rise);
        float angle = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;

        // Euler Z lifts the +X end, Euler -X lifts the +Z end.
        Quaternion rotation = alongX ? Quaternion.Euler(0f, 0f, angle) : Quaternion.Euler(-angle, 0f, 0f);
        Vector3 scale = alongX
            ? new Vector3(length / prefabCellSize, 1f, AxisSize(y) / prefabCellSize)
            : new Vector3(AxisSize(x) / prefabCellSize, 1f, length / prefabCellSize);

        Spawn(floorPrefab, CellToWorld(x, y), rotation, scale);
    }

    Vector3 CellDirectionToWorld(Vector2Int direction)
    {
        return new Vector3(direction.x, 0f, direction.y);
    }

    GameObject Spawn(GameObject prefab, Vector3 pos, Quaternion? rotation = null, Vector3? scaleMultiplier = null)
    {
        var obj = Instantiate(prefab, pos, rotation ?? Quaternion.identity);

        // Before Spawn: the scale is sent to the clients with the spawn.
        if (scaleMultiplier.HasValue)
            obj.transform.localScale = Vector3.Scale(obj.transform.localScale, scaleMultiplier.Value);

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

    void ShuffleList(List<Vector2Int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int r = Random.Range(0, i + 1);
            (list[i], list[r]) = (list[r], list[i]);
        }
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
