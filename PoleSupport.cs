using System.Collections.Generic;
using BepInEx.Bootstrap;
using UnityEngine;

namespace HangingHammock
{
    /// Nailed wood poles from Tent and Plank (and Economy Overhaul, which has the same item) to hang a hammock from:
    /// a level one as a beam over it, or two upright ones as posts at its ends.
    /// Soft dependency: the pole is found by its prefab slot, there is no reference to the other mod's assembly.
    /// All points are in physics space (aboard: carried from the ship onto her walk collider), as the rest of HammockHanger's probes.
    internal static class PoleSupport
    {
        private const int PoleIndex = 643;
        private static readonly string[] PoleMods = { "DogEggz.TentandPlank", "DogEggz.EconomyOverhaul" };

        private const float MaxSlope = 0.6f;    // |y| of the pole's direction: up to ~37 degrees from level
        private const float Catch = 0.5f;       // horizontal reach from the hammock's middle to the pole's line
        private const float EndInset = 0.05f;   // anchors stay this far from the pole's ends
        private const float Refresh = 1f;

        internal struct Hit
        {
            public ShipItem pole;
            public Vector3 under;   // the pole's underside straight over the probe point (as near as the pole allows)
            public Vector3 dir;     // the pole's direction, level
        }

        private static int enabled = -1;
        private static readonly List<ShipItem> poles = new List<ShipItem>();
        private static float refreshAt;

        internal static bool Enabled
        {
            get
            {
                if (enabled < 0)
                {
                    enabled = 0;
                    foreach (var g in PoleMods)
                        if (Chainloader.PluginInfos.ContainsKey(g)) { enabled = 1; Plugin.Log.LogInfo($"Wood poles from {g}: hammocks can hang from them"); break; }
                }
                return enabled == 1;
            }
        }

        internal static bool IsPole(ShipItem it)
        {
            if (it == null) return false;
            var sp = it.GetComponent<SaveablePrefab>();
            return sp != null && sp.prefabIndex == PoleIndex;
        }

        // Fixed in place: nailed, or a wall item held on its wall (as Tent and Plank counts a pole that holds things)
        internal static bool Usable(ShipItem pole) =>
            pole != null && pole.sold && pole.held == null && pole.GetComponent<BoxCollider>() != null
            && (pole.nailed || (pole.wallAttachment && pole.itemRigidbodyC != null && pole.itemRigidbodyC.attached));

        private static List<ShipItem> Poles()
        {
            if (Time.unscaledTime < refreshAt) return poles;
            refreshAt = Time.unscaledTime + Refresh;
            poles.Clear();
            foreach (var it in Object.FindObjectsOfType<ShipItem>())
                if (IsPole(it)) poles.Add(it);
            return poles;
        }

        // The pole's axis, end to end, inset from both ends. Its box is the pole's mesh bounds (along local z).
        private static void Segment(ShipItem pole, out Vector3 a, out Vector3 b, out float radius)
        {
            var box = pole.GetComponent<BoxCollider>();
            var t = pole.transform;
            float half = Mathf.Max(0f, box.size.z * 0.5f - EndInset);
            a = ToPhys(pole, t.TransformPoint(box.center + Vector3.forward * half));
            b = ToPhys(pole, t.TransformPoint(box.center - Vector3.forward * half));
            radius = Mathf.Max(box.size.x, box.size.y) * 0.5f;
        }

        // Where the pole is drawn, carried into physics space: aboard, from the ship onto her walk collider
        private static Vector3 ToPhys(ShipItem pole, Vector3 p)
        {
            Transform boat = pole.currentActualBoat, walk = pole.currentWalkCol;
            return boat != null && walk != null ? walk.TransformPoint(boat.InverseTransformPoint(p)) : p;
        }

        // The point of the pole's axis nearest to p's vertical line (measured level).
        private static Vector3 OverPoint(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 d = b - a;
            Vector2 dl = new Vector2(d.x, d.z), pl = new Vector2(p.x - a.x, p.z - a.z);
            float s = dl.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(pl, dl) / dl.sqrMagnitude);
            return a + d * s;
        }

        /// The nearest nailed level pole over origin, at most maxUp above it.
        internal static bool Find(Vector3 origin, float maxUp, out Hit hit)
        {
            hit = default;
            if (!Enabled) return false;
            float best = maxUp;
            foreach (var pole in Poles())
            {
                if (!Usable(pole)) continue;
                Segment(pole, out var a, out var b, out float r);
                Vector3 d = b - a;
                if (d.sqrMagnitude < 1e-4f || Mathf.Abs(d.normalized.y) > MaxSlope) continue;
                Vector3 under = OverPoint(a, b, origin) - Vector3.up * r;
                Vector3 off = under - origin;
                float up = off.y;
                off.y = 0f;
                if (up <= 0.05f || up > best || off.magnitude > Catch) continue;
                best = up;
                hit.pole = pole; hit.under = under;
                hit.dir = new Vector3(d.x, 0f, d.z).normalized;
            }
            return hit.pole != null;
        }

        /// The underside of this pole over p (clamped to the pole), or false if the pole can no longer hold.
        internal static bool Under(ShipItem pole, Vector3 p, out Vector3 under)
        {
            under = p;
            if (!Usable(pole)) return false;
            Segment(pole, out var a, out var b, out float r);
            under = OverPoint(a, b, p) - Vector3.up * r;
            return true;
        }

        /// Where a rope aimed at the nominal point ties to the pole: the nearest point of its underside; the hook faces down.
        internal static Vector3 Anchor(ShipItem pole, Vector3 nominal, out Vector3 normal)
        {
            normal = Vector3.down;
            if (!Usable(pole)) return nominal;
            Segment(pole, out var a, out var b, out float r);
            Vector3 d = b - a;
            float s = d.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(nominal - a, d) / d.sqrMagnitude);
            return a + d * s - Vector3.up * r;
        }

        // ---- Upright poles: a hammock hung between two posts ----

        private const float Upright = 0.8f;      // |y| of a post's direction: within ~37 degrees of vertical
        private const float PostReach = 0.6f;    // how far off the line between the posts the hammock's middle may be

        /// The point of the post's axis at height y (false when the post does not reach it).
        internal static bool PostPoint(ShipItem post, float y, out Vector3 axis, out float radius)
        {
            axis = default; radius = 0f;
            if (!Usable(post)) return false;
            Segment(post, out var a, out var b, out radius);
            Vector3 d = b - a;
            if (d.sqrMagnitude < 1e-4f || Mathf.Abs(d.normalized.y) < Upright) return false;
            float s = (y - a.y) / d.y;
            if (s < 0f || s > 1f) return false;
            axis = a + d * s;
            return true;
        }

        /// Where a rope from the hammock's end ties to the post: on its side facing the end, at height y; the hook faces the end.
        internal static Vector3 PostAnchor(ShipItem post, float y, Vector3 end, out Vector3 normal)
        {
            normal = Vector3.down;
            if (!Usable(post)) return end;
            Segment(post, out var a, out var b, out float r);
            Vector3 d = b - a;
            float s = Mathf.Abs(d.y) < 1e-4f ? 0f : Mathf.Clamp01((y - a.y) / d.y);
            Vector3 axis = a + d * s;
            Vector3 to = end - axis; to.y = 0f;
            if (to.sqrMagnitude < 1e-6f) return axis;
            normal = to.normalized;
            return axis + normal * r;
        }

        /// The heights an upright post's axis spans (false for a pole that is no post).
        internal static bool PostRange(ShipItem post, out float lo, out float hi)
        {
            lo = hi = 0f;
            if (!Usable(post)) return false;
            Segment(post, out var a, out var b, out _);
            Vector3 d = b - a;
            if (d.sqrMagnitude < 1e-4f || Mathf.Abs(d.normalized.y) < Upright) return false;
            lo = Mathf.Min(a.y, b.y); hi = Mathf.Max(a.y, b.y);
            return true;
        }

        /// Two fixed upright posts on both sides of origin, minSpan..maxSpan apart (level).
        internal static bool FindPosts(Vector3 origin, float minSpan, float maxSpan, out ShipItem postA, out ShipItem postB)
        {
            postA = postB = null;
            if (!Enabled) return false;
            var near = new List<KeyValuePair<ShipItem, Vector3>>();
            foreach (var pole in Poles())
            {
                if (!PostRange(pole, out float lo, out float hi) || !PostPoint(pole, (lo + hi) * 0.5f, out var axis, out _)) continue;
                Vector3 off = axis - origin; off.y = 0f;
                if (off.magnitude <= maxSpan) near.Add(new KeyValuePair<ShipItem, Vector3>(pole, axis));
            }
            float best = float.MaxValue;
            for (int i = 0; i < near.Count; i++)
                for (int j = i + 1; j < near.Count; j++)
                {
                    Vector3 a = near[i].Value, b = near[j].Value;
                    Vector2 ab = new Vector2(b.x - a.x, b.z - a.z), ao = new Vector2(origin.x - a.x, origin.z - a.z);
                    float span = ab.magnitude;
                    if (span < minSpan || span > maxSpan) continue;
                    float s = Vector2.Dot(ao, ab) / (span * span);
                    if (s < 0.2f || s > 0.8f) continue;
                    float side = Mathf.Abs(ab.x * ao.y - ab.y * ao.x) / span;
                    if (side > PostReach) continue;
                    float score = side + Mathf.Abs(s - 0.5f) * span;
                    if (score < best) { best = score; postA = near[i].Key; postB = near[j].Key; }
                }
            return postA != null;
        }

        /// The nearest nailed upright post at height y within reach of p (level distance), other than skip.
        internal static ShipItem PostNear(Vector3 p, float y, float reach, ShipItem skip)
        {
            if (!Enabled) return null;
            ShipItem best = null;
            foreach (var pole in Poles())
            {
                if (pole == skip || !PostPoint(pole, y, out var axis, out _)) continue;
                Vector3 off = axis - p; off.y = 0f;
                if (off.magnitude < reach) { reach = off.magnitude; best = pole; }
            }
            return best;
        }

        // ---- Any pole as one hook's place (a hammock hung from surfaces around it) ----

        /// The nearest fixed pole a ray meets within max (from minDist on): where on its surface, facing the ray's origin.
        internal static bool Raycast(Vector3 origin, Vector3 dir, float max, float minDist, out float dist, out Vector3 point, out Vector3 normal, out ShipItem hitPole)
        {
            dist = float.MaxValue; point = normal = default; hitPole = null;
            if (!Enabled) return false;
            foreach (var pole in Poles())
            {
                if (!Usable(pole)) continue;
                Segment(pole, out var a, out var b, out float r);
                ClosestOnSegments(origin, origin + dir * max, a, b, out float s, out float t);
                Vector3 onRay = origin + dir * (max * s), onAxis = a + (b - a) * t;
                Vector3 off = onRay - onAxis;
                if (off.magnitude > r) continue;
                float d = max * s;
                if (d < minDist || d >= dist) continue;
                normal = off.sqrMagnitude > 1e-8f ? off.normalized : -dir;
                dist = d; point = onAxis + normal * r; hitPole = pole;
            }
            return hitPole != null;
        }

        /// A fixed pole whose surface is within tol of p (a saved hook on it), or null.
        internal static ShipItem PoleAt(Vector3 p, float tol)
        {
            if (!Enabled) return null;
            ShipItem best = null; float bestD = float.MaxValue;
            foreach (var pole in Poles())
            {
                if (!Usable(pole)) continue;
                Segment(pole, out var a, out var b, out float r);
                Vector3 d = b - a;
                float t = d.sqrMagnitude < 1e-8f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, d) / d.sqrMagnitude);
                float gap = Mathf.Abs((p - (a + d * t)).magnitude - r);
                if (gap <= tol && gap < bestD) { bestD = gap; best = pole; }
            }
            return best;
        }

        // Closest points of segments p0-p1 and q0-q1, as fractions along each
        private static void ClosestOnSegments(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1, out float s, out float t)
        {
            Vector3 d1 = p1 - p0, d2 = q1 - q0, r = p0 - q0;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            if (a < 1e-8f && e < 1e-8f) { s = t = 0f; return; }
            if (a < 1e-8f) { s = 0f; t = Mathf.Clamp01(f / e); return; }
            float c = Vector3.Dot(d1, r);
            if (e < 1e-8f) { t = 0f; s = Mathf.Clamp01(-c / a); return; }
            float b = Vector3.Dot(d1, d2), den = a * e - b * b;
            s = den > 1e-8f ? Mathf.Clamp01((b * f - c * e) / den) : 0f;
            t = (b * s + f) / e;
            if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
            else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
        }

        /// A level forward for the hung hammock that lays its long axis along the pole, turned as little as possible.
        internal static Vector3 AlongForward(Vector3 poleDir, Vector3 axisA, Vector3 fwd)
        {
            Vector3 f = axisA == Vector3.forward ? poleDir : Vector3.Cross(poleDir, Vector3.up);
            return Vector3.Dot(f, fwd) < 0f ? -f : f;
        }
    }
}
