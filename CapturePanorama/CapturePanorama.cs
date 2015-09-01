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
    public bool captureStereoscopic = false;
    public float interpupillaryDistance = 0.0635f; // Average IPD of all subjects in US Army survey in meters
    public int numCirclePoints = 32;
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
    public ComputeShader convertPanoramaStereoShader;
    public ComputeShader copyShader;
    public ComputeShader readRFloatTextureShader;
    public ComputeShader textureToBufferShader;
    public bool enableDebugging = false;

    public enum ImageFormat { PNG, JPEG, BMP };
    public enum AntiAliasing { _1 = 1, _2 = 2, _4 = 4, _8 = 8 };

    string apiUrl = "http://alpha.vrchive.com/api/1/";
    string apiKey = "0b26e4dca20793a83fd92ad83e3e859e";

    GameObject go = null;
    Camera cam;
    Texture2D[] cameraTexs = null;
    RenderTexture[] cameraRenderTextures = null;

    bool capturingEveryFrame = false;
    bool usingGpuTransform;
    CubemapFace[] faces;
    int panoramaHeight, cameraWidth, cameraHeight;
    RenderTexture cubemapRenderTexture = null;
    int convertPanoramaKernelIdx = -1, convertPanoramaYPositiveKernelIdx = -1, convertPanoramaYNegativeKernelIdx = -1, copyKernelIdx = -1, readRFloatTextureIdx = -1, textureToBufferIdx = -1, renderStereoIdx = -1;
    int[] convertPanoramaKernelIdxs;
    byte[] imageFileBytes;
    string videoBaseName = "";
    private int frameNumber;
    const int ResultBufferSlices = 8; // Makes result buffer same pixel count as a cubemap texture
    float hFov = -1.0f, vFov = -1.0f, circleRadius = -1.0f;
    int threadsX = 32, threadsY = 32; // Must match all shaders

    int lastConfiguredPanoramaWidth, lastConfiguredSsaaFactor;
    bool lastConfiguredCaptureStereoscopic, lastConfiguredSaveCubemap, lastConfiguredUseGpuTransform;
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

        lastConfiguredCaptureStereoscopic = captureStereoscopic;
        lastConfiguredPanoramaWidth = panoramaWidth;
        lastConfiguredSsaaFactor = ssaaFactor;
        lastConfiguredAntiAliasing = antiAliasing;
        lastConfiguredSaveCubemap = saveCubemap;
        lastConfiguredUseGpuTransform = useGpuTransform;

        faces = new CubemapFace[] {
			CubemapFace.PositiveX, CubemapFace.NegativeX,
			CubemapFace.PositiveY, CubemapFace.NegativeY,
			CubemapFace.PositiveZ, CubemapFace.NegativeZ };

        panoramaHeight = panoramaWidth / 2;

        if (go != null)
            Destroy(go);

        go = new GameObject("CubemapCamera");
        go.AddComponent<Camera>();
        go.hideFlags = HideFlags.HideAndDontSave;

        cam = go.GetComponent<Camera>();
        cam.enabled = false;

        int numCameras = faces.Length;
        hFov = vFov = 90.0f;
        if (captureStereoscopic)
        {
            // For stereoscopic rendering, there are a set of points lying on a horizontal circle around the origin.
            // Will have four cameras per circle point, one turned left 45 deg, one turned right 45 deg,
            // one turned up 45 deg, one turned down 45 deg. Allows covering >= 180 deg horizontal and 180 deg vertical.
            // To be able to resolve all rays, we need to choose a large enough horizontal FOV for each camera.

            float maxAngleError = 360.0f / numCirclePoints;

            // TODO: Use different hFov/vFov for top/bottom cameras and left/right cameras (vFov of top/bottom cameras especially is a lot less)

            // Given our ipd adjustment curve 1-x^2, the IPD range of the top/bottom cameras will be only 0%-75%
            // of the original IPD. Hence the necessary hFov is given by 2*(pi/2 - acos(0.75)) = about 97.2 deg.
            float extraFovForRoundingErrors = 0.001f;
            float hFovTopBottom = 2.0f * (Mathf.PI / 2.0f - Mathf.Acos(0.75f)) * 360.0f / (2.0f * Mathf.PI);
            hFov = Mathf.Max(90f + maxAngleError, hFovTopBottom) + extraFovForRoundingErrors; // These will usually be similar so just use max for simplicity
            vFov = 90.0f;
            numCameras = numCirclePoints * 4;
            circleRadius = interpupillaryDistance / 2.0f;
            // float hFovPerCirclePoint = hFov * 2.0f;
            // float hFovRad = (hFovPerCirclePoint - maxAngleError * 2.0f) * (2 * Mathf.PI) / 360.0f;
            // circleRadius = (interpupillaryDistance / 2.0f) / Mathf.Cos((Mathf.PI - hFovRad) / 2);
        }

        double ppd90 = panoramaWidth * 90.0 / 360.0;
        // Match PPD at 90 degrees - if it's larger, central 90 degree section should match PPD
        cameraWidth = (int)Math.Ceiling(Math.Tan(hFov * (2.0f*Mathf.PI)/360.0f / 2.0f) * ppd90 * ssaaFactor);
        cameraHeight = (int)Math.Ceiling(Math.Tan(vFov * (2.0f * Mathf.PI) / 360.0f / 2.0f) * ppd90 * ssaaFactor);

        Log("Number of cameras: " + numCameras);
        Log("Camera dimensions: " + cameraWidth + "x" + cameraHeight);

        usingGpuTransform = useGpuTransform && convertPanoramaShader != null && SystemInfo.supportsComputeShaders;

        if (cubemapRenderTexture != null)
            Destroy(cubemapRenderTexture);

        cubemapRenderTexture = new RenderTexture(cameraWidth, cameraHeight, /*depth*/24, RenderTextureFormat.ARGB32);
        cubemapRenderTexture.antiAliasing = (int)antiAliasing;
        cubemapRenderTexture.Create();

        if (cameraRenderTextures != null)
            foreach (RenderTexture tex in cameraRenderTextures)
                Destroy(tex);

        cameraRenderTextures = new RenderTexture[numCameras];
        for (int i = 0; i < cameraRenderTextures.Length; i++)
        {
            cameraRenderTextures[i] = new RenderTexture(cameraWidth, cameraHeight, /*depth*/0, RenderTextureFormat.ARGB32);
            cameraRenderTextures[i].enableRandomWrite = true;
            cameraRenderTextures[i].antiAliasing = 1; // Must be 1 to avoid Unity ReadPixels() bug
            cameraRenderTextures[i].Create();
        }

        if (cameraTexs != null)
            foreach (Texture2D tex in cameraTexs)
                Destroy(tex);
        cameraTexs = null;

        if (saveCubemap || !usingGpuTransform)
        {
            cameraTexs = new Texture2D[numCameras];
            for (int i = 0; i < cameraTexs.Length; i++)
            {
                cameraTexs[i] = new Texture2D(cameraWidth, cameraHeight, TextureFormat.RGB24, /*mipmap*/false, /*linear*/true);
                cameraTexs[i].Apply(/*updateMipmaps*/false, /*makeNoLongerReadable*/false);
            }
        }

        if (usingGpuTransform)
        {
            convertPanoramaKernelIdx = convertPanoramaShader.FindKernel("CubeMapToEquirectangular");
            convertPanoramaYPositiveKernelIdx = convertPanoramaShader.FindKernel("CubeMapToEquirectangularPositiveY");
            convertPanoramaYNegativeKernelIdx = convertPanoramaShader.FindKernel("CubeMapToEquirectangularNegativeY");
            convertPanoramaKernelIdxs = new int[] { convertPanoramaKernelIdx, convertPanoramaYPositiveKernelIdx, convertPanoramaYNegativeKernelIdx };

            foreach (CubemapFace face in faces)
            {
                foreach (int kernelIdx in convertPanoramaKernelIdxs)
                {
                    convertPanoramaShader.SetTexture(kernelIdx, "cubemapFace" + face.ToString(), cameraRenderTextures[(int)face]);
                }
            }
            convertPanoramaShader.SetInt("equirectangularWidth", panoramaWidth);
            convertPanoramaShader.SetInt("equirectangularHeight", panoramaHeight);
            convertPanoramaShader.SetInt("ssaaFactor", ssaaFactor);
            convertPanoramaShader.SetInt("cameraWidth", cameraWidth);
            convertPanoramaShader.SetInt("cameraHeight", cameraHeight);
        }

        copyKernelIdx = copyShader.FindKernel("Copy");
        copyShader.SetInt("width", cameraWidth);
        copyShader.SetInt("height", cameraHeight);

        readRFloatTextureIdx = readRFloatTextureShader.FindKernel("ReadRFloatTexture");
        readRFloatTextureShader.SetInt("width", cameraWidth);
        readRFloatTextureShader.SetInt("height", cameraHeight);

        textureToBufferIdx = textureToBufferShader.FindKernel("TextureToBuffer");
        textureToBufferShader.SetInt("width", cameraWidth);
        textureToBufferShader.SetInt("height", cameraHeight);

        renderStereoIdx = convertPanoramaStereoShader.FindKernel("RenderStereo");
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
        if (captureStereoscopic != lastConfiguredCaptureStereoscopic ||
            panoramaWidth != lastConfiguredPanoramaWidth ||
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
        ComputeBuffer convertPanoramaResultBuffer = null;
        if (usingGpuTransform)
        {
            if (captureStereoscopic)
            {
                convertPanoramaResultBuffer =
                    new ComputeBuffer(/*count*/panoramaWidth * panoramaHeight * 2, /*stride*/4);
                convertPanoramaStereoShader.SetBuffer(renderStereoIdx, "result", convertPanoramaResultBuffer);
            }
            else
            {
                convertPanoramaResultBuffer =
                    new ComputeBuffer(/*count*/panoramaWidth * (panoramaHeight + ResultBufferSlices - 1) / ResultBufferSlices, /*stride*/4);
                foreach (int kernelIdx in convertPanoramaKernelIdxs)
                    convertPanoramaShader.SetBuffer(kernelIdx, "result", convertPanoramaResultBuffer);
            }
        }
        ComputeBuffer cameraPixelsBuffer = new ComputeBuffer(/*count*/cameraRenderTextures.Length * cameraWidth * cameraHeight, /*stride*/4);
        textureToBufferShader.SetBuffer(textureToBufferIdx, "result", cameraPixelsBuffer);
        ComputeBuffer forceWaitResultBuffer = new ComputeBuffer(/*count*/1, /*stride*/4);
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
        cam.fieldOfView = vFov; // hFov inferred from aspect ratio of target

        // Need to extract each cubemap into a Texture2D so we can read the pixels, but Unity bug
        // prevents this with antiAliasing: http://issuetracker.unity3d.com/issues/texture2d-dot-readpixels-fails-if-rendertexture-has-anti-aliasing-set
        // We copy the cubemap textures using a shader as a workaround.
        int[] forceWaitResult = new int[1];

        for (int i=0; i < cameraRenderTextures.Length; i++)
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
                if (captureStereoscopic)
                {
                    int circlePointNum = i / 4;
                    float circleAngle = 360.0f * circlePointNum / numCirclePoints;
                    cam.transform.localPosition = Quaternion.Euler(0.0f, circleAngle, 0.0f) * Vector3.forward * circleRadius;
                    if (i % 4 < 2)
                        cam.transform.localRotation = Quaternion.Euler(0.0f, circleAngle + (i % 2 == 0 ? -hFov / 2.0f : hFov / 2.0f), 0.0f);
                    else
                        cam.transform.localRotation = Quaternion.Euler((i % 2 == 0 ? -vFov / 2.0f - 0.1f : vFov / 2.0f + 0.1f), circleAngle, 0.0f);
                }
                else
                {
                    switch ((CubemapFace)i)
                    {
                        case CubemapFace.PositiveX: cam.transform.localRotation = baseRotation * Quaternion.Euler(0.0f, 90.0f, 0.0f); break;
                        case CubemapFace.NegativeX: cam.transform.localRotation = baseRotation * Quaternion.Euler(0.0f, -90.0f, 0.0f); break;
                        case CubemapFace.PositiveY: cam.transform.localRotation = baseRotation * Quaternion.Euler(90.0f, 0.0f, 0.0f); break;
                        case CubemapFace.NegativeY: cam.transform.localRotation = baseRotation * Quaternion.Euler(-90.0f, 0.0f, 0.0f); break;
                        case CubemapFace.PositiveZ: cam.transform.localRotation = baseRotation * Quaternion.Euler(0.0f, 0.0f, 0.0f); break;
                        case CubemapFace.NegativeZ: cam.transform.localRotation = baseRotation * Quaternion.Euler(0.0f, 180.0f, 0.0f); break;
                    }
                }

                cam.depthTextureMode = DepthTextureMode.None;
                cam.targetTexture = cubemapRenderTexture;
                cam.Render();
            }
            
            textureToBufferShader.SetTexture(textureToBufferIdx, "source", cubemapRenderTexture);
            textureToBufferShader.SetInt("startIdx", i * cameraWidth * cameraHeight);
            textureToBufferShader.Dispatch(textureToBufferIdx, (cameraWidth + threadsX - 1) / threadsX, (cameraHeight + threadsY - 1) / threadsY, 1);
            // Get force wait result to force a wait for the shader to complete
            forceWaitResultBuffer.GetData(forceWaitResult);
        }

        // If we need to access the cubemap pixels on the CPU, retrieve them now
        uint[] cameraPixels = null;
        if (saveCubemap || !usingGpuTransform)
        {
            cameraPixels = new uint[cameraPixelsBuffer.count];
            cameraPixelsBuffer.GetData(cameraPixels);
        }

        RenderTexture.active = null;

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
            for (int i = 0; i < cameraTexs.Length; i++)
            {
                {
                    Color32[] texPixels = cameraTexs[i].GetPixels32();

                    Bitmap bitmap = new Bitmap(cameraWidth, cameraHeight, PixelFormat.Format32bppArgb);
                    var bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
                    IntPtr ptr = bmpData.Scan0;
                    byte[] pixelValues = new byte[Math.Abs(bmpData.Stride) * bitmap.Height];
                    int stride = bmpData.Stride;
                    int width = cameraWidth;
                    int height = bmpData.Height;
                    for (int y = 0; y < cameraHeight; y++)
                    for (int x = 0; x < cameraWidth; x++)
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

                    string cameraId;
                    if (captureStereoscopic)
                    {
                        cameraId = i.ToString();
                        Log("Saving lightfield image for vertex " + cameraId);
                    }
                    else
                    {
                        cameraId = ((CubemapFace)i).ToString();
                        Log("Saving cubemap image " + cameraId);
                    }
                    string cubeFilepath = imagePath + "/" + filenameBase + "_" + cameraId + suffix;
                    // TODO: Use better image processing library to get decent JPEG quality out.
                    bitmap.Save(cubeFilepath, FormatToDrawingFormat(imageFormat));
                    bitmap.Dispose();
                }
            }
        }

        // If this is not here, the fade-in will drop frames.
        for (int i = 0; i < 2; i++)
            yield return new WaitForEndOfFrame();

        if (async && !usingGpuTransform && fadeDuringCapture)
            yield return StartCoroutine(FadeIn(fadeControls));

        filePath = imagePath + "/" + filenameBase + suffix;

        if (captureStereoscopic)
        {
            float tanHalfHFov = Mathf.Tan(hFov * (2 * Mathf.PI) / 360.0f / 2.0f);
            float tanHalfVFov = Mathf.Tan(vFov * (2 * Mathf.PI) / 360.0f / 2.0f);
            float hFovAdjust = (hFov / 2.0f) * (2 * Mathf.PI) / 360.0f;
            float vFovAdjust = (vFov / 2.0f + 0.1f) * (2 * Mathf.PI) / 360.0f;

#if false
            // INITIAL TEST SLOW CPU CODE
            Bitmap bitmapCpu = new Bitmap(panoramaWidth, panoramaHeight * 2, PixelFormat.Format32bppArgb);
            for (int y = 0; y < panoramaHeight; y++)
            for (int x = 0; x < panoramaWidth;  x++)
            {
                float xcoord = (float)x / panoramaWidth;
                float ycoord = (float)y / panoramaHeight;

                float latitude = (ycoord - 0.5f) * Mathf.PI;
                float sinLat = Mathf.Sin(latitude);
                float cosLat = Mathf.Cos(latitude);
                float longitude = (xcoord * 2.0f - 1.0f) * Mathf.PI;
                float sinLong = Mathf.Sin(longitude);
                float cosLong = Mathf.Cos(longitude);

                // Scale IPD down as latitude moves toward poles to avoid discontinuities
                float latitudeNormalized = latitude / (Mathf.PI / 2.0f); // Map to [-1, 1]
                // Map from [-1, 1] to [0, 1] with curve 1 - x^2 that is smooth and symmetric at origin
                float ipdScale = 1.0f - latitudeNormalized * latitudeNormalized;
                float scaledEyeRadius = ipdScale * interpupillaryDistance / 2.0f;
                for (int i = 0; i < 2; i++)
                {
                    // The following is equivalent to:
                    // Quaternion eyesRotation = Quaternion.Euler(0.0f, longitude * 360.0f / (2 * Mathf.PI), 0.0f);
                    // Vector3 initialEyePosition = (i == 0 ? Vector3.left : Vector3.right) * scaledEyeRadius;
                    // Vector3 pos = eyesRotation * initialEyePosition; // eye position
                    // Vector3 dir = eyesRotation * Vector3.forward; // gaze direction

                    Vector3 pos = new Vector3(-cosLong * scaledEyeRadius, 0.0f, sinLong * scaledEyeRadius);
                    if (i == 1) pos = -pos;
                    Vector3 dir = new Vector3(sinLong, 0.0f, cosLong);

                    float eyeAngle = (i == 0 ? -Mathf.PI / 2 : Mathf.PI / 2) + longitude;
                    float circlePointAngle = eyeAngle;
                    if (ipdScale < 1.0f - 0.0001f) // Avoids some numerical issues
                    {
                        // Find place where gaze ray crosses circle (point on ray with magnitude equal to circleRadius)
                        // (pos.x + dir.x * t)^2 + (pos.z + dir.z * t)^2 == circleRadius^2
                        // Using unit magnitude of gazeDirection, positive solution is:
                        float t = Mathf.Sqrt(circleRadius * circleRadius - dir.z * dir.z * pos.x * pos.x + 2 * dir.x * dir.z * pos.x * pos.z + (dir.z * dir.z - 1) * pos.z * pos.z) -
                                    dir.x * pos.x - dir.z * pos.z;
                        Vector3 circlePoint = pos + dir * t;
                        Debug.Assert(Math.Abs(circlePoint.magnitude - circleRadius) < 0.01);
                        circlePointAngle = Mathf.Atan2(circlePoint.x, circlePoint.z);
                    }
                    if (circlePointAngle < 0.0f) circlePointAngle += 2 * Mathf.PI;
                    Debug.Assert(circlePointAngle >= 0.0f && circlePointAngle < 2 * Mathf.PI);

                    float circlePointNumber = circlePointAngle / (2 * Mathf.PI) * numCirclePoints;
                    int circlePoint0 = (int)Mathf.Floor(circlePointNumber) % numCirclePoints;

                    Color color0 = new Color(), color1 = new Color();
                    for (int j=0; j < 2; j++)
                    {
                        int circlePointIdx = (j == 0 ? circlePoint0 : (circlePoint0 + 1) % numCirclePoints);
                        float cameraPointAngle = 2 * Mathf.PI * circlePointIdx / numCirclePoints;
                        float sinCameraPointAngle = Mathf.Sin(cameraPointAngle);
                        float cosCameraPointAngle = Mathf.Cos(cameraPointAngle);

                        // Equivalent to (using fact that both dir and circlePointNorm are unit vectors):
                        // Quaternion circlePointRotation = Quaternion.Euler(0.0f, cameraPointAngle * 360.0f / (2 * Mathf.PI), 0.0f);
                        // Vector3 circlePointNormal = circlePointRotation * Vector3.forward;
                        // float newLongitudeDegrees = Mathf.Sign(Vector3.Cross(circlePointNormal, dir).y) * Vector3.Angle(circlePointNormal, dir);

                        float newLongitude = Mathf.Sign(dir.x * cosCameraPointAngle - dir.z * sinCameraPointAngle) *
                                             Mathf.Acos(dir.z * cosCameraPointAngle + dir.x * sinCameraPointAngle);
                        float cosNewLong = Mathf.Cos(newLongitude);
                        float sinNewLong = Mathf.Sin(newLongitude);

                        // Select which of the two cameras for this point to use and adjust ray to make camera plane perpendicular to axes
                        int cameraNumBase = circlePointIdx * 4;

                        int cameraNum = cameraNumBase + (newLongitude >= 0.0f ? 1 : 0);
                        
                        float longitudeAdjust = (newLongitude >= 0.0f ? -hFovAdjust : hFovAdjust);
                        float longSum = newLongitude + longitudeAdjust;

                        // Equivalent to:
                        // Vector3 textureRayDir = Quaternion.Euler(-latitude * 360.0f / (2 * Mathf.PI), newLongitude * 360.0f / (2 * Mathf.PI), 0.0f) * Vector3.forward;
                        // Vector3 textureRayDirAdjusted = Quaternion.Euler(0.0f, longitudeAdjust * 360.0f / (2 * Mathf.PI), 0.0f) * textureRayDir;
                        Vector3 textureRayDirAdjusted = new Vector3(cosLat * Mathf.Sin(longSum), sinLat, cosLat * Mathf.Cos(longSum)); 

                        float u =  textureRayDirAdjusted.x / textureRayDirAdjusted.z / tanHalfHFov;
                        float v = -textureRayDirAdjusted.y / textureRayDirAdjusted.z / tanHalfVFov;

                        // There's a lot of vertical overlap so don't accept v near the edge of the left/right cameras, to avoid artifact pixels
                        if (! (textureRayDirAdjusted.z > 0.0f && u * u <= 1.0f && v * v <= 1.0f - 0.1f) )
                        {
                            cameraNum = cameraNumBase + (latitude >= 0.0f ? 3 : 2);
                            float latitudeAdjust = (latitude >= 0.0f ? vFovAdjust : -vFovAdjust);
                            float cosLatAdjust = Mathf.Cos(latitudeAdjust);
                            float sinLatAdjust = Mathf.Sin(latitudeAdjust);
                            // Equivalent to:
                            // textureRayDirAdjusted = Quaternion.Euler(latitudeAdjust * 360.0f / (2 * Mathf.PI), 0.0f, 0.0f) * textureRayDir;
                            textureRayDirAdjusted = new Vector3(cosLat * sinNewLong,
                                                                cosLatAdjust * sinLat - cosLat * cosNewLong * sinLatAdjust,
                                                                sinLatAdjust * sinLat + cosLat * cosNewLong * cosLatAdjust);

                            u =  textureRayDirAdjusted.x / textureRayDirAdjusted.z / tanHalfHFov;
                            v = -textureRayDirAdjusted.y / textureRayDirAdjusted.z / tanHalfVFov;

                            Debug.Assert(textureRayDirAdjusted.z > 0.0f && u * u <= 1.0f && v * v <= 1.0f);
                        }

                        u = (u + 1.0f) * 0.5f;
        				v = (v + 1.0f) * 0.5f;

                        Color col = GetCameraPixelBilinear(cameraPixels, cameraNum, u, v);
                        if (j == 0) color0 = col; else color1 = col;
                    }

                    Color32 c = Color.Lerp(color0, color1, circlePointNumber - Mathf.Floor(circlePointNumber));
                    bitmapCpu.SetPixel(x, y + panoramaHeight * i, System.Drawing.Color.FromArgb(c.r, c.g, c.b));
                }
            }
            bitmapCpu.Save(filePath + ".cpu." + FormatToExtension(imageFormat), FormatToDrawingFormat(imageFormat));
#endif

            // TODO: refactor out duplicated code
            Bitmap bitmap = new Bitmap(panoramaWidth, panoramaHeight * 2, PixelFormat.Format32bppArgb);
            var bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            IntPtr ptr = bmpData.Scan0;
            byte[] pixelValues = new byte[Math.Abs(bmpData.Stride) * bitmap.Height];

            // GPU CODE
            int stride = bmpData.Stride;
            convertPanoramaStereoShader.SetBuffer(renderStereoIdx, "cameraPixels", cameraPixelsBuffer);
            convertPanoramaStereoShader.SetFloat("tanHalfHFov", tanHalfHFov);
            convertPanoramaStereoShader.SetFloat("tanHalfVFov", tanHalfVFov);
            convertPanoramaStereoShader.SetFloat("hFovAdjust",  hFovAdjust);
            convertPanoramaStereoShader.SetFloat("vFovAdjust",  vFovAdjust);
            convertPanoramaStereoShader.SetFloat("interpupillaryDistance", interpupillaryDistance);
            convertPanoramaStereoShader.SetFloat("circleRadius", circleRadius);
            convertPanoramaStereoShader.SetInt("numCirclePoints", numCirclePoints);

            convertPanoramaStereoShader.SetInt("equirectangularWidth", panoramaWidth);
            convertPanoramaStereoShader.SetInt("equirectangularHeight", panoramaHeight);
            convertPanoramaStereoShader.SetInt("cameraWidth", cameraWidth);
            convertPanoramaStereoShader.SetInt("cameraHeight", cameraHeight);
            convertPanoramaStereoShader.SetInt("ssaaFactor", ssaaFactor);

            int[] resultPixels = new int[panoramaWidth * panoramaHeight * 2];
            convertPanoramaStereoShader.Dispatch(renderStereoIdx, (panoramaWidth + threadsX - 1) / threadsX, (panoramaHeight + threadsY - 1) / threadsY, 1);

            // Copy to pixelValues output array
            convertPanoramaResultBuffer.GetData(resultPixels);
            int inputIdx = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                int outputIdx = stride * y;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int packedCol = resultPixels[inputIdx];
                    pixelValues[outputIdx + 0] = (byte)((packedCol >>  0) & 0xFF);
                    pixelValues[outputIdx + 1] = (byte)((packedCol >>  8) & 0xFF);
                    pixelValues[outputIdx + 2] = (byte)((packedCol >> 16) & 0xFF);
                    pixelValues[outputIdx + 3] = 255;
                    outputIdx += 4;
                    inputIdx++;
                }
            }

            // TODO: refactor out duplicated code
            System.Runtime.InteropServices.Marshal.Copy(pixelValues, 0, ptr, pixelValues.Length);
            bitmap.UnlockBits(bmpData);
            bitmap.Save(filePath, FormatToDrawingFormat(imageFormat));
        }
        else
        {
            // Convert to equirectangular projection - use compute shader for better performance if supported by platform

            // Write pixels directly to .NET Bitmap for saving out
            // Based on https://msdn.microsoft.com/en-us/library/5ey6h79d%28v=vs.110%29.aspx
            {
                Bitmap bitmap = new Bitmap(panoramaWidth, panoramaHeight, PixelFormat.Format32bppArgb);
                var bmpData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
                IntPtr ptr = bmpData.Scan0;
                byte[] pixelValues = new byte[Math.Abs(bmpData.Stride) * bitmap.Height];

                if (async)
                    yield return StartCoroutine(CubemapToEquirectangular(cameraTexs, convertPanoramaResultBuffer, cameraWidth, cameraHeight, pixelValues, bmpData.Stride, panoramaWidth, panoramaHeight, ssaaFactor, async));
                else
                {
                    var enumerator = CubemapToEquirectangular(cameraTexs, convertPanoramaResultBuffer, cameraWidth, cameraHeight, pixelValues, bmpData.Stride, panoramaWidth, panoramaHeight, ssaaFactor, async);
                    while (enumerator.MoveNext()) { }
                }

                yield return null;
                System.Runtime.InteropServices.Marshal.Copy(pixelValues, 0, ptr, pixelValues.Length);
                bitmap.UnlockBits(bmpData);
                yield return null;

                Log("Time to take panorama screenshot: " + (Time.realtimeSinceStartup - startTime) + " sec");

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
        }

        // Release ComputeBuffers - all done with these
        foreach (var buffer in new ComputeBuffer[] { convertPanoramaResultBuffer, cameraPixelsBuffer, forceWaitResultBuffer })
            if (buffer != null)
                buffer.Release();
        convertPanoramaResultBuffer = cameraPixelsBuffer = forceWaitResultBuffer = null;

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

    private Color32 GetCameraPixelBilinear(uint[] cameraPixels, int cameraNum, float u, float v)
    {
        u *= cameraWidth;
        v *= cameraHeight;
        int left   = (int)Math.Floor(u);
        int right  = (int)Math.Min(cameraWidth  - 1, left + 1);
        int top    = (int)Math.Floor(v);
        int bottom = (int)Math.Min(cameraHeight - 1, top  + 1);
        float uFrac = u - left;
        float vFrac = v - top;

        int baseIdx = cameraNum * cameraWidth * cameraHeight;
        int topRow    = baseIdx + top    * cameraWidth;
        int bottomRow = baseIdx + bottom * cameraWidth;
        uint topLeft     = cameraPixels[topRow    + left ];
        uint topRight    = cameraPixels[topRow    + right];
        uint bottomLeft  = cameraPixels[bottomRow + left ];
        uint bottomRight = cameraPixels[bottomRow + right];

        float r = Mathf.Lerp(Mathf.Lerp( topLeft  >> 16        ,  bottomLeft  >> 16        , vFrac),
                             Mathf.Lerp( topRight >> 16        ,  bottomRight >> 16        , vFrac), uFrac);
        float g = Mathf.Lerp(Mathf.Lerp((topLeft  >>  8) & 0xFF, (bottomLeft  >>  8) & 0xFF, vFrac),
                             Mathf.Lerp((topRight >>  8) & 0xFF, (bottomRight >>  8) & 0xFF, vFrac), uFrac);
        float b = Mathf.Lerp(Mathf.Lerp( topLeft         & 0xFF,  bottomLeft         & 0xFF, vFrac),
                             Mathf.Lerp( topRight        & 0xFF,  bottomRight        & 0xFF, vFrac), uFrac);

        return new Color(r / 255.0f, g / 255.0f, b / 255.0f);
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

    IEnumerator CubemapToEquirectangular(Texture2D[] cubemapTexs, ComputeBuffer convertPanoramaResultBuffer, int cameraWidth, int cameraHeight, byte[] pixelValues,
        int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, bool async)
    {
        if (usingGpuTransform)
        {
            int sliceHeight = (panoramaHeight + ResultBufferSlices - 1) / ResultBufferSlices;

            Log("Invoking GPU shader for equirectangular reprojection");
            int[] resultPixels = new int[panoramaWidth * sliceHeight];
            int endYNegative   = (int)Mathf.Floor(panoramaHeight * 0.25f);
            int startYPositive = (int)Mathf.Ceil(panoramaHeight * 0.75f);
            for (int sliceNum = 0; sliceNum < ResultBufferSlices; sliceNum++)
            {
                int startSlice = sliceNum * sliceHeight;
                int endSlice = Math.Min(startSlice + sliceHeight, panoramaHeight);
                convertPanoramaShader.SetInt("startY", sliceNum * sliceHeight);
                // MAJOR PERFORMANCE BUG: SHOULD BE USING sliceHeight, NOT panoramaHeight BELOW! FIX AND TEST
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
                        pixelValues[outputIdx + 0] = (byte)((packedCol >>  0) & 0xFF);
                        pixelValues[outputIdx + 1] = (byte)((packedCol >>  8) & 0xFF);
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
                yield return StartCoroutine(CubemapToEquirectangularCpu(cubemapTexs, cameraWidth, cameraHeight, pixelValues,
                    stride, panoramaWidth, panoramaHeight, ssaaFactor, async));
            else
            {
                var enumerator = CubemapToEquirectangularCpu(cubemapTexs, cameraWidth, cameraHeight, pixelValues,
                    stride, panoramaWidth, panoramaHeight, ssaaFactor, async);
                while (enumerator.MoveNext()) { }
            }
        }
    }

    IEnumerator CubemapToEquirectangularCpu(Texture2D[] cubemapTexs, int cameraWidth, int cameraHeight, byte[] pixelValues,
        int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, bool async)
    {
        Log("Converting to equirectangular");

        yield return null; // Wait for next frame at beginning - already used up some time capturing snapshot

        float startTime = Time.realtimeSinceStartup;
        float processingTimePerFrame = cpuMillisecondsPerFrame / 1000.0f;
        float maxWidth  = 1.0f - 1.0f / cameraWidth;
        float maxHeight = 1.0f - 1.0f / cameraHeight;
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
            yield return StartCoroutine(CubemapToEquirectangularCpuPositiveY(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                0, 0, panoramaWidth, endYPositive));
            yield return StartCoroutine(CubemapToEquirectangularCpuNegativeY(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                0, startYNegative, panoramaWidth, panoramaHeight));

            yield return StartCoroutine(CubemapToEquirectangularCpuPositiveX(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                startXPositive, endTopMixedRegion, endXPositive, startBottomMixedRegion));
            yield return StartCoroutine(CubemapToEquirectangularCpuNegativeX(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                startXNegative, endTopMixedRegion, endXNegative, startBottomMixedRegion));
            yield return StartCoroutine(CubemapToEquirectangularCpuPositiveZ(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                startZPositive, endTopMixedRegion, endZPositive, startBottomMixedRegion));

            // Do in two pieces since z negative wraps/loops around
            yield return StartCoroutine(CubemapToEquirectangularCpuNegativeZ(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                startZNegative, endTopMixedRegion, panoramaWidth, startBottomMixedRegion));
            yield return StartCoroutine(CubemapToEquirectangularCpuNegativeZ(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                0, endTopMixedRegion, endZNegative, startBottomMixedRegion));

            // Handle all remaining image areas with the general case
            yield return StartCoroutine(CubemapToEquirectangularCpuGeneralCase(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxWidth, maxHeight, numPixelsAveraged,
                0, endYPositive, panoramaWidth, endTopMixedRegion));
            yield return StartCoroutine(CubemapToEquirectangularCpuGeneralCase(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxWidth, maxHeight, numPixelsAveraged,
                0, startBottomMixedRegion, panoramaWidth, startYNegative));

            // If width is not multiple of 8, due to rounding, there may be one-column strips where the X/Z textures mix together
            if (endZNegative < startXNegative)
                yield return StartCoroutine(CubemapToEquirectangularCpuGeneralCase(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxWidth, maxHeight, numPixelsAveraged,
                    endZNegative, endTopMixedRegion, startXNegative, startBottomMixedRegion));
            if (endXNegative < startZPositive)
                yield return StartCoroutine(CubemapToEquirectangularCpuGeneralCase(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxWidth, maxHeight, numPixelsAveraged,
                    endXNegative, endTopMixedRegion, startZPositive, startBottomMixedRegion));
            if (endZPositive < startXPositive)
                yield return StartCoroutine(CubemapToEquirectangularCpuGeneralCase(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxWidth, maxHeight, numPixelsAveraged,
                    endZPositive, endTopMixedRegion, startXPositive, startBottomMixedRegion));
            if (endXPositive < startZNegative)
                yield return StartCoroutine(CubemapToEquirectangularCpuGeneralCase(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxWidth, maxHeight, numPixelsAveraged,
                    endXPositive, endTopMixedRegion, startZNegative, startBottomMixedRegion));
        }
        else
        {
            IEnumerator enumerator;
            enumerator = CubemapToEquirectangularCpuPositiveY(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                0, 0, panoramaWidth, endYPositive);
            while (enumerator.MoveNext()) { }
            enumerator = CubemapToEquirectangularCpuNegativeY(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                0, startYNegative, panoramaWidth, panoramaHeight);
            while (enumerator.MoveNext()) { }

            enumerator = CubemapToEquirectangularCpuPositiveX(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                startXPositive, endTopMixedRegion, endXPositive, startBottomMixedRegion);
            while (enumerator.MoveNext()) { }
            enumerator = CubemapToEquirectangularCpuNegativeX(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                startXNegative, endTopMixedRegion, endXNegative, startBottomMixedRegion);
            while (enumerator.MoveNext()) { }
            enumerator = CubemapToEquirectangularCpuPositiveZ(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                startZPositive, endTopMixedRegion, endZPositive, startBottomMixedRegion);
            while (enumerator.MoveNext()) { }
            
            // Do in two pieces since z negative wraps/loops around
            enumerator = CubemapToEquirectangularCpuNegativeZ(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                startZNegative, endTopMixedRegion, panoramaWidth, startBottomMixedRegion);
            while (enumerator.MoveNext()) { }
            enumerator = CubemapToEquirectangularCpuNegativeZ(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, numPixelsAveraged,
                0, endTopMixedRegion, endZNegative, startBottomMixedRegion);
            while (enumerator.MoveNext()) { }

            // Handle all remaining image areas with the general case
            enumerator = CubemapToEquirectangularCpuGeneralCase(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxWidth, maxHeight, numPixelsAveraged,
                0, endYPositive, panoramaWidth, endTopMixedRegion);
            while (enumerator.MoveNext()) { }
            enumerator = CubemapToEquirectangularCpuGeneralCase(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxWidth, maxHeight, numPixelsAveraged,
                0, startBottomMixedRegion, panoramaWidth, startYNegative);
            while (enumerator.MoveNext()) { }

            // If width is not multiple of 8, due to rounding, there may be one-column strips where the X/Z textures mix together
            if (endZNegative < startXNegative)
            {
                enumerator = CubemapToEquirectangularCpuGeneralCase(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxWidth, maxHeight, numPixelsAveraged,
                    endZNegative, endTopMixedRegion, startXNegative, startBottomMixedRegion);
                while (enumerator.MoveNext()) { }
            }
            if (endXNegative < startZPositive)
            {
                enumerator = CubemapToEquirectangularCpuGeneralCase(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxWidth, maxHeight, numPixelsAveraged,
                    endXNegative, endTopMixedRegion, startZPositive, startBottomMixedRegion);
                while (enumerator.MoveNext()) { }
            }
            if (endZPositive < startXPositive)
            {
                enumerator = CubemapToEquirectangularCpuGeneralCase(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxWidth, maxHeight, numPixelsAveraged,
                    endZPositive, endTopMixedRegion, startXPositive, startBottomMixedRegion);
                while (enumerator.MoveNext()) { }
            }
            if (endXPositive < startZNegative)
            {
                enumerator = CubemapToEquirectangularCpuGeneralCase(cubemapTexs, pixelValues, stride, panoramaWidth, panoramaHeight, ssaaFactor, startTime, processingTimePerFrame, maxWidth, maxHeight, numPixelsAveraged,
                    endXPositive, endTopMixedRegion, startZNegative, startBottomMixedRegion);
                while (enumerator.MoveNext()) { }
            }
        }

        yield return null;
    }

    private IEnumerator CubemapToEquirectangularCpuPositiveY(Texture2D[] cubemapTexs, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, int numPixelsAveraged,
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

    private IEnumerator CubemapToEquirectangularCpuNegativeY(Texture2D[] cubemapTexs, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, int numPixelsAveraged,
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

    private IEnumerator CubemapToEquirectangularCpuPositiveX(Texture2D[] cubemapTexs, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, int numPixelsAveraged,
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

    private IEnumerator CubemapToEquirectangularCpuNegativeX(Texture2D[] cubemapTexs, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, int numPixelsAveraged,
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

    private IEnumerator CubemapToEquirectangularCpuPositiveZ(Texture2D[] cubemapTexs, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, int numPixelsAveraged,
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

    private IEnumerator CubemapToEquirectangularCpuNegativeZ(Texture2D[] cubemapTexs, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, int numPixelsAveraged,
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

    private IEnumerator CubemapToEquirectangularCpuGeneralCase(Texture2D[] cubemapTexs, byte[] pixelValues, int stride, int panoramaWidth, int panoramaHeight, int ssaaFactor, float startTime, float processingTimePerFrame, float maxWidth, float maxHeight, int numPixelsAveraged,
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
                u = Mathf.Min(u, maxWidth);
                v = Mathf.Min(v, maxHeight);

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
