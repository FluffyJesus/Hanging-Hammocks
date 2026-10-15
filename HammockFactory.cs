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
        private const int BundleIndex = 222;
        private const string BundleNameFallback = "wool";
        private static readonly Color BundleTint = new Color(0.6f, 0.48f, 0.4f);   // темнее и теплее шерсти

        public static GameObject Prefab;
        public static int Index = -1;
        public static bool Failed;
        public static Vector3 BundleSize = new Vector3(0.6f, 0.3f, 0.6f);
        public static bool Ready => Prefab != null && Index >= 0;

        private static GameObject holder;
        private static bool warnedSource, warnedTemplate;
        private static readonly Dictionary<Material, Material> tinted = new Dictionary<Material, Material>();

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

        // ????????? источник ?????????
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

        // ????????? сборка ?????????
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

            // ?? тюк собираем первым: по нему ориентируем гамак ??
            var bunR = BuildBundle(go, pd, out Bounds bunB);

            // ?? геометри€ гамака в пространстве лодки ??
            Matrix4x4 T = Matrix4x4.identity;
            Matrix4x4 M = boat.worldToLocalMatrix * bedT.localToWorldMatrix;
            var pts = new List<Vector3>();
            var mesh = ModelTweaks.Process(bedT.GetComponent<MeshFilter>().sharedMesh, M, pts, out bool readable);
            Bounds bb = ComputeBounds(pts, readable, mesh, M);

            // ƒлинна€ ось гамака = длинна€ ось тюка.  оллайдер тюка остаЄтс€ у вис€щего гамака,
            // и так он л€жет вдоль полотна, а не поперЄк
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

            var sleepT = MakeChild("sleep pos", sleepM, false);   // index 0 Ч Sleep использует GetChild(0)
            var wakeT  = MakeChild("wake pos", sleepM, false);    // index 1
            // bundle_visual был создан раньше Ч ставим его после sleep/wake, чтобы GetChild(0) оставалс€ sleep pos
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
                vr.sharedMaterials = sm;
                vr.shadowCastingMode = srcR.shadowCastingMode;
                vr.receiveShadows = srcR.receiveShadows;
            }

            // ?? корень ??
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

        private static List<Renderer> BuildBundle(GameObject go, PrefabsDirectory pd, out Bounds bounds)
        {
            bounds = default;
            var res = new List<Renderer>();
            var dir = pd.directory;

            GameObject src = null;
            if (BundleIndex < dir.Length && dir[BundleIndex] != null && dir[BundleIndex] != Prefab) src = dir[BundleIndex];
            if (src == null)
                foreach (var g in dir)
                    if (g != null && g.name.ToLowerInvariant().Contains(BundleNameFallback)) { src = g; break; }
            if (src == null) { Plugin.Log.LogWarning("Bundle prefab not found Ч bundle disabled"); return res; }

            var root = new GameObject("bundle_visual").transform;
            root.SetParent(go.transform, false);
            root.gameObject.layer = go.layer;

            Matrix4x4 inv = src.transform.worldToLocalMatrix;
            bool first = true;
            foreach (var smf in src.GetComponentsInChildren<MeshFilter>(true))
            {
                if (smf.sharedMesh == null) continue;
                var smr = smf.GetComponent<MeshRenderer>();
                if (smr == null || !smr.enabled) continue;

                var M = inv * smf.transform.localToWorldMatrix;
                var c = new GameObject("bundle_part").transform;
                c.SetParent(root, false);
                c.localPosition = M.GetColumn(3);
                c.localRotation = M.rotation;
                c.localScale = M.lossyScale;
                c.gameObject.layer = go.layer;
                c.gameObject.AddComponent<MeshFilter>().sharedMesh = smf.sharedMesh;
                var r = c.gameObject.AddComponent<MeshRenderer>();
                r.sharedMaterials = Tint(smr.sharedMaterials);
                r.shadowCastingMode = smr.shadowCastingMode;
                r.receiveShadows = smr.receiveShadows;
                res.Add(r);

                var wb = TransformBounds(smf.sharedMesh.bounds, M);
                if (first) { bounds = wb; first = false; } else bounds.Encapsulate(wb);
            }
            if (res.Count == 0) { Object.DestroyImmediate(root.gameObject); Plugin.Log.LogWarning($"Bundle prefab '{src.name}' has no meshes"); return res; }

            root.localPosition = -bounds.center;
            bounds.center = Vector3.zero;
            return res;
        }

        ///  опии материалов тюка с затемнением (ванильные тюки не мен€ютс€)
        private static Material[] Tint(Material[] src)
        {
            var res = new Material[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var m = src[i];
                if (m == null) continue;
                if (!tinted.TryGetValue(m, out var t))
                {
                    t = new Material(m) { name = m.name + "_hammock" };
                    string prop = t.HasProperty("_Color") ? "_Color"
                                : t.HasProperty("_BaseColor") ? "_BaseColor"
                                : t.HasProperty("_TintColor") ? "_TintColor" : null;
                    if (prop != null) t.SetColor(prop, t.GetColor(prop) * BundleTint);
                    tinted[m] = t;
                }
                res[i] = t;
            }
            return res;
        }

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