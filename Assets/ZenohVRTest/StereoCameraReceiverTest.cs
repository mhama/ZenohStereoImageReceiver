using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Pool;
using Zenoh;
using SkiaSharp;

public class StereoCameraReceiverTest : MonoBehaviour
{
    private Session session;
    private Subscriber subscriber;
    private KeyExpr keyExpr;

    [SerializeField]
    private string keyExprSrc = "rpi/camera/image_jpeg";

    private bool initialized = false;
    
    private byte[] managedBuffer;
    private object obj = new object();
    private Texture2D texture;
    private SynchronizationContext syncContext;

    [SerializeField]
    private TextAsset zenohConfigText;
    
    // Reference to the renderer
    [SerializeField] 
    private Renderer targetRenderer;
    
    // Flag indicating if the texture has been updated
    private bool textureUpdated = false;

    private ObjectPool<byte[]> jpegBufferObjectPool = new ObjectPool<byte[]>(
        createFunc: () => new byte[1],
        actionOnGet: obj => { },
        actionOnRelease: obj => { },
        actionOnDestroy: obj => { },
        collectionCheck: true,
        defaultCapacity: 1,
        maxSize: 10
    );

    private ObjectPool<byte[]> rgbaBufferObjectPool = new ObjectPool<byte[]>(
        createFunc: () => new byte[1],
        actionOnGet: obj => { },
        actionOnRelease: obj => { },
        actionOnDestroy: obj => { },
        collectionCheck: true,
        defaultCapacity: 1,
        maxSize: 10
    );

    void Start()
    {
        syncContext = SynchronizationContext.Current;
        // Initialize the lock object
        if (obj == null) obj = new object();
        
        // If no target renderer is set, use this object's renderer
        if (targetRenderer == null)
            targetRenderer = GetComponent<Renderer>();
            
        // Create and open Zenoh session
        session = new Session();
        string conf = zenohConfigText == null ? null : zenohConfigText?.text;
        var result = session.Open(conf);

        if (!result.IsOk)
        {
            Debug.LogError("Failed to open session");
            return;
        }
        
        // Create subscriber
        StartCoroutine(TestSubscriber());

        texture = new Texture2D(1, 1);
    }

    void Update()
    {
        // Only update the material if the texture has been updated
        if (textureUpdated && texture != null && targetRenderer != null)
        {
            targetRenderer.material.mainTexture = texture;
            textureUpdated = false;
            Debug.Log($"Material {targetRenderer.material} texture updated");
        }
    }

    void OnDestroy()
    {
        if (initialized)
        {
            // Close the session
            session.Close();
            initialized = false;
        }
        
        // Release resources
        if (keyExpr != null)
        {
            keyExpr.Dispose();
            keyExpr = null;
        }
        
        if (subscriber != null)
        {
            subscriber.Dispose();
            subscriber = null;
        }
        
        if (session != null)
        {
            session.Dispose();
            session = null;
        }
    }
     
    // Callback for when a sample is received
    private void OnSampleReceived(SampleRef sample)
    {
        // Get data from the sample
        byte[] data = sample.GetPayload().ToByteArray();
        string keyExpr = sample.GetKeyExprRef().ToString();
        
        Debug.Log($"received: keyexpr: {keyExpr}");
        
        // Update the managed buffer inside a lock
        lock(obj)
        {
            if (managedBuffer == null || managedBuffer.Length < data.Length)
            {
                managedBuffer = new byte[data.Length];
            }
            Array.Copy(data, managedBuffer, data.Length);
        }

        Process().Forget();
    }

    private async UniTask Process()
    {
        await UniTask.SwitchToMainThread();

        byte[] textureCopy = null;
        byte[] rgbaBuffer = null;
        try
        {
            textureCopy = jpegBufferObjectPool.Get();
            if (textureCopy.Length < managedBuffer.Length)
            {
                textureCopy = new byte[managedBuffer.Length];
            }

            rgbaBuffer = rgbaBufferObjectPool.Get();

            Array.Copy(managedBuffer, textureCopy, managedBuffer.Length);

            int width, height, rowBytes;
            
            await using (UniTask.ReturnToMainThread())
            {
                await UniTask.SwitchToThreadPool();
                var result = SkiaSharpHelper.DecodeJpegToRgba(textureCopy, ref rgbaBuffer, out width, out height, out rowBytes);
            }
            
            // Load JPEG image data into the texture
            if (texture != null && textureCopy != null && textureCopy.Length > 0)
            {
                if (texture.width != width || texture.height != height)
                {
                    if (texture != null)
                    {
                        UnityEngine.Object.Destroy(texture);
                    }
                    texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    textureUpdated = true;
                }
                texture.LoadRawTextureData(rgbaBuffer);
                texture.Apply();
                //Debug.Log($"Texture updated: {texture.width}x{texture.height}");
            }
            else
            {
                Debug.LogWarning("Failed to update texture: texture or buffer is null/empty");
            }

        }
        catch (Exception e)
        {
            Debug.LogError($"Error updating texture: {e.Message}\n{e.StackTrace}");
        }
        finally
        {
            await UniTask.SwitchToMainThread();
            if (textureCopy != null)
            {
                jpegBufferObjectPool.Release(textureCopy);
            }

            if (rgbaBuffer != null)
            {
                rgbaBufferObjectPool.Release(rgbaBuffer);
            }
        }
    }
    
    private IEnumerator TestSubscriber()
    {
        CreateSubscriber(keyExprSrc);
        initialized = true;
        yield return new WaitForSeconds(5.0f);
    }
    
    // Example of creating a subscriber
    private void CreateSubscriber(string keyExprStr)
    {
        keyExpr = new KeyExpr(keyExprStr);
        subscriber = new Subscriber();
        
        // Register subscriber with callback
        var result = subscriber.CreateSubscriber(session, keyExpr, OnSampleReceived);
        
        Debug.Log($"Subscriber created for key expression: {keyExprStr}");
    }
}

public static class SkiaSharpHelper
{
    /// <summary>
    /// JPEG データを RGBA8888 にデコードし、ユーザー側のバッファに直接書き込みます。
    /// バッファのサイズが足りない場合は false を返します。
    /// </summary>
    /// <param name="jpegData">JPEG 形式のバイト配列</param>
    /// <param name="pixels">ユーザー側で用意したバッファ</param>
    /// <param name="width">実際にデコードした画像の幅を返す</param>
    /// <param name="height">実際にデコードした画像の高さを返す</param>
    /// <param name="rowBytes">横1行のバイト数を返す（= width * 4 が基本だが整合性に注意）</param>
    /// <returns>デコードに成功すれば true、失敗またはバッファ不足なら false</returns>
    public static bool DecodeJpegToRgba(
        byte[] jpegData, 
        ref byte[] pixels, 
        out int width, 
        out int height, 
        out int rowBytes)
    {
        width = 0;
        height = 0;
        rowBytes = 0;

        using var ms = new MemoryStream(jpegData);
        
        // SKCodec で JPEG データを開く
        using var codec = SKCodec.Create(ms);
        if (codec == null)
        {
            return false;
        }

        // 画像サイズ等を取得
        var info = codec.Info;
        width = info.Width;
        height = info.Height;

        // RGBA8888 形式での画像情報を構築
        var decodeInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        rowBytes = decodeInfo.RowBytes; // 一行あたりのバイト数

        // バッファサイズが足りるかチェック
        int requiredSize = rowBytes * height;
        if (pixels.Length < requiredSize)
        {
            // recreate buffer
            Debug.Log($"Recreating RGBA buffer. pixels.Length:{pixels.Length} requiredSize:{requiredSize}");
            pixels = new byte[requiredSize];
        }

        // ユーザー側のバッファに直接デコード
        SKCodecResult result = codec.GetPixels(decodeInfo, pixels);
        return (result == SKCodecResult.Success || result == SKCodecResult.IncompleteInput);
    }
}
