using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NightSignal.Art
{
    /// <summary>Small immediate-mode mesh builder with submeshes, used by course, car, character and prop generators.</summary>
    public sealed class MeshBuilder
    {
        readonly List<Vector3> vertices = new List<Vector3>();
        readonly List<Vector3> normals = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<Color32> colors = new List<Color32>();
        readonly List<List<int>> submeshes = new List<List<int>>();
        public bool UseColors;

        public MeshBuilder(int submeshCount = 1)
        {
            for (int i = 0; i < submeshCount; i++) submeshes.Add(new List<int>());
        }

        public int VertexCount => vertices.Count;

        /// <summary>Position of an added vertex (for winding decisions while building).</summary>
        public Vector3 Position(int index) => vertices[index];

        public int AddVertex(Vector3 p, Vector3 n, Vector2 uv) => AddVertex(p, n, uv, new Color32(255, 255, 255, 255));

        public int AddVertex(Vector3 p, Vector3 n, Vector2 uv, Color32 c)
        {
            vertices.Add(p);
            normals.Add(n);
            uvs.Add(uv);
            colors.Add(c);
            return vertices.Count - 1;
        }

        public void AddTriangle(int sub, int a, int b, int c)
        {
            List<int> t = submeshes[sub];
            t.Add(a); t.Add(b); t.Add(c);
        }

        public void AddQuad(int sub, int a, int b, int c, int d)
        {
            AddTriangle(sub, a, b, c);
            AddTriangle(sub, a, c, d);
        }

        /// <summary>Flat-shaded quad from four corners (clockwise when viewed from the front, Unity convention).</summary>
        public void AddFlatQuad(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 uvScale, Color32? color = null)
        {
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            Color32 col = color ?? new Color32(255, 255, 255, 255);
            float w = Vector3.Distance(a, b), h = Vector3.Distance(a, d);
            int i0 = AddVertex(a, n, new Vector2(0, 0), col);
            int i1 = AddVertex(b, n, new Vector2(w * uvScale.x, 0), col);
            int i2 = AddVertex(c, n, new Vector2(w * uvScale.x, h * uvScale.y), col);
            int i3 = AddVertex(d, n, new Vector2(0, h * uvScale.y), col);
            AddQuad(sub, i0, i1, i2, i3);
        }

        /// <summary>Axis-aligned-in-local-frame box (centre, half size, rotation) with flat normals.</summary>
        public void AddBox(int sub, Vector3 centre, Vector3 half, Quaternion rot, float uvScale = 1f, Color32? color = null)
        {
            Vector3 P(float x, float y, float z) => centre + rot * Vector3.Scale(half, new Vector3(x, y, z));
            Vector2 s = new Vector2(uvScale, uvScale);
            AddFlatQuad(sub, P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), s, color);    // front (+z) faces +z
            AddFlatQuad(sub, P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), s, color); // back
            AddFlatQuad(sub, P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), s, color); // left
            AddFlatQuad(sub, P(1, -1, 1), P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), s, color);    // right
            AddFlatQuad(sub, P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), P(-1, 1, -1), s, color);    // top
            AddFlatQuad(sub, P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), s, color); // bottom
        }

        /// <summary>Closed cylinder along local +y (for posts, poles, lantern bodies).</summary>
        public void AddCylinder(int sub, Vector3 bottom, float radius, float height, int segments, Quaternion rot, Color32? color = null)
        {
            Color32 col = color ?? new Color32(255, 255, 255, 255);
            int start = vertices.Count;
            for (int i = 0; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Vector3 n = rot * dir;
                AddVertex(bottom + rot * (dir * radius), n, new Vector2(i / (float)segments, 0f), col);
                AddVertex(bottom + rot * (dir * radius + Vector3.up * height), n, new Vector2(i / (float)segments, height), col);
            }
            for (int i = 0; i < segments; i++)
            {
                int b0 = start + i * 2;
                AddQuad(sub, b0, b0 + 1, b0 + 3, b0 + 2);
            }
            Vector3 top = bottom + rot * (Vector3.up * height);
            int ct = AddVertex(top, rot * Vector3.up, new Vector2(0.5f, 0.5f), col);
            int firstTop = vertices.Count;
            for (int i = 0; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                AddVertex(top + rot * new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius), rot * Vector3.up, new Vector2(0.5f, 0.5f), col);
            }
            for (int i = 0; i < segments; i++) AddTriangle(sub, ct, firstTop + i + 1, firstTop + i);
        }

        public Mesh Build(string name, bool recalculateNormals = false)
        {
            var mesh = new Mesh { name = name };
            if (vertices.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            if (UseColors) mesh.SetColors(colors);
            mesh.subMeshCount = submeshes.Count;
            for (int i = 0; i < submeshes.Count; i++) mesh.SetTriangles(submeshes[i], i, false);
            if (recalculateNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }
    }

    public static class GameLayers
    {
        public const int Drivable = 8;
        public const int Barrier = 9;
        public const int Vehicle = 10;
        public const int Scenery = 11;
        public const int Avatar = 12;
        public const int DrivableMask = 1 << Drivable;
        public const int BarrierMask = 1 << Barrier;
    }
}
