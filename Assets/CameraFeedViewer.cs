using System;
using System.Collections.Generic;
using Meta.XR;
using TMPro;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Rendering;

public class CameraFeedViewer : MonoBehaviour
{
    public enum MarkerOverlayMode
    {
        WireframePlane,
        SolidPlane,
        AxesGizmo
    }

    struct MarkerDetection
    {
        public int Id;
        public Vector2[] Corners;
    }

    struct MarkerPoseDetection
    {
        public int Id;
        public Vector3 RotationVector;
        public Vector3 TranslationVector;
    }

    class MarkerVisual
    {
        public int MarkerId;
        public GameObject Root;
        public LineRenderer Outline;
        public MeshFilter FillMeshFilter;
        public MeshRenderer FillRenderer;
        public LineRenderer AxisX;
        public LineRenderer AxisY;
        public LineRenderer AxisZ;
        public bool HasSmoothedPose;
        public Vector3 SmoothedPosition;
        public Quaternion SmoothedRotation;
        public float LastVisibleTime;

        public void SetActive(bool active)
        {
            if (Root != null && Root.activeSelf != active)
            {
                Root.SetActive(active);
            }

            if (!active)
            {
                HasSmoothedPose = false;
            }
        }
    }

    class TrackedMarkerPose
    {
        public bool HasPose;
        public bool IsTracked;
        public float LastSeenTime;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
        public int ConsecutiveOutlierFrames;
    }

    public PassthroughCameraAccess cameraAccess;
    public Renderer targetRenderer;
    public TextMeshPro debugText;
    public MarkerOverlayMode overlayMode = MarkerOverlayMode.WireframePlane;
    [Min(0.0001f)] public float overlaySurfaceOffset = 0.002f;
    [Min(0.0001f)] public float lineWidth = 0.003f;
    [Range(0.05f, 1f)] public float solidPlaneOpacity = 0.3f;
    [Range(0.1f, 1.5f)] public float gizmoScale = 0.5f;
    [Min(0.001f)] public float markerSizeMeters = 0.05f;
    [Range(0.9f, 1.25f)] public float worldMarkerSizeMultiplier = 1f;
    [Min(0.001f)] public float axisLineWidth = 0.006f;
    [Min(0.005f)] public float minimumAxisLengthMeters = 0.03f;
    [Range(0f, 30f)] public float worldPoseSmoothing = 18f;
    [Min(0f)] public float worldPoseSnapDistanceMeters = 0.08f;
    [Range(0f, 180f)] public float worldPoseSnapRotationDegrees = 25f;
    [Range(1, 4)] public int processingDownscale = 2;
    [Range(1, 4)] public int lostMarkerProcessingDownscale = 1;
    [Range(1, 4)] public int processEveryNthFrame = 1;
    [Range(1f, 60f)] public float maxTrackingUpdatesPerSecond = 15f;
    public int tableCenterMarkerId = 0;
    [Range(0f, 30f)] public float tablePoseSmoothing = 24f;
    [Min(0f)] public float tablePoseHoldSeconds = 0.2f;
    [Min(0f)] public float tableMaxPositionJumpMeters = 0.05f;
    [Range(0f, 180f)] public float tableMaxRotationJumpDegrees = 20f;
    [Range(1, 5)] public int tableOutlierFramesBeforeSnap = 2;
    public bool drawDebugOverlay = true;
    public bool estimateMarkerPoses = true;
    public bool drawWorldMarkers = false;
    public bool updateCameraFeedTexture = false;
    public bool flipDisplayHorizontally = true;
    public bool flipDisplayVertically = true;
    public Transform worldOverlayParent;
    public Transform tableOriginTransform;
    public XRHandJointVisualizer recorderStatus;
    public bool debugTextFollowsView = true;
    [Min(0.1f)] public float debugTextDistance = 0.75f;
    public Vector2 debugTextViewOffset = new Vector2(0f, -0.12f);

    Texture2D debugTexture;
    AndroidJavaClass arucoClass;
    bool isProcessing;
    float lastTrackingRequestTime = float.NegativeInfinity;
    byte[] grayBytes;
    Color32[] debugPixels;
    readonly List<MarkerDetection> markerDetections = new List<MarkerDetection>();
    readonly List<MarkerPoseDetection> markerPoseDetections = new List<MarkerPoseDetection>();
    readonly Dictionary<int, MarkerVisual> markerVisuals = new Dictionary<int, MarkerVisual>();
    readonly List<int> visibleMarkerIds = new List<int>();
    readonly Dictionary<int, MarkerVisual> worldMarkerVisuals = new Dictionary<int, MarkerVisual>();
    readonly List<int> visibleWorldMarkerIds = new List<int>();
    readonly TrackedMarkerPose tableOriginPose = new TrackedMarkerPose();
    Pose lastProcessedCameraPose;
    bool hasLastProcessedCameraPose;

    Transform overlayRoot;
    Transform worldOverlayRoot;
    Material lineMaterial;
    Material fillMaterial;
    Material axisXMaterial; // Add this
    Material axisYMaterial; // Add this
    Material axisZMaterial; // Add this
    static readonly Color32 MarkerOutlineColor = new Color32(0, 255, 120, 255);
    static readonly Color AxisXColor = new Color(1f, 0.25f, 0.25f, 1f);
    static readonly Color AxisYColor = new Color(0.25f, 1f, 0.25f, 1f);
    static readonly Color AxisZColor = new Color(0.25f, 0.55f, 1f, 1f);

    public bool TryGetTableOriginPose(out Pose pose)
    {
        pose = default;
        if (!tableOriginPose.HasPose)
        {
            return false;
        }

        pose = new Pose(tableOriginPose.Position, tableOriginPose.Rotation);
        return true;
    }

    public bool IsTableCurrentlyTracked => tableOriginPose.IsTracked;

    public int GetLatestMarkerWorldPoses(int markerId, List<Pose> results)
    {
        if (results == null)
        {
            return 0;
        }

        results.Clear();

        if (!hasLastProcessedCameraPose)
        {
            return 0;
        }

        for (int i = 0; i < markerPoseDetections.Count; i++)
        {
            MarkerPoseDetection detection = markerPoseDetections[i];
            if (detection.Id != markerId)
            {
                continue;
            }

            if (TryConvertMarkerPoseToWorldPose(detection, lastProcessedCameraPose, out Vector3 worldPosition, out Quaternion worldRotation))
            {
                results.Add(new Pose(worldPosition, worldRotation));
            }
        }

        return results.Count;
    }

    void Start()
    {
        // These are serialized fields, so scene instances keep old values until explicitly changed.
        // Force the feed orientation we want at runtime instead of relying on code defaults.
        flipDisplayHorizontally = true;
        flipDisplayVertically = true;

        if (!Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
        {
            Permission.RequestUserPermission("horizonos.permission.HEADSET_CAMERA");
        }

        arucoClass = new AndroidJavaClass("com.GeorgiaTech.aruco.ArucoBridge");
        EnsureOverlayInfrastructure();
        EnsureWorldOverlayInfrastructure();
        ApplyTextureOrientation();
    }

    void OnDestroy()
    {
        if (debugTexture != null)
        {
            Destroy(debugTexture);
        }

        if (lineMaterial != null)
        {
            Destroy(lineMaterial);
        }

        if (fillMaterial != null)
        {
            Destroy(fillMaterial);
        }

        DestroyMarkerVisualSet(markerVisuals);
        DestroyMarkerVisualSet(worldMarkerVisuals);
    }

    int DetectMarkerCount(byte[] image, int width, int height)
    {
        if (arucoClass == null)
        {
            return -1;
        }

        return arucoClass.CallStatic<int>("detectMarkers", image, width, height);
    }

    float[] DetectMarkersDetailed(byte[] image, int width, int height)
    {
        if (arucoClass == null)
        {
            return Array.Empty<float>();
        }

        return arucoClass.CallStatic<float[]>("detectMarkersDetailed", image, width, height);
    }

    float[] DetectMarkerPoses(byte[] image, int width, int height, float fx, float fy, float cx, float cy)
    {
        if (arucoClass == null)
        {
            return Array.Empty<float>();
        }

        return arucoClass.CallStatic<float[]>("estimateMarkerPoses", image, width, height, fx, fy, cx, cy, markerSizeMeters);
    }

    void Update()
    {
        if (isProcessing || cameraAccess == null)
        {
            return;
        }

        if (processEveryNthFrame > 1 && (Time.frameCount % processEveryNthFrame) != 0)
        {
            return;
        }

        float minTrackingInterval = 1f / Mathf.Max(1f, maxTrackingUpdatesPerSecond);
        if ((Time.unscaledTime - lastTrackingRequestTime) < minTrackingInterval)
        {
            return;
        }

        var tex = cameraAccess.GetTexture();
        if (tex == null)
        {
            return;
        }

        isProcessing = true;
        lastTrackingRequestTime = Time.unscaledTime;

        int downscale = GetEffectiveProcessingDownscale();
        int width = tex.width / downscale;
        int height = tex.height / downscale;

        RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(tex, rt);

        Pose cameraPose = cameraAccess.IsPlaying ? cameraAccess.GetCameraPose() : default;
        Vector2Int currentResolution = cameraAccess.CurrentResolution;
        PassthroughCameraAccess.CameraIntrinsics cameraIntrinsics = cameraAccess.Intrinsics;

        AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, (request) =>
        {
            OnReadbackComplete(request, width, height, cameraPose, currentResolution, cameraIntrinsics);
            RenderTexture.ReleaseTemporary(rt);
        });
    }

    void LateUpdate()
    {
        UpdateDebugTextTransform();
    }

    void OnReadbackComplete(AsyncGPUReadbackRequest request, int width, int height, Pose cameraPose, Vector2Int currentResolution, PassthroughCameraAccess.CameraIntrinsics cameraIntrinsics)
    {
        if (request.hasError)
        {
            Debug.LogError("GPU Readback Error");
            isProcessing = false;
            return;
        }

        lastProcessedCameraPose = cameraPose;
        hasLastProcessedCameraPose = true;

        var rawData = request.GetData<byte>();

        bool needsDebugTexture = updateCameraFeedTexture && targetRenderer != null;

        if (grayBytes == null || grayBytes.Length != width * height)
        {
            grayBytes = new byte[width * height];

            if (needsDebugTexture)
            {
                debugPixels = new Color32[width * height];
            }
            else
            {
                debugPixels = null;
            }

            if (debugTexture != null)
            {
                Destroy(debugTexture);
                debugTexture = null;
            }

            if (needsDebugTexture)
            {
                debugTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                BindDebugTexture();
            }
        }
        else if (needsDebugTexture && (debugPixels == null || debugPixels.Length != width * height))
        {
            debugPixels = new Color32[width * height];
        }

        for (int y = 0; y < height; y++)
        {
            // Keep OpenCV input in its original top-down layout.
            int detectionY = (height - 1) - y;

            // Display orientation is handled independently so feed fixes don't break detection.
            int displayY = flipDisplayVertically ? (height - 1) - y : y;

            for (int x = 0; x < width; x++)
            {
                int displayX = flipDisplayHorizontally ? (width - 1) - x : x;

                int rawIndex = (y * width + x) * 4;
                int detectionIndex = (detectionY * width) + x;
                int displayIndex = (displayY * width) + displayX;

                byte grayValue = (byte)((rawData[rawIndex] + rawData[rawIndex + 1] + rawData[rawIndex + 2]) / 3);

                grayBytes[detectionIndex] = grayValue;
                if (debugPixels != null)
                {
                    debugPixels[displayIndex] = new Color32(grayValue, grayValue, grayValue, 255);
                }
            }
        }

        bool needsDetailedMarkerData = drawDebugOverlay && needsDebugTexture;
        if (needsDetailedMarkerData)
        {
            float[] markerData = DetectMarkersDetailed(grayBytes, width, height);
            ParseMarkerData(markerData, markerDetections);
        }
        else
        {
            markerDetections.Clear();
        }

        if (needsDetailedMarkerData && markerDetections.Count > 0 && debugPixels != null)
        {
            DrawMarkerOverlayIntoTexture(width, height);
        }

        if (estimateMarkerPoses && TryGetProcessingIntrinsics(width, height, currentResolution, cameraIntrinsics, out Vector2 focalLength, out Vector2 principalPoint))
        {
            float[] markerPoseData = DetectMarkerPoses(grayBytes, width, height, focalLength.x, focalLength.y, principalPoint.x, principalPoint.y);
            ParseMarkerPoseData(markerPoseData, markerPoseDetections);
        }
        else
        {
            markerPoseDetections.Clear();
        }

        if (debugTexture != null && debugPixels != null)
        {
            BindDebugTexture();
            debugTexture.SetPixels32(debugPixels);
            debugTexture.Apply();
        }

        if (needsDebugTexture)
        {
            UpdateMarkerVisuals(width, height);
        }
        else
        {
            HideInactiveVisuals(markerVisuals);
        }

        UpdateTrackedTableOrigin(cameraPose);
        if (drawWorldMarkers)
        {
            UpdateWorldMarkerVisuals(cameraPose);
        }
        else
        {
            HideInactiveVisuals(worldMarkerVisuals);
        }
        int markerCount = Mathf.Max(markerDetections.Count, markerPoseDetections.Count);
        UpdateDebugText(markerCount, width, height);
        isProcessing = false;
    }

    void UpdateDebugText(int markerCount, int width, int height)
    {
        if (debugText == null)
        {
            return;
        }

        string tableFound = tableOriginPose.IsTracked ? "Yes" : "No";
        string tablePosition = tableOriginPose.IsTracked
            ? $"{tableOriginPose.Position.x:F3}, {tableOriginPose.Position.y:F3}, {tableOriginPose.Position.z:F3}"
            : "--";
        if (recorderStatus == null)
        {
            recorderStatus = FindFirstObjectByType<XRHandJointVisualizer>();
        }

        string lastSavePath = recorderStatus != null && !string.IsNullOrWhiteSpace(recorderStatus.LastSavedFilePath)
            ? recorderStatus.LastSavedFilePath
            : "--";

        debugText.text =
            $"Markers detected: {Mathf.Max(0, markerCount)}\n" +
            $"Table found? {tableFound}\n" +
            $"Table position: {tablePosition}\n" +
            $"Last save: {lastSavePath}";
    }

    void UpdateDebugTextTransform()
    {
        if (!debugTextFollowsView || debugText == null)
        {
            return;
        }

        Transform followTransform = Camera.main != null ? Camera.main.transform : null;
        if (followTransform == null)
        {
            return;
        }

        Vector3 targetPosition =
            followTransform.position +
            followTransform.forward * Mathf.Max(0.1f, debugTextDistance) +
            followTransform.right * debugTextViewOffset.x +
            followTransform.up * debugTextViewOffset.y;

        debugText.transform.position = targetPosition;

        Vector3 towardCamera = followTransform.position - targetPosition;
        if (towardCamera.sqrMagnitude > 1e-6f)
        {
            debugText.transform.rotation = Quaternion.LookRotation(towardCamera.normalized, followTransform.up) * Quaternion.Euler(0f, 180f, 0f);
        }
    }

    void ParseMarkerData(float[] markerData, List<MarkerDetection> results)
    {
        results.Clear();

        if (markerData == null || markerData.Length == 0)
        {
            return;
        }

        int markerCount = Mathf.RoundToInt(markerData[0]);
        if (markerCount < 0)
        {
            return;
        }

        if (markerCount <= 0)
        {
            return;
        }

        int expectedLength = 1 + (markerCount * 9);
        if (markerData.Length < expectedLength)
        {
            Debug.LogWarning($"Aruco marker payload was shorter than expected. Expected {expectedLength}, got {markerData.Length}.");
            return;
        }

        for (int markerIndex = 0; markerIndex < markerCount; markerIndex++)
        {
            int baseIndex = 1 + (markerIndex * 9);
            MarkerDetection detection = new MarkerDetection
            {
                Id = Mathf.RoundToInt(markerData[baseIndex]),
                Corners = new Vector2[4]
            };

            for (int cornerIndex = 0; cornerIndex < 4; cornerIndex++)
            {
                int cornerBaseIndex = baseIndex + 1 + (cornerIndex * 2);
                detection.Corners[cornerIndex] = new Vector2(markerData[cornerBaseIndex], markerData[cornerBaseIndex + 1]);
            }

            results.Add(detection);
        }
    }

    void ParseMarkerPoseData(float[] markerPoseData, List<MarkerPoseDetection> results)
    {
        results.Clear();

        if (markerPoseData == null || markerPoseData.Length == 0)
        {
            return;
        }

        int markerCount = Mathf.RoundToInt(markerPoseData[0]);
        if (markerCount <= 0)
        {
            return;
        }

        int expectedLength = 1 + (markerCount * 7);
        if (markerPoseData.Length < expectedLength)
        {
            Debug.LogWarning($"Aruco pose payload was shorter than expected. Expected {expectedLength}, got {markerPoseData.Length}.");
            return;
        }

        for (int markerIndex = 0; markerIndex < markerCount; markerIndex++)
        {
            int baseIndex = 1 + (markerIndex * 7);
            results.Add(new MarkerPoseDetection
            {
                Id = Mathf.RoundToInt(markerPoseData[baseIndex]),
                RotationVector = new Vector3(
                    markerPoseData[baseIndex + 1],
                    markerPoseData[baseIndex + 2],
                    markerPoseData[baseIndex + 3]),
                TranslationVector = new Vector3(
                    markerPoseData[baseIndex + 4],
                    markerPoseData[baseIndex + 5],
                    markerPoseData[baseIndex + 6])
            });
        }
    }

    void DrawMarkerOverlayIntoTexture(int width, int height)
    {
        for (int markerIndex = 0; markerIndex < markerDetections.Count; markerIndex++)
        {
            Vector2[] corners = markerDetections[markerIndex].Corners;
            if (corners == null || corners.Length < 4)
            {
                continue;
            }

            for (int cornerIndex = 0; cornerIndex < corners.Length; cornerIndex++)
            {
                Vector2 start = corners[cornerIndex];
                Vector2 end = corners[(cornerIndex + 1) % corners.Length];
                DrawLineOnDebugTexture(start, end, width, height, MarkerOutlineColor);
            }
        }
    }

    void DrawLineOnDebugTexture(Vector2 start, Vector2 end, int width, int height, Color32 color)
    {
        int x0 = Mathf.RoundToInt(start.x);
        int y0 = Mathf.RoundToInt(start.y);
        int x1 = Mathf.RoundToInt(end.x);
        int y1 = Mathf.RoundToInt(end.y);

        int dx = Mathf.Abs(x1 - x0);
        int dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            SetDebugPixel(x0, y0, width, height, color);

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int e2 = err * 2;
            if (e2 > -dy)
            {
                err -= dy;
                x0 += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    void SetDebugPixel(int x, int y, int width, int height, Color32 color)
    {
        if (x < 0 || x >= width || y < 0 || y >= height)
        {
            return;
        }

        int pixelX = flipDisplayHorizontally ? (width - 1) - x : x;
        int pixelY = flipDisplayVertically ? y : (height - 1) - y;
        int index = (pixelY * width) + pixelX;
        if (index >= 0 && index < debugPixels.Length)
        {
            debugPixels[index] = color;
        }
    }

    void ApplyTextureOrientation()
    {
        // Does Nothing
    }

    void BindDebugTexture()
    {
        if (targetRenderer == null || debugTexture == null)
        {
            return;
        }

        Material material = targetRenderer.material;
        material.mainTexture = debugTexture;

        if (material.HasProperty("_BaseMap"))
        {
            material.SetTexture("_BaseMap", debugTexture);
        }

        if (material.HasProperty("_MainTex"))
        {
            material.SetTexture("_MainTex", debugTexture);
        }

        ApplyTextureOrientation();
    }

    void EnsureOverlayInfrastructure()
    {
        if (targetRenderer == null)
        {
            return;
        }

        if (overlayRoot == null)
        {
            GameObject rootObject = new GameObject("ArucoMarkerOverlays");
            overlayRoot = rootObject.transform;
            overlayRoot.SetParent(targetRenderer.transform, false);
        }

        if (lineMaterial == null)
        {
            lineMaterial = CreateMaterial(new Color(0.1f, 1f, 0.5f, 1f));
        }

        if (fillMaterial == null)
        {
            Color fillColor = new Color(0.1f, 1f, 0.5f, solidPlaneOpacity);
            fillMaterial = CreateMaterial(fillColor);
        }
        if (axisXMaterial == null) axisXMaterial = CreateMaterial(AxisXColor);
        if (axisYMaterial == null) axisYMaterial = CreateMaterial(AxisYColor);
        if (axisZMaterial == null) axisZMaterial = CreateMaterial(AxisZColor);
    }

    void EnsureWorldOverlayInfrastructure()
    {
        Transform desiredParent = worldOverlayParent;
        if (worldOverlayRoot == null)
        {
            GameObject rootObject = new GameObject("ArucoWorldMarkerOverlays");
            worldOverlayRoot = rootObject.transform;
            worldOverlayRoot.SetParent(desiredParent, false);
            worldOverlayRoot.localPosition = Vector3.zero;
            worldOverlayRoot.localRotation = Quaternion.identity;
            worldOverlayRoot.localScale = Vector3.one;
        }
        else if (worldOverlayRoot.parent != desiredParent)
        {
            worldOverlayRoot.SetParent(desiredParent, true);
        }

        if (lineMaterial == null)
        {
            lineMaterial = CreateMaterial(new Color(0.1f, 1f, 0.5f, 1f));
        }

        if (fillMaterial == null)
        {
            Color fillColor = new Color(0.1f, 1f, 0.5f, solidPlaneOpacity);
            fillMaterial = CreateMaterial(fillColor);
        }
        if (axisXMaterial == null) axisXMaterial = CreateMaterial(AxisXColor);
        if (axisYMaterial == null) axisYMaterial = CreateMaterial(AxisYColor);
        if (axisZMaterial == null) axisZMaterial = CreateMaterial(AxisZColor);
    }

    void DestroyMarkerVisualSet(Dictionary<int, MarkerVisual> visuals)
    {
        foreach (var visual in visuals.Values)
        {
            if (visual.Root != null)
            {
                Destroy(visual.Root);
            }
        }

        visuals.Clear();
    }

    void HideInactiveVisuals(Dictionary<int, MarkerVisual> visuals)
    {
        foreach (var visual in visuals.Values)
        {
            visual.SetActive(false);
        }
    }

    Material CreateMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }

        Material material = new Material(shader);
        material.color = color;

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }

        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
        }

        if (material.HasProperty("_Blend"))
        {
            material.SetFloat("_Blend", 0f);
        }

        if (material.HasProperty("_SrcBlend"))
        {
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        }

        if (material.HasProperty("_DstBlend"))
        {
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetFloat("_ZWrite", 0f);
        }

        if (material.HasProperty("_Cull"))
        {
            material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        }

        material.renderQueue = 3000;
        return material;
    }

    void UpdateMarkerVisuals(int width, int height)
    {
        EnsureOverlayInfrastructure();

        if (overlayRoot == null || targetRenderer == null)
        {
            return;
        }

        visibleMarkerIds.Clear();
        for (int i = 0; i < markerDetections.Count; i++)
        {
            MarkerDetection detection = markerDetections[i];
            MarkerVisual visual = GetOrCreateMarkerVisual(detection.Id);
            UpdateMarkerVisual(visual, detection, width, height);
            visual.SetActive(true);
            visibleMarkerIds.Add(detection.Id);
        }

        foreach (var pair in markerVisuals)
        {
            if (!visibleMarkerIds.Contains(pair.Key))
            {
                pair.Value.SetActive(false);
            }
        }
    }

    void UpdateWorldMarkerVisuals(Pose cameraPose)
    {
        EnsureWorldOverlayInfrastructure();

        if (worldOverlayRoot == null)
        {
            return;
        }

        visibleWorldMarkerIds.Clear();
        for (int i = 0; i < markerPoseDetections.Count; i++)
        {
            MarkerPoseDetection detection = markerPoseDetections[i];
            Vector3 worldPosition;
            Quaternion worldRotation;
            Pose trackedTablePose = default;

            bool useTrackedTableOrigin = detection.Id == tableCenterMarkerId && TryGetTableOriginPose(out trackedTablePose);
            if (useTrackedTableOrigin)
            {
                worldPosition = trackedTablePose.position;
                worldRotation = trackedTablePose.rotation;
            }
            else if (!TryConvertMarkerPoseToWorldPose(detection, cameraPose, out worldPosition, out worldRotation))
            {
                continue;
            }

            MarkerVisual visual = GetOrCreateWorldMarkerVisual(detection.Id);
            UpdateWorldMarkerVisual(visual, worldPosition, worldRotation);
            visual.SetActive(true);
            visibleWorldMarkerIds.Add(detection.Id);
        }

        foreach (var pair in worldMarkerVisuals)
        {
            if (!visibleWorldMarkerIds.Contains(pair.Key))
            {
                pair.Value.SetActive(false);
            }
        }
    }

    int GetEffectiveProcessingDownscale()
    {
        int defaultDownscale = Mathf.Max(1, processingDownscale);
        int recoveryDownscale = Mathf.Max(1, lostMarkerProcessingDownscale);

        if (recoveryDownscale >= defaultDownscale)
        {
            return defaultDownscale;
        }

        return tableOriginPose.IsTracked ? defaultDownscale : recoveryDownscale;
    }

    void UpdateTrackedTableOrigin(Pose cameraPose)
    {
        bool foundTableMarker = false;

        for (int i = 0; i < markerPoseDetections.Count; i++)
        {
            MarkerPoseDetection detection = markerPoseDetections[i];
            if (detection.Id != tableCenterMarkerId)
            {
                continue;
            }

            if (!TryConvertMarkerPoseToWorldPose(detection, cameraPose, out Vector3 worldPosition, out Quaternion worldRotation))
            {
                continue;
            }

            foundTableMarker = true;
            ApplyTrackedTablePose(worldPosition, worldRotation);
            break;
        }

        if (!foundTableMarker)
        {
            float timeSinceSeen = Time.time - tableOriginPose.LastSeenTime;
            tableOriginPose.IsTracked = tableOriginPose.HasPose && timeSinceSeen <= tablePoseHoldSeconds;
        }

        if (tableOriginTransform != null && tableOriginPose.HasPose)
        {
            tableOriginTransform.SetPositionAndRotation(tableOriginPose.Position, tableOriginPose.Rotation);
        }
    }

    void ApplyTrackedTablePose(Vector3 worldPosition, Quaternion worldRotation)
    {
        if (!tableOriginPose.HasPose)
        {
            tableOriginPose.Position = worldPosition;
            tableOriginPose.Rotation = worldRotation;
            tableOriginPose.HasPose = true;
            tableOriginPose.IsTracked = true;
            tableOriginPose.LastSeenTime = Time.time;
            tableOriginPose.ConsecutiveOutlierFrames = 0;
            return;
        }

        float positionDelta = Vector3.Distance(tableOriginPose.Position, worldPosition);
        float rotationDelta = Quaternion.Angle(tableOriginPose.Rotation, worldRotation);
        bool isLargeJump = tableOriginPose.IsTracked &&
            (tableMaxPositionJumpMeters > 0f && positionDelta > tableMaxPositionJumpMeters ||
             tableMaxRotationJumpDegrees > 0f && rotationDelta > tableMaxRotationJumpDegrees);

        if (isLargeJump)
        {
            tableOriginPose.ConsecutiveOutlierFrames++;

            if (tableOriginPose.ConsecutiveOutlierFrames < Mathf.Max(1, tableOutlierFramesBeforeSnap))
            {
                tableOriginPose.LastSeenTime = Time.time;
                return;
            }

            tableOriginPose.Position = worldPosition;
            tableOriginPose.Rotation = worldRotation;
            tableOriginPose.IsTracked = true;
            tableOriginPose.LastSeenTime = Time.time;
            tableOriginPose.ConsecutiveOutlierFrames = 0;
            return;
        }

        tableOriginPose.ConsecutiveOutlierFrames = 0;

        if (tablePoseSmoothing <= 0f)
        {
            tableOriginPose.Position = worldPosition;
            tableOriginPose.Rotation = worldRotation;
        }
        else
        {
            float blend = 1f - Mathf.Exp(-tablePoseSmoothing * Time.deltaTime);
            tableOriginPose.Position = Vector3.Lerp(tableOriginPose.Position, worldPosition, blend);
            tableOriginPose.Rotation = Quaternion.Slerp(tableOriginPose.Rotation, worldRotation, blend);
        }

        tableOriginPose.IsTracked = true;
        tableOriginPose.LastSeenTime = Time.time;
    }

    MarkerVisual GetOrCreateMarkerVisual(int markerId)
    {
        if (markerVisuals.TryGetValue(markerId, out MarkerVisual existingVisual))
        {
            return existingVisual;
        }

        EnsureOverlayInfrastructure();

        GameObject root = new GameObject($"ArucoMarker_{markerId}");
        root.transform.SetParent(overlayRoot, false);

        MarkerVisual visual = new MarkerVisual
        {
            MarkerId = markerId,
            Root = root
        };

        visual.Outline = CreateLineRenderer("Outline", root.transform, lineMaterial, MarkerOutlineColor);
        visual.AxisX = CreateLineRenderer("AxisX", root.transform, axisXMaterial, AxisXColor);
        visual.AxisY = CreateLineRenderer("AxisY", root.transform, axisYMaterial, AxisYColor);
        visual.AxisZ = CreateLineRenderer("AxisZ", root.transform, axisZMaterial, AxisZColor);

        GameObject fill = new GameObject("Fill");
        fill.transform.SetParent(root.transform, false);
        visual.FillMeshFilter = fill.AddComponent<MeshFilter>();
        visual.FillRenderer = fill.AddComponent<MeshRenderer>();
        visual.FillRenderer.material = fillMaterial;
        visual.FillMeshFilter.mesh = new Mesh { name = $"MarkerFill_{markerId}" };

        markerVisuals.Add(markerId, visual);
        return visual;
    }

    MarkerVisual GetOrCreateWorldMarkerVisual(int markerId)
    {
        if (worldMarkerVisuals.TryGetValue(markerId, out MarkerVisual existingVisual))
        {
            return existingVisual;
        }

        EnsureWorldOverlayInfrastructure();

        GameObject root = new GameObject($"ArucoWorldMarker_{markerId}");
        root.transform.SetParent(worldOverlayRoot, false);

        MarkerVisual visual = new MarkerVisual
        {
            MarkerId = markerId,
            Root = root
        };

        visual.Outline = CreateLineRenderer("Outline", root.transform, lineMaterial, MarkerOutlineColor);
        visual.AxisX = CreateLineRenderer("AxisX", root.transform, axisXMaterial, AxisXColor);
        visual.AxisY = CreateLineRenderer("AxisY", root.transform, axisYMaterial, AxisYColor);
        visual.AxisZ = CreateLineRenderer("AxisZ", root.transform, axisZMaterial, AxisZColor);

        GameObject fill = new GameObject("Fill");
        fill.transform.SetParent(root.transform, false);

        visual.FillMeshFilter = fill.AddComponent<MeshFilter>();
        visual.FillRenderer = fill.AddComponent<MeshRenderer>();
        visual.FillRenderer.material = fillMaterial;
        visual.FillMeshFilter.mesh = new Mesh { name = $"WorldMarkerFill_{markerId}" };

        worldMarkerVisuals.Add(markerId, visual);
        return visual;
    }

    LineRenderer CreateLineRenderer(string name, Transform parent, Material material, Color color)
    {
        GameObject lineObject = new GameObject(name);
        lineObject.transform.SetParent(parent, false);

        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.material = material;
        line.loop = false;
        line.useWorldSpace = false;
        line.widthMultiplier = lineWidth;
        line.numCapVertices = 2;
        line.numCornerVertices = 2;
        line.positionCount = 0;
        line.startColor = color;
        line.endColor = color;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.alignment = LineAlignment.TransformZ;
        return line;
    }

    void UpdateMarkerVisual(MarkerVisual visual, MarkerDetection detection, int width, int height)
    {
        Bounds localBounds = targetRenderer.localBounds;
        if (localBounds.size.sqrMagnitude <= 0f)
        {
            return;
        }

        int axisA;
        int axisB;
        int normalAxis;
        GetRendererPlaneAxes(localBounds.size, out axisA, out axisB, out normalAxis);

        Vector3[] localCorners = new Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            Vector2 uv = ImagePointToUv(detection.Corners[i], width, height);
            localCorners[i] = UvToRendererLocalPoint(uv, localBounds, axisA, axisB, normalAxis, overlaySurfaceOffset);
        }

        UpdateOutlineRenderer(visual.Outline, localCorners);
        UpdateFillMesh(visual.FillMeshFilter.mesh, localCorners);
        UpdateAxesRenderers(visual, localCorners);

        bool showOutline = overlayMode == MarkerOverlayMode.WireframePlane;
        bool showFill = overlayMode == MarkerOverlayMode.SolidPlane;
        bool showAxes = overlayMode == MarkerOverlayMode.AxesGizmo;

        visual.Outline.enabled = showOutline;
        visual.FillRenderer.enabled = showFill;
        visual.AxisX.enabled = showAxes;
        visual.AxisY.enabled = showAxes;
        visual.AxisZ.enabled = showAxes;
    }

    void UpdateWorldMarkerVisual(MarkerVisual visual, Vector3 worldPosition, Quaternion worldRotation)
    {
        bool shouldSnapToPose = !visual.HasSmoothedPose ||
            worldPoseSmoothing <= 0f ||
            Vector3.Distance(visual.SmoothedPosition, worldPosition) > worldPoseSnapDistanceMeters ||
            Quaternion.Angle(visual.SmoothedRotation, worldRotation) > worldPoseSnapRotationDegrees;

        if (shouldSnapToPose)
        {
            visual.SmoothedPosition = worldPosition;
            visual.SmoothedRotation = worldRotation;
            visual.HasSmoothedPose = true;
        }
        else
        {
            float blend = 1f - Mathf.Exp(-worldPoseSmoothing * Time.deltaTime);
            visual.SmoothedPosition = Vector3.Lerp(visual.SmoothedPosition, worldPosition, blend);
            visual.SmoothedRotation = Quaternion.Slerp(visual.SmoothedRotation, worldRotation, blend);
        }

        visual.Root.transform.SetPositionAndRotation(visual.SmoothedPosition, visual.SmoothedRotation);
        visual.LastVisibleTime = Time.time;

        Vector3[] localCorners = BuildMarkerLocalCorners(markerSizeMeters * worldMarkerSizeMultiplier, overlaySurfaceOffset);
        UpdateOutlineRenderer(visual.Outline, localCorners);
        UpdateFillMesh(visual.FillMeshFilter.mesh, localCorners);
        UpdateAxesRenderers(visual, localCorners);

        visual.Outline.enabled = true;
        visual.FillRenderer.enabled = true;
        visual.AxisX.enabled = true;
        visual.AxisY.enabled = true;
        visual.AxisZ.enabled = true;
    }

    Vector3[] BuildMarkerLocalCorners(float size, float zOffset)
    {
        float halfSize = size * 0.5f;
        return new[]
        {
            new Vector3(-halfSize, halfSize, zOffset),
            new Vector3(halfSize, halfSize, zOffset),
            new Vector3(halfSize, -halfSize, zOffset),
            new Vector3(-halfSize, -halfSize, zOffset)
        };
    }

    void UpdateOutlineRenderer(LineRenderer outline, Vector3[] corners)
    {
        outline.widthMultiplier = lineWidth;
        outline.positionCount = corners.Length + 1;
        for (int i = 0; i < corners.Length; i++)
        {
            outline.SetPosition(i, corners[i]);
        }

        outline.SetPosition(corners.Length, corners[0]);
    }

    void UpdateFillMesh(Mesh mesh, Vector3[] corners)
    {
        mesh.Clear();
        mesh.vertices = corners;
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f)
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    void UpdateAxesRenderers(MarkerVisual visual, Vector3[] corners)
    {
        // Find the center point of the local corners
        Vector3 origin = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;

        // Lift the axes slightly out of the marker (-Z is towards the camera) to prevent z-fighting
        float planeLift = Mathf.Max(overlaySurfaceOffset, 0.0015f);
        origin += new Vector3(0, 0, -planeLift);

        float scale = Mathf.Max(markerSizeMeters * gizmoScale, minimumAxisLengthMeters);

        // Draw axes along the standard Unity local directions.
        // X (Red) = Right, Y (Green) = Up, Z (Blue) = Outwards towards camera (-Z)
        SetAxis(visual.AxisX, origin, origin + new Vector3(scale, 0, 0), axisLineWidth);
        SetAxis(visual.AxisY, origin, origin + new Vector3(0, scale, 0), axisLineWidth);
        SetAxis(visual.AxisZ, origin, origin + new Vector3(0, 0, -scale), axisLineWidth);
    }

    void SetAxis(LineRenderer axis, Vector3 start, Vector3 end, float width)
    {
        axis.widthMultiplier = width;
        axis.positionCount = 2;
        axis.SetPosition(0, start);
        axis.SetPosition(1, end);
    }

    bool TryGetProcessingIntrinsics(int processingWidth, int processingHeight, Vector2Int currentResolution, PassthroughCameraAccess.CameraIntrinsics intrinsics, out Vector2 focalLength, out Vector2 principalPoint)
    {
        focalLength = default;
        principalPoint = default;

        if (currentResolution.x <= 0 || currentResolution.y <= 0 || intrinsics.SensorResolution.x <= 0 || intrinsics.SensorResolution.y <= 0)
        {
            return false;
        }

        Rect sensorCropRegion = CalcSensorCropRegion(currentResolution, intrinsics.SensorResolution);
        if (sensorCropRegion.width <= 0f || sensorCropRegion.height <= 0f)
        {
            return false;
        }

        float fxAtCurrentResolution = intrinsics.FocalLength.x * currentResolution.x / sensorCropRegion.width;
        float fyAtCurrentResolution = intrinsics.FocalLength.y * currentResolution.y / sensorCropRegion.height;
        float cxAtCurrentResolution = (intrinsics.PrincipalPoint.x - sensorCropRegion.x) * currentResolution.x / sensorCropRegion.width;
        float cyAtCurrentResolution = (intrinsics.PrincipalPoint.y - sensorCropRegion.y) * currentResolution.y / sensorCropRegion.height;

        float scaleX = (float)processingWidth / currentResolution.x;
        float scaleY = (float)processingHeight / currentResolution.y;

        focalLength = new Vector2(fxAtCurrentResolution * scaleX, fyAtCurrentResolution * scaleY);
        principalPoint = new Vector2(cxAtCurrentResolution * scaleX, cyAtCurrentResolution * scaleY);
        return true;
    }

    Rect CalcSensorCropRegion(Vector2Int currentResolution, Vector2Int sensorResolution)
    {
        Vector2 sensorResolutionF = sensorResolution;
        Vector2 currentResolutionF = currentResolution;
        Vector2 scaleFactor = currentResolutionF / sensorResolutionF;
        scaleFactor /= Mathf.Max(scaleFactor.x, scaleFactor.y);

        return new Rect(
            sensorResolutionF.x * (1f - scaleFactor.x) * 0.5f,
            sensorResolutionF.y * (1f - scaleFactor.y) * 0.5f,
            sensorResolutionF.x * scaleFactor.x,
            sensorResolutionF.y * scaleFactor.y);
    }

    bool TryConvertMarkerPoseToWorldPose(MarkerPoseDetection detection, Pose cameraPose, out Vector3 worldPosition, out Quaternion worldRotation)
    {
        worldPosition = default;
        worldRotation = Quaternion.identity;

        if (cameraPose.rotation == default && cameraPose.position == default)
        {
            return false;
        }

        // 1. Convert Position: OpenCV camera space (Y down) to Unity camera space (Y up)
        Vector3 localPosition = new Vector3(
            detection.TranslationVector.x,
            -detection.TranslationVector.y,
            detection.TranslationVector.z);

        // 2. Convert Rotation:
        Matrix4x4 cvRotation = RodriguesToMatrix(detection.RotationVector);

        // Extract the marker's physical basis vectors in OpenCV camera space
        Vector3 cvX = cvRotation.GetColumn(0);
        Vector3 cvY = cvRotation.GetColumn(1);
        Vector3 cvZ = cvRotation.GetColumn(2);

        // Convert those basis vectors into Unity's camera space by flipping the Y axis
        Vector3 unityX = new Vector3(cvX.x, -cvX.y, cvX.z);
        Vector3 unityY = new Vector3(cvY.x, -cvY.y, cvY.z);
        Vector3 unityZ = new Vector3(cvZ.x, -cvZ.y, cvZ.z);

        // unityZ points OUT of the physical marker towards the camera.
        // To build a standard Unity plane orientation (where Z points INTO the plane),
        // we negate unityZ for the Forward direction, and use unityY for the Up direction.
        Quaternion localRotation = Quaternion.LookRotation(-unityZ, unityY);

        worldPosition = cameraPose.position + cameraPose.rotation * localPosition;
        worldRotation = cameraPose.rotation * localRotation;
        return true;
    }

    Vector3 ConvertOpenCvVectorToUnity(Vector4 cvVector)
    {
        return new Vector3(cvVector.x, -cvVector.y, cvVector.z);
    }

    Matrix4x4 RodriguesToMatrix(Vector3 rotationVector)
    {
        float theta = rotationVector.magnitude;
        if (theta < 1e-6f)
        {
            return Matrix4x4.identity;
        }

        Vector3 axis = rotationVector / theta;
        float x = axis.x;
        float y = axis.y;
        float z = axis.z;
        float cosTheta = Mathf.Cos(theta);
        float sinTheta = Mathf.Sin(theta);
        float oneMinusCos = 1f - cosTheta;

        Matrix4x4 matrix = Matrix4x4.identity;
        matrix.m00 = cosTheta + (x * x * oneMinusCos);
        matrix.m01 = (x * y * oneMinusCos) - (z * sinTheta);
        matrix.m02 = (x * z * oneMinusCos) + (y * sinTheta);
        matrix.m10 = (y * x * oneMinusCos) + (z * sinTheta);
        matrix.m11 = cosTheta + (y * y * oneMinusCos);
        matrix.m12 = (y * z * oneMinusCos) - (x * sinTheta);
        matrix.m20 = (z * x * oneMinusCos) - (y * sinTheta);
        matrix.m21 = (z * y * oneMinusCos) + (x * sinTheta);
        matrix.m22 = cosTheta + (z * z * oneMinusCos);
        return matrix;
    }

    Vector2 ImagePointToUv(Vector2 imagePoint, int width, int height)
    {
        // OpenCV corners are in detection space (top-left origin). Convert them into the
        // displayed texture space, then into Unity UV space (bottom-left origin),
        // so the world overlay matches the corrected feed.
        float safeWidth = Mathf.Max(1f, width - 1f);
        float safeHeight = Mathf.Max(1f, height - 1f);

        float displayX = flipDisplayHorizontally ? (safeWidth - imagePoint.x) : imagePoint.x;
        float displayY = flipDisplayVertically ? imagePoint.y : (safeHeight - imagePoint.y);

        float u = Mathf.Clamp01(displayX / safeWidth);
        float v = Mathf.Clamp01(1f - (displayY / safeHeight));

        return new Vector2(u, v);
    }

    Vector3 UvToRendererLocalPoint(Vector2 uv, Bounds localBounds, int axisA, int axisB, int normalAxis, float normalOffset)
    {
        Vector3 point = localBounds.center;
        Vector3 min = localBounds.min;
        Vector3 size = localBounds.size;

        SetAxisValue(ref point, axisA, GetAxisValue(min, axisA) + (GetAxisValue(size, axisA) * uv.x));
        SetAxisValue(ref point, axisB, GetAxisValue(min, axisB) + (GetAxisValue(size, axisB) * uv.y));
        SetAxisValue(ref point, normalAxis, GetAxisValue(localBounds.max, normalAxis) + normalOffset);

        return point;
    }

    void GetRendererPlaneAxes(Vector3 size, out int axisA, out int axisB, out int normalAxis)
    {
        float x = Mathf.Abs(size.x);
        float y = Mathf.Abs(size.y);
        float z = Mathf.Abs(size.z);

        if (x <= y && x <= z)
        {
            normalAxis = 0;
            axisA = 2;
            axisB = 1;
            return;
        }

        if (y <= x && y <= z)
        {
            normalAxis = 1;
            axisA = 0;
            axisB = 2;
            return;
        }

        normalAxis = 2;
        axisA = 0;
        axisB = 1;
    }

    float GetAxisValue(Vector3 vector, int axis)
    {
        return axis switch
        {
            0 => vector.x,
            1 => vector.y,
            _ => vector.z
        };
    }

    void SetAxisValue(ref Vector3 vector, int axis, float value)
    {
        switch (axis)
        {
            case 0:
                vector.x = value;
                break;
            case 1:
                vector.y = value;
                break;
            default:
                vector.z = value;
                break;
        }
    }
}
