using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>Hover and click sounds for a UI button. Added in code by UiTheme and RelayManager.</summary>
[RequireComponent(typeof(UnityEngine.UI.Button))]
public class UiButtonSounds : MonoBehaviour, IPointerEnterHandler
{
    private UnityEngine.UI.Button button;

    private void Awake()
    {
        button = GetComponent<UnityEngine.UI.Button>();
        button.onClick.AddListener(() => AudioManager.Play(Sfx.UiClick));
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (button.IsInteractable())
            AudioManager.Play(Sfx.UiHover);
    }
}
