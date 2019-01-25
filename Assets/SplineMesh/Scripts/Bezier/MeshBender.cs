﻿using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SplineMesh {
    /// <summary>
    /// A component that create a deformed mesh from a given one, according to a cubic Bézier curve and other parameters.
    /// The mesh will always be bended along the X axis. Extreme X coordinates of source mesh verticies will be used as a bounding to the deformed mesh.
    /// The resulting mesh is stored in a MeshFilter component and automaticaly updated each time the cubic Bézier curve control points are changed.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    [ExecuteInEditMode]
    public class MeshBender : MonoBehaviour {
        private bool isDirty = false;
        private Mesh result;
        private List<Vertex> vertices = new List<Vertex>();
        private List<Vertex> transformedVertices = new List<Vertex>();
        private float minX, length;

        private Mesh source;
        /// <summary>
        /// The source mesh to bend.
        /// </summary>
        public Mesh Source {
            get { return source; }
            set {
                if (value == source) return;
                isDirty = true;
                source = value;
                vertices.Clear();
                int i = 0;
                foreach (Vector3 vert in source.vertices) {
                    Vertex v = new Vertex {
                        v = vert,
                        n = source.normals[i++]
                    };
                    vertices.Add(v);
                }

                result.hideFlags = source.hideFlags;
                result.indexFormat = source.indexFormat;
                result.vertices = source.vertices.ToArray();
                result.triangles = source.triangles.ToArray();

                result.uv = source.uv.ToArray();
                result.uv2 = source.uv2.ToArray();
                result.uv3 = source.uv3.ToArray();
                result.uv4 = source.uv4.ToArray();
                result.uv5 = source.uv5.ToArray();
                result.uv6 = source.uv6.ToArray();
                result.uv7 = source.uv7.ToArray();
                result.uv8 = source.uv8.ToArray();
                result.tangents = source.tangents.ToArray();
            }
        }

        private CubicBezierCurve curve;
        /// <summary>
        /// The cubic Bézier curve to use to bend the source mesh. The curve is observed and the mesh is bended again each time it changes.
        /// </summary>
        public CubicBezierCurve Curve {
            get { return curve; }
            set {
                if (value == curve) return;
                if(value == null) throw new ArgumentNullException("Value");
                isDirty = true;
                if (curve != null) {
                    curve.Changed.RemoveListener(Compute);
                }
                curve = value;
                curve.Changed.AddListener(Compute);
            }
        }

        private Vector3 translation;
        /// <summary>
        /// The offset to apply to the source mesh before bending it.
        /// </summary>
        public Vector3 Translation {
            get { return translation; }
            set {
                if (value == translation) return;
                isDirty = true;
                translation = value;
            }
        }

        private Quaternion rotation;
        /// <summary>
        /// The rotation to apply to the source mesh before bending it.
        /// Because source mesh will always be bended along the X axis but may be oriented differently.
        /// </summary>
        public Quaternion Rotation {
            get { return rotation; }
            set {
                if (value == rotation) return;
                isDirty = true;
                rotation = value;
            }
        }

        private Vector3 scale = Vector3.one;
        /// <summary>
        /// The scale to apply to the source mesh before bending it.
        /// Scale on X axis is internaly limited to -1;1 to restrain the mesh inside the curve bounds.
        /// </summary>
        public Vector3 Scale {
            get { return scale; }
            set {
                if (value == scale) return;
                isDirty = true;
                scale = value;
                scale.x = Mathf.Clamp(scale.x, -1, 1);
            }
        }

        private void OnEnable() {
            if(GetComponent<MeshFilter>().sharedMesh != null) {
                result = GetComponent<MeshFilter>().sharedMesh;
            } else {
                GetComponent<MeshFilter>().sharedMesh = result = new Mesh();
                result.name = "Generated by " + GetType().Name;
            }
        }

        /// <summary>
        /// Build data that are consistent between computations if no property has been changed.
        /// This method allows the computation due to curve changes to be faster.
        /// </summary>
        private void BuildData() {
            if (source == null) throw new Exception(GetType().Name + " can't compute because there is no source mesh.");

            // find the bounds along x
            minX = float.MaxValue;
            float maxX = float.MinValue;
            foreach (Vertex vert in vertices) {
                Vector3 p = vert.v;
                if (rotation != Quaternion.identity) {
                    p = rotation * p;
                }
                if (translation != Vector3.zero) {
                    p += translation;
                }
                maxX = Math.Max(maxX, p.x);
                minX = Math.Min(minX, p.x);
            }
            length = Math.Abs(maxX - minX);

            // if the mesh is reversed by scale, we must change the culling of the faces by inversing all triangles.
            // the mesh is reverse only if the number of resersing axes is impair.
            bool reversed = scale.x < 0;
            if (scale.y < 0) reversed = !reversed;
            if (scale.z < 0) reversed = !reversed;
            result.triangles = reversed ? MeshUtility.GetReversedTriangles(source) : source.triangles;

            // we transform the source mesh vertices according to rotation/translation/scale
            transformedVertices.Clear();
            foreach (Vertex vert in vertices) {
                Vertex transformed = new Vertex() {
                    v = vert.v,
                    n = vert.n
                };
                //  application of rotation
                if (rotation != Quaternion.identity) {
                    transformed.v = rotation * transformed.v;
                    transformed.n = rotation * transformed.n;
                }
                if (scale != Vector3.one) {
                    transformed.v = Vector3.Scale(transformed.v, scale);
                    transformed.n = Vector3.Scale(transformed.n, scale);
                }
                if (translation != Vector3.zero) {
                    transformed.v += translation;
                }
                transformedVertices.Add(transformed);
            }
        }

        /// <summary>
        /// Bend the mesh only if a property has changed since the last compute.
        /// </summary>
        public void ComputeIfNeeded() {
            if (!isDirty) return;
            Compute();
        }

        /// <summary>
        /// Bend the mesh. This method may take time and should not be called more than necessary.
        /// Consider using <see cref="ComputeIfNeeded"/> for faster result.
        /// </summary>
        public void Compute() {
            if (isDirty) {
                BuildData();
            }
            isDirty = false;

            // we manage a cache because in most situations, the mesh will contain several vertices located at the same curve distance.
            Dictionary<float, CurveSample> sampleCache = new Dictionary<float, CurveSample>();

            List<Vertex> bentVertices = new List<Vertex>(vertices.Count);
            // for each mesh vertex, we found its projection on the curve
            foreach (Vertex vert in transformedVertices) {
                Vertex bent = new Vertex() {
                    v = vert.v,
                    n = vert.n
                };
                float distanceRate = Math.Abs(bent.v.x - minX) / length;
                CurveSample sample;
                if(!sampleCache.TryGetValue(distanceRate, out sample)){
                    sample = curve.GetSampleAtDistance(curve.Length * distanceRate);
                    sampleCache[distanceRate] = sample;
                }

                Quaternion q = sample.Rotation * Quaternion.Euler(0, -90, 0);

                // application of scale
                bent.v = Vector3.Scale(bent.v, new Vector3(0, sample.scale.y, sample.scale.x));

                // application of roll
                bent.v = Quaternion.AngleAxis(sample.roll, Vector3.right) * bent.v;
                bent.n = Quaternion.AngleAxis(sample.roll, Vector3.right) * bent.n;

                // reset X value
                bent.v.x = 0;

                // application of the rotation + location
                bent.v = q * bent.v + sample.location;
                bent.n = q * bent.n;
                bentVertices.Add(bent);
            }

            result.vertices = bentVertices.Select(b => b.v).ToArray();
            result.normals = bentVertices.Select(b => b.n).ToArray();
            result.RecalculateBounds();
        }

        [Serializable]
        private struct Vertex {
            public Vector3 v;
            public Vector3 n;
        }

        private void OnDestroy() {
            if(curve != null) {
                curve.Changed.RemoveListener(Compute);
            }
        }
    }
}