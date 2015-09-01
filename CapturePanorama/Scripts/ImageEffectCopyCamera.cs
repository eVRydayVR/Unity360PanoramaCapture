// This is free and unencumbered software released into the public domain.
// For more information, please refer to <http://unlicense.org/>

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

        void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            var sequence = new List<RenderTexture>();
            sequence.Add(src);
            RenderTexture temp  = onRenderImageMethods.Count <= 1 ? null : new RenderTexture(dest.width, dest.height, dest.depth, dest.format);
            RenderTexture temp2 = onRenderImageMethods.Count <= 2 ? null : new RenderTexture(dest.width, dest.height, dest.depth, dest.format);
            for (int i = 0; i < onRenderImageMethods.Count - 1; i++)
                sequence.Add(i % 2 == 0 ? temp : temp2);
            sequence.Add(dest);

            for (int i = 0; i < onRenderImageMethods.Count; i++)
            {
                onRenderImageMethods[i].Method.Invoke(onRenderImageMethods[i].Instance, new object[] { sequence[i], sequence[i + 1] });
            }

            if (temp != null) Destroy(temp);
            if (temp2 != null) Destroy(temp2);
        }
    }
}
