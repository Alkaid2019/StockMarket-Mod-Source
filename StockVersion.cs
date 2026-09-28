using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StockMarket
{
    /// <summary>
    /// 模组版本号 + 在线更新检查。
    ///
    /// 每次启动游戏：开一条后台线程去发布页取「最新版本」，取完在屏幕左下角挂一块常驻小牌，
    /// 写着模组名、当前版本、最新版本和这次检测的时间戳；当前版本落后就在下面补一行红字。
    ///
    /// 版本比较的口径（用户定的）：先比数字，数字一样再比后缀 ——
    /// Beta 小于一切其他标注，正式版（没后缀）最大。末尾的 -t 只是「测试版」标记，不参与比较。
    ///
    /// 末尾带 -t 的测试版还会去问另一个发布页「还支不支持」：那里出现 error
    /// 就弹一个关不掉的窗，点确定直接退出游戏 —— 免得玩家拿着一个已经作废的版本报 bug。
    /// </summary>
    internal static class StockVersion
    {
        public const string ModName = "星际证券";

        /// <summary>作者署名（版本牌与更新提醒里显示，MelonInfo 也用它）。</summary>
        public const string Author = "ButterLab工作室";

        /// <summary>
        /// 当前版本。末尾带 -t 表示测试版，会额外查一次「还支不支持」。
        /// V1.0.0 起转为正式版（不带 -t、不带 Beta）：不再查支持状态页。
        /// </summary>
        public const string Current = "V1.0.0";

        public const string UpdateNotice = "目前有更新的版本，更新至最新版本获取最高体验";

        private const string VersionUrl = "https://pastebin.com/UxGTgxW2";
        private const string VersionRaw = "https://pastebin.com/raw/UxGTgxW2";
        private const string SupportUrl = "https://pastebin.com/KZdJ3dBn";
        private const string SupportRaw = "https://pastebin.com/raw/KZdJ3dBn";

        private const int CanvasOrder = 30900;   // 低于左下角入口按钮（31000），更低于主面板（32000）
        private const int ModalOrder = 32767;    // 停用通知要盖住一切，含主面板（32000）

        /// <summary>左下角版本牌亮多久自动收起（秒）。</summary>
        private const float ShowSeconds = 15f;

        private const float MarginX = 22f;
        private const float MarginY = 72f;       // 抬到【星际证券】入口按钮（20+44）上面
        private const float InfoW = 348f;
        private const float InfoH = 106f;        // 三行正文 + 可能的一行红字更新提醒

        private const float CardW = 640f;
        private const float CardH = 300f;

        // 后台线程写、主线程读，所以标 volatile
        private static volatile string _latest = "";
        private static volatile bool _done;
        private static volatile bool _failed;
        private static volatile bool _unsupported;
        private static string _stamp = "";

        private static Canvas _canvas;
        private static TextMeshProUGUI _infoText;
        private static bool _tried;      // 本场景已经建过一次（失败就不反复试，免得刷日志）

        private static float _deadline = -1f;   // 首次 Sync 时定，到点收牌
        private static bool _closed;            // 15 秒已经过了，本次启动不再显示

        private static Canvas _modal;

        // ── 生命周期 ──────────────────────────────────────────────────

        /// <summary>由 Core.OnInitializeMelon 调一次。</summary>
        public static void Start()
        {
            _stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Core.Log.Msg("[" + ModName + "] 当前版本 " + Current
                + (IsTestBuild() ? "（测试版，会检查是否仍受支持）" : "（正式版）")
                + "　启动时间 " + _stamp);

            Thread t = new Thread(Check);
            t.IsBackground = true;
            try { t.Start(); }
            catch (Exception ex)
            {
                _failed = true;
                _done = true;
                Core.Log.Warning("[版本] 起不了检查线程：" + ex.Message);
            }
        }

        /// <summary>切场景后旧画布已被销毁，清掉引用等下一帧重建。</summary>
        public static void OnSceneLoaded()
        {
            _canvas = null;
            _infoText = null;
            _tried = false;
            // 通知窗这里不主动清：它可能被旧场景带走了，也可能还在。
            // 留给 EnsureModal 用 Alive 判断，免得把活着的窗丢掉、又叠一个出来。
        }

        /// <summary>由 Core.OnUpdate 每帧调用。</summary>
        public static void Sync()
        {
            // 停用通知优先：这是硬拦，窗没了就重建，别让玩家溜进游戏
            if (_unsupported)
            {
                EnsureModal();
                return;
            }
            if (_closed) return;

            if (_deadline < 0f) _deadline = Time.unscaledTime + ShowSeconds;
            float left = _deadline - Time.unscaledTime;
            if (left <= 0f)
            {
                // 到点收牌：本场景拆掉，且本次启动不再显示。
                // 必须真把画布销毁掉 —— 之前只把引用置空，GameObject 还留在场景里继续画，
                // 牌子就永远停在最后一帧的「1 秒后自动关闭」不动（见用户反馈）。
                _closed = true;
                Canvas dead = _canvas;
                _canvas = null;
                _infoText = null;
                _tried = true;
                if (Alive(dead))
                {
                    try { UnityEngine.Object.Destroy(dead.gameObject); } catch { }
                }
                Core.Log.Msg("[版本] 版本提示已显示 " + (int)ShowSeconds + " 秒，自动收起。");
                return;
            }

            if (_canvas == null || !Alive(_canvas.gameObject))
            {
                _canvas = null;
                _infoText = null;
                if (!_tried) Build();
            }
            if (_infoText == null) return;

            string s = InfoText(left);
            if (_infoText.text != s) _infoText.text = s;
        }

        // ── 在线检查（后台线程）───────────────────────────────────────

        private static void Check()
        {
            try
            {
                string v = FetchValue(VersionUrl, VersionRaw);
                if (v.Length > 0)
                {
                    _latest = v;
                    Core.Log.Msg("[版本] 发布页最新版本 " + v + "，当前 " + Current + "，"
                        + (Compare(Current, v) < 0 ? "有更新。" : "已是最新。"));
                }
                else
                {
                    _failed = true;
                    Core.Log.Warning("[版本] 发布页里没读到版本号。");
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                Core.Log.Warning("[版本] 获取最新版本失败：" + ex.Message);
            }
            _done = true;

            // 只有末尾带 -t 的测试版才去问「还支不支持」
            if (!IsTestBuild()) return;
            try
            {
                string e = FetchValue(SupportUrl, SupportRaw);
                if (e.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _unsupported = true;
                    Core.Log.Warning("[版本] 支持状态页出现 error，本版本已停止支持。");
                }
                else
                {
                    Core.Log.Msg("[版本] 支持状态正常。");
                }
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[版本] 获取支持状态失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 先抓发布页，从 &lt;div class="de1"&gt;…&lt;/div&gt; 里取内容；
        /// 取不到（页面结构变了、或被挡了）就退回 raw 纯文本，取第一行。
        /// </summary>
        private static string FetchValue(string url, string rawUrl)
        {
            try
            {
                string v = ExtractDe1(Get(url));
                if (v.Length > 0) return v;
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[版本] 读取 " + url + " 失败：" + ex.Message);
            }
            return FirstLine(Strip(Get(rawUrl)));
        }

        private static string Get(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
            req.Accept = "text/html,text/plain,*/*";
            using (WebResponse resp = req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        private static string ExtractDe1(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            int i = html.IndexOf("class=\"de1\"", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            int gt = html.IndexOf('>', i);
            if (gt < 0) return "";
            int end = html.IndexOf('<', gt + 1);
            if (end < 0) return "";
            return FirstLine(Strip(html.Substring(gt + 1, end - gt - 1)));
        }

        /// <summary>剥掉 HTML 标签，只留文字。</summary>
        private static string Strip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            bool inTag = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return sb.ToString();
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int n = s.IndexOfAny(new char[] { '\r', '\n' });
            return (n < 0 ? s : s.Substring(0, n)).Trim();
        }

        // ── 版本比较 ──────────────────────────────────────────────────

        /// <summary>当前版本落后于发布页版本。</summary>
        public static bool HasUpdate
        {
            get { return _done && !_failed && _latest.Length > 0 && Compare(Current, _latest) < 0; }
        }

        private static bool IsTestBuild()
        {
            int[] nums; string tag; bool test;
            Parse(Current, out nums, out tag, out test);
            return test;
        }

        /// <summary>a &lt; b 返回 -1，相等 0，a &gt; b 返回 1。</summary>
        public static int Compare(string a, string b)
        {
            int[] na, nb; string ta, tb; bool dummy;
            Parse(a, out na, out ta, out dummy);
            Parse(b, out nb, out tb, out dummy);

            // 先比数字，短的补 0
            int n = Math.Max(na.Length, nb.Length);
            for (int i = 0; i < n; i++)
            {
                int va = i < na.Length ? na[i] : 0;
                int vb = i < nb.Length ? nb[i] : 0;
                if (va != vb) return va < vb ? -1 : 1;
            }
            // 数字一样才看后缀
            int ra = Rank(ta), rb = Rank(tb);
            if (ra != rb) return ra < rb ? -1 : 1;
            return 0;
        }

        /// <summary>Beta 小于一切其他标注；没标注（正式版）最大。</summary>
        private static int Rank(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return 2;
            if (string.Equals(tag, "beta", StringComparison.OrdinalIgnoreCase)) return 0;
            return 1;
        }

        /// <summary>
        /// "V0.8-Beta-t" → 数字 [0,8]、标注 "Beta"、test=true。
        /// 开头的 v、末尾单独一段 t 都不算版本内容。
        /// </summary>
        private static void Parse(string raw, out int[] nums, out string tag, out bool test)
        {
            nums = new int[0];
            tag = "";
            test = false;
            if (string.IsNullOrEmpty(raw)) return;

            string s = raw.Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);

            string[] seg = s.Split(new char[] { '.', '-', '_', ' ' },
                StringSplitOptions.RemoveEmptyEntries);
            List<int> list = new List<int>();
            for (int i = 0; i < seg.Length; i++)
            {
                string tok = seg[i].Trim();
                if (tok.Length == 0) continue;

                int num;
                if (int.TryParse(tok, out num)) { list.Add(num); continue; }

                // 末尾单独一段 t 是测试版标记，不参与比较
                if (i == seg.Length - 1 && string.Equals(tok, "t", StringComparison.OrdinalIgnoreCase))
                {
                    test = true;
                    continue;
                }
                if (tag.Length == 0) tag = tok;
            }
            nums = list.ToArray();
        }

        // ── 左下角版本牌 ──────────────────────────────────────────────

        private static void Build()
        {
            _tried = true;
            try
            {
                Canvas canvas = Ui.NewCanvas("StockMarket_VersionCanvas");
                if (canvas == null) return;
                canvas.sortingOrder = CanvasOrder;

                GameObject info = Ui.New("VersionInfo", canvas.transform);
                if (info == null)
                {
                    UnityEngine.Object.Destroy(canvas.gameObject);
                    return;
                }
                Ui.PlaceBottomLeft(info, MarginX, MarginY, InfoW, InfoH);

                Image rim = Ui.MakeSliced(info.transform, "VerRim", Ui.Card(), Palette.CardRim, false);
                if (rim != null) Ui.Stretch(rim.gameObject, 0f);
                Image face = Ui.MakeSliced(info.transform, "VerFace", Ui.Card(), Palette.CardBg, false);
                if (face != null) Ui.Stretch(face.gameObject, 2f);

                _infoText = Ui.MakeText(info.transform, "VerText", "", 14f,
                    Palette.CardBody, Ui.AlignTopLeft, false);
                if (_infoText != null)
                {
                    Ui.Place(_infoText.gameObject, 12f, 9f, InfoW - 24f, InfoH - 18f);
                    try { _infoText.lineSpacing = 4f; } catch { }
                }

                _canvas = canvas;
                Core.Log.Msg("[版本] 已在左下角挂出版本信息。");
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[版本] 版本信息挂载失败：" + ex.Message);
            }
        }

        private static string InfoText(float left)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<color=").Append(Palette.HexOf(Palette.Gold)).Append('>')
              .Append(ModName).Append("</color>　<color=")
              .Append(Palette.HexOf(Palette.Title)).Append('>').Append(Current).Append("</color>")
              .Append("<color=").Append(Palette.HexOf(Palette.Muted))
              .Append(">　作者：").Append(Author).Append("</color>\n");

            string latest = !_done ? "获取中…"
                : (_failed || _latest.Length == 0) ? "获取失败" : _latest;
            bool bad = _failed || _latest.Length == 0;
            sb.Append("<color=").Append(Palette.HexOf(Palette.Sub)).Append(">最新版本 </color>")
              .Append("<color=").Append(Palette.HexOf(bad ? Palette.Danger : Palette.Cyan))
              .Append('>').Append(latest).Append("</color>");
            if (_stamp.Length > 0)
            {
                sb.Append("　<color=").Append(Palette.HexOf(Palette.Muted)).Append('>')
                  .Append(_stamp).Append("</color>");
            }

            // 让玩家知道这块牌子会自己走，不用管它
            int sec = (int)Math.Ceiling(left);
            if (sec < 0) sec = 0;
            sb.Append('\n').Append("<color=").Append(Palette.HexOf(Palette.Muted)).Append(">（本提示 ")
              .Append(sec).Append(" 秒后自动关闭）</color>");

            if (HasUpdate)
            {
                sb.Append('\n').Append("<color=").Append(Palette.HexOf(Palette.Danger))
                  .Append('>').Append(UpdateNotice).Append("</color>");
            }
            return Palette.Rt(sb.ToString());
        }

        // ── 停止支持通知 ──────────────────────────────────────────────

        /// <summary>
        /// 弹窗没了就重建。
        ///
        /// 之前只在第一次建一次，玩家从主菜单点进存档时场景一换、这个画布跟着旧场景
        /// 一起销毁，人就顺顺当当进了游戏（见用户反馈「弹窗盖不住主界面」）。
        /// 现在每帧检查一次，丢了就补上。
        /// </summary>
        private static void EnsureModal()
        {
            KeepWorldBlocked();
            if (_modal != null && Alive(_modal.gameObject)) return;
            _modal = null;
            try
            {
                Canvas canvas = Ui.NewCanvas("StockMarket_NoticeCanvas");
                if (canvas == null) return;
                canvas.sortingOrder = ModalOrder;

                Image scrim = Ui.MakeImage(canvas.transform, "NoticeScrim",
                    new Color(0f, 0f, 0f, 0.78f), true);
                if (scrim != null) Ui.Stretch(scrim.gameObject, 0f);

                GameObject card = Ui.New("NoticeCard", canvas.transform);
                if (card == null) return;
                Ui.PlaceCentered(card, 0f, 0f, CardW, CardH);

                Image rim = Ui.MakeSliced(card.transform, "NoticeRim", Ui.Card(), Palette.CardRim, false);
                if (rim != null) Ui.Stretch(rim.gameObject, 0f);
                Image face = Ui.MakeSliced(card.transform, "NoticeFace", Ui.Card(), Palette.CardBg, false);
                if (face != null) Ui.Stretch(face.gameObject, 2f);

                TextMeshProUGUI title = Ui.MakeText(card.transform, "NoticeTitle",
                    "版本已停止支持", 24f, Palette.Danger, Ui.AlignCenter, false);
                if (title != null) Ui.Place(title.gameObject, 20f, 24f, CardW - 40f, 32f);

                TextMeshProUGUI body = Ui.MakeText(card.transform, "NoticeBody",
                    "该" + ModName + "版本已经不受工作室支持。\n\n"
                    + "请到发布页下载最新版本后再进游戏。", 18f, Palette.CardBody,
                    Ui.AlignTopLeft, true);
                if (body != null)
                {
                    Ui.Truncate(body);
                    Ui.Place(body.gameObject, 30f, 74f, CardW - 60f, CardH - 74f - 80f);
                }

                UiButton ok = Ui.MakeButton(card.transform, "NoticeOk", "确定", 20f,
                    Palette.BtnRed, Palette.Title, Quit, Ui.AlignCenter);
                if (ok != null) ok.Place((CardW - 180f) * 0.5f, CardH - 68f, 180f, 46f);

                _modal = canvas;
                Core.Log.Warning("[版本] 已弹出停止支持通知。");
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[版本] 停止支持通知弹窗失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 把游戏自己的鼠标交互栈压住。
        ///
        /// 光靠 UGUI 那层半透明遮罩是不够的：游戏的主界面按钮走的是它自己的输入，
        /// 遮罩挡不住，玩家能直接点进游戏。这里复用面板同一套 OverlayHandler。
        /// 栈可能被游戏或面板的收尾逻辑清掉，所以每帧核一次，发现是 0 就补上。
        /// </summary>
        private static void KeepWorldBlocked()
        {
            try
            {
                OverlayHandler handler = OverlayHandler.current;
                if (handler == null) return;
                if (handler.mouseBlockStacks <= 0) handler.BlockMouse();
            }
            catch { }
        }

        private static void Quit()
        {
            Core.Log.Msg("[操作] 点击【确定】，退出游戏。");
            try { Application.Quit(); } catch { }
        }

        private static bool Alive(UnityEngine.Object o)
        {
            try { return o != null; } catch { return false; }
        }
    }
}
