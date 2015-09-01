Unity Script: 360 Panorama Capture
Version 1.1 - 2015 June 27 (Unity 5.1.0p1)

Captures a 360-degree panorama of the player's in-game surroundings and saves/uploads it for later viewing.

SETUP
-----

NOTE: This plugin currently requires Unity 5.x.

1. Create an empty game object and add the Capture Panorama script (CapturePanorama.cs) to it.
2. Under Edit->Project Settings->Player->Other Settings->Optimization, set "Api Compatibility Level" from ".NET 2.0 Subset" to ".NET 2.0".
3. If your application is a VR application using the Oculus VR plugin, uncomment the line "#define OVR_SUPPORT" at the top of CapturePanorama.cs. If you are using Unity native VR support, this is unnecessary.
4. Run your application. Press P to capture a panorama. Processing will run in the background and may take 30-90 seconds. When it completes, a sound will play and an 8192x4096 PNG file will be saved in the application directory.
5. When you're ready, check the "Upload image" property to automatically upload all screenshots to the VRCHIVE panorama sharing website (http://alpha.vrchive.org).

If the procedure does not complete as expected, check the "Enable Debugging" property on the Capture Panorama script, build and run the application, and then send the resulting "output_log.txt" file from your data directory to the developer (eVRydayVR@gmail.com).

REFERENCE
---------

Properties on the Capture Panorama script:

* Panorama Name: Used as the prefix of the saved image filename. If "Upload Images" is enabled, this will appear in the title of the image on the web.

* Quality Setting: If this is set to the name of one of your quality settings, that quality setting will be used during panorama capture. It is recommended to use the highest available quality setting. By default the same quality setting will be used as during regular play.

* Screenshot Key (default "P"): the key to press to take a screenshot. If you wish to handle your own input, set this to "None" and invoke the CaptureScreenshotAsync() method from your script.

* Image Format (default PNG): Set to PNG or JPEG to determine what format(s) to save/upload the image file in. JPEG produces smaller filesize but is much lower quality.

* Panorama Width (between 4 and 32767, default 8192): Determines width of the resulting panorama image. Height of the image will be half this. Typical reasonable values are 2048, 4096, and 8192.

* Save Image Path: Directory where screenshots will be saved. If blank, the root application directory will be used.

* Upload Images (default off): Check to automatically publish panorama screenshots to VRCHIVE for sharing with others immediately after taking them. Visit alpha.vrchive.com to view them. Panoramas are currently uploaded anonymously (not under a user account).

* Use Default Orientation (default off): Resets the camera to the default (directly forward) rotation/orientation before taking the screenshot. May interfere with correct compositing if you have multiple cameras with different rotations. In VR applications, this is usually unnecessary because the headset orientation is used instead to correct the camera orientation.

* Milliseconds Per Frame: The CPU milliseconds to spend each frame on processing the panorama. The higher this is the more the frame rate will be reduced during processing, but the faster the processing will complete. By default it is 8.33 ms (1/120th sec).

* Start Sound: The sound played at the beginning of panorama processing. May be None.

* Done Sound: The sound played at the end of panorama processing. May be None.

* Fade During Capture: Whether to fade the screen to a solid color during capture. Helps to reduce simulator sickness, especially if Panorama Width is large. 

* Fade Time: How quickly to fade in/out. Affects total time needed for capture. A value of zero is not currently supported.

* Fade Color: Solid color to fade the screen to during capture.

* Fade Material: Material that will be placed in front of the camera during fade.

Development notes:

In scenes using the OVR plugin, the left eye will be used as the point of rendering. The package supports scenes with multiple cameras or OVR camera rigs, each with different culling masks. They will be composited based on depth to reproduce the player's view. Some effects such as camera shaders may not be reproduced.

Only monoscopic panoramas, not stereoscopic panoramas, are currently supported. All views are rendered from the same point and there is no attempt to reproduce parallax.

If you need to determine if a panorama capture is in process (e.g. to wait for the capture to complete), you can check the "Capturing" property.

If you want to produce video using this plugin, the key is to integrate it with a replay system. You should step through the replay one frame at a time, and for each frame invoke CaptureScreenshotAsync(). The resulting screenshots can be combined into a video using ffmpeg. This will work but will be very slow; I have a GPU-based shader which is faster but needs some work to fully prepare for a future release.

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