using UnityEngine;

namespace EscapeRoomML
{
    /// <summary>
    /// Smoothly pans the Main Camera to follow the Hero Arena's active room and draws a clean,
    /// YouTube-ready On-Screen HUD showing Room Progress, Timer Countdown, Attempts, and Victory Banner.
    /// Attach this to your Main Camera.
    /// </summary>
    public class HeroCameraAndHUD : MonoBehaviour
    {
        [Header("Hero Arena Reference")]
        [Tooltip("Assign your designated Hero EscapeArenaManager here.")]
        public EscapeArenaManager heroArena;

        [Header("Camera Follow Settings")]
        [Tooltip("Offset from the active room's center if the room has no explicit cameraAnchor assigned.")]
        public Vector3 fallbackRoomCameraOffset = new Vector3(0f, 11f, -9.5f);

        [Tooltip("How much the camera subtly shifts toward the Hero Agent inside the active room (0 = fixed room shot, 0.35 = gentle follow).")]
        [Range(0f, 1f)]
        public float agentFollowWeight = 0.25f;

        [Tooltip("Camera position smoothing time (lower = faster pan between rooms).")]
        public float positionSmoothTime = 0.45f;

        [Tooltip("Camera rotation smoothing speed.")]
        public float rotationLerpSpeed = 6f;

        [Header("YouTube HUD Overlay Settings")]
        public bool showHUD = true;

        private Vector3 _cameraVelocity;
        private GUIStyle _panelStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _bannerStyle;
        private Texture2D _darkTex;
        private Texture2D _barBgTex;
        private Texture2D _barFillGreenTex;
        private Texture2D _barFillWarnTex;
        private Texture2D _barFillDangerTex;

        private void Start()
        {
            if (heroArena == null)
            {
                // Auto-detect Hero Arena if not manually assigned
                var arenas = FindObjectsByType<EscapeArenaManager>(FindObjectsSortMode.None);
                foreach (var a in arenas)
                {
                    if (a.isHeroArena)
                    {
                        heroArena = a;
                        break;
                    }
                }
                if (heroArena == null && arenas.Length > 0)
                    heroArena = arenas[0];
            }
        }

        private void LateUpdate()
        {
            if (heroArena == null || heroArena.CurrentRoom == null)
                return;

            EscapeRoomController activeRoom = heroArena.CurrentRoom;
            Vector3 roomCenter = activeRoom.transform.position;
            Vector3 agentPos = heroArena.agent != null ? heroArena.agent.transform.position : roomCenter;

            Vector3 targetCamPos;
            Quaternion targetCamRot;

            if (activeRoom.cameraAnchor != null)
            {
                Vector3 blendedFocus = Vector3.Lerp(roomCenter, agentPos, agentFollowWeight);
                targetCamPos = activeRoom.cameraAnchor.position + (blendedFocus - roomCenter) * 0.5f;
                targetCamRot = Quaternion.LookRotation((blendedFocus - targetCamPos).normalized, Vector3.up);
            }
            else
            {
                Vector3 blendedFocus = Vector3.Lerp(roomCenter, agentPos, agentFollowWeight);
                targetCamPos = blendedFocus + fallbackRoomCameraOffset;
                targetCamRot = Quaternion.LookRotation((blendedFocus - targetCamPos).normalized, Vector3.up);
            }

            transform.position = Vector3.SmoothDamp(
                transform.position,
                targetCamPos,
                ref _cameraVelocity,
                positionSmoothTime
            );

            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetCamRot,
                rotationLerpSpeed * Time.deltaTime
            );
        }

        private void InitStyles()
        {
            if (_panelStyle != null)
                return;

            _darkTex = MakeSolidTexture(new Color(0.08f, 0.1f, 0.14f, 0.86f));
            _barBgTex = MakeSolidTexture(new Color(0.2f, 0.22f, 0.28f, 0.95f));
            _barFillGreenTex = MakeSolidTexture(new Color(0.2f, 0.85f, 0.45f, 0.95f));
            _barFillWarnTex = MakeSolidTexture(new Color(0.95f, 0.75f, 0.2f, 0.95f));
            _barFillDangerTex = MakeSolidTexture(new Color(0.92f, 0.25f, 0.25f, 0.95f));

            _panelStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _darkTex },
                padding = new RectOffset(16, 16, 14, 14)
            };

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };

            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Normal,
                normal = { textColor = new Color(0.88f, 0.91f, 0.96f) }
            };

            _bannerStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 26,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { background = _darkTex, textColor = new Color(0.3f, 1f, 0.55f) }
            };
        }

        private void OnGUI()
        {
            if (!showHUD || heroArena == null || heroArena.CurrentRoom == null)
                return;

            InitStyles();

            EscapeRoomController room = heroArena.CurrentRoom;
            int totalRooms = heroArena.rooms != null ? heroArena.rooms.Length : 3;
            int roomNum = heroArena.CurrentRoomIndex + 1;

            // Main top-left telemetry card
            Rect cardRect = new Rect(24f, 24f, 430f, 210f);
            GUI.Box(cardRect, GUIContent.none, _panelStyle);

            GUILayout.BeginArea(new Rect(40f, 36f, 398f, 190f));
            GUILayout.Label($"ROOM {roomNum} / {totalRooms}: {room.roomTitle}", _titleStyle);
            GUILayout.Space(4f);

            string doorStatus = room.IsDoorUnlocked ? "<color=#4ADE80>UNLOCKED</color>" : "<color=#F87171>LOCKED</color>";
            float puzzlePct = room.GetPuzzleProgress() * 100f;
            _bodyStyle.richText = true;

            GUILayout.Label($"<b>Room Attempt:</b> #{heroArena.CurrentRoomAttempt}   |   <b>Total Runs:</b> #{heroArena.TotalAttemptsAllRooms}", _bodyStyle);
            GUILayout.Label($"<b>Puzzles Solved:</b> {puzzlePct:0}%   |   <b>Exit Door:</b> {doorStatus}", _bodyStyle);

            float reward = heroArena.agent != null ? heroArena.agent.CumulativeEpisodeReward : 0f;
            GUILayout.Label($"<b>Episode Reward:</b> {reward:+0.00;-0.00;0.00}   |   <b>Elapsed:</b> {FormatTime(heroArena.TotalElapsedTime)}", _bodyStyle);
            GUILayout.Label($"<b>Status:</b> {heroArena.LastEventMessage}", _bodyStyle);
            GUILayout.EndArea();

            // Timer Progress Bar inside bottom of card
            float timeRatio = heroArena.CurrentRoomTimeLimit > 0f
                ? Mathf.Clamp01(heroArena.RemainingRoomTime / heroArena.CurrentRoomTimeLimit)
                : 0f;

            Rect barBgRect = new Rect(40f, 200f, 398f, 20f);
            GUI.DrawTexture(barBgRect, _barBgTex);

            Texture2D fillTex = timeRatio > 0.5f
                ? _barFillGreenTex
                : (timeRatio > 0.25f ? _barFillWarnTex : _barFillDangerTex);

            Rect barFillRect = new Rect(40f, 200f, 398f * timeRatio, 20f);
            GUI.DrawTexture(barFillRect, fillTex);

            GUIStyle timerTextStyle = new GUIStyle(_bodyStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = 13
            };
            GUI.Label(barBgRect, $"ROOM TIMER: {heroArena.RemainingRoomTime:0.0}s / {heroArena.CurrentRoomTimeLimit:0.0}s", timerTextStyle);

            // Victory Banner when Hero Agent finishes the final room
            if (heroArena.HasHeroCompletedAllRooms)
            {
                Rect victoryRect = new Rect(Screen.width * 0.5f - 320f, Screen.height * 0.5f - 60f, 640f, 120f);
                GUI.Box(
                    victoryRect,
                    $"ESCAPED ALL {totalRooms} ROOMS!\nTotal Time: {FormatTime(heroArena.TotalElapsedTime)}  |  Total Attempts: {heroArena.TotalAttemptsAllRooms}",
                    _bannerStyle
                );
            }
        }

        private static string FormatTime(float totalSeconds)
        {
            int mins = Mathf.FloorToInt(totalSeconds / 60f);
            int secs = Mathf.FloorToInt(totalSeconds % 60f);
            return $"{mins:00}:{secs:00}";
        }

        private static Texture2D MakeSolidTexture(Color color)
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }
    }
}