using UnityEngine;
[CreateAssetMenu(menuName = "Abilities/DashAbility ")]
public class DashAbility : Ability
{
    [SerializeField] private float dashDistance = 3f;

    protected override void Activate()
    {
        transform.position += transform.forward * dashDistance;
    }
}