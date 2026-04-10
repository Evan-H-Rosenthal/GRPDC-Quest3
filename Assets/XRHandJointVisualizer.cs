using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using System.IO;
using Unity.VisualScripting;

public class XRHandJointVisualizer : MonoBehaviour
{
    public GameObject Hand; // assign OpenXRHand here in the Inspector

    private List<GameObject> jointObjects = new List<GameObject>();
    private List<GameObject> jointSpheres = new List<GameObject>();
    private List<TextMesh> jointLabels = new List<TextMesh>();

    private int debugLayer;

    // Recording Tools
    public bool isRecording;
    public float recordStartTime = 0f;
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
    }

    void FixedUpdate()
    {
        if (Hand == null) return;

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

        // Check for Record Toggle (Space Bar)
        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (!isRecording) StartRecording();
            else StopRecording();
        }

        if (isRecording) RecordFrame();
    }

    private void StartRecording()
    {
        isRecording = true;
        recordStartTime = Time.time;
        recordedLines.Clear();

        foreach(var c in jointSpheres)
        {
            c.GetComponent<Renderer>().material.color = Color.green;
        }
        Debug.Log("Recording Started");
    }

    private void StopRecording()
    {
        isRecording = false;
        foreach(var c in jointSpheres)
        {
            c.GetComponent<Renderer>().material.color = Color.red;
        }

        string file = "C:/Users/evanl/Desktop/HandRecordings" + "/hand_recording_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
        File.WriteAllLines(file, recordedLines);

        Debug.Log("Recording Stopped and Saved To: " + file);
    }

    private void RecordFrame()
    {
        float t = Time.time - recordStartTime;

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("{");

        // Time
        sb.AppendFormat("\"time\":{0},", t);

        // Root - pos & rot
        Transform root = Hand.transform;
        Vector3 p = root.localPosition;
        Quaternion rq = root.localRotation;

        sb.AppendFormat("\"{0}\":{{\"pos\":[{1},{2},{3}],\"rot\":[{4},{5},{6},{7}]}},",
            root.name,
            p.x, p.y, p.z,
            rq.x, rq.y, rq.z, rq.w
        );

        // Joint rotations
        bool first = true; // control commas
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

    [System.Serializable]
    private class Serialization
    {
        public Dictionary<string, object> dict;
        public Serialization(Dictionary<string, object> d) { dict = d; }
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
