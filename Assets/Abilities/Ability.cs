using UnityEngine;

public abstract class Ability : MonoBehaviour
{
    protected AbilitySystem abilitySystem;

    public void Initialize(AbilitySystem system)
    {
        abilitySystem = system;
    }

    // Проверка: можно ли активировать способность
    public virtual bool CanActivate()
    {
        return abilitySystem.HasCrystal();
    }

    // Попытка активации
    public void TryActivate()
    {
        if (!CanActivate())
            return;

        abilitySystem.ConsumeCrystal();
        Activate();
    }

    // Конкретная логика способности
    protected abstract void Activate();
}