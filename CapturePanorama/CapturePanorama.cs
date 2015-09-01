// This is free and unencumbered software released into the public domain.
// For more information, please refer to <http://unlicense.org/>

// Uncomment to use in a VR project using the Oculus VR plugin
// This will avoid issues in which the captured panorama is pitched/rolled
// when the player pitches/rolls their headset.
//#define OVR_SUPPORT

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using Bitmap = System.Drawing.Bitmap;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;
using ImageLockMode = System.Drawing.Imaging.ImageLockMode;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Process = System.Diagnostics.Process;
using Rectangle = System.Drawing.Rectangle;
#if UNITY_5_1
using UnityEngine.VR;
#endif

public class CapturePanorama : MonoBehaviour
{
    public string panoramaName;
    public KeyCode captureKey = KeyCode.P;
    public ImageFormat imageFormat = ImageFormat.PNG;
    public int panoramaWidth = 8192;
    public AntiAliasing antiAliasing = AntiAliasing._8;
    public int ssaaFactor = 1;
    public string saveImagePath = "";
    public bool saveCubemap = false;
    public bool uploadImages = false;
    public bool useDefaultOrientation = false;
    public bool useGpuTransform = true;
    public float cpuMillisecondsPerFrame = 1000.0f / 120.0f;
    public bool captureEveryFrame = false;
    public int frameRate = 30;
    public int frameNumberDigits = 6;
    public AudioClip startSound;
    public AudioClip doneSound;
    public bool fadeDuringCapture = true;
    public float fadeTime = 0.25f;
    public Color fadeColor = new Color(0.0f, 0.0f, 0.0f, 1.0f);
    public Material fadeMaterial = null;
    public ComputeShader convertPanoramaShader;
    public ComputeShader copyShader;
    public bool enableDebugging = false;

    public enum ImageFormat { PNG, JPEG, BMP };
    public enum AntiAliasing { _1 = 1, _2 = 2, _4 = 4, _8 = 8 };

    string apiUrl = "http://alpha.vrchive.com/api/1/";
    string apiKey = "0b26e4dca20793a83fd92ad83e3e859e";

    GameObject go = null;
    Camera cam;
    Texture2D[] cubemapTexs = null;
    RenderTexture[] cubemapRenderTexturesCopy = null;

    bool capturingEveryFrame = false;
    bool usingGpuTransform;
    CubemapFace[] faces;
    CubemapFace[] faces2;
    int panoramaHeight, cubemapSize;
    RenderTexture cubemapRenderTexture = null;
    int convertPanoramaKernelIdx = -1, convertPanoramaYPositiveKernelIdx = -1, convertPanoramaYNegativeKernelIdx = -1, copyKernelIdx = -1;
    int[] convertPanoramaKernelIdxs;
    byte[] imageFileBytes;
    string videoBaseName = "";
    private int frameNumber;
    ComputeBuffer convertPanoramaResultBuffer, forceWaitResultBuffer;
    const int ResultBufferSlices = 8; // Makes result buffer same pixel count as a cubemap texture

    int lastConfiguredPanoramaWidth, lastConfiguredSsaaFactor;
    bool lastConfiguredSaveCubemap, lastConfiguredUseGpuTransform;
    AntiAliasing lastConfiguredAntiAliasing = AntiAliasing._1;

    DrawingImageFormat FormatToDrawingFormat(ImageFormat format)
    {
        switch (format)
        {
            case ImageFormat.PNG:  return DrawingImageFormat.Png;
            case ImageFormat.JPEG: return DrawingImageFormat.Jpeg;
            case ImageFormat.BMP:  return DrawingImageFormat.Bmp;
            default: Debug.Assert(false); return DrawingImageFormat.Png;
        }
    }

    string FormatMimeType(ImageFormat format)
    {
        switch (format)
        {
            case ImageFormat.PNG:  return "image/png";
            case ImageFormat.JPEG: return "image/jpeg";
            case ImageFormat.BMP:  return "image/bmp";
            default: Debug.Assert(false); return "";
        }
    }

    string FormatToExtension(ImageFormat format)
    {
        switch (format)
        {
            case ImageFormat.PNG:  return "png";
            case ImageFormat.JPEG: return "jpg";
            case ImageFormat.BMP:  return "bmp";
            default: Debug.Assert(false); return "";
        }
    }

    void Start()
    {
        Reinitialize();
    }

    void Reinitialize() {
        Log("Settings changed, calling Reinitialize()");

        lastConfiguredPanoramaWidth = panoramaWidth;
        lastConfiguredSsaaFactor = ssaaFactor;
        lastConfiguredAntiAliasing = antiAliasing;
        lastConfiguredSaveCubemap = saveCubemap;
        lastConfiguredUseGpuTransform = useGpuTransform;

        panoramaHeight = panoramaWidth / 2;
        cubemapSize = panoramaWidth * ssaaFactor / 4;

        if (go != null)
            Destroy(go);

        go = new GameObject("CubemapCamera");
        go.AddComponent<Camera>();
        go.hideFlags = HideFlags.HideAndDontSave;

        cam = go.GetComponent<Camera>();
        cam.enabled = false;

        usingGpuTransform = useGpuTransform && convertPanoramaShader != null && SystemInfo.supportsComputeShaders;

        faces = new CubemapFace[] {
			CubemapFace.PositiveX, CubemapFace.NegativeX,
			CubemapFace.PositiveY, CubemapFace.NegativeY,
			CubemapFace.PositiveZ, CubemapFace.NegativeZ };

        faces2 = new CubemapFace[] {
			CubemapFace.PositiveX, CubemapFace.NegativeX,
			CubemapFace.PositiveY, CubemapFace.NegativeY,
			CubemapFace.NegativeZ, CubemapFace.PositiveZ };

        if (cubemapRenderTexturesCopy != null)
            foreach (RenderTexture tex in cubemapRenderTexturesCopy)
                Destroy(tex);

        cubemapRenderTexturesCopy = new RenderTexture[6];
        foreach (CubemapFace face in faces2)
        {
            cubemapRenderTexturesCopy[(int)face] = new RenderTexture(cubemapSize, cubemapSize, /*depth*/0, RenderTextureFormat.ARGB32);
            cubemapRenderTexturesCopy[(int)face].enableRandomWrite = true;
            cubemapRenderTexturesCopy[(int)face].antiAliasing = 1; // Must be 1 to avoid Unity ReadPixels() bug
            cubemapRenderTexturesCopy[(int)face].Create();
        }

        if (cubemapTexs != null)
            foreach (Texture2D tex in cubemapTexs)
                Destroy(tex);

        cubemapTexs = null;
        if (saveCubemap || !usingGpuTransform)
        {
            cubemapTexs = new Texture2D[6];
            foreach (CubemapFace face in faces2)
            {
                cubemapTexs[(int)face] = new Texture2D(cubemapSize, cubemapSize, TextureFormat.RGB24, /*mipmap*/false, /*linear*/true);
                cubemapTexs[(int)face].Apply(/*updateMipmaps*/false, /*makeNoLongerReadable*/false);
            }
        }

        if (cubemapRenderTexture != null)
            Destroy(cubemapRenderTexture);

        cubemapRenderTexture = new RenderTexture(cubemapSize, cubemapSize, /*depth*/24, RenderTextureFormat.ARGB32);
        cubemapRenderTexture.antiAliasing = (int)antiAliasing;
        cubemapRenderTexture.Create();

        if (usingGpuTransform)
        {
            convertPanoramaKernelIdx = convertPanoramaShader.FindKernel("CubeMapToEquirectangular");
            convertPanoramaYPositiveKernelIdx = convertPanoramaShader.FindKernel("CubeMapToEquirectangularPositiveY");
            convertPanoramaYNegativeKernelIdx = convertPanoramaShader.FindKernel("CubeMapToEquirectangularNegativeY");
            convertPanoramaKernelIdxs = new int[] { convertPanoramaKernelIdx, convertPanoramaYPositiveKernelIdx, convertPanoramaYNegativeKernelIdx };

            foreach (CubemapFace face in faces)
            {
                foreach (int kernelIdx in convertPanoramaKernelIdxs)
                    convertPanoramaShader.SetTexture(kernelIdx, "cubemapFace" + face.ToString(), cubemapRenderTexturesCopy[(int)face]);
            }
            convertPanoramaShader.SetInt("equirectangularWidth", panoramaWidth);
            convertPanoramaShader.SetInt("equirectangularHeight", panoramaHeight);
            convertPanoramaShader.SetInt("ssaaFactor", ssaaFactor);
            convertPanoramaShader.SetInt("cubemapSize", cubemapSize);
        }

        copyKernelIdx = copyShader.FindKernel("Copy");
        copyShader.SetTexture(copyKernelIdx, "source", cubemapRenderTexture);
        copyShader.SetInt("width", cubemapSize);
        copyShader.SetInt("height", cubemapSize);
    }

    void Log(string s)
    {
        if (enableDebugging)
            Debug.Log(s, this);
    }

    void Update()
    {
        if (panoramaWidth <= 3) // Can occur temporarily while modifying Panorama Width property in editor
            return;
        if (panoramaWidth != lastConfiguredPanoramaWidth ||
            ssaaFactor != lastConfiguredSsaaFactor ||
            antiAliasing != lastConfiguredAntiAliasing ||
            saveCubemap != lastConfiguredSaveCubemap ||
            useGpuTransform != lastConfiguredUseGpuTransform)
        {
            Reinitialize();
        }

        if (capturingEveryFrame)
        {
            if (captureKey != KeyCode.None && Input.GetKeyDown(captureKey))
            {
                StopCaptureEveryFrame();
            }
            else
            {
                CaptureScreenshotSync(videoBaseName + "_" + frameNumber.ToString(new String('0', frameNumberDigits)));
                frameNumber += 1;
            }
        }
        else if (captureKey != KeyCode.None && Input.GetKeyDown(captureKey) && !Capturing)
        {
            if (captureEveryFrame)
            {
                StartCaptureEveryFrame();
            }
            else
            {
                string filenameBase = String.Format("{0}_{1:yyyy-MM-dd_HH-mm-ss-fff}", panoramaName, DateTime.Now);
                Log("Panorama capture key pressed, capturing " + filenameBase);
                CaptureScreenshotAsync(filenameBase);
            }
        }
    }

    public void StartCaptureEveryFrame()
    {
        Time.captureFramerate = frameRate;
        videoBaseName = String.Format("{0}_{1:yyyy-MM-dd_HH-mm-ss-fff}", panoramaName, DateTime.Now);
        frameNumber = 0;

        capturingEveryFrame = true;
    }

    public void StopCaptureEveryFrame()
    {
        Time.captureFramerate = 0;
        capturingEveryFrame = false;
    }

    public void CaptureScreenshotSync(string filenameBase)
    {
        var enumerator = CaptureScreenshotAsyncHelper(filenameBase, /*async*/false);
        while (enumerator.MoveNext()) { }
    }

    public void CaptureScreenshotAsync(string filenameBase)
    {
        StartCoroutine(CaptureScreenshotAsyncHelper(filenameBase, /*async*/true));
    }

    internal bool Capturing;

    static List<Process> resizingProcessList = new List<Process>();
    static List<string> resizingFilenames = new List<string>();

    void SetFadersEnabled(IEnumerable<ScreenFadeControl> fadeControls, bool value)
    {
        foreach (ScreenFadeControl fadeControl in fadeControls)
            fadeControl.enabled = value;
    }

    public IEnumerator FadeOut(IEnumerable<ScreenFadeControl> fadeControls)
    {
        Log("Doing fade out");
        // Derived from OVRScreenFade
        float elapsedTime = 0.0f;
        Color color = fadeColor;
        color.a = 0.0f;
        fadeMaterial.color = color;
        SetFadersEnabled(fadeControls, true);
        while (elapsedTime < fadeTime)
        {
            yield return new WaitForEndOfFrame();
            elapsedTime += Time.deltaTime;
            color.a = Mathf.Clamp01(elapsedTime / fadeTime);
            fadeMaterial.color = color;
        }
    }

    public IEnumerator FadeIn(IEnumerable<ScreenFadeControl> fadeControls)
    {
        Log("Fading back in");
        float elapsedTime = 0.0f;
        Color color = fadeMaterial.color = fadeColor;
        while (elapsedTime < fadeTime)
        {
            yield return new WaitForEndOfFrame();
            elapsedTime += Time.deltaTime;
            color.a = 1.0f - Mathf.Clamp01(elapsedTime / fadeTime);
            fadeMaterial.color = color;
        }
        SetFadersEnabled(fadeControls, false);
    }


    public IEnumerator CaptureScreenshotAsyncHelper(string filenameBase, bool async)
    {
        if (async)
            while (Capturing)
                yield return null; // If CaptureScreenshot() was called programmatically multiple times, serialize the coroutines
        Capturing = true;

        List<ScreenFadeControl> fadeControls = new List<ScreenFadeControl>();
        foreach (Camera c in Camera.allCameras)
        {
            var fadeControl = c.gameObject.AddComponent<ScreenFadeControl>();
            fadeControl.fadeMaterial = fadeMaterial;
            fadeControls.Add(fadeControl);
        }
        SetFadersEnabled(fadeControls, false);

        if (fadeDuringCapture && async)
            yield return StartCoroutine(FadeOut(fadeControls));

        // Make sure black is shown before we start - sometimes two frames are needed
        for (int i = 0; i < 2; i++)
            yield return new WaitForEndOfFrame();

        // Initialize compute buffers - do here instead of in Reinitialize() to work around error on Destroy()
        if (usingGpuTransform)
        {
            convertPanoramaResultBuffer =
                new ComputeBuffer(/*count*/panoramaWidth * (panoramaHeight + ResultBufferSlices - 1) / ResultBufferSlices, /*stride*/4);
            foreach (int kernelIdx in convertPanoramaKernelIdxs)
                convertPanoramaShader.SetBuffer(kernelIdx, "result", convertPanoramaResultBuffer);
        }
        forceWaitResultBuffer = new ComputeBuffer(/*count*/1, /*stride*/4);
        copyShader.SetBuffer(copyKernelIdx, "forceWaitResultBuffer", forceWaitResultBuffer);

        Log("Starting panorama capture");
        if (!captureEveryFrame && startSound != null)
        {
            AudioSource.PlayClipAtPoint(startSound, transform.position);
        }

        float startTime = Time.realtimeSinceStartup;

        Quaternion headOrientation = Quaternion.identity;
#if OVR_SUPPORT
        if (OVRManager.display != null)
        {
            headOrientation = OVRManager.display.GetHeadPose(0.0).orientation;
        }
#endif
#if UNITY_5_1
        if (VRSettings.enabled && VRSettings.loadedDevice != VRDeviceType.None)
        {
            headOrientation = InputTracking.GetLocalRotation(0);
        }
#endif

        var cameras = Camera.allCameras;
        Array.Sort(cameras, (x, y) => x.depth.CompareTo(y.depth));
        Log("Rendering cubemap");
        cam.fieldOfView = 90.0f;

        // Need to extract each cubemap into a Texture2D so we can read the pixels, but Unity bug
        // prevents this with antiAliasing: http://issuetracker.unity3d.com/issues/texture2d-dot-readpixels-fails-if-rendertexture-has-anti-aliasing-set
        // We copy the cubemap textures using a shader as a workaround.

        cam.targetTexture = cubemapRenderTexture;

        foreach (CubemapFace face in faces2)
        {
            foreach (Camera c in cameras)
            {
                if (c.gameObject.name.Contains("RightEye"))
                    continue; // Only render left eyes

                go.transform.position = c.transform.position;

                cam.clearFlags = c.clearFlags;
                cam.backgroundColor = c.backgroundColor;
                cam.cullingMask = c.cullingMask;
                cam.nearClipPlane = c.nearClipPlane;
                cam.farClipPlane = c.farClipPlane;
                cam.renderingPath = c.renderingPath;
                cam.hdr = c.hdr;
                
                var baseRotation = c.transform.rotation;
                baseRotation *= Quaternion.Inverse(headOrientation);
                if (useDefaultOrientation)
                {
                    baseRotation = Quaternion.identity;
                }

                // Don't use RenderToCubemap - it causes problems with compositing multiple cameras, and requires
                // more temporary VRAM. Just render cube map manually.
                switch (face)
                {
                    case CubemapFace.PositiveX: cam.transform.localRotation = baseRotation * Quaternion.Euler(  0.0f,  90.0f, 0.0f); break;
                    case CubemapFace.NegativeX: cam.transform.localRotation = baseRotation * Quaternion.Euler(  0.0f, -90.0f, 0.0f); break;
                    case CubemapFace.PositiveY: cam.transform.localRotation = baseRotation * Quaternion.Euler( 90.0f,   0.0f, 0.0f); break;
                    case CubemapFace.NegativeY: cam.transform.localRotation = baseRotation * Quaternion.Euler(-90.0f,   0.0f, 0.0f); break;
                    case CubemapFace.PositiveZ: cam.transform.localRotation = baseRotation * Quaternion.Euler(  0.0f,   0.0f, 0.0f); break;
                    case CubemapFace.NegativeZ: cam.transform.localRotation = baseRotation * Quaternion.Euler(  0.0f, 180.0f, 0.0f); break;
                }

                cam.Render();
            }

            copyShader.SetTexture(copyKernelIdx, "dest", cubemapRenderTexturesCopy[(int)face]);
            int threadsX = 32, threadsY = 32; // Must match shader
            copyShader.Dispatch(copyKernelIdx, (cubemapSize + threadsX - 1) / threadsX, (cubemapSize + threadsY - 1) / threadsY, 1);
            // Get force wait result to force a wait for the shader to complete
            int[] forceWaitResult = new int[1];
            forceWaitResultBuffer.GetData(forceWaitResult);
        }

        // If we need to access the cubemap pixels on the CPU, retrieve them now
        if (saveCubemap || !usingGpuTransform)
        {
            foreach (CubemapFace face in faces)
            {
                RenderTexture.active = cubemapRenderTexturesCopy[(int)face];
                cubemapTexs[(int)face].ReadPixels(new Rect(0, 0, cubemapSize, cubemapSize), 0, 0);
                cubemapTexs[(int)face].Apply(/*updateMipmaps*/false, /*makeNoLongerReadable*/false);
            }

            RenderTexture.active = null;
        }

        string suffix = "." + FormatToExtension(imageFormat);
        string filePath = "";
        // Save in separate thread to avoid hiccups
        string imagePath = saveImagePath;
        if (imagePath == null || imagePath == "")
        {
            imagePath = Application.dataPath + "/..";
        }

        if (saveCubemap)
        {
            // Save cubemap while still faded, as fast as possible - should be pretty quick
            foreach (CubemapFace face in faces)
            {
                Color32[] texPixels = cubemapTexs[(int)face].GetPixels32();

                Bitmap bitmap = new Bitmap(cubemapSize, cubemapSize, PixelFormat.Format32bppArgb);
                var bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
                IntPtr ptr = bmpData.Scan0;
                byte[] pixelValues = new byte[Math.Abs(bmpData.Stride) * bitmap.Height];
                int stride = bmpData.Stride;
                int width = cubemapSize;
                int height = bmpData.Height;
                for (int y = 0; y < cubemapSize; y++)
                for (int x = 0; x < cubemapSize; x++)
                {
                    Color32 c = texPixels[y * width + x];
                    int baseIdx = stride * (height - 1 - y) + x * 4;
                    pixelValues[baseIdx + 0] = c.b;
                    pixelValues[baseIdx + 1] = c.g;
                    pixelValues[baseIdx + 2] = c.r;
                    pixelValues[baseIdx + 3] = c.a;
                }
                System.Runtime.InteropServices.Marshal.Copy(pixelValues, 0, ptr, pixelValues.Length);
                bitmap.UnlockBits(bmpData);

                Log("Saving cubemap image " + face.ToString());
                string cubeFilepath = imagePath + "/" + filenameBase + "_" + face.ToString() + suffix;
                // TODO: Use better image processing library to get decent JPEG quality out.
                bitmap.Save(cubeFilepath, FormatToDrawingFormat(imageFormat));
                bitmap.Dispose();
            }
        }

        // If this is not here, the fade-in will drop frames.
        for (int i = 0; i < 2; i++)
            yield return new WaitForEndOfFrame();

        if (async && !usingGpuTransform && fadeDuringCapture)
            yield return StartCoroutine(FadeIn(fadeControls));

        // Convert to equirectangular projection - use compute shader for better performance if supported by platform

        // Write pixels directly to .NET Bitmap for saving out
        // Based on https://msdn.microsoft.com/en-us/library/5ey6h79d%28v=vs.110%29.aspx
        filePath = imagePath + "/" + filenameBase + suffix;
        {
            Bitmap bitmap = new Bitmap(panoramaWidth, panoramaHeight, PixelFormat.Format32bppArgb);
            var bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            IntPtr ptr = bmpData.Scan0;
            byte[] pixelValues = new byte[Math.Abs(bmpData.Stride) * bitmap.Height];

            if (async)
                yield return StartCoroutine(CubemapToEquirectangular(cubemapTexs, cubemapSize, pixelValues, bmpData.Stride, panoramaWidth, panoramaHeight, ssaaFactor, async));
            else
            {
                var enumerator = CubemapToEquirectangular(cubemapTexs, cubemapSize, pixelValues, bmpData.Stride, panoramaWidth, panoramaHeight, ssaaFactor, async);
                while (enumerator.MoveNext()) { }
            }

            yield return null;
            System.Runtime.InteropServices.Marshal.Copy(pixelValues, 0, ptr, pixelValues.Length);
            bitmap.UnlockBits(bmpData);
            yield return null;

            Log("Time to take panorama screenshot: " + (Time.realtimeSinceStartup - startTime) + " sec");

            if (convertPanoramaResultBuffer != null)
                convertPanoramaResultBuffer.Release();
            convertPanoramaResultBuffer = null;
            if (forceWaitResultBuffer != null)
                forceWaitResultBuffer.Release();
            forceWaitResultBuffer = null;

            var thread = new Thread(() =>
            {
                Log("Saving equirectangular image");
                // TODO: Use better image processing library to get decent JPEG quality out.
                bitmap.Save(filePath, FormatToDrawingFormat(imageFormat));
            });
            thread.Start();
            while (thread.ThreadState == ThreadState.Running)
                if (async)
                    yield return null;
                else
                    Thread.Sleep(0);

            bitmap.Dispose();
        }

        if (async && usingGpuTransform && fadeDuringCapture)
            yield return StartCoroutine(FadeIn(fadeControls));

        foreach (ScreenFadeControl fadeControl in fadeControls)
        {
            Destroy(fadeControl);
        }
        fadeControls.Clear();

        if (uploadImages && !captureEveryFrame)
        {
            Log("Uploading image");
            imageFileBytes = File.ReadAllBytes(filePath);
            string mimeType = FormatMimeType(imageFormat);
            if (async)
                yield return StartCoroutine(UploadImage(imageFileBytes, filenameBase + suffix, mimeType, async));
            else
            {
                var enumerator = UploadImage(imageFileBytes, filenameBase + suffix, mimeType, async);
                while (enumerator.MoveNext()) { }
            }
        }
        else
        {
            if (!captureEveryFrame && doneSound != null)
            {
                AudioSource.PlayClipAtPoint(doneSound, transform.position);
            }
            Capturing = false;
        }
    }

    internal void ClearProcessQueue()
    {
        while (resizingProcessList.Count > 0)
        {
            resizingProcessList[0].WaitForExit();
            File.Delete(resizingFilenames[0]);
            resizingProcessList.RemoveAt(0);
            resizingFilenames.RemoveAt(0);
        }
    }

    // Based on http://docs.unity3d.com/ScriptReference/WWWForm.html and
    // http://answers.unity3d.com/questions/48686/uploading-photo-and-video-on-a-web-server.html
    IEnumerator UploadImage(byte[] imageFileBytes, string filename, string mimeType, bool async)
    {
        float startTime = Time.realtimeSinceStartup;

        WWWForm form = new WWWForm();

        form.AddField("key", apiKey);
        form.AddField("action", "upload");
        form.AddBinaryData("source", imageFileBytes, filename, mimeType);

        WWW w = new WWW(apiUrl + "upload", form);
        yield return w;
        if (!string.IsNullOrEmpty(w.error))
        {
            Debug.LogError(w.error, this);
        }
        else
        {
            Log("Time to upload panorama screenshot: " + (Time.realtimeSinceStartup - startTime) + " sec");
            if (!captureEveryFrame && doneSound != null)
            {
                AudioSource.PlayClipAtPoint(doneSound, transform.position);
            }
            Capturing = false;
        }
    }

    IEnumerator CubemapToEquirectangular(Texture2D[] cubemapTexs, int cubemapSize, byte[] pixelValues,
        int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, bool async)
    {
        if (usingGpuTransform)
        {
            int sliceHeight = (panoramaHeight + ResultBufferSlices - 1) / ResultBufferSlices;

            Log("Invoking GPU shader for equirectangular reprojection");
            int threadsX = 32, threadsY = 32; // Must match shader
            int[] resultPixels = new int[panoramaWidth * sliceHeight];
            int endYNegative   = (int)Mathf.Floor(panoramaHeight * 0.25f);
            int startYPositive = (int)Mathf.Ceil(panoramaHeight * 0.75f);
            for (int sliceNum = 0; sliceNum < ResultBufferSlices; sliceNum++)
            {
                int startSlice = sliceNum * sliceHeight;
                int endSlice = Math.Min(startSlice + sliceHeight, panoramaHeight);
                convertPanoramaShader.SetInt("startY", sliceNum * sliceHeight);
                if (endSlice <= endYNegative)
                    convertPanoramaShader.Dispatch(convertPanoramaYNegativeKernelIdx, (panoramaWidth + threadsX - 1) / threadsX, (panoramaHeight + threadsY - 1) / threadsY, 1);
                else if (startSlice >= startYPositive)
                    convertPanoramaShader.Dispatch(convertPanoramaYPositiveKernelIdx, (panoramaWidth + threadsX - 1) / threadsX, (panoramaHeight + threadsY - 1) / threadsY, 1);
                else
                    convertPanoramaShader.Dispatch(convertPanoramaKernelIdx, (panoramaWidth + threadsX - 1) / threadsX, (panoramaHeight + threadsY - 1) / threadsY, 1);

                // Copy to pixelValues output array
                convertPanoramaResultBuffer.GetData(resultPixels);
                int inputIdx = 0;
                for (int y = startSlice; y < endSlice; y++)
                {
                    int outputIdx = stride * y;
                    for (int x = 0; x < panoramaWidth; x++)
                    {
                        int packedCol = resultPixels[inputIdx];
                        pixelValues[outputIdx + 0] = (byte)((packedCol >> 0) & 0xFF);
                        pixelValues[outputIdx + 1] = (byte)((packedCol >> 8) & 0xFF);
                        pixelValues[outputIdx + 2] = (byte)((packedCol >> 16) & 0xFF);
                        pixelValues[outputIdx + 3] = 255;
                        outputIdx += 4;
                        inputIdx++;
                    }
                }
            }
        }
        else
        {
            if (async)
                yield return StartCoroutine(CubemapToEquirectangularCpu(cubemapTexs, cubemapSize, pixelValues,
                    stride, panoramaWidth, panoramaHeight, ssaaFactor, async));
            else
            {
                var enumerator = CubemapToEquirectangularCpu(cubemapTexs, cubemapSize, pixelValues,
                    stride, panoramaWidth, panoramaHeight, ssaaFactor, async);
                while (enumerator.MoveNext()) { }
            }
        }
    }

    IEnumerator CubemapToEquirectangularCpu(Texture2D[] cubemapTexs, int cubemapSize, byte[] pixelValues,
        int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, bool async)
    {
        Log("Converting to equirectangular");

        yield return null; // Wait for next frame at beginning - already used up some time capturing snapshot

        float startTime = Time.realtimeSinceStartup;
        float processingTimePerFrame = cpuMillisecondsPerFrame / 1000.0f;
        float maxValue = 1.0f - 1.0f / cubemapSize;
        int numPixelsAveraged = ssaaFactor * ssaaFactor;

        // For efficiency we're going to do a series of rectangles each drawn from only one texture,
        // only using the slow general-case reprojection where necessary.

        int endYPositive   = (int)Mathf.Floor(panoramaHeight * 0.25f);
        int startYNegative = (int)Mathf.Ceil(panoramaHeight * 0.75f);

        // 0.195913f is angle in radians between (1, 0, 1) and (1, 1, 1) over pi
        int endTopMixedRegion      = (int)Mathf.Ceil (panoramaHeight * (0.5f - 0.195913f));
        int startBottomMixedRegion = (int)Mathf.Floor(panoramaHeight * (0.5f + 0.195913f));

        int startXNegative = (int)Mathf.Ceil (panoramaWidth * 1.0f / 8.0f);
        int endXNegative   = (int)Mathf.Floor(panoramaWidth * 3.0f / 8.0f);

        int startZPositive = (int)Mathf.Ceil (panoramaWidth * 3.0f / 8.0f);
        int endZPositive   = (int)Mathf.Floor(panoramaWidth * 5.0f / 8.0f);

        int startXPositive = (int)Mathf.Ceil(panoramaWidth  * 5.0f / 8.0f);
        int endXPositive   = (int)Mathf.Floor(panoramaWidth * 7.0f / 8.0f);

        int startZNegative = (int)Mathf.Ceil(panoramaWidth  * 7.0f / 8.0f);
        int endZNegative   = (int)Mathf.Floor(panoramaWidth * 1.0f / 8.0f); // z negative wraps/loops around

        if (async)
        {
            yield return StartCoroutine(CubemapToEquirectangularCpuPositiveY(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                0, 0, panoramaWidth, endYPositive));
            yield return StartCoroutine(CubemapToEquirectangularCpuNegativeY(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                0, startYNegative, panoramaWidth, panoramaHeight));

            yield return StartCoroutine(CubemapToEquirectangularCpuPositiveX(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                startXPositive, endTopMixedRegion, endXPositive, startBottomMixedRegion));
            yield return StartCoroutine(CubemapToEquirectangularCpuNegativeX(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                startXNegative, endTopMixedRegion, endXNegative, startBottomMixedRegion));
            yield return StartCoroutine(CubemapToEquirectangularCpuPositiveZ(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                startZPositive, endTopMixedRegion, endZPositive, startBottomMixedRegion));

            // Do in two pieces since z negative wraps/loops around
            yield return StartCoroutine(CubemapToEquirectangularCpuNegativeZ(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                startZNegative, endTopMixedRegion, panoramaWidth, startBottomMixedRegion));
            yield return StartCoroutine(CubemapToEquirectangularCpuNegativeZ(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                0, endTopMixedRegion, endZNegative, startBottomMixedRegion));

            // Handle all remaining image areas with the general case
            yield return StartCoroutine(CubemapToEquirectangularCpuGeneralCase(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                0, endYPositive, panoramaWidth, endTopMixedRegion));
            yield return StartCoroutine(CubemapToEquirectangularCpuGeneralCase(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                0, startBottomMixedRegion, panoramaWidth, startYNegative));

            // If width is not multiple of 8, due to rounding, there may be one-column strips where the X/Z textures mix together
            if (endZNegative < startXNegative)
                yield return StartCoroutine(CubemapToEquirectangularCpuGeneralCase(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                    endZNegative, endTopMixedRegion, startXNegative, startBottomMixedRegion));
            if (endXNegative < startZPositive)
                yield return StartCoroutine(CubemapToEquirectangularCpuGeneralCase(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                    endXNegative, endTopMixedRegion, startZPositive, startBottomMixedRegion));
            if (endZPositive < startXPositive)
                yield return StartCoroutine(CubemapToEquirectangularCpuGeneralCase(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                    endZPositive, endTopMixedRegion, startXPositive, startBottomMixedRegion));
            if (endXPositive < startZNegative)
                yield return StartCoroutine(CubemapToEquirectangularCpuGeneralCase(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                    endXPositive, endTopMixedRegion, startZNegative, startBottomMixedRegion));
        }
        else
        {
            IEnumerator enumerator;
            enumerator = CubemapToEquirectangularCpuPositiveY(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                0, 0, panoramaWidth, endYPositive);
            while (enumerator.MoveNext()) { }
            enumerator = CubemapToEquirectangularCpuNegativeY(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                0, startYNegative, panoramaWidth, panoramaHeight);
            while (enumerator.MoveNext()) { }

            enumerator = CubemapToEquirectangularCpuPositiveX(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                startXPositive, endTopMixedRegion, endXPositive, startBottomMixedRegion);
            while (enumerator.MoveNext()) { }
            enumerator = CubemapToEquirectangularCpuNegativeX(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                startXNegative, endTopMixedRegion, endXNegative, startBottomMixedRegion);
            while (enumerator.MoveNext()) { }
            enumerator = CubemapToEquirectangularCpuPositiveZ(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                startZPositive, endTopMixedRegion, endZPositive, startBottomMixedRegion);
            while (enumerator.MoveNext()) { }
            
            // Do in two pieces since z negative wraps/loops around
            enumerator = CubemapToEquirectangularCpuNegativeZ(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                startZNegative, endTopMixedRegion, panoramaWidth, startBottomMixedRegion);
            while (enumerator.MoveNext()) { }
            enumerator = CubemapToEquirectangularCpuNegativeZ(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                0, endTopMixedRegion, endZNegative, startBottomMixedRegion);
            while (enumerator.MoveNext()) { }

            // Handle all remaining image areas with the general case
            enumerator = CubemapToEquirectangularCpuGeneralCase(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                0, endYPositive, panoramaWidth, endTopMixedRegion);
            while (enumerator.MoveNext()) { } 
            enumerator = CubemapToEquirectangularCpuGeneralCase(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                0, startBottomMixedRegion, panoramaWidth, startYNegative);
            while (enumerator.MoveNext()) { }

            // If width is not multiple of 8, due to rounding, there may be one-column strips where the X/Z textures mix together
            if (endZNegative < startXNegative)
            {
                enumerator = CubemapToEquirectangularCpuGeneralCase(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                    endZNegative, endTopMixedRegion, startXNegative, startBottomMixedRegion);
                while (enumerator.MoveNext()) { }
            }
            if (endXNegative < startZPositive)
            {
                enumerator = CubemapToEquirectangularCpuGeneralCase(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                    endXNegative, endTopMixedRegion, startZPositive, startBottomMixedRegion);
                while (enumerator.MoveNext()) { }
            }
            if (endZPositive < startXPositive)
            {
                enumerator = CubemapToEquirectangularCpuGeneralCase(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                    endZPositive, endTopMixedRegion, startXPositive, startBottomMixedRegion);
                while (enumerator.MoveNext()) { }
            }
            if (endXPositive < startZNegative)
            {
                enumerator = CubemapToEquirectangularCpuGeneralCase(cubemapTexs, cubemapSize, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxValue, numPixelsAveraged,
                    endXPositive, endTopMixedRegion, startZNegative, startBottomMixedRegion);
                while (enumerator.MoveNext()) { }
            }
        }

        yield return null;
    }

    private IEnumerator CubemapToEquirectangularCpuPositiveY(Texture2D[] cubemapTexs, int cubemapSize, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, float maxValue, int numPixelsAveraged,
        int startX, int startY, int endX, int endY)
    {
        var cubemapTex = cubemapTexs[(int)CubemapFace.PositiveY];
        for (int y = startY; y < endY; y++)
        for (int x = startX; x < endX; x++)
        {
            int rTotal = 0, gTotal = 0, bTotal = 0, aTotal = 0;
            for (int ySsaa = y * ssaaFactor; ySsaa < (y + 1) * ssaaFactor; ySsaa++)
            for (int xSsaa = x * ssaaFactor; xSsaa < (x + 1) * ssaaFactor; xSsaa++)
            {
                float xcoord = (float)xSsaa / (panoramaWidth * ssaaFactor);
                float ycoord = (float)ySsaa / (panoramaHeight * ssaaFactor);

                float latitude = (ycoord - 0.5f) * Mathf.PI;
                float longitude = (xcoord * 2.0f - 1.0f) * Mathf.PI;

                float cosLat = Mathf.Cos(latitude);
                Vector3 equirectRayDirection = new Vector3(
                    cosLat * Mathf.Sin(longitude), -Mathf.Sin(latitude), cosLat * Mathf.Cos(longitude));

                float distance = 1.0f / equirectRayDirection.y;
                float u = equirectRayDirection.x * distance, v = equirectRayDirection.z * distance;
                // Debug.Assert (equirectRayDirection.y > 0.0f);
                // Debug.Assert (! (Mathf.Abs(u) > 1.0f || Mathf.Abs(v) > 1.0f) );

                u = (u + 1.0f) / 2.0f;
                v = (v + 1.0f) / 2.0f;

                Color32 c = cubemapTex.GetPixelBilinear(u, v);
                rTotal += c.r; gTotal += c.g; bTotal += c.b; aTotal += c.a;
            }

            int baseIdx = stride * (panoramaHeight - 1 - y) + x * 4;
            pixelValues[baseIdx + 0] = (byte)(bTotal / numPixelsAveraged);
            pixelValues[baseIdx + 1] = (byte)(gTotal / numPixelsAveraged);
            pixelValues[baseIdx + 2] = (byte)(rTotal / numPixelsAveraged);
            pixelValues[baseIdx + 3] = (byte)(aTotal / numPixelsAveraged);

            if ((x & 0xFF) == 0 && Time.realtimeSinceStartup - startTime > processingTimePerFrame)
            {
                yield return null; // Wait until next frame
                startTime = Time.realtimeSinceStartup;
            }
        }
    }

    private IEnumerator CubemapToEquirectangularCpuNegativeY(Texture2D[] cubemapTexs, int cubemapSize, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, float maxValue, int numPixelsAveraged,
        int startX, int startY, int endX, int endY)
    {
        var cubemapTex = cubemapTexs[(int)CubemapFace.NegativeY];
        for (int y = startY; y < endY; y++)
        for (int x = startX; x < endX; x++)
        {
            int rTotal = 0, gTotal = 0, bTotal = 0, aTotal = 0;
            for (int ySsaa = y * ssaaFactor; ySsaa < (y + 1) * ssaaFactor; ySsaa++)
            for (int xSsaa = x * ssaaFactor; xSsaa < (x + 1) * ssaaFactor; xSsaa++)
            {
                float xcoord = (float)xSsaa / (panoramaWidth * ssaaFactor);
                float ycoord = (float)ySsaa / (panoramaHeight * ssaaFactor);

                float latitude = (ycoord - 0.5f) * Mathf.PI;
                float longitude = (xcoord * 2.0f - 1.0f) * Mathf.PI;

                float cosLat = Mathf.Cos(latitude);
                Vector3 equirectRayDirection = new Vector3(
                    cosLat * Mathf.Sin(longitude), -Mathf.Sin(latitude), cosLat * Mathf.Cos(longitude));

                float distance = 1.0f / equirectRayDirection.y;
                float u = equirectRayDirection.x * distance, v = equirectRayDirection.z * distance;
                u = -u;
                // Debug.Assert (equirectRayDirection.y < 0.0f);
                // Debug.Assert (! (Mathf.Abs(u) > 1.0f || Mathf.Abs(v) > 1.0f) );

                u = (u + 1.0f) / 2.0f;
                v = (v + 1.0f) / 2.0f;

                Color32 c = cubemapTex.GetPixelBilinear(u, v);
                rTotal += c.r; gTotal += c.g; bTotal += c.b; aTotal += c.a;
            }

            int baseIdx = stride * (panoramaHeight - 1 - y) + x * 4;
            pixelValues[baseIdx + 0] = (byte)(bTotal / numPixelsAveraged);
            pixelValues[baseIdx + 1] = (byte)(gTotal / numPixelsAveraged);
            pixelValues[baseIdx + 2] = (byte)(rTotal / numPixelsAveraged);
            pixelValues[baseIdx + 3] = (byte)(aTotal / numPixelsAveraged);

            if ((x & 0xFF) == 0 && Time.realtimeSinceStartup - startTime > processingTimePerFrame)
            {
                yield return null; // Wait until next frame
                startTime = Time.realtimeSinceStartup;
            }
        }
    }

    private IEnumerator CubemapToEquirectangularCpuPositiveX(Texture2D[] cubemapTexs, int cubemapSize, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, float maxValue, int numPixelsAveraged,
        int startX, int startY, int endX, int endY)
    {
        var cubemapTex = cubemapTexs[(int)CubemapFace.PositiveX];
        for (int y = startY; y < endY; y++)
        for (int x = startX; x < endX; x++)
        {
            int rTotal = 0, gTotal = 0, bTotal = 0, aTotal = 0;
            for (int ySsaa = y * ssaaFactor; ySsaa < (y + 1) * ssaaFactor; ySsaa++)
            for (int xSsaa = x * ssaaFactor; xSsaa < (x + 1) * ssaaFactor; xSsaa++)
            {
                float xcoord = (float)xSsaa / (panoramaWidth * ssaaFactor);
                float ycoord = (float)ySsaa / (panoramaHeight * ssaaFactor);

                float latitude = (ycoord - 0.5f) * Mathf.PI;
                float longitude = (xcoord * 2.0f - 1.0f) * Mathf.PI;

                float cosLat = Mathf.Cos(latitude);
                Vector3 equirectRayDirection = new Vector3(
                    cosLat * Mathf.Sin(longitude), -Mathf.Sin(latitude), cosLat * Mathf.Cos(longitude));

                float distance = 1.0f / equirectRayDirection.x;
                float u = -equirectRayDirection.z * distance, v = equirectRayDirection.y * distance;
                v = -v;
                // Debug.Assert(equirectRayDirection.x > 0.0f);
                // Debug.Assert (! (Mathf.Abs(u) > 1.0f || Mathf.Abs(v) > 1.0f) );

                u = (u + 1.0f) / 2.0f;
                v = (v + 1.0f) / 2.0f;

                Color32 c = cubemapTex.GetPixelBilinear(u, v);
                rTotal += c.r; gTotal += c.g; bTotal += c.b; aTotal += c.a;
            }

            int baseIdx = stride * (panoramaHeight - 1 - y) + x * 4;
            pixelValues[baseIdx + 0] = (byte)(bTotal / numPixelsAveraged);
            pixelValues[baseIdx + 1] = (byte)(gTotal / numPixelsAveraged);
            pixelValues[baseIdx + 2] = (byte)(rTotal / numPixelsAveraged);
            pixelValues[baseIdx + 3] = (byte)(aTotal / numPixelsAveraged);

            if ((x & 0xFF) == 0 && Time.realtimeSinceStartup - startTime > processingTimePerFrame)
            {
                yield return null; // Wait until next frame
                startTime = Time.realtimeSinceStartup;
            }
        }
    }

    private IEnumerator CubemapToEquirectangularCpuNegativeX(Texture2D[] cubemapTexs, int cubemapSize, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, float maxValue, int numPixelsAveraged,
        int startX, int startY, int endX, int endY)
    {
        var cubemapTex = cubemapTexs[(int)CubemapFace.NegativeX];
        for (int y = startY; y < endY; y++)
        for (int x = startX; x < endX; x++)
        {
            int rTotal = 0, gTotal = 0, bTotal = 0, aTotal = 0;
            for (int ySsaa = y * ssaaFactor; ySsaa < (y + 1) * ssaaFactor; ySsaa++)
            for (int xSsaa = x * ssaaFactor; xSsaa < (x + 1) * ssaaFactor; xSsaa++)
            {
                float xcoord = (float)xSsaa / (panoramaWidth * ssaaFactor);
                float ycoord = (float)ySsaa / (panoramaHeight * ssaaFactor);

                float latitude = (ycoord - 0.5f) * Mathf.PI;
                float longitude = (xcoord * 2.0f - 1.0f) * Mathf.PI;

                float cosLat = Mathf.Cos(latitude);
                Vector3 equirectRayDirection = new Vector3(
                    cosLat * Mathf.Sin(longitude), -Mathf.Sin(latitude), cosLat * Mathf.Cos(longitude));

                float distance = 1.0f / equirectRayDirection.x;
                float u = -equirectRayDirection.z * distance, v = equirectRayDirection.y * distance;
                // Debug.Assert(equirectRayDirection.x < 0.0f);
                // Debug.Assert (! (Mathf.Abs(u) > 1.0f || Mathf.Abs(v) > 1.0f) );

                u = (u + 1.0f) / 2.0f;
                v = (v + 1.0f) / 2.0f;

                Color32 c = cubemapTex.GetPixelBilinear(u, v);
                rTotal += c.r; gTotal += c.g; bTotal += c.b; aTotal += c.a;
            }

            int baseIdx = stride * (panoramaHeight - 1 - y) + x * 4;
            pixelValues[baseIdx + 0] = (byte)(bTotal / numPixelsAveraged);
            pixelValues[baseIdx + 1] = (byte)(gTotal / numPixelsAveraged);
            pixelValues[baseIdx + 2] = (byte)(rTotal / numPixelsAveraged);
            pixelValues[baseIdx + 3] = (byte)(aTotal / numPixelsAveraged);

            if ((x & 0xFF) == 0 && Time.realtimeSinceStartup - startTime > processingTimePerFrame)
            {
                yield return null; // Wait until next frame
                startTime = Time.realtimeSinceStartup;
            }
        }
    }

    private IEnumerator CubemapToEquirectangularCpuPositiveZ(Texture2D[] cubemapTexs, int cubemapSize, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, float maxValue, int numPixelsAveraged,
        int startX, int startY, int endX, int endY)
    {
        var cubemapTex = cubemapTexs[(int)CubemapFace.PositiveZ];
        for (int y = startY; y < endY; y++)
        for (int x = startX; x < endX; x++)
        {
            int rTotal = 0, gTotal = 0, bTotal = 0, aTotal = 0;
            for (int ySsaa = y * ssaaFactor; ySsaa < (y + 1) * ssaaFactor; ySsaa++)
            for (int xSsaa = x * ssaaFactor; xSsaa < (x + 1) * ssaaFactor; xSsaa++)
            {
                float xcoord = (float)xSsaa / (panoramaWidth * ssaaFactor);
                float ycoord = (float)ySsaa / (panoramaHeight * ssaaFactor);

                float latitude = (ycoord - 0.5f) * Mathf.PI;
                float longitude = (xcoord * 2.0f - 1.0f) * Mathf.PI;

                float cosLat = Mathf.Cos(latitude);
                Vector3 equirectRayDirection = new Vector3(
                    cosLat * Mathf.Sin(longitude), -Mathf.Sin(latitude), cosLat * Mathf.Cos(longitude));

                float distance = 1.0f / equirectRayDirection.z;
                float u = equirectRayDirection.x * distance, v = equirectRayDirection.y * distance;
                v = -v;
                // Debug.Assert (equirectRayDirection.z > 0.0f);
                // Debug.Assert (! (Mathf.Abs(u) > 1.0f || Mathf.Abs(v) > 1.0f) );

                u = (u + 1.0f) / 2.0f;
                v = (v + 1.0f) / 2.0f;

                Color32 c = cubemapTex.GetPixelBilinear(u, v);
                rTotal += c.r; gTotal += c.g; bTotal += c.b; aTotal += c.a;
            }

            int baseIdx = stride * (panoramaHeight - 1 - y) + x * 4;
            pixelValues[baseIdx + 0] = (byte)(bTotal / numPixelsAveraged);
            pixelValues[baseIdx + 1] = (byte)(gTotal / numPixelsAveraged);
            pixelValues[baseIdx + 2] = (byte)(rTotal / numPixelsAveraged);
            pixelValues[baseIdx + 3] = (byte)(aTotal / numPixelsAveraged);

            if ((x & 0xFF) == 0 && Time.realtimeSinceStartup - startTime > processingTimePerFrame)
            {
                yield return null; // Wait until next frame
                startTime = Time.realtimeSinceStartup;
            }
        }
    }

    private IEnumerator CubemapToEquirectangularCpuNegativeZ(Texture2D[] cubemapTexs, int cubemapSize, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, float maxValue, int numPixelsAveraged,
        int startX, int startY, int endX, int endY)
    {
        var cubemapTex = cubemapTexs[(int)CubemapFace.NegativeZ];
        for (int y = startY; y < endY; y++)
        for (int x = startX; x < endX; x++)
        {
            int rTotal = 0, gTotal = 0, bTotal = 0, aTotal = 0;
            for (int ySsaa = y * ssaaFactor; ySsaa < (y + 1) * ssaaFactor; ySsaa++)
            for (int xSsaa = x * ssaaFactor; xSsaa < (x + 1) * ssaaFactor; xSsaa++)
            {
                float xcoord = (float)xSsaa / (panoramaWidth * ssaaFactor);
                float ycoord = (float)ySsaa / (panoramaHeight * ssaaFactor);

                float latitude = (ycoord - 0.5f) * Mathf.PI;
                float longitude = (xcoord * 2.0f - 1.0f) * Mathf.PI;

                float cosLat = Mathf.Cos(latitude);
                Vector3 equirectRayDirection = new Vector3(
                    cosLat * Mathf.Sin(longitude), -Mathf.Sin(latitude), cosLat * Mathf.Cos(longitude));

                float distance = 1.0f / equirectRayDirection.z;
                float u = equirectRayDirection.x * distance, v = equirectRayDirection.y * distance;
                // Debug.Assert (equirectRayDirection.z < 0.0f);
                // Debug.Assert (! (Mathf.Abs(u) > 1.0f || Mathf.Abs(v) > 1.0f) );

                u = (u + 1.0f) / 2.0f;
                v = (v + 1.0f) / 2.0f;

                Color32 c = cubemapTex.GetPixelBilinear(u, v);
                rTotal += c.r; gTotal += c.g; bTotal += c.b; aTotal += c.a;
            }

            int baseIdx = stride * (panoramaHeight - 1 - y) + x * 4;
            pixelValues[baseIdx + 0] = (byte)(bTotal / numPixelsAveraged);
            pixelValues[baseIdx + 1] = (byte)(gTotal / numPixelsAveraged);
            pixelValues[baseIdx + 2] = (byte)(rTotal / numPixelsAveraged);
            pixelValues[baseIdx + 3] = (byte)(aTotal / numPixelsAveraged);

            if ((x & 0xFF) == 0 && Time.realtimeSinceStartup - startTime > processingTimePerFrame)
            {
                yield return null; // Wait until next frame
                startTime = Time.realtimeSinceStartup;
            }
        }
    }

    private IEnumerator CubemapToEquirectangularCpuGeneralCase(Texture2D[] cubemapTexs, int cubemapSize, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, float maxValue, int numPixelsAveraged,
        int startX, int startY, int endX, int endY)
    {
        for (int y = startY; y < endY; y++)
        for (int x = startX; x < endX; x++)
        {
            int rTotal = 0, gTotal = 0, bTotal = 0, aTotal = 0;
            for (int ySsaa = y * ssaaFactor; ySsaa < (y + 1) * ssaaFactor; ySsaa++)
            for (int xSsaa = x * ssaaFactor; xSsaa < (x + 1) * ssaaFactor; xSsaa++)
            {
                float xcoord = (float)xSsaa / (panoramaWidth * ssaaFactor);
                float ycoord = (float)ySsaa / (panoramaHeight * ssaaFactor);

                float latitude = (ycoord - 0.5f) * Mathf.PI;
                float longitude = (xcoord * 2.0f - 1.0f) * Mathf.PI;

                // Equivalent to: Vector3 equirectRayDirection =
                //     Quaternion.Euler(-latitude * 360/(2*Mathf.PI), longitude * 360/(2*Mathf.PI), 0.0f) * new Vector3(0, 0, 1);
                float cosLat = Mathf.Cos(latitude);
                Vector3 equirectRayDirection = new Vector3(
                    cosLat * Mathf.Sin(longitude), -Mathf.Sin(latitude), cosLat * Mathf.Cos(longitude));

                float distance = 0.0f;
                CubemapFace face;
                float u, v;

                {
                    distance = 1.0f / equirectRayDirection.y;
                    u = equirectRayDirection.x * distance; v = equirectRayDirection.z * distance;
                    if (equirectRayDirection.y > 0.0f)
                    {
                        face = CubemapFace.PositiveY;
                    }
                    else
                    {
                        face = CubemapFace.NegativeY;
                        u = -u;
                    }
                }

                if (Mathf.Abs(u) > 1.0f || Mathf.Abs(v) > 1.0f)
                {
                    distance = 1.0f / equirectRayDirection.x;
                    u = -equirectRayDirection.z * distance; v = equirectRayDirection.y * distance;
                    if (equirectRayDirection.x > 0.0f)
                    {
                        face = CubemapFace.PositiveX;
                        v = -v;
                    }
                    else
                    {
                        face = CubemapFace.NegativeX;
                    }
                }
                if (Mathf.Abs(u) > 1.0f || Mathf.Abs(v) > 1.0f)
                {
                    distance = 1.0f / equirectRayDirection.z;
                    u = equirectRayDirection.x * distance; v = equirectRayDirection.y * distance;
                    if (equirectRayDirection.z > 0.0f)
                    {
                        face = CubemapFace.PositiveZ;
                        v = -v;
                    }
                    else
                    {
                        face = CubemapFace.NegativeZ;
                    }
                }

                u = (u + 1.0f) / 2.0f;
                v = (v + 1.0f) / 2.0f;

                // Boundary: should blend between cubemap views, but for now just grab color
                // of nearest pixel in selected cubemap view
                u = Mathf.Min(u, maxValue);
                v = Mathf.Min(v, maxValue);

                Color32 c = cubemapTexs[(int)face].GetPixelBilinear(u, v);
                rTotal += c.r; gTotal += c.g; bTotal += c.b; aTotal += c.a;
            }

            int baseIdx = stride * (panoramaHeight - 1 - y) + x * 4;
            pixelValues[baseIdx + 0] = (byte)(bTotal / numPixelsAveraged);
            pixelValues[baseIdx + 1] = (byte)(gTotal / numPixelsAveraged);
            pixelValues[baseIdx + 2] = (byte)(rTotal / numPixelsAveraged);
            pixelValues[baseIdx + 3] = (byte)(aTotal / numPixelsAveraged);

            if ((x & 0xFF) == 0 && Time.realtimeSinceStartup - startTime > processingTimePerFrame)
            {
                yield return null; // Wait until next frame
                startTime = Time.realtimeSinceStartup;
            }
        }
    }
}
