using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System;

public class CanvasPokeButtonFeedback : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler, IPointerClickHandler
{
    [SerializeField] private Image targetImage;
    [SerializeField] private Text targetText;
    [SerializeField] private string idleText = "Poke Me";
    [SerializeField] private string pressedText = "Poked!";
    [SerializeField] private Color idleColor = new Color(0.18f, 0.34f, 0.78f, 0.98f);
    [SerializeField] private Color pressedColor = new Color(0.09f, 0.72f, 0.35f, 1f);
    [SerializeField] private UnityEvent onClicked;

    private bool _isPressed;

    public event Action Clicked;

    public void SetIdleText(string value)
    {
        idleText = value;
        ApplyVisualState();
    }

    public void SetPressedText(string value)
    {
        pressedText = value;
        ApplyVisualState();
    }

    private void OnEnable()
    {
        _isPressed = false;
        ApplyVisualState();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        _isPressed = true;
        ApplyVisualState();
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        _isPressed = false;
        ApplyVisualState();
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        _isPressed = false;
        ApplyVisualState();
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        Clicked?.Invoke();
        onClicked?.Invoke();
    }

    private void ApplyVisualState()
    {
        if (targetImage != null)
        {
            targetImage.color = _isPressed ? pressedColor : idleColor;
        }

        if (targetText != null)
        {
            targetText.text = _isPressed ? pressedText : idleText;
        }
    }
}
