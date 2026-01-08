using UnityEngine;

public class AbilitySystem : MonoBehaviour
{
    [Header("Crystals")]
    [SerializeField] private int crystals = 0;

    private Ability currentAbility;

    public void SetAbility(Ability ability)
    {
        currentAbility = ability;
        currentAbility.Initialize(this);
    }

    public void TryUseAbility()
    {
        if (currentAbility == null)
            return;

        currentAbility.TryActivate();
    }

    public bool HasCrystal()
    {
        return crystals > 0;
    }

    public void AddCrystal(int amount = 1)
    {
        crystals += amount;
    }

    public void ConsumeCrystal()
    {
        crystals = Mathf.Max(0, crystals - 1);
    }
}