using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HangingHammock
{
    internal static class HammockShops
    {
        private const string SpawnerName = "HangingHammock_ShopSpawner";

        // Места гамаков у мебельщиков: путь_родителя|локальная_позиция|локальный_поворот
        private const string Placements =
            "island 1 A (gold rock) scenery|1553.865,6.174,-363.536|0,52.706,0;" +
            "island 15 M (Fort) scenery|-8.534,2.587,56.261|0,205.018,0;" +
            "island 9 E (dragon cliffs) scenery|-87.994,4.17,-540.262|0,135.937,0";

        private struct Spot { public Vector3 pos, euler; }

        private sealed class Shop
        {
            public Transform parent;
            public ShopItemSpawner anchor, ours;
            public bool warned;
        }

        private static readonly Dictionary<string, Shop> shops = new Dictionary<string, Shop>();
        private static Dictionary<string, Spot> spots;
        private static float timer;

        public static void Tick()
        {
            timer -= Time.unscaledDeltaTime;
            if (timer > 0f) return;
            timer = 5f;
            if (!GameState.playing) return;

            // Мебельщик = магазин, где продаётся кровать
            foreach (var s in Resources.FindObjectsOfTypeAll<ShopItemSpawner>())
            {
                if (s == null || !s.gameObject.scene.IsValid() || s.name == SpawnerName) continue;
                if (s.itemPrefab == null || s.itemPrefab.GetComponent<ShipItemBed>() == null) continue;
                if (s.itemPrefab.GetComponent<HammockHanger>() != null) continue;
                string key = Key(s);
                if (!shops.TryGetValue(key, out var shop) || shop.anchor == null)
                    shops[key] = new Shop { parent = s.transform.parent, anchor = s };
            }

            foreach (var kv in shops)
            {
                var shop = kv.Value;
                if (shop.anchor == null || shop.ours != null) continue;
                if (Spots().TryGetValue(kv.Key, out var spot)) shop.ours = Create(shop, spot, kv.Key);
                else if (!shop.warned)
                {
                    shop.warned = true;
                    Plugin.Log.LogInfo($"Furniture shop without hammock spot: '{kv.Key}'");
                }
            }
        }

        private static string Key(ShopItemSpawner s) =>
            Diagnostics.Path(s.transform.parent != null ? s.transform.parent : s.transform);

        private static ShopItemSpawner Create(Shop shop, Spot spot, string key)
        {
            var go = new GameObject(SpawnerName);
            go.SetActive(false);
            go.transform.SetParent(shop.parent, false);
            go.transform.localPosition = spot.pos;
            go.transform.localRotation = Quaternion.Euler(spot.euler);

            var sp = go.AddComponent<ShopItemSpawner>();
            sp.itemPrefab = HammockFactory.Prefab;
            sp.availableAtNight = shop.anchor.availableAtNight;
            sp.priceMult = shop.anchor.priceMult;
            go.SetActive(true);
            Plugin.Log.LogInfo($"Hammock shop spawner in '{key}'");
            return sp;
        }

        private static Dictionary<string, Spot> Spots()
        {
            if (spots != null) return spots;
            spots = new Dictionary<string, Spot>();
            foreach (var entry in Placements.Split(';'))
            {
                var p = entry.Split('|');
                if (p.Length != 3 || p[0].Trim().Length == 0) continue;
                spots[p[0].Trim()] = new Spot { pos = Plugin.ParseV3(p[1]), euler = Plugin.ParseV3(p[2]) };
            }
            return spots;
        }

#if DEBUG
        /// F10: перенести гамак ближайшего мебельщика в точку прицела.
        /// Не сохраняется — строку из лога вставить в Placements выше
        public static void CaptureSpot()
        {
            var cam = Camera.main;
            if (cam == null) return;
            if (!HammockHanger.CastStatic(cam.transform.position, cam.transform.forward, 8f, out var hit) || hit.normal.y < 0.7f)
            {
                HammockHanger.Notify("Aim at the floor");
                return;
            }

            Shop best = null; string bestKey = null; float bd = 30f;
            foreach (var kv in shops)
            {
                if (kv.Value.anchor == null) continue;
                float d = Vector3.Distance(kv.Value.anchor.transform.position, hit.point);
                if (d < bd) { bd = d; best = kv.Value; bestKey = kv.Key; }
            }
            if (best == null) { HammockHanger.Notify("No furniture shop nearby"); return; }
            if (best.ours == null) best.ours = Create(best, default, bestKey);

            Quaternion q = Quaternion.Euler(0f, cam.transform.eulerAngles.y + 90f, 0f);
            Vector3 pos = hit.point + Vector3.up * (HammockFactory.BundleSize.y * 0.5f + 0.03f);
            best.ours.transform.SetPositionAndRotation(pos, q);
            Respawn(best.ours);

            HammockHanger.Notify("Spot logged");
            Plugin.Log.LogInfo($"Shop spot: {bestKey}|{Plugin.V3(best.ours.transform.localPosition)}|{Plugin.V3(best.ours.transform.localEulerAngles)}");
        }

        private static void Respawn(ShopItemSpawner sp)
        {
            var it = R.Get(sp, "item") as ShipItem;
            if (it != null && !it.sold) Object.Destroy(it.gameObject);
            R.Set(sp, "item", null);
            R.Set(sp, "itemAvailableAtShop", false);
            R.Set(sp, "respawnTimer", 0f);
            R.Set(sp, "timer", 0f);
        }
#endif
    }

#if DEBUG
    /// F9: выдать гамак в руки
    internal static class DevSpawn
    {
        public static IEnumerator Run()
        {
            if (!GameState.playing || GameState.currentlyLoading) yield break;
            var cam = Camera.main;
            if (cam == null) yield break;

            var ptr = FindPointer(cam);
            if (ptr == null) { HammockHanger.Notify("No free GoPointer (hands busy?)"); yield break; }

            Vector3 fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            var go = Object.Instantiate(HammockFactory.Prefab,
                cam.transform.position + cam.transform.forward * 1.5f, Quaternion.LookRotation(fwd.normalized));
            var world = GameObject.Find("_shifting world");
            if (world != null) go.transform.SetParent(world.transform, true);

            var item = go.GetComponent<ShipItem>();
            item.sold = true;

            float t = 0f;
            while (item != null && item.GetItemRigidbody() == null && t < 5f) { t += Time.unscaledDeltaTime; yield return null; }
            if (item == null || item.GetItemRigidbody() == null) { Plugin.Log.LogError("Twin was not created"); yield break; }
            for (int i = 0; i < 4; i++) yield return new WaitForFixedUpdate();
            yield return new WaitForEndOfFrame();

            R.Call(go.GetComponent<SaveablePrefab>(), "RegisterToSave");
            R.Call(ptr, "PickUpItem", item);
        }

        private static GoPointer FindPointer(Camera cam)
        {
            GoPointer best = null;
            foreach (var p in Object.FindObjectsOfType<GoPointer>())
            {
                if (!p.isActiveAndEnabled) continue;
                if (R.Call(p, "GetHeldItem") != null) continue;
                if (best == null || p.transform.IsChildOf(cam.transform) || p.transform == cam.transform) best = p;
            }
            return best;
        }
    }
#endif
}