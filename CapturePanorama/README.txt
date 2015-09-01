Unity Script: 360 Panorama Capture
Version 1.2 - 2015 July 24 (Unity 5.1.2f1)

Captures a 360-degree panorama of the player's in-game surroundings and saves/uploads it for later viewing.

NOTE: This plugin currently requires Unity 5.x.

CAPTURING 360 IMAGES
--------------------

1. Create an empty game object and add the Capture Panorama script (CapturePanorama.cs) to it.
2. Under Edit->Project Settings->Player->Other Settings->Optimization, set "Api Compatibility Level" from ".NET 2.0 Subset" to ".NET 2.0".
3. If your application is a VR application using the Oculus VR plugin, uncomment the line "#define OVR_SUPPORT" at the top of CapturePanorama.cs. If you are using Unity native VR support, this is unnecessary.
4. Run your application. Press P to capture a panorama. A sound will play and the screen will fade to black. When it completes, a second sound will play and an 8192x4096 PNG file will be saved in the application directory. You can capture programmatically with CaptureScreenshotAsync().
5. When you're ready, check the "Upload image" property to automatically upload all screenshots to the VRCHIVE panorama sharing website (http://alpha.vrchive.org).

If the procedure does not complete as expected, check the "Enable Debugging" property on the Capture Panorama script, build and run the application, and then send the resulting "output_log.txt" file from your data directory to the developer (eVRydayVR@gmail.com).

RECORDING 360 VIDEOS
--------------------

As of version 1.2, the "Capture Every Frame" option can be used to create 360 videos suitable for uploading to providers such as YouTube and Vrideo. Steps follow.

Preparation:
* Make sure your application is able to run correctly when "Time.captureFramerate" is set to your desired video frame rate. Modify code which depends on the real clock time, e.g. waiting for a certain amount of time to pass, or waiting for audio events to complete, to instead use Time.deltaTime in Update().
* If you wish to capture gameplay and do not want to play the game at reduced frame rate during recording, implementing a replay system is recommended. This will also be useful for capturing the audio below.

Capture:

We will capture an image file for each frame of the video.

1. Check "Capture Every Frame" and enter the desired frame for capture in "Frame Rate".
2. The "BMP" image format provides the fastest capture. "PNG" will use less disk space.
3. Set "Save Image Path" to a fast disk with sufficient capacity for the raw video frames.
4. Set "Panorama Width" to the desired width of your video (typically 4096).
5. Enable "Use Gpu Transform". Disable "Save Cubemap" unless you want cube map images for each frame.
6. For highest quality with slower encoding, increase "Ssaa Factor" to 2, 3, or 4.
7. Start the application and use the capture hotkey ("P" by default) to toggle between capturing and not capturing. You can also programmatically call StartCaptureEveryFrame() and StopCaptureEveryFrame().
8. Run the same scene again at normal speed and use any recording software to separately record the audio.

Creating video:

Install ffmpeg, add it to your path, and save the included asset "assemble.cmd" batch file with your image sequence.

Example invocation of this script from the command line:

assemble Test_2015-07-24_06-42-00-045_ bmp test.mp4 60 18 ultrafast

The parameters are:
* The prefix and extension of each filename (e.g. in this case the filenames were of the form "Test_2015-07-24_06-42-00-045_000001.bmp");
* The output filename;
* The output frame rate (should match the frame rate set during capture);
* The CRF quality, typical range 14 for very good with large file to 24 for mediocre with small file;
* The encoding preset which trades off encoding time and quality, with "ultrafast" producing the largest file the most quickly. This also avoids certain encoding failures.

REFERENCE
---------

Properties on the Capture Panorama script:

* Panorama Name: Used as the prefix of the saved image filename. If "Upload Images" is enabled, this will appear in the title of the image on the web.

* Capture Key (default "P"): the key to press to capture a 360 screenshot. If you wish to handle your own input, set this to "None" and invoke the CaptureScreenshotAsync() method from your script. If "Capture Every Frame" is enabled, this start and stop capturing of the image sequence.

* Image Format (default PNG): Determines what format(s) to save/upload the image file in. JPEG produces smaller filesize but is much lower quality. BMP is faster to save than PNG but larger.

* Panorama Width (between 4 and 23800, default 8192): Determines width of the resulting panorama image. Height of the image will be half this. Typical reasonable values are 4096 and 8192. Need not be a power of two.

* Anti Aliasing (default 8): Sets the MSAA anti-aliasing quality to use during rendering.

* Ssaa Factor (default 1): Set to a larger value such as 2, 3, or 4 to render at a higher resolution and then downsample to produce the final image. Produces superior anti-aliasing at a large performance cost.

* Save Image Path: Directory where screenshots will be saved. If blank, the root application directory will be used.

* Save Cubemap (default off): Check to save the six captured cubemap images to disk. Some viewing software can view these directly. Will increase capture time.

* Upload Images (default off): Check to automatically publish panorama screenshots to VRCHIVE for sharing with others immediately after taking them. Visit alpha.vrchive.com to view them. Panoramas are currently uploaded anonymously (not under a user account).

* Use Default Orientation (default off): Resets the camera to the default (directly forward) rotation/orientation before taking the screenshot. May interfere with correct compositing if you have multiple cameras with different rotations. In VR applications, this is usually unnecessary because the headset orientation is used instead to correct the camera orientation.

* Use Gpu Transform (default on): Specifies whether to use the fast GPU-based shader to convert the captured cubemap to the final equirectangular image.

* Cpu Milliseconds Per Frame: When "Use Gpu Transform" is disabled, this will determine the number of CPU milliseconds to spend each frame on processing the panorama.

* Capture Every Frame (default off): When enabled, the Capture Key will start and stop the capturing of every frame to an image sequence.

* Frame Rate (default 30): Sets the frame rate used during capturing when Capture Every Frame is enabled. Determines what Time.captureFramerate will be set to.

* Frame Number Digits: When Capture Every Frame is enabled, this determines the number of digits to use for the frame number in the filenames of the image sequence (default 6, sufficient for 4.6 hours at 60 FPS).

* Start Sound: The sound played at the beginning of panorama processing. May be None.

* Done Sound: The sound played at the end of panorama processing. May be None.

* Fade During Capture: Whether to fade the screen to a solid color during capture. Helps to reduce simulator sickness, especially if Panorama Width is large. 

* Fade Time: How quickly to fade in/out. Affects total time needed for capture. A value of zero is not currently supported.

* Fade Color: Solid color to fade the screen to during capture.

* Fade Material: Material that will be placed in front of the camera during fade.

* Enable Debugging: Shows debugging logs and time-to-capture on the console.

CONFIG FILE
-----------

The ReadPanoConfig script allows users to modify panorama capture parameters in a build without modifying the source Unity project. To use it:

1. Add the ReadPanoConfig.cs script to the same object as the CapturePanorama.cs script.
2. Modify the included asset "CapturePanorama.ini" as needed for your application. WARNING: This will override any settings supplied in the Inspector.
3. After building, add the asset "CapturePanorama.ini" to the data directory of your build.
4. To modify settings, modify the .ini file and restart the application.

DEVELOPMENT NOTES
-----------------

In scenes using the OVR plugin, the left eye will be used as the point of rendering. The package supports scenes with multiple cameras or OVR camera rigs, each with different culling masks. They will be composited based on depth to reproduce the player's view. Some effects such as camera shaders may not be reproduced.

Only monoscopic panoramas, not stereoscopic panoramas, are currently supported. All views are rendered from the same point and there is no attempt to reproduce parallax.

If you need to determine if a panorama capture is in process (e.g. to wait for the capture to complete), you can check the "Capturing" property.

CREDITS
-------

Developed by D Coetzee of eVRydayVR: http://youtube.com/user/eVRydayVR

Funded by the panorama repository VRCHIVE: http://vrchive.com

Default sound effects Clicks_13 and Xylo_13 from:
Free SFX Package - Bleep Blop Audio
https://www.assetstore.unity3d.com/en/#!/content/5178

LICENSE
-------
This is free and unencumbered software released into the public domain.

Anyone is free to copy, modify, publish, use, compile, sell, or
distribute this software, either in source code form or as a compiled
binary, for any purpose, commercial or non-commercial, and by any
means.

In jurisdictions that recognize copyright laws, the author or authors
of this software dedicate any and all copyright interest in the
software to the public domain. We make this dedication for the benefit
of the public at large and to the detriment of our heirs and
successors. We intend this dedication to be an overt act of
relinquishment in perpetuity of all present and future rights to this
software under copyright law.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS BE LIABLE FOR ANY CLAIM, DAMAGES OR
OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
OTHER DEALINGS IN THE SOFTWARE.

For more information, please refer to <http://unlicense.org/>