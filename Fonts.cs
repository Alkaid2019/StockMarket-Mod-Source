using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using UnityEngine;

namespace StockMarket
{
    /// <summary>
    /// 中文 TMP 字体探测。手法照搬 PSPDA：
    /// 先在场景里找游戏正在用的字体，再从全部 TMP 字体资源里挑一个带中文的。
    /// </summary>
    internal static class Fonts
    {
        private static TMP_FontAsset _ui;
        private static TMP_FontAsset _game;
        private static bool _done;

        public static TMP_FontAsset Font()
        {
            if (!_done) Init();
            return _ui != null ? _ui : _game;
        }

        private static void Init()
        {
            _done = true;
            try
            {
                _game = FromSceneText();
                List<TMP_FontAsset> all = All();
                TMP_FontAsset pick = First(all, "notosanssc");
                if (pick == null) pick = First(all, "notosansjp");
                if (pick == null) pick = First(all, "notosans");
                if (pick == null)
                {
                    for (int i = 0; i < all.Count && pick == null; i++)
                    {
                        string n = Name(all[i]).ToLowerInvariant();
                        if (n.Contains("wdxl") || n.Contains("lubrifont")) continue;
                        if (HasCjk(all[i])) pick = all[i];
                    }
                }
                if (pick == null) pick = HasCjk(_game) ? _game : null;
                _ui = pick;

                Core.Log.Msg("[字体] 面板字体 = " + (_ui != null ? Name(_ui) : "未找到")
                    + "，中文 " + (HasCjk(_ui) ? "正常" : "可能显示为方块")
                    + "（候选 " + all.Count + " 个）");
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[字体] 初始化失败：" + ex.Message);
            }
        }

        /// <summary>取场景里第一个有字体的 TMP 文本，它的字体就是游戏原字体。</summary>
        private static TMP_FontAsset FromSceneText()
        {
            try
            {
                Il2CppReferenceArray<UnityEngine.Object> found =
                    Resources.FindObjectsOfTypeAll(Ui.TypeOf<TextMeshProUGUI>());
                if (found == null) return null;
                for (int i = 0; i < found.Length; i++)
                {
                    TextMeshProUGUI t = found[i] != null ? found[i].TryCast<TextMeshProUGUI>() : null;
                    if (t == null) continue;
                    TMP_FontAsset f = null;
                    try { f = t.font; } catch { }
                    if (f != null) return f;
                }
            }
            catch (Exception ex)
            {
                Core.Debug("[字体] 找场景字体失败：" + ex.Message);
            }
            return null;
        }

        private static List<TMP_FontAsset> All()
        {
            List<TMP_FontAsset> list = new List<TMP_FontAsset>();
            try
            {
                Il2CppReferenceArray<UnityEngine.Object> found =
                    Resources.FindObjectsOfTypeAll(Ui.TypeOf<TMP_FontAsset>());
                if (found == null) return list;
                for (int i = 0; i < found.Length; i++)
                {
                    TMP_FontAsset f = found[i] != null ? found[i].TryCast<TMP_FontAsset>() : null;
                    if (f != null) list.Add(f);
                }
            }
            catch (Exception ex)
            {
                Core.Debug("[字体] 枚举字体失败：" + ex.Message);
            }
            return list;
        }

        private static TMP_FontAsset First(List<TMP_FontAsset> all, string key)
        {
            for (int i = 0; i < all.Count; i++)
            {
                if (Name(all[i]).ToLowerInvariant().Contains(key) && HasCjk(all[i])) return all[i];
            }
            return null;
        }

        private static string Name(TMP_FontAsset f)
        {
            try { return f == null ? "" : (f.name ?? ""); }
            catch { return ""; }
        }

        /// <summary>
        /// 当前面板字体里有没有这个字形。
        /// 导航图标全靠它筛：字体缺哪个字就不画哪个图标，宁可少一个图标，
        /// 也不能在侧栏上排出一列方块。
        ///
        /// tryAddCharacter 必须传 true：TMP 图集是「动态」的，字库里有的字要等真正用到
        /// 才会被烘进图集，只查现有图集会把本来能显示的字误判成缺字。
        /// </summary>
        public static bool Has(char c)
        {
            TMP_FontAsset f = Font();
            if (f == null) return false;
            try { return f.HasCharacter(c, true, true); } catch { return false; }
        }

        /// <summary>用两个常用汉字判断字体是否带中文。</summary>
        private static bool HasCjk(TMP_FontAsset f)
        {
            try
            {
                if (f == null) return false;
                return f.HasCharacter('能', false, false) && f.HasCharacter('信', false, false);
            }
            catch { return false; }
        }
    }
}
