using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HangingHammock
{
    internal static class HammockFactory
    {
        private const string BedName = "hammock";
        private const string SleepName = "sleep pos";
        private const string PreferredBoat = "dhow medium";

        // --- procedural bundle settings ---
        private const float BundleLength = 0.62f;   // along X
        private const float BundleRadius = 0.095f;  // thickness of the roll
        private const float EndScale     = 0.92f;   // ends are almost flat
        private const float TaperLen     = 0.05f;
        private const float RopeRadius   = 0.009f;
        private static readonly float[] RopeAt = { 0.2f, 0.5f, 0.8f };   // rope positions along the roll (0..1)

        // --- spiral roll ---
        private const float LayerPitch  = 0.0085f; // thickness of one wrap of cloth
        private const float GrooveDepth = 0.007f;  // depth of the gap between wraps on the end faces
        private const float LayerJitter = 0.006f;  // how far individual wraps are recessed relative to each other
        private const float CoreRadius  = 0.014f;  // center of the spiral stays smooth
        private const float SeamTwist   = 0.6f;    // radians; how much the outer seam spirals along the length
        private const int   CapRings    = 100;     // radial resolution of the end faces

        // --- UV / textures ---
        private const float CapTexRadius     = BundleRadius;   // world radius covered by the end-face texture

        // --- hammock cloth: new UV + pleats where the cloth gathers into the rope ---
        private const int   HammockFolds     = 8;       // pleats across the width at each end
        private const float HammockFoldZone  = 0.18f;   // share of the length (from each end) covered by the pleats
        private const float HammockFoldShade = 0.30f;   // depth of the pleat creases
        private const int   HammockBins      = 48;      // outline sampling along the long axis for the UV mapping

        // --- painted folds (baked into the side texture) ---
        private const float FoldShade     = 0.08f;   // strength of the broad lengthwise folds
        private const float TensionShade  = 0.12f;   // strength of the small creases fanning out from each rope
        private const int   TensionWaves  = 6;      // creases around the circumference (integer, otherwise the texture has a seam)
        private const float TensionSpread = 0.06f;   // how far (meters) the creases reach from a rope

        // --- dents under the ropes ---
        private const float DentDepth     = 0.005f;  // how deep the cloth is pressed in under a rope
        private const float DentWidth     = 0.02f;   // softness/width of the dent in meters (gaussian)
        private const float DentVariation = 0.2f;    // 0..1, uneven pull around the circumference

        private const int RadialSeg = 48, LengthSeg = 48, RopeRingSeg = 28, RopeTubeSeg = 6;

        // Cloth of the bundle: dark brown
        private static readonly Color ClothColor = new Color(0.36f, 0.26f, 0.16f);
        // Rope rings: light, like vanilla Sailwind ropes. To match the hammock ropes exactly,
        // put the value of ProceduralRopeColor from the rope code here.
        private static readonly Color RopeColor = new Color(0.72f, 0.58f, 0.38f);
        // false = clean vanilla-style grain on the rings, true = strands and fibers
        private const bool DetailedRope = false;
        private const float RopeUVTiling = 8f;   // how many times the rope texture repeats around one ring
        private const bool  RopeUVSwap   = false; // set to true if the vanilla rope texture runs the wrong way (strands across instead of along the ring)

        public static GameObject Prefab;
        public static int Index = -1;
        public static bool Failed;
        public static Vector3 BundleSize = new Vector3(0.6f, 0.3f, 0.6f);
        public static bool Ready => Prefab != null && Index >= 0;

        private static GameObject holder;
        private static bool warnedSource, warnedTemplate;
        private static Texture2D grainTex, strandTex, sideTex, capTex, hammockTex;
        private static bool loggedMat;

        public static void TryBuild()
        {
            var pd = PrefabsDirectory.instance;
            if (pd == null || pd.directory == null || pd.directory.Length == 0) return;

            if (!FindSource(out var boat, out var bedT, out var sleep))
            {
                if (!warnedSource) { warnedSource = true; Plugin.Log.LogWarning("Hammock source not found yet, retrying..."); }
                return;
            }
            var template = FindTemplate(pd.directory);
            if (template == null)
            {
                if (!warnedTemplate) { warnedTemplate = true; Plugin.Log.LogError("No template prefab found."); }
                return;
            }

            try
            {
                Build(template, boat, bedT, sleep, pd);
                Register(pd);
                Plugin.Log.LogInfo($"Hammock prefab ready: template '{template.name}', index {Index}");
            }
            catch (Exception e)
            {
                Failed = true;
                Plugin.Log.LogError("Hammock build failed: " + e);
                if (Prefab != null) Object.Destroy(Prefab);
                Prefab = null;
            }
        }

        // ========= source =========
        private static bool FindSource(out Transform boat, out Transform bedT, out Transform sleep)
        {
            boat = null; bedT = null; sleep = null;
            int best = -1;
            foreach (var g in Resources.FindObjectsOfTypeAll<GPButtonBed>())
            {
                if (g == null || !g.gameObject.scene.IsValid()) continue;
                var t = g.transform;
                if (!string.Equals(t.name, BedName, StringComparison.OrdinalIgnoreCase)) continue;
                if (t.parent == null) continue;
                var mf = t.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                var sp = t.Find(SleepName);
                int score = (Diagnostics.Path(t.parent).ToLowerInvariant().Contains(PreferredBoat) ? 2 : 0) + (sp != null ? 1 : 0);
                if (score > best) { best = score; boat = t.parent; bedT = t; sleep = sp; }
            }
            return boat != null;
        }

        private static GameObject FindTemplate(GameObject[] dir)
        {
            GameObject bed = null, bigPlain = null, plain = null;
            foreach (var g in dir)
            {
                if (g == null || g == Prefab) continue;
                var si = g.GetComponent<ShipItem>();
                if (si == null || g.GetComponent<SaveablePrefab>() == null) continue;
                var tp = si.GetType();
                if (tp == typeof(ShipItemBed)) { if (bed == null) bed = g; }
                else if (tp == typeof(ShipItem))
                {
                    if (si.big && bigPlain == null) bigPlain = g;
                    if (plain == null) plain = g;
                }
            }
            if (bed != null) return bed;
            if (bigPlain != null) return bigPlain;
            return plain;
        }

        // ========= build =========
        private static void Build(GameObject template, Transform boat, Transform bedT, Transform srcSleep, PrefabsDirectory pd)
        {
            holder = new GameObject("HangingHammock_PrefabHolder");
            holder.SetActive(false);
            Object.DontDestroyOnLoad(holder);

            var go = Object.Instantiate(template, holder.transform);
            go.name = "hanging hammock";
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            for (int i = go.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(go.transform.GetChild(i).gameObject);

            var mf = go.GetComponent<MeshFilter>();   if (mf == null) mf = go.AddComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>(); if (mr == null) mr = go.AddComponent<MeshRenderer>();
            var box = go.AddComponent<BoxCollider>();
            var oldItem = go.GetComponent<ShipItem>();
            var keep = new HashSet<Component> { mf, mr, box, oldItem, go.GetComponent<SaveablePrefab>() };
            var rb = go.GetComponent<Rigidbody>(); if (rb != null) keep.Add(rb);
            Strip(go, keep);

            ShipItemBed bed;
            if (oldItem.GetType() == typeof(ShipItemBed)) bed = (ShipItemBed)oldItem;
            else
            {
                bed = go.AddComponent<ShipItemBed>();
                CopyItemFields(oldItem, bed);
                Retarget(go, oldItem, bed);
                Object.DestroyImmediate(oldItem);
            }

            // bundle goes first: the hammock is oriented along the bundle axis
            var bunR = BuildBundle(go, bedT.GetComponent<MeshRenderer>(), out Bounds bunB);

            // hammock geometry in boat space
            Matrix4x4 T = Matrix4x4.identity;
            Matrix4x4 M = boat.worldToLocalMatrix * bedT.localToWorldMatrix;
            var pts = new List<Vector3>();
            var mesh = ModelTweaks.Process(bedT.GetComponent<MeshFilter>().sharedMesh, M, pts, out bool readable);
            Bounds bb = ComputeBounds(pts, readable, mesh, M);

            // Long axis of the hammock = long axis of the bundle, so the hanging hammock
            // lies along the bundle collider and not across it
            bool turned = false;
            if (bunR.Count > 0)
            {
                bool hamX = bb.extents.x >= bb.extents.z;
                bool bunX = bunB.extents.x >= bunB.extents.z;
                if (hamX != bunX)
                {
                    Quaternion q = Quaternion.Euler(0f, 90f, 0f);
                    T = Matrix4x4.Rotate(q);
                    M = T * M;
                    for (int i = 0; i < pts.Count; i++) pts[i] = q * pts[i];
                    bb = ComputeBounds(pts, readable, mesh, M);
                    turned = true;
                }
            }
            Plugin.Log.LogInfo($"Hammock size {bb.size}, bundle size {bunB.size}, turned to bundle axis: {turned}");

            Vector3 O = bb.center;
            Vector3 axis = bb.extents.x >= bb.extents.z ? Vector3.right : Vector3.forward;

            // new UV for the hammock cloth (only possible when the mesh is readable)
            if (readable)
                RemapHammockUV(mesh, M, O, axis, axis == Vector3.right ? bb.extents.x : bb.extents.z);

            bool hasEnds = false; Vector3 endA = Vector3.zero, endB = Vector3.zero;
            if (readable && pts.Count > 0)
            {
                endA = FindTip(pts, axis, -1f) - O;
                endB = FindTip(pts, axis, 1f) - O;
                hasEnds = true;
            }

            Transform MakeChild(string name, Matrix4x4 m, bool keepScale)
            {
                var c = new GameObject(name).transform;
                c.SetParent(go.transform, false);
                c.localPosition = (Vector3)m.GetColumn(3) - O;
                c.localRotation = m.rotation;
                c.localScale = keepScale ? m.lossyScale : Vector3.one;
                c.gameObject.layer = go.layer;
                return c;
            }

            Matrix4x4 sleepM = srcSleep != null
                ? T * boat.worldToLocalMatrix * srcSleep.localToWorldMatrix
                : Matrix4x4.TRS(bb.center + Vector3.up * 0.5f, Quaternion.LookRotation(axis, Vector3.up), Vector3.one);

            var sleepT = MakeChild("sleep pos", sleepM, false);   // index 0: Sleep uses GetChild(0)
            var wakeT  = MakeChild("wake pos", sleepM, false);    // index 1
            // bundle_visual was created earlier: move it after sleep/wake so GetChild(0) stays "sleep pos"
            var bunRoot = go.transform.Find("bundle_visual");
            if (bunRoot != null) bunRoot.SetAsLastSibling();

            var v = MakeChild("hammock_visual", M, true);
            v.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var vr = v.gameObject.AddComponent<MeshRenderer>();
            var srcR = bedT.GetComponent<MeshRenderer>();
            if (srcR != null)
            {
                var sm = srcR.sharedMaterials;
                if (!readable) sm = ModelTweaks.TryCullOff(sm);
                else sm = HammockClothMaterials(sm, mesh.subMeshCount);
                vr.sharedMaterials = sm;
                vr.shadowCastingMode = srcR.shadowCastingMode;
                vr.receiveShadows = srcR.receiveShadows;
            }

            // root
            mf.sharedMesh = new Mesh { name = "hanging_hammock_proxy" };
            if (srcR != null) mr.sharedMaterials = srcR.sharedMaterials;
            Vector3 minSize = new Vector3(0.1f, 0.1f, 0.1f);
            Vector3 hamSize = Vector3.Max(bb.size, minSize);
            Vector3 bunSize = bunR.Count > 0 ? Vector3.Max(bunB.size, minSize) : hamSize;
            box.center = Vector3.zero;
            box.size = bunSize;
            BundleSize = bunSize;

            R.Set(bed, "name", Tuning.ItemName);
            R.Set(bed, "value", Tuning.Price);
            R.Set(bed, "mass", Tuning.Mass);
            R.Set(bed, "category", "furniture");
            bed.big = true;
            bed.holdDistance = Tuning.BundleHold;
            bed.sold = false;
            bed.nailed = false;
            bed.wallAttachment = false;
            bed.allowPlacingItems = false;
            bed.sleepPos = sleepT;
            bed.wakePos = wakeT;

            var hh = go.AddComponent<HammockHanger>();
            hh.bCenter = Vector3.zero;
            hh.bExt = bb.extents;
            hh.swingParts = new[] { sleepT, wakeT, v };
            hh.hammockRenderers = new Renderer[] { vr };
            hh.bundleRenderers = bunR.ToArray();
            hh.hasEnds = hasEnds;
            hh.endA = endA;
            hh.endB = endB;
            hh.hammockBoxSize = hamSize;
            hh.bundleBoxSize = bunSize;

            Prefab = go;
        }

        private static Bounds ComputeBounds(List<Vector3> pts, bool readable, Mesh mesh, Matrix4x4 M)
        {
            if (readable && pts.Count > 0)
            {
                var b = new Bounds(pts[0], Vector3.zero);
                foreach (var q in pts) b.Encapsulate(q);
                return b;
            }
            return TransformBounds(mesh.bounds, M);
        }

        private static Bounds TransformBounds(Bounds b, Matrix4x4 M)
        {
            Bounds r = default; bool first = true;
            for (int k = 0; k < 8; k++)
            {
                var c = b.center + Vector3.Scale(b.extents,
                    new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                var w = M.MultiplyPoint3x4(c);
                if (first) { r = new Bounds(w, Vector3.zero); first = false; } else r.Encapsulate(w);
            }
            return r;
        }

        private static Vector3 FindTip(List<Vector3> pts, Vector3 axis, float sign)
        {
            float best = float.MinValue;
            foreach (var q in pts) best = Mathf.Max(best, sign * Vector3.Dot(q, axis));
            Vector3 sum = Vector3.zero; int c = 0;
            foreach (var q in pts)
                if (sign * Vector3.Dot(q, axis) >= best - 0.03f) { sum += q; c++; }
            return c > 0 ? sum / c : Vector3.zero;
        }

        // ========= hammock cloth =========
        // Same color as the bundle cloth, with our own texture instead of the Dhow atlas.
        private static Material[] HammockClothMaterials(Material[] src, int subMeshes)
        {
            var tex = MakeHammockTexture();
            int n = Mathf.Max(1, subMeshes);
            var res = new Material[n];
            var names = new List<string>();
            for (int i = 0; i < n; i++)
            {
                Material b = (src != null && src.Length > 0) ? src[Mathf.Min(i, src.Length - 1)] : null;
                res[i] = MakeMat(b, ClothColor, tex);
                names.Add(b != null ? b.name : "null");
            }
            Plugin.Log.LogInfo("Hammock cloth: " + n + " submesh(es), source materials: " + string.Join(", ", names.ToArray()));
            return res;
        }

        // New UVs for the hammock mesh: u = position along the long axis (0..1),
        // v = position across, normalized to the LOCAL width (0 and 1 are the two edges).
        // Pleats painted at constant v therefore follow the outline and converge where the cloth narrows to the rope.
        private static void RemapHammockUV(Mesh mesh, Matrix4x4 M, Vector3 O, Vector3 axis, float halfLen)
        {
            Vector3 across = axis == Vector3.right ? Vector3.forward : Vector3.right;
            var verts = mesh.vertices;
            int n = verts.Length, N = HammockBins;
            halfLen = Mathf.Max(1e-3f, halfLen);

            var s01 = new float[n];
            var cc  = new float[n];
            var lo  = new float[N];
            var hi  = new float[N];
            for (int i = 0; i < N; i++) { lo[i] = float.MaxValue; hi[i] = float.MinValue; }

            for (int i = 0; i < n; i++)
            {
                Vector3 pw = M.MultiplyPoint3x4(verts[i]) - O;
                s01[i] = Mathf.Clamp01((Vector3.Dot(pw, axis) + halfLen) / (2f * halfLen));
                cc[i] = Vector3.Dot(pw, across);
                int b = Mathf.Min(N - 1, (int)(s01[i] * N));
                if (cc[i] < lo[b]) lo[b] = cc[i];
                if (cc[i] > hi[b]) hi[b] = cc[i];
            }

            // the vanilla mesh is low-poly: some bins may be empty, fill them from the nearest filled ones
            var ok = new bool[N];
            for (int i = 0; i < N; i++) ok[i] = lo[i] <= hi[i];
            for (int i = 0; i < N; i++)
            {
                if (ok[i]) continue;
                int l = i - 1; while (l >= 0 && !ok[l]) l--;
                int r = i + 1; while (r < N && !ok[r]) r++;
                if (l < 0 && r >= N) { lo[i] = -0.5f; hi[i] = 0.5f; }
                else if (l < 0)      { lo[i] = lo[r]; hi[i] = hi[r]; }
                else if (r >= N)     { lo[i] = lo[l]; hi[i] = hi[l]; }
                else
                {
                    float tt = (i - l) / (float)(r - l);
                    lo[i] = Mathf.Lerp(lo[l], lo[r], tt);
                    hi[i] = Mathf.Lerp(hi[l], hi[r], tt);
                }
            }

            var uv = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float f = s01[i] * N - 0.5f;
                int i0 = Mathf.Clamp(Mathf.FloorToInt(f), 0, N - 1);
                int i1 = Mathf.Min(i0 + 1, N - 1);
                float tt = Mathf.Clamp01(f - i0);
                float a = Mathf.Lerp(lo[i0], lo[i1], tt), b = Mathf.Lerp(hi[i0], hi[i1], tt);
                float mid = (a + b) * 0.5f, half = Mathf.Max(1e-3f, (b - a) * 0.5f);
                float cn = Mathf.Clamp((cc[i] - mid) / half, -1f, 1f);
                uv[i] = new Vector2(s01[i], cn * 0.5f + 0.5f);
            }
            mesh.uv = uv;
        }

        // Hammock texture: u = along the hammock, v = across (edge to edge). Not tiled.
        // Near both ends HammockFolds pleats gather toward the rope; the rest is almost flat.
        private static Texture2D MakeHammockTexture()
        {
            if (hammockTex != null) return hammockTex;
            const int W = 512, H = 256;
            hammockTex = new Texture2D(W, H, TextureFormat.RGBA32, true)
            { name = "hammock_cloth_tex", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
            {
                float c = (y + 0.5f) / H;                  // 0..1 across, edge to edge
                float edge = Mathf.Abs(c * 2f - 1f);       // 0 in the middle, 1 at the edges
                for (int x = 0; x < W; x++)
                {
                    float s = (x + 0.5f) / W;                              // 0..1 along
                    float dEnd = Mathf.Min(s, 1f - s) / HammockFoldZone;   // 0 at the end, 1 at the border of the zone
                    float env = 1f - Sm(0f, 1f, dEnd);                     // 1 at the end, 0 away from it

                    // creases sit between the edges (at half-integers of p), slightly irregular
                    float p = c * HammockFolds + 0.12f * Mathf.Sin(dEnd * 9f + c * 11f) + 0.10f * Mathf.Sin(c * 23f + s * 5f);
                    float crease = 0.5f - 0.5f * Mathf.Cos(p * Mathf.PI * 2f);
                    crease *= crease;                                      // narrower dark lines
                    float ridge = 1f - crease;

                    float val = 0.97f - HammockFoldShade * env * crease + 0.03f * env * ridge;
                    val *= 1f - 0.20f * env * env * env;                   // darker where the cloth is gathered into the rope
                    val *= 1f - 0.10f * Sm(0.85f, 1f, edge);               // slightly darker along the outline
                    val *= 1f + 0.03f * Mathf.Sin(c * 7f + 2.5f * Mathf.Sin(s * 4f));   // very soft broad folds
                    val = Mathf.Clamp01(val);
                    px[y * W + x] = new Color(val, val, val, 1f);
                }
            }
            hammockTex.SetPixels(px);
            hammockTex.Apply(true);
            return hammockTex;
        }

        // ========= procedural bundle =========
        private static List<Renderer> BuildBundle(GameObject go, MeshRenderer hammockSrc, out Bounds bounds)
        {
            var res = new List<Renderer>();

            Material baseMat = null;
            if (hammockSrc != null && hammockSrc.sharedMaterials.Length > 0)
                baseMat = hammockSrc.sharedMaterials[0];

            var root = new GameObject("bundle_visual").transform;
            root.SetParent(go.transform, false);
            root.gameObject.layer = go.layer;

            Mesh cloth = BuildClothMesh();
            Mesh rope  = BuildRopeMesh();

            // cloth has two submeshes: [0] side + seam (baked relief shading), [1] end faces (spiral shading)
            res.Add(AddPart(root, "bundle_cloth", cloth, go.layer,
                MakeMat(baseMat, ClothColor, MakeSideTexture()),
                MakeMat(baseMat, ClothColor, MakeCapTexture())));
            // Rings use the vanilla rope material (the one of the cloth rope prefab); our own color is only a fallback
            Material ropeMat = FindVanillaRopeMaterial();
            if (ropeMat != null)
            {
                var mt = ropeMat.mainTexture;
                Plugin.Log.LogInfo("Bundle rope uses vanilla material '" + ropeMat.name + "', shader '" + ropeMat.shader.name +
                                   "', main texture: " + (mt != null ? mt.name + " " + mt.width + "x" + mt.height : "none"));
            }
            else
            {
                Plugin.Log.LogWarning("Vanilla rope material not found, rings use RopeColor");
                ropeMat = MakeMat(baseMat, RopeColor, DetailedRope ? MakeStrandTexture() : MakeGrainTexture());
            }
            res.Add(AddPart(root, "bundle_rope", rope, go.layer, ropeMat));

            bounds = cloth.bounds;
            bounds.Encapsulate(rope.bounds);
            return res;
        }

        // Material of the vanilla rope: first the skinned cloth rope prefab, then the LineRenderer of any RopeEffect in the scene.
        // The prefab field is read through reflection, so it compiles even if the field is not public.
        internal static Material FindVanillaRopeMaterial()
        {
            try
            {
                var refs = RefsDirectory.instance;
                if (refs != null)
                {
                    var f = typeof(RefsDirectory).GetField("clothRopePrefab",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var prefab = f != null ? f.GetValue(refs) as GameObject : null;
                    if (prefab != null)
                    {
                        var cr = prefab.GetComponent<ClothRope>();
                        SkinnedMeshRenderer smr = cr != null ? cr.skinned : null;
                        if (smr == null) smr = prefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
                        if (smr != null && smr.sharedMaterial != null) return smr.sharedMaterial;
                    }
                }
                foreach (var re in Resources.FindObjectsOfTypeAll<RopeEffect>())
                {
                    if (re == null) continue;
                    var lr = re.GetComponent<LineRenderer>();
                    if (lr != null && lr.sharedMaterial != null) return lr.sharedMaterial;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Vanilla rope material lookup failed: " + e.Message);
            }
            return null;
        }

        private static Renderer AddPart(Transform root, string name, Mesh mesh, int layer, params Material[] mats)
        {
            var t = new GameObject(name).transform;
            t.SetParent(root, false);
            t.gameObject.layer = layer;
            t.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = t.gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterials = mats;
            return r;
        }

        private static Material MakeMat(Material baseMat, Color color, Texture2D tex)
        {
            Material m;
            if (baseMat != null) m = new Material(baseMat);
            else
            {
                var sh = Shader.Find("Standard");
                if (sh == null) sh = Shader.Find("Diffuse");
                m = new Material(sh);
            }
            m.name = (baseMat != null ? baseMat.name : "bundle") + "_hammock_bundle";

            string prop = m.HasProperty("_Color") ? "_Color"
                        : m.HasProperty("_BaseColor") ? "_BaseColor"
                        : m.HasProperty("_TintColor") ? "_TintColor" : null;
            if (prop != null) m.SetColor(prop, color);

            if (m.HasProperty("_MainTex"))
            {
                m.SetTexture("_MainTex", tex);
                m.mainTextureScale = Vector2.one;
                m.mainTextureOffset = Vector2.zero;
            }
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);

            // The base material comes from the Dhow atlas: its normal / detail / mask maps would be
            // stretched over our UVs (looks like wood grain), so every slot except the main one is cleared.
            var cleared = new List<string>();
            foreach (var tn in m.GetTexturePropertyNames())
            {
                if (tn == "_MainTex" || tn == "_BaseMap") continue;
                if (m.GetTexture(tn) == null) continue;
                cleared.Add(tn);
                m.SetTexture(tn, null);
            }
            if (!loggedMat && baseMat != null)
            {
                loggedMat = true;
                Plugin.Log.LogInfo("Bundle material base '" + baseMat.name + "', shader '" + baseMat.shader.name +
                                   "', cleared textures: " + (cleared.Count > 0 ? string.Join(", ", cleared.ToArray()) : "none"));
            }
            return m;
        }

        // Clean white texture with barely visible grain (Sailwind style); the color comes from the material
        private static Texture2D MakeGrainTexture()
        {
            if (grainTex != null) return grainTex;
            const int S = 16;
            grainTex = new Texture2D(S, S, TextureFormat.RGBA32, true)
            { name = "hammock_bundle_grain_tex", wrapMode = TextureWrapMode.Repeat };
            var px = new Color[S * S];
            for (int i = 0; i < px.Length; i++)
            {
                float noise = UnityEngine.Random.Range(0.97f, 1.03f);
                px[i] = new Color(noise, noise, noise, 1f);
            }
            grainTex.SetPixels(px);
            grainTex.Apply(true);
            return grainTex;
        }

        // Rope texture with strands and fibers (white-based; the color comes from the material)
        private static Texture2D MakeStrandTexture()
        {
            if (strandTex != null) return strandTex;
            const int S = 64;
            strandTex = new Texture2D(S, S, TextureFormat.RGBA32, true)
            { name = "hammock_bundle_strand_tex", wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float u = x / (float)S, v = y / (float)S;
                    float sc = Mathf.Repeat(u * 3f + v, 1f);
                    float strand = Mathf.Pow(Mathf.Sin(sc * Mathf.PI), 0.6f);
                    float shade = Mathf.Lerp(0.45f, 1.05f, strand);
                    float fiber = 0.94f + 0.06f * Mathf.Sin((u * 24f - v * 8f) * Mathf.PI * 2f);
                    float k = Mathf.Clamp01(shade * fiber);
                    px[y * S + x] = new Color(k, k, k, 1f);
                }
            strandTex.SetPixels(px);
            strandTex.Apply(true);
            return strandTex;
        }

        // Side texture, not tiled: u = position along the roll, v = angle around it.
        // It only emphasizes the relief (no pattern): darker around the rope dents, soft folds, shaded corner at the seam.
        private static Texture2D MakeSideTexture()
        {
            if (sideTex != null) return sideTex;
            const int W = 512, H = 256;
            sideTex = new Texture2D(W, H, TextureFormat.RGBA32, true)
            { name = "hammock_bundle_side_tex", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            const float TwoPi = Mathf.PI * 2f;
            const float SeamSpan = 0.25f;   // radians before the seam step that get shaded
            var px = new Color[W * H];
            for (int y = 0; y < H; y++)
            {
                float a = (y + 0.5f) / H * TwoPi;
                for (int x = 0; x < W; x++)
                {
                    float t = (x + 0.5f) / W;

                    float dentAO  = Mathf.Clamp01(RopeDent(a, t) / DentDepth);          // 0 away from ropes, ~1 under them
                    float fold    = (Wrinkle(a, t) - 1f) / 0.022f;                      // about -1..1, valleys are negative
                    float seamOcc = 1f - Sm(0f, SeamSpan, TwoPi - a);                   // 1 right at the step, 0 away from it

                    float tension = TensionWrinkles(a, t);                                    // about -1..1
                    float val = Mathf.Clamp01((1f + FoldShade * fold + TensionShade * tension) * (1f - 0.30f * dentAO) * (1f - 0.25f * seamOcc));
                    px[y * W + x] = new Color(val, val, val, 1f);
                }
            }
            sideTex.SetPixels(px);
            sideTex.Apply(true);
            return sideTex;
        }

        // End-face texture: darkens the gaps between wraps (same spiral as the geometry in CapOffset)
        // and gives every wrap a slightly different shade.
        private static Texture2D MakeCapTexture()
        {
            if (capTex != null) return capTex;
            const int S = 256;
            capTex = new Texture2D(S, S, TextureFormat.RGBA32, true)
            { name = "hammock_bundle_cap_tex", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
            float rEnd = Profile(0f);
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float nx = (x + 0.5f) / S * 2f - 1f;
                    float ny = (y + 0.5f) / S * 2f - 1f;
                    float r = Mathf.Sqrt(nx * nx + ny * ny) * CapTexRadius;
                    float a = Mathf.Atan2(ny, nx);
                    if (a < 0f) a += Mathf.PI * 2f;

                    float q = Mathf.Max(0f, (rEnd - r) / LayerPitch - a / (Mathf.PI * 2f));   // same spiral coordinate as CapOffset
                    int k = Mathf.FloorToInt(q);
                    float sp = q - k;
                    float edge   = Mathf.Min(sp, 1f - sp);
                    float crease = 1f - Sm(0f, 0.30f, edge);        // dark in the gap between wraps
                    float tone   = 0.90f + 0.10f * Hash01(k);       // each wrap a slightly different shade

                    float val = Mathf.Clamp01(tone * (1f - 0.5f * crease));
                    px[y * S + x] = new Color(val, val, val, 1f);
                }
            capTex.SetPixels(px);
            capTex.Apply(true);
            return capTex;
        }

        // Radius profile along the roll: t in [0..1]
        private static float Profile(float t)
        {
            float e = Mathf.Min(t, 1f - t) / TaperLen;
            float s = e >= 1f ? 1f : Mathf.SmoothStep(EndScale, 1f, e);
            return BundleRadius * s;
        }

        private static float Sm(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }

        private static float Hash01(int k)
        {
            float s = Mathf.Sin(k * 12.9898f + 4.1f) * 43758.5453f;
            return s - Mathf.Floor(s);
        }

        // Outer radius of the roll at spiral angle a (0..2PI) and position t along the length.
        // Radius shrinks by one LayerPitch per turn; at a=2PI it jumps back up (the seam step).
        private static float OuterR(float a, float t)
        {
            float wrinkle = Wrinkle(a, t);
            return (Profile(t) - LayerPitch * a / (Mathf.PI * 2f) - RopeDent(a, t)) * wrinkle;
        }

        private static float Wrinkle(float a, float t)
        {
            // Mostly lengthwise creases (a is multiplied by integers only, so the seam stays consistent);
            // t only bends them slowly, no crossing diagonals.
            return 1f + 0.014f * Mathf.Sin(a * 3f + 1.6f * Mathf.Sin(t * 2.1f + 0.7f))
                      + 0.008f * Mathf.Sin(a * 5f - 2.0f * Mathf.Sin(t * 1.7f + 2.0f) + 1.1f);
        }

        // Small creases fanning out along the roll from each rope: strongest at the rope, fading away from it.
        // Only integer multiples of a are used, so the texture closes at a=0 / a=2PI.
        private static float TensionWrinkles(float a, float t)
        {
            float sum = 0f;
            for (int k = 0; k < RopeAt.Length; k++)
            {
                float d = (t - RopeAt[k]) * BundleLength;
                float env = Mathf.Exp(-(d * d) / (TensionSpread * TensionSpread));
                if (env < 0.01f) continue;
                float phase = a * TensionWaves + 1.4f * Mathf.Sin(a * 2f + k * 1.7f) + 18f * d * Mathf.Sin(a * 3f + k);
                sum += env * Mathf.Sin(phase);
            }
            return Mathf.Clamp(sum, -1f, 1f);
        }

        // How far the cloth is pressed in at angle a / position t because of the rope rings.
        // Only integer multiples of a are used so the seam (a=0 vs a=2PI) stays consistent.
        private static float RopeDent(float a, float t)
        {
            float sum = 0f;
            for (int k = 0; k < RopeAt.Length; k++)
            {
                float x = (t - RopeAt[k]) * BundleLength / DentWidth;
                if (x * x > 16f) continue;
                float depth = DentDepth * (1f + DentVariation * Mathf.Sin(a * 3f + k * 2.1f));
                sum += depth * Mathf.Exp(-x * x);
            }
            return sum;
        }

        // How far the end face is recessed at radius r / angle a (spiral grooves + uneven wraps)
        private static float CapOffset(float r, float a, float rEnd, float rim)
        {
            float q = Mathf.Max(0f, (rEnd - r) / LayerPitch - a / (Mathf.PI * 2f));   // spiral coordinate
            int k = Mathf.FloorToInt(q);
            float s = q - k;

            float edge   = Mathf.Min(s, 1f - s);                   // 0 exactly at the gap between wraps
            float groove = 1f - Sm(0f, 0.22f, edge);
            float jit    = Mathf.Lerp(Hash01(k), Hash01(k + 1), Sm(0f, 1f, s)) * LayerJitter;

            float fadeRim  = Sm(0f, LayerPitch * 0.6f, rim - r);   // rim must stay flush with the side surface
            float fadeCore = Sm(CoreRadius * 0.4f, CoreRadius * 1.5f, r);
            return (GrooveDepth * groove + jit) * fadeRim * fadeCore;
        }

        private static Mesh BuildClothMesh()
        {
            const float TwoPi = Mathf.PI * 2f;
            int seg = RadialSeg, rows = LengthSeg, stride = seg + 1;
            var verts = new List<Vector3>();
            var uvs   = new List<Vector2>();
            var sideTris = new List<int>();   // submesh 0: side + seam (relief texture)
            var capTris  = new List<int>();   // submesh 1: end faces (spiral texture)

            Vector3 At(float px, float pr, float pa, float pt)
            {
                float th = pa + SeamTwist * (pt - 0.5f);
                return new Vector3(px, pr * Mathf.Cos(th), pr * Mathf.Sin(th));
            }
            void Quad(List<int> tl, int qa, int qb, int qc, int qd, bool flip)
            {
                if (!flip) { tl.Add(qa); tl.Add(qb); tl.Add(qc); tl.Add(qb); tl.Add(qd); tl.Add(qc); }
                else       { tl.Add(qa); tl.Add(qc); tl.Add(qb); tl.Add(qb); tl.Add(qc); tl.Add(qd); }
            }

            // 1. side surface: u = along the roll (0..1), v = around it (0..1), one texture without tiling
            for (int i = 0; i <= rows; i++)
            {
                float t = i / (float)rows, x = (t - 0.5f) * BundleLength;
                for (int j = 0; j <= seg; j++)
                {
                    float a = j / (float)seg * TwoPi;
                    verts.Add(At(x, OuterR(a, t), a, t));
                    uvs.Add(new Vector2(t, j / (float)seg));
                }
            }
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < seg; j++)
                {
                    int s0 = i * stride + j;
                    Quad(sideTris, s0, s0 + 1, s0 + stride, s0 + stride + 1, false);
                }

            // 2. seam wall (the step where the outer edge of the cloth ends); own vertices = hard edge
            int wb = verts.Count;
            for (int i = 0; i <= rows; i++)
            {
                float t = i / (float)rows, x = (t - 0.5f) * BundleLength;
                verts.Add(At(x, OuterR(TwoPi, t), TwoPi, t));   // lower side
                verts.Add(At(x, OuterR(0f, t), 0f, t));         // upper side
                uvs.Add(new Vector2(t, 0.998f));   // same v on both sides: the wall samples the shaded corner, no stretching
                uvs.Add(new Vector2(t, 0.998f));
            }
            for (int i = 0; i < rows; i++)
            {
                int I0 = wb + i * 2, O0 = I0 + 1, I1 = I0 + 2, O1 = I0 + 3;
                sideTris.Add(I0); sideTris.Add(O0); sideTris.Add(I1);
                sideTris.Add(O0); sideTris.Add(O1); sideTris.Add(I1);
            }

            // 3. end faces with spiral: planar UV from (r, a), matches MakeCapTexture
            for (int end = 0; end < 2; end++)
            {
                float sign = end == 0 ? -1f : 1f;
                float te = end;
                float rEnd = Profile(te);
                int cb = verts.Count;

                for (int m = 0; m <= CapRings; m++)
                {
                    float rho = m / (float)CapRings;
                    for (int j = 0; j <= seg; j++)
                    {
                        float a = j / (float)seg * TwoPi;
                        float rim = OuterR(a, te);
                        float r = rho * rim;
                        float off = CapOffset(r, a, rEnd, rim);
                        verts.Add(At(sign * (BundleLength * 0.5f - off), r, a, te));
                        uvs.Add(new Vector2(0.5f + r * Mathf.Cos(a) / (2f * CapTexRadius),
                                            0.5f + r * Mathf.Sin(a) / (2f * CapTexRadius)));
                    }
                }
                for (int m = 0; m < CapRings; m++)
                    for (int j = 0; j <= seg; j++)
                    {
                        int a0 = cb + m * stride + j;
                        int b0 = j < seg ? a0 + 1 : cb + m * stride;   // last column wraps to the first
                        Quad(capTris, a0, b0, a0 + stride, b0 + stride, end == 1);
                    }
            }

            var mesh = new Mesh { name = "hammock_bundle_cloth" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(sideTris, 0);
            mesh.SetTriangles(capTris, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildRopeMesh()
        {
            var verts = new List<Vector3>();
            var uvs   = new List<Vector2>();
            var tris  = new List<int>();

            foreach (float t in RopeAt)
            {
                float x  = (t - 0.5f) * BundleLength;
                float Rr = Profile(t) - LayerPitch * 0.5f + RopeRadius * 0.4f - DentDepth;   // rope sinks into the cloth, accounts for the seam step
                int baseIdx = verts.Count;

                for (int u = 0; u <= RopeRingSeg; u++)
                {
                    float au = u / (float)RopeRingSeg * Mathf.PI * 2f;
                    var radial = new Vector3(0f, Mathf.Cos(au), Mathf.Sin(au));
                    var center = new Vector3(x, 0f, 0f) + radial * Rr;
                    for (int v = 0; v <= RopeTubeSeg; v++)
                    {
                        float av = v / (float)RopeTubeSeg * Mathf.PI * 2f;
                        var n = radial * Mathf.Cos(av) + Vector3.right * Mathf.Sin(av);
                        verts.Add(center + n * RopeRadius);
                        float uu = u / (float)RopeRingSeg * RopeUVTiling, vv = v / (float)RopeTubeSeg;
                        uvs.Add(RopeUVSwap ? new Vector2(vv, uu) : new Vector2(uu, vv));
                    }
                }

                int stride = RopeTubeSeg + 1;
                for (int u = 0; u < RopeRingSeg; u++)
                    for (int v = 0; v < RopeTubeSeg; v++)
                    {
                        int a = baseIdx + u * stride + v, b = a + stride, c = a + 1, d = b + 1;
                        tris.Add(a); tris.Add(b); tris.Add(c);
                        tris.Add(b); tris.Add(d); tris.Add(c);
                    }
            }

            var mesh = new Mesh { name = "hammock_bundle_rope" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ========= helpers =========
        private static void Strip(GameObject go, HashSet<Component> keep)
        {
            for (int pass = 0; pass < 4; pass++)
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null || c is Transform || keep.Contains(c)) continue;
                    if (pass == 0 && !(c is MonoBehaviour)) continue;
                    Object.DestroyImmediate(c);
                }
            foreach (var c in go.GetComponents<Component>())
                if (c != null && !(c is Transform) && !keep.Contains(c))
                    Plugin.Log.LogWarning("Template component not removed: " + c.GetType().Name);
        }

        private static void CopyItemFields(ShipItem from, ShipItem to)
        {
            for (var t = typeof(ShipItem); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (var f in t.GetFields(R.F | System.Reflection.BindingFlags.DeclaredOnly))
                    if (!f.IsLiteral && !f.IsInitOnly) f.SetValue(to, f.GetValue(from));
        }

        private static void Retarget(GameObject go, ShipItem from, ShipItem to)
        {
            foreach (var c in go.GetComponents<MonoBehaviour>())
            {
                if (c == null || c == from || c == to) continue;
                for (var t = c.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                    foreach (var f in t.GetFields(R.F | System.Reflection.BindingFlags.DeclaredOnly))
                        if (f.FieldType.IsAssignableFrom(typeof(ShipItemBed)) && ReferenceEquals(f.GetValue(c), from))
                            f.SetValue(c, to);
            }
        }

        private static void Register(PrefabsDirectory pd)
        {
            var dir = pd.directory;
            int idx = Plugin.PrefabIndex.Value;
            if (idx < 0) idx = dir.Length;
            if (idx < dir.Length && dir[idx] != null && dir[idx] != Prefab)
            {
                Plugin.Log.LogError($"PrefabIndex {idx} is taken by '{dir[idx].name}'. Using {dir.Length}. Hammocks saved with the old index will NOT load correctly!");
                idx = dir.Length;
            }
            pd.directory = Pad(dir, idx, Prefab);
            if (pd.shipItems != null && pd.shipItems.Length > 0)
                pd.shipItems = Pad(pd.shipItems, idx, Prefab.GetComponent<ShipItem>());
            R.Set(Prefab.GetComponent<SaveablePrefab>(), "prefabIndex", idx);
            Index = idx;
        }

        private static T[] Pad<T>(T[] a, int idx, T v)
        {
            if (idx < a.Length) { a[idx] = v; return a; }
            var n = new T[idx + 1];
            Array.Copy(a, n, a.Length);
            for (int i = a.Length; i <= idx; i++) n[i] = v;
            return n;
        }
    }
}