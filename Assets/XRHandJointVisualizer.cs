using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public class XRHandJointVisualizer : MonoBehaviour
{
    public GameObject Hand; // assign OpenXRHand here in the Inspector
    public GameObject leftHand;
    public CameraFeedViewer tableTracker;
    public ArucoCubeTracker[] cubeTrackers;
    public bool showJointDebugVisuals = false;
    public bool showJointLabels = false;
    public string recordingDirectoryOverride = "";
    public bool useControllerButtonToggle = false;
    public bool usePinchToggle = false;
    public Transform recordingToggleZone;
    public bool useHeadRelativeToggleZone = true;
    public Vector3 headRelativeToggleZoneOffset = new Vector3(-0.18f, 0.12f, 0.35f);
    [Min(0.02f)] public float recordingToggleZoneRadius = 0.07f;
    [Min(0.05f)] public float recordingToggleCooldownSeconds = 0.75f;
    [Min(0.005f)] public float leftPinchDistanceThreshold = 0.025f;
    public bool freezeTableOriginOnRecordingStart = true;
    public bool requireTrackedTableToStartRecording = false;
    public bool showRecordingToggleVisual = true;
    [Min(0.01f)] public float recordingToggleVisualScale = 0.035f;
    public bool stabilizeRecordedHandData = true;
    [Min(0.01f)] public float rootPositionSmoothing = 18f;
    [Min(0.01f)] public float rootRotationSmoothing = 20f;
    [Min(0.01f)] public float jointRotationSmoothing = 24f;
    [Min(0f)] public float occlusionSpikeAngleDegrees = 22f;
    [Min(0f)] public float occlusionAngularVelocityThreshold = 540f;
    [Min(0f)] public float occlusionHoldSeconds = 0.12f;
    [Range(0f, 1f)] public float occlusionBlendMultiplier = 0.18f;
    public bool recordWristTransforms = true;
    public bool recordFingerTipPositions = true;
    public bool recordJointRotations = false;

    private List<GameObject> jointObjects = new List<GameObject>();
    private List<GameObject> jointSpheres = new List<GameObject>();
    private List<TextMesh> jointLabels = new List<TextMesh>();
    private Dictionary<string, FilteredRotationState> filteredJointRotations = new Dictionary<string, FilteredRotationState>();

    private int debugLayer;
    private GameObject recordingToggleVisual;
    private Renderer recordingToggleVisualRenderer;
    private float lastRecordingToggleTime = float.NegativeInfinity;
    private bool leftPinchWasActiveLastFrame;
    private OVRHand leftOvrHand;
    private Transform leftThumbTip;
    private Transform leftIndexTip;
    private Transform recordedThumbTip;
    private Transform recordedIndexTip;
    private Transform recordedMiddleTip;
    private Transform recordedRingTip;
    private Transform recordedLittleTip;

    // Recording Tools
    public bool isRecording;
    public float recordStartTime = 0f;
    public string LastSavedFilePath { get; private set; } = "";
    private List<string> recordedLines = new List<string>();
    private int recordedFrameCount;
    private Pose recordingStartTableOriginPose;
    private bool hasRecordingStartTableOriginPose;
    private bool hasStabilizedRootLocalPosition;
    private Vector3 stabilizedRootLocalPosition;
    private bool hasStabilizedRootLocalRotation;
    private Quaternion stabilizedRootLocalRotation = Quaternion.identity;
    private bool hasStabilizedRootWorldPosition;
    private Vector3 stabilizedRootWorldPosition;
    private bool hasStabilizedRootWorldRotation;
    private Quaternion stabilizedRootWorldRotation = Quaternion.identity;
    private List<JointDistanceRecord> recordingStartJointDistances = new List<JointDistanceRecord>();
    private List<JointOffsetRecord> recordingStartWristToFingerBaseMeasurements = new List<JointOffsetRecord>();
    private HashSet<string> targetJoints = new HashSet<string>()
    {
        // All joints shown here show significant rotation and are key to hand movement
        // Other joints like handTips and Metacarpals for non-thumbs are not included
        // Root is not named here — handled separately:
        "XRHand_ThumbMetacarpal",
        "XRHand_ThumbProximal",
        "XRHand_ThumbDistal",

        "XRHand_LittleProximal",
        "XRHand_LittleIntermediate",
        "XRHand_LittleDistal",

        "XRHand_RingProximal",
        "XRHand_RingIntermediate",
        "XRHand_RingDistal",

        "XRHand_MiddleProximal",
        "XRHand_MiddleIntermediate",
        "XRHand_MiddleDistal",

        "XRHand_IndexProximal",
        "XRHand_IndexIntermediate",
        "XRHand_IndexDistal"
    };

    private struct FilteredRotationState
    {
        public bool initialized;
        public Quaternion rotation;
        public float lastReliableTime;
    }

    private struct JointDistanceRecord
    {
        public string fromJoint;
        public string toJoint;
        public float distanceMeters;
    }

    private struct JointOffsetRecord
    {
        public string fromJoint;
        public string toJoint;
        public Vector3 offsetMeters;
        public float distanceMeters;
    }

    void Start()
    {
        if (Hand == null)
        {
            Debug.LogError("Hand not assigned! Did you forget or something?");
            return;
        }

        // Make or find a debug layer (default to 0 if missing)
        debugLayer = LayerMask.NameToLayer("DebugVisuals");
        if (debugLayer == -1) debugLayer = 0;

        // Collect joints
        GetChildrenRecursive(Hand);
        CacheRecordedThumbTip();
        if (usePinchToggle)
        {
            CacheLeftOvrHand();
            CacheLeftHandPinchJoints();
        }

        foreach (var joint in jointObjects)
        {
            if (showJointDebugVisuals)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.localScale = Vector3.one * 0.01f;
                cube.GetComponent<Collider>().enabled = false;
                cube.layer = debugLayer;

                cube.GetComponent<Renderer>().material.shader = Shader.Find("Unlit/Color");
                cube.GetComponent<Renderer>().material.color = Color.red;
                jointSpheres.Add(cube);
            }

            if (showJointLabels)
            {
                GameObject textObj = new GameObject("JointLabel_" + joint.name);
                TextMesh tm = textObj.AddComponent<TextMesh>();
                tm.text = joint.name;
                tm.fontSize = 16;
                tm.characterSize = 0.0025f;
                tm.anchor = TextAnchor.LowerCenter;
                tm.color = Color.yellow;

                textObj.layer = debugLayer;
                jointLabels.Add(tm);
            }
        }

        Debug.Log($"Created {jointSpheres.Count} joint debug cubes and {jointLabels.Count} joint labels");
        EnsureRecordingToggleVisual();
        CacheCubeTrackers();
    }

    void Update()
    {
        if (Hand == null) return;

        UpdateRecordingToggleVisual();
        UpdateRecordingToggle();
        UpdateStabilizedTrackingState(Time.deltaTime);

        for (int i = 0; i < jointObjects.Count; i++)
        {
            Transform t = jointObjects[i].transform;

            if (showJointDebugVisuals && i < jointSpheres.Count)
            {
                jointSpheres[i].transform.position = t.position;
                jointSpheres[i].transform.rotation = t.rotation;
            }

            if (showJointLabels && i < jointLabels.Count)
            {
                Vector3 rot = t.localEulerAngles;
                jointLabels[i].text = $"{t.name}\nRot: {rot.x:F1}, {rot.y:F1}, {rot.z:F1}";
                jointLabels[i].transform.position = t.position + Vector3.up * 0.015f;

                if (Camera.main != null)
                {
                    jointLabels[i].transform.rotation = Camera.main.transform.rotation;
                }
            }
        }

        if (isRecording) RecordFrame();
    }

    private void StartRecording()
    {
        if (requireTrackedTableToStartRecording && !TryCaptureRecordingStartTableOrigin())
        {
            Debug.LogWarning("Recording start blocked because no tracked table origin is available.");
            return;
        }

        isRecording = true;
        recordStartTime = Time.time;
        recordedLines.Clear();
        recordedFrameCount = 0;
        CaptureRecordingStartJointDistances();
        AppendRecordingMetadataLine();
        if (!freezeTableOriginOnRecordingStart)
        {
            hasRecordingStartTableOriginPose = false;
        }
        else if (!hasRecordingStartTableOriginPose)
        {
            TryCaptureRecordingStartTableOrigin();
        }
        UpdateRecordingVisualState();
        Debug.Log(hasRecordingStartTableOriginPose
            ? "Recording Started with frozen table origin."
            : "Recording Started without frozen table origin.");
    }

    private void StopRecording()
    {
        isRecording = false;
        UpdateRecordingVisualState();

        string outputDirectory = GetRecordingDirectory();
        Directory.CreateDirectory(outputDirectory);

        string file = Path.Combine(outputDirectory, "hand_recording_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
        File.WriteAllLines(file, recordedLines);
        LastSavedFilePath = file;
        hasRecordingStartTableOriginPose = false;

        Debug.Log("Recording Stopped and Saved To: " + file);
    }

    public bool IsRecording => isRecording;

    public void ToggleRecording()
    {
        if (!isRecording)
        {
            StartRecording();
        }
        else
        {
            StopRecording();
        }
    }

    private void RecordFrame()
    {
        float t = Time.time - recordStartTime;

        StringBuilder sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"recordType\":\"frame\",");
        sb.AppendFormat("\"frameIndex\":{0},", recordedFrameCount++);
        sb.AppendFormat("\"jointRotationsRecorded\":{0},", recordJointRotations ? "true" : "false");

        // Time
        sb.AppendFormat("\"time\":{0},", t);

        Transform root = Hand.transform;
        Vector3 localPosition = stabilizeRecordedHandData && hasStabilizedRootLocalPosition
            ? stabilizedRootLocalPosition
            : root.localPosition;
        Quaternion localRotation = stabilizeRecordedHandData && hasStabilizedRootLocalRotation
            ? stabilizedRootLocalRotation
            : root.localRotation;
        if (recordWristTransforms)
        {
            AppendPoseObject(sb, "handRootLocal", localPosition, localRotation);
        }

        Vector3 worldPosition = stabilizeRecordedHandData && hasStabilizedRootWorldPosition
            ? stabilizedRootWorldPosition
            : root.position;
        Quaternion worldRotation = stabilizeRecordedHandData && hasStabilizedRootWorldRotation
            ? stabilizedRootWorldRotation
            : root.rotation;
        if (recordWristTransforms)
        {
            AppendPoseObject(sb, "handRootWorld", worldPosition, worldRotation);
        }

        Pose liveTableOriginPose = default;
        bool hasLiveTableOrigin = tableTracker != null && tableTracker.TryGetTableOriginPose(out liveTableOriginPose);
        sb.AppendFormat("\"tableOriginTracked\":{0},", hasLiveTableOrigin ? "true" : "false");
        Vector3 recordedRootPosition = worldPosition;
        Quaternion recordedRootRotation = worldRotation;
        string recordedRootSpace = "world";

        if (recordWristTransforms && hasLiveTableOrigin)
        {
            AppendPoseObject(sb, "tableOriginWorld", liveTableOriginPose.position, liveTableOriginPose.rotation);
        }

        Pose recordingTableOriginPose = liveTableOriginPose;
        bool hasRecordingTableOrigin = hasLiveTableOrigin;

        if (freezeTableOriginOnRecordingStart)
        {
            if (!hasRecordingStartTableOriginPose && hasLiveTableOrigin)
            {
                recordingStartTableOriginPose = liveTableOriginPose;
                hasRecordingStartTableOriginPose = true;
            }

            if (hasRecordingStartTableOriginPose)
            {
                recordingTableOriginPose = recordingStartTableOriginPose;
                hasRecordingTableOrigin = true;
                sb.AppendFormat("\"tableOriginFrozen\":true,");
                if (recordWristTransforms)
                {
                    AppendPoseObject(
                        sb,
                        "tableOriginRecordingStartWorld",
                        recordingStartTableOriginPose.position,
                        recordingStartTableOriginPose.rotation);
                }
            }
            else
            {
                sb.AppendFormat("\"tableOriginFrozen\":false,");
            }
        }
        else
        {
            sb.AppendFormat("\"tableOriginFrozen\":false,");
        }

        if (hasRecordingTableOrigin)
        {
            Vector3 tableRelativePosition =
                Quaternion.Inverse(recordingTableOriginPose.rotation) *
                (worldPosition - recordingTableOriginPose.position);
            Quaternion tableRelativeRotation =
                Quaternion.Inverse(recordingTableOriginPose.rotation) * worldRotation;
            if (recordWristTransforms)
            {
                AppendPoseObject(sb, "handRootTable", tableRelativePosition, tableRelativeRotation);
            }
            recordedRootPosition = tableRelativePosition;
            recordedRootRotation = tableRelativeRotation;
            recordedRootSpace = "table";
        }

        if (recordWristTransforms)
        {
            sb.AppendFormat("\"rootSpace\":\"{0}\",", recordedRootSpace);
            AppendPoseObject(sb, root.name, recordedRootPosition, recordedRootRotation);
        }

        if (recordFingerTipPositions)
        {
            AppendFingerTipsRelativeToWrist(sb, root);
        }
        AppendCubeRecordingData(sb, hasRecordingTableOrigin, recordingTableOriginPose);

        if (recordJointRotations)
        {
            // Joint rotations
            foreach (var j in jointObjects)
            {
                string jn = j.name;
                if (!targetJoints.Contains(jn)) continue;

                Quaternion q = GetRecordedJointRotation(jn, j.transform.localRotation);

                sb.AppendFormat("\"{0}\":[{1},{2},{3},{4}],",
                    jn, q.x, q.y, q.z, q.w);
            }
        }

        // Remove trailing comma (last char might be ,)
        if (sb[sb.Length - 1] == ',')
            sb.Remove(sb.Length - 1, 1);

        sb.Append("}");
        recordedLines.Add(sb.ToString());
    }

    private void UpdateStabilizedTrackingState(float deltaTime)
    {
        if (Hand == null)
        {
            return;
        }

        Transform root = Hand.transform;
        if (!stabilizeRecordedHandData || deltaTime <= 0f)
        {
            stabilizedRootLocalPosition = root.localPosition;
            stabilizedRootLocalRotation = root.localRotation;
            hasStabilizedRootLocalPosition = true;
            hasStabilizedRootLocalRotation = true;
            stabilizedRootWorldPosition = root.position;
            stabilizedRootWorldRotation = root.rotation;
            hasStabilizedRootWorldPosition = true;
            hasStabilizedRootWorldRotation = true;

            if (recordJointRotations)
            {
                foreach (GameObject jointObject in jointObjects)
                {
                    string jointName = jointObject.name;
                    if (!targetJoints.Contains(jointName))
                    {
                        continue;
                    }

                    filteredJointRotations[jointName] = new FilteredRotationState
                    {
                        initialized = true,
                        rotation = jointObject.transform.localRotation,
                        lastReliableTime = Time.time
                    };
                }
            }
            else
            {
                filteredJointRotations.Clear();
            }

            return;
        }

        stabilizedRootLocalPosition = DampVector(stabilizedRootLocalPosition, root.localPosition, rootPositionSmoothing, deltaTime, ref hasStabilizedRootLocalPosition);
        stabilizedRootLocalRotation = DampQuaternion(stabilizedRootLocalRotation, root.localRotation, rootRotationSmoothing, deltaTime, ref hasStabilizedRootLocalRotation);
        stabilizedRootWorldPosition = DampVector(stabilizedRootWorldPosition, root.position, rootPositionSmoothing, deltaTime, ref hasStabilizedRootWorldPosition);
        stabilizedRootWorldRotation = DampQuaternion(stabilizedRootWorldRotation, root.rotation, rootRotationSmoothing, deltaTime, ref hasStabilizedRootWorldRotation);

        if (!recordJointRotations)
        {
            filteredJointRotations.Clear();
            return;
        }

        foreach (GameObject jointObject in jointObjects)
        {
            string jointName = jointObject.name;
            if (!targetJoints.Contains(jointName))
            {
                continue;
            }

            Quaternion rawRotation = jointObject.transform.localRotation;
            FilteredRotationState state;
            if (!filteredJointRotations.TryGetValue(jointName, out state) || !state.initialized)
            {
                state = new FilteredRotationState
                {
                    initialized = true,
                    rotation = rawRotation,
                    lastReliableTime = Time.time
                };
                filteredJointRotations[jointName] = state;
                continue;
            }

            float angleDelta = Quaternion.Angle(state.rotation, rawRotation);
            float angularVelocity = deltaTime > 0f ? angleDelta / deltaTime : 0f;
            bool potentialOcclusionSpike =
                angleDelta >= occlusionSpikeAngleDegrees ||
                angularVelocity >= occlusionAngularVelocityThreshold;

            float blend = jointRotationSmoothing;
            if (!potentialOcclusionSpike)
            {
                state.lastReliableTime = Time.time;
            }
            else
            {
                float timeSinceReliable = Time.time - state.lastReliableTime;
                float recoveryFactor = Mathf.Clamp01(timeSinceReliable / Mathf.Max(0.0001f, occlusionHoldSeconds));
                float occlusionBlend = Mathf.Lerp(occlusionBlendMultiplier, 1f, recoveryFactor);
                blend *= occlusionBlend;
            }

            state.rotation = DampQuaternion(state.rotation, rawRotation, blend, deltaTime, ref state.initialized);
            filteredJointRotations[jointName] = state;
        }
    }

    private void AppendPoseObject(StringBuilder sb, string name, Vector3 position, Quaternion rotation)
    {
        sb.AppendFormat("\"{0}\":{{\"pos\":[{1},{2},{3}],\"rot\":[{4},{5},{6},{7}]}},",
            name,
            position.x, position.y, position.z,
            rotation.x, rotation.y, rotation.z, rotation.w);
    }

    private void AppendCubeRecordingData(StringBuilder sb, bool hasRecordingTableOrigin, Pose recordingTableOriginPose)
    {
        CacheCubeTrackers();

        if (cubeTrackers == null || cubeTrackers.Length == 0)
        {
            return;
        }

        foreach (ArucoCubeTracker cubeTracker in cubeTrackers)
        {
            if (cubeTracker == null)
            {
                continue;
            }

            string cubeKey = $"cube{cubeTracker.cubeMarkerId}";
            sb.AppendFormat("\"{0}Tracked\":{1},", cubeKey, cubeTracker.IsCubeCurrentlyTracked ? "true" : "false");

            if (!hasRecordingTableOrigin || !cubeTracker.TryGetCubeWorldPose(out Pose cubeWorldPose))
            {
                continue;
            }

            Vector3 tableRelativePosition =
                Quaternion.Inverse(recordingTableOriginPose.rotation) *
                (cubeWorldPose.position - recordingTableOriginPose.position);
            Quaternion tableRelativeRotation =
                Quaternion.Inverse(recordingTableOriginPose.rotation) * cubeWorldPose.rotation;
            AppendPoseObject(sb, $"{cubeKey}Table", tableRelativePosition, tableRelativeRotation);
        }
    }

    private void CacheCubeTrackers()
    {
        if (cubeTrackers != null && cubeTrackers.Length > 0)
        {
            return;
        }

        cubeTrackers = FindObjectsByType<ArucoCubeTracker>(FindObjectsSortMode.None);
    }

    private void CacheRecordedThumbTip()
    {
        recordedThumbTip = FindChildTransformByName(Hand != null ? Hand.transform : null, "XRHand_ThumbTip");
        if (recordedThumbTip == null && Hand != null)
        {
            Debug.LogWarning("XRHand_ThumbTip was not found under the tracked hand. Thumb tip IK data will be omitted from recordings.");
        }

        recordedIndexTip = FindChildTransformByName(Hand != null ? Hand.transform : null, "XRHand_IndexTip");
        if (recordedIndexTip == null && Hand != null)
        {
            Debug.LogWarning("XRHand_IndexTip was not found under the tracked hand. Index tip IK data will be omitted from recordings.");
        }

        recordedMiddleTip = FindChildTransformByName(Hand != null ? Hand.transform : null, "XRHand_MiddleTip");
        if (recordedMiddleTip == null && Hand != null)
        {
            Debug.LogWarning("XRHand_MiddleTip was not found under the tracked hand. Middle tip IK data will be omitted from recordings.");
        }

        recordedRingTip = FindChildTransformByName(Hand != null ? Hand.transform : null, "XRHand_RingTip");
        if (recordedRingTip == null && Hand != null)
        {
            Debug.LogWarning("XRHand_RingTip was not found under the tracked hand. Ring tip IK data will be omitted from recordings.");
        }

        recordedLittleTip = FindChildTransformByName(Hand != null ? Hand.transform : null, "XRHand_LittleTip");
        if (recordedLittleTip == null && Hand != null)
        {
            Debug.LogWarning("XRHand_LittleTip was not found under the tracked hand. Little tip IK data will be omitted from recordings.");
        }
    }

    private void AppendFingerTipsRelativeToWrist(StringBuilder sb, Transform root)
    {
        if (root == null)
        {
            return;
        }

        if (recordedThumbTip == null)
        {
            CacheRecordedThumbTip();
        }

        if (recordedThumbTip == null)
        {
            return;
        }

        AppendTipRelativeToWrist(sb, root, "thumbTipRelativeToWrist", recordedThumbTip);
        AppendTipRelativeToWrist(sb, root, "indexTipRelativeToWrist", recordedIndexTip);
        AppendTipRelativeToWrist(sb, root, "middleTipRelativeToWrist", recordedMiddleTip);
        AppendTipRelativeToWrist(sb, root, "ringTipRelativeToWrist", recordedRingTip);
        AppendTipRelativeToWrist(sb, root, "littleTipRelativeToWrist", recordedLittleTip);
    }

    private bool TryCaptureRecordingStartTableOrigin()
    {
        if (tableTracker == null || !tableTracker.TryGetTableOriginPose(out Pose tableOriginPose))
        {
            return false;
        }

        recordingStartTableOriginPose = tableOriginPose;
        hasRecordingStartTableOriginPose = true;
        return true;
    }

    private string GetRecordingDirectory()
    {
        if (!string.IsNullOrWhiteSpace(recordingDirectoryOverride))
        {
            return recordingDirectoryOverride;
        }

        return Path.Combine(GetDefaultRecordingRootDirectory(), "HandRecordings");
    }

    private string GetDefaultRecordingRootDirectory()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using AndroidJavaClass environment = new AndroidJavaClass("android.os.Environment");

            string documentsType = environment.GetStatic<string>("DIRECTORY_DOCUMENTS");
            using AndroidJavaObject sharedDocumentsDirectory =
                environment.CallStatic<AndroidJavaObject>("getExternalStoragePublicDirectory", documentsType);

            string sharedDocumentsPath = sharedDocumentsDirectory?.Call<string>("getAbsolutePath");
            if (!string.IsNullOrWhiteSpace(sharedDocumentsPath))
            {
                return sharedDocumentsPath;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("Shared Documents path lookup failed, falling back to app-specific storage: " + ex.Message);
        }

        try
        {
            using AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using AndroidJavaObject currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            using AndroidJavaClass environment = new AndroidJavaClass("android.os.Environment");

            string documentsType = environment.GetStatic<string>("DIRECTORY_DOCUMENTS");
            using AndroidJavaObject appDocumentsDirectory = currentActivity.Call<AndroidJavaObject>("getExternalFilesDir", documentsType);

            string appDocumentsPath = appDocumentsDirectory?.Call<string>("getAbsolutePath");
            if (!string.IsNullOrWhiteSpace(appDocumentsPath))
            {
                return appDocumentsPath;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("Falling back to persistentDataPath for recordings: " + ex.Message);
        }
#endif

        return Application.persistentDataPath;
    }

    private void CaptureRecordingStartJointDistances()
    {
        recordingStartJointDistances.Clear();
        recordingStartWristToFingerBaseMeasurements.Clear();

        if (Hand == null)
        {
            return;
        }

        Transform[] handTransforms = Hand.GetComponentsInChildren<Transform>(true);
        foreach (Transform joint in handTransforms)
        {
            Transform parent = joint.parent;
            if (parent == null)
            {
                continue;
            }

            if (!parent.IsChildOf(Hand.transform) && parent != Hand.transform)
            {
                continue;
            }

            recordingStartJointDistances.Add(new JointDistanceRecord
            {
                fromJoint = parent.name,
                toJoint = joint.name,
                distanceMeters = joint.localPosition.magnitude
            });
        }

        AppendWristToFingerBaseMeasurement("XRHand_ThumbMetacarpal");
        AppendWristToFingerBaseMeasurement("XRHand_IndexProximal");
        AppendWristToFingerBaseMeasurement("XRHand_MiddleProximal");
        AppendWristToFingerBaseMeasurement("XRHand_RingProximal");
        AppendWristToFingerBaseMeasurement("XRHand_LittleProximal");
    }

    private void AppendRecordingMetadataLine()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"recordType\":\"metadata\",");
        sb.Append("\"schemaVersion\":3,");
        sb.AppendFormat("\"stabilizedRecording\":{0},", stabilizeRecordedHandData ? "true" : "false");
        sb.AppendFormat("\"recordWristTransforms\":{0},", recordWristTransforms ? "true" : "false");
        sb.AppendFormat("\"recordFingerTipPositions\":{0},", recordFingerTipPositions ? "true" : "false");
        sb.AppendFormat("\"recordJointRotations\":{0},", recordJointRotations ? "true" : "false");
        sb.Append("\"jointDistanceUnit\":\"meters\",");
        sb.Append("\"jointDistances\":[");

        for (int i = 0; i < recordingStartJointDistances.Count; i++)
        {
            JointDistanceRecord distanceRecord = recordingStartJointDistances[i];
            sb.AppendFormat(
                "{{\"from\":\"{0}\",\"to\":\"{1}\",\"distance\":{2}}}",
                distanceRecord.fromJoint,
                distanceRecord.toJoint,
                distanceRecord.distanceMeters);

            if (i < recordingStartJointDistances.Count - 1)
            {
                sb.Append(",");
            }
        }

        sb.Append("],");
        sb.Append("\"wristToFingerBaseMeasurements\":[");

        for (int i = 0; i < recordingStartWristToFingerBaseMeasurements.Count; i++)
        {
            JointOffsetRecord measurement = recordingStartWristToFingerBaseMeasurements[i];
            sb.AppendFormat(
                "{{\"from\":\"{0}\",\"to\":\"{1}\",\"offset\":[{2},{3},{4}],\"distance\":{5}}}",
                measurement.fromJoint,
                measurement.toJoint,
                measurement.offsetMeters.x,
                measurement.offsetMeters.y,
                measurement.offsetMeters.z,
                measurement.distanceMeters);

            if (i < recordingStartWristToFingerBaseMeasurements.Count - 1)
            {
                sb.Append(",");
            }
        }

        sb.Append("]");
        sb.Append("}");
        recordedLines.Add(sb.ToString());
    }

    private void AppendWristToFingerBaseMeasurement(string fingerBaseJointName)
    {
        Transform fingerBase = FindChildTransformByName(Hand.transform, fingerBaseJointName);
        if (fingerBase == null)
        {
            return;
        }

        Vector3 localOffset = fingerBase.localPosition;
        recordingStartWristToFingerBaseMeasurements.Add(new JointOffsetRecord
        {
            fromJoint = Hand.transform.name,
            toJoint = fingerBaseJointName,
            offsetMeters = localOffset,
            distanceMeters = localOffset.magnitude
        });
    }

    private void UpdateRecordingToggle()
    {
        if (useControllerButtonToggle && OVRInput.GetDown(OVRInput.RawButton.X))
        {
            ToggleRecording();
            return;
        }

        if (!usePinchToggle)
        {
            return;
        }

        if (!TryGetRecordingToggleZonePose(out Vector3 zonePosition, out Quaternion zoneRotation))
        {
            leftPinchWasActiveLastFrame = false;
            return;
        }

        bool isPinching = TryGetLeftPinchPose(out Vector3 pinchPosition);
        bool pinchInsideZone = isPinching && Vector3.Distance(pinchPosition, zonePosition) <= recordingToggleZoneRadius;
        bool cooldownComplete = (Time.time - lastRecordingToggleTime) >= recordingToggleCooldownSeconds;

        if (pinchInsideZone && !leftPinchWasActiveLastFrame && cooldownComplete)
        {
            ToggleRecording();
            lastRecordingToggleTime = Time.time;
        }

        leftPinchWasActiveLastFrame = isPinching;
    }

    private bool TryGetRecordingToggleZonePose(out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;

        if (recordingToggleZone != null)
        {
            position = recordingToggleZone.position;
            rotation = recordingToggleZone.rotation;
            return true;
        }

        if (!useHeadRelativeToggleZone || Camera.main == null)
        {
            return false;
        }

        Transform cameraTransform = Camera.main.transform;
        position =
            cameraTransform.position +
            cameraTransform.right * headRelativeToggleZoneOffset.x +
            cameraTransform.up * headRelativeToggleZoneOffset.y +
            cameraTransform.forward * headRelativeToggleZoneOffset.z;
        rotation = cameraTransform.rotation;
        return true;
    }

    private void EnsureRecordingToggleVisual()
    {
        if (!showRecordingToggleVisual || recordingToggleVisual != null)
        {
            return;
        }

        recordingToggleVisual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        recordingToggleVisual.name = "RecordingToggleVisual";
        recordingToggleVisual.layer = debugLayer;

        Collider visualCollider = recordingToggleVisual.GetComponent<Collider>();
        if (visualCollider != null)
        {
            visualCollider.enabled = false;
        }

        recordingToggleVisualRenderer = recordingToggleVisual.GetComponent<Renderer>();
        if (recordingToggleVisualRenderer != null)
        {
            recordingToggleVisualRenderer.material.shader = Shader.Find("Unlit/Color");
            recordingToggleVisualRenderer.material.color = Color.red;
        }
    }

    private void UpdateRecordingToggleVisual()
    {
        if (recordingToggleVisual == null)
        {
            EnsureRecordingToggleVisual();
        }

        if (recordingToggleVisual == null)
        {
            return;
        }

        bool hasZone = TryGetRecordingToggleZonePose(out Vector3 zonePosition, out Quaternion zoneRotation);
        recordingToggleVisual.SetActive(showRecordingToggleVisual && hasZone);
        if (!showRecordingToggleVisual || !hasZone)
        {
            return;
        }

        recordingToggleVisual.transform.SetPositionAndRotation(zonePosition, zoneRotation);
        recordingToggleVisual.transform.localScale = Vector3.one * Mathf.Max(0.01f, recordingToggleVisualScale);

        if (recordingToggleVisualRenderer != null)
        {
            recordingToggleVisualRenderer.material.color = isRecording ? Color.green : Color.red;
        }
    }

    private void CacheLeftHandPinchJoints()
    {
        leftThumbTip = null;
        leftIndexTip = null;

        if (leftHand == null)
        {
            return;
        }

        Transform[] leftHandTransforms = leftHand.GetComponentsInChildren<Transform>(true);
        foreach (Transform candidate in leftHandTransforms)
        {
            string candidateName = candidate.name;
            if (leftThumbTip == null && candidateName.Contains("ThumbTip"))
            {
                leftThumbTip = candidate;
            }
            else if (leftIndexTip == null && candidateName.Contains("IndexTip"))
            {
                leftIndexTip = candidate;
            }
        }

        if (leftThumbTip == null || leftIndexTip == null)
        {
            Debug.LogWarning("Left-hand pinch joints were not found. Assign a left hand root with thumb/index tip children.");
        }
    }

    private void CacheLeftOvrHand()
    {
        leftOvrHand = null;
        if (leftHand == null)
        {
            return;
        }

        leftOvrHand = leftHand.GetComponentInChildren<OVRHand>(true);
        if (leftOvrHand == null)
        {
            Debug.LogWarning("Left-hand OVRHand component was not found. Falling back to custom pinch detection.");
        }
    }

    private bool TryGetLeftPinchPose(out Vector3 pinchPosition)
    {
        pinchPosition = default;

        if (leftOvrHand == null && leftHand != null)
        {
            CacheLeftOvrHand();
        }

        if (leftOvrHand != null)
        {
            bool isBuiltInPinching = leftOvrHand.GetFingerIsPinching(OVRHand.HandFinger.Index);
            if (!isBuiltInPinching || !leftOvrHand.IsTracked)
            {
                return false;
            }

            Transform pointerPose = leftOvrHand.PointerPose;
            if (pointerPose == null)
            {
                return false;
            }

            pinchPosition = pointerPose.position;
            return true;
        }

        if ((leftThumbTip == null || leftIndexTip == null) && leftHand != null)
        {
            CacheLeftHandPinchJoints();
        }

        if (leftThumbTip == null || leftIndexTip == null)
        {
            return false;
        }

        Vector3 thumbPosition = leftThumbTip.position;
        Vector3 indexPosition = leftIndexTip.position;
        float pinchDistance = Vector3.Distance(thumbPosition, indexPosition);
        if (pinchDistance > leftPinchDistanceThreshold)
        {
            return false;
        }

        pinchPosition = (thumbPosition + indexPosition) * 0.5f;
        return true;
    }

    private void UpdateRecordingVisualState()
    {
        Color stateColor = isRecording ? Color.green : Color.red;
        foreach (GameObject jointVisual in jointSpheres)
        {
            Renderer renderer = jointVisual.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = stateColor;
            }
        }

        if (recordingToggleVisualRenderer != null)
        {
            recordingToggleVisualRenderer.material.color = stateColor;
        }
    }

    // What is a hand but a fleshy tree?
    // A fleshy tree designed to be traversed by a recursive algorithm.
    private void GetChildrenRecursive(GameObject parent)
    {
        jointObjects.Add(parent);

        foreach (Transform child in parent.transform)
        {
            GetChildrenRecursive(child.gameObject);
        }
    }

    private Quaternion GetRecordedJointRotation(string jointName, Quaternion fallbackRotation)
    {
        if (!stabilizeRecordedHandData)
        {
            return fallbackRotation;
        }

        FilteredRotationState state;
        if (filteredJointRotations.TryGetValue(jointName, out state) && state.initialized)
        {
            return state.rotation;
        }

        return fallbackRotation;
    }

    private static float GetExponentialBlend(float smoothing, float deltaTime)
    {
        if (smoothing <= 0f || deltaTime <= 0f)
        {
            return 1f;
        }

        return 1f - Mathf.Exp(-smoothing * deltaTime);
    }

    private static Vector3 DampVector(Vector3 current, Vector3 target, float smoothing, float deltaTime, ref bool initialized)
    {
        if (!initialized)
        {
            initialized = true;
            return target;
        }

        return Vector3.Lerp(current, target, GetExponentialBlend(smoothing, deltaTime));
    }

    private static Quaternion DampQuaternion(Quaternion current, Quaternion target, float smoothing, float deltaTime, ref bool initialized)
    {
        if (!initialized)
        {
            initialized = true;
            return target;
        }

        return Quaternion.Slerp(current, target, GetExponentialBlend(smoothing, deltaTime));
    }

    private static void AppendTipRelativeToWrist(StringBuilder sb, Transform root, string jsonFieldName, Transform tipTransform)
    {
        if (sb == null || root == null || tipTransform == null)
        {
            return;
        }

        Vector3 tipRelativeToWrist = root.InverseTransformPoint(tipTransform.position);
        sb.AppendFormat(
            "\"{0}\":[{1},{2},{3}],",
            jsonFieldName,
            tipRelativeToWrist.x,
            tipRelativeToWrist.y,
            tipRelativeToWrist.z);
    }

    private static Transform FindChildTransformByName(Transform root, string targetName)
    {
        if (root == null)
        {
            return null;
        }

        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            if (transforms[i].name == targetName)
            {
                return transforms[i];
            }
        }

        return null;
    }
}
