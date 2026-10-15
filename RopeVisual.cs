using UnityEngine;
using UnityEngine.Rendering;

namespace HangingHammock
{
    /// Верёвка-цилиндр со сгенерированной витой текстурой и узлом у гамака
    internal sealed class RopeVisual
    {
        private static readonly Color RopeColor = new Color32(0x8C, 0x73, 0x50, 0xFF);

        private static Mesh cylinder, knotMesh;
        private static Material material;
        private static MaterialPropertyBlock mpb;
        private static readonly int MainTexST = Shader.PropertyToID("_MainTex_ST");

        private readonly GameObject ropeGo, knotGo;
        private readonly MeshRenderer ropeR;
        private bool visible = true;

        public RopeVisual(string name, Material hammockMat)
        {
            var mat = GetMaterial(hammockMat);

            ropeGo = new GameObject(name);
            ropeGo.AddComponent<MeshFilter>().sharedMesh = Cylinder();
            ropeR = ropeGo.AddComponent<MeshRenderer>();
            ropeR.sharedMaterial = mat;
            ropeR.shadowCastingMode = ShadowCastingMode.On;

            knotGo = new GameObject(name + "_knot");
            knotGo.AddComponent<MeshFilter>().sharedMesh = Knot();
            var kr = knotGo.AddComponent<MeshRenderer>();
            kr.sharedMaterial = mat;
            kr.shadowCastingMode = ShadowCastingMode.On;

            SetVisible(false);
        }

        public void Set(Vector3 top, Vector3 bottom)
        {
            Vector3 d = top - bottom;
            float len = d.magnitude;
            if (len < 1e-4f) { SetVisible(false); return; }
            SetVisible(true);

            Vector3 dir = d / len;
            Quaternion q = Quaternion.FromToRotation(Vector3.up, dir);
            float w = Mathf.Max(0.005f, Tuning.RopeThickness);

            ropeGo.transform.SetPositionAndRotation(bottom, q);
            ropeGo.transform.localScale = new Vector3(w, len, w);

            if (mpb == null) mpb = new MaterialPropertyBlock();
            ropeR.GetPropertyBlock(mpb);
            mpb.SetVector(MainTexST, new Vector4(1f, len / (w * 3f), 0f, 0f));   // шаг витка ? 3 толщины
            ropeR.SetPropertyBlock(mpb);

            float k = Tuning.KnotSize;
            knotGo.transform.SetPositionAndRotation(bottom + dir * (k * 0.25f), q);
            knotGo.transform.localScale = new Vector3(k, k * 1.4f, k);
        }

        public void SetVisible(bool v)
        {
            if (visible == v) return;
            visible = v;
            if (ropeGo != null) ropeGo.SetActive(v);
            if (knotGo != null) knotGo.SetActive(v);
        }

        public void Destroy()
        {
            if (ropeGo != null) Object.Destroy(ropeGo);
            if (knotGo != null) Object.Destroy(knotGo);
        }

        // ????????? ресурсы ?????????
        private static Mesh Cylinder()
        {
            if (cylinder != null) return cylinder;
            const int seg = 8;
            var v = new Vector3[(seg + 1) * 2];
            var n = new Vector3[v.Length];
            var uv = new Vector2[v.Length];
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                float x = Mathf.Cos(a), z = Mathf.Sin(a);
                v[i * 2] = new Vector3(x * 0.5f, 0f, z * 0.5f);
                v[i * 2 + 1] = new Vector3(x * 0.5f, 1f, z * 0.5f);
                n[i * 2] = n[i * 2 + 1] = new Vector3(x, 0f, z);
                uv[i * 2] = new Vector2(i / (float)seg, 0f);
                uv[i * 2 + 1] = new Vector2(i / (float)seg, 1f);
            }
            var t = new int[seg * 6];
            for (int i = 0; i < seg; i++)
            {
                int b0 = i * 2, t0 = i * 2 + 1, b1 = (i + 1) * 2, t1 = (i + 1) * 2 + 1;
                t[i * 6] = b0; t[i * 6 + 1] = t0; t[i * 6 + 2] = t1;
                t[i * 6 + 3] = b0; t[i * 6 + 4] = t1; t[i * 6 + 5] = b1;
            }
            cylinder = new Mesh { name = "hammock_rope_cylinder", vertices = v, normals = n, uv = uv, triangles = t };
            cylinder.RecalculateTangents();
            cylinder.RecalculateBounds();
            return cylinder;
        }

        private static Mesh Knot()
        {
            if (knotMesh != null) return knotMesh;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            knotMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
            return knotMesh;
        }

        private static Material GetMaterial(Material hammockMat)
        {
            if (material != null) return material;

            Material baseM = hammockMat != null && hammockMat.HasProperty("_MainTex") ? hammockMat : null;
            if (baseM == null) { var sh = Shader.Find("Standard"); if (sh != null) baseM = new Material(sh); }
            if (baseM == null) baseM = new Material(Shader.Find("Sprites/Default"));

            material = new Material(baseM) { name = "hammock_rope" };
            material.mainTexture = MakeTexture();
            if (material.HasProperty("_Color")) material.color = Color.white;
            if (material.HasProperty("_BumpMap")) material.SetTexture("_BumpMap", null);
            return material;
        }

        private static Texture2D MakeTexture()
        {
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true)
            { name = "hammock_rope_tex", wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float u = x / (float)S, v = y / (float)S;
                    float s = Mathf.Repeat(u * 3f + v, 1f);                         // три пряди, один виток на тайл
                    float strand = Mathf.Pow(Mathf.Sin(s * Mathf.PI), 0.6f);         // 0 в канавке, 1 на гребне
                    float shade = Mathf.Lerp(0.45f, 1.05f, strand);
                    float fiber = 0.94f + 0.06f * Mathf.Sin((u * 24f - v * 8f) * Mathf.PI * 2f);
                    var c = RopeColor * (shade * fiber);
                    c.a = 1f;
                    px[y * S + x] = c;
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }
    }
}