using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class InstructionFlowController : MonoBehaviour
{
    private enum FlowStage
    {
        Welcome,
        DemoOverview,
        Hands,
        Tracking,
        Grabbing,
        Task1Overview,
        Countdown,
        Task1Complete,
        Task2Overview,
        Task2Complete,
        Task3Overview,
        Task3Complete
    }

    [SerializeField] private GameObject canvasRoot;
    [SerializeField] private Text headerText;
    [SerializeField] private Text instructionalText;
    [SerializeField] private CanvasPokeButtonFeedback leftButton;
    [SerializeField] private CanvasPokeButtonFeedback rightButton;
    [SerializeField] private CameraFeedViewer cameraFeedViewer;
    [SerializeField] private XRHandJointVisualizer recorder;

    private FlowStage _currentStage;
    private CameraFeedViewer.DemoTaskType _countdownTask = CameraFeedViewer.DemoTaskType.None;
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
            cameraFeedViewer.TaskCompleted += HandleTaskCompleted;
        }

        _currentStage = FlowStage.Welcome;
        _countdownTask = CameraFeedViewer.DemoTaskType.None;
        SetCanvasVisible(true);
        ShowCurrentStage();
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
            cameraFeedViewer.TaskCompleted -= HandleTaskCompleted;
        }

        if (_countdownCoroutine != null)
        {
            StopCoroutine(_countdownCoroutine);
            _countdownCoroutine = null;
        }
    }

    private void HandleLeftButtonClicked()
    {
        if (_countdownCoroutine != null)
        {
            return;
        }

        switch (_currentStage)
        {
            case FlowStage.DemoOverview:
                _currentStage = FlowStage.Welcome;
                break;
            case FlowStage.Hands:
                _currentStage = FlowStage.DemoOverview;
                break;
            case FlowStage.Tracking:
                _currentStage = FlowStage.Hands;
                break;
            case FlowStage.Grabbing:
                _currentStage = FlowStage.Tracking;
                break;
            case FlowStage.Task1Overview:
                _currentStage = FlowStage.Grabbing;
                break;
            case FlowStage.Task2Overview:
                _currentStage = FlowStage.Task1Complete;
                break;
            case FlowStage.Task3Overview:
                _currentStage = FlowStage.Task2Complete;
                break;
            default:
                return;
        }

        ShowCurrentStage();
    }

    private void HandleRightButtonClicked()
    {
        if (_countdownCoroutine != null)
        {
            return;
        }

        switch (_currentStage)
        {
            case FlowStage.Welcome:
                _currentStage = FlowStage.DemoOverview;
                break;
            case FlowStage.DemoOverview:
                _currentStage = FlowStage.Hands;
                break;
            case FlowStage.Hands:
                _currentStage = FlowStage.Tracking;
                break;
            case FlowStage.Tracking:
                _currentStage = FlowStage.Grabbing;
                break;
            case FlowStage.Grabbing:
                _currentStage = FlowStage.Task1Overview;
                break;
            case FlowStage.Task1Overview:
                StartCountdown(CameraFeedViewer.DemoTaskType.Task1Stack);
                return;
            case FlowStage.Task1Complete:
                _currentStage = FlowStage.Task2Overview;
                break;
            case FlowStage.Task2Overview:
                StartCountdown(CameraFeedViewer.DemoTaskType.Task2Placement);
                return;
            case FlowStage.Task2Complete:
                _currentStage = FlowStage.Task3Overview;
                break;
            case FlowStage.Task3Overview:
                StartCountdown(CameraFeedViewer.DemoTaskType.Task3DoubleStack);
                return;
            case FlowStage.Task3Complete:
                RestartDemo();
                return;
            default:
                return;
        }

        ShowCurrentStage();
    }

    private void StartCountdown(CameraFeedViewer.DemoTaskType taskType)
    {
        _countdownTask = taskType;
        _currentStage = FlowStage.Countdown;
        ShowCurrentStage();
    }

    private void ShowCurrentStage()
    {
        switch (_currentStage)
        {
            case FlowStage.Welcome:
                ShowPage(
                    "Welcome to the Demo!",
                    "Please poke the button on the right with your pointer finger to continue.",
                    false,
                    true,
                    "Continue");
                break;
            case FlowStage.DemoOverview:
                ShowPage(
                    "Demo Overview",
                    "You will be completing three different tasks in this demo.\n\n" +
                    "The demo should not take more than five minutes.\n\n" +
                    "If you do not want to participate, please poke the \"Back\" button.",
                    true,
                    true,
                    "Continue");
                break;
            case FlowStage.Hands:
                ShowPage(
                    "Considerations: Hands",
                    "You will be moving around the colored cubes on the table with your <b>Right Hand Only.</b>\n\n" +
                    "The program will only record the things you do with your Right Hand.\n\n" +
                    "If you are Left-Handed, please try your best.",
                    true,
                    true,
                    "Continue");
                break;
            case FlowStage.Tracking:
                ShowPage(
                    "Considerations: Tracking",
                    "Please do not move the blocks or your hand around too fast.\n\n" +
                    "If the virtual objects begin to act erratic, please move closer to the table.\n\n" +
                    "You will get better results if you take your time.",
                    true,
                    true,
                    "Continue");
                break;
            case FlowStage.Grabbing:
                ShowPage(
                    "Considerations: Grabbing",
                    "Try not to obscure the markers on the sides of the blocks unless needed.\n\n" +
                    "Grabbing the blocks by their edges is an effective way to move them.",
                    true,
                    true,
                    "Continue");
                break;
            case FlowStage.Task1Overview:
                ShowPage(
                    "Task 1: 3-block Stack",
                    "Your first task is to stack three blocks on top of each other in the order specified.\n\n" +
                    "Please stack the first block onto the colored circle.\n\n" +
                    "The colored cubes next to the circle will show you the order to stack the blocks.",
                    true,
                    true,
                    "Start!");
                break;
            case FlowStage.Countdown:
                ShowPage(
                    GetCountdownHeader(),
                    string.Empty,
                    false,
                    false,
                    string.Empty);
                if (_countdownCoroutine == null)
                {
                    _countdownCoroutine = StartCoroutine(RunCountdown());
                }
                break;
            case FlowStage.Task1Complete:
                ShowPage(
                    "Task 1 Complete!",
                    "You completed the task!\n\n" +
                    "I hope you got a feel for how you should move the blocks around.\n\n" +
                    "Poke the \"Continue\" button to move to the next task.",
                    false,
                    true,
                    "Continue");
                break;
            case FlowStage.Task2Overview:
                ShowPage(
                    "Task 2 Overview",
                    "The next task is to position the blocks in their specified locations.\n\n" +
                    "Place the blocks on the colored circles.\n\n" +
                    "You will not need to stack any blocks for this task.",
                    true,
                    true,
                    "Start");
                break;
            case FlowStage.Task2Complete:
                ShowPage(
                    "Task 2 Complete!",
                    "You completed the task!\n\n" +
                    "If you would like to view the recordings of your hands, now's probably the time to do so.\n\n" +
                    "Otherwise, there is one more task to complete.",
                    false,
                    true,
                    "Continue");
                break;
            case FlowStage.Task3Overview:
                ShowPage(
                    "Task 3: 2 Stacks of 2",
                    "The final task is to stack two towers of two blocks each.\n\n" +
                    "You should be familiar with how to stack and move the blocks by now.",
                    true,
                    true,
                    "Start");
                break;
            case FlowStage.Task3Complete:
                ShowPage(
                    "Task 3 Complete!",
                    "You completed task 3!\n\n" +
                    "This is the end of the VR portion of the demo. Please remove the headset.\n\n" +
                    "Thank you for participating!",
                    false,
                    true,
                    "Continue");
                break;
        }
    }

    private void ShowPage(string header, string body, bool showLeftButton, bool showRightButton, string rightButtonLabel)
    {
        SetCanvasVisible(true);

        if (headerText != null)
        {
            headerText.text = header;
        }

        if (instructionalText != null)
        {
            instructionalText.text = body;
        }

        if (leftButton != null)
        {
            leftButton.gameObject.SetActive(showLeftButton);
            leftButton.SetIdleText("Back");
            leftButton.SetPressedText("Back");
        }

        if (rightButton != null)
        {
            rightButton.gameObject.SetActive(showRightButton);
            rightButton.SetIdleText(rightButtonLabel);
            rightButton.SetPressedText(rightButtonLabel);
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

        CameraFeedViewer.DemoTaskType taskToStart = _countdownTask;
        _countdownTask = CameraFeedViewer.DemoTaskType.None;
        _countdownCoroutine = null;

        if (cameraFeedViewer != null)
        {
            switch (taskToStart)
            {
                case CameraFeedViewer.DemoTaskType.Task1Stack:
                    cameraFeedViewer.BeginStackingTask();
                    break;
                case CameraFeedViewer.DemoTaskType.Task2Placement:
                    cameraFeedViewer.BeginColorPlacementTask();
                    break;
                case CameraFeedViewer.DemoTaskType.Task3DoubleStack:
                    cameraFeedViewer.BeginDoubleStackTask();
                    break;
            }
        }

        if (recorder != null)
        {
            recorder.StartRecordingIfNeeded();
        }

        SetCanvasVisible(false);
    }

    private void HandleTaskCompleted(CameraFeedViewer.DemoTaskType completedTask)
    {
        if (recorder != null)
        {
            recorder.StopRecordingIfNeeded();
        }

        switch (completedTask)
        {
            case CameraFeedViewer.DemoTaskType.Task1Stack:
                _currentStage = FlowStage.Task1Complete;
                break;
            case CameraFeedViewer.DemoTaskType.Task2Placement:
                _currentStage = FlowStage.Task2Complete;
                break;
            case CameraFeedViewer.DemoTaskType.Task3DoubleStack:
                _currentStage = FlowStage.Task3Complete;
                break;
            default:
                return;
        }

        ShowCurrentStage();
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

    private string GetCountdownHeader()
    {
        switch (_countdownTask)
        {
            case CameraFeedViewer.DemoTaskType.Task2Placement:
                return "Task 2 Beginning!";
            case CameraFeedViewer.DemoTaskType.Task3DoubleStack:
                return "Task 3 Beginning!";
            default:
                return "Task 1 Beginning!";
        }
    }

    private void RestartDemo()
    {
        Scene activeScene = SceneManager.GetActiveScene();
        if (activeScene.IsValid())
        {
            SceneManager.LoadScene(activeScene.buildIndex);
        }
    }
}
