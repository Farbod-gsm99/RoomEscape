using UnityEngine;

namespace EscapeRoomML
{
    /// <summary>
    /// Coordinates the multi-room escape sequence for a single Arena instance (Hero or Background Parallel Arena).
    /// Manages per-room timers, checkpoint progression, door locking, and the final escape state.
    /// </summary>
    public class EscapeArenaManager : MonoBehaviour
    {
        [Header("Arena Configuration")]
        [Tooltip("Mark TRUE on the single Arena that the Main Camera & HUD follow for your YouTube recording.")]
        public bool isHeroArena = false;

        [Tooltip("Reference to the EscapeAgent inside this arena.")]
        public EscapeAgent agent;

        [Tooltip("Ordered array of rooms (Room 1, Room 2, Room 3).")]
        public EscapeRoomController[] rooms;

        [Header("Progression Rules")]
        [Tooltip("How many times the agent must clear a room before advancing to the next room (1 = immediate checkpoint advance).")]
        public int requiredSolvesToAdvance = 1;

        [Header("Rewards & Penalties")]
        [Tooltip("Reward granted when the agent passes through a room's exit door in time.")]
        public float roomEscapeReward = 2.0f;

        [Tooltip("Bonus reward granted when escaping the final room.")]
        public float finalRoomBonusReward = 1.5f;

        [Tooltip("Penalty applied when the room timer expires or the agent hits a hazard/pit.")]
        public float failurePenalty = -1.0f;

        // Live state exposed for the HUD and Camera
        public int CurrentRoomIndex { get; private set; } = 0;
        public int CurrentRoomAttempt { get; private set; } = 1;
        public int TotalAttemptsAllRooms { get; private set; } = 1;
        public float RemainingRoomTime { get; private set; }
        public float CurrentRoomTimeLimit { get; private set; } = 30f;
        public float TotalElapsedTime { get; private set; } = 0f;
        public bool HasHeroCompletedAllRooms { get; private set; } = false;
        public string LastEventMessage { get; private set; } = "Training started...";

        private int _currentRoomSolves = 0;

        public EscapeRoomController CurrentRoom =>
            (rooms != null && CurrentRoomIndex >= 0 && CurrentRoomIndex < rooms.Length)
                ? rooms[CurrentRoomIndex]
                : null;

        private void Awake()
        {
            if (agent == null)
                agent = GetComponentInChildren<EscapeAgent>();

            CurrentRoomIndex = 0;
        }

        private void Update()
        {
            if (HasHeroCompletedAllRooms)
                return;

            TotalElapsedTime += Time.deltaTime;

            // Tick down the active room timer
            RemainingRoomTime -= Time.deltaTime;
            if (RemainingRoomTime <= 0f)
            {
                RemainingRoomTime = 0f;
                OnAgentFailed("Time Expired!");
            }
        }

        /// <summary>
        /// Called by EscapeAgent.OnEpisodeBegin() at the start of every ML-Agents episode.
        /// Respawns the agent at the start of CurrentRoomIndex and resets that room's puzzles.
        /// </summary>
        public void BeginRoomEpisode()
        {
            if (HasHeroCompletedAllRooms)
                return;

            EscapeRoomController activeRoom = CurrentRoom;
            if (activeRoom == null)
            {
                Debug.LogError($"[EscapeArenaManager] No room assigned at index {CurrentRoomIndex} on {name}!");
                return;
            }

            // Ensure all previous rooms have their exit doors locked shut behind the agent
            for (int i = 0; i < CurrentRoomIndex; i++)
            {
                if (rooms[i] != null)
                    rooms[i].CloseAndLockDoorBehindAgent();
            }

            // Reset the current room's puzzles, keys, blocks, and doors
            activeRoom.ResetRoom();

            // Reset the room countdown timer
            CurrentRoomTimeLimit = Mathf.Max(5f, activeRoom.roomTimeLimit);
            RemainingRoomTime = CurrentRoomTimeLimit;

            // Teleport the agent to the current room's checkpoint spawn point
            activeRoom.GetAgentSpawnPose(out Vector3 spawnPos, out Quaternion spawnRot);
            agent.TeleportToPose(spawnPos, spawnRot);
        }

        /// <summary>
        /// Called when the agent crosses the ExitZoneTrigger of the current room.
        /// </summary>
        public void OnAgentClearedCurrentRoom()
        {
            if (HasHeroCompletedAllRooms)
                return;

            EscapeRoomController clearedRoom = CurrentRoom;
            if (clearedRoom == null || !clearedRoom.IsDoorUnlocked)
                return;

            agent.AddReward(roomEscapeReward);
            _currentRoomSolves++;

            bool isLastRoom = (CurrentRoomIndex >= rooms.Length - 1);

            if (!isLastRoom)
            {
                if (_currentRoomSolves >= requiredSolvesToAdvance)
                {
                    // Close the door behind the agent and advance checkpoint to the next room
                    clearedRoom.CloseAndLockDoorBehindAgent();
                    CurrentRoomIndex++;
                    _currentRoomSolves = 0;
                    CurrentRoomAttempt = 1;
                    LastEventMessage = $"Escaped Room {CurrentRoomIndex}! Advancing to Room {CurrentRoomIndex + 1}...";
                }
                else
                {
                    LastEventMessage = $"Cleared Room {CurrentRoomIndex + 1} ({_currentRoomSolves}/{requiredSolvesToAdvance})";
                }

                agent.EndEpisode();
            }
            else
            {
                // Agent just escaped the FINAL room!
                agent.AddReward(finalRoomBonusReward);

                if (isHeroArena)
                {
                    // Stop the Hero Arena here for the YouTube finale!
                    HasHeroCompletedAllRooms = true;
                    LastEventMessage = "VICTORY! Hero Agent escaped all rooms!";
                    agent.EndEpisode();
                    agent.FreezeAgentOnVictory();
                }
                else
                {
                    // Background parallel arenas stay in the final room and keep resetting it
                    // so they continue feeding final-room training experiences to the shared brain
                    // until the Hero Arena also escapes!
                    CurrentRoomAttempt++;
                    TotalAttemptsAllRooms++;
                    LastEventMessage = "Background Arena cleared final room (repeating final room for training).";
                    agent.EndEpisode();
                }
            }
        }

        /// <summary>
        /// Called when the room timer runs out or the agent touches a hazard/pit.
        /// Respawns the agent at the start of the CURRENT room (never back to Room 1).
        /// </summary>
        public void OnAgentFailed(string reason)
        {
            if (HasHeroCompletedAllRooms)
                return;

            agent.AddReward(failurePenalty);
            CurrentRoomAttempt++;
            TotalAttemptsAllRooms++;
            LastEventMessage = $"Room {CurrentRoomIndex + 1} Failed: {reason} (Attempt #{CurrentRoomAttempt})";

            agent.EndEpisode();
        }
    }
}