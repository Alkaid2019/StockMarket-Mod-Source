using System;
using System.Collections.Generic;
using System.Text;

namespace StockMarket
{
    /// <summary>
    /// 基本面：每支标的背后那家公司的「底子」。
    ///
    /// 它是一条**慢变量**——每隔几个运营周期才出一份新财报，出的那一下才挪一次
    /// 合理估值中枢（FairValue 上的基本面乘数）。价格短期的趋势和噪声照旧由
    /// StockEngine 管，基本面只负责把「这家公司到底值多少」这条底线慢慢抬上去或压下来。
    /// 这样基本面才是拿来「看」的，不是拿来「算当天涨跌」的，玩家也不会变成盯财报套利。
    ///
    /// 与席位、回溯行情一样，全部由 (标的Id + 期号) 的确定性函数算出来，**不落存档**：
    /// 读档、快进、回档看到的财报都是同一份。
    /// </summary>
    public static class StockFundamentals
    {
        /// <summary>出财报的间隔（天）。3 个运营周期一份，太快就没有「季度」的感觉。</summary>
        public const int PeriodDays = 21;

        /// <summary>一份财报。</summary>
        public sealed class Fin
        {
            public int Period;          // 期号，从 1 开始
            public int Day;             // 这一期的第一天
            public double Revenue;      // 本期营收（元）
            public double Profit;       // 本期净利（元）
            public double PrevRevenue;  // 上期营收
            public double PrevProfit;   // 上期净利
            public double DebtRatio;    // 资产负债率 0..1
            public double Shares;       // 流通股本（股）
            public double BookValue;    // 每股净资产（元）

            public double RevenueGrowth { get { return PrevRevenue > 0 ? Revenue / PrevRevenue - 1.0 : 0.0; } }
            public double ProfitGrowth { get { return PrevProfit > 0 ? Profit / PrevProfit - 1.0 : 0.0; } }
            public double Eps { get { return Shares > 0 ? Profit / Shares : 0.0; } }
            public double NetMargin { get { return Revenue > 0 ? Profit / Revenue : 0.0; } }
        }

        // ── 期号 ──────────────────────────────────────────────────────
        public static int PeriodIndex()
        {
            int p = StockState.Today / PeriodDays + 1;
            return p < 1 ? 1 : p;
        }

        /// <summary>当前这一期的财报。</summary>
        public static Fin Current(string id)
        {
            return At(id, PeriodIndex());
        }

        /// <summary>指定期号的财报。</summary>
        public static Fin At(string id, int period)
        {
            StockDef def = StockDefs.Get(id);
            if (def == null) return null;
            if (period < 1) period = 1;
            return Build(def, period);
        }

        /// <summary>
        /// 基本面乘数：加在 FairValue 上的那一个因子。
        /// 逻辑很直白——利润同比变好、负债不高，公司就值更多钱；反之打折。
        /// 幅度夹在 0.80~1.25，避免某一家公司把整支股票的锚点拉飞。
        /// </summary>
        public static double ValuationFactor(string id)
        {
            Fin f = Current(id);
            if (f == null) return 1.0;

            double growth = f.ProfitGrowth;
            if (growth > 0.60) growth = 0.60;
            if (growth < -0.60) growth = -0.60;

            // 负债率 50% 是中性线，超了扣分、低了加分
            double debt = (f.DebtRatio - 0.50) * 0.40;

            double factor = 1.0 + growth * 0.45 - debt;
            if (factor > 1.25) factor = 1.25;
            if (factor < 0.80) factor = 0.80;
            return factor;
        }

        /// <summary>本期是不是刚换过财报（换期当天界面可以提示一句）。</summary>
        public static bool IsFreshReport()
        {
            return StockState.Today % PeriodDays == 0;
        }

        // ── 财报的生成 ────────────────────────────────────────────────

        private static Fin Build(StockDef def, int period)
        {
            return Raw(def, period, Raw(def, period - 1, null));
        }

        /// <summary>
        /// 造一份财报。prev 传 null 表示「上期」也没得参考（只有第 0 期是这样，
        /// 那是内部用来当第 1 期对比基准的虚拟期，界面上永远不会显示）。
        /// 所以第 1 期的同比是拿两份独立抽样比出来的，天然有正有负，不会整表一起红或一起绿。
        /// </summary>
        private static Fin Raw(StockDef def, int period, Fin prev)
        {
            if (period < 0) period = 0;

            double scale = def.BasePrice * 12000.0;
            bool black = def.Category == StockCategory.BlackMarket;

            uint seed = StockState.Hash32(def.Id + "|fin|" + period);
            // 营收：平稳里带波动，黑市那几家大起大落
            double swing = black ? 0.55 : 0.28;
            double rev = scale * (0.80 + StockState.NextUnit(ref seed) * 0.90)
                       * (1.0 + (StockState.NextUnit(ref seed) - 0.5) * 2.0 * swing);

            // 净利率：空间站股厚（8%~26%），黑市股票宽（-12%~40%），亏钱的公司也得有
            double margin = black
                ? -0.12 + StockState.NextUnit(ref seed) * 0.52
                : 0.08 + StockState.NextUnit(ref seed) * 0.18;
            double profit = rev * margin;

            // 负债率：治安部/上层区公司底子厚，革命军与黑市借得多
            double debtBase = def.Region == "黑市" ? 0.62
                : (def.Region == "革命军" ? 0.55
                : (def.Region == "治安部" ? 0.38 : 0.45));
            double debt = debtBase + (StockState.NextUnit(ref seed) - 0.5) * 0.24;
            if (debt < 0.05) debt = 0.05;
            if (debt > 0.92) debt = 0.92;

            double shares = StockBots.FloatCapShares;
            Fin f = new Fin
            {
                Period = period,
                Day = period * PeriodDays,
                Revenue = rev,
                Profit = profit,
                DebtRatio = debt,
                Shares = shares,
                BookValue = 0.0
            };

            if (prev != null)
            {
                f.PrevRevenue = prev.Revenue;
                f.PrevProfit = prev.Profit;
            }
            else
            {
                f.PrevRevenue = rev * 0.88;
                f.PrevProfit = profit * 0.85;
            }

            // 每股净资产：拿基价和负债率粗略倒推，让 PB 有个合理的量级
            f.BookValue = Math.Max(0.5, def.BasePrice * (1.0 - debt * 0.6));
            return f;
        }

        // ── 给界面用的文字 ────────────────────────────────────────────

        /// <summary>
        /// 市盈率（按当前市价 / 年化每股收益）。亏损的公司返回 0。
        /// 这里把一期当成「一个季度」来年化（×4）——游戏里一期是 21 天，
        /// 按真实日历折算会算出个位数倍的市盈率，看着假，不如按季度口径。
        /// </summary>
        public static double Pe(string id)
        {
            Fin f = Current(id);
            if (f == null || f.Eps <= 0.0) return 0.0;
            double price = StockEngine.LivePriceYuan(id);
            return price / (f.Eps * 4.0);
        }

        /// <summary>市净率。</summary>
        public static double Pb(string id)
        {
            Fin f = Current(id);
            if (f == null || f.BookValue <= 0.0) return 0.0;
            return StockEngine.LivePriceYuan(id) / f.BookValue;
        }

        /// <summary>一张财报的摘要行（营收 / 净利 / 负债，带同比）。</summary>
        public static string ReportText(string id)
        {
            Fin f = Current(id);
            if (f == null) return string.Empty;
            StringBuilder sb = new StringBuilder();
            sb.Append("营收 ").Append(Money(f.Revenue)).Append("　同比 ")
              .Append(Pct(f.RevenueGrowth)).Append('\n');
            sb.Append("净利 ").Append(Money(f.Profit)).Append("　同比 ")
              .Append(Pct(f.ProfitGrowth)).Append("　净利率 ")
              .Append((f.NetMargin * 100.0).ToString("0.0")).Append("%\n");
            double debt = f.DebtRatio * 100.0;
            sb.Append("资产负债率 ").Append(debt.ToString("0.0")).Append("%　每股收益 ")
              .Append(f.Eps.ToString("0.000")).Append(" 元　每股净资产 ")
              .Append(f.BookValue.ToString("0.00")).Append(" 元");
            return sb.ToString();
        }

        /// <summary>
        /// 股东名单：复用游戏自己的派系和身份，不再另造一批人名。
        /// 大股东跟着层区走（治安部就是军需口，黑市就是蛇头集团），
        /// 后面挂上「正在盯这支标的」的机构席位，最后是散户合计。
        /// </summary>
        public static string ShareholderText(string id)
        {
            StockDef def = StockDefs.Get(id);
            if (def == null) return string.Empty;

            List<string> holders = new List<string>();
            holders.Add(MajorHolder(def.Region) + "　35%");

            List<string> insts = StockBots.InstitutionsOn(id);
            for (int i = 0; i < insts.Count; i++)
            {
                holders.Add(insts[i] + "　" + (18 - i * 4) + "%");
            }
            if (insts.Count == 0) holders.Add("产业资本　12%");
            holders.Add("散户与其他　" + Math.Max(5, 30 - insts.Count * 6) + "%");

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < holders.Count; i++)
            {
                if (i > 0) sb.Append("　·　");
                sb.Append(holders[i]);
            }
            return sb.ToString();
        }

        private static string MajorHolder(string region)
        {
            switch (region)
            {
                case "下层区": return "下层区商会";
                case "上层区": return "上层区家族基金";
                case "治安部": return "治安部军需处";
                case "黑市": return "黑市蛇头集团";
                case "革命军": return "革命军后勤委员会";
                default: return "站方国资";
            }
        }

        /// <summary>
        /// 公告：从这份财报和当前挂着的随机事件里推出来的两三条短讯。
        /// 财报越夸张，公告口气越重，玩家扫一眼就知道这家公司出什么事了。
        /// </summary>
        public static string NoticeText(string id)
        {
            Fin f = Current(id);
            if (f == null) return string.Empty;
            StringBuilder sb = new StringBuilder();
            sb.Append("【第 ").Append(f.Period).Append(" 期财报】本期已发布\n");

            double pg = f.ProfitGrowth;
            if (pg >= 0.15) sb.Append("· 业绩预增：净利同比 ").Append(Pct(pg)).Append("，好于市场预期\n");
            else if (pg <= -0.15) sb.Append("· 业绩预警：净利同比 ").Append(Pct(pg)).Append("，管理层称将收缩开支\n");
            else sb.Append("· 经营平稳：净利同比 ").Append(Pct(pg)).Append("，与上期基本持平\n");

            if (f.Profit > 0)
            {
                // 分红预案：赚钱的公司才谈得上派息，比例跟净利率挂钩
                double per10 = Math.Max(0.1, f.Eps * 3.0);
                sb.Append("· 分红预案：拟每 10 股派 ").Append(per10.ToString("0.00")).Append(" 元\n");
            }
            else
            {
                sb.Append("· 风险提示：本期亏损，公司暂不安排分红\n");
            }

            double debt = f.DebtRatio;
            if (debt >= 0.70) sb.Append("· 负债偏高：资产负债率 ").Append((debt * 100.0).ToString("0.0")).Append("%，融资成本上升\n");
            else if (debt <= 0.30) sb.Append("· 财务稳健：资产负债率仅 ").Append((debt * 100.0).ToString("0.0")).Append("%\n");

            string ev = EventLine(id);
            if (ev.Length > 0) sb.Append(ev);
            return sb.ToString();
        }

        /// <summary>把当前挂着的随机事件，翻译成一条「公司相关」的公告行。</summary>
        private static string EventLine(string id)
        {
            for (int i = 0; i < StockState.Events.Count; i++)
            {
                ActiveEvent ev = StockState.Events[i];
                StockEventDef sd = StockDefs.GetEvent(ev.DefId);
                if (sd == null || !StockEngine.TargetsStockPublic(sd, ev, id)) continue;
                return "· 相关事件：" + sd.Name + "　" + StockEngine.SeverityText(sd, ev.DaysLeft) + "\n";
            }
            return string.Empty;
        }

        // ── 小工具 ────────────────────────────────────────────────────
        private static string Pct(double v)
        {
            return (v >= 0 ? "+" : "") + (v * 100.0).ToString("0.0") + "%";
        }

        private static string Money(double yuan)
        {
            double abs = Math.Abs(yuan);
            if (abs >= 100000000.0) return (yuan / 100000000.0).ToString("0.00") + " 亿";
            if (abs >= 10000.0) return (yuan / 10000.0).ToString("0.0") + " 万";
            return yuan.ToString("0") + " 元";
        }
    }
}