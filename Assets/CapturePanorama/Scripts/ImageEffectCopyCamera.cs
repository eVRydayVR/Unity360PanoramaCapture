/* Copyright (c) 2015 D Coetzee (eVRydayVR), VRCHIVE, and other rights-holders

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
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CapturePanorama.Internals
{
    class ImageEffectCopyCamera : MonoBehaviour
    {
        public struct InstanceMethodPair {
            public object Instance;
            public MethodInfo Method;
        }
        public List<InstanceMethodPair> onRenderImageMethods = new List<InstanceMethodPair>();

        public static List<InstanceMethodPair> GenerateMethodList(Camera camToCopy)
        {
            var result = new List<InstanceMethodPair>();
            foreach (var script in camToCopy.gameObject.GetComponents<MonoBehaviour>())
            {
                if (script.enabled)
                {
                    Type scriptType = script.GetType();
                    MethodInfo m = scriptType.GetMethod("OnRenderImage",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new Type[] { typeof(RenderTexture), typeof(RenderTexture) }, null);
                    if (m != null)
                    {
                        InstanceMethodPair pair = new InstanceMethodPair();
                        pair.Instance = script;
                        pair.Method = m;
                        result.Add(pair);
                    }
                }
            }
            return result;
        }

        RenderTexture[] temp = new RenderTexture[] { null, null };

        void OnDestroy()
        {
            for (int i = 0; i < temp.Length; i++)
            {
                if (temp[i] != null)
                    Destroy(temp[i]);
                temp[i] = null;
            }
        }

        void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            int desiredDepth = Math.Max(src.depth, dest.depth);

            for (int i = 0; i < temp.Length; i++)
            {
                if (onRenderImageMethods.Count > i + 1)
                {
                    if (temp[i] != null &&
                        (temp[i].width != dest.width || temp[i].height != dest.height || temp[i].depth != desiredDepth || temp[i].format != dest.format))
                    {
                        Destroy(temp[i]);
                        temp[i] = null;
                    }
                    if (temp[i] == null)
                        temp[i] = new RenderTexture(dest.width, dest.height, desiredDepth, dest.format);
                }
            }

            var sequence = new List<RenderTexture>();
            sequence.Add(src);
            for (int i = 0; i < onRenderImageMethods.Count - 1; i++)
                sequence.Add(i % 2 == 0 ? temp[0] : temp[1]);
            sequence.Add(dest);

            for (int i = 0; i < onRenderImageMethods.Count; i++)
                onRenderImageMethods[i].Method.Invoke(onRenderImageMethods[i].Instance, new object[] { sequence[i], sequence[i + 1] });
        }
    }
}
