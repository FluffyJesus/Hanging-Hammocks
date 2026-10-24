using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HangingHammock
{
    /// Сборка верёвки + узлы на обоих концах + крюк на потолке
    internal sealed class RopeVisual
    {
        // Базовый цвет аутентичной ванильной пеньковой верёвки Sailwind
        private static readonly Color VanillaRopeColor = new Color(0.48f, 0.40f, 0.28f, 1f);
        private static readonly Color ProceduralRopeColor = new Color32(0x8C, 0x73, 0x50, 0xFF);

        private static Mesh cylinder, knotMesh;
        private static Material vanillaMaterial, detailedMaterial;
        private static Texture2D detailedTexture, vanillaTexture;
        private static MaterialPropertyBlock mpb;
        private static readonly int MainTexST = Shader.PropertyToID("_MainTex_ST");
        private const float RopeStart = 0.9f;
        // Толщина верёвки = толщина «верёвки», из которой сделан узел (диаметр трубки витка: 2 * 0.19 в единицах узла)
        private const float KnotRopeRatio = 0.38f;
        // Насколько «подтянут» узел: радиус витков умножается на это число (1 = как было, 0.8 = на 20% туже)
        private const float KnotTighten = 0.8f;
        // true, если верёвка использует тот же материал, что кольца свёртка (ванильный материал верёвки игры)
        private static bool vanillaShared;

        private readonly GameObject ropeGo, knotGo, topKnotGo, hookGo;
        private readonly MeshRenderer ropeR, knotR, topKnotR;
        private bool visible = true;
        private bool lastDetailedState;

        public RopeVisual(string name, Material hammockMat)
        {
            lastDetailedState = Plugin.DetailedRopeTexture.Value;
            var mat = GetMaterial(hammockMat, lastDetailedState);

            ropeGo = new GameObject(name);
            ropeGo.AddComponent<MeshFilter>().sharedMesh = Cylinder();
            ropeR = ropeGo.AddComponent<MeshRenderer>();
            ropeR.sharedMaterial = mat;
            ropeR.shadowCastingMode = ShadowCastingMode.On;

            knotGo = new GameObject(name + "_knot");
            knotGo.AddComponent<MeshFilter>().sharedMesh = Knot();
            knotR = knotGo.AddComponent<MeshRenderer>();
            knotR.sharedMaterial = mat;
            knotR.shadowCastingMode = ShadowCastingMode.On;

            topKnotGo = new GameObject(name + "_knot_top");
            topKnotGo.AddComponent<MeshFilter>().sharedMesh = Knot();
            topKnotR = topKnotGo.AddComponent<MeshRenderer>();
            topKnotR.sharedMaterial = mat;
            topKnotR.shadowCastingMode = ShadowCastingMode.On;

            hookGo = new GameObject(name + "_hook");
            hookGo.AddComponent<MeshFilter>().sharedMesh = HookModel.Get();
            var hr = hookGo.AddComponent<MeshRenderer>();
            hr.sharedMaterial = HookModel.GetMaterial(hammockMat);
            hr.shadowCastingMode = ShadowCastingMode.On;

            SetVisible(false);
        }

        private void CheckMaterialUpdate(Material hammockMat)
        {
            bool detailed = Plugin.DetailedRopeTexture.Value;
            if (detailed != lastDetailedState)
            {
                lastDetailedState = detailed;
                var mat = GetMaterial(hammockMat, detailed);
                if (ropeR != null) ropeR.sharedMaterial = mat;
                if (knotR != null) knotR.sharedMaterial = mat;
                if (topKnotR != null) topKnotR.sharedMaterial = mat;
            }
        }

        public void Set(Vector3 top, Vector3 topNormal, Vector3 bottom, Vector3 side, Vector3 hookAxis)
        {
            CheckMaterialUpdate(null);

            bool hooks = Plugin.ShowHooks.Value;
            float w = Mathf.Max(0.005f, Tuning.RopeThickness);
            float ks = w * Mathf.Clamp(Tuning.KnotSize / (w * 1.6f), 1f, 1.5f);

            Vector3 end = top;
            Vector3 hz = Vector3.forward, hx = Vector3.right;
            float slack = 0f;
            if (hooks)
            {
                Vector3 n = topNormal.sqrMagnitude > 1e-6f ? topNormal.normalized : Vector3.down;
                Vector3 hy = -n;
                // The hook turns so the rope lies in its plane and curls away from the pull (on a wall: its open end up)
                Vector3 curl = Vector3.ProjectOnPlane(top - bottom, n);
                if (curl.sqrMagnitude > 1e-6f) hz = Vector3.Cross(n, curl.normalized);
                else hz = Mathf.Abs(n.y) < 0.5f ? Vector3.Cross(n, Vector3.up) : Vector3.ProjectOnPlane(hookAxis, n);
                if (hz.sqrMagnitude < 1e-6f) hz = Vector3.ProjectOnPlane(Vector3.forward, n);
                if (hz.sqrMagnitude < 1e-6f) hz = Vector3.ProjectOnPlane(Vector3.right, n);
                hz.Normalize();
                Quaternion hq = Quaternion.LookRotation(hz, hy);
                hx = hq * Vector3.right;

                hookGo.transform.SetPositionAndRotation(top, hq);
                // The loop rests on the bend where the rope pulls it: from the bend's centre toward the hammock end
                Vector3 bend = top + n * HookModel.Neck + hx * HookModel.Reach;
                Vector3 pull = Vector3.ProjectOnPlane(bottom - bend, hz);
                end = bend + (pull.sqrMagnitude > 1e-6f ? pull.normalized : n) * HookModel.Reach;
                slack = Mathf.Max(0f, 0.33f * ks - HookModel.WireRadius);
            }

            Vector3 d = end - bottom;
            float len = d.magnitude;
            if (len < 1e-4f) { SetVisible(false); return; }
            SetVisible(true);
            if (hookGo.activeSelf != hooks) { hookGo.SetActive(hooks); topKnotGo.SetActive(hooks); }

            Vector3 dir = d / len;

            float offB = RopeStart * ks, offT = hooks ? offB : 0f;
            float total = offB + offT + slack;
            float cap = len * (hooks ? 0.8f : 0.5f);
            if (total > cap) { float f = cap / total; offB *= f; offT *= f; slack *= f; }
            float rl = len - offB - offT - slack;

            ropeGo.transform.SetPositionAndRotation(bottom + dir * offB, Quaternion.FromToRotation(Vector3.up, dir));
            float rw = Mathf.Max(0.004f, KnotRopeRatio * ks);   // толщина верёвки = толщина верёвки узла
            ropeGo.transform.localScale = new Vector3(rw, rl, rw);

            if (mpb == null) mpb = new MaterialPropertyBlock();
            ropeR.GetPropertyBlock(mpb);
            // Для детализированной текстуры используем тайлинг витков, для ванильной — 1:1
            float vTiling = lastDetailedState ? (rl / (rw * 3f)) : 1f;
            // у общего ванильного материала оставляем его собственный тайлинг
            if (!(vanillaShared && !lastDetailedState))
            {
                mpb.SetVector(MainTexST, new Vector4(1f, vTiling, 0f, 0f));
                ropeR.SetPropertyBlock(mpb);
            }

            Vector3 fwd = Vector3.ProjectOnPlane(side, dir);
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.ProjectOnPlane(Vector3.forward, dir);
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.ProjectOnPlane(Vector3.right, dir);
            knotGo.transform.SetPositionAndRotation(bottom, Quaternion.LookRotation(fwd.normalized, dir));
            knotGo.transform.localScale = Vector3.one * ks;

            if (hooks)
            {
                Vector3 tf = Vector3.ProjectOnPlane(hz, dir);
                if (tf.sqrMagnitude < 1e-6f) tf = Vector3.Cross(dir, hx);
                topKnotGo.transform.SetPositionAndRotation(end - dir * slack, Quaternion.LookRotation(tf.normalized, -dir));
                topKnotGo.transform.localScale = Vector3.one * ks;
            }
        }

        public void SetVisible(bool v)
        {
            if (visible == v) return;
            visible = v;
            if (ropeGo != null) ropeGo.SetActive(v);
            if (knotGo != null) knotGo.SetActive(v);
            if (!v)
            {
                if (topKnotGo != null) topKnotGo.SetActive(false);
                if (hookGo != null) hookGo.SetActive(false);
            }
        }

        public void Destroy()
        {
            if (ropeGo != null) Object.Destroy(ropeGo);
            if (knotGo != null) Object.Destroy(knotGo);
            if (topKnotGo != null) Object.Destroy(topKnotGo);
            if (hookGo != null) Object.Destroy(hookGo);
        }

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
            var v = new List<Vector3>(); var n = new List<Vector3>();
            var uv = new List<Vector2>(); var t = new List<int>();

            AddTeardrop(v, n, uv, t, -0.5f, 0.95f, 0.36f, 0.17f, 32, 8);
            AddTorus(v, n, uv, t, Matrix4x4.TRS(new Vector3(0f, 1.0f, 0f), Quaternion.Euler(6f, 0f, 0f), Vector3.one),
                     0.42f * KnotTighten, 0.27f, 16, 8);
            for (int k = 0; k < 3; k++)
            {
                float tilt = (k % 2 == 0) ? 9f : -9f;
                AddTorus(v, n, uv, t,
                         Matrix4x4.TRS(new Vector3(0f, 1.38f + 0.3f * k, 0f), Quaternion.Euler(tilt, k * 40f, 0f), Vector3.one),
                         0.5f * KnotTighten, 0.19f, 16, 6);
            }

            knotMesh = new Mesh { name = "hammock_rope_knot" };
            knotMesh.SetVertices(v);
            knotMesh.SetNormals(n);
            knotMesh.SetUVs(0, uv);
            knotMesh.SetTriangles(t, 0);
            knotMesh.RecalculateTangents();
            knotMesh.RecalculateBounds();
            return knotMesh;
        }

        private static void AddTeardrop(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t,
                                        float yBottom, float yTop, float halfW, float r, int segU, int segV)
        {
            float yc = (yTop + yBottom) * 0.5f, hh = (yTop - yBottom) * 0.5f;
            float k = halfW / 0.7698f;
            System.Func<float, Vector3> P = a =>
            {
                a = Mathf.Repeat(a, Mathf.PI * 2f);
                return new Vector3(0f, yc + hh * Mathf.Cos(a), k * Mathf.Sin(a) * Mathf.Sin(a * 0.5f));
            };

            float perim = 0f;
            for (int i = 0; i < segU; i++)
                perim += (P((i + 1) / (float)segU * Mathf.PI * 2f) - P(i / (float)segU * Mathf.PI * 2f)).magnitude;
            float lenRepeat = Mathf.Max(1f, Mathf.Round(perim / 1.5f));

            int b = v.Count;
            const float e = 1e-3f;
            for (int i = 0; i <= segU; i++)
            {
                float a = i / (float)segU * Mathf.PI * 2f;
                Vector3 p = P(a);
                Vector3 tan = P(a + e) - P(a - e);
                tan = tan.sqrMagnitude < 1e-12f ? Vector3.forward : tan.normalized;
                Vector3 nIn = Vector3.Cross(Vector3.right, tan).normalized;
                for (int j = 0; j <= segV; j++)
                {
                    float q = j / (float)segV * Mathf.PI * 2f;
                    Vector3 nn = nIn * Mathf.Cos(q) + Vector3.right * Mathf.Sin(q);
                    v.Add(p + nn * r);
                    n.Add(nn);
                    uv.Add(new Vector2(j / (float)segV, i / (float)segU * lenRepeat));
                }
            }
            int row = segV + 1;
            for (int i = 0; i < segU; i++)
                for (int j = 0; j < segV; j++)
                {
                    int a0 = b + i * row + j, a1 = a0 + row;
                    t.Add(a0); t.Add(a0 + 1); t.Add(a1);
                    t.Add(a1); t.Add(a0 + 1); t.Add(a1 + 1);
                }
        }

        private static void AddTorus(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t,
                                     Matrix4x4 m, float R, float r, int segU, int segV)
        {
            int b = v.Count;
            float lenRepeat = Mathf.Max(1f, Mathf.Round(2f * Mathf.PI * R / 1.5f));
            for (int i = 0; i <= segU; i++)
            {
                float a = i / (float)segU * Mathf.PI * 2f;
                Vector3 c = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                for (int j = 0; j <= segV; j++)
                {
                    float p = j / (float)segV * Mathf.PI * 2f;
                    Vector3 nn = c * Mathf.Cos(p) + Vector3.up * Mathf.Sin(p);
                    v.Add(m.MultiplyPoint3x4(c * R + nn * r));
                    n.Add(m.MultiplyVector(nn).normalized);
                    uv.Add(new Vector2(j / (float)segV, i / (float)segU * lenRepeat));
                }
            }
            int row = segV + 1;
            for (int i = 0; i < segU; i++)
                for (int j = 0; j < segV; j++)
                {
                    int a0 = b + i * row + j, a1 = a0 + row;
                    t.Add(a0); t.Add(a0 + 1); t.Add(a1);
                    t.Add(a1); t.Add(a0 + 1); t.Add(a1 + 1);
                }
        }

        private static Material CreateBaseMaterial(Material hammockMat)
        {
            Material baseM = hammockMat != null && hammockMat.HasProperty("_MainTex") ? hammockMat : null;
            if (baseM == null)
            {
                // Попытка найти оригинальный материал верёвки из LineRenderer игрового RopeEffect
                var ropeFx = Object.FindObjectOfType<RopeEffect>();
                if (ropeFx != null)
                {
                    var lr = ropeFx.GetComponent<LineRenderer>();
                    if (lr != null && lr.sharedMaterial != null) baseM = lr.sharedMaterial;
                }
            }
            if (baseM == null) { var sh = Shader.Find("Standard"); if (sh != null) baseM = new Material(sh); }
            if (baseM == null) baseM = new Material(Shader.Find("Sprites/Default"));

            return new Material(baseM);
        }

        private static Material GetMaterial(Material hammockMat, bool detailed)
        {
            if (detailed)
            {
                if (detailedMaterial != null) return detailedMaterial;
                detailedMaterial = CreateBaseMaterial(hammockMat);
                detailedMaterial.name = "hammock_rope_detailed";
                detailedMaterial.mainTexture = MakeDetailedTexture();
                detailedMaterial.mainTextureScale = Vector2.one;
                detailedMaterial.mainTextureOffset = Vector2.zero;
                if (detailedMaterial.HasProperty("_Color")) detailedMaterial.color = Color.white;
                if (detailedMaterial.HasProperty("_BumpMap")) detailedMaterial.SetTexture("_BumpMap", null);
                return detailedMaterial;
            }
            else
            {
                if (vanillaMaterial != null) return vanillaMaterial;

                // Тот же материал, что у колец свёртка (ванильная верёвка игры)
                var shared = HammockFactory.FindVanillaRopeMaterial();
                if (shared != null)
                {
                    vanillaMaterial = shared;
                    vanillaShared = true;
                    return vanillaMaterial;
                }

                vanillaMaterial = CreateBaseMaterial(hammockMat);
                vanillaMaterial.name = "hammock_rope_vanilla";
                vanillaMaterial.mainTexture = MakeVanillaTexture();
                vanillaMaterial.mainTextureScale = Vector2.one;
                vanillaMaterial.mainTextureOffset = Vector2.zero;
                if (vanillaMaterial.HasProperty("_Color")) vanillaMaterial.color = VanillaRopeColor;
                if (vanillaMaterial.HasProperty("_BumpMap")) vanillaMaterial.SetTexture("_BumpMap", null);
                if (vanillaMaterial.HasProperty("_Glossiness")) vanillaMaterial.SetFloat("_Glossiness", 0.05f);
                return vanillaMaterial;
            }
        }

        /// Ванильная чистая текстура с едва заметным приятным зерном (стиль Sailwind)
        private static Texture2D MakeVanillaTexture()
        {
            if (vanillaTexture != null) return vanillaTexture;
            const int S = 16;
            vanillaTexture = new Texture2D(S, S, TextureFormat.RGBA32, true)
            { name = "hammock_rope_vanilla_tex", wrapMode = TextureWrapMode.Repeat };
            var px = new Color[S * S];
            for (int i = 0; i < px.Length; i++)
            {
                float noise = Random.Range(0.97f, 1.03f);
                px[i] = new Color(noise, noise, noise, 1f);
            }
            vanillaTexture.SetPixels(px);
            vanillaTexture.Apply(true);
            return vanillaTexture;
        }

        /// Детализированная процедурная текстура с волокнами
        private static Texture2D MakeDetailedTexture()
        {
            if (detailedTexture != null) return detailedTexture;
            const int S = 64;
            detailedTexture = new Texture2D(S, S, TextureFormat.RGBA32, true)
            { name = "hammock_rope_detailed_tex", wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float u = x / (float)S, v = y / (float)S;
                    float s = Mathf.Repeat(u * 3f + v, 1f);
                    float strand = Mathf.Pow(Mathf.Sin(s * Mathf.PI), 0.6f);
                    float shade = Mathf.Lerp(0.45f, 1.05f, strand);
                    float fiber = 0.94f + 0.06f * Mathf.Sin((u * 24f - v * 8f) * Mathf.PI * 2f);
                    var c = ProceduralRopeColor * (shade * fiber);
                    c.a = 1f;
                    px[y * S + x] = c;
                }
            detailedTexture.SetPixels(px);
            detailedTexture.Apply(true);
            return detailedTexture;
        }
    }
}