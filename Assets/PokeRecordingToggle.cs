using Oculus.Interaction;
using UnityEngine;

public class PokeRecordingToggle : MonoBehaviour
{
    public XRHandJointVisualizer recorder;
    public PokeInteractable pokeInteractable;
    public Transform buttonVisualRoot;
    [Min(0.05f)] public float toggleCooldownSeconds = 0.35f;
    public Color idleColor = Color.red;
    public Color recordingColor = Color.green;

    private Renderer[] buttonRenderers = System.Array.Empty<Renderer>();
    private float lastToggleTime = float.NegativeInfinity;
    private IInteractableView interactableView;

    private void Awake()
    {
        if (recorder == null)
        {
            recorder = FindFirstObjectByType<XRHandJointVisualizer>();
        }

        if (pokeInteractable == null)
        {
            pokeInteractable = GetComponent<PokeInteractable>();
        }

        if (buttonVisualRoot == null)
        {
            Transform visuals = transform.Find("Visuals");
            if (visuals == null && transform.parent != null)
            {
                visuals = transform.parent.Find("Visuals");
            }

            if (visuals != null)
            {
                buttonVisualRoot = visuals;
            }
        }

        if (buttonVisualRoot != null)
        {
            buttonRenderers = buttonVisualRoot.GetComponentsInChildren<Renderer>(true);
        }

        interactableView = pokeInteractable;
        ApplyVisualState();
    }

    private void OnEnable()
    {
        if (interactableView != null)
        {
            interactableView.WhenStateChanged += HandleStateChanged;
        }
    }

    private void OnDisable()
    {
        if (interactableView != null)
        {
            interactableView.WhenStateChanged -= HandleStateChanged;
        }
    }

    private void Update()
    {
        ApplyVisualState();
    }

    private void HandleStateChanged(InteractableStateChangeArgs args)
    {
        if (args.NewState != InteractableState.Select || args.PreviousState == InteractableState.Select)
        {
            return;
        }

        if (recorder == null)
        {
            recorder = FindFirstObjectByType<XRHandJointVisualizer>();
            if (recorder == null)
            {
                Debug.LogWarning("PokeRecordingToggle could not find an XRHandJointVisualizer to control.");
                return;
            }
        }

        if ((Time.time - lastToggleTime) < toggleCooldownSeconds)
        {
            return;
        }

        recorder.ToggleRecording();
        lastToggleTime = Time.time;
        ApplyVisualState();
    }

    private void ApplyVisualState()
    {
        Color targetColor = recorder != null && recorder.IsRecording ? recordingColor : idleColor;
        for (int i = 0; i < buttonRenderers.Length; i++)
        {
            Renderer renderer = buttonRenderers[i];
            if (renderer != null)
            {
                renderer.material.color = targetColor;
            }
        }
    }
}
