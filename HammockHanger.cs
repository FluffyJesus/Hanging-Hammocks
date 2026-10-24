using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HangingHammock
{
    [DefaultExecutionOrder(10000)]
    public class HammockHanger : MonoBehaviour
    {
        public Vector3 bCenter;
        public Vector3 bExt;
        public Transform[] swingParts;
        public Renderer[] hammockRenderers;
        public Renderer[] bundleRenderers;
        public bool hasEnds;
        public Vector3 endA, endB;
        public Vector3 hammockBoxSize, bundleBoxSize;

        internal const int Mask = ~((1 << 2) | (1 << 5));
        private const string KeyPrefix = "HangingHammock.";
        private const string PoleTag = ";pole", PostsTag = ";posts", FreeTag = ";free;";
        // Between two upright poles: the posts stand at least the hammock's length plus PostGap apart (room for the ropes),
        // at most plus PostMaxGap. A rope rises PostRopeSlope degrees from the end to its hook, carrying on the cloth's line
        private const float PostGap = 0.1f, PostMaxGap = 1.5f, PostRopeSlope = 35f;
        // A rope never runs flatter than this over level, at hanging or at a height change (else its knot turns over)
        private const float MinRopeAngle = 30f;
        private static float HookRise(float gap) => Mathf.Max(0.03f, gap) * Mathf.Tan(PostRopeSlope * Mathf.Deg2Rad);
        private const float AnchorSearch = 2.5f, AnchorMinDist = 0.12f;
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        private static readonly FieldInfo OnBoatF =
            typeof(ItemRigidbody).GetField("onBoat", BindingFlags.NonPublic | BindingFlags.Instance);

        private ShipItem item;
        private ItemRigidbody twinC;
        private Transform twin;
        private Rigidbody body;
        private Renderer rootRenderer;
        private BoxCollider box;
        private bool scaleLogged;

        private Collider[] twinCols;
        private BoxCollider twinBox;
        private Vector3 twinBoxOrigSize, twinBoxOrigCenter;
        private bool twinBoxResized;
        private float colGraceUntil;

        public Vector3 axisA, axisT;
        private Vector3[] restPos;
        private Quaternion[] restRot;
        private bool partsDirty;

        private Renderer[] allRenderers;
        private Behaviour rootOutline;
        private Behaviour[] partOutlines;
        private FieldInfo outlineColor;
        private bool colliderInit, colliderHung;
        private float npcLyingUntil, occupied;   // occupied: 0..1, eased; 1 = someone (player or NPC) is lying in the hammock
        private void MarkNpcLying() { npcLyingUntil = Time.time + 2f; }

        public bool IsHung { get; private set; }
        private bool hangMode;
        private float ropeLength;
        private bool wasHeld, canHang, lastCanHang, restoreDone;
        private float probeUp, aliveTime;
        private float takeDownHold;
        private bool takeDownHintShown;

        private float pinTimer; private Transform pinParent; private Vector3 pinPos; private Quaternion pinRot;

        private float phi, phiVel;
        private bool hasPrev;
        private Vector3 prevPivot, prevPivotVel, pivotAcc;
        private Quaternion prevRestRot;

        private RopeVisual[] ropes;

        // Hung from Tent and Plank wood poles (PoleSupport): a level one over it (onPole) or two upright posts (onPosts:
        // pole and post2). It comes down when a pole is picked up or removed
        private ShipItem pole, post2;
        private bool onPole, onPosts;
        private float poleWaitUntil;   // after a load: how long to look for the poles before taking the hammock down
        private PoleSupport.Hit previewBeam;
        private ShipItem previewPostA, previewPostB;
        private bool lastTooFlat;

        // Hung from surfaces around it (no ceiling straight over it): each rope found its own hook, saved with the hammock
        private bool onFree, previewFree;
        private readonly Vector3[] previewFreeA = new Vector3[2], previewFreeN = new Vector3[2];
        private readonly ShipItem[] previewFreePoles = new ShipItem[2];
        // A hook of those on a wood pole: the pole (found again after a load); it comes down when the pole goes
        private readonly ShipItem[] freePoles = new ShipItem[2];
        private readonly bool[] freeOnPole = new bool[2];

        private readonly Vector3[] topL = new Vector3[2];
        private readonly Vector3[] topN = new Vector3[2];
        private bool anchorsDirty = true;
        private float anchorRecheckAt;

        private float Top => bCenter.y + bExt.y;
        private float Height => 2f * bExt.y;

        private void Start()
        {
            VirtualCrewPatch.TryPatch();

            item = GetComponent<ShipItem>();
            rootRenderer = GetComponent<Renderer>();
            box = GetComponent<BoxCollider>();
            if (hammockRenderers == null) hammockRenderers = new Renderer[0];
            if (bundleRenderers == null) bundleRenderers = new Renderer[0];

            if (bExt.x >= bExt.z) { axisA = Vector3.right; axisT = Vector3.forward; }
            else                  { axisA = Vector3.forward; axisT = Vector3.right; }

            int n = swingParts != null ? swingParts.Length : 0;
            restPos = new Vector3[n]; restRot = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                if (swingParts[i] == null) continue;
                restPos[i] = swingParts[i].localPosition;
                restRot[i] = swingParts[i].localRotation;
            }

            allRenderers = new Renderer[hammockRenderers.Length + bundleRenderers.Length];
            hammockRenderers.CopyTo(allRenderers, 0);
            bundleRenderers.CopyTo(allRenderers, hammockRenderers.Length);

            Material hm = hammockRenderers.Length > 0 && hammockRenderers[0] != null ? hammockRenderers[0].sharedMaterial : null;
            ropes = new[] { new RopeVisual("HammockRope_0", hm), new RopeVisual("HammockRope_1", hm) };
        }

        private bool EnsureTwin()
        {
            if (twinC != null) return true;
            var rb = item.GetItemRigidbody();
            if (rb == null) return false;
            twinC = rb; twin = rb.transform; body = rb.GetComponent<Rigidbody>();
            return true;
        }

        private void SetHungPhysics(bool hung)
        {
            if (twinC == null) return;
            twinC.attached = hung;
            twinC.disableCol = false;
            twinC.inStove = false;
        }

        private bool OnBoat(out Transform boat, out Transform walk)
        {
            boat = item != null ? item.currentActualBoat : null;
            walk = item != null ? item.currentWalkCol : null;
            if (boat == null || walk == null || twinC == null || OnBoatF == null) return false;
            return (bool)OnBoatF.GetValue(twinC);
        }

        private Vector3 ToPhys(Vector3 p) =>
            OnBoat(out var b, out var w) ? w.TransformPoint(b.InverseTransformPoint(p)) : p;

        private Vector3 ToItem(Vector3 p) =>
            OnBoat(out var b, out var w) ? b.TransformPoint(w.InverseTransformPoint(p)) : p;

        private Quaternion ToPhys(Quaternion q) =>
            OnBoat(out var b, out var w) ? w.rotation * Quaternion.Inverse(b.rotation) * q : q;

        private void ManageTwinColliders()
        {
            if (twin == null) return;
            if (twinCols == null || twinCols.Length == 0)
            {
                twinCols = twin.GetComponentsInChildren<Collider>(true);
                twinBox = twin.GetComponent<BoxCollider>();
                if (twinBox != null) { twinBoxOrigSize = twinBox.size; twinBoxOrigCenter = twinBox.center; }
                if (twinCols.Length == 0) return;
            }

            bool held = item != null && item.held != null;
            bool grace = Time.unscaledTime < colGraceUntil && !held;

            if (IsHung && Plugin.SolidWhenHung.Value && twinBox != null)
            {
                if (!twinBoxResized)
                {
                    twinBox.center = Vector3.zero;
                    twinBox.size = hammockBoxSize;
                    twinBoxResized = true;
                }
                return;
            }

            RestoreTwinBox();

            if (IsHung || grace)
                foreach (var c in twinCols)
                    if (c != null && c.enabled) c.enabled = false;
        }

        private void RestoreTwinBox()
        {
            if (!twinBoxResized || twinBox == null) return;
            twinBox.center = twinBoxOrigCenter;
            twinBox.size = twinBoxOrigSize;
            twinBoxResized = false;
        }

        private void Update()
        {
            VirtualCrewPatch.TryPatch();

            if (item == null || !EnsureTwin()) return;
            aliveTime += Time.unscaledDeltaTime;

            if (GameState.inBed == transform && !IsHung && Sleep.instance != null)
            {
                Sleep.instance.LeaveBed();
                Notify("Hang the hammock first");
            }

            bool held = item.held != null;
            if (!item.sold) { wasHeld = held; return; }
            if (!restoreDone && aliveTime > 1f) TryRestore();

            if (held)
            {
                if (!wasHeld) hangMode = false;
                if (IsHung) Unhang();

                if (Input.GetKeyDown(Plugin.ModeKey.Value) && !GameState.inCursorMenu)
                {
                    hangMode = !hangMode;
                    UISoundPlayer.instance.PlayUISound(UISounds.winchClick, 0.6f, hangMode ? 1.1f : 0.9f);
                    Notify(hangMode ? "Hammock unrolled: release under a ceiling to hang" : "Hammock rolled up");
                }
                item.holdDistance = hangMode ? Tuning.HangHold : Tuning.BundleHold;

                canHang = hangMode && ProbeForHang(ToPhys(transform.position), out probeUp, out _, out _, out previewBeam);
                previewPostA = previewPostB = null;
                previewFree = false;
                if (hangMode && !canHang) canHang = ProbePosts(ToPhys(transform.position), out previewPostA, out previewPostB, out _, out _);
                bool tooFlat = canHang && PreviewMatrix(out var pm) && !RopesSteep(pm, probeUp, previewBeam.pole, previewPostA, previewPostB);
                if (tooFlat) canHang = false;
                if (hangMode && !canHang && PreviewMatrix(out var fm) && ProbeFree(fm, previewFreeA, previewFreeN, previewFreePoles, out _, out _))
                {
                    canHang = previewFree = true;
                    tooFlat = false;
                    previewBeam = default;
                    previewPostA = previewPostB = null;
                }
                if (tooFlat && !lastTooFlat) Notify("The ropes would be too flat here");
                lastTooFlat = tooFlat;
                if (canHang && !lastCanHang)
                    Notify(previewPostA != null ? "Release to hang the hammock between the posts"
                        : previewBeam.pole != null ? "Release to hang the hammock from the pole" : "Release to hang the hammock");
                lastCanHang = canHang;
            }
            else
            {
                if (wasHeld)
                {
                    if (canHang && item.GetCurrentInventorySlot() == -1) Hang();
                    canHang = lastCanHang = false;
                    hangMode = false;
                    item.holdDistance = Tuning.BundleHold;
                }
                if (IsHung) UpdateHung();
            }
            wasHeld = held;
        }

        private void UpdateHung()
        {
            if (!item.nailed) { Unhang(); Notify("Hammock taken down"); return; }
            if ((onPole || onPosts) && !PoleHolds()) return;
            if (onFree && !FreePolesHold()) return;
            if (!twinC.attached) twinC.attached = true;
            if (twinC.inStove) twinC.inStove = false;
            if (HandleTakeDown()) return;
            HandleHeightInput();
        }

        private void FixedUpdate()
        {
            ManageTwinColliders();

            if (pinTimer <= 0f || twin == null) return;
            pinTimer -= Time.deltaTime;
            if (twin.parent != pinParent) { pinTimer = 0f; return; }
            if ((twin.localPosition - pinPos).sqrMagnitude > 1e-6f || Quaternion.Angle(twin.localRotation, pinRot) > 0.1f)
            {
                twin.localPosition = pinPos; twin.localRotation = pinRot;
                if (body != null) { body.position = twin.position; body.rotation = twin.rotation; }
            }
        }

        private void Pin(float seconds)
        {
            pinParent = twin.parent; pinPos = twin.localPosition; pinRot = twin.localRotation; pinTimer = seconds;
        }

        internal static bool CastStatic(Vector3 origin, Vector3 dir, float max, out RaycastHit best, float minDist = 0f)
        {
            best = default;
            bool oldBack = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            var hits = Physics.RaycastAll(origin, dir, max, Mask, QueryTriggerInteraction.Ignore);
            Physics.queriesHitBackfaces = oldBack;

            float bestD = float.MaxValue; bool found = false;
            foreach (var h in hits)
            {
                if (h.distance < minDist) continue;
                var c = h.collider;
                if (c.GetComponentInParent<ShipItem>() != null) continue;
                if (c.GetComponentInParent<ItemRigidbody>() != null) continue;
                if (c.GetComponentInParent<CharacterController>() != null) continue;
                if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;
                if (h.distance < bestD) { bestD = h.distance; best = h; found = true; }
            }
            return found;
        }

        private bool ProbeForHang(Vector3 origin, out float up, out float room, out float len, out PoleSupport.Hit beam)
        {
            up = room = len = 0f;
            bool hitCeil = CastStatic(origin, Vector3.up, Tuning.MaxCeiling, out var ch);
            // A wood pole under the ceiling (or with no ceiling over it) is the beam; one above the ceiling is out of reach
            if (PoleSupport.Find(origin, hitCeil ? ch.distance : Tuning.MaxCeiling, out beam))
                up = beam.under.y - origin.y;
            else
            {
                if (!hitCeil || Mathf.Abs(ch.normal.y) < 0.5f) return false;
                up = ch.distance;
            }
            Vector3 top = origin + Vector3.up * up;
            room = CastStatic(top + Vector3.down * 0.05f, Vector3.down, 8f, out var fh) ? fh.distance + 0.05f : 8f;
            float maxLen = room - Height - Tuning.FloorClearance;
            if (maxLen < Tuning.MinRope) return false;
            len = Mathf.Clamp(Tuning.DefaultRope, Tuning.MinRope, maxLen);
            return true;
        }

        // Two upright poles on both sides of origin. The hooks go where the hammock is held (its ends rise under them),
        // kept within the height both posts reach and raised so its bottom clears the floor
        private bool ProbePosts(Vector3 origin, out ShipItem postA, out ShipItem postB, out float hookY, out float rise)
        {
            float endY = EndLocal(1f).y;
            float span = Vector3.Distance(EndLocal(-1f), EndLocal(1f));
            hookY = rise = 0f;
            if (!PoleSupport.FindPosts(origin, span + PostGap, span + PostMaxGap, out postA, out postB)) return false;
            if (!PoleSupport.PostRange(postA, out float loA, out float hiA) || !PoleSupport.PostRange(postB, out float loB, out float hiB)) return false;
            float lo = Mathf.Max(loA, loB), hi = Mathf.Min(hiA, hiB);
            if (hi <= lo) return false;

            PoleSupport.PostPoint(postA, (lo + hi) * 0.5f, out var pa, out float ra);
            PoleSupport.PostPoint(postB, (lo + hi) * 0.5f, out var pb, out float rb);
            Vector3 between = pb - pa; between.y = 0f;
            rise = HookRise((between.magnitude - span - ra - rb) * 0.5f);
            hookY = Mathf.Clamp(origin.y + endY + rise, lo, hi);

            Vector3 mid = (pa + pb) * 0.5f; mid.y = hookY;
            if (CastStatic(mid, Vector3.down, 8f, out var fh))
            {
                float bottom = hookY - rise - endY + bCenter.y - bExt.y;
                float lift = fh.point.y + Tuning.FloorClearance - bottom;
                if (lift > 0f) hookY += lift;
            }
            return PoleSupport.PostPoint(postA, hookY, out _, out _) && PoleSupport.PostPoint(postB, hookY, out _, out _);
        }

        // Where the rope from this end (physics space) ties to the post: PostRopeSlope over the end, on the side facing it
        private static Vector3 PostHook(ShipItem post, Vector3 endW, out Vector3 normal)
        {
            float rise = HookRise(0.1f);
            if (PoleSupport.PostPoint(post, endW.y, out var axis, out float r))
            {
                Vector3 gap = axis - endW; gap.y = 0f;
                rise = HookRise(gap.magnitude - r);
            }
            return PoleSupport.PostAnchor(post, endW.y + rise, endW, out normal);
        }

        // The post a rope from this end (physics space) ties to: the nearer of the two
        private ShipItem PostFor(Vector3 endW, ShipItem a, ShipItem b)
        {
            if (a == null || b == null) return a ?? b;
            bool aOk = PoleSupport.PostPoint(a, endW.y, out var pa, out _), bOk = PoleSupport.PostPoint(b, endW.y, out var pb, out _);
            if (!aOk || !bOk) return aOk ? a : b;
            pa.y = pb.y = endW.y;
            return (pa - endW).sqrMagnitude <= (pb - endW).sqrMagnitude ? a : b;
        }

        private static Vector3 FindAnchor(Matrix4x4 m, Vector3 endL, Vector3 nominalL, out Vector3 normal)
        {
            Vector3 a = m.MultiplyPoint3x4(endL), b = m.MultiplyPoint3x4(nominalL);
            Vector3 d = b - a;
            float dist = d.magnitude;
            Vector3 up = m.MultiplyVector(Vector3.up).normalized;
            normal = -up;
            if (dist < 1e-3f) return b;
            Vector3 dir = d / dist;

            if (CastStatic(a, dir, dist + AnchorSearch, out var h, AnchorMinDist))
            {
                normal = Vector3.Dot(h.normal, dir) > 0f ? -h.normal : h.normal;
                return h.point;
            }

            if (CastStatic(b - up * 0.3f, up, AnchorSearch + 0.3f, out h))
            {
                normal = Vector3.Dot(h.normal, up) > 0f ? -h.normal : h.normal;
                return h.point;
            }
            return b;
        }

        // Where the rope of end i (0: -axis, 1: +axis) ties, for the hammock at m: between posts, on a beam pole or at the
        // ceiling ceilY over it (in its own frame)
        private Vector3 AnchorW(Matrix4x4 m, int i, float ceilY, ShipItem beamPole, ShipItem postA, ShipItem postB, out Vector3 nW)
        {
            float sign = i == 0 ? -1f : 1f;
            Vector3 e = EndLocal(sign);
            if (postA != null && postB != null)
            {
                Vector3 endW = m.MultiplyPoint3x4(e);
                return PostHook(PostFor(endW, postA, postB), endW, out nW);
            }
            ceilY = Mathf.Max(e.y + 0.05f, ceilY);
            if (beamPole != null) return PoleSupport.Anchor(beamPole, m.MultiplyPoint3x4(TopAnchor(e, sign, ceilY)), out nW);
            return FindAnchor(m, e, TopAnchor(e, sign, ceilY), out nW);
        }

        // A rope from a hammock end (side: level, across the hammock) at least MinRopeAngle over level and leaning at most
        // RopeSideAngle out of the upright plane through the hammock's length
        private static bool RopeFits(Vector3 rope, Vector3 side)
        {
            float l = rope.magnitude;
            if (l < 1e-3f) return true;
            float maxSide = Mathf.Sin(Mathf.Clamp(Plugin.RopeSideAngle.Value, 0f, 90f) * Mathf.Deg2Rad);
            return rope.y / l >= Mathf.Sin(MinRopeAngle * Mathf.Deg2Rad) && Mathf.Abs(Vector3.Dot(rope, side)) / l <= maxSide + 1e-4f;
        }

        // Both ropes fit (RopeFits) for the hammock at m
        private bool RopesSteep(Matrix4x4 m, float ceilY, ShipItem beamPole, ShipItem postA, ShipItem postB)
        {
            Vector3 side = Vector3.ProjectOnPlane(m.MultiplyVector(axisT), Vector3.up).normalized;
            for (int i = 0; i < 2; i++)
            {
                Vector3 end = m.MultiplyPoint3x4(EndLocal(i == 0 ? -1f : 1f));
                if (!RopeFits(AnchorW(m, i, ceilY, beamPole, postA, postB, out _) - end, side)) return false;
            }
            return true;
        }

        // Hooks for a hammock held at m on any surface around it but the tops of things (ceilings, beams, walls, trees): from
        // each end the rope nearest the usual RopeAngle, at least MinRopeAngle over level, at most MaxRope long, in any direction.
        // Hung, the hammock turns to lie along the line between the hooks, centred between them at the height it is held;
        // there its ropes must fit (RopeFits, MinRope..MaxRope) and its bottom clear the floor
        private bool ProbeFree(Matrix4x4 m, Vector3[] aW, Vector3[] nW, ShipItem[] onPoles, out Vector3 pos, out Quaternion rot)
        {
            Vector3 up = Vector3.up;
            pos = m.MultiplyPoint3x4(Vector3.zero);
            rot = m.rotation;

            float maxRope = Mathf.Max(Tuning.MinRope + 0.1f, Plugin.MaxRope.Value);
            float nominal = 90f - Mathf.Clamp(Plugin.RopeAngle.Value, 0f, 90f - MinRopeAngle);
            float[] elevations = { nominal, 90f, 75f, 60f, 45f, MinRopeAngle + 1f };
            float[] azimuths = { 0f, 15f, -15f, 30f, -30f, 45f, -45f, 60f, -60f, 75f, -75f, 90f, -90f };
            for (int i = 0; i < 2; i++)
            {
                float sign = i == 0 ? -1f : 1f;
                Vector3 end = m.MultiplyPoint3x4(EndLocal(sign));
                Vector3 outward = Vector3.ProjectOnPlane(m.MultiplyVector(axisA * sign), up).normalized;
                Vector3 side = Vector3.Cross(up, outward);
                float bestScore = float.MaxValue;
                bool found = false;
                foreach (float el in elevations)
                    foreach (float az in azimuths)
                    {
                        if (el >= 89.9f && az != 0f) continue;
                        float e = el * Mathf.Deg2Rad, a = az * Mathf.Deg2Rad;
                        Vector3 dir = Mathf.Cos(e) * (Mathf.Cos(a) * outward + Mathf.Sin(a) * side) + Mathf.Sin(e) * up;
                        float score = Mathf.Abs(el - nominal) + Mathf.Abs(az);
                        if (score >= bestScore) continue;
                        if (!CastHook(end, dir, maxRope, out var hp, out var hn, out var hpole)) continue;
                        bestScore = score; aW[i] = hp; nW[i] = hn; onPoles[i] = hpole; found = true;
                    }
                if (!found) return false;
            }

            // Along the hooks' line: the long axis from hook 0 to hook 1, the ends centred between them
            Vector3 line = aW[1] - aW[0]; line.y = 0f;
            if (line.sqrMagnitude > 1e-4f)
            {
                line.Normalize();
                rot = Quaternion.LookRotation(axisA == Vector3.forward ? line : Vector3.Cross(line, up), up);
                Vector3 endsMid = (EndLocal(-1f) + EndLocal(1f)) * 0.5f; endsMid.y = 0f;
                Vector3 mid = (aW[0] + aW[1]) * 0.5f;
                mid.y = pos.y;
                pos = mid - rot * endsMid;
            }

            var hung = Matrix4x4.TRS(pos, rot, Vector3.one);
            Vector3 across = rot * axisT;

            // Both ropes at one angle: from the usual RopeAngle outward, the first elevation at which a straight rope from
            // each end, out along the hammock, meets a surface for both (else the hooks found above stay)
            Vector3 alongW = rot * axisA;
            var symA = new Vector3[2]; var symN = new Vector3[2]; var symP = new ShipItem[2];
            for (int k = 0; k <= 48; k++)
            {
                float el = nominal + (k % 2 == 0 ? 1f : -1f) * 1.25f * ((k + 1) / 2);
                if (el < MinRopeAngle + 0.5f || el > 90f) continue;
                float e = el * Mathf.Deg2Rad;
                bool both = true;
                for (int i = 0; i < 2 && both; i++)
                {
                    Vector3 end = hung.MultiplyPoint3x4(EndLocal(i == 0 ? -1f : 1f));
                    Vector3 dir = Mathf.Cos(e) * alongW * (i == 0 ? -1f : 1f) + Mathf.Sin(e) * up;
                    both = CastHook(end, dir, maxRope, out symA[i], out symN[i], out symP[i]);
                }
                if (!both) continue;
                for (int i = 0; i < 2; i++) { aW[i] = symA[i]; nW[i] = symN[i]; onPoles[i] = symP[i]; }
                break;
            }
            for (int i = 0; i < 2; i++)
            {
                Vector3 rope = aW[i] - hung.MultiplyPoint3x4(EndLocal(i == 0 ? -1f : 1f));
                float l = rope.magnitude;
                if (l < Tuning.MinRope || l > maxRope || !RopeFits(rope, across)) return false;
            }
            Vector3 center = hung.MultiplyPoint3x4(bCenter);
            return !CastStatic(center, Vector3.down, 8f, out var fh) || center.y - bExt.y - fh.point.y >= Tuning.FloorClearance;
        }

        // Where a rope from o along dir first meets a hook's place within max: a surface but the top of something, or a
        // fixed wood pole (pole: which one)
        private static bool CastHook(Vector3 o, Vector3 dir, float max, out Vector3 point, out Vector3 normal, out ShipItem pole)
        {
            point = normal = default; pole = null;
            float best = float.MaxValue;
            if (CastStatic(o, dir, max, out var h, Tuning.MinRope))
            {
                Vector3 n = Vector3.Dot(h.normal, dir) > 0f ? -h.normal : h.normal;
                if (n.y <= 0.5f) { best = h.distance; point = h.point; normal = n; }
            }
            if (PoleSupport.Raycast(o, dir, max, Tuning.MinRope, out float d, out var pp, out var pn, out var hitPole) && d < best)
            {
                best = d; point = pp; normal = pn; pole = hitPole;
            }
            return best < float.MaxValue;
        }

        // Hung from surfaces: a hook on a wood pole holds while the pole is fixed; after a load the pole is looked for again
        // at the hook. A pole picked up, removed or never found takes the hammock down
        private bool FreePolesHold()
        {
            for (int i = 0; i < 2; i++)
            {
                if (!freeOnPole[i]) continue;
                if (freePoles[i] == null && Time.time < poleWaitUntil)
                {
                    freePoles[i] = PoleSupport.PoleAt(twin.TransformPoint(topL[i]), 0.05f);
                    continue;
                }
                if (PoleSupport.Usable(freePoles[i])) continue;
                colGraceUntil = Time.unscaledTime + 1f;
                item.nailed = false;
                Unhang();
                Notify("The pole is gone: hammock taken down");
                return false;
            }
            return true;
        }

        // Hung from surfaces: the hammock moves d down (d < 0: up) under fixed hooks. Each rope keeps MinRope..MaxRope and
        // MinRopeAngle, and the floor stays clear
        private void SetFreeHeight(float d)
        {
            float maxRope = Mathf.Max(Tuning.MinRope + 0.1f, Plugin.MaxRope.Value);
            var newTop = new Vector3[2];
            float longest = 0f;
            for (int i = 0; i < 2; i++)
            {
                newTop[i] = topL[i] + Vector3.up * d;
                Vector3 rope = newTop[i] - EndLocal(i == 0 ? -1f : 1f);
                float l = rope.magnitude;
                if (l < Tuning.MinRope || l > maxRope) { Notify("Limit reached"); return; }
                if (d < 0f && !RopeFits(rope, axisT)) { Notify("Limit reached: the ropes would be too flat"); return; }
                longest = Mathf.Max(longest, l);
            }
            if (d > 0f)
            {
                Vector3 center = twin.TransformPoint(bCenter);
                if (CastStatic(center, Vector3.down, 8f, out var fh) && center.y - bExt.y - d - fh.point.y < Tuning.FloorClearance)
                { Notify("Limit reached"); return; }
            }

            twin.position -= twin.up * d;
            if (body != null) body.position = twin.position;
            transform.position -= transform.up * d;
            if (pinTimer > 0f) Pin(pinTimer);

            topL[0] = newTop[0]; topL[1] = newTop[1];
            ropeLength += d;
            hasPrev = false;
            Save();
            UISoundPlayer.instance.PlayUISound(UISounds.winchClick, 0.6f, 1f);
            Notify("Rope: " + longest.ToString("0.00", CI) + " m");
        }

        private void ComputeHungAnchors()
        {
            if (twin == null) return;
            if (onFree) { anchorsDirty = false; return; }   // its hooks are fixed where it was hung (topL, saved)
            Matrix4x4 m = twin.localToWorldMatrix;
            float ceilY = Top + ropeLength;
            for (int i = 0; i < 2; i++)
            {
                Vector3 aW = AnchorW(m, i, ceilY, onPole ? pole : null, onPosts ? pole : null, onPosts ? post2 : null, out Vector3 nW);
                topL[i] = twin.InverseTransformPoint(aW);
                topN[i] = twin.InverseTransformDirection(nW);
            }
            anchorsDirty = false;
        }

        private void MarkAnchorsDirty(float recheckDelay)
        {
            anchorsDirty = true;
            anchorRecheckAt = Time.time + recheckDelay;
        }

        private void Hang()
        {
            Vector3 fwd = Vector3.ProjectOnPlane(twin.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(twin.up, Vector3.up);
            Vector3 heldFwd = fwd.normalized;
            Vector3 pos = twin.position;
            ShipItem postA = null, postB = null;
            bool placed = true;

            if (ProbeForHang(twin.position, out float up, out _, out float len, out var beam))
            {
                Vector3 ceil = twin.position + Vector3.up * up;
                if (beam.pole != null)
                {
                    // On a pole: straight under it, the long side along it, so both ropes reach it
                    ceil = beam.under;
                    fwd = PoleSupport.AlongForward(beam.dir, axisA, fwd.normalized);
                }
                pos = ceil - Vector3.up * (Top + len);
            }
            else if (ProbePosts(twin.position, out postA, out postB, out float hookY, out float rise))
            {
                // Between posts: its ends centred between them, the long side from post to post, the ends under the hooks
                PoleSupport.PostPoint(postA, hookY, out var pa, out _);
                PoleSupport.PostPoint(postB, hookY, out var pb, out _);
                Vector3 span = pb - pa; span.y = 0f;
                fwd = PoleSupport.AlongForward(span.normalized, axisA, fwd.normalized);
                Vector3 endsMid = (EndLocal(-1f) + EndLocal(1f)) * 0.5f; endsMid.y = 0f;
                pos = (pa + pb) * 0.5f - Quaternion.LookRotation(fwd.normalized, Vector3.up) * endsMid;
                pos.y = hookY - rise - EndLocal(1f).y;
                len = rise;
            }
            else placed = false;

            Quaternion rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            var freeW = new Vector3[2]; var freeN = new Vector3[2]; var freeP = new ShipItem[2];
            bool free = false;
            if (!placed || !RopesSteep(Matrix4x4.TRS(pos, rot, Vector3.one), Top + len, beam.pole, postA, postB))
            {
                // Nothing straight over it, or the ropes would be too flat: hooks on the surfaces around it, where it is held
                if (!ProbeFree(Matrix4x4.TRS(twin.position, Quaternion.LookRotation(heldFwd, Vector3.up), Vector3.one), freeW, freeN, freeP, out pos, out rot))
                {
                    if (placed) Notify("The ropes would be too flat here");
                    return;
                }
                free = true;
                beam = default; postA = postB = null;
                len = 0f;
                for (int i = 0; i < 2; i++)
                    len = Mathf.Max(len, Vector3.Distance(freeW[i], Matrix4x4.TRS(pos, rot, Vector3.one).MultiplyPoint3x4(EndLocal(i == 0 ? -1f : 1f))));
            }
            onFree = free;
            onPole = beam.pole != null;
            onPosts = postA != null;
            pole = onPosts ? postA : beam.pole;
            post2 = postB;

            ropeLength = len;

            if (body != null && !body.isKinematic) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            twin.SetPositionAndRotation(pos, rot);
            if (body != null) { body.position = pos; body.rotation = rot; }
            if (free)
                for (int i = 0; i < 2; i++)
                {
                    topL[i] = twin.InverseTransformPoint(freeW[i]); topN[i] = twin.InverseTransformDirection(freeN[i]);
                    freePoles[i] = freeP[i]; freeOnPole[i] = freeP[i] != null;
                }
            item.nailed = true;
            SetHungPhysics(true);
            Pin(0.5f);

            IsHung = true;
            hangMode = false;
            ResetSwing();
            MarkAnchorsDirty(0.75f);
            Save();
            VirtualCrewPatch.RegisterHammock(this);
            UISoundPlayer.instance.PlayUISound(UISounds.winchUnclick, 1f, 0.6f);
            Notify("Hammock hung");
        }

        private void Unhang()
        {
            IsHung = false;
            pole = post2 = null;
            onPole = onPosts = onFree = false;
            freePoles[0] = freePoles[1] = null;
            freeOnPole[0] = freeOnPole[1] = false;
            pinTimer = 0f;
            takeDownHold = 0f;
            takeDownHintShown = false;
            SetHungPhysics(false);
            RestoreTwinBox();
            RemoveSave();
            SetRopesVisible(false);
            ResetSwing();
            anchorsDirty = true;
            anchorRecheckAt = 0f;
            partsDirty = true;
            VirtualCrewPatch.UnregisterHammock(this);
            ResetParts();
        }

        private bool HandleTakeDown()
        {
            if (GameState.inBed != null || GameState.inCursorMenu || !item.IsLookedAt()
                || !Input.GetKey(Plugin.ModeKey.Value))
            {
                takeDownHold = 0f;
                takeDownHintShown = false;
                return false;
            }

            if (!takeDownHintShown) { takeDownHintShown = true; Notify("Taking down..."); }
            takeDownHold += Time.unscaledDeltaTime;
            if (takeDownHold < Tuning.TakeDownHold) return false;

            takeDownHold = 0f;
            takeDownHintShown = false;
            var ptr = R.Get(item, "pointedAtBy") as GoPointer;
            StartCoroutine(TakeDown(ptr));
            return true;
        }

        private IEnumerator TakeDown(GoPointer ptr)
        {
            colGraceUntil = Time.unscaledTime + 1f;
            item.nailed = false;
            Unhang();
            UISoundPlayer.instance.PlayUISound(UISounds.winchUnclick, 1f, 0.7f);

            yield return new WaitForFixedUpdate();

            if (item == null || item.held != null) yield break;
            if (ptr == null || R.Call(ptr, "GetHeldItem") != null) ptr = FindFreePointer();
            if (ptr != null) R.Call(ptr, "PickUpItem", item);
            else Notify("Hammock taken down");
        }

        private static GoPointer FindFreePointer()
        {
            var cam = Camera.main;
            GoPointer best = null;
            foreach (var p in FindObjectsOfType<GoPointer>())
            {
                if (!p.isActiveAndEnabled) continue;
                if (R.Call(p, "GetHeldItem") != null) continue;
                if (best == null || (cam != null && (p.transform == cam.transform || p.transform.IsChildOf(cam.transform)))) best = p;
            }
            return best;
        }

        private void HandleHeightInput()
        {
            if (GameState.inBed != null || GameState.inCursorMenu) return;
            if (!item.IsLookedAt()) return;

            float delta = 0f;
            if (Plugin.UseMouseWheel.Value)
            {
                float w = Input.mouseScrollDelta.y;
                if (Mathf.Abs(w) > 0.01f) delta = -Mathf.Sign(w) * Tuning.HeightStep;
            }
            if (Input.GetKeyDown(Plugin.RaiseKey.Value)) delta = -Tuning.HeightStep;
            if (Input.GetKeyDown(Plugin.LowerKey.Value)) delta = Tuning.HeightStep;
            if (delta != 0f) SetRopeLength(ropeLength + delta);
        }

        private void SetRopeLength(float target)
        {
            if (onPosts) { SetPostHeight(target - ropeLength); return; }
            if (onFree) { SetFreeHeight(target - ropeLength); return; }
            float maxLen = 5f;
            if (pole != null)
            {
                if (PoleSupport.Under(pole, twin.position, out Vector3 under) &&
                    CastStatic(under - twin.up * 0.05f, -twin.up, 8f, out var ph))
                    maxLen = ph.distance + 0.05f - Height - Tuning.FloorClearance;
            }
            else if (CastStatic(twin.position, twin.up, Top + ropeLength + 0.5f, out var ch) &&
                CastStatic(ch.point - twin.up * 0.05f, -twin.up, 8f, out var fh))
                maxLen = fh.distance + 0.05f - Height - Tuning.FloorClearance;

            float newLen = Mathf.Clamp(target, Tuning.MinRope, Mathf.Max(Tuning.MinRope, maxLen));
            float d = newLen - ropeLength;
            if (Mathf.Abs(d) < 1e-4f) { Notify("Limit reached"); return; }
            if (d < 0f && !RopesSteep(Matrix4x4.Translate(-twin.up * d) * twin.localToWorldMatrix, Top + newLen, onPole ? pole : null, null, null))
            { Notify("Limit reached: the ropes would be too flat"); return; }

            twin.position -= twin.up * d;
            if (body != null) body.position = twin.position;
            transform.position -= transform.up * d;
            if (pinTimer > 0f) Pin(pinTimer);

            ropeLength = newLen;
            hasPrev = false;
            MarkAnchorsDirty(0.3f);
            Save();
            UISoundPlayer.instance.PlayUISound(UISounds.winchClick, 0.6f, 1f);
            Notify("Rope: " + ropeLength.ToString("0.00", CI) + " m");
        }

        // Between posts the hooks slide along them: d > 0 lowers the hammock. Both posts must reach, the floor stays clear
        private void SetPostHeight(float d)
        {
            bool reach = true;
            for (int i = 0; i < 2; i++)
            {
                Vector3 endW = twin.TransformPoint(EndLocal(i == 0 ? -1f : 1f));
                var post = PostFor(endW, pole, post2);
                reach &= PoleSupport.PostPoint(post, PostHook(post, endW, out _).y - d, out _, out _);
            }
            float bottom = twin.position.y + bCenter.y - bExt.y - d;
            bool clear = !CastStatic(twin.position + Vector3.up * Top, Vector3.down, 8f, out var fh) || bottom - fh.point.y >= Tuning.FloorClearance;
            if (!reach || !clear) { Notify("Limit reached"); return; }
            if (d < 0f && !RopesSteep(Matrix4x4.Translate(-twin.up * d) * twin.localToWorldMatrix, 0f, null, pole, post2))
            { Notify("Limit reached: the ropes would be too flat"); return; }

            twin.position -= twin.up * d;
            if (body != null) body.position = twin.position;
            transform.position -= transform.up * d;
            if (pinTimer > 0f) Pin(pinTimer);

            hasPrev = false;
            MarkAnchorsDirty(0.3f);
            UISoundPlayer.instance.PlayUISound(UISounds.winchClick, 0.6f, 1f);
        }

        private Vector3 RestPos(int i) =>
            i == 0 ? restPos[0] + Vector3.up * Tuning.SleepOffset : restPos[i];

        private void FixShopScale()
        {
            Vector3 l = transform.lossyScale;
            if ((l - Vector3.one).sqrMagnitude < 1e-4f) return;
            if (Mathf.Abs(l.x) < 1e-4f || Mathf.Abs(l.y) < 1e-4f || Mathf.Abs(l.z) < 1e-4f) return;
            Vector3 s = transform.localScale;
            transform.localScale = new Vector3(s.x / l.x, s.y / l.y, s.z / l.z);
            if (!scaleLogged) { scaleLogged = true; Plugin.Log.LogInfo($"Shop hammock scale fixed: {l}"); }
        }

        private void LateUpdate()
        {
            if (item == null || restPos == null) return;
            if (!item.sold) FixShopScale();

            bool held = item.held != null;
            bool showHam = bundleRenderers.Length == 0 || IsHung || (held && hangMode);
            MirrorVisuals(showHam);
            UpdateCollider();

            if (held)
            {
                ResetParts();
                if (canHang && EnsureTwin()) DrawPreview(); else SetRopesVisible(false);
                return;
            }
            if (!IsHung) { ResetParts(); SetRopesVisible(false); return; }

            if (anchorsDirty) ComputeHungAnchors();
            else if (anchorRecheckAt > 0f && Time.time >= anchorRecheckAt) { anchorRecheckAt = 0f; ComputeHungAnchors(); }

            Vector3 pivotL = (topL[0] + topL[1]) * 0.5f;
            Vector3 swingAxisL = (topL[1] - topL[0]).normalized;
            if (swingAxisL.sqrMagnitude < 1e-4f) swingAxisL = axisA;
            Vector3 pivotW = transform.TransformPoint(pivotL);

            bool freeze = !Plugin.EnableSwing.Value || (Plugin.FreezeSwingInBed.Value && GameState.inBed == transform);
            if (freeze) { phi = phiVel = 0f; hasPrev = false; }
            else Simulate(Time.deltaTime, transform.rotation, pivotW, onPosts || onFree ? Vector3.Distance(pivotL, bCenter) : ropeLength + bExt.y);

            Quaternion S = Quaternion.AngleAxis(phi, swingAxisL);
            for (int i = 0; i < swingParts.Length; i++)
            {
                if (swingParts[i] == null) continue;
                swingParts[i].localPosition = pivotL + S * (RestPos(i) - pivotL);
                swingParts[i].localRotation = S * restRot[i];
            }
            partsDirty = true;

            Vector3 sideW = transform.TransformDirection(S * axisA);
            Vector3 hookAxisW = transform.TransformDirection(axisA);
            for (int i = 0; i < 2; i++)
            {
                float sign = i == 0 ? -1f : 1f;
                Vector3 e = EndLocal(sign);
                Vector3 bottom = transform.TransformPoint(pivotL + S * (e - pivotL));
                Vector3 top = transform.TransformPoint(topL[i]);
                ropes[i].Set(top, transform.TransformDirection(topN[i]), bottom, sideW, hookAxisW);
            }
        }

        private void UpdateCollider()
        {
            if (box == null || bundleRenderers.Length == 0) return;
            if (colliderInit && colliderHung == IsHung) return;
            Vector3 size = IsHung ? hammockBoxSize : bundleBoxSize;
            if (size.sqrMagnitude < 1e-6f) return;
            colliderInit = true;
            colliderHung = IsHung;
            box.center = Vector3.zero;
            box.size = size;
        }

        private void ResetParts()
        {
            if (!partsDirty) return;
            for (int i = 0; i < swingParts.Length; i++)
            {
                if (swingParts[i] == null) continue;
                swingParts[i].localPosition = RestPos(i);
                swingParts[i].localRotation = restRot[i];
            }
            partsDirty = false;
        }

        private static void SetR(Renderer r, bool en, int layer)
        {
            if (r == null) return;
            if (r.enabled != en) r.enabled = en;
            if (r.gameObject.layer != layer) r.gameObject.layer = layer;
        }

        private void MirrorVisuals(bool showHam)
        {
            bool vis = rootRenderer == null || rootRenderer.enabled;
            int layer = gameObject.layer;
            foreach (var r in hammockRenderers) SetR(r, vis && showHam, layer);
            foreach (var r in bundleRenderers) SetR(r, vis && !showHam, layer);

            if (rootOutline == null) SetupOutline();
            if (rootOutline == null || partOutlines == null) return;
            object col = outlineColor != null ? outlineColor.GetValue(rootOutline) : null;
            for (int i = 0; i < partOutlines.Length; i++)
            {
                var o = partOutlines[i];
                if (o == null) continue;
                bool en = rootOutline.enabled && allRenderers[i] != null && allRenderers[i].enabled;
                if (o.enabled != en) o.enabled = en;
                if (col != null) outlineColor.SetValue(o, col);
            }
        }

        private void SetupOutline()
        {
            foreach (var c in GetComponents<Behaviour>())
                if (c != null && c.GetType().Name == "Outline") { rootOutline = c; break; }
            if (rootOutline == null) return;

            var t = rootOutline.GetType();
            outlineColor = t.GetField("color", R.F);
            partOutlines = new Behaviour[allRenderers.Length];
            for (int i = 0; i < allRenderers.Length; i++)
                if (allRenderers[i] != null)
                {
                    var o = (Behaviour)allRenderers[i].gameObject.AddComponent(t);
                    o.enabled = false;
                    partOutlines[i] = o;
                }
        }

        private void Simulate(float dt, Quaternion restRot, Vector3 pivotW, float L)
        {
            if (dt <= 1e-6f) return;
            dt = Mathf.Min(dt, 0.5f);

            // With a load (player in bed or a Virtual Crew NPC lying in it) the swing gets calmer; eased in over ~1 s
            bool loaded = GameState.inBed == transform || Time.time < npcLyingUntil;
            occupied = Mathf.MoveTowards(occupied, loaded ? 1f : 0f, dt);
            float calm = Mathf.Lerp(1f, Tuning.OccupiedCalm, occupied);
            float inertia = Tuning.Inertia * calm;
            float accelInfluence = Tuning.AccelInfluence * calm;
            float damping = Tuning.Damping * Mathf.Lerp(1f, Tuning.OccupiedDamping, occupied);
            float maxAngle = Tuning.MaxAngle * Mathf.Lerp(1f, Tuning.OccupiedMaxAngle, occupied);

            if (hasPrev)
            {
                Vector3 step = pivotW - prevPivot;
                Vector3 v = step.magnitude > 5f ? prevPivotVel : step / dt;
                Vector3 a = Vector3.ClampMagnitude((v - prevPivotVel) / dt, 30f);
                pivotAcc = Vector3.Lerp(pivotAcc, a, 1f - Mathf.Exp(-dt * 8f));
                prevPivotVel = v;

                Quaternion dq = Quaternion.Inverse(prevRestRot) * restRot;
                dq.ToAngleAxis(out float ang, out Vector3 ax);
                if (ang > 180f) ang -= 360f;
                if (Mathf.Abs(ang) > 1e-4f && Mathf.Abs(ang) < 30f && !float.IsNaN(ax.x))
                {
                    phi -= ang * Vector3.Dot(ax, axisA) * inertia;
                }
            }
            else prevPivotVel = Vector3.zero;
            prevPivot = pivotW; prevRestRot = restRot; hasPrev = true;

            Vector3 gEff = Physics.gravity - pivotAcc * accelInfluence;
            Vector3 gl = Quaternion.Inverse(restRot) * gEff;
            Vector3 gA = Vector3.ProjectOnPlane(gl, axisA);
            float phiEq = Vector3.SignedAngle(Vector3.down, gA, axisA);
            L = Mathf.Max(0.3f, L);
            float w2A = gA.magnitude / L;

            int n = Mathf.Clamp(Mathf.CeilToInt(dt / 0.01f), 1, 50);
            float h = dt / n;
            for (int i = 0; i < n; i++)
            {
                phiVel += (-w2A * Mathf.Sin((phi - phiEq) * Mathf.Deg2Rad) * Mathf.Rad2Deg - damping * phiVel) * h;
                phi += phiVel * h;
            }
            Clamp(ref phi, ref phiVel, maxAngle);
        }

        private static void Clamp(ref float a, ref float v, float max)
        {
            if (a > max) { a = max; if (v > 0f) v = 0f; }
            else if (a < -max) { a = -max; if (v < 0f) v = 0f; }
        }

        private void ResetSwing() { phi = phiVel = 0f; hasPrev = false; pivotAcc = prevPivotVel = Vector3.zero; }

        private Vector3 EndLocal(float sign)
        {
            if (hasEnds) return sign < 0f ? endA : endB;
            float half = Mathf.Abs(Vector3.Dot(bExt, axisA));
            return new Vector3(bCenter.x, Top, bCenter.z) + axisA * (sign * half * 0.95f);
        }

        private Vector3 TopAnchor(Vector3 e, float sign, float ceilY)
        {
            float vert = Mathf.Max(0.02f, ceilY - e.y);
            float spread = vert * Mathf.Tan(Mathf.Clamp(Plugin.RopeAngle.Value, 0f, 70f) * Mathf.Deg2Rad);
            return new Vector3(e.x, ceilY, e.z) + axisA * (sign * spread);
        }

        // The held hammock, level, in physics space
        private bool PreviewMatrix(out Matrix4x4 m)
        {
            m = Matrix4x4.identity;
            Vector3 origin = ToPhys(transform.position);
            Quaternion physRot = ToPhys(transform.rotation);
            Vector3 fwd = Vector3.ProjectOnPlane(physRot * Vector3.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(physRot * Vector3.up, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) return false;
            m = Matrix4x4.TRS(origin, Quaternion.LookRotation(fwd.normalized, Vector3.up), Vector3.one);
            return true;
        }

        private void DrawPreview()
        {
            if (!PreviewMatrix(out var m)) { SetRopesVisible(false); return; }

            Vector3 sideW = transform.TransformDirection(axisA);
            for (int i = 0; i < 2; i++)
            {
                Vector3 e = EndLocal(i == 0 ? -1f : 1f);
                Vector3 nPhys = previewFree ? previewFreeN[i] : default;
                Vector3 pPhys = previewFree ? previewFreeA[i] : AnchorW(m, i, probeUp, previewBeam.pole, previewPostA, previewPostB, out nPhys);
                Vector3 top = ToItem(pPhys);
                Vector3 nItem = (ToItem(pPhys + nPhys) - top).normalized;
                ropes[i].Set(top, nItem, transform.TransformPoint(e), sideW, sideW);
            }
        }

        private void SetRopesVisible(bool v)
        {
            if (ropes == null) return;
            foreach (var r in ropes) r?.SetVisible(v);
        }

        private void OnDisable() => SetRopesVisible(false);

        private void OnDestroy()
        {
            VirtualCrewPatch.UnregisterHammock(this);
            if (ropes == null) return;
            foreach (var r in ropes) r?.Destroy();
        }

        private string Key()
        {
            var sp = GetComponent<SaveablePrefab>();
            return (sp == null || sp.instanceId == 0) ? null : KeyPrefix + sp.instanceId;
        }

        private void Save()
        {
            string k = Key();
            if (k != null && GameState.modData != null) GameState.modData[k] = ropeLength.ToString("R", CI) + (onPole ? PoleTag : onPosts ? PostsTag : onFree ? FreeTag + FreeHooks() : "");
        }

        private void RemoveSave()
        {
            string k = Key();
            if (k != null && GameState.modData != null) GameState.modData.Remove(k);
        }

        private void TryRestore()
        {
            if (GameState.currentlyLoading || GameState.modData == null) return;
            string k = Key();
            if (k == null) return;
            restoreDone = true;
            if (!GameState.modData.TryGetValue(k, out string s)) return;
            if (!item.nailed) { GameState.modData.Remove(k); return; }
            int free = s.IndexOf(FreeTag, StringComparison.Ordinal);
            onFree = free >= 0 && ReadFreeHooks(s.Substring(free + FreeTag.Length));
            if (free >= 0) s = s.Substring(0, free);
            onPole = s.EndsWith(PoleTag, StringComparison.Ordinal);
            onPosts = s.EndsWith(PostsTag, StringComparison.Ordinal);
            if (onPole || onPosts)
            {
                s = s.Substring(0, s.Length - (onPole ? PoleTag : PostsTag).Length);
                pole = post2 = null;
                poleWaitUntil = Time.time + 10f;
            }
            if (!float.TryParse(s, NumberStyles.Float, CI, out ropeLength)) ropeLength = Tuning.DefaultRope;
            SetHungPhysics(true);
            IsHung = true;
            ResetSwing();
            MarkAnchorsDirty(1f);
            VirtualCrewPatch.RegisterHammock(this);
            Plugin.Log.LogInfo($"Restored hung hammock {k}, rope {ropeLength:0.00}");
        }

        // The hooks of a hammock hung from surfaces, in its own frame: "a;b;normal a;normal b"
        // the last two: 1 for a hook on a wood pole
        private string FreeHooks() => Plugin.V3(topL[0]) + ";" + Plugin.V3(topL[1]) + ";" + Plugin.V3(topN[0]) + ";" + Plugin.V3(topN[1])
            + ";" + (freeOnPole[0] ? "1" : "0") + ";" + (freeOnPole[1] ? "1" : "0");

        private bool ReadFreeHooks(string s)
        {
            var p = s.Split(';');
            if (p.Length < 4) return false;
            topL[0] = Plugin.ParseV3(p[0]); topL[1] = Plugin.ParseV3(p[1]);
            topN[0] = Plugin.ParseV3(p[2]); topN[1] = Plugin.ParseV3(p[3]);
            for (int i = 0; i < 2; i++) { freeOnPole[i] = p.Length > 4 + i && p[4 + i] == "1"; freePoles[i] = null; }
            if (freeOnPole[0] || freeOnPole[1]) poleWaitUntil = Time.time + 10f;
            return topL[0] != Vector3.zero && topL[1] != Vector3.zero;
        }

        // Hung from poles: true while they hold. After a load they are looked for again (over the hammock, or a post at
        // each end); a pole picked up, removed or never found takes the hammock down (it drops where it hung).
        private bool PoleHolds()
        {
            if ((pole == null || (onPosts && post2 == null)) && Time.time < poleWaitUntil)
            {
                if (onPole)
                {
                    Vector3 top = twin.TransformPoint(new Vector3(0f, Top, 0f));
                    if (PoleSupport.Find(top, ropeLength + 0.3f, out var hit)) pole = hit.pole;
                }
                else
                {
                    Vector3 a = twin.TransformPoint(EndLocal(-1f)), b = twin.TransformPoint(EndLocal(1f));
                    var pa = PoleSupport.PostNear(a, a.y + 0.05f, PostMaxGap, null);
                    var pb = pa != null ? PoleSupport.PostNear(b, b.y + 0.05f, PostMaxGap, pa) : null;
                    if (pb != null) { pole = pa; post2 = pb; }
                }
                if (pole != null && (!onPosts || post2 != null)) { poleWaitUntil = 0f; MarkAnchorsDirty(0f); }
                return true;
            }
            if (PoleSupport.Usable(pole) && (!onPosts || PoleSupport.Usable(post2))) return true;

            colGraceUntil = Time.unscaledTime + 1f;
            item.nailed = false;
            Unhang();
            Notify("The pole is gone: hammock taken down");
            return false;
        }

        internal static void Notify(string msg)
        {
            if (NotificationUi.instance != null) NotificationUi.instance.ShowNotification(msg);
        }

        // ????????? Полная интеграция с Virtual Crew (без виляния ног!) ?????????
        public static class VirtualCrewPatch
        {
            private static bool isPatched;
            private static readonly List<HammockHanger> HungHammocks = new List<HammockHanger>();

            private static FieldInfo contextField;
            private static PropertyInfo worldBoatProp;
            private static FieldInfo actorsField;
            private static FieldInfo bodyLyingField;
            private static FieldInfo headLocalField;
            private static FieldInfo alongLocalField;
            private static FieldInfo upLocalField;

            public static void RegisterHammock(HammockHanger h)
            {
                if (h != null && !HungHammocks.Contains(h)) HungHammocks.Add(h);
            }

            public static void UnregisterHammock(HammockHanger h)
            {
                if (h != null) HungHammocks.Remove(h);
            }

            public static void TryPatch()
            {
                if (isPatched) return;

                try
                {
                    Assembly vcAsm = null;
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (string.Equals(a.GetName().Name, "SailwindVirtualCrew", StringComparison.OrdinalIgnoreCase))
                        {
                            vcAsm = a;
                            break;
                        }
                    }

                    if (vcAsm == null) return;

                    var harmony = new Harmony("HangingHammock.VirtualCrewIntegration");

                    // 1. Защита от выкидывания из кровати
                    Type locatorType = vcAsm.GetType("SailwindVirtualCrew.LocatorUtils");
                    if (locatorType != null)
                    {
                        // A rolled or lying hammock is not a bed: crew only sleep in a hung one
                        MethodInfo findBedsMethod = locatorType.GetMethod("FindBedsOnBoat", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (findBedsMethod != null)
                        {
                            var findPostfix = typeof(VirtualCrewPatch).GetMethod(nameof(FindBedsOnBoatPostfix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            harmony.Patch(findBedsMethod, postfix: new HarmonyMethod(findPostfix));
                        }

                        MethodInfo bedCheckMethod = locatorType.GetMethod("IsBedStillOnBoat", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (bedCheckMethod != null)
                        {
                            var checkPrefix = typeof(VirtualCrewPatch).GetMethod(nameof(IsBedStillOnBoatPrefix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            harmony.Patch(bedCheckMethod, prefix: new HarmonyMethod(checkPrefix));
                        }
                    }

                    // 2. Синхронизация качания тела
                    Type coordType = vcAsm.GetType("SailwindVirtualCrew.CrewNavigationCoordinator");
                    if (coordType != null)
                    {
                        MethodInfo tickMethod = coordType.GetMethod("Tick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        if (tickMethod != null)
                        {
                            var tickPrefix = typeof(VirtualCrewPatch).GetMethod(nameof(TickPrefix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            harmony.Patch(tickMethod, prefix: new HarmonyMethod(tickPrefix));
                        }
                    }

                    isPatched = true;
                }
                catch
                {
                }
            }

            public static bool IsBedStillOnBoatPrefix(Component bed, ref bool __result)
            {
                if (bed == null) return true;
                HammockHanger hanger = bed.GetComponent<HammockHanger>() ?? bed.GetComponentInParent<HammockHanger>();
                if (hanger != null)
                {
                    // hung: stays a bed while the boat moves; taken down or rolled up: the sleeper gets up
                    __result = hanger.IsHung;
                    return false;
                }
                return true;
            }

            public static void FindBedsOnBoatPostfix(List<Component> __result)
            {
                if (__result == null) return;
                __result.RemoveAll(bed =>
                {
                    if (bed == null) return false;
                    HammockHanger hanger = bed.GetComponent<HammockHanger>() ?? bed.GetComponentInParent<HammockHanger>();
                    return hanger != null && !hanger.IsHung;
                });
            }

            public static void TickPrefix(object __instance)
            {
                if (__instance == null || HungHammocks.Count == 0) return;

                try
                {
                    Type coordType = __instance.GetType();

                    if (contextField == null)
                        contextField = coordType.GetField("_context", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (contextField == null) return;
                    object ctx = contextField.GetValue(__instance);
                    if (ctx == null) return;

                    if (worldBoatProp == null)
                        worldBoatProp = ctx.GetType().GetProperty("WorldBoat", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    Transform worldBoat = worldBoatProp != null ? worldBoatProp.GetValue(ctx, null) as Transform : null;
                    if (worldBoat == null) return;

                    if (actorsField == null)
                    {
                        actorsField = coordType.GetField("_actorsByCrew", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                   ?? coordType.GetField("_actorsByOwner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    if (actorsField == null) return;

                    var dict = actorsField.GetValue(__instance) as IDictionary;
                    if (dict == null || dict.Count == 0) return;

                    foreach (DictionaryEntry entry in dict)
                    {
                        object actor = entry.Value;
                        if (actor == null) continue;

                        Type actorType = actor.GetType();

                        if (bodyLyingField == null)
                            bodyLyingField = actorType.GetField("_bodyLying", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (bodyLyingField == null) continue;

                        bool isLying = (bool)bodyLyingField.GetValue(actor);
                        if (!isLying) continue;

                        if (headLocalField == null)
                            headLocalField = actorType.GetField("_bodyLieHeadLocal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (headLocalField == null) continue;

                        Vector3 currentHeadLocal = (Vector3)headLocalField.GetValue(actor);
                        Vector3 currentHeadWorld = worldBoat.TransformPoint(currentHeadLocal);

                        HammockHanger targetHammock = null;
                        float bestDist = 2.5f;

                        for (int i = 0; i < HungHammocks.Count; i++)
                        {
                            var h = HungHammocks[i];
                            if (h == null || !h.IsHung || h.swingParts == null || h.swingParts.Length == 0 || h.swingParts[0] == null)
                                continue;

                            float d = Vector3.Distance(currentHeadWorld, h.swingParts[0].position);
                            if (d < bestDist)
                            {
                                bestDist = d;
                                targetHammock = h;
                            }
                        }

                        if (targetHammock == null) continue;
                        targetHammock.MarkNpcLying();

                        Transform sleepT = targetHammock.swingParts[0];
                        Vector3 eyes = sleepT.position;

                        // 1. Продольная ось гамака (СТРОГО вдоль полотна, исключает любые виляния ног в стороны!)
                        Vector3 longAxisW = targetHammock.transform.TransformDirection(targetHammock.axisA);
                        Vector3 toHeadW = Vector3.Project(eyes - targetHammock.transform.position, longAxisW).normalized;
                        if (toHeadW.sqrMagnitude < 0.01f) toHeadW = longAxisW;
                        Vector3 toFeetW = -toHeadW;

                        Vector3 alongLocal = worldBoat.InverseTransformDirection(toFeetW);
                        Vector3 headLocal = worldBoat.InverseTransformPoint(eyes - worldBoat.up * 0.08f + toHeadW * 0.03f);

                        // 2. Крен корпуса вместе с наклоном полотна гамака
                        Vector3 swingAxisL = (targetHammock.topL[1] - targetHammock.topL[0]).normalized;
                        if (swingAxisL.sqrMagnitude < 1e-4f) swingAxisL = targetHammock.axisA;
                        Quaternion S = Quaternion.AngleAxis(targetHammock.phi, swingAxisL);
                        Vector3 clothUpW = targetHammock.transform.TransformDirection(S * Vector3.up);
                        Vector3 upLocal = worldBoat.InverseTransformDirection(clothUpW);

                        if (alongLocalField == null)
                            alongLocalField = actorType.GetField("_bodyLieAlongLocal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (upLocalField == null)
                            upLocalField = actorType.GetField("_bodyLieUpLocal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                        // Применяем выверенные координаты
                        headLocalField.SetValue(actor, headLocal);
                        if (alongLocalField != null) alongLocalField.SetValue(actor, alongLocal);
                        if (upLocalField != null) upLocalField.SetValue(actor, upLocal);
                    }
                }
                catch
                {
                }
            }
        }
    }
}