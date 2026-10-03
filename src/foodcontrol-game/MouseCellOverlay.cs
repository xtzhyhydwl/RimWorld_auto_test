using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using RimWorld;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// **鼠标格坐标显示**（用户 2026-10-03 要求）。
    ///
    /// ## 为什么需要它（我的工具设计错了）
    ///
    /// 用户原话：
    /// > 「你应该给我做一个命令，就是我能够**实际操作**的那种……在 architect 的 order 下边，
    /// >  我可以直接把那个建筑**框出来**。要不然实际上你告诉我 XY，
    /// >  我又不是 AI，我这个地图上也不会显示 XY 这个，很麻烦。
    /// >  要不然就是你给我做一个**能够实时显示鼠标停留地点 XY 的**那种小插件，那也可以，
    /// >  那我可以把 XY 读出来。」
    ///
    /// **我之前的做法是错的**：我让用户"告诉我 x, z, 宽, 高" ——
    /// 那是按"**我能调用**"设计的，而不是按"**用户能操作**"设计的。
    /// 玩家在游戏里**看不到坐标**，让他报坐标等于让他拿尺子量。
    ///
    /// ## 这个覆盖层做什么
    ///
    /// 在鼠标旁边显示当前指着的格子信息：
    /// - 格坐标（用户要的核心）
    /// - 地形 + 肥力
    /// - 有没有屋顶
    /// - 属于哪个区（种植区/储藏区）
    /// - 有没有建筑
    ///
    /// ⚠ 只在**有地图**时显示；开关可通过 `FoodControlSettings`（暂时用静态字段 + 快捷键）。
    /// </summary>
    public class MouseCellOverlay : GameComponent
    {
        /// <summary>总开关（默认开 —— 用户要的就是"实时显示"）</summary>
        public static bool Enabled = true;

        /// <summary>显示位置偏移（避免压住鼠标）</summary>
        public static float OffsetX = 18f;
        public static float OffsetY = 18f;

        private static UnityEngine.GUIStyle _style;
        private static Texture2D _bg;

        public MouseCellOverlay(Verse.Game game)
        {
        }

        public override void GameComponentOnGUI()
        {
            if (!Enabled) return;
            if (Current.ProgramState != ProgramState.Playing) return;

            Map map = Verse.Find.CurrentMap;
            if (map == null) return;

            IntVec3 cell;
            try
            {
                // ⚠ 用 `UI.MouseCell()`（已处理相机/缩放），别自己拿屏幕坐标算
                cell = UI.MouseCell();
                if (!cell.InBounds(map)) return;
            }
            catch (Exception) { return; }

            string text = DescribeCell(map, cell);
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                DrawLabel(text);
            }
            catch (Exception) { }
        }

        /// <summary>把一格的信息写成多行文本</summary>
        public static string DescribeCell(Map map, IntVec3 cell)
        {
            var sb = new StringBuilder();

            // ① 坐标（用户最需要的）
            sb.Append("格 (").Append(cell.x).Append(", ").Append(cell.z).Append(")");

            // ② 地形 + 肥力
            try
            {
                TerrainDef terr = cell.GetTerrain(map);
                if (terr != null)
                {
                    sb.Append("\n地形 ").Append(terr.label ?? terr.defName);
                    try
                    {
                        float f = map.fertilityGrid.FertilityAt(cell);
                        sb.Append(" 肥").Append(f.ToString("F2"));
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }

            // ③ 屋顶（种植区最怕这个）
            try
            {
                bool roofed = map.roofGrid.Roofed(cell);
                sb.Append("\n屋顶 ").Append(roofed ? "★有（植物不生长）" : "无");
            }
            catch (Exception) { }

            // ④ 属于哪个区
            try
            {
                Zone z = map.zoneManager.ZoneAt(cell);
                if (z is Zone_Growing) sb.Append("\n区 种植区#").Append(z.ID);
                else if (z is Zone_Stockpile) sb.Append("\n区 储藏区#").Append(z.ID);
                else if (z != null) sb.Append("\n区 ").Append(z.GetType().Name).Append("#").Append(z.ID);
                else sb.Append("\n区 无");
            }
            catch (Exception) { }

            // ⑤ 上面有什么
            try
            {
                var things = cell.GetThingList(map);
                string top = null;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t?.def == null) continue;
                    // 只报"有意思"的（跳过地板贴图之类）
                    if (t.def.category == ThingCategory.Building
                        || t.def.category == ThingCategory.Item
                        || t is Blueprint || t is Frame || t is Pawn)
                    {
                        top = t.def.label ?? t.def.defName;
                        if (t is Blueprint) top = "蓝图:" + top;
                        else if (t is Frame) top = "框架:" + top;
                        break;
                    }
                }
                sb.Append("\n上面 ").Append(top ?? "空");
            }
            catch (Exception) { }

            // ⑥ 我们造的吗（建筑账本）
            try
            {
                if (BuildingLedger.Registry.IsOurs(cell.x, cell.z))
                {
                    var recs = BuildingLedger.Registry.At(cell.x, cell.z);
                    sb.Append("\n★ 我们造的");
                    if (recs.Count > 1) sb.Append("（").Append(recs.Count).Append(" 栋共用）");
                    else if (recs.Count == 1) sb.Append("（").Append(recs[0].TemplateId).Append("）");
                }
            }
            catch (Exception) { }

            return sb.ToString();
        }

        private static void DrawLabel(string text)
        {
            if (_style == null)
            {
                _style = new UnityEngine.GUIStyle(UnityEngine.GUI.skin.label)
                {
                    fontSize = 13,
                    alignment = TextAnchor.UpperLeft,
                    wordWrap = false,
                };
                _style.normal.textColor = Color.white;
            }
            if (_bg == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
                _bg.Apply();
            }

            Vector2 mouse = Event.current != null ? Event.current.mousePosition : Vector2.zero;
            // ⚠ `Event.current.mousePosition` 在 OnGUI 里是 GUI 坐标（左上为原点），
            //   和 `Input.mousePosition`（左下为原点）不一样 —— 用前者才对得上屏幕位置。
            float x = mouse.x + OffsetX;
            float y = mouse.y + OffsetY;

            Vector2 size = _style.CalcSize(new UnityEngine.GUIContent(text));
            float w = size.x + 14f, h = size.y + 10f;

            // 别画出屏幕
            if (x + w > UI.screenWidth) x = Math.Max(4f, mouse.x - w - 6f);
            if (y + h > UI.screenHeight) y = Math.Max(4f, UI.screenHeight - h - 4f);

            var rect = new Rect(x, y, w, h);
            UnityEngine.GUI.DrawTexture(rect, _bg);
            UnityEngine.GUI.Label(new Rect(rect.x + 7f, rect.y + 5f, rect.width - 14f, rect.height - 10f), text, _style);
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref Enabled, "mouseCellOverlayEnabled", true);
        }
    }
}
