﻿/* Copyright (c) 2015 D Coetzee (eVRydayVR), VRCHIVE, and other rights-holders

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

using System;
using System.IO;
using UnityEngine;

namespace CapturePanorama
{
    public class ReadPanoConfig : MonoBehaviour
    {
        public string iniPath;

        void Start()
        {
            if (Application.isEditor)
                return;

            CapturePanorama pano = GetComponent<CapturePanorama>();
            string path = iniPath;
            if (path == "")
            {
                string filename = "CapturePanorama.ini";
                path = Application.dataPath + "/" + filename;
            }
            if (!File.Exists(path))
            {
                // INI file does not exist, creating instead
                WriteConfig(path, pano);
                return;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Trim() == "")
                    continue;

                string[] splitLine = line.Split(new char[] { '=' }, 2);
                string key = splitLine[0].Trim();
                string val = splitLine[1].Trim();

                if (key == "Panorama Name")
                    pano.panoramaName = val;
                else if (key == "Capture Key")
                    pano.captureKey = (KeyCode)Enum.Parse(typeof(KeyCode), val);
                else if (key == "Image Format")
                    pano.imageFormat = (CapturePanorama.ImageFormat)Enum.Parse(typeof(CapturePanorama.ImageFormat), val);
                else if (key == "Capture Stereoscopic")
                    pano.captureStereoscopic = bool.Parse(val);
                else if (key == "Interpupillary Distance")
                    pano.interpupillaryDistance = float.Parse(val);
                else if (key == "Num Circle Points")
                    pano.numCirclePoints = int.Parse(val);
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
                else if (key == "Max Frames To Record")
                    pano.maxFramesToRecord = val == "" ? 0 : int.Parse(val);
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

        private void WriteConfig(string path, CapturePanorama pano)
        {
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("Panorama Name" + "=" + pano.panoramaName);
                writer.WriteLine("Capture Key" + "=" + pano.captureKey);
                writer.WriteLine("Image Format" + "=" + pano.imageFormat);
                writer.WriteLine("Capture Stereoscopic" + "=" + pano.captureStereoscopic);
                writer.WriteLine("Interpupillary Distance" + "=" + pano.interpupillaryDistance);
                writer.WriteLine("Num Circle Points" + "=" + pano.numCirclePoints);
                writer.WriteLine("Panorama Width" + "=" + pano.panoramaWidth);
                writer.WriteLine("Anti Aliasing" + "=" + (int)pano.antiAliasing);
                writer.WriteLine("Ssaa Factor" + "=" + pano.ssaaFactor);
                writer.WriteLine("Save Image Path" + "=" + pano.saveImagePath);
                writer.WriteLine("Save Cubemap" + "=" + pano.saveCubemap);
                writer.WriteLine("Upload Images" + "=" + pano.uploadImages);
                writer.WriteLine("Use Default Orientation" + "=" + pano.useDefaultOrientation);
                writer.WriteLine("Use Gpu Transform" + "=" + pano.useGpuTransform);
                writer.WriteLine("Cpu Milliseconds Per Frame" + "=" + pano.cpuMillisecondsPerFrame);
                writer.WriteLine("Capture Every Frame" + "=" + pano.captureEveryFrame);
                writer.WriteLine("Frame Rate" + "=" + pano.frameRate);
                writer.WriteLine("Max Frames To Record" + "=" + pano.maxFramesToRecord);
                writer.WriteLine("Frame Number Digits" + "=" + pano.frameNumberDigits);
                writer.WriteLine("Fade During Capture" + "=" + pano.fadeDuringCapture);
                writer.WriteLine("Fade Time" + "=" + pano.fadeTime);
                writer.WriteLine("Enable Debugging" + "=" + pano.enableDebugging);
            }
        }
    }
}
