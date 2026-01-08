using UnityEngine;

public class AbilityInitializer : MonoBehaviour
{
    [SerializeField] private AbilitySystem abilitySystem;
    [SerializeField] private AbilityData abilityData;

    private void Start()
    {
        GameObject abilityInstance = Instantiate(
            abilityData.abilityPrefab,
            abilitySystem.transform
        );

        Ability ability = abilityInstance.GetComponent<Ability>();
        abilitySystem.SetAbility(ability);
    }
}