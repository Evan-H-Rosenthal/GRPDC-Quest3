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

    private void OnEnable()
    {
        if (cameraFeedViewer == null)
        {
            cameraFeedViewer = FindFirstObjectByType<CameraFeedViewer>();
        }

        if (leftButton != null)
        {
            leftButton.Clicked += HandleLeftButtonClicked;
        }

        if (rightButton != null)
        {
            rightButton.Clicked += HandleRightButtonClicked;
        }

        _currentPageIndex = IdlePageIndex;
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

        if (canvasRoot != null)
        {
            canvasRoot.SetActive(false);
        }
        else
        {
            gameObject.SetActive(false);
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
