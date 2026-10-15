using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HangingHammock
{
    /// ¬ерЄвка-цилиндр + узел из торов (петл€ через край гамака, кольцо и обмотка)
    internal sealed class RopeVisual
    {
        private static readonly Color RopeColor = new Color32(0x8C, 0x73, 0x50, 0xFF);

        private static Mesh cylinder, knotMesh;
        private static Material material;
        private static MaterialPropertyBlock mpb;
        private static readonly int MainTexST = Shader.PropertyToID("_MainTex_ST");
		private const float RopeStart = 0.9f;   // где начинаетс€ цилиндр верЄвки (в диаметрах верЄвки от угла гамака)

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

        /// side Ч направление длинной оси гамака (в мире), задаЄт плоскость петли узла
        public void Set(Vector3 top, Vector3 bottom, Vector3 side)
		{
			Vector3 d = top - bottom;
			float len = d.magnitude;
			if (len < 1e-4f) { SetVisible(false); return; }
			SetVisible(true);

			Vector3 dir = d / len;
			float w = Mathf.Max(0.005f, Tuning.RopeThickness);
			float ks = w * Mathf.Clamp(Tuning.KnotSize / (w * 1.6f), 1f, 1.5f);

			// верЄвка начинаетс€ от стыка узла, а не от угла гамака
			float off = Mathf.Min(RopeStart * ks, len * 0.5f);
			float rl = len - off;
			ropeGo.transform.SetPositionAndRotation(bottom + dir * off, Quaternion.FromToRotation(Vector3.up, dir));
			ropeGo.transform.localScale = new Vector3(w, rl, w);

			if (mpb == null) mpb = new MaterialPropertyBlock();
			ropeR.GetPropertyBlock(mpb);
			mpb.SetVector(MainTexST, new Vector4(1f, rl / (w * 3f), 0f, 0f));
			ropeR.SetPropertyBlock(mpb);

			Vector3 fwd = Vector3.ProjectOnPlane(side, dir);
			if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.ProjectOnPlane(Vector3.forward, dir);
			if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.ProjectOnPlane(Vector3.right, dir);

			knotGo.transform.SetPositionAndRotation(bottom, Quaternion.LookRotation(fwd.normalized, dir));
			knotGo.transform.localScale = Vector3.one * ks;
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

        // ????????? меши ?????????
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

        /// ”зел (ось Y Ч вдоль верЄвки вверх, 0 Ч угол гамака, единица = диаметр верЄвки):
		/// петл€-капл€ через угол гамака, кольцо-стык на еЄ вершине и три витка обмотки
		private static Mesh Knot()
		{
			if (knotMesh != null) return knotMesh;
			var v = new List<Vector3>(); var n = new List<Vector3>();
			var uv = new List<Vector2>(); var t = new List<int>();

			// петл€ под нат€жением: низ охватывает угол, верх ст€нут в стык
			AddTeardrop(v, n, uv, t, -0.5f, 0.95f, 0.36f, 0.17f, 32, 8);
			// кольцо-стык
			AddTorus(v, n, uv, t, Matrix4x4.TRS(new Vector3(0f, 1.0f, 0f), Quaternion.Euler(6f, 0f, 0f), Vector3.one),
					 0.42f, 0.27f, 16, 8);
			// обмотка
			for (int k = 0; k < 3; k++)
			{
				float tilt = (k % 2 == 0) ? 9f : -9f;
				AddTorus(v, n, uv, t,
						 Matrix4x4.TRS(new Vector3(0f, 1.38f + 0.3f * k, 0f), Quaternion.Euler(tilt, k * 40f, 0f), Vector3.one),
						 0.5f, 0.19f, 16, 6);
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

		/// “рубка вдоль капли в плоскости YZ: острие сверху (yTop), скругление снизу (yBottom)
		private static void AddTeardrop(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t,
										float yBottom, float yTop, float halfW, float r, int segU, int segV)
		{
			float yc = (yTop + yBottom) * 0.5f, hh = (yTop - yBottom) * 0.5f;
			float k = halfW / 0.7698f;   // максимум sin(a)Јsin(a/2) ? 0.7698 > ширина ровно halfW
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

        /// “ор в плоскости XZ (R Ч большой радиус, r Ч радиус трубки). UV: u Ч вокруг трубки, v Ч вдоль
        private static void AddTorus(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t,
                                     Matrix4x4 m, float R, float r, int segU, int segV)
        {
            int b = v.Count;
            float lenRepeat = Mathf.Max(1f, Mathf.Round(2f * Mathf.PI * R / 1.5f));   // целое Ч без шва
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

        private static Material GetMaterial(Material hammockMat)
        {
            if (material != null) return material;

            Material baseM = hammockMat != null && hammockMat.HasProperty("_MainTex") ? hammockMat : null;
            if (baseM == null) { var sh = Shader.Find("Standard"); if (sh != null) baseM = new Material(sh); }
            if (baseM == null) baseM = new Material(Shader.Find("Sprites/Default"));

            material = new Material(baseM) { name = "hammock_rope" };
            material.mainTexture = MakeTexture();
            material.mainTextureScale = Vector2.one;
            material.mainTextureOffset = Vector2.zero;
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
                    float s = Mathf.Repeat(u * 3f + v, 1f);                     // три пр€ди, виток по длине
                    float strand = Mathf.Pow(Mathf.Sin(s * Mathf.PI), 0.6f);
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