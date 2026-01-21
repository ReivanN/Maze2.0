using UnityEngine;
using UnityEngine.InputSystem;

public class AbilityInput : MonoBehaviour
{
    [SerializeField] private AbilitySystem abilitySystem;

    private void Update()
    {
        if (Keyboard.current.ctrlKey.wasPressedThisFrame)
        {
            abilitySystem.TryUseAbility();
        }
    }
}