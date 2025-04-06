using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MoveRelativeToCamera : MonoBehaviour
{
    [SerializeField]
    private Transform cameraOffset;

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    private void LateUpdate()
    {
        transform.position = cameraOffset.position + new Vector3(0, 0, 1);
    }
}
