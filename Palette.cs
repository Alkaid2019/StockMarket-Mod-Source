using System;
using UnityEngine;

namespace StockMarket
{
    /// <summary>
    /// 界面配色表。取自 PSPDA 的深蓝冷色系，另加红涨绿跌等语义色。
    /// 所有颜色都从这里取，不在各处写死，方便后续统一调。
    ///
    /// 两套风格：深色（默认，深蓝夜）和亮色（浅灰蓝日）。字段是可变 static，
    /// 切风格就是 Palette.SetTheme() 重新刷一遍值 —— 颜色是建界面时烙进
    /// Image/Text 里的，所以切完必须把整个面板拆了重建（见 StockUI.RebuildTheme）。
    /// </summary>
    internal static class Palette
    {
        public enum Theme { Dark, Light }

        private static Theme _theme = Theme.Dark;
        public static bool IsLight { get { return _theme == Theme.Light; } }
        public static Theme Current { get { return _theme; } }

        // ── 底色与面板 ────────────────────────────────────────────────
        public static Color PageBg;    // 页面底
        public static Color CardBg;    // 卡片纸面
        public static Color SlotBg;    // 卡内嵌槽（表格底、图表底）
        public static Color CardRim;   // 卡片描边
        public static Color Divider;   // 分隔线

        // ── 文字 ──────────────────────────────────────────────────────
        public static Color Title;     // 页面/卡片标题
        public static Color Body;      // 正文
        public static Color CardBody;  // 卡片正文
        public static Color Sub;       // 次级
        public static Color Muted;     // 弱化/说明
        public static Color Gold;      // 琥珀金数值
        public static Color GoldSoft;  // 提示金
        public static Color Danger;    // 危险
        public static Color Cyan;      // 青色点缀

        // ── 涨跌语义色（A 股习惯：红涨绿跌）─────────────────────────
        public static Color Up;
        public static Color Down;
        public static Color Flat;

        // ── 按钮底 ────────────────────────────────────────────────────
        public static Color BtnIdle;   // 普通/步进
        public static Color BtnBlue;   // 主操作（买入、开户）
        public static Color BtnGreen;  // 转入
        public static Color BtnOrange; // 提现/卖出
        public static Color BtnRed;    // 清仓
        public static Color NavOn;     // 侧栏选中
        public static Color Disabled;  // 禁用
        public static Color DisabledFg;

        // ── 图表 ──────────────────────────────────────────────────────
        public static Color Series1;   // 总资产
        public static Color Series2;   // 可用资金
        public static Color Series3;   // 持仓市值
        public static Color Grid;

        // ── 引导专用 ──────────────────────────────────────────────────
        /// <summary>压暗用的底色（不含透明度），亮色下换深灰蓝而不是纯黑。</summary>
        public static Color DimBase;
        /// <summary>说明卡背后那层半透明挡板。</summary>
        public static Color Scrim;
        /// <summary>引导气泡的纸面：刻意跟卡片纸面拉开，免得糊成一片。</summary>
        public static Color TutBg;
        /// <summary>引导气泡的描边。</summary>
        public static Color TutRim;

        static Palette()
        {
            Apply(Theme.Dark);
        }

        /// <summary>切风格。切完界面颜色不会自己变，得重建面板。</summary>
        public static void SetTheme(Theme t)
        {
            Apply(t);
        }

        private static void Apply(Theme t)
        {
            _theme = t;
            if (t == Theme.Light)
            {
                // 亮色：浅灰蓝页面 + 纯白卡片 + 深墨蓝文字。
                // 强调色一律走「浅底 + 深字」，因为按钮前景色取的是 Title，
                // 亮色下 Title 是深色，压在饱和深色底上就看不见了。
                PageBg = Hex("DDE6F0");
                CardBg = Hex("FFFFFF");
                SlotBg = Hex("EFF4F9");
                CardRim = Hex("A9BFD3");
                Divider = Hex("C2D3E0");

                Title = Hex("17293A");
                Body = Hex("2C3E50");
                CardBody = Hex("33465A");
                Sub = Hex("44586C");
                Muted = Hex("6B7F94");
                Gold = Hex("A87514");
                GoldSoft = Hex("8A6A2E");
                Danger = Hex("B03A16");
                Cyan = Hex("2C7A9E");

                Up = Hex("D93A1E");
                Down = Hex("1E8F5F");
                Flat = Hex("6B7F94");

                BtnIdle = Hex("D9E3EE");
                BtnBlue = Hex("9CC6E4");
                BtnGreen = Hex("A2D6BE");
                BtnOrange = Hex("EAC79A");
                BtnRed = Hex("E8A392");
                NavOn = Hex("B6D5EA");
                Disabled = Hex("CCD6E0");
                DisabledFg = Hex("8C9CAD");

                Series1 = Hex("2C7A9E");
                Series2 = Hex("1E8F5F");
                Series3 = Hex("A87514");
                Grid = new Color(0f, 0f, 0f, 0.13f);

                DimBase = new Color(0.10f, 0.14f, 0.20f, 1f);
                Scrim = new Color(0.10f, 0.14f, 0.20f, 0.44f);
                TutBg = Hex("FFF6E0");
                TutRim = Hex("D2A02A");
            }
            else
            {
                PageBg = Hex("1B2D3F");
                CardBg = Hex("1B2836");
                SlotBg = Hex("162230");
                CardRim = Hex("4F7391");
                Divider = Hex("59809E");

                Title = Hex("F5FAFF");
                Body = Hex("DEE8F5");
                CardBody = Hex("CCDEF2");
                Sub = Hex("C7D9ED");
                Muted = Hex("9EB3CC");
                Gold = Hex("FFC24A");
                GoldSoft = Hex("F5CC94");
                Danger = Hex("D05020");
                Cyan = Hex("9EC7DE");

                Up = Hex("E5533D");
                Down = Hex("45C08A");
                Flat = Hex("9EB3CC");

                BtnIdle = Hex("25384A");
                BtnBlue = Hex("2E6C93");
                BtnGreen = Hex("2E7D5B");
                BtnOrange = Hex("8A5A2E");
                BtnRed = Hex("B0431C");
                NavOn = Hex("2E5E80");
                Disabled = Hex("4D596B");
                DisabledFg = Hex("9EADC2");

                Series1 = Hex("9EC7DE");
                Series2 = Hex("45C08A");
                Series3 = Hex("FFC24A");
                Grid = new Color(1f, 1f, 1f, 0.10f);

                DimBase = new Color(0.02f, 0.03f, 0.06f, 1f);
                Scrim = new Color(0.02f, 0.03f, 0.06f, 0.55f);
                TutBg = Hex("23405C");
                TutRim = Hex("FFC24A");
            }
        }

        /// <summary>#RRGGBB → Color。</summary>
        public static Color Hex(string hex)
        {
            int r = 0, g = 0, b = 0;
            if (!string.IsNullOrEmpty(hex) && hex.Length >= 6)
            {
                try
                {
                    r = Convert.ToInt32(hex.Substring(0, 2), 16);
                    g = Convert.ToInt32(hex.Substring(2, 2), 16);
                    b = Convert.ToInt32(hex.Substring(4, 2), 16);
                }
                catch { }
            }
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        }

        /// <summary>
        /// Color → RichText 用的 #RRGGBB。图例这类富文本必须写死色值，
        /// 之前图例是手抄的十六进制，结果涨跌抄反了，所以统一从 Palette 反推。
        /// </summary>
        public static string HexOf(Color c)
        {
            int r = (int)(c.r * 255f + 0.5f);
            int g = (int)(c.g * 255f + 0.5f);
            int b = (int)(c.b * 255f + 0.5f);
            if (r < 0) r = 0; if (r > 255) r = 255;
            if (g < 0) g = 0; if (g > 255) g = 255;
            if (b < 0) b = 0; if (b > 255) b = 255;
            return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
        }

        /// <summary>
        /// 富文本里写死的深色主题色值 → 当前主题色值。
        /// 面板里大量文案用 &lt;color=#FFC24A&gt; 这类字面量标关键词，这些值是深色主题的，
        /// 切到亮色主题后压在白卡片上几乎看不见（见 问题截图/改变ui配色…）。
        /// 所以写进 TMP 之前统一过一遍：深色主题下原样返回，零开销。
        /// </summary>
        public static string Rt(string s)
        {
            if (string.IsNullOrEmpty(s) || _theme == Theme.Dark) return s;
            if (s.IndexOf('#') < 0) return s;
            return s
                .Replace("#FFC24A", HexOf(Gold))        // 金：关键词 / 数值 / 按钮名
                .Replace("#E5533D", HexOf(Up))          // 红：上涨 / 风险
                .Replace("#45C08A", HexOf(Down))        // 绿：下跌
                .Replace("#9EC7DE", HexOf(Cyan))        // 青：补充说明
                .Replace("#9EB3CC", HexOf(Muted))       // 灰：弱化说明
                .Replace("#8FA6BC", HexOf(Muted))       // 导航图标
                .Replace("#D05020", HexOf(Danger))      // 深红：危险
                .Replace("#6E819A", HexOf(DisabledFg)); // 未开通的小圆点
        }

        /// <summary>改透明度。</summary>
        public static Color A(Color c, float a)
        {
            return new Color(c.r, c.g, c.b, a);
        }

        /// <summary>提亮（t&gt;0）或压暗（t&lt;0）。</summary>
        public static Color Shift(Color c, float t)
        {
            if (t >= 0f)
            {
                return new Color(
                    c.r + (1f - c.r) * t,
                    c.g + (1f - c.g) * t,
                    c.b + (1f - c.b) * t,
                    c.a);
            }
            float k = 1f + t;
            return new Color(c.r * k, c.g * k, c.b * k, c.a);
        }

        /// <summary>盈亏数值的颜色：正红、负绿、平灰。</summary>
        public static Color Pnl(double value)
        {
            if (value > 0.0001) return Up;
            if (value < -0.0001) return Down;
            return Flat;
        }

        /// <summary>涨跌幅的颜色。</summary>
        public static Color Change(double percent)
        {
            return Pnl(percent);
        }
    }
}
