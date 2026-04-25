using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class InstructionFlowController : MonoBehaviour
{
    [SerializeField] private GameObject canvasRoot;
    [SerializeField] private Text headerText;
    [SerializeField] private Text instructionalText;
    [SerializeField] private CanvasPokeButtonFeedback leftButton;
    [SerializeField] private CanvasPokeButtonFeedback rightButton;
    [SerializeField] private CameraFeedViewer cameraFeedViewer;
    [SerializeField] private XRHandJointVisualizer recorder;

    private const int IdlePageIndex = 0;
    private const int ReadyToStartPageIndex = 5;
    private const int CountdownPageIndex = 6;

    private readonly PageData[] _pages =
    {
        new PageData(
            "Welcome to the Demo!",
            "Please poke the button on the right with your pointer finger to continue."),
        new PageData(
            "Demo Overview",
            "You will be completing three different tasks in this demo.\n\n" +
            "The demo should not take more than five minutes.\n\n" +
            "If you do not want to participate, please poke the \"Back\" button."),
        new PageData(
            "Considerations: Hands",
            "You will be moving around the colored cubes on the table with your <b>Right Hand Only.</b>\n\n" +
            "The program will only record the things you do with your Right Hand.\n\n" +
            "If you are Left-Handed, please try your best."),
        new PageData(
            "Considerations: Tracking",
            "Please do not move the blocks or your hand around too fast.\n\n" +
            "If the virtual objects begin to act erratic, please move closer to the table.\n\n" +
            "You will get better results if you take your time."),
        new PageData(
            "Considerations: Grabbing",
            "Try not to obscure the markers on the sides of the blocks unless needed.\n\n" +
            "Grabbing the blocks by their edges is an effective way to move them."),
        new PageData(
            "Task 1: 3-block Stack",
            "Your first task is to stack three blocks on top of each other in the order specified.\n\n" +
            "Please stack the first block onto the colored circle.\n\n" +
            "The colored cubes next to the circle will show you the order to stack the blocks."),
        new PageData(
            "Task 1 Beginning!",
            string.Empty)
    };

    private int _currentPageIndex;
    private Coroutine _countdownCoroutine;
    private Canvas _canvas;
    private GraphicRaycaster _graphicRaycaster;
    private Behaviour[] _overlayCanvasBehaviours;
    private Transform _canvasTransform;
    private Vector3 _originalCanvasLocalPosition;
    private bool _hasOriginalCanvasLocalPosition;

    private void OnEnable()
    {
        if (cameraFeedViewer == null)
        {
            cameraFeedViewer = FindFirstObjectByType<CameraFeedViewer>();
        }

        if (recorder == null)
        {
            recorder = FindFirstObjectByType<XRHandJointVisualizer>();
        }

        CacheCanvasComponents();

        if (leftButton != null)
        {
            leftButton.Clicked += HandleLeftButtonClicked;
        }

        if (rightButton != null)
        {
            rightButton.Clicked += HandleRightButtonClicked;
        }

        if (cameraFeedViewer != null)
        {
            cameraFeedViewer.StackingTaskCompleted += HandleStackingTaskCompleted;
        }

        _currentPageIndex = IdlePageIndex;
        SetCanvasVisible(true);
        ShowPage(_currentPageIndex);
    }

    private void OnDisable()
    {
        if (leftButton != null)
        {
            leftButton.Clicked -= HandleLeftButtonClicked;
        }

        if (rightButton != null)
        {
            rightButton.Clicked -= HandleRightButtonClicked;
        }

        if (cameraFeedViewer != null)
        {
            cameraFeedViewer.StackingTaskCompleted -= HandleStackingTaskCompleted;
        }

        if (_countdownCoroutine != null)
        {
            StopCoroutine(_countdownCoroutine);
            _countdownCoroutine = null;
        }
    }

    private void HandleLeftButtonClicked()
    {
        if (_countdownCoroutine != null || _currentPageIndex == IdlePageIndex)
        {
            return;
        }

        _currentPageIndex = Mathf.Max(IdlePageIndex, _currentPageIndex - 1);
        ShowPage(_currentPageIndex);
    }

    private void HandleRightButtonClicked()
    {
        if (_countdownCoroutine != null)
        {
            return;
        }

        if (_currentPageIndex < CountdownPageIndex)
        {
            _currentPageIndex++;
            ShowPage(_currentPageIndex);
        }
    }

    private void ShowPage(int pageIndex)
    {
        PageData page = _pages[pageIndex];

        if (headerText != null)
        {
            headerText.text = page.Header;
        }

        if (instructionalText != null)
        {
            instructionalText.text = page.Body;
        }

        bool showLeftButton = pageIndex != IdlePageIndex && pageIndex != CountdownPageIndex;
        bool showRightButton = pageIndex != CountdownPageIndex;

        if (leftButton != null)
        {
            leftButton.gameObject.SetActive(showLeftButton);
            leftButton.SetIdleText("Back");
            leftButton.SetPressedText("Back");
        }

        if (rightButton != null)
        {
            rightButton.gameObject.SetActive(showRightButton);
            string rightButtonLabel = pageIndex == ReadyToStartPageIndex ? "Start!" : "Continue";
            rightButton.SetIdleText(rightButtonLabel);
            rightButton.SetPressedText(rightButtonLabel);
        }

        if (pageIndex == CountdownPageIndex)
        {
            _countdownCoroutine = StartCoroutine(RunCountdown());
        }
    }

    private IEnumerator RunCountdown()
    {
        string[] countdownSteps = { "3", "2", "1", "Go!" };

        foreach (string step in countdownSteps)
        {
            if (instructionalText != null)
            {
                instructionalText.text = step;
            }

            yield return new WaitForSeconds(1f);
        }

        _countdownCoroutine = null;

        if (cameraFeedViewer != null)
        {
            cameraFeedViewer.BeginStackingTask();
        }

        if (recorder != null)
        {
            recorder.StartRecordingIfNeeded();
        }

        SetCanvasVisible(false);
    }

    private void HandleStackingTaskCompleted()
    {
        if (recorder != null)
        {
            recorder.StopRecordingIfNeeded();
        }

        SetCanvasVisible(true);

        if (headerText != null)
        {
            headerText.text = "Task 1 Complete!";
        }

        if (instructionalText != null)
        {
            instructionalText.text = "You completed the task!";
        }

        if (leftButton != null)
        {
            leftButton.gameObject.SetActive(false);
        }

        if (rightButton != null)
        {
            rightButton.gameObject.SetActive(true);
            rightButton.SetIdleText("Continue");
            rightButton.SetPressedText("Continue");
        }
    }

    private void SetCanvasVisible(bool isVisible)
    {
        CacheCanvasComponents();

        if (_canvasTransform != null && _hasOriginalCanvasLocalPosition)
        {
            _canvasTransform.localPosition = isVisible
                ? _originalCanvasLocalPosition
                : _originalCanvasLocalPosition + (Vector3.up * 1000000f);
        }

        if (_canvas != null)
        {
            _canvas.enabled = isVisible;
        }

        if (_graphicRaycaster != null)
        {
            _graphicRaycaster.enabled = isVisible;
        }

        if (_overlayCanvasBehaviours != null)
        {
            for (int i = 0; i < _overlayCanvasBehaviours.Length; i++)
            {
                if (_overlayCanvasBehaviours[i] != null)
                {
                    _overlayCanvasBehaviours[i].enabled = isVisible;
                }
            }
        }
    }

    private void CacheCanvasComponents()
    {
        GameObject root = canvasRoot != null ? canvasRoot : gameObject;

        if (_canvasTransform == null)
        {
            _canvasTransform = root.transform;
        }

        if (!_hasOriginalCanvasLocalPosition && _canvasTransform != null)
        {
            _originalCanvasLocalPosition = _canvasTransform.localPosition;
            _hasOriginalCanvasLocalPosition = true;
        }

        if (_canvas == null)
        {
            _canvas = root.GetComponent<Canvas>();
        }

        if (_graphicRaycaster == null)
        {
            _graphicRaycaster = root.GetComponent<GraphicRaycaster>();
        }

        if (_overlayCanvasBehaviours == null)
        {
            Behaviour[] behaviours = root.GetComponents<Behaviour>();
            int overlayCount = 0;
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] != null && behaviours[i].GetType().Name.Contains("OverlayCanvas"))
                {
                    overlayCount++;
                }
            }

            _overlayCanvasBehaviours = new Behaviour[overlayCount];
            int overlayIndex = 0;
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] != null && behaviours[i].GetType().Name.Contains("OverlayCanvas"))
                {
                    _overlayCanvasBehaviours[overlayIndex++] = behaviours[i];
                }
            }
        }
    }

    private readonly struct PageData
    {
        public readonly string Header;
        public readonly string Body;

        public PageData(string header, string body)
        {
            Header = header;
            Body = body;
        }
    }
}
