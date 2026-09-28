using System;
using System.Collections.Generic;
using System.Text;

namespace StockMarket
{
    /// <summary>
    /// 市场里的「其他人」：散户、机构、公司操盘手。
    ///
    /// 这一层不参与任何结算，唯一产出是**每支标的每天的净流入（元）**。
    /// 净流入再喂回两处：
    ///   · 日线 NextPrice —— 作为一个不大的乘数项，让「有人在买」这件事真的推动价格；
    ///   · 日内 BuildPath —— 映射成一条微微上倾 / 下倾的斜坡，盘面上看得见资金痕迹。
    ///
    /// 为什么不做逐笔席位撮合：真正要影响玩家决策的只有「净流入这个数」，
    /// 拆到每一笔既吃性能，也没人看得见（逐笔明细是四期付费「资金透视」的内容）。
    ///
    /// 席位状态**完全不落存档**：所有行为都是 (席位Id + 标的Id + 天数) 的确定性函数，
    /// 读档按同一天重算就逐位一致，跟分时路径、回溯行情一个套路，省掉一套序列化格式。
    /// </summary>
    public static class StockBots
    {
        /// <summary>席位角色。</summary>
        public enum SeatKind
        {
            Retail = 0,      // 散户：追涨杀跌，听消息就冲
            Institution = 1, // 机构：逆势、看价值，动作慢
            Operator = 2     // 操盘手：顺势加码，靠拉抬/砸盘吃饭
        }

        private sealed class BotSeat
        {
            public string Id;
            public string Name;
            public SeatKind Kind;
            public double Ticket;      // 单日基础成交量（元）
            public double Follow;      // 对「涨跌 + 趋势」的跟随权重（正=追涨，负=抄底）
            public double NewsWeight;  // 对消息的反应强度
            public string[] Locked;    // 只做这几支；null = 全市场都做
        }

        // 席位表。散户多而散，机构少而重，操盘手窄而猛。
        // 机构/操盘手都锁定特定标的——现实里机构不会今天买快餐明天买军械，
        // 锁仓还能保证「每支标的都有人管」，不至于冷门股一片死水。
        private static readonly BotSeat[] Seats =
        {
            // ── 散户 8 席：全市场乱窜，涨了追、跌了跑 ──
            new BotSeat { Id = "R1", Name = "街头散户",   Kind = SeatKind.Retail, Ticket = 2600, Follow =  0.85, NewsWeight = 1.30, Locked = null },
            new BotSeat { Id = "R2", Name = "跟风大妈",   Kind = SeatKind.Retail, Ticket = 3200, Follow =  1.00, NewsWeight = 1.55, Locked = null },
            new BotSeat { Id = "R3", Name = "退休老头",   Kind = SeatKind.Retail, Ticket = 2400, Follow =  0.70, NewsWeight = 0.90, Locked = null },
            new BotSeat { Id = "R4", Name = "白领小散",   Kind = SeatKind.Retail, Ticket = 3600, Follow =  0.80, NewsWeight = 1.10, Locked = null },
            new BotSeat { Id = "R5", Name = "杠杆青年",   Kind = SeatKind.Retail, Ticket = 5200, Follow =  1.25, NewsWeight = 1.45, Locked = null },
            new BotSeat { Id = "R6", Name = "技术派散户", Kind = SeatKind.Retail, Ticket = 3000, Follow =  0.95, NewsWeight = 0.75, Locked = null },
            new BotSeat { Id = "R7", Name = "打新散户",   Kind = SeatKind.Retail, Ticket = 2000, Follow =  0.55, NewsWeight = 1.20, Locked = null },
            new BotSeat { Id = "R8", Name = "隔壁店主",   Kind = SeatKind.Retail, Ticket = 2200, Follow =  0.60, NewsWeight = 0.85, Locked = null },

            // ── 机构 4 席：各自盯自己的行业，逆向操作，仓位重 ──
            new BotSeat { Id = "I1", Name = "民生消费基金", Kind = SeatKind.Institution, Ticket = 42000, Follow = -0.45, NewsWeight = 0.35, Locked = new[] { "LL", "KFC", "AGRI", "PEPS" } },
            new BotSeat { Id = "I2", Name = "科技成长基金", Kind = SeatKind.Institution, Ticket = 56000, Follow = -0.55, NewsWeight = 0.45, Locked = new[] { "MID", "HW", "APPL" } },
            new BotSeat { Id = "I3", Name = "奢侈品配置盘", Kind = SeatKind.Institution, Ticket = 68000, Follow = -0.40, NewsWeight = 0.30, Locked = new[] { "UL", "CHAN", "TOUR", "HOTEL" } },
            new BotSeat { Id = "I4", Name = "矿能重仓盘",   Kind = SeatKind.Institution, Ticket = 74000, Follow = -0.35, NewsWeight = 0.40, Locked = new[] { "ENER", "BMW", "BENZ" } },

            // ── 操盘手 3 席：各自坐庄一片，顺势猛拉猛砸 ──
            new BotSeat { Id = "O1", Name = "黑市操盘手", Kind = SeatKind.Operator, Ticket = 96000, Follow = 1.75, NewsWeight = 1.60, Locked = new[] { "BM", "FORGE", "GUN", "CART", "DRUG" } },
            new BotSeat { Id = "O2", Name = "革命军资金", Kind = SeatKind.Operator, Ticket = 64000, Follow = 1.55, NewsWeight = 1.35, Locked = new[] { "RMED", "RCEL", "REV" } },
            new BotSeat { Id = "O3", Name = "治安部关联", Kind = SeatKind.Operator, Ticket = 58000, Follow = 1.40, NewsWeight = 1.20, Locked = new[] { "SEC", "RAY", "BOEI", "BANK" } },
        };

        // 流通盘量级：净流入占「盘子」的比例，才是对价格有意义的那个数。
        // 用基价乘一个固定股数，跟 StockIndicators 合成成交量的量级是同一套思路。
        // 基本面模块也拿它当流通股本，两边必须是同一个数，否则净利率会算飞。
        public const double FloatCapShares = 90000.0;

        // 净流入对价格的贡献系数。刻意压得很小（±1%）：
        // 收盘价还得靠趋势 + 噪声 + 均值回归撑着，资金流只是「有人在做」的那点推力，
        // 不然玩家盯住净流入就能反推收盘价，等于白送套利。
        public const double PriceImpact = 0.12;
        public const double PathImpact = 0.22;      // 日内斜坡：比日线更明显一点，盘面上要看得见

        // ── 缓存（同一天只算一次）──────────────────────────────────────
        private static int _flowDay = -1;
        private static readonly Dictionary<string, double> _flowYuan = new Dictionary<string, double>();

        /// <summary>今天的净流入（元）是否已经算好。</summary>
        private static void EnsureFlow()
        {
            if (_flowDay == StockState.Today && _flowYuan.Count > 0) return;
            _flowDay = StockState.Today;
            _flowYuan.Clear();
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef def = StockDefs.All[i];
                if (def == null) continue;
                _flowYuan[def.Id] = ComputeFlow(def);
            }
        }

        /// <summary>让外部在开盘前把它算好（避免第一帧现算）。</summary>
        public static void BeginDay()
        {
            EnsureFlow();
        }

        /// <summary>换档 / 重建时清缓存。</summary>
        public static void Reset()
        {
            _flowDay = -1;
            _flowYuan.Clear();
        }

        /// <summary>某支标的今天的净流入（元）。正 = 净买入，负 = 净卖出。</summary>
        public static double NetInflowYuan(string id)
        {
            EnsureFlow();
            double v;
            return _flowYuan.TryGetValue(id, out v) ? v : 0.0;
        }

        /// <summary>净流入占流通盘的比例，已限量到 ±8%。喂给定价与路径的那一项。</summary>
        public static double NetFlowPct(string id)
        {
            StockDef def = StockDefs.Get(id);
            double cap = (def != null && def.BasePrice > 0 ? def.BasePrice : 10.0) * FloatCapShares;
            double pct = NetInflowYuan(id) / cap;
            if (pct > 0.08) pct = 0.08;
            if (pct < -0.08) pct = -0.08;
            return pct;
        }

        /// <summary>
        /// 净流入对收盘价的贡献（元）。加在 NextPrice 的和式里。
        /// </summary>
        public static double PriceTermYuan(string id, double currentYuan)
        {
            return currentYuan * NetFlowPct(id) * PriceImpact;
        }

        /// <summary>今日净流入的一句话，给界面显示用。</summary>
        public static string FlowText(string id)
        {
            double y = NetInflowYuan(id);
            double w = y / 10000.0;
            if (Math.Abs(w) >= 0.05) return (w > 0 ? "+" : "") + w.ToString("0.0") + "万";
            return (y > 0 ? "+" : "") + y.ToString("0") + "元";
        }

        /// <summary>今日各标的净流入排名（金额从高到低），给「资金动向」那一行用。</summary>
        public static List<string> TopFlowIds(int count)
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < StockDefs.All.Length; i++) ids.Add(StockDefs.All[i].Id);
            ids.Sort((a, b) => Math.Abs(NetInflowYuan(b)).CompareTo(Math.Abs(NetInflowYuan(a))));
            if (ids.Count > count) ids.RemoveRange(count, ids.Count - count);
            return ids;
        }

        /// <summary>净流入最大的几支，一行文字（调试页用，验证席位确实在动）。</summary>
        public static string TopFlowText(int count)
        {
            List<string> ids = TopFlowIds(count);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                StockDef def = StockDefs.Get(ids[i]);
                if (def == null) continue;
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append(def.Name).Append(' ').Append(FlowText(ids[i]));
            }
            return sb.Length > 0 ? sb.ToString() : "—";
        }

        /// <summary>正在盯这支标的的机构席位名字（财报页的股东名单要用）。</summary>
        public static List<string> InstitutionsOn(string id)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < Seats.Length; i++)
            {
                BotSeat seat = Seats[i];
                if (seat.Kind != SeatKind.Institution) continue;
                if (seat.Locked != null && Contains(seat.Locked, id)) names.Add(seat.Name);
            }
            return names;
        }

        /// <summary>在这支标的上坐庄的操盘手；没人坐庄返回 null。传闻与控评都挂在它身上。</summary>
        public static string OperatorOn(string id)
        {
            for (int i = 0; i < Seats.Length; i++)
            {
                BotSeat seat = Seats[i];
                if (seat.Kind != SeatKind.Operator) continue;
                if (seat.Locked != null && Contains(seat.Locked, id)) return seat.Name;
            }
            return null;
        }

        /// <summary>席位的组成（散户几席 / 机构几家 / 操盘手几家），调试页与资讯页显示用。</summary>
        public static string SeatSummary()
        {
            int retail = 0, inst = 0, op = 0;
            for (int i = 0; i < Seats.Length; i++)
            {
                if (Seats[i].Kind == SeatKind.Retail) retail++;
                else if (Seats[i].Kind == SeatKind.Institution) inst++;
                else op++;
            }
            return "散户 " + retail + " 席　机构 " + inst + " 家　操盘手 " + op + " 家";
        }

        // ── 核心：一个席位今天想对某支标的净买多少 ────────────────────

        private static double ComputeFlow(StockDef def)
        {
            // 昨天涨了多少 —— 散户的反应全在这上面
            double yesterday = 0.0;
            List<long> hist;
            if (StockState.PriceHistory.TryGetValue(def.Id, out hist) && hist != null && hist.Count >= 2)
            {
                long prev = hist[hist.Count - 2];
                long last = hist[hist.Count - 1];
                if (prev > 0) yesterday = (double)(last - prev) / prev;
            }

            int trend = StockEngine.TrendOf(def.Id);
            double trendScore = trend == 0 ? 1.0 : (trend == 1 ? -1.0 : 0.0);

            // 消息面：正在挂着的随机事件对这支标的的冲击，正=利好
            double news = StockEngine.ActiveImpactFor(def.Id);

            double sum = 0.0;
            for (int s = 0; s < Seats.Length; s++)
            {
                BotSeat seat = Seats[s];
                if (seat.Locked != null && !Contains(seat.Locked, def.Id)) continue;

                // 意图分：涨跌 + 趋势 + 消息，各自乘上这个席位的性格权重
                double mood = seat.Follow * (yesterday * 22.0 + trendScore * 0.50)
                            + seat.NewsWeight * news * 4.0
                            + (Unit(seat.Id + "|m|" + def.Id) - 0.5) * 0.95;
                if (mood > 1.0) mood = 1.0;
                if (mood < -1.0) mood = -1.0;

                // 今天动不动手：不是每天都交易，掷一次骰子
                double act = Unit(seat.Id + "|a|" + def.Id);
                double size = act < 0.42 ? 0.0 : seat.Ticket * (0.35 + act * 1.35);

                sum += mood * size;
            }
            return sum;
        }

        private static bool Contains(string[] set, string id)
        {
            for (int i = 0; i < set.Length; i++)
            {
                if (set[i] == id) return true;
            }
            return false;
        }

        /// <summary>席位Id + 标的Id + 天数 定死的伪随机，读档重算结果一致。</summary>
        private static double Unit(string key)
        {
            uint seed = StockState.Hash32(key + "|" + StockState.Today);
            return StockState.NextUnit(ref seed);
        }
    }
}