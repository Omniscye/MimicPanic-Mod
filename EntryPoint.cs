using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using System.Collections.Generic;
using UnityEngine;

// Empress — Mimic Panic (v1.0.0)
namespace Empress.MimicPanic
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class MimicPanicPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "Empress.MimicPanic";
        public const string PluginName = "Mimic Panic";
        public const string PluginVersion = "1.1.3";

        internal static MimicPanicPlugin Instance { get; private set; } = null!;
        internal new static ManualLogSource Logger => Instance._logger;
        private ManualLogSource _logger => base.Logger;

        internal Harmony? Harmony { get; private set; }

        internal static ConfigEntry<float> ChanceItemsPercent = null!;
        internal static ConfigEntry<float> ChanceValuablesPercent = null!;
        internal static ConfigEntry<bool> EnableItems = null!;
        internal static ConfigEntry<bool> EnableValuables = null!;
        internal static ConfigEntry<bool> BlockInStartRoom = null!;
        internal static ConfigEntry<bool> RollOncePerObject = null!;
        internal static ConfigEntry<bool> VerboseGrabs = null!;
        internal static ConfigEntry<bool> LogRolls = null!;
        internal static ConfigEntry<bool> AlwaysPassRoll = null!;

        private static readonly Queue<PendingTransform> _queue = new Queue<PendingTransform>(64);
        private static readonly HashSet<int> _queuedOrDoneKeys = new HashSet<int>();
        private static readonly HashSet<int> _attemptedKeys = new HashSet<int>();

        private struct PendingTransform
        {
            public GameObject go;
            public Vector3 pos;
            public SourceKind kind;
            public int key;
        }

        internal enum SourceKind { Item, Valuable }

        private void Awake()
        {
            Instance = this;

            EnableItems = Config.Bind("General", "EnableItems", true, "Allow items (ItemAttributes) to transform on grab.");
            EnableValuables = Config.Bind("General", "EnableValuables", true, "Allow valuables (ValuableObject) to transform on grab.");

            var percentRange = new AcceptableValueRange<float>(0f, 100f);
            ChanceItemsPercent = Config.Bind(
                "Chances", "ItemsPercent", 5.0f,
                new ConfigDescription("Percent chance an item becomes an enemy when grabbed.", percentRange));
            ChanceValuablesPercent = Config.Bind(
                "Chances", "ValuablesPercent", 5.0f,
                new ConfigDescription("Percent chance a valuable becomes an enemy when grabbed.", percentRange));

            BlockInStartRoom = Config.Bind("Safety", "BlockInStartRoom", true, "Block transformations in start/extraction rooms.");
            RollOncePerObject = Config.Bind("Safety", "RollOncePerObject", true, "If true, each object only rolls once — first time it’s grabbed.");

            VerboseGrabs = Config.Bind("Debug", "VerboseGrabs", false, "Log every grab with classification and IDs.");
            LogRolls = Config.Bind("Debug", "LogRolls", true, "Log RNG rolls and thresholds on attempted transforms.");
            AlwaysPassRoll = Config.Bind("Debug", "AlwaysPassRoll", false, "Force the RNG to pass (host-only). Use for quick verification.");

            this.gameObject.transform.parent = null;
            this.gameObject.hideFlags = HideFlags.HideAndDontSave;

            Patch();
            Logger.LogInfo($"{Info.Metadata.GUID} v{Info.Metadata.Version} loaded. Don’t get handsy with the loot.");
        }

        private void Update()
        {
            if (!LevelReady()) return;

            int guard = 32;
            while (_queue.Count > 0 && guard-- > 0)
            {
                var p = _queue.Dequeue();
                TryTransformNow(p);
            }
        }

        private static bool LevelReady()
        {
            return LevelGenerator.Instance != null && LevelGenerator.Instance.Generated;
        }

        internal void Patch()
        {
            Harmony ??= new Harmony(Info.Metadata.GUID);
            Harmony.PatchAll(typeof(PhysGrabObject_GrabStarted_Patch));
            Harmony.PatchAll(typeof(PhysGrabObject_GrabPlayerAddRPC_Patch));
        }

        internal void Unpatch()
        {
            Harmony?.UnpatchSelf();
        }

        internal static void RequestTransformOnGrab(PhysGrabObject pgo, SourceKind kind)
        {
            if (pgo == null) return;
            var go = pgo.gameObject;

            if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
            if (SemiFunc.RunIsShop()) return;

            if (BlockInStartRoom.Value)
            {
                var rvc = go.GetComponent<RoomVolumeCheck>();
                if (rvc != null && rvc.CurrentRooms != null)
                {
                    foreach (var rv in rvc.CurrentRooms)
                    {
                        if (rv != null && rv.Extraction)
                        {
                            if (VerboseGrabs.Value)
                                Logger.LogInfo($"[MimicPanic] Blocked in Extraction room: \"{go.name}\".");
                            return;
                        }
                    }
                }
            }

            int key = MakeKey(go);
            if (RollOncePerObject.Value && _attemptedKeys.Contains(key))
                return;

            _attemptedKeys.Add(key);

            float need = kind == SourceKind.Item ? Mathf.Clamp(ChanceItemsPercent.Value, 0f, 100f)
                                                 : Mathf.Clamp(ChanceValuablesPercent.Value, 0f, 100f);
            float roll = AlwaysPassRoll.Value ? 0f : Random.Range(0f, 100f);

            if (LogRolls.Value)
            {
                var pv = go.GetComponent<PhotonView>();
                int vid = pv != null ? pv.ViewID : 0;
                Logger.LogInfo($"[MimicPanic] Roll {roll:F2} <= {need:F2}? kind={kind} name=\"{go.name}\" PV:{vid}");
            }

            if (roll > need) return;

            if (_queuedOrDoneKeys.Contains(key)) return;
            _queuedOrDoneKeys.Add(key);

            _queue.Enqueue(new PendingTransform
            {
                go = go,
                pos = go.transform.position,
                kind = kind,
                key = key
            });
        }

        private static int MakeKey(GameObject go)
        {
            var pv = go.GetComponent<PhotonView>();
            if (pv != null && pv.ViewID != 0) return pv.ViewID;
            unchecked { return (go.GetInstanceID() * -1) - 7; }
        }

        private static void TryTransformNow(PendingTransform p)
        {
            if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
            if (p.go == null) return;
            if (EnemyDirector.instance == null || LevelGenerator.Instance == null) return;

            var pool = CollectEnemySetups();
            if (pool.Count == 0)
            {
                Logger.LogWarning("[MimicPanic] No enemy setups available — skipping transform.");
                return;
            }

            var pick = pool[Random.Range(0, pool.Count)];
            if (pick == null)
            {
                Logger.LogWarning("[MimicPanic] Picked null enemy setup — skipping transform.");
                return;
            }

            LevelGenerator.Instance.EnemySpawn(pick, p.pos);
            DestroyNetworked(p.go);
            Logger.LogInfo($"[MimicPanic] OnGrab transform: {p.kind} at {p.pos} -> enemy \"{pick.name}\".");
        }

        private static List<EnemySetup> CollectEnemySetups()
        {
            var list = new List<EnemySetup>(32);
            var dir = EnemyDirector.instance;

            if (dir.enemiesDifficulty1 != null) list.AddRange(dir.enemiesDifficulty1);
            if (dir.enemiesDifficulty2 != null) list.AddRange(dir.enemiesDifficulty2);
            if (dir.enemiesDifficulty3 != null) list.AddRange(dir.enemiesDifficulty3);

            for (int i = list.Count - 1; i-- > 0;)
                if (list[i] == null) list.RemoveAt(i);

            return list;
        }

        private static void DestroyNetworked(GameObject go)
        {
            if (go == null) return;

            if (SemiFunc.IsMultiplayer())
            {
                var pv = go.GetComponent<PhotonView>();
                if (pv != null && pv.ViewID != 0)
                    PhotonNetwork.Destroy(go);
                else
                    Object.Destroy(go);
            }
            else
            {
                Object.Destroy(go);
            }
        }

        [HarmonyPatch(typeof(PhysGrabObject), nameof(PhysGrabObject.GrabStarted))]
        private static class PhysGrabObject_GrabStarted_Patch
        {
            private static void Postfix(PhysGrabObject __instance, PhysGrabber player)
            {
                if (__instance == null) return;
                var go = __instance.gameObject;

                bool isValuable = go.GetComponent<ValuableObject>() != null;
                bool isItem = !isValuable && go.GetComponent<ItemAttributes>() != null;

                if (VerboseGrabs.Value)
                {
                    var pv = go.GetComponent<PhotonView>();
                    int vid = pv != null ? pv.ViewID : 0;
                    MimicPanicPlugin.Logger.LogInfo($"[MimicPanic] Grab: \"{go.name}\" PV:{vid} isValuable:{isValuable} isItem:{isItem}");
                }

                if (isValuable && EnableValuables.Value)
                {
                    MimicPanicPlugin.RequestTransformOnGrab(__instance, SourceKind.Valuable);
                    return;
                }

                if (isItem && EnableItems.Value)
                {
                    MimicPanicPlugin.RequestTransformOnGrab(__instance, SourceKind.Item);
                }
            }
        }

        [HarmonyPatch(typeof(PhysGrabObject), "GrabPlayerAddRPC")]
        private static class PhysGrabObject_GrabPlayerAddRPC_Patch
        {
            private static void Postfix(PhysGrabObject __instance, int photonViewID)
            {
                if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

                var go = __instance.gameObject;

                bool isValuable = go.GetComponent<ValuableObject>() != null;
                bool isItem = !isValuable && go.GetComponent<ItemAttributes>() != null;

                if (isValuable && EnableValuables.Value)
                {
                    RequestTransformOnGrab(__instance, SourceKind.Valuable);
                    return;
                }

                if (isItem && EnableItems.Value)
                {
                    RequestTransformOnGrab(__instance, SourceKind.Item);
                }
            }
        }
    }
}