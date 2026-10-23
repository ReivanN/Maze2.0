using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A patrolling robot (Sci-Fi ToiletMech), one in each half of the maze (spawned by CaveDungeonGenerator):
///   Patrol  - walks between random cells of its half;
///   Chase   - sees a player: runs at them. Lost them - runs to the place it saw them last and after 2-3 seconds
///             without seeing them goes back to patrolling;
///   Attack  - reached the player: that player loses at once (DungeonProgressManager.KillPlayer);
///   Stunned - hit with the knife (KnifePickup): knocked down for a few seconds.
///
/// Only the server thinks and moves the robot, NetworkTransform sends the position. The animation is a NetworkVariable,
/// so every player plays the same clip locally. Paths are searched on the maze grid of the generator: the robot never
/// leaves its half, never walks through the doors and never steps on falling platforms.
/// </summary>
public class RobotEnemy : NetworkBehaviour
{
    private enum State : byte
    {
        Patrol,
        Chase,
        Attack,
        Stunned
    }

    private enum Anim : byte
    {
        Idle,
        Walk,
        Run,
        Attack,
        KnockDown,
        GetUp
    }

    // Animator state names, made by RobotEnemySetup (Maze > Setup > Create Robot Enemy + Knife).
    private static readonly string[] AnimStates = { "Idle", "Walk", "Run", "Attack", "KnockStart", "KnockEnd" };

    private static readonly List<RobotEnemy> instances = new List<RobotEnemy>();
    private static readonly RaycastHit[] rayHits = new RaycastHit[16];
    private static readonly Vector2Int[] Directions = { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };

    [Header("Model")]
    [Tooltip("The model is scaled to this height (maze cells are 2 m).")]
    [SerializeField] private float modelHeight = 2.2f;
    [Tooltip("...but never wider than this, so it fits into a corridor.")]
    [SerializeField] private float modelMaxWidth = 1.9f;
    [SerializeField] private float colliderRadius = 0.5f;

    [Header("Movement")]
    [SerializeField] private float patrolSpeed = 1.6f;
    [Tooltip("A bit slower than the player's sprint, so the player can run away.")]
    [SerializeField] private float chaseSpeed = 4.3f;
    [SerializeField] private float turnSpeed = 540f;
    [SerializeField] private float patrolWaitMin = 0.8f;
    [SerializeField] private float patrolWaitMax = 2.5f;
    [Tooltip("A patrol target is at least this many cells away.")]
    [SerializeField] private int patrolMinDistance = 4;

    [Header("Vision")]
    [SerializeField] private float viewDistance = 11f;
    [SerializeField] private float viewAngle = 110f;
    [Tooltip("A player this close is noticed even behind the robot.")]
    [SerializeField] private float hearingRadius = 1.8f;
    [SerializeField] private float eyeHeight = 1.7f;
    [Tooltip("After spawning the robot does not look for players for this long (players are still being moved to their start).")]
    [SerializeField] private float spawnGracePeriod = 2.5f;
    [Tooltip("The robot gives up the chase after not seeing the player for a random time between these values.")]
    [SerializeField] private float loseSightTimeMin = 2f;
    [SerializeField] private float loseSightTimeMax = 3f;

    [Header("Attack")]
    [SerializeField] private float attackRange = 1.4f;
    [Tooltip("The hit lands this long after the attack starts; the player can still run out of reach until then.")]
    [SerializeField] private float attackHitTime = 0.45f;
    [SerializeField] private float attackDuration = 1.1f;
    [Tooltip("At the moment of the hit the player has to be at most attackRange * this far.")]
    [SerializeField] private float attackReachMultiplier = 1.4f;

    [Header("Stun")]
    [SerializeField] private float stunDuration = 5f;
    [Tooltip("The last part of the stun, while the robot gets up.")]
    [SerializeField] private float getUpDuration = 1.2f;

    [Header("Sound")]
    [SerializeField] private float soundMaxDistance = 16f;

    private readonly NetworkVariable<byte> anim = new NetworkVariable<byte>(
        (byte)Anim.Idle,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private Animator animator;

    // ---- server only ----
    private CaveDungeonGenerator generator;
    private bool rightSide;
    private State state = State.Patrol;
    private float stateTime;
    private float awakeTime;
    private float waitUntil;
    private float visionTimer;
    private float repathTimer;
    private float nextAttackTime;
    private bool attackHitDone;
    private NetworkObject target;
    private Vector3 lastSeenPosition;
    private float lastSeenTime;
    private float loseSightDelay;
    private readonly List<Vector2Int> path = new List<Vector2Int>();
    private readonly Dictionary<Vector2Int, Vector2Int> parents = new Dictionary<Vector2Int, Vector2Int>();
    private readonly Dictionary<Vector2Int, int> distances = new Dictionary<Vector2Int, int>();
    private readonly Queue<Vector2Int> queue = new Queue<Vector2Int>();
    private readonly List<Vector2Int> cellBuffer = new List<Vector2Int>();

    public static IReadOnlyList<RobotEnemy> Instances => instances;
    public bool IsStunned => state == State.Stunned;

    private void Awake()
    {
        animator = GetComponentInChildren<Animator>();
        if (animator != null)
            animator.applyRootMotion = false;

        FitModel();
    }

    public override void OnNetworkSpawn()
    {
        instances.Add(this);
        anim.OnValueChanged += OnAnimChanged;
        PlayAnim(anim.Value, 0f);

        awakeTime = Time.time + spawnGracePeriod;
        waitUntil = awakeTime;
    }

    public override void OnNetworkDespawn()
    {
        instances.Remove(this);
        anim.OnValueChanged -= OnAnimChanged;
    }

    /// <summary>Server: the maze this robot walks in and the half it guards.</summary>
    public void ServerInit(CaveDungeonGenerator mazeGenerator, bool guardsRightSide)
    {
        generator = mazeGenerator;
        rightSide = guardsRightSide;
    }

    /// <summary>Server: hit with the knife. Knocks the robot down; a robot that is already down stays down longer.</summary>
    public void ServerStun()
    {
        if (!IsServer)
            return;

        state = State.Stunned;
        stateTime = 0f;
        target = null;
        path.Clear();
        SetAnim(Anim.KnockDown);
        StunnedRpc();
    }

    private void Update()
    {
        if (IsServer && IsSpawned)
            ServerUpdate();
    }

    // ================= SERVER: STATES =================

    private void ServerUpdate()
    {
        stateTime += Time.deltaTime;

        DungeonProgressManager manager = DungeonProgressManager.Instance;
        bool gameStopped = generator == null ||
                           manager != null && (manager.IsGameOver || manager.IsLevelCompleted());

        if (gameStopped)
        {
            // The attack and the knock-down play to the end, otherwise the robot just stands.
            if (state == State.Chase || state == State.Patrol)
                SetAnim(Anim.Idle);
            return;
        }

        switch (state)
        {
            case State.Patrol:
                UpdatePatrol();
                break;
            case State.Chase:
                UpdateChase();
                break;
            case State.Attack:
                UpdateAttack(manager);
                break;
            case State.Stunned:
                UpdateStunned();
                break;
        }
    }

    private void UpdatePatrol()
    {
        if (LookForPlayer())
        {
            StartChase();
            return;
        }

        if (path.Count == 0)
        {
            SetAnim(Anim.Idle);

            if (Time.time < waitUntil)
                return;

            if (!TryBuildPatrolPath())
            {
                waitUntil = Time.time + 1f;
                return;
            }
        }

        FollowPath(patrolSpeed);
        SetAnim(Anim.Walk);

        if (path.Count == 0)
            waitUntil = Time.time + Random.Range(patrolWaitMin, patrolWaitMax);
    }

    private void StartChase()
    {
        state = State.Chase;
        stateTime = 0f;
        repathTimer = 0f;
        path.Clear();
        AlertRpc();
    }

    private void UpdateChase()
    {
        bool sees = LookForPlayer();

        if (!sees && Time.time - lastSeenTime > loseSightDelay)
        {
            // Lost the player for good: back to patrolling after a short look around.
            state = State.Patrol;
            target = null;
            path.Clear();
            waitUntil = Time.time + 0.5f;
            SetAnim(Anim.Idle);
            return;
        }

        if (sees && target != null && Time.time >= nextAttackTime && IsInReach(target, attackRange))
        {
            StartAttack();
            return;
        }

        Vector2Int goal = WorldToCell(lastSeenPosition);

        repathTimer -= Time.deltaTime;
        if (repathTimer <= 0f)
        {
            repathTimer = 0.25f;
            BuildPath(WorldToCell(transform.position), goal, path);
        }

        Vector3 before = transform.position;

        // In the player's cell or the next one: straight at them instead of the cell centers. Only when the robot
        // can really get there; otherwise it walks to the nearest cell it may stand on and waits there.
        if (path.Count == 1 && path[0] == goal)
        {
            Vector3 destination = lastSeenPosition;
            destination.y = transform.position.y;
            MoveTowards(destination, chaseSpeed, attackRange * 0.6f);
        }
        else
        {
            FollowPath(chaseSpeed);
        }

        bool moved = (transform.position - before).sqrMagnitude > 0.000001f;
        SetAnim(moved ? Anim.Run : Anim.Idle);
    }

    private void StartAttack()
    {
        state = State.Attack;
        stateTime = 0f;
        attackHitDone = false;
        path.Clear();
        SetAnim(Anim.Attack);
    }

    private void UpdateAttack(DungeonProgressManager manager)
    {
        if (target != null)
            TurnTowards(target.transform.position - transform.position);

        if (!attackHitDone && stateTime >= attackHitTime)
        {
            attackHitDone = true;

            if (target != null && IsInReach(target, attackRange * attackReachMultiplier) &&
                manager != null && manager.KillPlayer(target.OwnerClientId))
            {
                CaughtRpc(RpcTarget.Single(target.OwnerClientId, RpcTargetUse.Temp));
            }
        }

        if (stateTime >= attackDuration)
        {
            // The player got away (or the game is over and the robot stands): keep chasing.
            state = State.Chase;
            stateTime = 0f;
            nextAttackTime = Time.time + 0.3f;
            lastSeenTime = Time.time;
            SetAnim(Anim.Run);
        }
    }

    private void UpdateStunned()
    {
        if (stateTime >= stunDuration - getUpDuration)
            SetAnim(Anim.GetUp);

        if (stateTime < stunDuration)
            return;

        state = State.Patrol;
        stateTime = 0f;
        path.Clear();
        waitUntil = Time.time + 0.3f;
        SetAnim(Anim.Idle);
    }

    // ================= SERVER: VISION =================

    /// <summary>Looks for the nearest visible player on this robot's half. Updates the target and where it was seen.</summary>
    private bool LookForPlayer()
    {
        if (Time.time < awakeTime)
            return false;

        visionTimer -= Time.deltaTime;
        if (visionTimer > 0f)
            return target != null && Time.time - lastSeenTime < 0.15f;

        visionTimer = 0.1f;

        NetworkObject best = null;
        float bestDistance = float.MaxValue;

        foreach (NetworkClient client in NetworkManager.ConnectedClientsList)
        {
            NetworkObject player = client.PlayerObject;
            if (player == null || !generator.IsOnSide(player.transform.position, rightSide))
                continue;

            if (!CanSee(player, out float distance) || distance >= bestDistance)
                continue;

            best = player;
            bestDistance = distance;
        }

        if (best == null)
            return false;

        target = best;
        lastSeenPosition = best.transform.position;

        // Seen again after losing them: a new 2-3 seconds before giving up.
        if (Time.time - lastSeenTime > 0.3f)
            loseSightDelay = Random.Range(loseSightTimeMin, loseSightTimeMax);

        lastSeenTime = Time.time;
        return true;
    }

    private bool CanSee(NetworkObject player, out float distance)
    {
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        Vector3 targetPoint = player.transform.position + Vector3.up * 1f;
        Vector3 toTarget = targetPoint - eye;
        distance = toTarget.magnitude;

        if (distance > viewDistance)
            return false;

        Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z);
        bool veryClose = flat.magnitude <= hearingRadius;

        if (!veryClose && Vector3.Angle(transform.forward, flat) > viewAngle * 0.5f)
            return false;

        return HasLineOfSight(eye, toTarget, distance, player);
    }

    // The first thing the ray hits (except the robot itself and triggers) has to be the player.
    private bool HasLineOfSight(Vector3 eye, Vector3 direction, float distance, NetworkObject player)
    {
        int count = Physics.RaycastNonAlloc(eye, direction / distance, rayHits, distance, ~0, QueryTriggerInteraction.Ignore);
        float nearest = float.MaxValue;
        Collider blocker = null;

        for (int i = 0; i < count; i++)
        {
            if (rayHits[i].collider.transform.IsChildOf(transform) || rayHits[i].distance >= nearest)
                continue;

            nearest = rayHits[i].distance;
            blocker = rayHits[i].collider;
        }

        return blocker == null || blocker.GetComponentInParent<NetworkObject>() == player;
    }

    private bool IsInReach(NetworkObject player, float range)
    {
        Vector3 offset = player.transform.position - transform.position;
        return Mathf.Abs(offset.y) < 1.8f && new Vector2(offset.x, offset.z).magnitude <= range;
    }

    // ================= SERVER: MOVEMENT =================

    private void FollowPath(float speed)
    {
        if (path.Count == 0)
            return;

        Vector3 next = generator.GetCellWorldPosition(path[0]);
        next.y = transform.position.y;

        if (MoveTowards(next, speed, 0f))
            path.RemoveAt(0);
    }

    // Returns true when the destination (minus stopDistance) is reached.
    private bool MoveTowards(Vector3 destination, float speed, float stopDistance)
    {
        Vector3 offset = destination - transform.position;
        offset.y = 0f;
        float distance = offset.magnitude;

        // Right at the point the direction is just noise: turning to it made the robot twitch around on the spot.
        if (distance > 0.05f)
            TurnTowards(offset);

        float travel = distance - stopDistance;
        if (travel <= 0f)
            return true;

        float step = speed * Time.deltaTime;
        bool arrived = step >= travel;
        Vector3 position = transform.position + offset / distance * Mathf.Min(step, travel);

        // Up and down the ramps: the robot always stands on the floor under it.
        position.y = generator.GetFloorHeight(position);
        transform.position = position;
        return arrived;
    }

    private void TurnTowards(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
            return;

        transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), turnSpeed * Time.deltaTime);
    }

    private Vector2Int WorldToCell(Vector3 position) => generator.WorldToCell(position);

    // ================= SERVER: PATHS ON THE MAZE GRID =================

    // Breadth-first search over the cells the robot may walk on, from its current cell.
    private void Explore(Vector2Int start)
    {
        parents.Clear();
        distances.Clear();
        queue.Clear();

        distances[start] = 0;
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();

            foreach (Vector2Int direction in Directions)
            {
                Vector2Int next = current + direction;
                if (distances.ContainsKey(next) || !generator.IsRobotWalkable(next, rightSide))
                    continue;

                distances[next] = distances[current] + 1;
                parents[next] = current;
                queue.Enqueue(next);
            }
        }
    }

    /// <summary>
    /// Path from start to goal; when the goal cannot be reached (the player is on a falling platform, in a doorway)
    /// the path leads to the reachable cell nearest to it. See BuildPathTo for why the robot's own cell is skipped.
    /// </summary>
    private void BuildPath(Vector2Int start, Vector2Int goal, List<Vector2Int> result)
    {
        Explore(start);

        Vector2Int best = start;
        int bestScore = int.MaxValue;

        foreach (KeyValuePair<Vector2Int, int> pair in distances)
        {
            int score = (Mathf.Abs(pair.Key.x - goal.x) + Mathf.Abs(pair.Key.y - goal.y)) * 1000 + pair.Value;
            if (score < bestScore)
            {
                bestScore = score;
                best = pair.Key;
            }
        }

        BuildPathTo(start, best, result);
    }

    private bool TryBuildPatrolPath()
    {
        Vector2Int start = WorldToCell(transform.position);
        Explore(start);

        cellBuffer.Clear();
        foreach (KeyValuePair<Vector2Int, int> pair in distances)
        {
            if (pair.Value >= patrolMinDistance)
                cellBuffer.Add(pair.Key);
        }

        // A tiny area: any other cell will do.
        if (cellBuffer.Count == 0)
        {
            foreach (Vector2Int cell in distances.Keys)
            {
                if (cell != start)
                    cellBuffer.Add(cell);
            }
        }

        if (cellBuffer.Count == 0)
            return false;

        BuildPathTo(start, cellBuffer[Random.Range(0, cellBuffer.Count)], path);
        return path.Count > 0;
    }

    private void BuildPathTo(Vector2Int start, Vector2Int end, List<Vector2Int> result)
    {
        result.Clear();

        Vector2Int current = end;
        while (current != start && parents.TryGetValue(current, out Vector2Int parent))
        {
            result.Add(current);
            current = parent;
        }

        result.Add(start);
        result.Reverse();

        // The robot heads straight for the next cell instead of first going back to the center of its own one.
        // Two neighbouring cells share their width across the move, so together they form one rectangle and the
        // straight line never cuts a wall corner. Going back to the own center made the robot spin while chasing:
        // the path is rebuilt every 0.25 s, and every time that center was behind a robot that had already passed it.
        if (result.Count >= 2)
            result.RemoveAt(0);
    }

    // ================= ANIMATION / FEEDBACK (everyone) =================

    private void SetAnim(Anim value)
    {
        if (anim.Value != (byte)value)
            anim.Value = (byte)value;
    }

    private void OnAnimChanged(byte oldValue, byte newValue)
    {
        PlayAnim(newValue, 0.15f);
    }

    private void PlayAnim(byte value, float fade)
    {
        if (animator == null || animator.runtimeAnimatorController == null || value >= AnimStates.Length)
            return;

        animator.CrossFadeInFixedTime(AnimStates[value], fade);
    }

    [Rpc(SendTo.Everyone)]
    private void AlertRpc()
    {
        AudioManager.PlayAt(Sfx.RobotAlert, transform.position, soundMaxDistance);
    }

    [Rpc(SendTo.Everyone)]
    private void StunnedRpc()
    {
        AudioManager.PlayAt(Sfx.RobotStunned, transform.position, soundMaxDistance);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void CaughtRpc(RpcParams rpcParams)
    {
        DamageVignette.Flash();
    }

    // Scales the imported model to modelHeight (never wider than modelMaxWidth) and stands it on the root.
    private void FitModel()
    {
        Transform model = animator != null ? animator.transform : transform.childCount > 0 ? transform.GetChild(0) : null;
        if (model == null || model == transform)
            return;

        Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return;

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        if (bounds.size.y < 0.01f)
            return;

        float scale = modelHeight / bounds.size.y;
        float width = Mathf.Max(bounds.size.x, bounds.size.z) * scale;
        if (width > modelMaxWidth)
            scale *= modelMaxWidth / width;

        // Scaling around the model pivot moves its feet; put them back on the robot's root.
        Vector3 pivot = model.position;
        float feetY = pivot.y + (bounds.min.y - pivot.y) * scale;

        model.localScale *= scale;
        model.position += Vector3.up * (transform.position.y - feetY);

        CapsuleCollider capsule = GetComponent<CapsuleCollider>();
        if (capsule != null)
        {
            float height = bounds.size.y * scale;
            capsule.radius = colliderRadius;
            capsule.height = height;
            capsule.center = Vector3.up * (height * 0.5f);
        }
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.6f, 0f, 0.5f);
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        Quaternion left = Quaternion.Euler(0f, -viewAngle * 0.5f, 0f);
        Quaternion right = Quaternion.Euler(0f, viewAngle * 0.5f, 0f);
        Gizmos.DrawLine(eye, eye + left * transform.forward * viewDistance);
        Gizmos.DrawLine(eye, eye + right * transform.forward * viewDistance);
        Gizmos.DrawWireSphere(transform.position, hearingRadius);
    }
}
