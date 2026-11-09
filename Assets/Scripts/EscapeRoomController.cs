using System.Collections.Generic;
using UnityEngine;

namespace EscapeRoomML
{
    /// <summary>
    /// Controls a single room's puzzle elements, moving hazards, exit door, and spawn points.
    /// Attach this to the root GameObject of each room (e.g., Room_1, Room_2, Room_3).
    /// </summary>
    public class EscapeRoomController : MonoBehaviour
    {
        [Header("Room Identity & Timing")]
        [Tooltip("Display name shown on the YouTube HUD (e.g., 'Room 1: Moving Hazards').")]
        public string roomTitle = "Room 1: Hazard Gauntlet";

        [Tooltip("Maximum time in seconds allowed to escape this room before respawning.")]
        public float roomTimeLimit = 30f;

        [Header("Spawning")]
        [Tooltip("Where the agent spawns when starting or retrying this room.")]
        public Transform agentSpawnPoint;

        [Tooltip("Optional camera anchor transform for framing this room when the Hero Agent is inside.")]
        public Transform cameraAnchor;

        [Tooltip("Enable small position jitter on spawn to prevent deterministic local loops.")]
        public bool randomizeAgentSpawn = true;

        [Tooltip("Maximum horizontal jitter (in meters) applied to the agent's spawn position.")]
        public float spawnJitterRadius = 0.6f;

        [Header("Exit Door Settings")]
        [Tooltip("The physical door mesh/collider that blocks passage to the next room.")]
        public Transform exitDoorTransform;

        [Tooltip("Local offset applied to exitDoorTransform when the door opens (e.g., (0, 3, 0) slides up).")]
        public Vector3 doorOpenOffset = new Vector3(0f, 3.2f, 0f);

        [Tooltip("Speed at which the exit door slides open or closed.")]
        public float doorSlideSpeed = 8f;

        [Tooltip("Optional Renderer on the door or doorframe to tint Red (Locked) / Green (Unlocked).")]
        public Renderer doorStatusRenderer;

        public Color lockedColor = new Color(0.85f, 0.2f, 0.2f);
        public Color unlockedColor = new Color(0.2f, 0.85f, 0.35f);

        [Header("Room Puzzles & Hazards")]
        [Tooltip("All puzzle elements (Keys, Block Switches, Jump Pressure Plates) required to unlock this room's exit door. Leave empty if the door starts unlocked (e.g., Room 1).")]
        public List<RoomPuzzleElement> requiredPuzzles = new List<RoomPuzzleElement>();

        [Tooltip("All moving hazards or sweeping lasers inside this room so they reset cleanly on retry.")]
        public List<RoomPuzzleElement> roomHazards = new List<RoomPuzzleElement>();

        // Runtime state
        public bool IsDoorUnlocked { get; private set; }
        public bool IsEntryClosed { get; private set; }

        private Vector3 _doorClosedLocalPos;
        private Vector3 _doorTargetLocalPos;
        private MaterialPropertyBlock _propBlock;

        private void Awake()
        {
            _propBlock = new MaterialPropertyBlock();

            if (exitDoorTransform != null)
            {
                _doorClosedLocalPos = exitDoorTransform.localPosition;
                _doorTargetLocalPos = _doorClosedLocalPos;
            }

            // Register this room controller with all child puzzle & hazard elements
            foreach (var puzzle in requiredPuzzles)
            {
                if (puzzle != null)
                    puzzle.Initialize(this);
            }

            foreach (var hazard in roomHazards)
            {
                if (hazard != null)
                    hazard.Initialize(this);
            }
        }

        private void Update()
        {
            if (exitDoorTransform != null)
            {
                exitDoorTransform.localPosition = Vector3.MoveTowards(
                    exitDoorTransform.localPosition,
                    _doorTargetLocalPos,
                    doorSlideSpeed * Time.deltaTime
                );
            }
        }

        /// <summary>
        /// Resets all puzzles, pushable blocks, keys, and hazards in this room for a fresh attempt.
        /// </summary>
        public void ResetRoom()
        {
            foreach (var puzzle in requiredPuzzles)
            {
                if (puzzle != null)
                    puzzle.ResetElement();
            }

            foreach (var hazard in roomHazards)
            {
                if (hazard != null)
                    hazard.ResetElement();
            }

            EvaluateDoorState();
        }

        /// <summary>
        /// Explicitly locks and closes this room's exit door (used when the agent advances past this room
        /// so the door shuts permanently behind it).
        /// </summary>
        public void CloseAndLockDoorBehindAgent()
        {
            IsDoorUnlocked = false;
            IsEntryClosed = true;
            _doorTargetLocalPos = _doorClosedLocalPos;
            if (exitDoorTransform != null)
            {
                exitDoorTransform.localPosition = _doorClosedLocalPos;
            }
            UpdateDoorVisuals(false);
        }

        /// <summary>
        /// Checks whether all required puzzles in this room have been solved and opens/locks the door accordingly.
        /// </summary>
        public void EvaluateDoorState()
        {
            bool allSolved = true;
            for (int i = 0; i < requiredPuzzles.Count; i++)
            {
                if (requiredPuzzles[i] != null && !requiredPuzzles[i].IsSolved)
                {
                    allSolved = false;
                    break;
                }
            }

            IsDoorUnlocked = allSolved;
            _doorTargetLocalPos = IsDoorUnlocked
                ? _doorClosedLocalPos + doorOpenOffset
                : _doorClosedLocalPos;

            UpdateDoorVisuals(IsDoorUnlocked);
        }

        /// <summary>
        /// Called by a child RoomPuzzleElement when it becomes solved or unsolved.
        /// </summary>
        public void NotifyPuzzleStateChanged()
        {
            EvaluateDoorState();
        }

        /// <summary>
        /// Returns normalized progress [0..1] of puzzles solved in this room.
        /// </summary>
        public float GetPuzzleProgress()
        {
            if (requiredPuzzles == null || requiredPuzzles.Count == 0)
                return 1f;

            int solvedCount = 0;
            for (int i = 0; i < requiredPuzzles.Count; i++)
            {
                if (requiredPuzzles[i] != null && requiredPuzzles[i].IsSolved)
                    solvedCount++;
            }

            return (float)solvedCount / requiredPuzzles.Count;
        }

        /// <summary>
        /// Returns the world position of the next active objective in this room for vector observations
        /// and reward shaping. Once all puzzles are solved, returns the exit door position.
        /// </summary>
        public Vector3 GetCurrentObjectiveWorldPosition()
        {
            for (int i = 0; i < requiredPuzzles.Count; i++)
            {
                var puzzle = requiredPuzzles[i];
                if (puzzle != null && !puzzle.IsSolved)
                {
                    return puzzle.GetObjectiveTargetPosition();
                }
            }

            if (exitDoorTransform != null)
                return exitDoorTransform.position;

            return transform.position;
        }

        /// <summary>
        /// Computes the world position and rotation where the agent should spawn in this room.
        /// </summary>
        public void GetAgentSpawnPose(out Vector3 position, out Quaternion rotation)
        {
            if (agentSpawnPoint == null)
            {
                position = transform.position + Vector3.up * 0.5f;
                rotation = transform.rotation;
                return;
            }

            Vector3 jitter = Vector3.zero;
            if (randomizeAgentSpawn && spawnJitterRadius > 0f)
            {
                Vector2 circle = Random.insideUnitCircle * spawnJitterRadius;
                jitter = new Vector3(circle.x, 0f, circle.y);
            }

            position = agentSpawnPoint.position + jitter;
            rotation = agentSpawnPoint.rotation;
        }

        private void UpdateDoorVisuals(bool unlocked)
        {
            if (doorStatusRenderer == null)
                return;

            doorStatusRenderer.GetPropertyBlock(_propBlock);
            Color targetColor = unlocked ? unlockedColor : lockedColor;
            _propBlock.SetColor("_BaseColor", targetColor); // URP Lit shader property
            _propBlock.SetColor("_Color", targetColor);     // Built-in Standard shader fallback
            doorStatusRenderer.SetPropertyBlock(_propBlock);
        }
    }
}