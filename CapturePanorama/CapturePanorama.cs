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
using System.Linq;
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
    public string qualitySetting;
    public KeyCode screenshotKey = KeyCode.P;
    public ImageFormat imageFormat = ImageFormat.PNG;
    public int panoramaWidth = 8192;
    public string saveImagePath = "";
    public bool saveCubemap = false;
    public bool uploadImages = false;
    public bool useDefaultOrientation = false;
    public float millisecondsPerFrame = 1000.0f/120.0f;
    public bool useGpuTransform = false;
    public AudioClip startSound;
    public AudioClip doneSound;
    public bool fadeDuringCapture = true;
    public float fadeTime = 0.25f;
    public Color fadeColor = new Color(0.0f, 0.0f, 0.0f, 1.0f);
    public Material fadeMaterial = null;
    public ComputeShader convertPanoramaShader;
    public bool enableDebugging = false;

    public enum ImageFormat { JPEG, PNG };
    internal string filenameSuffix;

    string apiUrl = "http://alpha.vrchive.com/api/1/";
    string apiKey = "0b26e4dca20793a83fd92ad83e3e859e";

    GameObject go = null;
    Camera cam;
    Texture2D[] cubemapTexs = null;

    bool usingGpuTransform;
    CubemapFace[] faces;
    int lastConfiguredPanoramaWidth, panoramaHeight, cubemapSize;
    RenderTexture cubemapRenderTexture = null;
    RenderTexture equirectangularRenderTexture = null;
    Texture2D equirectangularTexture;
    int kernelIdx = -1;
    byte[] imageFileBytes;

    void Start()
    {
        Reinitialize();
    }

    void Reinitialize() {
        lastConfiguredPanoramaWidth = panoramaWidth;
        panoramaHeight = panoramaWidth / 2;
        cubemapSize = panoramaWidth / 4;

        if (go != null)
            Destroy(go);

        go = new GameObject("CubemapCamera");
        go.AddComponent<Camera>();
        go.hideFlags = HideFlags.HideAndDontSave;

        cam = go.GetComponent<Camera>();
        cam.enabled = false;

        faces = new CubemapFace[] {
			CubemapFace.PositiveX, CubemapFace.NegativeX,
			CubemapFace.PositiveY, CubemapFace.NegativeY,
			CubemapFace.PositiveZ, CubemapFace.NegativeZ };

        if (cubemapTexs != null) {
            foreach (Texture2D tex in cubemapTexs) {
                Destroy(tex);
            }
        }

        cubemapTexs = new Texture2D[6];
        foreach (CubemapFace face in faces)
        {
            cubemapTexs[(int)face] = new Texture2D(cubemapSize, cubemapSize, TextureFormat.RGB24, /*mipmap*/false, /*linear*/true);
        }

        if (cubemapRenderTexture != null)
            Destroy(cubemapRenderTexture);
            
        cubemapRenderTexture = new RenderTexture(cubemapSize, cubemapSize, 24, RenderTextureFormat.ARGB32);
        cubemapRenderTexture.Create();

        if (equirectangularRenderTexture != null)
            Destroy(equirectangularRenderTexture);

        if (equirectangularTexture != null)
            Destroy(equirectangularTexture);

        usingGpuTransform = useGpuTransform && convertPanoramaShader != null && SystemInfo.supportsComputeShaders;

        if (usingGpuTransform)
        {
            equirectangularRenderTexture =
                new RenderTexture(panoramaWidth, panoramaHeight, 0, RenderTextureFormat.ARGB32);
            equirectangularRenderTexture.enableRandomWrite = true;
            equirectangularRenderTexture.Create();

            equirectangularTexture = new Texture2D(panoramaWidth, panoramaHeight, TextureFormat.RGB24, /*mipmap*/false, /*linear*/true);

            kernelIdx = convertPanoramaShader.FindKernel("CubeMapToEquirectangular");
        }
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
        if (panoramaWidth != lastConfiguredPanoramaWidth) {
            Reinitialize();
        }
        if (screenshotKey != KeyCode.None && Input.GetKeyDown(screenshotKey) && !Capturing)
        {
            string filenameBase = String.Format("{0}_{1:yyyy-MM-dd_HH-mm-ss-fff}", panoramaName, DateTime.Now);
            Log("Panorama capture key pressed, capturing " + filenameBase);
            CaptureScreenshotAsync(filenameBase);
        }
    }

    public void CaptureScreenshotAsync(string filenameBase)
    {
        StartCoroutine(CaptureScreenshotAsyncHelper(filenameBase));
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


    public IEnumerator CaptureScreenshotAsyncHelper(string filenameBase)
    {
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

        if (fadeDuringCapture)
            yield return StartCoroutine(FadeOut(fadeControls));

        // Make sure black is shown before we start - sometimes two frames are needed
        for (int i = 0; i < 2; i++)
            yield return new WaitForEndOfFrame();

        Log("Starting panorama capture");
        if (startSound != null)
        {
            AudioSource.PlayClipAtPoint(startSound, transform.position);
        }

        float startTime = Time.realtimeSinceStartup;

        Log("Changing quality level");
        int saveQualityLevel = QualitySettings.GetQualityLevel();
        bool qualitySettingWasFound = false;
        string[] qualitySettingNames = QualitySettings.names;
        for (int i = 0; i < qualitySettingNames.Length; i++)
        {
            string name = qualitySettingNames[i];
            if (name == qualitySetting)
            {
                QualitySettings.SetQualityLevel(i, /*applyExpensiveChanges*/true);
                qualitySettingWasFound = true;
            }
        }
        if (qualitySetting != "" && !qualitySettingWasFound)
        {
            Debug.LogError("Quality setting specified for CapturePanorama is invalid, ignoring.", this);
        }

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
        foreach (CubemapFace face in faces)
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
                cam.targetTexture = cubemapRenderTexture;
                cam.fieldOfView = 90.0f;

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
                    case CubemapFace.PositiveX: cam.transform.localRotation = baseRotation * Quaternion.Euler(0.0f, 90.0f, 0.0f); break;
                    case CubemapFace.NegativeX: cam.transform.localRotation = baseRotation * Quaternion.Euler(0.0f, -90.0f, 0.0f); break;
                    case CubemapFace.PositiveY: cam.transform.localRotation = baseRotation * Quaternion.Euler(90.0f, 0.0f, 0.0f); break;
                    case CubemapFace.NegativeY: cam.transform.localRotation = baseRotation * Quaternion.Euler(-90.0f, 0.0f, 0.0f); break;
                    case CubemapFace.PositiveZ: cam.transform.localRotation = baseRotation * Quaternion.Euler(0.0f, 0.0f, 0.0f); break;
                    case CubemapFace.NegativeZ: cam.transform.localRotation = baseRotation * Quaternion.Euler(0.0f, 180.0f, 0.0f); break;
                }

                cam.Render();
            }

            RenderTexture.active = cubemapRenderTexture;
            cubemapTexs[(int)face].ReadPixels(new Rect(0, 0, cubemapSize, cubemapSize), 0, 0);
            cubemapTexs[(int)face].Apply();
        }

        Log("Resetting quality level");
        QualitySettings.SetQualityLevel(saveQualityLevel, /*applyExpensiveChanges*/true);

        string suffix = filenameSuffix + ((imageFormat == ImageFormat.JPEG) ? ".jpg" : ".png");
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
                if (imageFormat == ImageFormat.JPEG)
                    // TODO: Use better image processing library to get decent JPEG quality out.
                    bitmap.Save(cubeFilepath, DrawingImageFormat.Jpeg);
                else
                    bitmap.Save(cubeFilepath, DrawingImageFormat.Png);

                bitmap.Dispose();
            }
        }

        // If this is not here, the fade-in will drop frames.
        for (int i = 0; i < 2; i++)
            yield return new WaitForEndOfFrame();

        if (!usingGpuTransform && fadeDuringCapture)
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

            yield return StartCoroutine(CubemapToEquirectangular(cubemapTexs, cubemapSize, pixelValues, bmpData.Stride, panoramaWidth, panoramaHeight));

            yield return null;
            System.Runtime.InteropServices.Marshal.Copy(pixelValues, 0, ptr, pixelValues.Length);
            bitmap.UnlockBits(bmpData);
            yield return null;

            Log("Time to take panorama screenshot: " + (Time.realtimeSinceStartup - startTime) + " sec");

            var thread = new Thread(() =>
            {
                Log("Saving equirectangular image");
                if (imageFormat == ImageFormat.JPEG)
                    // TODO: Use better image processing library to get decent JPEG quality out.
                    bitmap.Save(filePath, DrawingImageFormat.Jpeg);
                else
                    bitmap.Save(filePath, DrawingImageFormat.Png);
            });
            thread.Start();
            while (thread.ThreadState == ThreadState.Running)
                yield return null;

            bitmap.Dispose();
        }

        if (usingGpuTransform && fadeDuringCapture)
            yield return StartCoroutine(FadeIn(fadeControls));

        foreach (ScreenFadeControl fadeControl in fadeControls)
        {
            Destroy(fadeControl);
        }
        fadeControls.Clear();

        if (uploadImages)
        {
            Log("Uploading image");
            imageFileBytes = File.ReadAllBytes(filePath);
            yield return StartCoroutine(UploadImage(imageFileBytes, filenameBase + suffix, (imageFormat == ImageFormat.JPEG) ? "image/jpeg" : "image/png"));
        }
        else
        {
            if (doneSound != null)
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
    IEnumerator UploadImage(byte[] imageFileBytes, string filename, string mimeType)
    {
        float startTime = Time.realtimeSinceStartup;

        WWWForm form = new WWWForm();

        form.AddField("key", apiKey);
        form.AddField("action", "upload");
        form.AddBinaryData("source", imageFileBytes, filename, "image/jpeg");

        WWW w = new WWW(apiUrl + "upload", form);
        yield return w;
        if (!string.IsNullOrEmpty(w.error))
        {
            Debug.LogError(w.error, this);
        }
        else
        {
            Log("Time to upload panorama screenshot: " + (Time.realtimeSinceStartup - startTime) + " sec");
            if (doneSound != null)
            {
                AudioSource.PlayClipAtPoint(doneSound, transform.position);
            }
            Capturing = false;
        }
    }

    IEnumerator CubemapToEquirectangular(Texture2D[] cubemapTexs, int cubemapSize, byte[] pixelValues,
        int stride, int equirectangularWidth, int equirectangularHeight)
    {
        if (usingGpuTransform)
        {
            convertPanoramaShader.SetTexture(kernelIdx, "equirectangular", equirectangularRenderTexture);
            foreach (CubemapFace face in faces)
            {
                convertPanoramaShader.SetTexture(kernelIdx, "cubemapFace" + face.ToString(), cubemapTexs[(int)face]);
            }
            convertPanoramaShader.SetInt("equirectangularWidth", equirectangularWidth);
            convertPanoramaShader.SetInt("equirectangularHeight", equirectangularHeight);
            convertPanoramaShader.SetInt("cubemapSize", cubemapSize);

            convertPanoramaShader.SetInt("startX", 0);
            convertPanoramaShader.SetInt("startY", 0);

            // TODO: fix params here when renderTextureHeight/Width < panoWidth/HeightWithSsaa
            convertPanoramaShader.Dispatch(kernelIdx, equirectangularWidth, equirectangularHeight, 1);

            RenderTexture.active = equirectangularRenderTexture;
            equirectangularTexture.ReadPixels(new Rect(0, 0, equirectangularWidth, equirectangularHeight), 0, 0);
            equirectangularTexture.Apply();

            // Copy to pixelValues output array
            Color32[] texturePixels = equirectangularTexture.GetPixels32();
            int inputIdx = 0;
            for (int y = 0; y < equirectangularHeight; y++)
            {
                int outputIdx = stride * y;
                for (int x = 0; x < equirectangularWidth; x++)
                {
                    Color32 c = texturePixels[inputIdx];
                    pixelValues[outputIdx + 0] = c.b;
                    pixelValues[outputIdx + 1] = c.g;
                    pixelValues[outputIdx + 2] = c.r;
                    pixelValues[outputIdx + 3] = c.a;
                    outputIdx += 4;
                    inputIdx++;
                }
            }
        }
        else
        {
            yield return StartCoroutine(CubemapToEquirectangularCpu(cubemapTexs, cubemapSize, pixelValues,
                stride, equirectangularWidth, equirectangularHeight));
        }
    }

    IEnumerator CubemapToEquirectangularCpu(Texture2D[] cubemapTexs, int cubemapSize, byte[] pixelValues,
        int stride, int equirectangularWidth, int equirectangularHeight)
    {
        Log("Converting to equirectangular");

        yield return null; // Wait for next frame at beginning - already used up some time capturing snapshot

        float startTime = Time.realtimeSinceStartup;
        float processingTimePerFrame = millisecondsPerFrame / 1000.0f;
        float maxValue = 1.0f - 1.0f / cubemapSize;
        int height = equirectangularHeight;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < equirectangularWidth; x++)
            {
                float xcoord = (float)x / equirectangularWidth;
                float ycoord = (float)y / equirectangularHeight;
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

                // equirectangular.SetPixel(x, y, );
                Color32 c = cubemapTexs[(int)face].GetPixelBilinear(u, v);
                int baseIdx = stride * (height - 1 - y) + x * 4;
                pixelValues[baseIdx + 0] = c.b;
                pixelValues[baseIdx + 1] = c.g;
                pixelValues[baseIdx + 2] = c.r;
                pixelValues[baseIdx + 3] = c.a;

                if ((x & 0xFF) == 0 && Time.realtimeSinceStartup - startTime > processingTimePerFrame)
                {
                    yield return null; // Wait until next frame
                    startTime = Time.realtimeSinceStartup;
                }
            }
        }

        yield return null;
    }
}
