using System;
using System.IO;
using FoodControl.Core;
using RimWorld;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// **建筑账本的游戏侧门面**：持有 `BuildingRegistry`，负责读写文件。
    ///
    /// ## 为什么账本能修掉"守卫被 debug 开局骗到"
    ///
    /// `ShelterDomain.HasAnyShelter` 的第一行是 `CountBlueprintBuilds(map) > 0` ——
    /// 而 **debug 开局自带 32 个蓝图 + 662 面墙**（实测），所以它一上来就判定
    /// "已有住处"，正常路径永远拒绝动手。
    ///
    /// 作者当时用 `_placedOnce`（会话内标志）绕过了后两行，但第一行没绕过 ——
    /// 这就是我上一轮"验证建造被守卫拦住"的根因。
    ///
    /// **账本是这个问题的正解**：不问"地图上有没有建筑"（会被开局自带的东西污染），
    /// 而问"**我们造过没有**"（账本里有没有记录）。
    ///
    /// ## 持久化
    ///
    /// 存成独立 JSON（不是 Scribe 序列化）。取舍：
    /// - **好处**：可读、可 git diff、便于调试、不涉及存档格式迁移
    /// - **代价**：不跟着存档走。换存档时会读到上一个存档的账本。
    ///
    /// 所以账本里存了 `mapUniqueId`，换图时自动弃用旧账本。
    /// </summary>
    public static class BuildingLedger
    {
        private static BuildingRegistry _reg = new BuildingRegistry();
        private static string _boundMapId = "";
        private static bool _dirty;
        private static bool _loaded;

        public static BuildingRegistry Registry
        {
            get { EnsureLoaded(); return _reg; }
        }

        /// <summary>账本文件路径（隔离存档目录下）</summary>
        public static string FilePath
        {
            get
            {
                try
                {
                    return Path.Combine(GenFilePaths.SaveDataFolderPath, "foodcontrol-buildings.json");
                }
                catch (Exception) { return null; }
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string p = FilePath;
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return;

                string json = File.ReadAllText(p);
                _reg = BuildingRegistry.FromJson(json);

                // 头部的 mapUniqueId 用来判断"这账本是不是当前这张图的"
                int i = json.IndexOf("\"mapUniqueId\"", StringComparison.Ordinal);
                if (i >= 0)
                {
                    int q1 = json.IndexOf('"', i + 13);
                    if (q1 >= 0)
                    {
                        int q2 = json.IndexOf('"', q1 + 1);
                        if (q2 > q1) _boundMapId = json.Substring(q1 + 1, q2 - q1 - 1);
                    }
                }

                try { Log.Message("[FoodControl] 建筑账本已载入：" + _reg.Summary()); } catch (Exception) { }
            }
            catch (Exception e)
            {
                try { Log.Warning("[FoodControl] 建筑账本载入失败（当作空账本）：" + e.Message); } catch (Exception) { }
                _reg = new BuildingRegistry();
            }
        }

        /// <summary>
        /// 算出**当前这张地图的身份**。
        ///
        /// ⚠ 一开始我只用了 `map.uniqueID` —— **实测撞墙**：
        ///   每开一个新殖民地，`uniqueID` **都是 0**，所以"换了图"根本判不出来，
        ///   上一局的账本会被当成这一局的（坐标会指到完全无关的地方）。
        ///
        /// 所以身份用**世界种子 + 地图 id** 一起算：
        /// 前者区分"不同的一局"，后者区分"同一局的不同地图"（比如远行队的地图）。
        /// 两者任一变化都说明"这不是同一张图了"。
        /// </summary>
        public static string MapIdentity(Map map)
        {
            if (map == null) return "";
            string seed = "";
            string mapId = "";
            try
            {
                if (Find.World != null && Find.World.info != null)
                    seed = Find.World.info.seedString ?? "";
            }
            catch (Exception) { }
            try { mapId = map.uniqueID.ToString(); }
            catch (Exception) { }

            if (string.IsNullOrEmpty(seed) && string.IsNullOrEmpty(mapId)) return "";
            return seed + "#" + mapId;
        }

        /// <summary>
        /// 确保账本绑定到**当前这张地图**。换了图就清空重来 ——
        /// 否则会把上一张图的坐标当成这一张的（会指到完全无关的地方）。
        /// </summary>
        public static void BindToMap(Map map)
        {
            EnsureLoaded();
            if (map == null) return;

            string id = MapIdentity(map);
            if (string.IsNullOrEmpty(id)) return;

            if (_boundMapId != id)
            {
                if (_reg.Count > 0)
                {
                    try
                    {
                        Log.Message("[FoodControl] 地图变了（" + _boundMapId + " → " + id + "），清空旧账本");
                    }
                    catch (Exception) { }
                }
                _reg.Clear();
                _boundMapId = id;
                _dirty = true;
            }
        }

        /// <summary>**我们造过住处吗** —— 守卫用这个，而不是数地图上的建筑</summary>
        public static bool HasAnyOfOurs()
        {
            EnsureLoaded();
            return _reg.Count > 0;
        }

        /// <summary>我们造过的墙/门总数（现存）</summary>
        public static int AliveStructureCells()
        {
            EnsureLoaded();
            int n = 0;
            var cells = _reg.AllCells();
            for (int i = 0; i < cells.Count; i++)
            {
                RegistryCell c = cells[i];
                if (c == null || !c.StillThere) continue;
                if (c.Role == "wall" || c.Role == "door") n++;
            }
            return n;
        }

        /// <summary>记一笔放置（由 `TemplateBuildDomain` / `ShelterDomain` 调用）</summary>
        public static BuildingRecord Record(
            Map map, string templateId, int originX, int originZ, int rotation, bool mirrored,
            System.Collections.Generic.List<RegistryCell> cells)
        {
            EnsureLoaded();
            BindToMap(map);
            if (cells == null || cells.Count == 0) return null;

            var rec = new BuildingRecord
            {
                Id = "b" + (_reg.Count + 1) + "-" + DateTime.UtcNow.Ticks.ToString("x"),
                TemplateId = templateId ?? "",
                OriginX = originX,
                OriginZ = originZ,
                Rotation = rotation,
                Mirrored = mirrored,
                Day = 0,
                Tick = 0,
            };
            try
            {
                if (map != null)
                {
                    rec.Tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
                    rec.Day = GenLocalDate.DayOfYear(map);
                }
            }
            catch (Exception) { }

            rec.Cells = new System.Collections.Generic.List<RegistryCell>(cells);
            _reg.Add(rec);
            _dirty = true;
            Save();

            try { Log.Message("[FoodControl] 记账：" + rec.ToString()); } catch (Exception) { }
            return rec;
        }

        /// <summary>用地图现状校验（只标记"没了"，不删除）</summary>
        public static int Reconcile(Map map)
        {
            EnsureLoaded();
            if (map == null) return 0;

            int changed = _reg.Reconcile((x, z, role) =>
            {
                try
                {
                    var c = new IntVec3(x, 0, z);
                    if (!c.InBounds(map)) return false;
                    var list = c.GetThingList(map);
                    for (int i = 0; i < list.Count; i++)
                    {
                        Thing t = list[i];
                        if (t?.def == null) continue;
                        bool wallLike = t.def.IsBuildingArtificial
                            && t.def.defName.IndexOf("Wall", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool doorLike = t is Building_Door || (t.def.defName.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (role == "wall" && wallLike) return true;
                        if (role == "door" && doorLike) return true;
                        // 蓝图/框架也算"还在计划中"
                        if ((t is Blueprint || t is Frame)
                            && (wallLike || doorLike || t.def.defName.IndexOf("Wall", StringComparison.OrdinalIgnoreCase) >= 0))
                            return true;
                    }
                    return false;
                }
                catch (Exception) { return false; }
            });

            if (changed > 0) { _dirty = true; Save(); }
            return changed;
        }

        /// <summary>写盘（失败不抛 —— 记账不该让游戏崩）</summary>
        public static bool Save()
        {
            if (!_dirty) return true;
            try
            {
                string p = FilePath;
                if (string.IsNullOrEmpty(p)) return false;

                string json = _reg.ToJsonWithMapId(_boundMapId);
                File.WriteAllText(p, json);
                _dirty = false;
                return true;
            }
            catch (Exception e)
            {
                try { Log.Warning("[FoodControl] 建筑账本写盘失败：" + e.Message); } catch (Exception) { }
                return false;
            }
        }

        /// <summary>测试用：清空</summary>
        public static void Reset()
        {
            _reg.Clear();
            _boundMapId = "";
            _dirty = true;
            _loaded = true;
        }

        /// <summary>一行摘要（日志/探针用）</summary>
        public static string Describe()
        {
            EnsureLoaded();
            return _reg.Summary() + "（地图 " + _boundMapId + "）";
        }
    }
}
