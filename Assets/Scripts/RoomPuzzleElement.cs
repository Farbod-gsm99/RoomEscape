using UnityEngine;

namespace EscapeRoomML
{
    /// <summary>
    /// Unified component for interactive room puzzle mechanics and hazards:
    /// - CollectibleKey (Room 2: pick up key to unlock door)
    /// - BlockPressureSwitch (Room 3: push physics block onto floor switch)
    /// - AgentPressurePlate (Room 3: jump over sweeping laser onto elevated pressure plate)
    /// - MovingHazard (Rooms 1-3: patrolling obstacles or sweeping laser beams)
    /// - ExitZoneTrigger (Placed just past each room's exit door to detect room completion)
    /// </summary>
    public class RoomPuzzleElement : MonoBehaviour
    {
        public enum ElementType
        {
            CollectibleKey,
            BlockPressureSwitch,
            AgentPressurePlate,
            MovingHazard,
            ExitZoneTrigger
        }

        [Header("Element Role")]
        public ElementType elementType = ElementType.CollectibleKey;

        [Tooltip("Reward granted the first time this puzzle element is solved during an episode.")]
        public float solveReward = 0.6f;

        [Header("Randomization (Optional)")]
        [Tooltip("Slightly randomize initial position on episode reset (useful for Keys or PushBlocks).")]
        public bool randomizePositionOnReset = false;
        public float randomPositionRadius = 0.5f;

        [Header("Block Pressure Switch Settings (Room 3)")]
        [Tooltip("Reference to the pushable physics crate that activates this switch.")]
        public Rigidbody associatedPushBlock;
        [Tooltip("Small reward per step for moving the push block closer to this switch.")]
        public bool enableBlockDistanceShaping = true;

        [Header("Pressure Plate Settings")]
        [Tooltip("If true, once stepped on or triggered, the plate stays solved for the rest of the room attempt.")]
        public bool latchWhenActivated = true;

        [Header("Moving / Sweeping Hazard Settings")]
        [Tooltip("Local offset for ping-pong patrol movement (e.g., (4, 0, 0) moves 4m left/right).")]
        public Vector3 patrolLocalOffset = new Vector3(3.5f, 0f, 0f);
        [Tooltip("Speed of ping-pong patrol movement.")]
        public float patrolSpeed = 2.0f;
        [Tooltip("Degrees per second rotation for sweeping lasers or spinning hazards (e.g., (0, 65, 0)).")]
        public Vector3 rotationSpeedEuler = Vector3.zero;
        [Tooltip("Randomize hazard phase offset on reset so the agent times its jump dynamically instead of memorizing frame counts.")]
        public bool randomizeHazardPhase = true;

        [Header("Visual Feedback")]
        public Renderer statusRenderer;
        public Color inactiveColor = new Color(0.9f, 0.25f, 0.2f);
        public Color activeColor = new Color(0.2f, 0.9f, 0.35f);

        // Runtime state
        public bool IsSolved { get; private set; }

        private EscapeRoomController _roomController;
        private Vector3 _initialLocalPos;
        private Quaternion _initialLocalRot;
        private Vector3 _blockInitialLocalPos;
        private Quaternion _blockInitialLocalRot;
        private float _previousBlockDistance;
        private float _hazardTimeOffset;
        private MaterialPropertyBlock _propBlock;
        private Collider _myCollider;

        private void Awake()
        {
            _propBlock = new MaterialPropertyBlock();
            _myCollider = GetComponent<Collider>();
            _initialLocalPos = transform.localPosition;
            _initialLocalRot = transform.localRotation;

            if (associatedPushBlock != null)
            {
                _blockInitialLocalPos = associatedPushBlock.transform.localPosition;
                _blockInitialLocalRot = associatedPushBlock.transform.localRotation;
            }
        }

        public void Initialize(EscapeRoomController controller)
        {
            _roomController = controller;
        }

        private void Update()
        {
            if (elementType == ElementType.MovingHazard)
            {
                UpdateHazardMotion();
            }
            else if (elementType == ElementType.CollectibleKey && !IsSolved)
            {
                // Gentle visual spin so viewers can spot the key easily
                transform.Rotate(0f, 90f * Time.deltaTime, 0f, Space.Self);
            }
        }

        private void FixedUpdate()
        {
            // Continuous shaping reward when pushing the crate toward the floor switch in Room 3
            if (elementType == ElementType.BlockPressureSwitch &&
                !IsSolved &&
                enableBlockDistanceShaping &&
                associatedPushBlock != null &&
                _roomController != null)
            {
                float currentDist = Vector3.Distance(associatedPushBlock.position, transform.position);
                float delta = _previousBlockDistance - currentDist;
                if (Mathf.Abs(delta) > 0.001f)
                {
                    var arena = _roomController.GetComponentInParent<EscapeArenaManager>();
                    if (arena != null && arena.agent != null)
                    {
                        arena.agent.AddReward(Mathf.Clamp(delta * 0.15f, -0.02f, 0.03f));
                    }
                    _previousBlockDistance = currentDist;
                }
            }
        }

        private void UpdateHazardMotion()
        {
            float t = Time.time * patrolSpeed + _hazardTimeOffset;
            if (patrolLocalOffset.sqrMagnitude > 0.0001f)
            {
                float factor = Mathf.PingPong(t, 1f);
                transform.localPosition = Vector3.Lerp(
                    _initialLocalPos - patrolLocalOffset * 0.5f,
                    _initialLocalPos + patrolLocalOffset * 0.5f,
                    factor
                );
            }

            if (rotationSpeedEuler.sqrMagnitude > 0.0001f)
            {
                transform.Rotate(rotationSpeedEuler * Time.deltaTime, Space.Self);
            }
        }

        /// <summary>
        /// Resets puzzle state, positions, and associated physics blocks at the start of a room attempt.
        /// </summary>
        public void ResetElement()
        {
            IsSolved = false;

            Vector3 jitter = Vector3.zero;
            if (randomizePositionOnReset && randomPositionRadius > 0f)
            {
                Vector2 offset2D = Random.insideUnitCircle * randomPositionRadius;
                jitter = new Vector3(offset2D.x, 0f, offset2D.y);
            }

            transform.localPosition = _initialLocalPos + jitter;
            transform.localRotation = _initialLocalRot;

            if (elementType == ElementType.CollectibleKey)
            {
                gameObject.SetActive(true);
                if (_myCollider != null)
                    _myCollider.enabled = true;
            }
            else if (elementType == ElementType.BlockPressureSwitch && associatedPushBlock != null)
            {
                Vector3 blockJitter = Vector3.zero;
                if (randomizePositionOnReset && randomPositionRadius > 0f)
                {
                    Vector2 offset2D = Random.insideUnitCircle * randomPositionRadius;
                    blockJitter = new Vector3(offset2D.x, 0f, offset2D.y);
                }

                associatedPushBlock.transform.localPosition = _blockInitialLocalPos + blockJitter;
                associatedPushBlock.transform.localRotation = _blockInitialLocalRot;
                SetRigidbodyZeroVelocity(associatedPushBlock);
                _previousBlockDistance = Vector3.Distance(associatedPushBlock.position, transform.position);
            }
            else if (elementType == ElementType.MovingHazard)
            {
                _hazardTimeOffset = randomizeHazardPhase ? Random.Range(0f, 10f) : 0f;
            }

            UpdateVisualColor(false);
        }

        /// <summary>
        /// Returns the position the agent should aim toward to solve this puzzle element.
        /// For a BlockPressureSwitch, if the block isn't on the switch yet, returns the block's position.
        /// </summary>
        public Vector3 GetObjectiveTargetPosition()
        {
            if (elementType == ElementType.BlockPressureSwitch && !IsSolved && associatedPushBlock != null)
            {
                return associatedPushBlock.position;
            }
            return transform.position;
        }

        private void OnTriggerEnter(Collider other)
        {
            HandleOverlap(other);
        }

        private void OnCollisionEnter(Collision collision)
        {
            HandleOverlap(collision.collider);
        }

        private void OnTriggerExit(Collider other)
        {
            if (elementType == ElementType.BlockPressureSwitch && !latchWhenActivated && IsSolved)
            {
                if (IsMatchingPushBlock(other))
                {
                    IsSolved = false;
                    UpdateVisualColor(false);
                    if (_roomController != null)
                        _roomController.NotifyPuzzleStateChanged();
                }
            }
        }

        private void HandleOverlap(Collider other)
        {
            if (other == null)
                return;

            switch (elementType)
            {
                case ElementType.CollectibleKey:
                    if (!IsSolved && other.TryGetComponent<EscapeAgent>(out var keyAgent))
                    {
                        MarkSolved(keyAgent);
                        gameObject.SetActive(false);
                    }
                    break;

                case ElementType.AgentPressurePlate:
                    if (!IsSolved && other.TryGetComponent<EscapeAgent>(out var plateAgent))
                    {
                        MarkSolved(plateAgent);
                    }
                    break;

                case ElementType.BlockPressureSwitch:
                    if (!IsSolved && IsMatchingPushBlock(other))
                    {
                        var arena = _roomController != null
                            ? _roomController.GetComponentInParent<EscapeArenaManager>()
                            : GetComponentInParent<EscapeArenaManager>();
                        MarkSolved(arena != null ? arena.agent : null);
                    }
                    break;

                case ElementType.MovingHazard:
                    if (other.TryGetComponent<EscapeAgent>(out var hitAgent))
                    {
                        hitAgent.OnHitHazard(gameObject.name);
                    }
                    break;

                case ElementType.ExitZoneTrigger:
                    if (other.TryGetComponent<EscapeAgent>(out var exitingAgent))
                    {
                        exitingAgent.OnReachedRoomExitTrigger();
                    }
                    break;
            }
        }

        private bool IsMatchingPushBlock(Collider other)
        {
            if (associatedPushBlock != null && other.attachedRigidbody == associatedPushBlock)
                return true;

            return other.CompareTag("PushBlock");
        }

        private void MarkSolved(EscapeAgent agent)
        {
            IsSolved = true;
            UpdateVisualColor(true);

            if (agent != null && solveReward > 0f)
            {
                agent.AddReward(solveReward);
            }

            if (_roomController != null)
            {
                _roomController.NotifyPuzzleStateChanged();
            }
        }

        private void UpdateVisualColor(bool solved)
        {
            if (statusRenderer == null)
                return;

            statusRenderer.GetPropertyBlock(_propBlock);
            Color c = solved ? activeColor : inactiveColor;
            _propBlock.SetColor("_BaseColor", c);
            _propBlock.SetColor("_Color", c);
            statusRenderer.SetPropertyBlock(_propBlock);
        }

        private static void SetRigidbodyZeroVelocity(Rigidbody rb)
        {
            if (rb == null || rb.isKinematic)
                return;

#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = Vector3.zero;
#else
            rb.velocity = Vector3.zero;
#endif
            rb.angularVelocity = Vector3.zero;
        }
    }
}