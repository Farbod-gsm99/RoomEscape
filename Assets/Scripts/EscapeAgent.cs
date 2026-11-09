using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace EscapeRoomML
{
    /// <summary>
    /// Unity 6 + ML-Agents 4.1 3D Cube Escape Agent (1x1x1 BoxCollider)
    /// Includes:
    /// - Downward Cliff/Pit & Low-Hazard Raycast Probes (so the agent can see floor gaps & lasers to jump over)
    /// - High-Water-Mark Horizontal Progress Reward (prevents running in circles at spawn)
    /// - Contextual Jump Shaping (rewards jumping forward when a pit or hazard is ahead)
    /// Vector Observation Space Size remains 14.
    /// </summary>
    [RequireComponent(typeof(Rigidbody), typeof(BoxCollider))]
    public class EscapeAgent : Agent
    {
        [Header("Arena Reference")]
        public EscapeArenaManager arenaManager;

        [Header("Movement & Jump Settings")]
        [Tooltip("Forward/backward and strafe movement speed (m/s).")]
        public float moveSpeed = 6.0f;

        [Tooltip("Rotation speed in degrees per second.")]
        public float turnSpeed = 180f;

        [Tooltip("Upward velocity applied when jumping (6.5 clears a 1.5m pit and 0.8m platform).")]
        public float jumpVelocity = 6.5f;

        [Tooltip("Extra gravity multiplier while falling for snappy platforming over lasers.")]
        public float fallGravityMultiplier = 2.0f;

        [Header("Cube Ground & Pit Check (Centered on 1x1x1 Cube)")]
        [Tooltip("Half-extents of the ground check box cast below the cube center.")]
        public Vector3 groundBoxHalfExtents = new Vector3(0.42f, 0.08f, 0.42f);
        [Tooltip("Cast distance downward from the center of the 1x1x1 cube.")]
        public float groundCastDistance = 0.52f;
        public LayerMask groundLayerMask = ~0;

        [Header("Reward Shaping (Anti-Looping & Jump Discovery)")]
        [Tooltip("Small per-step time penalty to encourage escaping quickly.")]
        public float stepTimePenalty = -0.0005f;

        [Tooltip("Reward per meter of NEW closest horizontal progress toward the active objective (cannot be farmed in circles).")]
        public float progressRewardPerMeter = 0.15f;

        [Tooltip("Small bonus when jumping forward across a detected pit edge or low hazard.")]
        public float smartJumpBonus = 0.06f;

        [Tooltip("Tiny penalty for jumping when no pit, hazard, or elevated target is nearby.")]
        public float unnecessaryJumpPenalty = -0.008f;

        [Tooltip("Y-threshold below the room floor that counts as falling into a pit.")]
        public float pitFallYOffset = -1.8f;

        // Runtime state
        public float CumulativeEpisodeReward { get; private set; }
        public bool IsGrounded { get; private set; }

        private Rigidbody _rb;
        private BoxCollider _boxCollider;
        private float _closestHorizontalDistToObjective;
        private Vector3 _lastTrackedObjectivePos;
        private float _jumpCooldownTimer;
        private bool _isFrozenForVictory;

        // Forward pit/hazard perception cache updated every FixedUpdate
        private bool _isPitAheadNear;
        private bool _isPitAheadFar;
        private bool _isLowHazardAhead;

        public override void Initialize()
        {
            _rb = GetComponent<Rigidbody>();
            _boxCollider = GetComponent<BoxCollider>();
            _rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

            // Apply a zero-friction material so the Cube Agent never snags on walls or floor seams
#if UNITY_6000_0_OR_NEWER
            var slipMat = new PhysicsMaterial("CubeAgentZeroFriction")
            {
                dynamicFriction = 0f,
                staticFriction = 0f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounciness = 0f,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
#else
            var slipMat = new PhysicMaterial("CubeAgentZeroFriction")
            {
                dynamicFriction = 0f,
                staticFriction = 0f,
                frictionCombine = PhysicMaterialCombine.Minimum,
                bounciness = 0f,
                bounceCombine = PhysicMaterialCombine.Minimum
            };
#endif
            _boxCollider.material = slipMat;

            if (arenaManager == null)
                arenaManager = GetComponentInParent<EscapeArenaManager>();
        }

        public override void OnEpisodeBegin()
        {
            if (_isFrozenForVictory)
                return;

            CumulativeEpisodeReward = 0f;
            _jumpCooldownTimer = 0f;

            if (arenaManager != null)
            {
                arenaManager.BeginRoomEpisode();
                ResetObjectiveProgressTracker();
            }
        }

        /// <summary>
        /// Resets the high-water-mark distance tracker whenever a room starts or a sub-puzzle is solved.
        /// </summary>
        public void ResetObjectiveProgressTracker()
        {
            if (arenaManager != null && arenaManager.CurrentRoom != null)
            {
                _lastTrackedObjectivePos = arenaManager.CurrentRoom.GetCurrentObjectiveWorldPosition();
                _closestHorizontalDistToObjective = HorizontalDistance(transform.position, _lastTrackedObjectivePos);
            }
        }

        /// <summary>
        /// Collects 14 Vector Observations:
        /// [0]     Normalized Remaining Room Time (0..1)
        /// [1]     Normalized Current Room Index (0..1)
        /// [2]     Is Grounded (0 or 1)
        /// [3]     Is Current Room Door Unlocked (0 or 1)
        /// [4..6]  Agent Local Velocity Normalized (x, y, z)
        /// [7..9]  Relative Vector to Current Active Objective in Local Space (x, y, z)
        /// [10]    Pit / Floor Gap Detected 1.1m Ahead (0 or 1)
        /// [11]    Pit / Floor Gap Detected 2.2m Ahead (0 or 1)
        /// [12]    Low Jumpable Hazard Detected Ahead (0 or 1)
        /// [13]    Current Room Puzzle Completion Progress (0..1)
        /// Total Vector Observation Space Size = 14.
        /// </summary>
        public override void CollectObservations(VectorSensor sensor)
        {
            if (arenaManager == null || arenaManager.CurrentRoom == null)
            {
                for (int i = 0; i < 14; i++)
                    sensor.AddObservation(0f);
                return;
            }

            EscapeRoomController room = arenaManager.CurrentRoom;
            UpdateGroundedAndForwardProbes();

            // 1. Normalized remaining room time (1 float)
            float normTime = arenaManager.CurrentRoomTimeLimit > 0f
                ? Mathf.Clamp01(arenaManager.RemainingRoomTime / arenaManager.CurrentRoomTimeLimit)
                : 0f;
            sensor.AddObservation(normTime);

            // 2. Normalized room index (1 float)
            float maxRoomIdx = Mathf.Max(1, arenaManager.rooms.Length - 1);
            sensor.AddObservation(Mathf.Clamp01(arenaManager.CurrentRoomIndex / maxRoomIdx));

            // 3. Grounded state (1 float)
            sensor.AddObservation(IsGrounded ? 1f : 0f);

            // 4. Exit door unlocked state (1 float)
            sensor.AddObservation(room.IsDoorUnlocked ? 1f : 0f);

            // 5. Local velocity normalized (3 floats)
            Vector3 currentVel = GetLinearVelocity();
            Vector3 localVel = transform.InverseTransformDirection(currentVel) / Mathf.Max(1f, moveSpeed);
            sensor.AddObservation(Vector3.ClampMagnitude(localVel, 1.5f));

            // 6. Relative vector to current active objective (3 floats)
            Vector3 objectiveWorldPos = room.GetCurrentObjectiveWorldPosition();
            Vector3 toObjectiveLocal = transform.InverseTransformPoint(objectiveWorldPos) / 16f;
            sensor.AddObservation(Vector3.ClampMagnitude(toObjectiveLocal, 1.5f));

            // 7. Forward Pit & Jumpable Hazard Probes (3 floats — lets the agent "see" pits in the floor!)
            sensor.AddObservation(_isPitAheadNear ? 1f : 0f);
            sensor.AddObservation(_isPitAheadFar ? 1f : 0f);
            sensor.AddObservation(_isLowHazardAhead ? 1f : 0f);

            // 8. Room puzzle completion fraction (1 float)
            sensor.AddObservation(room.GetPuzzleProgress());
        }

        public override void OnActionReceived(ActionBuffers actionBuffers)
        {
            if (_isFrozenForVictory || arenaManager == null || arenaManager.HasHeroCompletedAllRooms)
                return;

            UpdateGroundedAndForwardProbes();
            if (_jumpCooldownTimer > 0f)
                _jumpCooldownTimer -= Time.fixedDeltaTime;

            var discreteActions = actionBuffers.DiscreteActions;
            int moveAction = discreteActions[0];
            int strafeAction = discreteActions[1];
            int rotateAction = discreteActions[2];
            int jumpAction = discreteActions[3];

            // 1. Rotation
            float turnDir = 0f;
            if (rotateAction == 1) turnDir = -1f;
            else if (rotateAction == 2) turnDir = 1f;

            if (Mathf.Abs(turnDir) > 0.01f)
            {
                Quaternion deltaRot = Quaternion.Euler(0f, turnDir * turnSpeed * Time.fixedDeltaTime, 0f);
                _rb.MoveRotation(_rb.rotation * deltaRot);
            }

            // 2. Horizontal Translation
            float forwardDir = 0f;
            if (moveAction == 1) forwardDir = 1f;
            else if (moveAction == 2) forwardDir = -0.75f;

            float strafeDir = 0f;
            if (strafeAction == 1) strafeDir = -0.85f;
            else if (strafeAction == 2) strafeDir = 0.85f;

            Vector3 desiredHorizontalDir = (transform.forward * forwardDir + transform.right * strafeDir);
            if (desiredHorizontalDir.sqrMagnitude > 1f)
                desiredHorizontalDir.Normalize();

            Vector3 currentVel = GetLinearVelocity();
            Vector3 targetVel = new Vector3(
                desiredHorizontalDir.x * moveSpeed,
                currentVel.y,
                desiredHorizontalDir.z * moveSpeed
            );

            // 3. Jump Action + Contextual Jump Shaping
            if (jumpAction == 1 && IsGrounded && _jumpCooldownTimer <= 0f)
            {
                targetVel.y = jumpVelocity;
                IsGrounded = false;
                _jumpCooldownTimer = 0.35f;

                EscapeRoomController currentRoom = arenaManager.CurrentRoom;
                bool objectiveIsElevated = currentRoom != null &&
                    (currentRoom.GetCurrentObjectiveWorldPosition().y > transform.position.y + 0.15f);

                if ((_isPitAheadNear || _isPitAheadFar || _isLowHazardAhead || objectiveIsElevated) && moveAction == 1)
                {
                    AddReward(smartJumpBonus);
                }
                else
                {
                    AddReward(unnecessaryJumpPenalty);
                }
            }

            if (!IsGrounded && targetVel.y < 0f)
            {
                targetVel.y += Physics.gravity.y * (fallGravityMultiplier - 1f) * Time.fixedDeltaTime;
            }

            SetLinearVelocity(targetVel);

            // 4. Per-step time penalty
            AddReward(stepTimePenalty);

            // 5. High-Water-Mark Horizontal Progress Reward (cannot be farmed by running in circles!)
            EscapeRoomController room = arenaManager.CurrentRoom;
            if (room != null)
            {
                Vector3 objectivePos = room.GetCurrentObjectiveWorldPosition();

                // If the room's active objective switched (e.g., picked up Key -> now targeting Exit Door),
                // reset the high-water-mark baseline to the new target.
                if (Vector3.SqrMagnitude(objectivePos - _lastTrackedObjectivePos) > 1.0f)
                {
                    _lastTrackedObjectivePos = objectivePos;
                    _closestHorizontalDistToObjective = HorizontalDistance(transform.position, objectivePos);
                }

                float currentHorizDist = HorizontalDistance(transform.position, objectivePos);
                if (currentHorizDist < _closestHorizontalDistToObjective)
                {
                    float newProgress = _closestHorizontalDistToObjective - currentHorizDist;
                    AddReward(newProgress * progressRewardPerMeter);
                    _closestHorizontalDistToObjective = currentHorizDist;
                }

                // Check if the agent fell into a pit
                if (transform.position.y < room.transform.position.y + pitFallYOffset)
                {
                    arenaManager.OnAgentFailed("Fell in Pit", isTimeout: false);
                }
            }

            CumulativeEpisodeReward = GetCumulativeReward();
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var discreteActions = actionsOut.DiscreteActions;
            discreteActions[0] = 0;
            discreteActions[1] = 0;
            discreteActions[2] = 0;
            discreteActions[3] = 0;

#if ENABLE_INPUT_SYSTEM
            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) discreteActions[0] = 1;
                else if (kb.sKey.isPressed || kb.downArrowKey.isPressed) discreteActions[0] = 2;

                if (kb.aKey.isPressed) discreteActions[1] = 1;
                else if (kb.dKey.isPressed) discreteActions[1] = 2;

                if (kb.qKey.isPressed || kb.leftArrowKey.isPressed) discreteActions[2] = 1;
                else if (kb.eKey.isPressed || kb.rightArrowKey.isPressed) discreteActions[2] = 2;

                if (kb.spaceKey.isPressed) discreteActions[3] = 1;
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) discreteActions[0] = 1;
            else if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) discreteActions[0] = 2;

            if (Input.GetKey(KeyCode.A)) discreteActions[1] = 1;
            else if (Input.GetKey(KeyCode.D)) discreteActions[1] = 2;

            if (Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.LeftArrow)) discreteActions[2] = 1;
            else if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.RightArrow)) discreteActions[2] = 2;

            if (Input.GetKey(KeyCode.Space)) discreteActions[3] = 1;
#endif
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision.collider.CompareTag("Hazard"))
                OnHitHazard(collision.collider.name);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag("Hazard"))
                OnHitHazard(other.name);
        }

        public void OnHitHazard(string hazardName)
        {
            if (arenaManager != null && !_isFrozenForVictory)
                arenaManager.OnAgentFailed($"Hit {hazardName}", isTimeout: false);
        }

        public void OnReachedRoomExitTrigger()
        {
            if (arenaManager != null && !_isFrozenForVictory)
                arenaManager.OnAgentClearedCurrentRoom();
        }

        public void TeleportToPose(Vector3 position, Quaternion rotation)
        {
            if (_rb == null)
                _rb = GetComponent<Rigidbody>();

            SetLinearVelocity(Vector3.zero);
            _rb.angularVelocity = Vector3.zero;
            transform.SetPositionAndRotation(position, rotation);
            _rb.position = position;
            _rb.rotation = rotation;
            UpdateGroundedAndForwardProbes();
        }

        public void FreezeAgentOnVictory()
        {
            _isFrozenForVictory = true;
            if (_rb != null)
            {
                SetLinearVelocity(Vector3.zero);
                _rb.angularVelocity = Vector3.zero;
                _rb.isKinematic = true;
            }
        }

        private void UpdateGroundedAndForwardProbes()
        {
            IsGrounded = Physics.BoxCast(
                transform.position,
                groundBoxHalfExtents,
                Vector3.down,
                out _,
                transform.rotation,
                groundCastDistance,
                groundLayerMask,
                QueryTriggerInteraction.Ignore
            );

            // Probe 1.1m and 2.2m ahead downward to detect floor gaps (pits)
            Vector3 nearProbeOrigin = transform.position + transform.forward * 1.1f;
            Vector3 farProbeOrigin = transform.position + transform.forward * 2.2f;

            _isPitAheadNear = !Physics.Raycast(
                nearProbeOrigin,
                Vector3.down,
                1.3f,
                groundLayerMask,
                QueryTriggerInteraction.Ignore
            );

            _isPitAheadFar = !Physics.Raycast(
                farProbeOrigin,
                Vector3.down,
                1.3f,
                groundLayerMask,
                QueryTriggerInteraction.Ignore
            );

            // Probe forward at ankle/waist height to detect jumpable hazards/lasers
            _isLowHazardAhead = false;
            if (Physics.SphereCast(
                transform.position + Vector3.down * 0.1f,
                0.35f,
                transform.forward,
                out RaycastHit hit,
                2.5f,
                ~0,
                QueryTriggerInteraction.Collide))
            {
                if (hit.collider.CompareTag("Hazard"))
                {
                    _isLowHazardAhead = true;
                }
            }
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private Vector3 GetLinearVelocity()
        {
#if UNITY_6000_0_OR_NEWER
            return _rb.linearVelocity;
#else
            return _rb.velocity;
#endif
        }

        private void SetLinearVelocity(Vector3 vel)
        {
#if UNITY_6000_0_OR_NEWER
            _rb.linearVelocity = vel;
#else
            _rb.velocity = vel;
#endif
        }
    }
}