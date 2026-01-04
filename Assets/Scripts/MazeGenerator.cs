using System.Collections.Generic;
using UnityEngine;

public class MazeGenerator : MonoBehaviour
{
    [Header("Размеры лабиринта в клетках")]
    public int cellsX = 10;
    public int cellsY = 10;

    [Header("Размер клетки")]
    public float cellSize = 2f;

    [Header("Префабы")]
    public float regenerateInterval = 5f;   // Каждые 5 сек
    public Transform mazeParent;
    public GameObject wallPrefab;
    public GameObject floorPrefab;

    private float timer;

    private int[,] grid;
    private List<GameObject> wallPool = new List<GameObject>();
    private List<GameObject> floorPool = new List<GameObject>();

    void Start()
    {
        InitGrid();
        CreatePool();
        GenerateMaze();
        BuildMaze();
    }

    void Update()
    {
        timer += Time.deltaTime;

        if (timer >= regenerateInterval)
        {
            timer = 0f;
            Regenerate();
        }
    }

    // ----------------------------------------------------------
    //  SYSTEM INIT
    // ----------------------------------------------------------

    private void InitGrid()
    {
        grid = new int[cellsX, cellsY];

        // 1 – стена
        for (int x = 0; x < cellsX; x++)
            for (int y = 0; y < cellsY; y++)
                grid[x, y] = 1;
    }

    private void CreatePool()
    {
        int total = cellsX * cellsY;

        for (int i = 0; i < total; i++)
        {
            GameObject wall = Instantiate(wallPrefab, mazeParent);
            wall.SetActive(false);
            wallPool.Add(wall);

            GameObject floor = Instantiate(floorPrefab, mazeParent);
            floor.SetActive(false);
            floorPool.Add(floor);
        }
    }

    // ----------------------------------------------------------
    //  CORE REGENERATION
    // ----------------------------------------------------------

    private void Regenerate()
    {
        InitGrid();
        GenerateMaze();
        BuildMaze();
    }

    // ----------------------------------------------------------
    //  MAZE GENERATION
    // ----------------------------------------------------------

    private void GenerateMaze()
    {
        // Старт алгоритма "прорезания"
        Carve(1, 1);
    }

    void Carve(int x, int y)
    {
        grid[x, y] = 0;

        int[][] dirs = new int[][]
        {
            new int[]{ 1, 0 },
            new int[]{ -1, 0 },
            new int[]{ 0, 1 },
            new int[]{ 0, -1 }
        };

        // Перемешивание направлений
        for (int i = 0; i < dirs.Length; i++)
        {
            int rnd = Random.Range(0, dirs.Length);
            var temp = dirs[i];
            dirs[i] = dirs[rnd];
            dirs[rnd] = temp;
        }

        foreach (var d in dirs)
        {
            int nx = x + d[0] * 2;
            int ny = y + d[1] * 2;

            if (nx > 0 && ny > 0 && nx < cellsX - 1 && ny < cellsY - 1)
            {
                if (grid[nx, ny] == 1)
                {
                    grid[x + d[0], y + d[1]] = 0;
                    Carve(nx, ny);
                }
            }
        }
    }

    // ----------------------------------------------------------
    //  BUILD MAZE FROM GRID USING POOL
    // ----------------------------------------------------------

    void BuildMaze()
    {
        int total = cellsX * cellsY;
        int index = 0;

        for (int x = 0; x < cellsX; x++)
        {
            for (int y = 0; y < cellsY; y++)
            {
                Vector3 floorPos = new Vector3(x * cellSize, 0f, y * cellSize);
                Vector3 wallPos = new Vector3(x * cellSize, 1f, y * cellSize);

                GameObject floor = floorPool[index];
                GameObject wall = wallPool[index];

                // Пол активен всегда
                floor.transform.position = floorPos;
                floor.SetActive(true);

                // Стена — в зависимости от grid
                if (grid[x, y] == 1)
                {
                    wall.transform.position = wallPos;
                    wall.SetActive(true);
                }
                else
                {
                    wall.SetActive(false);
                }

                index++;
            }
        }
    }
}