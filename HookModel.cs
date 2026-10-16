using System.Collections.Generic;
using UnityEngine;

namespace HangingHammock
{
    ///  ованый крюк: из потолка торчит только шейка с буртиком и J-изгиб.
    /// Ћокальные оси: +Y Ч в сторону дерева, X Ч куда загибаетс€ крюк, Z Ч вдоль гамака.
    internal static class HookModel
    {
        public const float WireRadius = 0.005f;   // радиус проволоки (дырка петли узла ~0.007, не делать больше)
        public const float Neck = 0.025f;         // шейка от потолка до начала изгиба
        public const float Reach = 0.03f;         // радиус изгиба
        public const float Depth = Neck + Reach;  // от потолка до нижней точки изгиба (место контакта с петлЄй)

        private const float Embed = 0.03f;        // сколько проволоки Ђвнутриї дерева (не видно)

        private static Mesh mesh;
        private static Material material;

        public static Mesh Get()
        {
            if (mesh != null) return mesh;

            var pts = new List<Vector3>();
            var rad = new List<float>();
            void P(float x, float y, float r) { pts.Add(new Vector3(x, y, 0f)); rad.Add(r); }

            float rw = WireRadius;

            // часть в дереве + буртик (кованое утолщение у входа в дерево)
            P(0f,  Embed,       rw * 2.2f);
            P(0f,  0.004f,      rw * 2.2f);
            P(0f, -0.010f,      rw);
            // шейка
            P(0f, -Neck * 0.5f, rw);
            P(0f, -Neck,        rw);

            // J-изгиб: от 180∞ через низ (270∞) до ~380∞, конец заострЄн
            const int arcN = 20;
            float span = Mathf.PI + 0.35f;
            for (int i = 1; i <= arcN; i++)
            {
                float u = i / (float)arcN;
                float a = Mathf.PI + u * span;
                float x = Reach + Reach * Mathf.Cos(a);
                float y = -Neck + Reach * Mathf.Sin(a);
                float r = rw * Mathf.Lerp(1f, 0.04f, Mathf.SmoothStep(0f, 1f, (u - 0.65f) / 0.35f));
                P(x, y, r);
            }

            var v = new List<Vector3>(); var n = new List<Vector3>();
            var uv = new List<Vector2>(); var t = new List<int>();
            AddSweep(v, n, uv, t, pts, rad, 10);

            mesh = new Mesh { name = "hammock_hook" };
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// “руба вдоль плоской (XY) ломаной с переменным радиусом
        private static void AddSweep(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t,
                                     List<Vector3> pts, List<float> rad, int segV)
        {
            int b = v.Count;
            float along = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 a = pts[Mathf.Max(i - 1, 0)], c = pts[Mathf.Min(i + 1, pts.Count - 1)];
                Vector3 tan = (c - a).normalized;
                Vector3 np = new Vector3(-tan.y, tan.x, 0f);          // = cross(Z, tan)
                if (i > 0) along += (pts[i] - pts[i - 1]).magnitude;

                for (int j = 0; j <= segV; j++)
                {
                    float q = j / (float)segV * Mathf.PI * 2f;
                    Vector3 nn = np * Mathf.Cos(q) + Vector3.forward * Mathf.Sin(q);
                    v.Add(pts[i] + nn * rad[i]);
                    n.Add(nn);
                    uv.Add(new Vector2(j / (float)segV, along * 20f));
                }
            }
            int row = segV + 1;
            for (int i = 0; i < pts.Count - 1; i++)
                for (int j = 0; j < segV; j++)
                {
                    int a0 = b + i * row + j, a1 = a0 + row;
                    t.Add(a0); t.Add(a0 + 1); t.Add(a1);
                    t.Add(a1); t.Add(a0 + 1); t.Add(a1 + 1);
                }
        }

        public static Material GetMaterial(Material hammockMat)
        {
            if (material != null) return material;

            Material baseM = hammockMat != null && hammockMat.HasProperty("_MainTex") ? hammockMat : null;
            if (baseM == null) { var sh = Shader.Find("Standard"); if (sh != null) baseM = new Material(sh); }
            if (baseM == null) baseM = new Material(Shader.Find("Sprites/Default"));

            material = new Material(baseM) { name = "hammock_hook" };
            material.mainTexture = MakeIronTexture();
            material.mainTextureScale = Vector2.one;
            material.mainTextureOffset = Vector2.zero;
            if (material.HasProperty("_Color")) material.color = Color.white;
            if (material.HasProperty("_BumpMap")) material.SetTexture("_BumpMap", null);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.3f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.1f);
            return material;
        }

        /// “Ємное кованое железо с редкими ржавыми п€тнами
        private static Texture2D MakeIronTexture()
        {
            const int S = 32;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true)
            { name = "hammock_hook_tex", wrapMode = TextureWrapMode.Repeat };
            var iron = new Color(0.20f, 0.19f, 0.18f);
            var rust = new Color(0.36f, 0.20f, 0.11f);
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float h = Mathf.Abs(Mathf.Sin(x * 12.9898f + y * 78.233f) * 43758.5453f) % 1f;
                    float patch = Mathf.PerlinNoise(x * 0.3f + 7f, y * 0.3f + 3f);
                    Color c = Color.Lerp(iron * Mathf.Lerp(0.75f, 1.25f, h), rust, Mathf.SmoothStep(0.6f, 0.85f, patch) * 0.7f);
                    c.a = 1f;
                    px[y * S + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }
    }
}