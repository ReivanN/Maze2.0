using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public class NetworkObjectPoolManager : NetworkBehaviour
{
    public static NetworkObjectPoolManager Instance { get; private set; }

    [System.Serializable]
    public class Pool
    {
        public string tag;
        public GameObject prefab;
        public int size;
        public Transform parent;
    }

    [Header("Pool Settings")]
    public List<Pool> pools = new List<Pool>();
    public bool expandIfNeeded = true;
    public int expandAmount = 10;

    private Dictionary<string, Queue<GameObject>> poolDictionary;
    private Dictionary<string, Pool> poolInfo;
    private Dictionary<GameObject, string> objectToTag;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    public void InitializePools(
        GameObject floorPrefab, 
        GameObject wallPrefab, 
        GameObject startPrefab, 
        GameObject endPrefab, 
        int initialSize = 50)
    {
        if (!IsServer) return;

        // Создаем пулы для каждого типа объектов
        pools.Clear();
        
        pools.Add(new Pool { 
            tag = "Floor", 
            prefab = floorPrefab, 
            size = initialSize,
            parent = transform
        });
        
        pools.Add(new Pool { 
            tag = "Wall", 
            prefab = wallPrefab, 
            size = initialSize,
            parent = transform
        });
        
        pools.Add(new Pool { 
            tag = "Start", 
            prefab = startPrefab, 
            size = 5,
            parent = transform
        });
        
        pools.Add(new Pool { 
            tag = "End", 
            prefab = endPrefab, 
            size = 5,
            parent = transform
        });

        InitializeAllPools();
    }

    void InitializeAllPools()
    {
        poolDictionary = new Dictionary<string, Queue<GameObject>>();
        poolInfo = new Dictionary<string, Pool>();
        objectToTag = new Dictionary<GameObject, string>();

        foreach (Pool pool in pools)
        {
            Queue<GameObject> objectPool = new Queue<GameObject>();

            for (int i = 0; i < pool.size; i++)
            {
                GameObject obj = CreatePooledObject(pool.prefab, pool.parent);
                obj.SetActive(false);
                objectPool.Enqueue(obj);
            }

            poolDictionary.Add(pool.tag, objectPool);
            poolInfo.Add(pool.tag, pool);
        }
    }

    GameObject CreatePooledObject(GameObject prefab, Transform parent)
    {
        GameObject obj = Instantiate(prefab, parent);
        
        // Добавляем компонент для автоматического возврата в пул
        var poolable = obj.AddComponent<PoolableObject>();
        poolable.Initialize(this);
        
        var netObj = obj.GetComponent<NetworkObject>();
        if (netObj != null)
        {
            // Предварительно спавним объект как неактивный
            netObj.Spawn();
            netObj.TrySetParent(parent);
        }
        
        return obj;
    }

    public GameObject GetFromPool(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        string tag = GetTagForPrefab(prefab);
        
        if (string.IsNullOrEmpty(tag))
        {
            Debug.LogWarning($"Prefab {prefab.name} not found in pools. Instantiating normally.");
            return Instantiate(prefab, position, rotation);
        }

        if (!poolDictionary.ContainsKey(tag))
        {
            Debug.LogError($"Pool with tag {tag} doesn't exist.");
            return Instantiate(prefab, position, rotation);
        }

        // Проверяем, есть ли доступные объекты в пуле
        if (poolDictionary[tag].Count == 0)
        {
            if (expandIfNeeded)
            {
                ExpandPool(tag, expandAmount);
            }
            else
            {
                Debug.LogWarning($"Pool {tag} is empty!");
                return Instantiate(prefab, position, rotation);
            }
        }

        GameObject objectToSpawn = poolDictionary[tag].Dequeue();
        
        // Настраиваем позицию и вращение
        objectToSpawn.transform.position = position;
        objectToSpawn.transform.rotation = rotation;
        objectToSpawn.SetActive(true);

        // Реактивируем NetworkObject если нужно
        var netObj = objectToSpawn.GetComponent<NetworkObject>();
        if (netObj != null && !netObj.IsSpawned)
        {
            netObj.Spawn();
        }

        return objectToSpawn;
    }

    public void ReturnToPool(GameObject obj)
    {
        if (obj == null) return;

        if (!objectToTag.TryGetValue(obj, out string tag))
        {
            Debug.LogWarning($"Object {obj.name} not from pool. Destroying.");
            if (obj.TryGetComponent<NetworkObject>(out var netObj))
            {
                netObj.Despawn();
            }
            Destroy(obj);
            return;
        }

        if (!poolDictionary.ContainsKey(tag))
        {
            Debug.LogError($"Pool with tag {tag} doesn't exist.");
            Destroy(obj);
            return;
        }

        // Деактивируем объект
        obj.SetActive(false);
        obj.transform.SetParent(poolInfo[tag].parent);
        
        // Деспавним NetworkObject но не уничтожаем
        if (obj.TryGetComponent<NetworkObject>(out var networkObj))
        {
            if (networkObj.IsSpawned)
            {
                networkObj.Despawn(false); // Не уничтожаем GameObject
            }
        }

        // Возвращаем в пул
        poolDictionary[tag].Enqueue(obj);
    }

    void ExpandPool(string tag, int amount)
    {
        if (!poolInfo.ContainsKey(tag))
        {
            Debug.LogError($"Cannot expand non-existent pool: {tag}");
            return;
        }

        Pool pool = poolInfo[tag];
        Queue<GameObject> objectPool = poolDictionary[tag];

        for (int i = 0; i < amount; i++)
        {
            GameObject obj = CreatePooledObject(pool.prefab, pool.parent);
            obj.SetActive(false);
            objectPool.Enqueue(obj);
        }

        Debug.Log($"Pool {tag} expanded by {amount}. New size: {objectPool.Count}");
    }

    string GetTagForPrefab(GameObject prefab)
    {
        foreach (var pool in pools)
        {
            if (pool.prefab == prefab)
            {
                return pool.tag;
            }
        }
        return null;
    }

    public void ClearAllPools()
    {
        if (poolDictionary == null) return;

        foreach (var pool in poolDictionary.Values)
        {
            while (pool.Count > 0)
            {
                GameObject obj = pool.Dequeue();
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

        poolDictionary.Clear();
        poolInfo.Clear();
        objectToTag.Clear();
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            ClearAllPools();
            Instance = null;
        }
    }
}

// Вспомогательный компонент для объектов пула
public class PoolableObject : MonoBehaviour
{
    private NetworkObjectPoolManager poolManager;

    public void Initialize(NetworkObjectPoolManager manager)
    {
        poolManager = manager;
    }

    public void ReturnToPool()
    {
        if (poolManager != null)
        {
            poolManager.ReturnToPool(gameObject);
        }
        else
        {
            // Если менеджер не найден, уничтожаем объект
            if (TryGetComponent<NetworkObject>(out var netObj))
            {
                netObj.Despawn();
            }
            Destroy(gameObject);
        }
    }

    // Можно добавить автоматический возврат при деактивации
    private void OnDisable()
    {
        // Автоматический возврат в пул при деактивации
        // Комментируйте если нужно ручное управление
        // ReturnToPool();
    }
}