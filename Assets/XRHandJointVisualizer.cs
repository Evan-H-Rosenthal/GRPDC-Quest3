using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public class XRHandJointVisualizer : MonoBehaviour
{
    public GameObject Hand; // assign OpenXRHand here in the Inspector
    public GameObject leftHand;
    public CameraFeedViewer tableTracker;
    public string recordingDirectoryOverride = "";
    public bool useControllerButtonToggle = false;
    public bool usePinchToggle = false;
    public Transform recordingToggleZone;
    public bool useHeadRelativeToggleZone = true;
    public Vector3 headRelativeToggleZoneOffset = new Vector3(-0.18f, 0.12f, 0.35f);
    [Min(0.02f)] public float recordingToggleZoneRadius = 0.07f;
    [Min(0.05f)] public float recordingToggleCooldownSeconds = 0.75f;
    [Min(0.005f)] public float leftPinchDistanceThreshold = 0.025f;
    public bool showRecordingToggleVisual = true;
    [Min(0.01f)] public float recordingToggleVisualScale = 0.035f;

    private List<GameObject> jointObjects = new List<GameObject>();
    private List<GameObject> jointSpheres = new List<GameObject>();
    private List<TextMesh> jointLabels = new List<TextMesh>();

    private int debugLayer;
    private GameObject recordingToggleVisual;
    private Renderer recordingToggleVisualRenderer;
    private float lastRecordingToggleTime = float.NegativeInfinity;
    private bool leftPinchWasActiveLastFrame;
    private OVRHand leftOvrHand;
    private Transform leftThumbTip;
    private Transform leftIndexTip;

    // Recording Tools
    public bool isRecording;
    public float recordStartTime = 0f;
    public string LastSavedFilePath { get; private set; } = "";
    private List<string> recordedLines = new List<string>();
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
        if (usePinchToggle)
        {
            CacheLeftOvrHand();
            CacheLeftHandPinchJoints();
        }

        foreach (var joint in jointObjects)
        {
            // Create cube
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.localScale = Vector3.one * 0.01f;
            cube.GetComponent<Collider>().enabled = false;
            cube.layer = debugLayer;

            cube.GetComponent<Renderer>().material.shader = Shader.Find("Unlit/Color");
            cube.GetComponent<Renderer>().material.color = Color.red;
            jointSpheres.Add(cube);

            // Create text label
            GameObject textObj = new GameObject("JointLabel_" + joint.name);
            TextMesh tm = textObj.AddComponent<TextMesh>();
            tm.text = joint.name;
            tm.fontSize = 16;              // smaller text
            tm.characterSize = 0.0025f;    // smaller scaling
            tm.anchor = TextAnchor.LowerCenter;
            tm.color = Color.yellow;

            textObj.layer = debugLayer;
            jointLabels.Add(tm);
        }

        Debug.Log($"Created {jointSpheres.Count} joint debug spheres + labels");
        EnsureRecordingToggleVisual();
    }

    void Update()
    {
        if (Hand == null) return;

        UpdateRecordingToggleVisual();
        UpdateRecordingToggle();

        for (int i = 0; i < jointObjects.Count; i++)
        {
            Transform t = jointObjects[i].transform;

            // Update cube
            jointSpheres[i].transform.position = t.position;
            jointSpheres[i].transform.rotation = t.rotation;

            // Display local Euler rotation in text (rounded)
            Vector3 rot = t.localEulerAngles;
            jointLabels[i].text = $"{t.name}\nRot: {rot.x:F1}, {rot.y:F1}, {rot.z:F1}";

            // Update label slightly above the cube
            jointLabels[i].transform.position = t.position + Vector3.up * 0.015f;

            // Always face camera
            if (Camera.main != null)
                jointLabels[i].transform.rotation = Camera.main.transform.rotation;
        }

        if (isRecording) RecordFrame();
    }

    private void StartRecording()
    {
        isRecording = true;
        recordStartTime = Time.time;
        recordedLines.Clear();
        UpdateRecordingVisualState();
        Debug.Log("Recording Started");
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

        // Time
        sb.AppendFormat("\"time\":{0},", t);

        Transform root = Hand.transform;
        Vector3 localPosition = root.localPosition;
        Quaternion localRotation = root.localRotation;
        AppendPoseObject(sb, "handRootLocal", localPosition, localRotation);

        AppendPoseObject(sb, "handRootWorld", root.position, root.rotation);

        Pose tableOriginPose = default;
        bool hasTableOrigin = tableTracker != null && tableTracker.TryGetTableOriginPose(out tableOriginPose);
        sb.AppendFormat("\"tableOriginTracked\":{0},", hasTableOrigin ? "true" : "false");
        Vector3 recordedRootPosition = root.position;
        Quaternion recordedRootRotation = root.rotation;
        string recordedRootSpace = "world";

        if (hasTableOrigin)
        {
            AppendPoseObject(sb, "tableOriginWorld", tableOriginPose.position, tableOriginPose.rotation);

            Vector3 tableRelativePosition = Quaternion.Inverse(tableOriginPose.rotation) * (root.position - tableOriginPose.position);
            Quaternion tableRelativeRotation = Quaternion.Inverse(tableOriginPose.rotation) * root.rotation;
            AppendPoseObject(sb, "handRootTable", tableRelativePosition, tableRelativeRotation);
            recordedRootPosition = tableRelativePosition;
            recordedRootRotation = tableRelativeRotation;
            recordedRootSpace = "table";
        }

        sb.AppendFormat("\"rootSpace\":\"{0}\",", recordedRootSpace);
        AppendPoseObject(sb, root.name, recordedRootPosition, recordedRootRotation);

        // Joint rotations
        foreach (var j in jointObjects)
        {
            string jn = j.name;
            if (!targetJoints.Contains(jn)) continue;

            Quaternion q = j.transform.localRotation;

            sb.AppendFormat("\"{0}\":[{1},{2},{3},{4}],",
                jn, q.x, q.y, q.z, q.w);
        }

        // Remove trailing comma (last char might be ,)
        if (sb[sb.Length - 1] == ',')
            sb.Remove(sb.Length - 1, 1);

        sb.Append("}");
        recordedLines.Add(sb.ToString());
    }

    private void AppendPoseObject(StringBuilder sb, string name, Vector3 position, Quaternion rotation)
    {
        sb.AppendFormat("\"{0}\":{{\"pos\":[{1},{2},{3}],\"rot\":[{4},{5},{6},{7}]}},",
            name,
            position.x, position.y, position.z,
            rotation.x, rotation.y, rotation.z, rotation.w);
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
}
