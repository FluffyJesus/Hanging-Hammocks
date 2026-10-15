using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HangingHammock
{
    internal static class ModelTweaks
    {
        /// Делает меш двусторонним и собирает его вершины в пространстве лодки
        public static Mesh Process(Mesh src, Matrix4x4 toBoat, List<Vector3> boatPts, out bool readable)
        {
            readable = src != null && src.isReadable;
            if (!readable)
            {
                if (src != null) Plugin.Log.LogWarning($"Mesh '{src.name}' is NOT readable: using material culling fallback");
                return src;
            }

            var v = src.vertices;
            foreach (var p in v) boatPts.Add(toBoat.MultiplyPoint3x4(p));

            var tris = new int[src.subMeshCount][];
            for (int s = 0; s < tris.Length; s++) tris[s] = src.GetTriangles(s);
            return MakeDoubleSided(src, v, tris);
        }

        private static Mesh MakeDoubleSided(Mesh src, Vector3[] v, int[][] tris)
        {
            int n = v.Length;
            var m = new Mesh { name = src.name + "_2sided" };
            if (n * 2 > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            m.vertices = Twice(v, x => x);
            var nr = src.normals;  if (nr.Length == n) m.normals = Twice(nr, x => -x);
            var tg = src.tangents; if (tg.Length == n) m.tangents = Twice(tg, x => new Vector4(-x.x, -x.y, -x.z, x.w));
            var uv = src.uv;       if (uv.Length == n) m.uv = Twice(uv, x => x);
            var uv2 = src.uv2;     if (uv2.Length == n) m.uv2 = Twice(uv2, x => x);
            var col = src.colors;  if (col.Length == n) m.colors = Twice(col, x => x);

            m.subMeshCount = tris.Length;
            for (int s = 0; s < tris.Length; s++)
            {
                var t = tris[s];
                var d = new int[t.Length * 2];
                Array.Copy(t, d, t.Length);
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    d[t.Length + i]     = t[i] + n;
                    d[t.Length + i + 1] = t[i + 2] + n;
                    d[t.Length + i + 2] = t[i + 1] + n;
                }
                m.SetTriangles(d, s);
            }
            if (nr.Length != n) m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        private static T[] Twice<T>(T[] a, Func<T, T> f)
        {
            var r = new T[a.Length * 2];
            for (int i = 0; i < a.Length; i++) { r[i] = a[i]; r[i + a.Length] = f(a[i]); }
            return r;
        }

        public static Material[] TryCullOff(Material[] mats)
        {
            var res = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                res[i] = m;
                if (m == null) continue;
                string prop = m.HasProperty("_Cull") ? "_Cull" : m.HasProperty("_CullMode") ? "_CullMode" : null;
                if (prop == null) { Plugin.Log.LogWarning($"Material '{m.name}' has no _Cull"); continue; }
                var c = new Material(m) { name = m.name + "_noCull" };
                c.SetInt(prop, 0);
                res[i] = c;
            }
            return res;
        }
    }

    internal static class Diagnostics
    {
        public static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
    }
}