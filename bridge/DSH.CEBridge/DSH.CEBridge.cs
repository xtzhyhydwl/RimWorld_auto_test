using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using RimBridgeServer.Sdk;
using Verse;
using RimWorld;

namespace DSH.CEBridge
{
    /// <summary>
    /// Character Editor 的桥接伴生工具。
    ///
    /// 背景：Character Editor 没有对外 API，但它的**数据层与 UI 层是分离的**
    /// （CEditor.StartEditor 只负责开窗，不碰数据）。数据操作散在 42 个 internal 的
    /// *Tool 静态类里，其中 SkillTool / RaceTool / AlienRaceTool 是 public，其余必须反射。
    ///
    /// 本组工具用反射直接调这些内部 API，好处是不依赖 UI（不受滑块点不到、没有文字输入工具的限制）。
    /// 代价是纯字符串匹配，CE 升级后可能失效 —— 所以第一个工具是自检用的 ce/probe。
    /// </summary>
    public sealed class CEBridgeTools
    {
        private const string CE_ASSEMBLY = "CharacterEditor";
        private const BindingFlags ANY_STATIC =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags ANY_INSTANCE =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags ANY_ALL =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        // ── 反射小工具 ───────────────────────────────────────────────

        private static Assembly FindCE()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (a.GetName().Name == CE_ASSEMBLY) return a; }
                catch { }
            }
            return null;
        }

        private static Type FindType(Assembly asm, string name)
        {
            if (asm == null) return null;
            var t = asm.GetType(name, false);
            if (t != null) return t;
            try
            {
                foreach (var x in asm.GetTypes())
                    if (x.FullName == name || x.Name == name) return x;
            }
            catch { }
            return null;
        }

        private static MethodInfo FindMethod(Type t, string name, bool staticOnly)
        {
            if (t == null) return null;
            foreach (var m in t.GetMethods(staticOnly ? ANY_STATIC : ANY_ALL))
                if (m.Name == name) return m;
            return null;
        }

        private static object Static(Type t, string name, params object[] args)
        {
            var m = FindMethod(t, name, true);
            if (m == null) throw new MissingMethodException(t.FullName + "." + name);
            return m.Invoke(null, args);
        }

        // ── 找到游戏里的 Pawn ────────────────────────────────────────

        /// <summary>
        /// pawnId 有两种写法，都要认：
        ///   - RimBridgeServer 的格式：Thing_&lt;种族DefName&gt;&lt;thingIDNumber&gt;，例如 Thing_Ratkin46626 / Thing_Human802
        ///   - 本模块历史上输出过的：Thing_&lt;thingIDNumber&gt;，例如 Thing_115
        /// 所以按"尾部连续数字"匹配最稳，另外也接受纯数字和名字。
        /// </summary>
        private static int TrailingNumber(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            int end = s.Length;
            int start = end;
            while (start > 0 && char.IsDigit(s[start - 1])) start--;
            if (start == end) return -1;
            int v;
            return int.TryParse(s.Substring(start, end - start), out v) ? v : -1;
        }

        private static Pawn FindPawn(string pawnName, string pawnId)
        {
            var map = Verse.Find.CurrentMap;
            if (map == null) return null;
            var all = map.mapPawns.AllPawns;
            if (all == null) return null;

            int wantNumber = string.IsNullOrEmpty(pawnId) ? -1 : TrailingNumber(pawnId);

            foreach (var p in all)
            {
                if (p == null) continue;
                if (wantNumber >= 0 && wantNumber == p.thingIDNumber) return p;
                if (!string.IsNullOrEmpty(pawnName))
                {
                    string shortName = null, fullName = null;
                    try { shortName = p.Name != null ? p.Name.ToStringShort : null; } catch { }
                    try { fullName = p.Name != null ? p.Name.ToStringFull : null; } catch { }
                    if (p.LabelShort == pawnName || shortName == pawnName || fullName == pawnName) return p;
                }
            }
            return null;
        }

        /// <summary>按 RimBridgeServer 的格式生成 pawnId，保证和其他工具能互相引用</summary>
        private static string PawnIdOf(Pawn p)
        {
            string race = null;
            try { race = p.def != null ? p.def.defName : null; } catch { }
            if (string.IsNullOrEmpty(race))
            {
                try { race = p.kindDef != null ? p.kindDef.defName : null; } catch { }
            }
            return "Thing_" + (race ?? "Pawn") + p.thingIDNumber;
        }

        private static string PawnLabel(Pawn p)
        {
            try { return p.Name != null ? p.Name.ToStringFull : p.LabelShort; }
            catch { return p.LabelShort; }
        }

        // ── ce/probe ─────────────────────────────────────────────────

        [Tool(
            "ce/probe",
            Description = "Check whether the Character Editor mod is loaded and whether its internal data API is reachable by reflection. Read-only, safe to call any time.",
            ResultDescription = "Load state plus which Character Editor types and methods were found.")]
        public object Probe()
        {
            var result = new Dictionary<string, object>();
            var asm = FindCE();
            if (asm == null)
            {
                result["loaded"] = false;
                result["hint"] = "Character Editor 本体没加载。检查 void.charactereditor 是否在启用列表里。";
                return result;
            }
            result["loaded"] = true;
            result["assemblyVersion"] = asm.GetName().Version.ToString();

            var ceditor = FindType(asm, "CharacterEditor.CEditor");
            result["CEditor_found"] = ceditor != null;
            if (ceditor != null)
            {
                var apiProp = ceditor.GetProperty("API", ANY_STATIC);
                result["CEditor_API_property_found"] = apiProp != null && apiProp.CanRead;
                if (apiProp != null && apiProp.CanRead)
                {
                    object api = null;
                    try { api = apiProp.GetValue(null, null); } catch (Exception ex) { result["CEditor_API_error"] = ex.Message; }
                    result["CEditor_API_isNull"] = api == null;
                    if (api != null)
                    {
                        var pawnProp = api.GetType().GetProperty("Pawn", ANY_INSTANCE);
                        result["CEditor_currentPawn_property"] = pawnProp != null;
                    }
                }
            }

            var skillTool = FindType(asm, "CharacterEditor.SkillTool");
            result["SkillTool_public"] = skillTool != null && skillTool.IsPublic;
            result["SkillTool_get"] = FindMethod(skillTool, "GetAllSkillsAsSeparatedString", true) != null;
            result["SkillTool_set"] = FindMethod(skillTool, "SetSkillsFromSeparatedString", true) != null;

            var preset = FindType(asm, "CharacterEditor.PresetPawn");
            result["PresetPawn_found"] = preset != null;
            if (preset != null)
            {
                result["PresetPawn_SavePawn"] = FindMethod(preset, "SavePawn", false) != null;
                result["PresetPawn_LoadPawn"] = FindMethod(preset, "LoadPawn", false) != null;
                var pe = FindType(asm, "CharacterEditor.PresetPawn+Param");
                if (pe != null && pe.IsEnum) result["PresetPawn_Param_count"] = Enum.GetNames(pe).Length;
            }

            var tools = new List<string>();
            try
            {
                foreach (var t in asm.GetTypes())
                    if (t.IsClass && t.Name.EndsWith("Tool") && t.Namespace == "CharacterEditor")
                        tools.Add(t.Name + (t.IsPublic ? "(public)" : "(internal)"));
            }
            catch { }
            tools.Sort();
            result["dataToolCount"] = tools.Count;
            result["dataTools"] = string.Join(", ", tools.ToArray());

            var map = Verse.Find.CurrentMap;
            result["hasMap"] = map != null;
            if (map != null) result["mapPawnCount"] = map.mapPawns.AllPawns.Count;
            return result;
        }

        // ── ce/colonists ─────────────────────────────────────────────

        [Tool(
            "ce/colonists",
            Description = "List player colonists with the stable pawn id used by every other ce/* tool. Read-only.",
            ResultDescription = "Colonists as name plus pawnId pairs.")]
        public object Colonists()
        {
            var map = Verse.Find.CurrentMap;
            if (map == null) return new { success = false, error = "没有当前地图" };
            var list = new List<object>();
            foreach (var p in map.mapPawns.FreeColonists)
            {
                if (p == null) continue;
                list.Add(new { name = p.LabelShort, fullName = PawnLabel(p), pawnId = PawnIdOf(p), thingIdNumber = p.thingIDNumber });
            }
            return new { success = true, count = list.Count, colonists = list };
        }

        // ── ce/get_skills ────────────────────────────────────────────

        [Tool(
            "ce/get_skills",
            Description = "Read a colonist's full skill table through Character Editor's own SkillTool (bypasses the UI). Returns Character Editor's native separated-string format.",
            ResultDescription = "The raw skill string Character Editor uses, plus the parsed entries.")]
        public object GetSkills(
            [ToolParameter(Description = "Colonist short name, e.g. Alex")] string pawnName = null,
            [ToolParameter(Description = "Stable pawn id, e.g. Thing_Human409 (takes priority over pawnName)")] string pawnId = null)
        {
            var asm = FindCE();
            if (asm == null) return new { success = false, error = "Character Editor 没加载" };
            var pawn = FindPawn(pawnName, pawnId);
            if (pawn == null) return new { success = false, error = "找不到小人：" + (pawnId ?? pawnName) };

            var skillTool = FindType(asm, "CharacterEditor.SkillTool");
            var raw = Static(skillTool, "GetAllSkillsAsSeparatedString", new object[] { pawn });
            var text = raw as string;

            // 顺便按常见分隔符拆一下，方便人看
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(text))
            {
                var chunks = text.Split(new[] { '|', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var c in chunks) parts.Add(c.Trim());
            }
            return new
            {
                success = true,
                pawn = PawnLabel(pawn),
                pawnId = PawnIdOf(pawn),
                raw,
                rawLength = text != null ? text.Length : 0,
                splitByPipe = parts
            };
        }

        // ── ce/set_skills ────────────────────────────────────────────

        [Tool(
            "ce/set_skills",
            Description = "Overwrite a colonist's whole skill table using Character Editor's native separated-string format. Read the format first with ce/get_skills, edit it, and pass it back. This is the same operation as using Character Editor's UI.",
            ResultDescription = "Whether the write was applied, plus the value read back afterwards.")]
        public object SetSkills(
            [ToolParameter(Description = "The full separated skill string, in Character Editor's format", Required = true)] string skills,
            [ToolParameter(Description = "Colonist short name, e.g. Alex")] string pawnName = null,
            [ToolParameter(Description = "Stable pawn id; takes priority over pawnName")] string pawnId = null)
        {
            var asm = FindCE();
            if (asm == null) return new { success = false, error = "Character Editor 没加载" };
            var pawn = FindPawn(pawnName, pawnId);
            if (pawn == null) return new { success = false, error = "找不到小人：" + (pawnId ?? pawnName) };

            var skillTool = FindType(asm, "CharacterEditor.SkillTool");
            var setter = FindMethod(skillTool, "SetSkillsFromSeparatedString", true);
            if (setter == null) return new { success = false, error = "SkillTool.SetSkillsFromSeparatedString 不存在" };

            var ps = setter.GetParameters();
            object[] args;
            if (ps.Length == 2) args = new object[] { pawn, skills };
            else if (ps.Length == 3) args = new object[] { pawn, skills, false };
            else return new { success = false, error = "SetSkillsFromSeparatedString 参数个数不认识：" + ps.Length };

            setter.Invoke(null, args);

            // 立刻回读，别相信"调用没抛异常就成功"
            var readback = Static(skillTool, "GetAllSkillsAsSeparatedString", new object[] { pawn });
            return new
            {
                success = true,
                pawn = PawnLabel(pawn),
                wrote = skills,
                readback,
                changed = !Equals(readback, skills)
            };
        }

        // ── ce/get_traits ────────────────────────────────────────────

        [Tool(
            "ce/get_traits",
            Description = "Read a colonist's traits through Character Editor's TraitTool (bypasses the UI).",
            ResultDescription = "The raw trait string Character Editor uses.")]
        public object GetTraits(
            [ToolParameter(Description = "Colonist short name")] string pawnName = null,
            [ToolParameter(Description = "Stable pawn id")] string pawnId = null)
        {
            var asm = FindCE();
            if (asm == null) return new { success = false, error = "Character Editor 没加载" };
            var pawn = FindPawn(pawnName, pawnId);
            if (pawn == null) return new { success = false, error = "找不到小人" };

            var traitTool = FindType(asm, "CharacterEditor.TraitTool");
            if (traitTool == null) return new { success = false, error = "TraitTool 不存在" };
            object raw = null;
            string used = null;
            foreach (var name in new[] { "GetAllTraitsAsSeparatedString", "GetTraitsAsSeparatedString", "GetAllTraits" })
            {
                var m = FindMethod(traitTool, name, true);
                if (m != null)
                {
                    try { raw = m.Invoke(null, new object[] { pawn }); used = name; break; }
                    catch (Exception ex) { return new { success = false, error = name + " 调用失败: " + ex.Message }; }
                }
            }
            return new
            {
                success = raw != null,
                pawn = PawnLabel(pawn),
                method = used,
                raw,
                hint = raw == null ? "TraitTool 上没有找到预期的 Get*AsSeparatedString 方法，用 ce/probe 看 dataTools 里的名字。" : null
            };
        }

        // ── 身体部位 / 断肢 ──────────────────────────────────────────
        //
        // 这一组有两层意义：
        //  1) 直接回答"能不能自动改四肢健全程度" —— 用游戏原版 API，最可靠
        //  2) 同时 dump CE 自己 HealthTool/BodyTool 的方法，看 CE 内部是怎么做的
        //
        // "某个部位没了"在 RimWorld 里是挂在 BodyPartRecord 上的一个
        // Hediff_MissingPart。而且父部位缺失时，子部位通过 hediffSet.PartIsMissing()
        // 也会返回 true —— 读数必须用这个 API，不能只看有没有 MissingPart 这个 hediff。

        private static List<BodyPartRecord> FindParts(Pawn pawn, string query)
        {
            var hits = new List<BodyPartRecord>();
            if (pawn?.RaceProps?.body == null) return hits;
            var q = (query ?? string.Empty).Trim().ToLowerInvariant();
            foreach (var part in pawn.RaceProps.body.AllParts)
            {
                if (part?.def == null) continue;
                string defName = part.def.defName ?? string.Empty;
                string label = string.Empty;
                try { label = part.LabelCap ?? part.def.label ?? string.Empty; } catch { }
                if (defName.ToLowerInvariant() == q) { hits.Insert(0, part); continue; }
                if (defName.ToLowerInvariant().Contains(q) || label.ToLowerInvariant().Contains(q)) hits.Add(part);
            }
            return hits;
        }

        private static Hediff MissingHediffFor(Pawn pawn, BodyPartRecord part)
        {
            foreach (var h in pawn.health.hediffSet.hediffs)
            {
                if (h is Hediff_MissingPart && h.Part == part) return h;
            }
            return null;
        }

        private static object DescribePart(Pawn pawn, BodyPartRecord part)
        {
            string label = string.Empty;
            try { label = part.LabelCap; } catch { }
            bool missing = false;
            try { missing = pawn.health.hediffSet.PartIsMissing(part); } catch { }
            bool directMissing = MissingHediffFor(pawn, part) != null;
            var hediffs = new List<string>();
            foreach (var h in pawn.health.hediffSet.hediffs)
            {
                if (h.Part == part) hediffs.Add(h.def.defName + "(" + h.Label + ")");
            }
            string parent = null;
            try { parent = part.parent?.def?.defName; } catch { }
            return new
            {
                defName = part.def.defName,
                label,
                parent,
                depth = part.depth,
                missing,
                directMissing,
                hediffs
            };
        }

        [Tool(
            "ce/body",
            Description = "Read a pawn's body-part tree and which parts are missing or injured. Uses RimWorld's own health API, so it also reports parts that are missing because an ancestor part is gone. Read-only.",
            ResultDescription = "All body parts with missing state plus a short summary of what is gone.")]
        public object Body(
            [ToolParameter(Description = "Colonist short name")] string pawnName = null,
            [ToolParameter(Description = "Stable pawn id, e.g. Thing_115")] string pawnId = null,
            [ToolParameter(Description = "Only return parts whose label or defName contains this text")] string query = null)
        {
            var pawn = FindPawn(pawnName, pawnId);
            if (pawn == null) return new { success = false, error = "找不到小人：" + (pawnId ?? pawnName) };
            if (pawn.RaceProps?.body == null) return new { success = false, error = "这个小人的种族没有 body 定义" };

            var parts = query == null ? new List<BodyPartRecord>(pawn.RaceProps.body.AllParts) : FindParts(pawn, query);
            var list = new List<object>();
            var missingLabels = new List<string>();
            foreach (var p in parts)
            {
                var d = DescribePart(pawn, p);
                list.Add(d);
            }
            foreach (var p in pawn.RaceProps.body.AllParts)
            {
                bool m = false;
                try { m = pawn.health.hediffSet.PartIsMissing(p); } catch { }
                if (m)
                {
                    try { missingLabels.Add(p.LabelCap); } catch { }
                }
            }
            missingLabels.Sort();
            return new
            {
                success = true,
                pawn = PawnLabel(pawn),
                pawnId = PawnIdOf(pawn),
                bodyDef = pawn.RaceProps.body.defName,
                partCount = pawn.RaceProps.body.AllParts.Count,
                returned = list.Count,
                missingParts = missingLabels,
                missingCount = missingLabels.Count,
                headCount = pawn.RaceProps.body.GetPartsWithDef(BodyPartDefOf.Head)?.Count ?? 0,
                parts = list
            };
        }

        [Tool(
            "ce/amputate",
            Description = "Make a body part missing on a pawn (adds RimWorld's Hediff_MissingPart), the same result as losing a limb. Use ce/body first to see the exact part labels such as 'left leg' or 'right arm'.",
            ResultDescription = "Which part was cut, and the missing state read back afterwards.")]
        public object Amputate(
            [ToolParameter(Description = "Part label or defName substring, e.g. 'left leg' or 'Leg'", Required = true)] string part,
            [ToolParameter(Description = "Colonist short name")] string pawnName = null,
            [ToolParameter(Description = "Stable pawn id")] string pawnId = null,
            [ToolParameter(Description = "When several parts match, which one to use (0-based, default 0)")] int index = 0)
        {
            var pawn = FindPawn(pawnName, pawnId);
            if (pawn == null) return new { success = false, error = "找不到小人" };
            var hits = FindParts(pawn, part);
            if (hits.Count == 0) return new { success = false, error = "找不到部位：" + part, candidates = AllPartLabels(pawn) };
            if (index < 0 || index >= hits.Count) return new { success = false, error = "index 越界，匹配到 " + hits.Count + " 个", candidates = PartLabels(pawn, hits) };

            var target = hits[index];
            var before = DescribePart(pawn, target);
            bool already = false;
            try { already = pawn.health.hediffSet.PartIsMissing(target); } catch { }

            if (!already)
            {
                var hediff = HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, pawn, target) as Hediff_MissingPart;
                if (hediff == null) return new { success = false, error = "HediffMaker 没能造出 Hediff_MissingPart" };
                pawn.health.AddHediff(hediff, target);
            }

            bool nowMissing = false;
            try { nowMissing = pawn.health.hediffSet.PartIsMissing(target); } catch { }
            return new
            {
                success = nowMissing,
                pawn = PawnLabel(pawn),
                part = DescribePart(pawn, target),
                matched = PartLabels(pawn, hits),
                wasAlreadyMissing = already,
                before,
                note = nowMissing ? null : "加完 hediff 后 PartIsMissing 仍为 false，需要排查"
            };
        }

        [Tool(
            "ce/restore",
            Description = "Restore a missing body part and everything under it. Amputating a leg also makes its children (femur, tibia, foot, toes) missing, so restoring must clear the whole subtree, not just the one part.",
            ResultDescription = "Which missing-part entries were cleared and the readback state.")]
        public object Restore(
            [ToolParameter(Description = "Part label or defName substring, e.g. 'left leg'", Required = true)] string part,
            [ToolParameter(Description = "Colonist short name")] string pawnName = null,
            [ToolParameter(Description = "Stable pawn id")] string pawnId = null,
            [ToolParameter(Description = "Which match to use when several parts match (0-based)")] int index = 0)
        {
            var pawn = FindPawn(pawnName, pawnId);
            if (pawn == null) return new { success = false, error = "找不到小人" };
            var hits = FindParts(pawn, part);
            if (hits.Count == 0) return new { success = false, error = "找不到部位：" + part, candidates = AllPartLabels(pawn) };
            if (index < 0 || index >= hits.Count) return new { success = false, error = "index 越界", candidates = PartLabels(pawn, hits) };

            var target = hits[index];
            var removed = new List<string>();

            // 要清的范围：目标本身 + 它的整棵子树 + 它所有祖先。
            // Amputate 一个父部位时，子部位会各自带上自己的 MissingBodyPart，
            // 只移除目标自己是不够的 —— 实测 18 → 17 而不是 18 → 9。
            var toClear = new HashSet<BodyPartRecord>();
            CollectSubtree(target, toClear);
            var anc = target.parent;
            while (anc != null) { toClear.Add(anc); anc = anc.parent; }

            var pending = new List<Hediff>();
            foreach (var h in pawn.health.hediffSet.hediffs)
            {
                if (h is Hediff_MissingPart && h.Part != null && toClear.Contains(h.Part)) pending.Add(h);
            }
            foreach (var h in pending)
            {
                string label = null;
                try { label = h.Part.LabelCap; } catch { }
                pawn.health.RemoveHediff(h);
                removed.Add((label ?? h.Part.def.defName) + " [" + h.Part.def.defName + "]");
            }

            bool stillMissing = false;
            try { stillMissing = pawn.health.hediffSet.PartIsMissing(target); } catch { }
            return new
            {
                success = !stillMissing,
                pawn = PawnLabel(pawn),
                part = DescribePart(pawn, target),
                removedCount = removed.Count,
                removed,
                stillMissing,
                note = removed.Count == 0 ? "这个部位本来就是健全的" : null
            };
        }

        private static void CollectSubtree(BodyPartRecord part, HashSet<BodyPartRecord> into)
        {
            if (part == null || into.Contains(part)) return;
            into.Add(part);
            if (part.parts == null) return;
            foreach (var c in part.parts) CollectSubtree(c, into);
        }

        private static List<string> AllPartLabels(Pawn pawn)
        {
            var labels = new List<string>();
            foreach (var p in pawn.RaceProps.body.AllParts)
            {
                try { labels.Add(p.LabelCap + " [" + p.def.defName + "]"); } catch { }
            }
            return labels;
        }

        private static List<string> PartLabels(Pawn pawn, List<BodyPartRecord> parts)
        {
            var labels = new List<string>();
            foreach (var p in parts)
            {
                try { labels.Add(p.LabelCap + " [" + p.def.defName + "]"); } catch { }
            }
            return labels;
        }

        // ── ce/ce_api：CE 自己的 HealthTool / BodyTool 能做什么 ──────

        [Tool(
            "ce/ce_api",
            Description = "Dump the method signatures of Character Editor's internal HealthTool / BodyTool / TraitTool so we can see exactly what the mod itself offers for body editing. Read-only.",
            ResultDescription = "Method signatures grouped by tool class.")]
        public object CeApi(
            [ToolParameter(Description = "Which CE tool class to dump; defaults to health-related ones")] string tool = null)
        {
            var asm = FindCE();
            if (asm == null) return new { success = false, error = "Character Editor 没加载" };
            var names = string.IsNullOrEmpty(tool)
                ? new[] { "HealthTool", "BodyTool", "TraitTool", "PawnxTool" }
                : new[] { tool };
            var result = new Dictionary<string, object>();
            foreach (var n in names)
            {
                var t = FindType(asm, "CharacterEditor." + n);
                if (t == null) { result[n] = "(类型不存在)"; continue; }
                var sigs = new List<string>();
                foreach (var m in t.GetMethods(ANY_ALL))
                {
                    if (m.IsSpecialName) continue;
                    var ps = m.GetParameters();
                    var parts = new string[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                        parts[i] = ps[i].ParameterType.Name + " " + ps[i].Name;
                    sigs.Add((m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", parts) + ")");
                }
                sigs.Sort();
                result[n] = new { isPublic = t.IsPublic, methodCount = sigs.Count, methods = sigs };
            }
            return new { success = true, tools = result };
        }
    }
}
