using UnityEngine;

namespace EscapeRoomML
{
    /// <summary>
    /// Smoothly moves the Main Camera to match the Hero Arena's active room CameraAnchor (position & rotation)
    /// and draws the YouTube On-Screen HUD.
    /// Works whether attached directly to Main Camera OR to EscapeArena / any manager object in the scene.
    /// </summary>
    public class HeroCameraAndHUD : MonoBehaviour
    {
        [Header("Hero Arena & Camera References")]
        [Tooltip("Assign your designated Hero EscapeArenaManager here (auto-detected if left empty).")]
        public EscapeArenaManager heroArena;

        [Tooltip("Camera to move (auto-detects Main Camera if left empty).")]
        public Camera targetCamera;

        [Header("Camera Framing & Follow Settings")]
        [Tooltip("If TRUE, uses the exact Position and Rotation of each room's CameraAnchor without overriding your angle.")]
        public bool useExactCameraAnchorPose = true;

        [Tooltip("Only used when useExactCameraAnchorPose is FALSE: how much the camera shifts/tilts toward the agent.")]
        [Range(0f, 1f)]
        public float agentFollowWeight = 0.0f;

        [Tooltip("Fallback offset from the active room's center ONLY if the room has no CameraAnchor assigned.")]
        public Vector3 fallbackRoomCameraOffset = new Vector3(0f, 11f, -9.5f);

        [Tooltip("Fallback pitch angle (in degrees) ONLY if the room has no CameraAnchor assigned.")]
        public Vector3 fallbackCameraEulerAngles = new Vector3(48f, 0f, 0f);

        [Tooltip("Camera position smoothing time when transitioning between rooms (0 = instant snap).")]
        public float positionSmoothTime = 0.35f;

        [Tooltip("Camera rotation smoothing speed when transitioning between rooms.")]
        public float rotationLerpSpeed = 8f;

        [Header("YouTube HUD Overlay Settings")]
        public bool showHUD = true;

        private Transform _camTransform;
        private Vector3 _cameraVelocity;
        private bool _hasSnappedInitialPose = false;

        private GUIStyle _panelStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _bannerStyle;
        private Texture2D _darkTex;
        private Texture2D _barBgTex;
        private Texture2D _barFillGreenTex;
        private Texture2D _barFillWarnTex;
        private Texture2D _barFillDangerTex;

        private void Awake()
        {
            ResolveCameraAndHeroArena();
        }

        private void Start()
        {
            ResolveCameraAndHeroArena();
            SnapToActiveRoomImmediate();
        }

        private void ResolveCameraAndHeroArena()
        {
            // 1. Find the Camera to control (works even if this script is attached to EscapeArena instead of Main Camera!)
            if (targetCamera == null)
            {
                if (!TryGetComponent<Camera>(out targetCamera))
                    targetCamera = Camera.main;

                if (targetCamera == null)
                    targetCamera = FindFirstObjectByType<Camera>();
            }

            if (targetCamera != null)
                _camTransform = targetCamera.transform;
            else
                _camTransform = transform;

            // 2. Find the Hero Arena (preferring the one with isHeroArena == true)
            if (heroArena == null || !heroArena.isHeroArena)
            {
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
            if (heroArena == null || _camTransform == null)
            {
                ResolveCameraAndHeroArena();
                if (heroArena == null || _camTransform == null)
                    return;
            }

            EscapeRoomController activeRoom = heroArena.CurrentRoom;
            if (activeRoom == null)
                return;

            GetDesiredCameraPose(activeRoom, out Vector3 targetPos, out Quaternion targetRot);

            if (!_hasSnappedInitialPose)
            {
                _camTransform.SetPositionAndRotation(targetPos, targetRot);
                _cameraVelocity = Vector3.zero;
                _hasSnappedInitialPose = true;
                return;
            }

            if (positionSmoothTime <= 0.001f)
            {
                _camTransform.position = targetPos;
            }
            else
            {
                _camTransform.position = Vector3.SmoothDamp(
                    _camTransform.position,
                    targetPos,
                    ref _cameraVelocity,
                    positionSmoothTime
                );
            }

            _camTransform.rotation = Quaternion.Slerp(
                _camTransform.rotation,
                targetRot,
                rotationLerpSpeed * Time.deltaTime
            );
        }

        private void SnapToActiveRoomImmediate()
        {
            if (heroArena != null && heroArena.CurrentRoom != null && _camTransform != null)
            {
                GetDesiredCameraPose(heroArena.CurrentRoom, out Vector3 targetPos, out Quaternion targetRot);
                _camTransform.SetPositionAndRotation(targetPos, targetRot);
                _cameraVelocity = Vector3.zero;
                _hasSnappedInitialPose = true;
            }
        }

        private void GetDesiredCameraPose(EscapeRoomController activeRoom, out Vector3 targetCamPos, out Quaternion targetCamRot)
        {
            Vector3 roomCenter = activeRoom.transform.position;
            Vector3 agentPos = heroArena.agent != null ? heroArena.agent.transform.position : roomCenter;

            if (activeRoom.cameraAnchor != null)
            {
                if (useExactCameraAnchorPose || agentFollowWeight <= 0.001f)
                {
                    // Honor the EXACT position and rotation set on the room's CameraAnchor!
                    targetCamPos = activeRoom.cameraAnchor.position;
                    targetCamRot = activeRoom.cameraAnchor.rotation;
                }
                else
                {
                    Vector3 blendedFocus = Vector3.Lerp(roomCenter, agentPos, agentFollowWeight);
                    targetCamPos = activeRoom.cameraAnchor.position + (blendedFocus - roomCenter) * 0.35f;
                    targetCamRot = Quaternion.Slerp(
                        activeRoom.cameraAnchor.rotation,
                        Quaternion.LookRotation((blendedFocus - targetCamPos).normalized, Vector3.up),
                        agentFollowWeight
                    );
                }
            }
            else
            {
                Vector3 blendedFocus = Vector3.Lerp(roomCenter, agentPos, agentFollowWeight);
                targetCamPos = blendedFocus + fallbackRoomCameraOffset;
                targetCamRot = Quaternion.Euler(fallbackCameraEulerAngles);
            }
        }

        private void InitStyles()
        {
            if (_panelStyle != null && _darkTex != null)
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
                richText = true,
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
            if (!showHUD)
                return;

            InitStyles();

            if (heroArena == null)
            {
                GUI.Box(new Rect(24f, 24f, 520f, 50f), "HeroCameraAndHUD: No EscapeArenaManager found in Scene!", _panelStyle);
                return;
            }

            EscapeRoomController room = heroArena.CurrentRoom;
            if (room == null)
            {
                GUI.Box(new Rect(24f, 24f, 560f, 50f), $"HeroCameraAndHUD: '{heroArena.name}' has no Rooms assigned in EscapeArenaManager!", _panelStyle);
                return;
            }

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