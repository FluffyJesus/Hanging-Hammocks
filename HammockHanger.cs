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

                canHang = hangMode && ProbeForHang(ToPhys(transform.position), out probeUp, out _, out _);
                if (canHang && !lastCanHang) Notify("Release to hang the hammock");
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

        private bool ProbeForHang(Vector3 origin, out float up, out float room, out float len)
        {
            up = room = len = 0f;
            if (!CastStatic(origin, Vector3.up, Tuning.MaxCeiling, out var ch)) return false;
            if (Mathf.Abs(ch.normal.y) < 0.5f) return false;
            up = ch.distance;
            room = CastStatic(ch.point + Vector3.down * 0.05f, Vector3.down, 8f, out var fh) ? fh.distance + 0.05f : 8f;
            float maxLen = room - Height - Tuning.FloorClearance;
            if (maxLen < Tuning.MinRope) return false;
            len = Mathf.Clamp(Tuning.DefaultRope, Tuning.MinRope, maxLen);
            return true;
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

        private void ComputeHungAnchors()
        {
            if (twin == null) return;
            Matrix4x4 m = twin.localToWorldMatrix;
            float ceilY = Top + ropeLength;
            for (int i = 0; i < 2; i++)
            {
                float sign = i == 0 ? -1f : 1f;
                Vector3 e = EndLocal(sign);
                topL[i] = twin.InverseTransformPoint(FindAnchor(m, e, TopAnchor(e, sign, ceilY), out Vector3 nW));
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
            if (!ProbeForHang(twin.position, out float up, out _, out float len)) return;

            Vector3 ceil = twin.position + Vector3.up * up;
            Vector3 fwd = Vector3.ProjectOnPlane(twin.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(twin.up, Vector3.up);
            Quaternion rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);

            ropeLength = len;
            Vector3 pos = ceil - Vector3.up * (Top + ropeLength);

            if (body != null && !body.isKinematic) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            twin.SetPositionAndRotation(pos, rot);
            if (body != null) { body.position = pos; body.rotation = rot; }
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
            float maxLen = 5f;
            if (CastStatic(twin.position, twin.up, Top + ropeLength + 0.5f, out var ch) &&
                CastStatic(ch.point - twin.up * 0.05f, -twin.up, 8f, out var fh))
                maxLen = fh.distance + 0.05f - Height - Tuning.FloorClearance;

            float newLen = Mathf.Clamp(target, Tuning.MinRope, Mathf.Max(Tuning.MinRope, maxLen));
            float d = newLen - ropeLength;
            if (Mathf.Abs(d) < 1e-4f) { Notify("Limit reached"); return; }

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
            else Simulate(Time.deltaTime, transform.rotation, pivotW, ropeLength + bExt.y);

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

        private void DrawPreview()
        {
            Vector3 origin = ToPhys(transform.position);
            Quaternion physRot = ToPhys(transform.rotation);
            Vector3 fwd = Vector3.ProjectOnPlane(physRot * Vector3.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(physRot * Vector3.up, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) { SetRopesVisible(false); return; }
            Matrix4x4 m = Matrix4x4.TRS(origin, Quaternion.LookRotation(fwd.normalized, Vector3.up), Vector3.one);

            Vector3 sideW = transform.TransformDirection(axisA);
            for (int i = 0; i < 2; i++)
            {
                float sign = i == 0 ? -1f : 1f;
                Vector3 e = EndLocal(sign);
                float ceilY = Mathf.Max(e.y + 0.05f, probeUp);
                Vector3 pPhys = FindAnchor(m, e, TopAnchor(e, sign, ceilY), out Vector3 nPhys);
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
            if (k != null && GameState.modData != null) GameState.modData[k] = ropeLength.ToString("R", CI);
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
            if (!float.TryParse(s, NumberStyles.Float, CI, out ropeLength)) ropeLength = Tuning.DefaultRope;
            SetHungPhysics(true);
            IsHung = true;
            ResetSwing();
            MarkAnchorsDirty(1f);
            VirtualCrewPatch.RegisterHammock(this);
            Plugin.Log.LogInfo($"Restored hung hammock {k}, rope {ropeLength:0.00}");
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
                if (hanger != null && hanger.IsHung)
                {
                    __result = true;
                    return false;
                }
                return true;
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