using System;
using StarterAssets;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class ClientPlayerMove : NetworkBehaviour
{
   [SerializeField] private PlayerInput playerInput;
   [SerializeField] private StarterAssetsInputs starterAssetsInputs;
   [SerializeField] private ThirdPersonController thirdPersonController;

   private void Awake()
   {
      starterAssetsInputs.enabled = false;
      thirdPersonController.enabled = false;
      playerInput.enabled = false;
   }

   public override void OnNetworkSpawn()
   {
      base.OnNetworkSpawn();
      if (IsOwner)
      {
         starterAssetsInputs.enabled = true;
         thirdPersonController.enabled = true;
         playerInput.enabled = true;
      }
   }
}
