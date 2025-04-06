using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ScreenScaleAdjuster : MonoBehaviour
{
    [SerializeField] private float horizontalFov = 70f;
    
    [SerializeField] private float aspectRatio = 1.6f;
    
    // Start is called before the first frame update
    void Start()
    {
        float scaleX = Mathf.Tan(horizontalFov * 0.5f * Mathf.Deg2Rad) * 2;
        float scaleY = scaleX / aspectRatio;
        transform.localScale = new Vector3(scaleX, scaleY, 1f);
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    float ToRadian(float degree)
    {
        return degree * Mathf.Deg2Rad;
    }
}
