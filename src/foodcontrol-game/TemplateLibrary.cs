using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Verse;

namespace FoodControl.Game
{
    /// <summary>
    /// **样板库**：把 `templates/*.json` 读进来。
    ///
    /// ## 样板放在哪
    ///
    /// 优先从 **mod 自己的目录**读：`<ModContentPack.RootDir>/templates/*.json`。
    /// 那个目录是工作区 `mods/FoodControl/`（通过 junction 暴露给游戏），
    /// 而仓库里的 `templates/` 部署时会拷过去 —— 所以**样板受 git 管理**（用户选的）。
    ///
    /// 退路：如果 mod 目录里没有，再试隔离存档目录（便于临时试验）。
    ///
    /// ⚠ 为什么不在 Core 里读文件：Core 不许引用游戏程序集（`ModContentPack`
    ///   是游戏类型）。所以"找目录"这件事必须在 Game 层，Core 只接收文件内容。
    /// </summary>
    public static class TemplateLibrary
    {
        /// <summary>已加载的样板（id → 样板）</summary>
        private static readonly Dictionary<string, FoodControl.Core.BuildTemplate> _byId
            = new Dictionary<string, FoodControl.Core.BuildTemplate>();

        /// <summary>加载时的错误（诊断用）</summary>
        private static readonly List<string> _errors = new List<string>();

        private static bool _loaded;
        private static string _loadedFrom = "";

        public static IList<string> Errors
        {
            get { return _errors.AsReadOnly(); }
        }

        public static string LoadedFrom
        {
            get { return _loadedFrom; }
        }

        public static int Count
        {
            get { return _byId.Count; }
        }

        /// <summary>列出所有样板 id</summary>
        public static List<string> Ids()
        {
            EnsureLoaded();
            return new List<string>(_byId.Keys);
        }

        /// <summary>取一份样板；没有返回 null</summary>
        public static FoodControl.Core.BuildTemplate Get(string id)
        {
            EnsureLoaded();
            FoodControl.Core.BuildTemplate t;
            return _byId.TryGetValue(id ?? "", out t) ? t : null;
        }

        /// <summary>取**所有**样板（按 id 排序，保证顺序稳定）</summary>
        public static List<FoodControl.Core.BuildTemplate> All()
        {
            EnsureLoaded();
            var list = new List<FoodControl.Core.BuildTemplate>(_byId.Values);
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        /// <summary>按 tag 找（比如 "bedroom"）</summary>
        public static List<FoodControl.Core.BuildTemplate> WithTag(string tag)
        {
            EnsureLoaded();
            var list = new List<FoodControl.Core.BuildTemplate>();
            if (string.IsNullOrEmpty(tag)) return list;
            foreach (var kv in _byId)
            {
                for (int i = 0; i < kv.Value.Tags.Count; i++)
                {
                    if (string.Equals(kv.Value.Tags[i], tag, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(kv.Value);
                        break;
                    }
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        /// <summary>测试用：强制重新加载（比如刚写了新样板）</summary>
        public static void Reload()
        {
            _loaded = false;
            _byId.Clear();
            _errors.Clear();
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            foreach (string dir in CandidateDirs())
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

                    string[] files = Directory.GetFiles(dir, "*.json");
                    int ok = 0;
                    for (int i = 0; i < files.Length; i++)
                    {
                        string f = files[i];
                        try
                        {
                            string json = File.ReadAllText(f);
                            var t = FoodControl.Core.TemplateJson.Read(json);

                            // 没写 id 就用文件名兜底
                            if (string.IsNullOrEmpty(t.Id))
                                t.Id = Path.GetFileNameWithoutExtension(f);

                            List<string> errs = t.Validate();
                            if (errs.Count > 0)
                            {
                                _errors.Add(Path.GetFileName(f) + " 校验有问题：" + string.Join("; ", errs.ToArray()));
                            }

                            _byId[t.Id] = t;
                            ok++;
                        }
                        catch (Exception e)
                        {
                            _errors.Add(Path.GetFileName(f) + " 解析失败：" + e.Message);
                        }
                    }

                    if (ok > 0)
                    {
                        _loadedFrom = dir;
                        try { Log.Message("[FoodControl] 样板库加载了 " + ok + " 份样板，来自 " + dir); } catch (Exception) { }
                        return;      // 找到非空目录就用它，不再往下试
                    }
                }
                catch (Exception e)
                {
                    _errors.Add("扫描目录失败 " + dir + "：" + e.Message);
                }
            }

            try { Log.Message("[FoodControl] 样板库为空（没找到任何 .json 样板）"); } catch (Exception) { }
        }

        /// <summary>
        /// 候选目录，按优先级。
        ///
        /// ⚠ 这里**不能**写成 `yield return` —— C# 不允许在"带 catch 的 try 块"里 yield
        /// （错误 CS1626）。所以先收集到 List 再返回。
        /// </summary>
        private static List<string> CandidateDirs()
        {
            var list = new List<string>();

            // ① mod 自己的目录（工作区 mods/FoodControl/templates）
            string mr = ModRoot();
            if (!string.IsNullOrEmpty(mr)) list.Add(Path.Combine(mr, "templates"));

            // ② 隔离存档目录（便于临时试验）
            try
            {
                list.Add(Path.Combine(GenFilePaths.SaveDataFolderPath, "foodcontrol-templates"));
            }
            catch (Exception) { }

            return list;
        }

        private static string _modRoot;

        /// <summary>找到本 mod 的内容根目录</summary>
        private static string ModRoot()
        {
            if (!string.IsNullOrEmpty(_modRoot)) return _modRoot;
            try
            {
                var mod = LoadedModManager.GetMod<FoodControl.Game.ModBoot>();
                if (mod != null && mod.Content != null && mod.Content.RootDir != null)
                {
                    _modRoot = mod.Content.RootDir;
                    return _modRoot;
                }
            }
            catch (Exception) { }

            // 退路：我们的程序集物理位置往上找（Assemblies 的父目录的父目录）
            try
            {
                string asmLoc = typeof(TemplateLibrary).Assembly.Location;
                if (!string.IsNullOrEmpty(asmLoc))
                {
                    // .../1.6/Assemblies/FoodControl.dll → .../
                    string d = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(asmLoc)));
                    if (!string.IsNullOrEmpty(d)) { _modRoot = d; return _modRoot; }
                }
            }
            catch (Exception) { }

            _modRoot = "";
            return _modRoot;
        }

        /// <summary>一行摘要（日志/遥测用）</summary>
        public static string Describe()
        {
            EnsureLoaded();
            return string.Format("样板库 {0} 份（来自 {1}）{2}",
                _byId.Count,
                string.IsNullOrEmpty(_loadedFrom) ? "(未找到目录)" : _loadedFrom,
                _errors.Count > 0 ? "；" + _errors.Count + " 个问题" : "");
        }
    }
}
