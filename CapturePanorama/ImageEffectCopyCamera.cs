using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Assets.CapturePanorama
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
            var parameters = new object[] { src, dest };
            foreach (var pair in onRenderImageMethods)
            {
                pair.Method.Invoke(pair.Instance, parameters);
            }
        }
    }
}
