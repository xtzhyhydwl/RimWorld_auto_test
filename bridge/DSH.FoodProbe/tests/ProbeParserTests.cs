// 探针 `ParseCellPairs` 的离线回归测试。
//
// ## 为什么需要它
//
// 2026-10-03 实测发现：`foodprobe/place_blueprints` 传 2 个坐标**只放下 1 个**，
// 而且返回的坐标是**第二个**。根因是 JSON 解析写错了（见下 `OldParse`）：
//   外层 `[` → 到第一个 `]` 之间是 `[150,150` → 切出 `["[150","150"]`
//   → `int.TryParse("[150")` 失败 → **第一对静默丢掉**。
// 它**返回 success:true**，什么都不说 —— 正是本项目铁律
// 「任何"没做成"的分支都必须能说出为什么」针对的那类 bug。
//
// 后果很实在：上一轮的"建造顺序 = 放置顺序"实验因为这个 bug 只跑了一半，
// 差一点被误读成"游戏行为如此"。
//
// ## 本测试的纪律
//
// 「回归测试必须验证**能抓住 bug**」——所以这里**同时**保留了错误的旧实现，
// 并断言**旧实现真的会丢第一对**。这样：
//   - 有人把 ParseCellPairs 写回旧逻辑 → 新实现的断言会红
//   - 有人"顺手把测试改成总是通过"（比如删掉旧实现那段）→ 旧实现的断言会红
//
// 用法：node scripts/test-probe-parser.mjs

using System;
using System.Collections.Generic;

internal static class ProbeParserTests
{
    // ────────────────────────────────────────────────────────────────
    // 被测实现（与 bridge/DSH.FoodProbe/DSH.FoodProbe.cs 的 ParseCellPairs 同源）
    // ────────────────────────────────────────────────────────────────

    /// <summary>当前实现：去掉 `[`/`]`，剩下的数字每两个配一对。</summary>
    private static List<int[]> Parse(string s)
    {
        var result = new List<int[]>();
        if (string.IsNullOrEmpty(s)) return result;

        string cleaned = s.Replace("[", " ").Replace("]", " ");
        string[] parts = cleaned.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

        var nums = new List<int>();
        foreach (string p in parts)
        {
            int v;
            if (int.TryParse(p.Trim(), out v)) nums.Add(v);
        }

        for (int k = 0; k + 1 < nums.Count; k += 2)
            result.Add(new int[] { nums[k], nums[k + 1] });

        return result;
    }

    /// <summary>★ 修复前的旧实现（故意保留，用来证明测试抓得住这个 bug）。</summary>
    private static List<int[]> OldParse(string s)
    {
        var result = new List<int[]>();
        if (string.IsNullOrEmpty(s)) return result;

        int i = 0;
        while (i < s.Length)
        {
            int open = s.IndexOf('[', i);
            if (open < 0) break;
            int close = s.IndexOf(']', open);
            if (close < 0) break;

            string inner = s.Substring(open + 1, close - open - 1);
            string[] parts = inner.Split(',');
            if (parts.Length >= 2)
            {
                int a, b;
                if (int.TryParse(parts[0].Trim(), out a) && int.TryParse(parts[1].Trim(), out b))
                    result.Add(new int[] { a, b });
            }
            i = close + 1;
        }
        return result;
    }

    // ────────────────────────────────────────────────────────────────
    // 极简断言
    // ────────────────────────────────────────────────────────────────

    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = null)
    {
        if (ok) { _pass++; Console.WriteLine("  [PASS] " + name); }
        else { _fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  — " + detail : "")); }
    }

    private static string Fmt(List<int[]> pairs)
    {
        var parts = new List<string>();
        foreach (var p in pairs) parts.Add("(" + p[0] + "," + p[1] + ")");
        return "[" + string.Join(" ", parts.ToArray()) + "]";
    }

    private static void CheckPairs(string name, string input, string expected)
    {
        string got = Fmt(Parse(input));
        Check(name, got == expected, "输入 " + input + " → " + got + "，期望 " + expected);
    }

    private static int Main()
    {
        Console.WriteLine("=== 探针 ParseCellPairs 回归测试 ===");
        Console.WriteLine();

        // ① ★ 这个 bug 真的存在（旧实现会丢第一对）—— 证明本测试有抓 bug 的能力
        Console.WriteLine("① 旧实现确实有 bug（★ 若这条红了，说明本测试失去抓 bug 的能力）");
        {
            var old = OldParse("[[150,150],[155,150]]");
            Check("旧实现丢掉了第一个坐标", old.Count == 1, "旧实现得到 " + Fmt(old));
            Check("旧实现剩下的是**第二个**坐标",
                old.Count == 1 && old[0][0] == 155 && old[0][1] == 150, "旧实现得到 " + Fmt(old));
        }
        Console.WriteLine();

        // ② 新实现：文档里写的格式必须完整解析
        Console.WriteLine("② 新实现：完整解析嵌套 JSON");
        CheckPairs("两个坐标 → 两对，顺序不变", "[[150,150],[155,150]]", "[(150,150) (155,150)]");
        CheckPairs("一个坐标 → 一对", "[[150,150]]", "[(150,150)]");
        CheckPairs("三个坐标 → 三对", "[[1,2],[3,4],[5,6]]", "[(1,2) (3,4) (5,6)]");
        CheckPairs("空白不影响", "[[150, 150], [155, 150]]", "[(150,150) (155,150)]");
        CheckPairs("换行不影响", "[[150,150],\n [155,150]]", "[(150,150) (155,150)]");
        Console.WriteLine();

        // ③ 边界：不能崩、不能瞎编
        Console.WriteLine("③ 边界");
        Check("空串 → 空", Parse("").Count == 0, Fmt(Parse("")));
        Check("null → 空", Parse(null).Count == 0, Fmt(Parse(null)));
        Check("没有坐标 → 空", Parse("[]").Count == 0, Fmt(Parse("[]")));
        Check("奇数个数字 → 丢掉落单的那个（不崩）", Parse("[[1,2],[3]]").Count == 1,
            Fmt(Parse("[[1,2],[3]]")));
        Check("负数坐标可解析", Fmt(Parse("[[-3,-4]]")) == "[(-3,-4)]", Fmt(Parse("[[-3,-4]]")));
        Console.WriteLine();

        // ④ ★ 上一轮真实的失败调用（2 个坐标只放下 1 个）在这份实现下会被完整解析
        Console.WriteLine("④ ★ 复现上一轮真实调用");
        Check("上一轮传的 cellsJson 现在解析出 2 对",
            Parse("[[150,150],[155,150]]").Count == 2, Fmt(Parse("[[150,150],[155,150]]")));
        Console.WriteLine();

        Console.WriteLine("=== 通过 " + _pass + " / 失败 " + _fail + " ===");
        return _fail == 0 ? 0 : 1;
    }
}
