using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

[DisallowMultipleComponent]
public class ArucoCubeTracker : MonoBehaviour
{
    [Serializable]
    struct FaceTemplate
    {
        public string Name;
        public Quaternion MarkerRotationLocalToCube;

        public FaceTemplate(string name, Quaternion markerRotationLocalToCube)
        {
            Name = name;
            MarkerRotationLocalToCube = markerRotationLocalToCube;
        }
    }

    struct CubeCandidate
    {
        public int FaceIndex;
        public string FaceName;
        public Vector3 Position;
        public Quaternion Rotation;
        public int SupportCount;
        public float Score;
    }

    class TrackedCubePose
    {
        public bool HasPose;
        public bool IsTracked;
        public bool IsVisible;
        public float LastSeenTime;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
        public int ConsecutiveOutlierFrames;
    }

    static readonly FaceTemplate[] FaceTemplates =
    {
        new FaceTemplate("Front", Quaternion.LookRotation(Vector3.back, Vector3.up)),
        new FaceTemplate("Back", Quaternion.LookRotation(Vector3.forward, Vector3.up)),
        new FaceTemplate("Left", Quaternion.LookRotation(Vector3.right, Vector3.up)),
        new FaceTemplate("Right", Quaternion.LookRotation(Vector3.left, Vector3.up)),
        new FaceTemplate("Top", Quaternion.LookRotation(Vector3.down, Vector3.forward)),
        new FaceTemplate("Bottom", Quaternion.LookRotation(Vector3.up, Vector3.forward))
    };

    public CameraFeedViewer markerTracker;
    public string cubeLabel = "Cube 1";
    public int cubeMarkerId = 1;
    [Min(0.001f)] public float cubeSizeMeters = 0.0762f;
    [Min(0f)] public float poseHoldSeconds = 0.75f;
    [Range(0f, 30f)] public float poseSmoothing = 12f;
    [Min(0f)] public float maxPositionJumpMeters = 0.08f;
    [Range(0f, 180f)] public float maxRotationJumpDegrees = 55f;
    [Range(1, 5)] public int outlierFramesBeforeSnap = 2;
    [Min(0.001f)] public float consensusPositionToleranceMeters = 0.025f;
    [Range(0f, 90f)] public float consensusRotationToleranceDegrees = 20f;
    [Min(0f)] public float maxPositionStepPerUpdateMeters = 0.03f;
    [Range(0f, 180f)] public float maxRotationStepPerUpdateDegrees = 18f;
    [Min(0f)] public float relocalizationSnapDistanceMeters = 0.2f;
    [Range(0f, 180f)] public float relocalizationSnapRotationDegrees = 75f;
    public bool onlyUpdateWhenHandNear = true;
    [Min(0.01f)] public float handNearDistanceMeters = 0.18f;
    [Min(0.01f)] public float handFarDistanceMeters = 0.26f;
    public Transform primaryHandTransform;
    public Transform secondaryHandTransform;
    public bool requireConfirmationForSingleMarkerFaceSwitch = true;
    [Range(1, 8)] public int singleMarkerFaceSwitchConfirmationFrames = 3;
    public bool requireConfirmationForSingleMarkerLargeRotation = true;
    [Range(1f, 90f)] public float singleMarkerMaxTrustedRotationDeltaDegrees = 20f;
    [Range(1, 8)] public int singleMarkerLargeRotationConfirmationFrames = 4;
    [Range(1f, 45f)] public float singleMarkerRotationConfirmationToleranceDegrees = 10f;
    [Min(0f)] public float stationaryPositionDeadbandMeters = 0.0025f;
    [Range(0f, 45f)] public float stationaryRotationDeadbandDegrees = 2f;
    public bool createRuntimeVisual = true;
    public Transform cubeVisualTransform;
    public Material cubeMaterial;
    public Color cubeColor = Color.red;
    public bool showLabel = false;

    readonly List<Pose> markerPoseBuffer = new List<Pose>(6);
    readonly List<CubeCandidate> candidateBuffer = new List<CubeCandidate>(36);
    readonly TrackedCubePose trackedPose = new TrackedCubePose();

    Transform runtimeVisualRoot;
    TextMeshPro runtimeLabel;
    int lastResolvedFaceIndex = -1;
    int pendingSingleMarkerFaceIndex = -1;
    int pendingSingleMarkerFaceFrames;
    bool hasPendingSingleMarkerRotation;
    Quaternion pendingSingleMarkerRotation = Quaternion.identity;
    int pendingSingleMarkerRotationFrames;
    bool handWasNearLastUpdate;
    XRHandJointVisualizer handVisualizer;

    public bool TryGetCubeWorldPose(out Pose pose)
    {
        pose = default;
        if (!trackedPose.HasPose)
        {
            return false;
        }

        pose = new Pose(trackedPose.Position, trackedPose.Rotation);
        return true;
    }

    public bool IsCubeCurrentlyTracked => trackedPose.IsTracked;
    public bool HasCubePose => trackedPose.HasPose;
    public string CubeLabel => cubeLabel;
    public Color CubeColor => cubeColor;
    public float CubeSizeMeters => cubeSizeMeters;
    public int CubeMarkerId => cubeMarkerId;

    public bool TryGetCubePoseRelativeToTable(out Pose pose)
    {
        pose = default;
        if (!trackedPose.HasPose || markerTracker == null || !markerTracker.TryGetTableOriginPose(out Pose tablePose))
        {
            return false;
        }

        Vector3 localPosition = Quaternion.Inverse(tablePose.rotation) * (trackedPose.Position - tablePose.position);
        Quaternion localRotation = Quaternion.Inverse(tablePose.rotation) * trackedPose.Rotation;
        pose = new Pose(localPosition, localRotation);
        return true;
    }

    public bool TryGetCurrentCubeWorldPose(out Pose pose)
    {
        pose = default;
        if (!trackedPose.HasPose || !trackedPose.IsTracked)
        {
            return false;
        }

        pose = new Pose(trackedPose.Position, trackedPose.Rotation);
        return true;
    }

    public bool TryGetCurrentCubePoseRelativeToTable(out Pose pose)
    {
        pose = default;
        if (!trackedPose.IsTracked)
        {
            return false;
        }

        return TryGetCubePoseRelativeToTable(out pose);
    }

    void Update()
    {
        if (markerTracker == null)
        {
            markerTracker = FindFirstObjectByType<CameraFeedViewer>();
            if (markerTracker == null)
            {
                return;
            }
        }

        int markerPoseCount = markerTracker.GetLatestMarkerWorldPoses(cubeMarkerId, markerPoseBuffer);
        if (markerPoseCount <= 0)
        {
            trackedPose.IsTracked = false;
            trackedPose.IsVisible = trackedPose.HasPose;
            UpdateVisual();
            return;
        }

        if (!TryResolveCubePose(markerPoseBuffer, out Vector3 worldPosition, out Quaternion worldRotation, out int visibleMarkerCount))
        {
            trackedPose.IsTracked = false;
            trackedPose.IsVisible = trackedPose.HasPose;
            UpdateVisual();
            return;
        }

        if (!ShouldAcceptMeasurement(worldPosition))
        {
            trackedPose.IsTracked = false;
            trackedPose.IsVisible = trackedPose.HasPose;
            UpdateVisual();
            return;
        }

        ApplyTrackedPose(worldPosition, worldRotation, visibleMarkerCount);
        UpdateVisual();
    }

    bool ShouldAcceptMeasurement(Vector3 candidateWorldPosition)
    {
        if (!onlyUpdateWhenHandNear)
        {
            return true;
        }

        if (!trackedPose.HasPose)
        {
            return true;
        }

        EnsureHandReferences();

        if (primaryHandTransform == null && secondaryHandTransform == null)
        {
            return true;
        }

        float distanceThreshold = handWasNearLastUpdate
            ? Mathf.Max(handNearDistanceMeters, handFarDistanceMeters)
            : handNearDistanceMeters;
        Vector3 referencePosition = trackedPose.Position;

        bool isNear = IsHandNear(referencePosition, primaryHandTransform, distanceThreshold) ||
            IsHandNear(referencePosition, secondaryHandTransform, distanceThreshold) ||
            IsHandNear(candidateWorldPosition, primaryHandTransform, distanceThreshold) ||
            IsHandNear(candidateWorldPosition, secondaryHandTransform, distanceThreshold);

        handWasNearLastUpdate = isNear;
        return isNear;
    }

    bool IsHandNear(Vector3 cubePosition, Transform handTransform, float distanceThreshold)
    {
        if (handTransform == null)
        {
            return false;
        }

        return Vector3.Distance(cubePosition, handTransform.position) <= distanceThreshold;
    }

    void EnsureHandReferences()
    {
        if (primaryHandTransform != null || secondaryHandTransform != null)
        {
            return;
        }

        if (handVisualizer == null)
        {
            handVisualizer = FindFirstObjectByType<XRHandJointVisualizer>();
        }

        if (handVisualizer == null)
        {
            return;
        }

        if (primaryHandTransform == null && handVisualizer.Hand != null)
        {
            primaryHandTransform = handVisualizer.Hand.transform;
        }

        if (secondaryHandTransform == null && handVisualizer.leftHand != null)
        {
            secondaryHandTransform = handVisualizer.leftHand.transform;
        }
    }

    bool TryResolveCubePose(List<Pose> markerPoses, out Vector3 worldPosition, out Quaternion worldRotation, out int visibleMarkerCount)
    {
        worldPosition = default;
        worldRotation = Quaternion.identity;
        visibleMarkerCount = markerPoses != null ? markerPoses.Count : 0;

        candidateBuffer.Clear();
        float halfCube = cubeSizeMeters * 0.5f;

        for (int markerIndex = 0; markerIndex < markerPoses.Count; markerIndex++)
        {
            Pose markerPose = markerPoses[markerIndex];
            Vector3 candidatePosition = markerPose.position + (markerPose.rotation * (Vector3.forward * halfCube));

            for (int faceIndex = 0; faceIndex < FaceTemplates.Length; faceIndex++)
            {
                FaceTemplate face = FaceTemplates[faceIndex];
                Quaternion candidateRotation = markerPose.rotation * Quaternion.Inverse(face.MarkerRotationLocalToCube);
                candidateBuffer.Add(new CubeCandidate
                {
                    FaceIndex = faceIndex,
                    FaceName = face.Name,
                    Position = candidatePosition,
                    Rotation = candidateRotation,
                    SupportCount = 1,
                    Score = 0f
                });
            }
        }

        if (candidateBuffer.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < candidateBuffer.Count; i++)
        {
            CubeCandidate candidate = candidateBuffer[i];
            int supportCount = 1;

            for (int j = 0; j < candidateBuffer.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                CubeCandidate other = candidateBuffer[j];
                if (Vector3.Distance(candidate.Position, other.Position) > consensusPositionToleranceMeters)
                {
                    continue;
                }

                if (Quaternion.Angle(candidate.Rotation, other.Rotation) > consensusRotationToleranceDegrees)
                {
                    continue;
                }

                supportCount++;
            }

            float positionPenalty = trackedPose.HasPose ? Vector3.Distance(trackedPose.Position, candidate.Position) : 0f;
            float rotationPenalty = trackedPose.HasPose ? Quaternion.Angle(trackedPose.Rotation, candidate.Rotation) * 0.02f : 0f;
            float continuityBonus = candidate.FaceIndex == lastResolvedFaceIndex ? 3f : 0f;
            float singleMarkerBonus = markerPoses.Count == 1 && candidate.FaceIndex == lastResolvedFaceIndex ? 8f : 0f;

            candidate.SupportCount = supportCount;
            candidate.Score = supportCount * 10f - positionPenalty - rotationPenalty + continuityBonus + singleMarkerBonus;
            candidateBuffer[i] = candidate;
        }

        CubeCandidate bestCandidate = candidateBuffer[0];
        for (int i = 1; i < candidateBuffer.Count; i++)
        {
            CubeCandidate candidate = candidateBuffer[i];
            if (candidate.Score > bestCandidate.Score)
            {
                bestCandidate = candidate;
                continue;
            }

            if (Mathf.Approximately(candidate.Score, bestCandidate.Score) &&
                Quaternion.Angle(candidate.Rotation, trackedPose.Rotation) < Quaternion.Angle(bestCandidate.Rotation, trackedPose.Rotation))
            {
                bestCandidate = candidate;
            }
        }

        Vector3 accumulatedPosition = Vector3.zero;
        Quaternion accumulatedRotation = bestCandidate.Rotation;
        int accumulatedCount = 0;

        for (int i = 0; i < candidateBuffer.Count; i++)
        {
            CubeCandidate candidate = candidateBuffer[i];
            if (Vector3.Distance(candidate.Position, bestCandidate.Position) > consensusPositionToleranceMeters)
            {
                continue;
            }

            if (Quaternion.Angle(candidate.Rotation, bestCandidate.Rotation) > consensusRotationToleranceDegrees)
            {
                continue;
            }

            accumulatedPosition += candidate.Position;
            accumulatedRotation = AverageRotation(accumulatedRotation, candidate.Rotation, accumulatedCount + 1);
            accumulatedCount++;
        }

        if (accumulatedCount <= 0)
        {
            return false;
        }

        worldPosition = accumulatedPosition / accumulatedCount;
        worldRotation = accumulatedRotation;

        if (!ShouldAcceptResolvedFace(markerPoses.Count, bestCandidate.FaceIndex))
        {
            return false;
        }

        worldRotation = StabilizeSingleMarkerRotation(markerPoses.Count, worldRotation);
        lastResolvedFaceIndex = bestCandidate.FaceIndex;
        return true;
    }

    bool ShouldAcceptResolvedFace(int visibleMarkerCount, int resolvedFaceIndex)
    {
        if (!trackedPose.HasPose)
        {
            pendingSingleMarkerFaceIndex = -1;
            pendingSingleMarkerFaceFrames = 0;
            ResetPendingSingleMarkerRotation();
            return true;
        }

        if (!requireConfirmationForSingleMarkerFaceSwitch || visibleMarkerCount != 1 || lastResolvedFaceIndex < 0)
        {
            pendingSingleMarkerFaceIndex = -1;
            pendingSingleMarkerFaceFrames = 0;
            ResetPendingSingleMarkerRotation();
            return true;
        }

        if (resolvedFaceIndex == lastResolvedFaceIndex)
        {
            pendingSingleMarkerFaceIndex = -1;
            pendingSingleMarkerFaceFrames = 0;
            return true;
        }

        if (pendingSingleMarkerFaceIndex != resolvedFaceIndex)
        {
            pendingSingleMarkerFaceIndex = resolvedFaceIndex;
            pendingSingleMarkerFaceFrames = 1;
            return false;
        }

        pendingSingleMarkerFaceFrames++;
        if (pendingSingleMarkerFaceFrames < Mathf.Max(1, singleMarkerFaceSwitchConfirmationFrames))
        {
            return false;
        }

        pendingSingleMarkerFaceIndex = -1;
        pendingSingleMarkerFaceFrames = 0;
        return true;
    }

    Quaternion StabilizeSingleMarkerRotation(int visibleMarkerCount, Quaternion candidateRotation)
    {
        if (!requireConfirmationForSingleMarkerLargeRotation ||
            visibleMarkerCount != 1 ||
            !trackedPose.HasPose)
        {
            ResetPendingSingleMarkerRotation();
            return candidateRotation;
        }

        float rotationDelta = Quaternion.Angle(trackedPose.Rotation, candidateRotation);
        if (rotationDelta <= singleMarkerMaxTrustedRotationDeltaDegrees)
        {
            ResetPendingSingleMarkerRotation();
            return candidateRotation;
        }

        if (!hasPendingSingleMarkerRotation ||
            Quaternion.Angle(pendingSingleMarkerRotation, candidateRotation) > singleMarkerRotationConfirmationToleranceDegrees)
        {
            pendingSingleMarkerRotation = candidateRotation;
            pendingSingleMarkerRotationFrames = 1;
            hasPendingSingleMarkerRotation = true;
            return trackedPose.Rotation;
        }

        pendingSingleMarkerRotationFrames++;
        if (pendingSingleMarkerRotationFrames < Mathf.Max(1, singleMarkerLargeRotationConfirmationFrames))
        {
            return trackedPose.Rotation;
        }

        Quaternion acceptedRotation = pendingSingleMarkerRotation;
        ResetPendingSingleMarkerRotation();
        return acceptedRotation;
    }

    void ResetPendingSingleMarkerRotation()
    {
        hasPendingSingleMarkerRotation = false;
        pendingSingleMarkerRotation = Quaternion.identity;
        pendingSingleMarkerRotationFrames = 0;
    }

    Quaternion AverageRotation(Quaternion currentAverage, Quaternion nextRotation, int sampleCount)
    {
        if (sampleCount <= 1)
        {
            return nextRotation;
        }

        float dot = Quaternion.Dot(currentAverage, nextRotation);
        if (dot < 0f)
        {
            nextRotation = new Quaternion(-nextRotation.x, -nextRotation.y, -nextRotation.z, -nextRotation.w);
        }

        float blend = 1f / sampleCount;
        return Quaternion.Slerp(currentAverage, nextRotation, blend);
    }

    void ApplyTrackedPose(Vector3 worldPosition, Quaternion worldRotation, int visibleMarkerCount)
    {
        if (!trackedPose.HasPose)
        {
            trackedPose.Position = worldPosition;
            trackedPose.Rotation = worldRotation;
            trackedPose.HasPose = true;
            trackedPose.IsTracked = true;
            trackedPose.IsVisible = true;
            trackedPose.LastSeenTime = Time.time;
            trackedPose.ConsecutiveOutlierFrames = 0;
            return;
        }

        float positionDelta = Vector3.Distance(trackedPose.Position, worldPosition);
        float rotationDelta = Quaternion.Angle(trackedPose.Rotation, worldRotation);
        bool shouldRelocalize =
            !trackedPose.IsVisible ||
            (relocalizationSnapDistanceMeters > 0f && positionDelta > relocalizationSnapDistanceMeters) ||
            (relocalizationSnapRotationDegrees > 0f && rotationDelta > relocalizationSnapRotationDegrees);

        if (shouldRelocalize)
        {
            trackedPose.Position = worldPosition;
            trackedPose.Rotation = worldRotation;
            trackedPose.ConsecutiveOutlierFrames = 0;
            trackedPose.LastSeenTime = Time.time;
            trackedPose.IsTracked = true;
            trackedPose.IsVisible = true;
            return;
        }

        bool isLargeJump = trackedPose.HasPose &&
            (maxPositionJumpMeters > 0f && positionDelta > maxPositionJumpMeters ||
             maxRotationJumpDegrees > 0f && rotationDelta > maxRotationJumpDegrees);

        if (isLargeJump)
        {
            trackedPose.ConsecutiveOutlierFrames++;
            if (trackedPose.ConsecutiveOutlierFrames < Mathf.Max(1, outlierFramesBeforeSnap))
            {
                trackedPose.LastSeenTime = Time.time;
                trackedPose.IsTracked = true;
                trackedPose.IsVisible = true;
                return;
            }

            trackedPose.Position = worldPosition;
            trackedPose.Rotation = worldRotation;
            trackedPose.ConsecutiveOutlierFrames = 0;
            trackedPose.LastSeenTime = Time.time;
            trackedPose.IsTracked = true;
            trackedPose.IsVisible = true;
            return;
        }

        trackedPose.ConsecutiveOutlierFrames = 0;

        bool measurementIsWithinDeadband =
            positionDelta <= stationaryPositionDeadbandMeters &&
            rotationDelta <= stationaryRotationDeadbandDegrees;
        if (measurementIsWithinDeadband)
        {
            trackedPose.LastSeenTime = Time.time;
            trackedPose.IsTracked = true;
            trackedPose.IsVisible = true;
            return;
        }

        if (poseSmoothing <= 0f)
        {
            trackedPose.Position = worldPosition;
            trackedPose.Rotation = worldRotation;
        }
        else
        {
            float blend = 1f - Mathf.Exp(-poseSmoothing * Time.deltaTime);
            Vector3 blendedPosition = Vector3.Lerp(trackedPose.Position, worldPosition, blend);
            Quaternion blendedRotation = Quaternion.Slerp(trackedPose.Rotation, worldRotation, blend);

            if (maxPositionStepPerUpdateMeters > 0f)
            {
                blendedPosition = Vector3.MoveTowards(
                    trackedPose.Position,
                    blendedPosition,
                    maxPositionStepPerUpdateMeters);
            }

            if (maxRotationStepPerUpdateDegrees > 0f)
            {
                blendedRotation = Quaternion.RotateTowards(
                    trackedPose.Rotation,
                    blendedRotation,
                    maxRotationStepPerUpdateDegrees);
            }

            trackedPose.Position = blendedPosition;
            trackedPose.Rotation = blendedRotation;
        }

        trackedPose.LastSeenTime = Time.time;
        trackedPose.IsTracked = true;
        trackedPose.IsVisible = true;
    }

    void UpdateVisual()
    {
        Transform visualTransform = GetOrCreateVisualTransform();
        if (visualTransform == null)
        {
            return;
        }

        bool shouldShow = trackedPose.HasPose && trackedPose.IsVisible;
        if (visualTransform.gameObject.activeSelf != shouldShow)
        {
            visualTransform.gameObject.SetActive(shouldShow);
        }

        if (!shouldShow)
        {
            return;
        }

        visualTransform.SetPositionAndRotation(trackedPose.Position, trackedPose.Rotation);

        if (runtimeLabel != null)
        {
            runtimeLabel.text = cubeLabel;
            runtimeLabel.transform.localPosition = new Vector3(0f, cubeSizeMeters * 0.75f, 0f);

            if (Camera.main != null)
            {
                runtimeLabel.transform.rotation = Quaternion.LookRotation(
                    runtimeLabel.transform.position - Camera.main.transform.position,
                    Camera.main.transform.up);
            }
        }
    }

    Transform GetOrCreateVisualTransform()
    {
        if (cubeVisualTransform != null)
        {
            return cubeVisualTransform;
        }

        if (!createRuntimeVisual)
        {
            return null;
        }

        if (runtimeVisualRoot != null)
        {
            return runtimeVisualRoot;
        }

        GameObject root = new GameObject($"{cubeLabel}_Tracked");
        runtimeVisualRoot = root.transform;

        GameObject cubeBody = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cubeBody.name = "Visual";
        cubeBody.transform.SetParent(runtimeVisualRoot, false);
        cubeBody.transform.localScale = Vector3.one * cubeSizeMeters;

        Collider cubeCollider = cubeBody.GetComponent<Collider>();
        if (cubeCollider != null)
        {
            cubeCollider.enabled = false;
        }

        Renderer cubeRenderer = cubeBody.GetComponent<Renderer>();
        if (cubeMaterial != null)
        {
            cubeRenderer.sharedMaterial = cubeMaterial;
        }
        else
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            Material material = new Material(shader);
            material.color = cubeColor;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", cubeColor);
            }

            cubeRenderer.material = material;
        }

        if (showLabel)
        {
            GameObject labelObject = new GameObject("Label");
            labelObject.transform.SetParent(runtimeVisualRoot, false);
            runtimeLabel = labelObject.AddComponent<TextMeshPro>();
            runtimeLabel.fontSize = 2f;
            runtimeLabel.alignment = TextAlignmentOptions.Center;
            runtimeLabel.color = cubeColor;
            runtimeLabel.text = cubeLabel;
            runtimeLabel.rectTransform.sizeDelta = new Vector2(0.25f, 0.06f);
        }

        root.SetActive(false);
        cubeVisualTransform = runtimeVisualRoot;
        return cubeVisualTransform;
    }
}
