using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
public class PlayerTeleport : NetworkBehaviour
{
    public GameObject player;

    private void OnEnable()
    {
        GameObject start =  GameObject.FindGameObjectWithTag("PlayerStart");
        
            if (!IsServer) return;
            {
                player.transform.position = start.transform.position;
            }
            
        
    }

    
}
