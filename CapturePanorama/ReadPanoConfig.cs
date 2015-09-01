using UnityEngine;
using System;
using System.Collections;
using System.IO;

public class ReadPanoConfig : MonoBehaviour
{
    public string iniPath;

    void Start() {
        string path = iniPath;
        if (path == "")
        {
            string filename = "CapturePanorama.ini";
            path = Application.dataPath + "/" + filename;
            if (!File.Exists(path))
                path = Application.dataPath + "/CapturePanorama/" + filename;
        }
        if (!File.Exists(path))
        {
            Debug.LogError("Could not find CapturePanorama.ini");
            return;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Trim() == "")
                continue;

            string[] splitLine = line.Split(new char[] {'='}, 2);
            string key = splitLine[0].Trim();
            string val = splitLine[1].Trim();
            CapturePanorama pano = GetComponent<CapturePanorama>();

            if (key == "Panorama Name")
                pano.panoramaName = val;
            else if (key == "Capture Key")
                pano.captureKey = (KeyCode)Enum.Parse(typeof(KeyCode), val);
            else if (key == "Image Format")
                pano.imageFormat = (CapturePanorama.ImageFormat)Enum.Parse(typeof(CapturePanorama.ImageFormat), val);
            else if (key == "Panorama Width")
                pano.panoramaWidth = int.Parse(val);
            else if (key == "Anti Aliasing")
                pano.antiAliasing = (CapturePanorama.AntiAliasing)int.Parse(val);
            else if (key == "Ssaa Factor")
                pano.ssaaFactor = int.Parse(val);
            else if (key == "Save Image Path")
                pano.saveImagePath = val;
            else if (key == "Save Cubemap")
                pano.saveCubemap = bool.Parse(val);
            else if (key == "Upload Images")
                pano.uploadImages = bool.Parse(val);
            else if (key == "Use Default Orientation")
                pano.useDefaultOrientation = bool.Parse(val);
            else if (key == "Use Gpu Transform")
                pano.useGpuTransform = bool.Parse(val);
            else if (key == "Cpu Milliseconds Per Frame")
                pano.cpuMillisecondsPerFrame = (float)double.Parse(val);
            else if (key == "Capture Every Frame")
                pano.captureEveryFrame = bool.Parse(val);
            else if (key == "Frame Rate")
                pano.frameRate = int.Parse(val);
            else if (key == "Frame Number Digits")
                pano.frameNumberDigits = int.Parse(val);
            else if (key == "Fade During Capture")
                pano.fadeDuringCapture = bool.Parse(val);
            else if (key == "Fade Time")
                pano.fadeTime = float.Parse(val);
            else if (key == "Enable Debugging")
                pano.enableDebugging = bool.Parse(val);
            else
                Debug.LogError("Unrecognized key in line in CapturePanorama.ini: " + line);
        }
	}
}
