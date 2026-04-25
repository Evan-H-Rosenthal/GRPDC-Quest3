using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Meta.XR;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

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

    class StackCubeSample
    {
        public int CubeId;
        public Vector3 WorldPosition;
        public Color Color;
        public float CubeSize;
    }

    class DetectedStack
    {
        public readonly List<StackCubeSample> Cubes = new List<StackCubeSample>(4);
        public Vector2 HorizontalCenter;
        public float Score;
    }

    class StackHudVisual
    {
        public Transform Root;
        public readonly List<GameObject> Cells = new List<GameObject>(4);
        public readonly List<Renderer> CellRenderers = new List<Renderer>(4);
        public readonly List<Material> CellMaterials = new List<Material>(4);
    }

    class PlacementTargetVisual
    {
        public Transform Root;
        public Transform SurfaceMarker;
        public Renderer SurfaceRenderer;
        public Material SurfaceMaterial;
        public Transform SymbolRoot;
        public readonly List<Transform> SymbolSegments = new List<Transform>(3);
        public readonly List<Renderer> SymbolRenderers = new List<Renderer>(3);
        public readonly List<Material> SymbolMaterials = new List<Material>(3);
        public Transform TaskPreviewRoot;
        public readonly List<Transform> PreviewCubes = new List<Transform>(3);
        public readonly List<Renderer> PreviewCubeRenderers = new List<Renderer>(3);
        public readonly List<Material> PreviewCubeMaterials = new List<Material>(3);
        public readonly List<Transform> PreviewStatusRoots = new List<Transform>(3);
        public readonly List<Renderer> PreviewStatusRenderers = new List<Renderer>(6);
        public readonly List<Material> PreviewStatusMaterials = new List<Material>(6);
    }

    class PlacementTaskRequirement
    {
        public int CubeId;
        public Color Color;
        public float CubeSize;
    }

    class TrackingPipelineResult
    {
        public int Width;
        public int Height;
        public Pose CameraPose;
        public byte[] GrayBytes;
        public float[] MarkerData;
        public float[] MarkerPoseData;
        public bool NeedsDebugTexture;
        public bool NeedsDetailedMarkerData;
        public double ProcessingMilliseconds;
    }

    public PassthroughCameraAccess cameraAccess;
    public Renderer targetRenderer;
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
    public bool enableMarkerTrackingPipeline = true;
    [Min(0.05f)] public float pipelineDisabledTestCubeDistanceMeters = 0.6f;
    [Min(0.02f)] public float pipelineDisabledTestCubeSizeMeters = 0.12f;
    public int tableCenterMarkerId = 0;
    [Range(0f, 30f)] public float tablePoseSmoothing = 24f;
    [Min(0f)] public float tablePoseHoldSeconds = 0.2f;
    [Min(0f)] public float tableMaxPositionJumpMeters = 0.05f;
    [Range(0f, 180f)] public float tableMaxRotationJumpDegrees = 20f;
    [Range(1, 5)] public int tableOutlierFramesBeforeSnap = 2;
    [Min(0f)] public float tablePositionDeadbandMeters = 0.0015f;
    [Range(0f, 45f)] public float tableRotationDeadbandDegrees = 1.5f;
    [Min(0f)] public float tableMaxPositionStepPerUpdateMeters = 0.015f;
    [Range(0f, 180f)] public float tableMaxRotationStepPerUpdateDegrees = 10f;
    public bool drawDebugOverlay = true;
    public bool estimateMarkerPoses = true;
    public bool drawWorldMarkers = false;
    public bool updateCameraFeedTexture = false;
    public bool flipDisplayHorizontally = true;
    public bool flipDisplayVertically = true;
    public Transform worldOverlayParent;
    public Transform tableOriginTransform;
    public bool applyTrackedTableRotationToAnchor = true;
    public bool showStackHud = true;
    [Min(0.1f)] public float stackHudDistance = 0.9f;
    public Vector2 stackHudViewOffset = new Vector2(0.18f, 0.12f);
    [Min(0.005f)] public float stackHudCellSize = 0.03f;
    [Min(0f)] public float stackHudCellGap = 0.0075f;
    [Min(0.0005f)] public float stackHudCellDepth = 0.002f;
    [Min(0.01f)] public float stackHudStackSpacing = 0.085f;
    [Min(0.01f)] public float stackGroupingDistanceMeters = 0.055f;
    [Range(0.2f, 1.2f)] public float stackSupportHorizontalToleranceFactor = 0.45f;
    [Range(0.2f, 1.2f)] public float stackSupportMinVerticalSpacingFactor = 0.7f;
    [Range(0.8f, 2.2f)] public float stackSupportMaxVerticalSpacingFactor = 1.35f;
    [Range(1, 2)] public int maxDetectedStacksToDisplay = 2;
    [Range(1, 6)] public int stackConfigurationConfirmationFrames = 2;
    [Range(1, 4)] public int stackConfigurationReleaseFrames = 1;
    [Range(4f, 30f)] public float stackHudUpdatesPerSecond = 12f;
    public bool showFpsCounter = true;
    [Min(0.05f)] public float fpsCounterUpdateIntervalSeconds = 0.2f;
    [Min(0.1f)] public float fpsCounterTextScale = 0.35f;
    public Vector2 fpsCounterLocalOffset = new Vector2(-0.14f, 0.06f);
    [Min(0.25f)] public float fpsAverageWindowSeconds = 2f;
    public bool showPlacementTarget = true;
    [Min(0.05f)] public float placementTargetMinRadiusMeters = 0.12f;
    [Min(0.08f)] public float placementTargetMaxRadiusMeters = 0.22f;
    [Min(0.01f)] public float placementTargetSurfaceSizeMeters = 0.09f;
    [Min(0.001f)] public float placementTargetSurfaceThicknessMeters = 0.004f;
    [Min(0f)] public float placementTargetSurfaceLiftMeters = 0.002f;
    [Min(0.02f)] public float placementTargetIndicatorHeightMeters = 0.12f;
    [Min(0.005f)] public float placementTargetIndicatorScaleMeters = 0.045f;
    [Min(0.01f)] public float placementTargetMatchRadiusMeters = 0.06f;
    public Vector3 placementTaskPreviewLocalOffset = new Vector3(0.11f, 0.045f, 0f);
    [Min(0.01f)] public float placementTaskPreviewCubeSizeMeters = 0.03f;
    [Min(0f)] public float placementTaskPreviewCubeGapMeters = 0.008f;
    [Min(0.005f)] public float placementTaskPreviewStatusOffsetMeters = 0.04f;
    [Min(0.005f)] public float placementTaskPreviewStatusScaleMeters = 0.02f;

    Texture2D debugTexture;
    AndroidJavaClass arucoClass;
    bool isProcessing;
    float lastTrackingRequestTime = float.NegativeInfinity;
    float trackingCooldownUntilTime = float.NegativeInfinity;
    byte[] grayBytes;
    byte[] readbackBytes;
    Color32[] debugPixels;
    readonly object trackingResultLock = new object();
    TrackingPipelineResult pendingTrackingResult;
    bool hasPendingTrackingResult;
    int trackingWorkerRunning;
    int consecutiveSlowTrackingFrames;
    readonly List<MarkerDetection> markerDetections = new List<MarkerDetection>();
    readonly List<MarkerPoseDetection> markerPoseDetections = new List<MarkerPoseDetection>();
    readonly Dictionary<int, MarkerVisual> markerVisuals = new Dictionary<int, MarkerVisual>();
    readonly List<int> visibleMarkerIds = new List<int>();
    readonly Dictionary<int, MarkerVisual> worldMarkerVisuals = new Dictionary<int, MarkerVisual>();
    readonly List<int> visibleWorldMarkerIds = new List<int>();
    readonly TrackedMarkerPose tableOriginPose = new TrackedMarkerPose();
    readonly List<ArucoCubeTracker> cubeTrackers = new List<ArucoCubeTracker>(4);
    readonly List<StackCubeSample> stackCubeSamples = new List<StackCubeSample>(4);
    readonly List<DetectedStack> detectedStacks = new List<DetectedStack>(2);
    readonly List<DetectedStack> rawDetectedStacks = new List<DetectedStack>(2);
    readonly List<StackHudVisual> stackHudVisuals = new List<StackHudVisual>(2);
    readonly List<DetectedStack> pendingDetectedStacks = new List<DetectedStack>(2);
    readonly List<DetectedStack> stackCandidateBuffer = new List<DetectedStack>(12);
    readonly List<StackCubeSample> stackCandidateSampleBuffer = new List<StackCubeSample>(4);
    Pose lastProcessedCameraPose;
    bool hasLastProcessedCameraPose;
    float lastStackHudEvaluationTime = float.NegativeInfinity;
    int displayedStackSignatureHash;
    int pendingStackSignatureHash;
    int pendingStackSignatureFrames;
    bool hasPendingStackSignature;
    readonly Queue<float> fpsSampleDurations = new Queue<float>(256);
    float fpsSampleDurationSum;
    float lastFpsCounterUpdateTime = float.NegativeInfinity;
    PlacementTargetVisual placementTargetVisual;
    Vector2 placementTargetOffset;
    int placementTargetCubeId = -1;
    Color placementTargetCubeColor = Color.white;
    bool hasPlacementTarget;
    bool isPlacementTaskActive;
    bool hasCompletedPlacementTask;
    readonly List<PlacementTaskRequirement> placementTaskRequirements = new List<PlacementTaskRequirement>(3);
    GameObject pipelineDisabledTestCube;
    Material pipelineDisabledTestCubeMaterial;

    Transform overlayRoot;
    Transform worldOverlayRoot;
    Transform stackHudRoot;
    Transform fpsCounterRoot;
    TextMesh fpsCounterText;
    Material lineMaterial;
    Material fillMaterial;
    Material axisXMaterial; // Add this
    Material axisYMaterial; // Add this
    Material axisZMaterial; // Add this
    static readonly Color32 MarkerOutlineColor = new Color32(0, 255, 120, 255);
    static readonly Color AxisXColor = new Color(1f, 0.25f, 0.25f, 1f);
    static readonly Color AxisYColor = new Color(0.25f, 1f, 0.25f, 1f);
    static readonly Color AxisZColor = new Color(0.25f, 0.55f, 1f, 1f);

    public event Action StackingTaskCompleted;

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

    public void BeginStackingTask()
    {
        hasCompletedPlacementTask = false;
        RefreshCubeTrackersIfNeeded();

        if (!TryGeneratePlacementTaskRequirements())
        {
            Debug.LogWarning("Could not start stacking task because fewer than three cube trackers were available.");
            isPlacementTaskActive = false;
            hasPlacementTarget = false;
            placementTargetCubeId = -1;
            return;
        }

        RandomizePlacementTarget();
        isPlacementTaskActive = hasPlacementTarget && placementTaskRequirements.Count >= 3;
    }

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

    void RefreshCubeTrackers()
    {
        cubeTrackers.Clear();

        ArucoCubeTracker[] trackers = FindObjectsByType<ArucoCubeTracker>(FindObjectsSortMode.None);
        for (int i = 0; i < trackers.Length; i++)
        {
            if (trackers[i] != null)
            {
                cubeTrackers.Add(trackers[i]);
            }
        }
    }

    void EnsureStackHudInfrastructure()
    {
        if (stackHudRoot != null)
        {
            return;
        }

        GameObject root = new GameObject("DetectedStacksHud");
        stackHudRoot = root.transform;
        EnsureFpsCounterInfrastructure();
    }

    void DestroyStackHudVisuals()
    {
        for (int i = 0; i < stackHudVisuals.Count; i++)
        {
            StackHudVisual visual = stackHudVisuals[i];
            if (visual == null)
            {
                continue;
            }

            for (int materialIndex = 0; materialIndex < visual.CellMaterials.Count; materialIndex++)
            {
                if (visual.CellMaterials[materialIndex] != null)
                {
                    Destroy(visual.CellMaterials[materialIndex]);
                }
            }

            if (visual.Root != null)
            {
                Destroy(visual.Root.gameObject);
            }
        }

        stackHudVisuals.Clear();

        if (stackHudRoot != null)
        {
            Destroy(stackHudRoot.gameObject);
            stackHudRoot = null;
        }

        fpsCounterRoot = null;
        fpsCounterText = null;
    }

    void EnsureFpsCounterInfrastructure()
    {
        if (stackHudRoot == null || fpsCounterRoot != null)
        {
            return;
        }

        GameObject fpsObject = new GameObject("FpsCounter");
        fpsCounterRoot = fpsObject.transform;
        fpsCounterRoot.SetParent(stackHudRoot, false);

        fpsCounterText = fpsObject.AddComponent<TextMesh>();
        fpsCounterText.text = "FPS --";
        fpsCounterText.fontSize = 64;
        fpsCounterText.characterSize = 0.03f;
        fpsCounterText.anchor = TextAnchor.UpperLeft;
        fpsCounterText.alignment = TextAlignment.Left;
        fpsCounterText.color = Color.white;
    }

    void EnsurePlacementTargetInfrastructure()
    {
        if (placementTargetVisual != null && placementTargetVisual.Root != null)
        {
            return;
        }

        GameObject rootObject = new GameObject("PlacementTarget");
        PlacementTargetVisual visual = new PlacementTargetVisual
        {
            Root = rootObject.transform
        };

        GameObject surface = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        surface.name = "SurfaceMarker";
        surface.transform.SetParent(visual.Root, false);
        Collider surfaceCollider = surface.GetComponent<Collider>();
        if (surfaceCollider != null)
        {
            surfaceCollider.enabled = false;
        }

        visual.SurfaceMarker = surface.transform;
        visual.SurfaceRenderer = surface.GetComponent<Renderer>();
        visual.SurfaceRenderer.shadowCastingMode = ShadowCastingMode.Off;
        visual.SurfaceRenderer.receiveShadows = false;
        visual.SurfaceMaterial = CreateHudCellMaterial();
        visual.SurfaceRenderer.sharedMaterial = visual.SurfaceMaterial;

        GameObject symbolRootObject = new GameObject("StatusSymbol");
        symbolRootObject.transform.SetParent(visual.Root, false);
        visual.SymbolRoot = symbolRootObject.transform;

        for (int i = 0; i < 3; i++)
        {
            GameObject segment = GameObject.CreatePrimitive(PrimitiveType.Cube);
            segment.name = $"Segment_{i + 1}";
            segment.transform.SetParent(visual.SymbolRoot, false);
            Collider segmentCollider = segment.GetComponent<Collider>();
            if (segmentCollider != null)
            {
                segmentCollider.enabled = false;
            }

            Renderer renderer = segment.GetComponent<Renderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Material material = CreateHudCellMaterial();
            renderer.sharedMaterial = material;

            visual.SymbolSegments.Add(segment.transform);
            visual.SymbolRenderers.Add(renderer);
            visual.SymbolMaterials.Add(material);
        }

        GameObject taskPreviewRootObject = new GameObject("TaskPreview");
        taskPreviewRootObject.transform.SetParent(visual.Root, false);
        visual.TaskPreviewRoot = taskPreviewRootObject.transform;

        for (int i = 0; i < 3; i++)
        {
            GameObject previewCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            previewCube.name = $"PreviewCube_{i + 1}";
            previewCube.transform.SetParent(visual.TaskPreviewRoot, false);
            Collider previewCubeCollider = previewCube.GetComponent<Collider>();
            if (previewCubeCollider != null)
            {
                previewCubeCollider.enabled = false;
            }

            Renderer previewCubeRenderer = previewCube.GetComponent<Renderer>();
            previewCubeRenderer.shadowCastingMode = ShadowCastingMode.Off;
            previewCubeRenderer.receiveShadows = false;
            Material previewCubeMaterial = CreateHudCellMaterial();
            previewCubeRenderer.sharedMaterial = previewCubeMaterial;

            visual.PreviewCubes.Add(previewCube.transform);
            visual.PreviewCubeRenderers.Add(previewCubeRenderer);
            visual.PreviewCubeMaterials.Add(previewCubeMaterial);

            GameObject previewStatusRootObject = new GameObject($"PreviewStatus_{i + 1}");
            previewStatusRootObject.transform.SetParent(visual.TaskPreviewRoot, false);
            Transform previewStatusRoot = previewStatusRootObject.transform;
            visual.PreviewStatusRoots.Add(previewStatusRoot);

            for (int segmentIndex = 0; segmentIndex < 2; segmentIndex++)
            {
                GameObject statusSegment = GameObject.CreatePrimitive(PrimitiveType.Cube);
                statusSegment.name = $"Segment_{segmentIndex + 1}";
                statusSegment.transform.SetParent(previewStatusRoot, false);
                Collider statusCollider = statusSegment.GetComponent<Collider>();
                if (statusCollider != null)
                {
                    statusCollider.enabled = false;
                }

                Renderer statusRenderer = statusSegment.GetComponent<Renderer>();
                statusRenderer.shadowCastingMode = ShadowCastingMode.Off;
                statusRenderer.receiveShadows = false;
                Material statusMaterial = CreateHudCellMaterial();
                statusRenderer.sharedMaterial = statusMaterial;

                visual.PreviewStatusRenderers.Add(statusRenderer);
                visual.PreviewStatusMaterials.Add(statusMaterial);
            }
        }

        rootObject.SetActive(false);
        placementTargetVisual = visual;
    }

    void DestroyPlacementTargetVisual()
    {
        if (placementTargetVisual == null)
        {
            return;
        }

        if (placementTargetVisual.SurfaceMaterial != null)
        {
            Destroy(placementTargetVisual.SurfaceMaterial);
        }

        for (int i = 0; i < placementTargetVisual.SymbolMaterials.Count; i++)
        {
            if (placementTargetVisual.SymbolMaterials[i] != null)
            {
                Destroy(placementTargetVisual.SymbolMaterials[i]);
            }
        }

        for (int i = 0; i < placementTargetVisual.PreviewCubeMaterials.Count; i++)
        {
            if (placementTargetVisual.PreviewCubeMaterials[i] != null)
            {
                Destroy(placementTargetVisual.PreviewCubeMaterials[i]);
            }
        }

        for (int i = 0; i < placementTargetVisual.PreviewStatusMaterials.Count; i++)
        {
            if (placementTargetVisual.PreviewStatusMaterials[i] != null)
            {
                Destroy(placementTargetVisual.PreviewStatusMaterials[i]);
            }
        }

        if (placementTargetVisual.Root != null)
        {
            Destroy(placementTargetVisual.Root.gameObject);
        }

        placementTargetVisual = null;
    }

    void UpdateStackHudTransform()
    {
        if (!showStackHud)
        {
            if (stackHudRoot != null && stackHudRoot.gameObject.activeSelf)
            {
                stackHudRoot.gameObject.SetActive(false);
            }

            return;
        }

        EnsureStackHudInfrastructure();

        Transform followTransform = Camera.main != null ? Camera.main.transform : null;
        if (followTransform == null)
        {
            if (stackHudRoot.gameObject.activeSelf)
            {
                stackHudRoot.gameObject.SetActive(false);
            }

            return;
        }

        if (!stackHudRoot.gameObject.activeSelf)
        {
            stackHudRoot.gameObject.SetActive(true);
        }

        if (stackHudRoot.parent != followTransform)
        {
            stackHudRoot.SetParent(followTransform, false);
        }

        stackHudRoot.localPosition = new Vector3(
            stackHudViewOffset.x,
            stackHudViewOffset.y,
            Mathf.Max(0.1f, stackHudDistance));
        stackHudRoot.localRotation = Quaternion.identity;
        stackHudRoot.localScale = Vector3.one;
        UpdateFpsCounterVisual();
    }

    void UpdateStackHudVisuals()
    {
        if (!showStackHud || stackHudRoot == null || !stackHudRoot.gameObject.activeInHierarchy)
        {
            HideAllStackHudVisuals();
            return;
        }

        float updateInterval = 1f / Mathf.Max(4f, stackHudUpdatesPerSecond);
        if ((Time.unscaledTime - lastStackHudEvaluationTime) >= updateInterval)
        {
            DetectStacks();
            StabilizeDetectedStacks();
            lastStackHudEvaluationTime = Time.unscaledTime;
        }

        EnsureStackHudVisualCount(detectedStacks.Count);

        for (int stackIndex = 0; stackIndex < stackHudVisuals.Count; stackIndex++)
        {
            bool shouldShow = stackIndex < detectedStacks.Count;
            StackHudVisual visual = stackHudVisuals[stackIndex];
            if (visual?.Root == null)
            {
                continue;
            }

            if (visual.Root.gameObject.activeSelf != shouldShow)
            {
                visual.Root.gameObject.SetActive(shouldShow);
            }

            if (!shouldShow)
            {
                continue;
            }

            UpdateStackHudVisual(visual, detectedStacks[stackIndex], stackIndex);
        }
    }

    void HideAllStackHudVisuals()
    {
        for (int i = 0; i < stackHudVisuals.Count; i++)
        {
            if (stackHudVisuals[i]?.Root != null && stackHudVisuals[i].Root.gameObject.activeSelf)
            {
                stackHudVisuals[i].Root.gameObject.SetActive(false);
            }
        }
    }

    void DetectStacks()
    {
        if (cubeTrackers.Count == 0)
        {
            RefreshCubeTrackers();
        }

        stackCubeSamples.Clear();
        rawDetectedStacks.Clear();

        for (int i = 0; i < cubeTrackers.Count; i++)
        {
            ArucoCubeTracker tracker = cubeTrackers[i];
            if (tracker == null || !tracker.IsCubeCurrentlyTracked)
            {
                continue;
            }

            if (!tracker.TryGetCurrentCubeWorldPose(out Pose cubePose))
            {
                continue;
            }

            stackCubeSamples.Add(new StackCubeSample
            {
                CubeId = tracker.CubeMarkerId,
                WorldPosition = cubePose.position,
                Color = tracker.CubeColor,
                CubeSize = Mathf.Max(0.001f, tracker.CubeSizeMeters)
            });
        }

        BuildDetectedStacksFromCandidates();
    }

    void BuildDetectedStacksFromCandidates()
    {
        int sampleCount = stackCubeSamples.Count;
        if (sampleCount <= 0)
        {
            return;
        }

        stackCandidateBuffer.Clear();
        int maxMask = 1 << sampleCount;
        for (int mask = 0; mask < maxMask; mask++)
        {
            int selectedCount = CountBits(mask);
            if (selectedCount < 2)
            {
                continue;
            }

            DetectedStack candidate = TryBuildStackCandidate(mask);
            if (candidate != null)
            {
                stackCandidateBuffer.Add(candidate);
            }
        }

        stackCandidateBuffer.Sort((a, b) =>
        {
            int sizeComparison = b.Cubes.Count.CompareTo(a.Cubes.Count);
            if (sizeComparison != 0)
            {
                return sizeComparison;
            }

            return b.Score.CompareTo(a.Score);
        });

        int usedMask = 0;
        for (int i = 0; i < stackCandidateBuffer.Count && rawDetectedStacks.Count < maxDetectedStacksToDisplay; i++)
        {
            DetectedStack candidate = stackCandidateBuffer[i];
            int candidateMask = BuildMaskForStack(candidate);
            if ((usedMask & candidateMask) != 0)
            {
                continue;
            }

            rawDetectedStacks.Add(candidate);
            usedMask |= candidateMask;
        }

        rawDetectedStacks.Sort((a, b) =>
        {
            int xComparison = a.HorizontalCenter.x.CompareTo(b.HorizontalCenter.x);
            return xComparison != 0 ? xComparison : a.HorizontalCenter.y.CompareTo(b.HorizontalCenter.y);
        });
    }

    void EnsureStackHudVisualCount(int targetCount)
    {
        EnsureStackHudInfrastructure();

        while (stackHudVisuals.Count < targetCount)
        {
            stackHudVisuals.Add(CreateStackHudVisual(stackHudVisuals.Count));
        }
    }

    static Vector3 GetStackVerticalAxis()
    {
        return Vector3.up;
    }

    static float GetVerticalCoordinate(Vector3 worldPosition)
    {
        return Vector3.Dot(worldPosition, GetStackVerticalAxis());
    }

    static Vector2 GetHorizontalCoordinates(Vector3 worldPosition)
    {
        Vector3 up = GetStackVerticalAxis();
        Vector3 planar = worldPosition - (up * Vector3.Dot(worldPosition, up));
        return new Vector2(planar.x, planar.z);
    }

    DetectedStack TryBuildStackCandidate(int sampleMask)
    {
        stackCandidateSampleBuffer.Clear();
        for (int sampleIndex = 0; sampleIndex < stackCubeSamples.Count; sampleIndex++)
        {
            if ((sampleMask & (1 << sampleIndex)) == 0)
            {
                continue;
            }

            stackCandidateSampleBuffer.Add(stackCubeSamples[sampleIndex]);
        }

        if (stackCandidateSampleBuffer.Count < 2)
        {
            return null;
        }

        stackCandidateSampleBuffer.Sort((a, b) => GetVerticalCoordinate(a.WorldPosition).CompareTo(GetVerticalCoordinate(b.WorldPosition)));

        float horizontalTolerance = 0f;
        float verticalScorePenalty = 0f;
        float horizontalScorePenalty = 0f;
        Vector2 horizontalCenter = Vector2.zero;
        for (int i = 0; i < stackCandidateSampleBuffer.Count; i++)
        {
            horizontalCenter += GetHorizontalCoordinates(stackCandidateSampleBuffer[i].WorldPosition);
            horizontalTolerance = Mathf.Max(
                horizontalTolerance,
                Mathf.Max(0.01f, stackCandidateSampleBuffer[i].CubeSize * stackSupportHorizontalToleranceFactor, stackGroupingDistanceMeters));
        }

        horizontalCenter /= stackCandidateSampleBuffer.Count;
        float maxHorizontalDistanceFromCenter = 0f;
        for (int i = 0; i < stackCandidateSampleBuffer.Count; i++)
        {
            float distanceFromCenter = Vector2.Distance(GetHorizontalCoordinates(stackCandidateSampleBuffer[i].WorldPosition), horizontalCenter);
            maxHorizontalDistanceFromCenter = Mathf.Max(maxHorizontalDistanceFromCenter, distanceFromCenter);
        }

        if (maxHorizontalDistanceFromCenter > horizontalTolerance)
        {
            return null;
        }

        for (int i = 0; i < stackCandidateSampleBuffer.Count - 1; i++)
        {
            StackCubeSample lower = stackCandidateSampleBuffer[i];
            StackCubeSample upper = stackCandidateSampleBuffer[i + 1];
            float averageCubeSize = (lower.CubeSize + upper.CubeSize) * 0.5f;
            float verticalDelta = GetVerticalCoordinate(upper.WorldPosition) - GetVerticalCoordinate(lower.WorldPosition);
            float minimumVerticalSpacing = averageCubeSize * stackSupportMinVerticalSpacingFactor;
            float maximumVerticalSpacing = averageCubeSize * stackSupportMaxVerticalSpacingFactor;

            if (verticalDelta < minimumVerticalSpacing || verticalDelta > maximumVerticalSpacing)
            {
                return null;
            }

            float horizontalDistance = Vector2.Distance(
                GetHorizontalCoordinates(lower.WorldPosition),
                GetHorizontalCoordinates(upper.WorldPosition));
            float pairTolerance = Mathf.Max(0.01f, averageCubeSize * stackSupportHorizontalToleranceFactor, stackGroupingDistanceMeters);
            if (horizontalDistance > pairTolerance)
            {
                return null;
            }

            verticalScorePenalty += Mathf.Abs(verticalDelta - averageCubeSize);
            horizontalScorePenalty += horizontalDistance;
        }

        float totalVerticalSpan =
            GetVerticalCoordinate(stackCandidateSampleBuffer[stackCandidateSampleBuffer.Count - 1].WorldPosition) -
            GetVerticalCoordinate(stackCandidateSampleBuffer[0].WorldPosition);
        float minimumSpan = stackCandidateSampleBuffer[0].CubeSize * stackSupportMinVerticalSpacingFactor;
        if (totalVerticalSpan < minimumSpan)
        {
            return null;
        }

        DetectedStack candidate = new DetectedStack
        {
            HorizontalCenter = horizontalCenter,
            Score = (stackCandidateSampleBuffer.Count * 100f) - (verticalScorePenalty * 100f) - (horizontalScorePenalty * 100f) - (maxHorizontalDistanceFromCenter * 100f)
        };

        for (int i = 0; i < stackCandidateSampleBuffer.Count; i++)
        {
            candidate.Cubes.Add(stackCandidateSampleBuffer[i]);
        }

        return candidate;
    }

    void StabilizeDetectedStacks()
    {
        int rawSignatureHash = BuildStackSignatureHash(rawDetectedStacks);
        if (rawSignatureHash == displayedStackSignatureHash)
        {
            pendingStackSignatureHash = 0;
            pendingStackSignatureFrames = 0;
            hasPendingStackSignature = false;

            if (rawDetectedStacks.Count > 0)
            {
                CopyStacks(rawDetectedStacks, detectedStacks);
            }

            return;
        }

        int confirmationFrames = rawDetectedStacks.Count <= 0
            ? Mathf.Max(1, stackConfigurationReleaseFrames)
            : Mathf.Max(1, stackConfigurationConfirmationFrames);

        if (!hasPendingStackSignature || rawSignatureHash != pendingStackSignatureHash)
        {
            pendingStackSignatureHash = rawSignatureHash;
            pendingStackSignatureFrames = 1;
            hasPendingStackSignature = true;
            CopyStacks(rawDetectedStacks, pendingDetectedStacks);
            return;
        }

        pendingStackSignatureFrames++;
        if (pendingStackSignatureFrames < confirmationFrames)
        {
            return;
        }

        displayedStackSignatureHash = pendingStackSignatureHash;
        pendingStackSignatureHash = 0;
        pendingStackSignatureFrames = 0;
        hasPendingStackSignature = false;
        CopyStacks(pendingDetectedStacks, detectedStacks);
    }

    void CopyStacks(List<DetectedStack> source, List<DetectedStack> destination)
    {
        destination.Clear();
        for (int i = 0; i < source.Count; i++)
        {
            DetectedStack copy = new DetectedStack
            {
                HorizontalCenter = source[i].HorizontalCenter,
                Score = source[i].Score
            };

            for (int cubeIndex = 0; cubeIndex < source[i].Cubes.Count; cubeIndex++)
            {
                StackCubeSample sourceSample = source[i].Cubes[cubeIndex];
                copy.Cubes.Add(new StackCubeSample
                {
                    CubeId = sourceSample.CubeId,
                    WorldPosition = sourceSample.WorldPosition,
                    Color = sourceSample.Color,
                    CubeSize = sourceSample.CubeSize
                });
            }

            destination.Add(copy);
        }
    }

    int BuildStackSignatureHash(List<DetectedStack> stacks)
    {
        if (stacks == null || stacks.Count == 0)
        {
            return 0;
        }

        int hash = 17;
        for (int stackIndex = 0; stackIndex < stacks.Count; stackIndex++)
        {
            hash = (hash * 31) + stacks[stackIndex].Cubes.Count;
            for (int cubeIndex = 0; cubeIndex < stacks[stackIndex].Cubes.Count; cubeIndex++)
            {
                hash = (hash * 31) + stacks[stackIndex].Cubes[cubeIndex].CubeId;
            }
        }

        return hash;
    }

    void UpdatePlacementTargetVisual()
    {
        if (!enableMarkerTrackingPipeline || !showPlacementTarget || !isPlacementTaskActive)
        {
            if (placementTargetVisual?.Root != null && placementTargetVisual.Root.gameObject.activeSelf)
            {
                placementTargetVisual.Root.gameObject.SetActive(false);
            }

            return;
        }

        EnsurePlacementTargetInfrastructure();
        RefreshCubeTrackersIfNeeded();

        if (!TryGetPlacementTargetWorldPosition(out Vector3 worldPosition))
        {
            if (placementTargetVisual.Root.gameObject.activeSelf)
            {
                placementTargetVisual.Root.gameObject.SetActive(false);
            }

            return;
        }

        if (!placementTargetVisual.Root.gameObject.activeSelf)
        {
            placementTargetVisual.Root.gameObject.SetActive(true);
        }

        bool bottomCubeCorrect = IsTargetCubeAtPlacementTarget(worldPosition);
        Color statusColor = bottomCubeCorrect ? new Color(0.2f, 1f, 0.3f, 1f) : new Color(1f, 0.25f, 0.25f, 1f);

        placementTargetVisual.Root.position = worldPosition;
        placementTargetVisual.Root.rotation = Quaternion.identity;

        float markerRadius = placementTargetSurfaceSizeMeters * 0.5f;
        placementTargetVisual.SurfaceMarker.localPosition = new Vector3(0f, placementTargetSurfaceLiftMeters, 0f);
        placementTargetVisual.SurfaceMarker.localScale = new Vector3(markerRadius * 2f, placementTargetSurfaceThicknessMeters * 0.5f, markerRadius * 2f);
        SetMaterialColor(placementTargetVisual.SurfaceMaterial, placementTargetCubeColor);

        placementTargetVisual.SymbolRoot.localPosition = new Vector3(0f, placementTargetIndicatorHeightMeters, 0f);
        placementTargetVisual.SymbolRoot.localRotation = Quaternion.identity;
        placementTargetVisual.SymbolRoot.localScale = Vector3.one;

        UpdatePlacementTargetSymbol(bottomCubeCorrect, statusColor);
        UpdatePlacementTaskPreview(worldPosition);
    }

    void RefreshCubeTrackersIfNeeded()
    {
        if (cubeTrackers.Count == 0)
        {
            RefreshCubeTrackers();
        }
    }

    bool TryGetPlacementTargetWorldPosition(out Vector3 worldPosition)
    {
        worldPosition = default;
        if (!TryGetTableOriginPose(out Pose tablePose))
        {
            return false;
        }

        if (!hasPlacementTarget)
        {
            RandomizePlacementTarget();
        }

        if (!hasPlacementTarget)
        {
            return false;
        }

        worldPosition = new Vector3(
            tablePose.position.x + placementTargetOffset.x,
            tablePose.position.y,
            tablePose.position.z + placementTargetOffset.y);
        return true;
    }

    void RandomizePlacementTarget()
    {
        if (placementTaskRequirements.Count <= 0)
        {
            hasPlacementTarget = false;
            placementTargetCubeId = -1;
            return;
        }

        placementTargetCubeId = placementTaskRequirements[0].CubeId;
        placementTargetCubeColor = placementTaskRequirements[0].Color;

        float minRadius = Mathf.Max(0.05f, placementTargetMinRadiusMeters);
        float maxRadius = Mathf.Max(minRadius + 0.01f, placementTargetMaxRadiusMeters);
        float angleRadians = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        float radius = UnityEngine.Random.Range(minRadius, maxRadius);
        placementTargetOffset = new Vector2(Mathf.Cos(angleRadians), Mathf.Sin(angleRadians)) * radius;
        hasPlacementTarget = true;
    }

    bool IsTargetCubeAtPlacementTarget(Vector3 targetWorldPosition)
    {
        if (placementTargetCubeId < 0)
        {
            return false;
        }

        Vector2 targetHorizontal = new Vector2(targetWorldPosition.x, targetWorldPosition.z);
        float matchRadius = Mathf.Max(0.01f, placementTargetMatchRadiusMeters);

        for (int i = 0; i < cubeTrackers.Count; i++)
        {
            ArucoCubeTracker tracker = cubeTrackers[i];
            if (tracker == null || tracker.CubeMarkerId != placementTargetCubeId)
            {
                continue;
            }

            if (!tracker.TryGetCurrentCubeWorldPose(out Pose cubePose))
            {
                continue;
            }

            Vector2 cubeHorizontal = new Vector2(cubePose.position.x, cubePose.position.z);
            if (Vector2.Distance(targetHorizontal, cubeHorizontal) <= matchRadius)
            {
                return true;
            }
        }

        return false;
    }

    bool TryGeneratePlacementTaskRequirements()
    {
        placementTaskRequirements.Clear();

        List<ArucoCubeTracker> validTrackers = new List<ArucoCubeTracker>(cubeTrackers.Count);
        for (int i = 0; i < cubeTrackers.Count; i++)
        {
            if (cubeTrackers[i] != null)
            {
                validTrackers.Add(cubeTrackers[i]);
            }
        }

        if (validTrackers.Count < 3)
        {
            return false;
        }

        for (int i = 0; i < validTrackers.Count; i++)
        {
            int swapIndex = UnityEngine.Random.Range(i, validTrackers.Count);
            ArucoCubeTracker temp = validTrackers[i];
            validTrackers[i] = validTrackers[swapIndex];
            validTrackers[swapIndex] = temp;
        }

        for (int i = 0; i < 3; i++)
        {
            placementTaskRequirements.Add(new PlacementTaskRequirement
            {
                CubeId = validTrackers[i].CubeMarkerId,
                Color = validTrackers[i].CubeColor,
                CubeSize = Mathf.Max(0.001f, validTrackers[i].CubeSizeMeters)
            });
        }

        return true;
    }

    void UpdatePlacementTaskPreview(Vector3 targetWorldPosition)
    {
        if (placementTargetVisual == null || placementTargetVisual.TaskPreviewRoot == null)
        {
            return;
        }

        bool shouldShowPreview = placementTaskRequirements.Count > 0;
        if (placementTargetVisual.TaskPreviewRoot.gameObject.activeSelf != shouldShowPreview)
        {
            placementTargetVisual.TaskPreviewRoot.gameObject.SetActive(shouldShowPreview);
        }

        if (!shouldShowPreview)
        {
            return;
        }

        placementTargetVisual.TaskPreviewRoot.position = targetWorldPosition + placementTaskPreviewLocalOffset;
        placementTargetVisual.TaskPreviewRoot.rotation = Quaternion.identity;
        placementTargetVisual.TaskPreviewRoot.localScale = Vector3.one;

        DetectedStack matchingStack = FindDetectedStackNearPlacementTarget(targetWorldPosition);
        bool isTaskComplete = IsPlacementTaskComplete(matchingStack);

        float cubeSize = Mathf.Max(0.01f, placementTaskPreviewCubeSizeMeters);
        float cubeDepth = cubeSize;
        float gap = Mathf.Max(0f, placementTaskPreviewCubeGapMeters);
        float totalHeight = (placementTaskRequirements.Count * cubeSize) + (Mathf.Max(0, placementTaskRequirements.Count - 1) * gap);
        float statusOffset = Mathf.Max(cubeSize * 0.8f, placementTaskPreviewStatusOffsetMeters);

        for (int i = 0; i < placementTargetVisual.PreviewCubes.Count; i++)
        {
            bool shouldShowCube = i < placementTaskRequirements.Count;
            placementTargetVisual.PreviewCubes[i].gameObject.SetActive(shouldShowCube);
            placementTargetVisual.PreviewStatusRoots[i].gameObject.SetActive(shouldShowCube);

            if (!shouldShowCube)
            {
                continue;
            }

            float y = -totalHeight * 0.5f + (i * (cubeSize + gap)) + (cubeSize * 0.5f);
            placementTargetVisual.PreviewCubes[i].localPosition = new Vector3(0f, y, 0f);
            placementTargetVisual.PreviewCubes[i].localRotation = Quaternion.identity;
            placementTargetVisual.PreviewCubes[i].localScale = new Vector3(cubeSize, cubeSize, cubeDepth);
            SetMaterialColor(placementTargetVisual.PreviewCubeMaterials[i], placementTaskRequirements[i].Color);

            bool isCorrect = matchingStack != null &&
                i < matchingStack.Cubes.Count &&
                matchingStack.Cubes[i].CubeId == placementTaskRequirements[i].CubeId;

            placementTargetVisual.PreviewStatusRoots[i].localPosition = new Vector3(statusOffset, y, 0f);
            placementTargetVisual.PreviewStatusRoots[i].localRotation = Quaternion.identity;
            placementTargetVisual.PreviewStatusRoots[i].localScale = Vector3.one;
            UpdatePreviewStatusSymbol(i, isCorrect);
        }

        if (isTaskComplete && !hasCompletedPlacementTask)
        {
            hasCompletedPlacementTask = true;
            isPlacementTaskActive = false;
            hasPlacementTarget = false;
            StackingTaskCompleted?.Invoke();
        }
    }

    bool IsPlacementTaskComplete(DetectedStack matchingStack)
    {
        if (matchingStack == null || matchingStack.Cubes.Count < placementTaskRequirements.Count)
        {
            return false;
        }

        for (int i = 0; i < placementTaskRequirements.Count; i++)
        {
            if (matchingStack.Cubes[i].CubeId != placementTaskRequirements[i].CubeId)
            {
                return false;
            }
        }

        return placementTaskRequirements.Count > 0;
    }

    DetectedStack FindDetectedStackNearPlacementTarget(Vector3 targetWorldPosition)
    {
        if (detectedStacks == null || detectedStacks.Count == 0)
        {
            return null;
        }

        Vector2 targetHorizontal = new Vector2(targetWorldPosition.x, targetWorldPosition.z);
        float bestDistance = float.PositiveInfinity;
        DetectedStack bestStack = null;

        for (int i = 0; i < detectedStacks.Count; i++)
        {
            DetectedStack stack = detectedStacks[i];
            if (stack == null || stack.Cubes.Count == 0)
            {
                continue;
            }

            Vector2 bottomHorizontal = GetHorizontalCoordinates(stack.Cubes[0].WorldPosition);
            float distance = Vector2.Distance(targetHorizontal, bottomHorizontal);
            if (distance > Mathf.Max(0.01f, placementTargetMatchRadiusMeters))
            {
                continue;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestStack = stack;
            }
        }

        return bestStack;
    }

    void UpdatePreviewStatusSymbol(int requirementIndex, bool showCheck)
    {
        int baseIndex = requirementIndex * 2;
        if (baseIndex + 1 >= placementTargetVisual.PreviewStatusRenderers.Count)
        {
            return;
        }

        Transform statusRoot = placementTargetVisual.PreviewStatusRoots[requirementIndex];
        Transform segmentA = placementTargetVisual.PreviewStatusRenderers[baseIndex].transform;
        Transform segmentB = placementTargetVisual.PreviewStatusRenderers[baseIndex + 1].transform;

        float thickness = Mathf.Max(0.002f, placementTaskPreviewStatusScaleMeters * 0.18f);
        float length = Mathf.Max(0.005f, placementTaskPreviewStatusScaleMeters);
        Color symbolColor = showCheck ? new Color(0.2f, 1f, 0.3f, 1f) : new Color(1f, 0.25f, 0.25f, 1f);

        SetMaterialColor(placementTargetVisual.PreviewStatusMaterials[baseIndex], symbolColor);
        SetMaterialColor(placementTargetVisual.PreviewStatusMaterials[baseIndex + 1], symbolColor);

        if (showCheck)
        {
            segmentA.localPosition = new Vector3(-length * 0.18f, -length * 0.08f, 0f);
            segmentA.localRotation = Quaternion.Euler(0f, 0f, 45f);
            segmentA.localScale = new Vector3(thickness, length * 0.45f, thickness);

            segmentB.localPosition = new Vector3(length * 0.05f, length * 0.08f, 0f);
            segmentB.localRotation = Quaternion.Euler(0f, 0f, -45f);
            segmentB.localScale = new Vector3(thickness, length * 0.9f, thickness);
        }
        else
        {
            segmentA.localPosition = Vector3.zero;
            segmentA.localRotation = Quaternion.Euler(0f, 0f, 45f);
            segmentA.localScale = new Vector3(thickness, length, thickness);

            segmentB.localPosition = Vector3.zero;
            segmentB.localRotation = Quaternion.Euler(0f, 0f, -45f);
            segmentB.localScale = new Vector3(thickness, length, thickness);
        }
    }

    void UpdatePlacementTargetSymbol(bool showCheck, Color symbolColor)
    {
        float thickness = placementTargetIndicatorScaleMeters * 0.18f;
        float length = placementTargetIndicatorScaleMeters;

        for (int i = 0; i < placementTargetVisual.SymbolSegments.Count; i++)
        {
            bool shouldShow = showCheck ? i < 2 : i < 2;
            if (placementTargetVisual.SymbolSegments[i].gameObject.activeSelf != shouldShow)
            {
                placementTargetVisual.SymbolSegments[i].gameObject.SetActive(shouldShow);
            }

            if (shouldShow)
            {
                SetMaterialColor(placementTargetVisual.SymbolMaterials[i], symbolColor);
            }
        }

        if (showCheck)
        {
            placementTargetVisual.SymbolSegments[0].localPosition = new Vector3(-length * 0.18f, -length * 0.08f, 0f);
            placementTargetVisual.SymbolSegments[0].localRotation = Quaternion.Euler(0f, 0f, 45f);
            placementTargetVisual.SymbolSegments[0].localScale = new Vector3(thickness, length * 0.45f, thickness);

            placementTargetVisual.SymbolSegments[1].localPosition = new Vector3(length * 0.05f, length * 0.08f, 0f);
            placementTargetVisual.SymbolSegments[1].localRotation = Quaternion.Euler(0f, 0f, -45f);
            placementTargetVisual.SymbolSegments[1].localScale = new Vector3(thickness, length * 0.9f, thickness);
        }
        else
        {
            placementTargetVisual.SymbolSegments[0].localPosition = Vector3.zero;
            placementTargetVisual.SymbolSegments[0].localRotation = Quaternion.Euler(0f, 0f, 45f);
            placementTargetVisual.SymbolSegments[0].localScale = new Vector3(thickness, length, thickness);

            placementTargetVisual.SymbolSegments[1].localPosition = Vector3.zero;
            placementTargetVisual.SymbolSegments[1].localRotation = Quaternion.Euler(0f, 0f, -45f);
            placementTargetVisual.SymbolSegments[1].localScale = new Vector3(thickness, length, thickness);
        }

        if (placementTargetVisual.SymbolSegments.Count > 2 &&
            placementTargetVisual.SymbolSegments[2].gameObject.activeSelf)
        {
            placementTargetVisual.SymbolSegments[2].gameObject.SetActive(false);
        }
    }

    void SetMaterialColor(Material material, Color color)
    {
        if (material == null)
        {
            return;
        }

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }
        else if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }
    }

    void UpdatePipelineDisabledDiagnosticVisual()
    {
        if (enableMarkerTrackingPipeline)
        {
            if (pipelineDisabledTestCube != null && pipelineDisabledTestCube.activeSelf)
            {
                pipelineDisabledTestCube.SetActive(false);
            }

            return;
        }

        EnsurePipelineDisabledTestCube();
        if (pipelineDisabledTestCube == null)
        {
            return;
        }

        Transform cameraTransform = Camera.main != null ? Camera.main.transform : null;
        if (cameraTransform == null)
        {
            pipelineDisabledTestCube.SetActive(false);
            return;
        }

        if (!pipelineDisabledTestCube.activeSelf)
        {
            pipelineDisabledTestCube.SetActive(true);
        }

        pipelineDisabledTestCube.transform.position = cameraTransform.position + (cameraTransform.forward * Mathf.Max(0.05f, pipelineDisabledTestCubeDistanceMeters));
        pipelineDisabledTestCube.transform.rotation = Quaternion.identity;
        pipelineDisabledTestCube.transform.localScale = Vector3.one * Mathf.Max(0.02f, pipelineDisabledTestCubeSizeMeters);
    }

    void EnsurePipelineDisabledTestCube()
    {
        if (pipelineDisabledTestCube != null)
        {
            return;
        }

        pipelineDisabledTestCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        pipelineDisabledTestCube.name = "PipelineDisabledTestCube";

        Collider cubeCollider = pipelineDisabledTestCube.GetComponent<Collider>();
        if (cubeCollider != null)
        {
            cubeCollider.enabled = false;
        }

        Renderer cubeRenderer = pipelineDisabledTestCube.GetComponent<Renderer>();
        cubeRenderer.shadowCastingMode = ShadowCastingMode.Off;
        cubeRenderer.receiveShadows = false;

        pipelineDisabledTestCubeMaterial = CreateHudCellMaterial();
        SetMaterialColor(pipelineDisabledTestCubeMaterial, new Color(0.15f, 0.85f, 1f, 1f));
        cubeRenderer.sharedMaterial = pipelineDisabledTestCubeMaterial;

        pipelineDisabledTestCube.SetActive(false);
    }

    int BuildMaskForStack(DetectedStack stack)
    {
        int mask = 0;
        for (int cubeIndex = 0; cubeIndex < stack.Cubes.Count; cubeIndex++)
        {
            for (int sampleIndex = 0; sampleIndex < stackCubeSamples.Count; sampleIndex++)
            {
                if (stackCubeSamples[sampleIndex].CubeId != stack.Cubes[cubeIndex].CubeId)
                {
                    continue;
                }

                mask |= 1 << sampleIndex;
                break;
            }
        }

        return mask;
    }

    int CountBits(int value)
    {
        int count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }

        return count;
    }

    StackHudVisual CreateStackHudVisual(int index)
    {
        GameObject rootObject = new GameObject($"DetectedStack_{index + 1}");
        Transform rootTransform = rootObject.transform;
        rootTransform.SetParent(stackHudRoot, false);

        StackHudVisual visual = new StackHudVisual
        {
            Root = rootTransform
        };

        for (int cellIndex = 0; cellIndex < 4; cellIndex++)
        {
            GameObject cell = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cell.name = $"Cell_{cellIndex + 1}";
            cell.transform.SetParent(rootTransform, false);
            cell.transform.localRotation = Quaternion.identity;
            cell.transform.localScale = new Vector3(stackHudCellSize, stackHudCellSize, stackHudCellDepth);

            Collider collider = cell.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }

            Renderer renderer = cell.GetComponent<Renderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Material material = CreateHudCellMaterial();
            renderer.sharedMaterial = material;

            visual.Cells.Add(cell);
            visual.CellRenderers.Add(renderer);
            visual.CellMaterials.Add(material);
        }

        rootObject.SetActive(false);
        return visual;
    }

    Material CreateHudCellMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            shader = Shader.Find("Unlit/Color");
        }

        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }

        Material material = new Material(shader);
        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 0f);
        }

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", Color.white);
        }
        else if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", Color.white);
        }

        return material;
    }

    void UpdateStackHudVisual(StackHudVisual visual, DetectedStack stack, int stackIndex)
    {
        int visibleCellCount = Mathf.Min(visual.Cells.Count, stack.Cubes.Count);
        float totalHeight = (visibleCellCount * stackHudCellSize) + (Mathf.Max(0, visibleCellCount - 1) * stackHudCellGap);
        float leftOffset = -((Mathf.Max(1, detectedStacks.Count) - 1) * stackHudStackSpacing * 0.5f);
        visual.Root.localPosition = new Vector3(leftOffset + (stackIndex * stackHudStackSpacing), 0f, 0f);

        for (int cellIndex = 0; cellIndex < visual.Cells.Count; cellIndex++)
        {
            bool shouldShow = cellIndex < stack.Cubes.Count;
            GameObject cell = visual.Cells[cellIndex];
            if (cell.activeSelf != shouldShow)
            {
                cell.SetActive(shouldShow);
            }

            if (!shouldShow)
            {
                continue;
            }

            float y = -totalHeight * 0.5f + (cellIndex * (stackHudCellSize + stackHudCellGap)) + (stackHudCellSize * 0.5f);
            cell.transform.localPosition = new Vector3(0f, y, 0f);
            cell.transform.localScale = new Vector3(stackHudCellSize, stackHudCellSize, stackHudCellDepth);

            Material material = visual.CellMaterials[cellIndex];
            Color color = stack.Cubes[cellIndex].Color;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            else if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
        }
    }

    void Start()
    {
        // These are serialized fields, so scene instances keep old values until explicitly changed.
        // Force the runtime configuration we want instead of relying on stale Inspector values.
        flipDisplayHorizontally = true;
        flipDisplayVertically = true;
        updateCameraFeedTexture = false;
        drawWorldMarkers = false;

        if (!Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
        {
            Permission.RequestUserPermission("horizonos.permission.HEADSET_CAMERA");
        }

        arucoClass = new AndroidJavaClass("com.GeorgiaTech.aruco.ArucoBridge");
        EnsureStackHudInfrastructure();
        RefreshCubeTrackers();
        EnsurePlacementTargetInfrastructure();
        ApplyTextureOrientation();
        UpdatePipelineDisabledDiagnosticVisual();
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

        if (pipelineDisabledTestCubeMaterial != null)
        {
            Destroy(pipelineDisabledTestCubeMaterial);
        }

        if (pipelineDisabledTestCube != null)
        {
            Destroy(pipelineDisabledTestCube);
        }

        DestroyMarkerVisualSet(markerVisuals);
        DestroyMarkerVisualSet(worldMarkerVisuals);
        DestroyStackHudVisuals();
        DestroyPlacementTargetVisual();
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
        ApplyPendingTrackingResultIfAvailable();
        UpdatePipelineDisabledDiagnosticVisual();

        if (!enableMarkerTrackingPipeline)
        {
            isProcessing = false;
            return;
        }

        if (isProcessing || Volatile.Read(ref trackingWorkerRunning) != 0 || cameraAccess == null)
        {
            return;
        }

        if (Time.unscaledTime < trackingCooldownUntilTime)
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
        UpdateStackHudTransform();
        UpdateStackHudVisuals();
        UpdatePlacementTargetVisual();
    }

    void OnReadbackComplete(AsyncGPUReadbackRequest request, int width, int height, Pose cameraPose, Vector2Int currentResolution, PassthroughCameraAccess.CameraIntrinsics cameraIntrinsics)
    {
        if (!enableMarkerTrackingPipeline)
        {
            isProcessing = false;
            return;
        }

        if (request.hasError)
        {
            Debug.LogError("GPU Readback Error");
            isProcessing = false;
            return;
        }

        var rawData = request.GetData<byte>();
        int byteCount = rawData.Length;
        if (readbackBytes == null || readbackBytes.Length != byteCount)
        {
            readbackBytes = new byte[byteCount];
        }

        rawData.CopyTo(readbackBytes);

        bool needsDebugTexture = updateCameraFeedTexture && targetRenderer != null;
        bool needsDetailedMarkerData = drawDebugOverlay && needsDebugTexture;
        Vector2 focalLength = default;
        Vector2 principalPoint = default;
        bool shouldEstimateMarkerPoses = estimateMarkerPoses &&
            TryGetProcessingIntrinsics(width, height, currentResolution, cameraIntrinsics, out focalLength, out principalPoint);

        byte[] frameBytes = readbackBytes;
        Interlocked.Exchange(ref trackingWorkerRunning, 1);

        Task.Run(() =>
        {
            ProcessTrackingFrameOnWorker(
                frameBytes,
                width,
                height,
                cameraPose,
                needsDebugTexture,
                needsDetailedMarkerData,
                shouldEstimateMarkerPoses,
                focalLength,
                principalPoint);
        });
    }

    void ProcessTrackingFrameOnWorker(byte[] rgbaBytes, int width, int height, Pose cameraPose, bool needsDebugTexture, bool needsDetailedMarkerData, bool shouldEstimateMarkerPoses, Vector2 focalLength, Vector2 principalPoint)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        byte[] workerGrayBytes = null;

        try
        {
            workerGrayBytes = grayBytes;
            int pixelCount = width * height;
            if (workerGrayBytes == null || workerGrayBytes.Length != pixelCount)
            {
                workerGrayBytes = new byte[pixelCount];
            }

            for (int y = 0; y < height; y++)
            {
                int detectionY = (height - 1) - y;
                int rawRowIndex = y * width * 4;
                int detectionRowIndex = detectionY * width;

                for (int x = 0; x < width; x++)
                {
                    int rawIndex = rawRowIndex + (x * 4);
                    workerGrayBytes[detectionRowIndex + x] = (byte)((rgbaBytes[rawIndex] + rgbaBytes[rawIndex + 1] + rgbaBytes[rawIndex + 2]) / 3);
                }
            }

            AttachCurrentThreadForAndroid();

            float[] markerData = needsDetailedMarkerData
                ? DetectMarkersDetailed(workerGrayBytes, width, height)
                : Array.Empty<float>();

            float[] markerPoseData = shouldEstimateMarkerPoses
                ? DetectMarkerPoses(workerGrayBytes, width, height, focalLength.x, focalLength.y, principalPoint.x, principalPoint.y)
                : Array.Empty<float>();

            stopwatch.Stop();
            TrackingPipelineResult result = new TrackingPipelineResult
            {
                Width = width,
                Height = height,
                CameraPose = cameraPose,
                GrayBytes = workerGrayBytes,
                MarkerData = markerData,
                MarkerPoseData = markerPoseData,
                NeedsDebugTexture = needsDebugTexture,
                NeedsDetailedMarkerData = needsDetailedMarkerData,
                ProcessingMilliseconds = stopwatch.Elapsed.TotalMilliseconds
            };

            lock (trackingResultLock)
            {
                pendingTrackingResult = result;
                hasPendingTrackingResult = true;
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"Marker tracking worker failed: {exception}");
            lock (trackingResultLock)
            {
                pendingTrackingResult = null;
                hasPendingTrackingResult = true;
            }
        }
        finally
        {
            Interlocked.Exchange(ref trackingWorkerRunning, 0);
        }
    }

    void AttachCurrentThreadForAndroid()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJNI.AttachCurrentThread();
#endif
    }

    void ApplyPendingTrackingResultIfAvailable()
    {
        TrackingPipelineResult result = null;
        lock (trackingResultLock)
        {
            if (!hasPendingTrackingResult)
            {
                return;
            }

            result = pendingTrackingResult;
            pendingTrackingResult = null;
            hasPendingTrackingResult = false;
        }

        if (result == null)
        {
            isProcessing = false;
            return;
        }

        if (!enableMarkerTrackingPipeline)
        {
            isProcessing = false;
            return;
        }

        grayBytes = result.GrayBytes;
        lastProcessedCameraPose = result.CameraPose;
        hasLastProcessedCameraPose = true;

        UpdateTrackingThrottle(result.ProcessingMilliseconds);

        if (result.NeedsDetailedMarkerData)
        {
            ParseMarkerData(result.MarkerData, markerDetections);
        }
        else
        {
            markerDetections.Clear();
        }

        ParseMarkerPoseData(result.MarkerPoseData, markerPoseDetections);

        if (result.NeedsDebugTexture)
        {
            UpdateDebugTextureFromGrayBytes(result.Width, result.Height);
        }

        if (result.NeedsDetailedMarkerData && markerDetections.Count > 0 && debugPixels != null)
        {
            DrawMarkerOverlayIntoTexture(result.Width, result.Height);
        }

        if (debugTexture != null && debugPixels != null)
        {
            BindDebugTexture();
            debugTexture.SetPixels32(debugPixels);
            debugTexture.Apply();
        }

        if (result.NeedsDebugTexture)
        {
            UpdateMarkerVisuals(result.Width, result.Height);
        }
        else
        {
            HideInactiveVisuals(markerVisuals);
        }

        UpdateTrackedTableOrigin(result.CameraPose);
        if (drawWorldMarkers)
        {
            UpdateWorldMarkerVisuals(result.CameraPose);
        }
        else
        {
            HideInactiveVisuals(worldMarkerVisuals);
        }

        isProcessing = false;
    }

    void UpdateDebugTextureFromGrayBytes(int width, int height)
    {
        int pixelCount = width * height;
        if (debugPixels == null || debugPixels.Length != pixelCount)
        {
            debugPixels = new Color32[pixelCount];
        }

        if (debugTexture == null || debugTexture.width != width || debugTexture.height != height)
        {
            if (debugTexture != null)
            {
                Destroy(debugTexture);
            }

            debugTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            BindDebugTexture();
        }

        for (int y = 0; y < height; y++)
        {
            int displayY = flipDisplayVertically ? (height - 1) - y : y;
            int detectionY = (height - 1) - y;

            for (int x = 0; x < width; x++)
            {
                int displayX = flipDisplayHorizontally ? (width - 1) - x : x;
                byte grayValue = grayBytes[(detectionY * width) + x];
                debugPixels[(displayY * width) + displayX] = new Color32(grayValue, grayValue, grayValue, 255);
            }
        }
    }

    void UpdateTrackingThrottle(double processingMilliseconds)
    {
        float targetFrameMilliseconds = 1000f / Mathf.Max(45f, Application.targetFrameRate > 0 ? Application.targetFrameRate : 72f);
        if (processingMilliseconds > targetFrameMilliseconds)
        {
            consecutiveSlowTrackingFrames++;
        }
        else
        {
            consecutiveSlowTrackingFrames = Mathf.Max(0, consecutiveSlowTrackingFrames - 1);
        }

        if (consecutiveSlowTrackingFrames >= 2)
        {
            float cooldownSeconds = Mathf.Clamp((float)(processingMilliseconds / 1000.0), 0.01f, 0.08f);
            trackingCooldownUntilTime = Time.unscaledTime + cooldownSeconds;
            consecutiveSlowTrackingFrames = 0;
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
        bool foundTableMarker = TryGetBestTableMarkerPose(cameraPose, out Vector3 worldPosition, out Quaternion worldRotation);
        if (foundTableMarker)
        {
            ApplyTrackedTablePose(worldPosition, worldRotation);
        }

        if (!foundTableMarker)
        {
            float timeSinceSeen = Time.time - tableOriginPose.LastSeenTime;
            tableOriginPose.IsTracked = tableOriginPose.HasPose && timeSinceSeen <= tablePoseHoldSeconds;
        }

        if (tableOriginTransform != null && tableOriginPose.HasPose)
        {
            if (applyTrackedTableRotationToAnchor)
            {
                tableOriginTransform.SetPositionAndRotation(tableOriginPose.Position, tableOriginPose.Rotation);
            }
            else
            {
                tableOriginTransform.position = tableOriginPose.Position;
            }
        }
    }

    void UpdateFpsCounterVisual()
    {
        if (stackHudRoot == null)
        {
            return;
        }

        EnsureFpsCounterInfrastructure();
        if (fpsCounterRoot == null || fpsCounterText == null)
        {
            return;
        }

        bool shouldShow = showStackHud && showFpsCounter && stackHudRoot.gameObject.activeInHierarchy;
        if (fpsCounterRoot.gameObject.activeSelf != shouldShow)
        {
            fpsCounterRoot.gameObject.SetActive(shouldShow);
        }

        if (!shouldShow)
        {
            return;
        }

        fpsCounterRoot.localPosition = new Vector3(fpsCounterLocalOffset.x, fpsCounterLocalOffset.y, -0.02f);
        fpsCounterRoot.localRotation = Quaternion.identity;
        fpsCounterRoot.localScale = Vector3.one * Mathf.Clamp(fpsCounterTextScale, 0.1f, 0.35f);

        float deltaTime = Time.unscaledDeltaTime;
        if (deltaTime > 0f)
        {
            fpsSampleDurations.Enqueue(deltaTime);
            fpsSampleDurationSum += deltaTime;

            float targetWindow = Mathf.Max(0.25f, fpsAverageWindowSeconds);
            while (fpsSampleDurationSum > targetWindow && fpsSampleDurations.Count > 1)
            {
                fpsSampleDurationSum -= fpsSampleDurations.Dequeue();
            }
        }

        if ((Time.unscaledTime - lastFpsCounterUpdateTime) < Mathf.Max(0.05f, fpsCounterUpdateIntervalSeconds))
        {
            return;
        }

        lastFpsCounterUpdateTime = Time.unscaledTime;
        float averageFps = fpsSampleDurationSum > 0f
            ? fpsSampleDurations.Count / fpsSampleDurationSum
            : 0f;
        fpsCounterText.text = $"AVG FPS {averageFps:0.0}";

        if (averageFps >= 70f)
        {
            fpsCounterText.color = new Color(0.45f, 1f, 0.45f, 1f);
        }
        else if (averageFps >= 50f)
        {
            fpsCounterText.color = new Color(1f, 0.9f, 0.3f, 1f);
        }
        else
        {
            fpsCounterText.color = new Color(1f, 0.45f, 0.45f, 1f);
        }
    }

    bool TryGetBestTableMarkerPose(Pose cameraPose, out Vector3 worldPosition, out Quaternion worldRotation)
    {
        worldPosition = default;
        worldRotation = Quaternion.identity;

        bool foundCandidate = false;
        float bestScore = float.PositiveInfinity;

        for (int i = 0; i < markerPoseDetections.Count; i++)
        {
            MarkerPoseDetection detection = markerPoseDetections[i];
            if (detection.Id != tableCenterMarkerId)
            {
                continue;
            }

            if (!TryConvertMarkerPoseToWorldPose(detection, cameraPose, out Vector3 candidatePosition, out Quaternion candidateRotation))
            {
                continue;
            }

            float score = 0f;
            if (tableOriginPose.HasPose)
            {
                score += Vector3.Distance(tableOriginPose.Position, candidatePosition);
                score += Quaternion.Angle(tableOriginPose.Rotation, candidateRotation) * 0.0025f;
            }

            if (!foundCandidate || score < bestScore)
            {
                foundCandidate = true;
                bestScore = score;
                worldPosition = candidatePosition;
                worldRotation = candidateRotation;
            }
        }

        return foundCandidate;
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

        bool measurementIsWithinDeadband =
            positionDelta <= tablePositionDeadbandMeters &&
            rotationDelta <= tableRotationDeadbandDegrees;
        if (measurementIsWithinDeadband)
        {
            tableOriginPose.IsTracked = true;
            tableOriginPose.LastSeenTime = Time.time;
            return;
        }

        if (tablePoseSmoothing <= 0f)
        {
            tableOriginPose.Position = worldPosition;
            tableOriginPose.Rotation = worldRotation;
        }
        else
        {
            float blend = 1f - Mathf.Exp(-tablePoseSmoothing * Time.deltaTime);
            Vector3 blendedPosition = Vector3.Lerp(tableOriginPose.Position, worldPosition, blend);
            Quaternion blendedRotation = Quaternion.Slerp(tableOriginPose.Rotation, worldRotation, blend);

            if (tableMaxPositionStepPerUpdateMeters > 0f)
            {
                blendedPosition = Vector3.MoveTowards(
                    tableOriginPose.Position,
                    blendedPosition,
                    tableMaxPositionStepPerUpdateMeters);
            }

            if (tableMaxRotationStepPerUpdateDegrees > 0f)
            {
                blendedRotation = Quaternion.RotateTowards(
                    tableOriginPose.Rotation,
                    blendedRotation,
                    tableMaxRotationStepPerUpdateDegrees);
            }

            tableOriginPose.Position = blendedPosition;
            tableOriginPose.Rotation = blendedRotation;
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
