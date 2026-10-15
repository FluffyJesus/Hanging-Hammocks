using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace HangingHammock
{
    /// Внутренние константы
    internal static class Tuning
    {
        public const string ItemName = "hammock";
        public const float Price = 220f;
        public const float Mass = 4f;
        public const float BundleHold = 1.2f, HangHold = 1.8f;
        public const float TakeDownHold = 0.5f, HeightStep = 0.05f;
        public const float MaxCeiling = 3f, DefaultRope = 0.6f, MinRope = 0.1f, FloorClearance = 0.25f;
        public const float SleepOffset = -0.35f;
        public const float RopeThickness = 0.03f, KnotSize = 0.07f;
        public const float Damping = 0.35f, MaxAngle = 35f, Inertia = 1f, AccelInfluence = 1f;
    }

    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "fluffyjesus.sailwind.hanginghammock";
        public const string NAME = "Hanging Hammock";
        public const string VERSION = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyCode> ModeKey, RaiseKey, LowerKey;
        internal static ConfigEntry<bool> UseMouseWheel;
        internal static ConfigEntry<float> RopeAngle;
        internal static ConfigEntry<bool> EnableSwing, FreezeSwingInBed, SolidWhenHung;
        internal static ConfigEntry<bool> ShopEnable;
        internal static ConfigEntry<int> PrefabIndex;

        private float buildTimer;

        private void Awake()
        {
            Log = Logger;

            ModeKey       = Config.Bind("1 Controls", "ModeKey", KeyCode.Y,
                "In hands: toggle rolled bundle / unrolled hammock. Looking at a hung hammock: hold to take it down");
            UseMouseWheel = Config.Bind("1 Controls", "UseMouseWheel", true, "Mouse wheel on a hung hammock changes its height");
            RaiseKey      = Config.Bind("1 Controls", "RaiseKey", KeyCode.PageUp, "Raise hung hammock");
            LowerKey      = Config.Bind("1 Controls", "LowerKey", KeyCode.PageDown, "Lower hung hammock");

            RopeAngle        = Config.Bind("2 Hammock", "RopeAngle", 30f, "Rope angle from vertical, degrees (0 = straight down)");
            EnableSwing      = Config.Bind("2 Hammock", "EnableSwing", true, "Hammock swings with the boat");
            FreezeSwingInBed = Config.Bind("2 Hammock", "FreezeSwingInBed", false, "Stop swinging while you lie in the hammock (against motion sickness)");
            SolidWhenHung    = Config.Bind("2 Hammock", "SolidWhenHung", false,
                "false = you and items pass through a hung hammock. true = it has a collider fitted to its shape (items can rest on it)");

            ShopEnable  = Config.Bind("3 General", "SellInShops", true, "Sell hammocks at furniture shops");
            PrefabIndex = Config.Bind("3 General", "PrefabIndex", 900,
                "Item index in the prefab directory. Change only on conflict with another item mod, and NEVER after hammocks exist in your save");

            Log.LogInfo($"{NAME} {VERSION} loaded");
        }

        private void Update()
        {
            if (!HammockFactory.Ready)
            {
                if (HammockFactory.Failed) return;
                buildTimer -= Time.unscaledDeltaTime;
                if (buildTimer <= 0f) { buildTimer = 1f; HammockFactory.TryBuild(); }
                return;
            }

            if (ShopEnable.Value) HammockShops.Tick();

#if DEBUG
            // Инструменты разработчика: только в сборке dotnet build -c Debug
            if (Input.GetKeyDown(KeyCode.F9)) StartCoroutine(DevSpawn.Run());
            if (Input.GetKeyDown(KeyCode.F10)) HammockShops.CaptureSpot();
#endif
        }

        internal static Vector3 ParseV3(string s)
        {
            var p = s.Split(',');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            try { return new Vector3(float.Parse(p[0], ci), float.Parse(p[1], ci), float.Parse(p[2], ci)); }
            catch { return Vector3.zero; }
        }

        internal static string V3(Vector3 v)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return v.x.ToString("0.###", ci) + "," + v.y.ToString("0.###", ci) + "," + v.z.ToString("0.###", ci);
        }
    }
}