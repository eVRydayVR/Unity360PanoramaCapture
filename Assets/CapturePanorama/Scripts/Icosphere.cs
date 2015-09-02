/* Copyright (c) 2015 D Coetzee (eVRydayVR) and other contributors

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
using UnityEngine;

// Based on http://blog.andreaskahler.com/2009/06/creating-icosphere-mesh-in-code.html
// Currently unused but planned for use with future features like seam-free mono capture.
namespace CapturePanorama
{
    public static class Icosphere
    {

        // Use this for initialization
        public static Mesh BuildIcosphere(float radius, int iterations)
        {
            Mesh result = BuildIcosahedron(radius);
            for (int i = 0; i < iterations; i++)
                Refine(result);
            return result;
        }

        public static Mesh BuildIcosahedron(float radius) // radius is distance to each vertex from origin
        {
            Mesh result = new Mesh();

            // create 12 vertices of a icosahedron
            float t = (float)((1.0 + Math.Sqrt(5.0)) / 2.0);

            Vector3[] vertices = new Vector3[]
            {
                new Vector3(-1.0f,     t,  0.0f),
                new Vector3( 1.0f,     t,  0.0f),
                new Vector3(-1.0f,    -t,  0.0f),
                new Vector3( 1.0f,    -t,  0.0f),
	
    	        new Vector3( 0.0f, -1.0f,     t),
                new Vector3( 0.0f,  1.0f,     t),
                new Vector3( 0.0f, -1.0f,    -t),
                new Vector3( 0.0f,  1.0f,    -t),
	
	            new Vector3(    t,  0.0f, -1.0f),
                new Vector3(    t,  0.0f,  1.0f),
                new Vector3(   -t,  0.0f, -1.0f),
                new Vector3(   -t,  0.0f,  1.0f),
            };

            float scale = radius / new Vector3(1.0f, t, 0.0f).magnitude;
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] *= scale;

            result.vertices = vertices;

            result.triangles = new int[]
            {
                // 5 faces around point 0
                0, 11, 5,
                0, 5, 1,
                0, 1, 7,
                0, 7, 10,
                0, 10, 11,

                // 5 adjacent faces
                1, 5, 9,
                5, 11, 4,
                11, 10, 2,
                10, 7, 6,
                7, 1, 8,

                // 5 faces around point 3
                3, 9, 4,
                3, 4, 2,
                3, 2, 6,
                3, 6, 8,
                3, 8, 9,

                // 5 adjacent faces
                4, 9, 5,
                2, 4, 11,
                6, 2, 10,
                8, 6, 7,
                9, 8, 1,
            };

            return result;
        }

        private static void Refine(Mesh m)
        {
            throw new Exception("TODO");
        }
    }
}
