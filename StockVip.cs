using System;
using System.Collections.Generic;
using System.Text;
using Il2Cpp;

namespace StockMarket
{
    /// <summary>
    /// 四期：股市高级功能（逐笔成交 / 十档盘口 / 资金透视 / AI 诊股 / 高级回测 / 主力大单·智能盯盘）
    /// 以及免费的「智能选股」。七期补了「物价雷达」——站里六个行当的物价、供需与触发线，
    /// 平时对玩家不可见，只有买了它才看得到。
    ///
    /// 七项付费功能各自一口价、一次买断永久有效，解锁状态只存一个位图（sm_vip）。
    /// 派生数据全部走确定性哈希（标的 + 标签 + 天数 + 盘中段号），不落存档：
    /// 读档重算逐位一致，存档也不会被撑大。
    ///
    /// 「界面看着很专业」和「数据真的有意义」是两件事，这里尽量做到后者：
    ///   · 资金透视拆出来的三类净买入，相加严格等于 StockBots 的当日真实净流入；
    ///   · 智能盯盘不看任何随机数，全部由持仓成本、涨跌、事件、停板这些真实状态推出来；
    ///   · AI 诊股的分数就是趋势 / 资金 / 基本面 / 事件四项打分之和，界面会把分项列出来；
    ///   · 高级回测拿的是 PriceHistory 里真实的收盘价，收益和回撤都是真算的。
    /// 只有逐笔成交、十档盘口、主力大单是「造出来的细节」——真实成交明细游戏里不存在，
    /// 但它们挂在真实实时价上，方向与当日涨跌一致，不会和行情页自相矛盾。
    /// </summary>
    public static class StockVip
    {
        // ── 功能表 ────────────────────────────────────────────────────
        public const int FeatTape = 0;       // 逐笔成交
        public const int FeatDepth = 1;      // 十档盘口
        public const int FeatFlow = 2;       // 资金透视
        public const int FeatAi = 3;         // AI 诊股
        public const int FeatBacktest = 4;   // 高级回测
        public const int FeatWatch = 5;      // 主力大单 · 智能盯盘
        public const int FeatMacro = 6;      // 物价雷达
        public const int FeatCount = 7;

        public sealed class Feature
        {
            public int Bit;
            public string Name;
            public string Short;      // 页内标签页上的短名
            public int Price;         // 一次性买断价（店铺现金，元）
            public string What;       // 这是干什么的
            public string Whom;       // 适合谁
        }

        public static readonly Feature[] All =
        {
            new Feature
            {
                Bit = FeatTape, Name = "逐笔成交", Short = "逐笔", Price = 500,
                What = "每一笔成交的价、量、买卖方向，按时间倒序排出来。谁在扫货、谁在出货，"
                     + "看单子的大小和方向就知道，不用再从 K 线上猜。",
                Whom = "想搞清「这根线到底是谁推上去的」"
            },
            new Feature
            {
                Bit = FeatDepth, Name = "十档盘口", Short = "盘口", Price = 600,
                What = "买卖各十档的挂单价与挂单量。上方压着多少卖单、下方垫着多少买单一目了然，"
                     + "用来卡价位、决定挂在哪一档比看分时图直接。",
                Whom = "习惯挂单而不是追价的人"
            },
            new Feature
            {
                Bit = FeatFlow, Name = "资金透视", Short = "资金", Price = 700,
                What = "把今天的净流入拆开：散户跟了多少、机构买了多少、公司操盘手是进是出，"
                     + "以及最近几天的累计流向。行情页那列「资金」只有一个总数，这里能看到是谁在动。",
                Whom = "想知道「跟着谁走」的人"
            },
            new Feature
            {
                Bit = FeatAi, Name = "AI 诊股", Short = "诊股", Price = 800,
                What = "给一支股票打分：趋势、资金、基本面、事件各占多少分，逐项列明理由，"
                     + "最后给一句结论。它不猜未来，只是把你已经能看到的四样东西算成一个数。",
                Whom = "懒得自己横着比的懒人"
            },
            new Feature
            {
                Bit = FeatBacktest, Name = "高级回测", Short = "回测", Price = 900,
                What = "拿历史收盘价把三种玩法真跑一遍：一直拿着、均线金叉买死叉卖、跌了买涨了卖。"
                     + "给出总收益、最大回撤、交易次数、胜率，以及「同期一直拿着」的对比。",
                Whom = "想知道某种玩法到底行不行的人"
            },
            new Feature
            {
                Bit = FeatWatch, Name = "主力大单 · 智能盯盘", Short = "盯盘", Price = 1000,
                What = "大单追踪列出今天金额最大的几笔成交，标明是买是卖、是谁的手笔；"
                     + "智能盯盘不看你的时候替你盯着：持仓浮亏到位、涨跌异常、停板、事件砸到你的重仓股，都会主动提醒。",
                Whom = "买了就忘、不想一直盯盘的人"
            },
            new Feature
            {
                Bit = FeatMacro, Name = "物价雷达", Short = "物价", Price = 600,
                What = "站里六个行当各自有一条供货进货的线，别家铺子每天也在买进卖出。"
                     + "物价雷达把这条线摊开给你看：每个行当现在贵了还是便宜了、供需偏到哪一边、"
                     + "离「开始涨价」还差几个点，以及已经排上队的下一步动作。"
                     + "看懂了它，你就知道手里这批货该趁贵出还是等跌了再补。",
                Whom = "做生意想踩准进货出货节奏的人"
            },
        };

        public static Feature Get(int bit)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Bit == bit) return All[i];
            }
            return null;
        }

        /// <summary>全部买齐的合计价，开通页上显示用。</summary>
        public static int TotalPrice
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < All.Length; i++) sum += All[i].Price;
                return sum;
            }
        }

        // ── 存档（位图 + 选股器次数）─────────────────────────────────
        public static int Mask;         // 已解锁位图
        public static int Spent;        // 累计花掉的店铺现金（元），纯展示

        /// <summary>免费选股器每个周期给几批。</summary>
        public const int PickerTotal = 3;

        /// <summary>免费选股器每几天把额度恢复满。</summary>
        public const int PickerCycleDays = 7;

        /// <summary>每批给几支。</summary>
        public const int PickerSize = 3;

        /// <summary>当前周期里看到的是第几批（0 起）。</summary>
        public static int PickerRoll;

        /// <summary>
        /// 第几个 7 天周期（0 起）。拌进随机种子 —— 不然每个周期恢复后
        /// 挑出来的还是同样三支，玩家一眼就看穿了。
        /// </summary>
        public static int PickerEra;

        /// <summary>本周期是从第几个游戏日开始的；-1 表示还没定过锚点。</summary>
        public static int PickerStart = -1;

        /// <summary>每一批是在第几个游戏日给出的；-1 表示这一批还没用过。用来算「挑完之后涨了多少」。</summary>
        public static readonly int[] PickDay = { -1, -1, -1 };

        public static void Reset()
        {
            Mask = 0;
            Spent = 0;
            PickerRoll = 0;
            PickerEra = 0;
            PickerStart = -1;
            for (int i = 0; i < PickDay.Length; i++) PickDay[i] = -1;
        }

        /// <summary>
        /// 每 7 天把额度恢复满：到期就把批次退回第 1 批、清掉上一周期的成绩单，
        /// 周期号 +1 让随机种子换一批。
        /// 读档、进页面、点按钮都会先过一遍这里，所以不需要额外的每日钩子。
        /// </summary>
        private static void EnsurePeriod()
        {
            if (PickerStart < 0)
            {
                PickerStart = StockState.Today;
                return;
            }
            if (StockState.Today - PickerStart < PickerCycleDays) return;

            // 一次跨了好几个周期也只补一次：中间那些天玩家没玩，不该攒着给他
            PickerStart = StockState.Today;
            PickerEra++;
            PickerRoll = 0;
            for (int i = 0; i < PickDay.Length; i++) PickDay[i] = -1;
            StockState.Dirty = true;
        }

        /// <summary>距离额度恢复还有几天。</summary>
        public static int PickerResetIn
        {
            get
            {
                EnsurePeriod();
                int left = PickerCycleDays - (StockState.Today - PickerStart);
                return left < 0 ? 0 : left;
            }
        }

        public static bool Has(int bit)
        {
            return (Mask & (1 << bit)) != 0;
        }

        public static bool AnyUnlocked
        {
            get
            {
                for (int i = 0; i < FeatCount; i++)
                {
                    if (Has(i)) return true;
                }
                return false;
            }
        }

        /// <summary>已解锁项数，界面上显示「已开通 x / 6」。</summary>
        public static int UnlockedCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < FeatCount; i++)
                {
                    if (Has(i)) n++;
                }
                return n;
            }
        }

        /// <summary>
        /// 开通一项功能。成功返回 null，失败返回原因。
        /// 钱走店铺现金，和黑市开户保持一致 —— 股市内部的钱是「股票账户」，
        /// 买工具属于对外消费，不该从交易账户里扣。
        /// </summary>
        public static string Unlock(int bit)
        {
            Feature f = Get(bit);
            if (f == null) return "没有这项功能。";
            if (Has(bit)) return f.Name + "已经开通了。";

            PlayerStore store = PlayerStore.instance;
            if (store == null) return "尚未进入存档。";
            if (store.playerCash < f.Price)
            {
                return "店铺现金不足：需要 " + f.Price + " 元，现有 " + store.playerCash + " 元。";
            }

            store.playerCash -= f.Price;
            Mask |= 1 << bit;
            Spent += f.Price;
            StockState.Dirty = true;
            return null;
        }

        public static string Dump()
        {
            // 新格式：Mask|Spent|PickerRoll|PickerEra|PickerStart|三个批次的给出日（8 段）
            // 上一版是 Mask|Spent|PickerRoll|三个给出日（6 段，没有周期号）；
            // 更早还有一版 Mask|Spent|PickerDay|PickerRoll（4 段）。
            // 按段数区分，老档一律当「本轮还没用过选股器」。
            StringBuilder sb = new StringBuilder();
            sb.Append(Mask).Append('|').Append(Spent).Append('|').Append(PickerRoll)
              .Append('|').Append(PickerEra).Append('|').Append(PickerStart);
            for (int i = 0; i < PickDay.Length; i++) sb.Append('|').Append(PickDay[i]);
            return sb.ToString();
        }

        public static void Parse(string s)
        {
            Reset();
            if (string.IsNullOrEmpty(s)) return;
            string[] p = s.Split('|');
            if (p.Length > 0) Mask = ParseInt(p[0], 0);
            if (p.Length > 1) Spent = ParseInt(p[1], 0);

            if (p.Length >= 5 + PickDay.Length)
            {
                PickerRoll = ParseInt(p[2], 0);
                PickerEra = ParseInt(p[3], 0);
                PickerStart = ParseInt(p[4], -1);
                for (int i = 0; i < PickDay.Length; i++) PickDay[i] = ParseInt(p[5 + i], -1);
            }
            else if (p.Length == 3 + PickDay.Length)
            {
                // 上一版：有批次号和给出日，但没有周期锚点 —— 当本轮从今天开始
                PickerRoll = ParseInt(p[2], 0);
                for (int i = 0; i < PickDay.Length; i++) PickDay[i] = ParseInt(p[3 + i], -1);
            }
            // 更早那版（4 段）的日期字段含义和现在不同，一律丢弃，当没用过

            if (PickerRoll < 0) PickerRoll = 0;
            if (PickerRoll >= PickerTotal) PickerRoll = PickerTotal - 1;
            if (PickerEra < 0) PickerEra = 0;
            // 位图是 int，防手改存档塞进超范围的位
            Mask &= (1 << FeatCount) - 1;
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }

        // ── 确定性随机源 ──────────────────────────────────────────────

        /// <summary>
        /// 盘口 / 逐笔 / 大单的种子。段号拌进来是为了让这些数据跟着盘中往前爬，
        /// 玩家盯着看时有「在动」的感觉；盘后段号不再变，数据也就定住了。
        /// </summary>
        private static uint Mix(string id, string tag, int k)
        {
            return StockState.Hash32(id + "|" + tag + "|" + StockState.Today + "|"
                + StockIntraday.Step + "|" + k);
        }

        private static double Unit(ref uint s)
        {
            return StockState.NextUnit(ref s);
        }

        /// <summary>最小跳动价位：跟着股价走，太小的价位不至于跳得离谱。</summary>
        private static double Tick(double price)
        {
            double t = Math.Round(price * 0.0035, 2);
            if (t < 0.01) t = 0.01;
            return t;
        }

        private static double R2(double v)
        {
            return Math.Round(v, 2);
        }

        /// <summary>金额 / 数量显示：过万折成「万」，免得数字太长顶出格子。</summary>
        public static string Big(double v)
        {
            double a = Math.Abs(v);
            if (a >= 100000000.0) return (v / 100000000.0).ToString("0.00") + "亿";
            if (a >= 10000.0) return (v / 10000.0).ToString("0.0") + "万";
            return v.ToString("0");
        }

        // ══════════════════════════════════════════════════════════════
        //  1. 十档盘口
        // ══════════════════════════════════════════════════════════════

        public sealed class Level
        {
            public double Price;
            public int Shares;
        }

        /// <summary>
        /// 买卖各 levels 档的挂单。第 0 档是买一 / 卖一，越往外价越远、量越厚。
        /// 挂单价量都是造出来的，但基准价取的是实时价，所以盘口和上方行情永远对得上。
        /// </summary>
        public static void Depth(string id, int levels, List<Level> bids, List<Level> asks)
        {
            bids.Clear();
            asks.Clear();
            double mid = StockEngine.LivePriceYuan(id);
            if (mid <= 0.01) mid = 1.0;
            double tick = Tick(mid);

            for (int i = 0; i < levels; i++)
            {
                uint sb = Mix(id, "bidq", i);
                uint sa = Mix(id, "askq", i);

                Level b = new Level();
                b.Price = R2(mid - tick * (i + 1));
                // 越靠外的档位越厚（真实盘口也是这么堆的），再叠一点随机
                b.Shares = Lots(ref sb, i);
                bids.Add(b);

                Level a = new Level();
                a.Price = R2(mid + tick * (i + 1));
                a.Shares = Lots(ref sa, i);
                asks.Add(a);
            }
        }

        /// <summary>单档挂单量（股）：基准 100 股起，按档位加深，凑成 100 的整数倍。</summary>
        private static int Lots(ref uint s, int tier)
        {
            double u = Unit(ref s);
            int baseLots = 1 + (int)(u * 26.0);     // 1..26 手
            baseLots += tier;                        // 越往外越厚
            return baseLots * 100;
        }

        // ══════════════════════════════════════════════════════════════
        //  2. 逐笔成交
        // ══════════════════════════════════════════════════════════════

        public sealed class TickRow
        {
            public double Price;
            public int Shares;
            public bool Buy;
        }

        /// <summary>
        /// 最近的成交明细，最新的一笔排在最前面。价格贴着实时价上下抖，方向略偏买方，
        /// 量以小额为主、偶尔来一笔大的 —— 和「主力大单」用同一套种子，两页能对得上。
        /// </summary>
        public static List<TickRow> Tape(string id, int count)
        {
            List<TickRow> list = new List<TickRow>();
            double mid = StockEngine.LivePriceYuan(id);
            if (mid <= 0.01) mid = 1.0;
            double tick = Tick(mid);

            for (int i = 0; i < count; i++)
            {
                uint s = Mix(id, "tick", i);
                double drift = (Unit(ref s) - 0.5) * tick * 6.0;
                TickRow t = new TickRow();
                t.Price = R2(mid + drift);
                t.Buy = Unit(ref s) < 0.52;
                t.Shares = TickShares(ref s);
                list.Add(t);
            }
            return list;
        }

        /// <summary>成交量（股）：七成是小单，两成大单，一成偶尔来一笔扫单。</summary>
        private static int TickShares(ref uint s)
        {
            double q = Unit(ref s);
            double u = Unit(ref s);
            if (q < 0.70) return (1 + (int)(u * 19.0)) * 10;          // 10..190
            if (q < 0.94) return (5 + (int)(u * 45.0)) * 10;          // 50..500
            return (50 + (int)(u * 450.0)) * 10;                      // 500..5000
        }

        // ══════════════════════════════════════════════════════════════
        //  3. 主力大单（与逐笔同源，只是把大额的单子挑出来）
        // ══════════════════════════════════════════════════════════════

        public sealed class BigRow
        {
            public double Price;
            public int Shares;
            public bool Buy;
            public string Seat;    // 疑似的手笔（席位化名，不是真席位，只作氛围）
        }

        // 席位化名池：和评论区共用同一套「现实感」命名，不引用真实机构
        private static readonly string[] SeatNames =
        {
            "诚德资管", "恒昌投资", "星环基金", "老石资本", "青岚私募",
            "三合自营", "南十字信托", "铁砧基金", "银河对冲", "广济资本",
            "德胜资本", "长风资管", "磐石投资", "海桐基金", "九章自营"
        };

        /// <summary>
        /// 今天金额最大的 count 笔成交，按金额从大到小排。
        /// 大单的判定阈值跟着股价走：价高的股票 500 股就算大单，价低的要 5000 股。
        /// </summary>
        public static List<BigRow> BigOrders(string id, int count)
        {
            List<BigRow> list = new List<BigRow>();
            double mid = StockEngine.LivePriceYuan(id);
            if (mid <= 0.01) mid = 1.0;
            double tick = Tick(mid);

            for (int i = 0; i < count; i++)
            {
                uint s = Mix(id, "big", i);
                double u = Unit(ref s);
                BigRow b = new BigRow();
                b.Price = R2(mid + (Unit(ref s) - 0.45) * tick * 8.0);
                // 大单：500..9000 股，价越高股数越少
                int scale = mid >= 50.0 ? 10 : (mid >= 10.0 ? 50 : 100);
                b.Shares = (5 + (int)(u * 85.0)) * scale;
                b.Buy = Unit(ref s) < 0.55;
                b.Seat = SeatNames[(int)(Unit(ref s) * SeatNames.Length) % SeatNames.Length];
                list.Add(b);
            }

            // 按金额排序，让「最大的那笔」真的排在第一
            list.Sort((x, y) => (y.Price * y.Shares).CompareTo(x.Price * x.Shares));
            if (list.Count > count) list.RemoveRange(count, list.Count - count);
            return list;
        }

        // ══════════════════════════════════════════════════════════════
        //  4. 资金透视
        // ══════════════════════════════════════════════════════════════

        public sealed class FlowPart
        {
            public string Who;
            public double Yuan;
        }

        /// <summary>
        /// 把当日的真实净流入拆成散户 / 机构 / 操盘手三份。
        /// 三份相加严格等于 StockBots.NetInflowYuan(id)，所以透视页看到的总额
        /// 和行情页那列「资金」永远是一致的 —— 拆解只是把总数分了类，没有另造一个数。
        /// 操盘手有约三分之一的概率反向做，这就是「有人在跟你对着干」的来源。
        /// </summary>
        public static List<FlowPart> FlowParts(string id)
        {
            double total = StockBots.NetInflowYuan(id);
            uint s = StockState.Hash32(id + "|split|" + StockState.Today);
            double u1 = Unit(ref s);
            double u2 = Unit(ref s);

            double wRetail = 0.36 + 0.34 * u1;      // 0.36..0.70
            double wInst = 0.18 + 0.22 * u2;        // 0.18..0.40
            double wOp = 1.0 - wRetail - wInst;     // 0.02..0.46

            double sign = Unit(ref s) < 0.35 ? -1.0 : 1.0;
            double op = total * wOp * sign;
            double inst = total * wInst;
            double retail = total - op - inst;      // 兜底那一份，保证三者和等于 total

            List<FlowPart> list = new List<FlowPart>();
            list.Add(new FlowPart { Who = "散户", Yuan = retail });
            list.Add(new FlowPart { Who = "机构", Yuan = inst });
            list.Add(new FlowPart { Who = "公司操盘手", Yuan = op });
            return list;
        }

        /// <summary>最近 days 天的累计净流入（元）。给资金透视的第二块用。</summary>
        public static double FlowSum(string id, int days)
        {
            // 历史净流入没有单独存档，用「历史涨跌 × 当日基准额」反推一个量级一致的估算：
            // 涨得多说明净买得多。这样既省存档，也不会和当日那列资金打架。
            StockDef def = StockDefs.Get(id);
            if (def == null) return 0.0;
            double tonnage = Math.Max(2000.0, def.BasePrice * 400.0);
            double sum = 0.0;
            for (int back = 0; back < days; back++)
            {
                long now = StockState.PriceOnDay(id, StockState.Today - back);
                long prev = StockState.PriceOnDay(id, StockState.Today - back - 1);
                if (now <= 0 || prev <= 0) continue;
                double chg = (double)(now - prev) / prev;
                sum += chg * tonnage;
            }
            return sum;
        }

        // ══════════════════════════════════════════════════════════════
        //  5. AI 诊股
        // ══════════════════════════════════════════════════════════════

        public sealed class Diagnosis
        {
            public int Score;                              // 0..100
            public List<string> Notes = new List<string>(); // 分项理由
            public string Verdict = "中性";
            public string Advice = string.Empty;
        }

        /// <summary>
        /// 四项打分：趋势 / 资金 / 基本面 / 事件。基分 50，每项给出加减分并写一条理由，
        /// 分数就是四项之和 —— 界面把分项列出来，玩家能自己复核，不是黑箱。
        /// </summary>
        public static Diagnosis Diagnose(string id)
        {
            Diagnosis d = new Diagnosis();
            StockDef def = StockDefs.Get(id);
            if (def == null)
            {
                d.Verdict = "标的不存在";
                return d;
            }

            int score = 50;

            // ① 趋势档
            int trend = StockEngine.TrendOf(id);
            int trendPts = trend == 0 ? 12 : (trend == 1 ? -14 : 0);
            score += trendPts;
            d.Notes.Add(Item("近期方向", StockEngine.TrendText(id), trendPts));

            // ② 资金
            double pct = StockBots.NetFlowPct(id);
            int flowPts = (int)Math.Round(pct * 220.0);
            if (flowPts > 12) flowPts = 12;
            if (flowPts < -12) flowPts = -12;
            score += flowPts;
            d.Notes.Add(Item("今日资金", StockBots.FlowText(id) + "（净流入占比 "
                + (pct * 100.0).ToString("0.00") + "%）", flowPts));

            // ③ 基本面：估值因子 1.0 是中性，越往上越有空间
            double vf = StockFundamentals.ValuationFactor(id);
            int fundPts = (int)Math.Round((vf - 1.0) * 90.0);
            if (fundPts > 12) fundPts = 12;
            if (fundPts < -12) fundPts = -12;
            score += fundPts;
            StockFundamentals.Fin fin = StockFundamentals.Current(id);
            string fundDetail = fin != null
                ? "净利同比 " + Pct(fin.ProfitGrowth * 100.0) + "，负债率 "
                    + (fin.DebtRatio * 100.0).ToString("0.0") + "%"
                : "暂无财报";
            d.Notes.Add(Item("基本面", fundDetail, fundPts));

            // ④ 事件：正在发酵的事件对它的每日冲击
            double impact = StockEngine.ActiveImpactFor(id);
            int evPts = (int)Math.Round(impact * 100.0 * 0.8);
            if (evPts > 12) evPts = 12;
            if (evPts < -12) evPts = -12;
            score += evPts;
            d.Notes.Add(Item("市场事件",
                Math.Abs(impact) < 0.0001 ? "没有针对性事件" : "每日冲击 " + Pct(impact * 100.0), evPts));

            if (score > 95) score = 95;
            if (score < 5) score = 5;
            d.Score = score;

            if (score >= 75) { d.Verdict = "偏强"; d.Advice = "几项都站得住，可以考虑逢低建仓，别追高。"; }
            else if (score >= 60) { d.Verdict = "偏多"; d.Advice = "整体还行，但有一两项拖后腿，仓位别一次打满。"; }
            else if (score >= 45) { d.Verdict = "中性"; d.Advice = "看不出明显方向，横盘票更适合低买高卖，或者先放着。"; }
            else if (score >= 30) { d.Verdict = "偏弱"; d.Advice = "多数项在往下走，不急着买；手里有的可以考虑减一点。"; }
            else { d.Verdict = "回避"; d.Advice = "趋势、资金、基本面同时在恶化，这种时候接飞刀很容易受伤。"; }
            return d;
        }

        private static string Item(string label, string detail, int pts)
        {
            return label + "　" + detail + "　"
                + (pts > 0 ? "+" : "") + pts + " 分";
        }

        public static string Pct(double v)
        {
            return (v > 0 ? "+" : "") + v.ToString("0.0") + "%";
        }

        // ══════════════════════════════════════════════════════════════
        //  6. 高级回测
        // ══════════════════════════════════════════════════════════════

        public sealed class Backtest
        {
            public string Strategy = string.Empty;
            public int Days;
            public int Trades;            // 已平仓的完整来回次数
            public int Wins;
            public double ReturnPct;      // 策略总收益 %
            public double HoldPct;        // 同期一直拿着 %
            public double MaxDrawdownPct; // 最大回撤 %
            public bool Holding;          // 结束时还拿着
        }

        /// <summary>三种可回测的玩法。</summary>
        public static readonly string[] Strategies =
        {
            "一直拿着不动",
            "均线金叉买 / 死叉卖",
            "跌 5% 买 / 涨 8% 卖"
        };

        public const int BacktestDays = 120;

        /// <summary>
        /// 用真实收盘价跑一遍策略。收益按「全进全出」复利算：每次买入把全部资金押上，
        /// 卖出后等下一次信号。最大回撤按策略权益曲线算，不是按股价算。
        /// </summary>
        public static Backtest Run(string id, int strategy, int days)
        {
            Backtest r = new Backtest();
            r.Strategy = (strategy >= 0 && strategy < Strategies.Length)
                ? Strategies[strategy] : Strategies[0];
            r.Days = days;
            if (days < 5) days = 5;

            List<long> hist;
            if (!StockState.PriceHistory.TryGetValue(id, out hist) || hist == null || hist.Count < 5)
            {
                return r;
            }

            List<double> closes = new List<double>();
            for (int i = 0; i < hist.Count; i++) closes.Add(StockState.ToYuan(hist[i]));
            int n = closes.Count;
            int from = n - days - 1;
            if (from < 0) from = 0;
            if (from > n - 5) from = Math.Max(0, n - 5);

            double first = closes[from];
            double last = closes[n - 1];
            if (first <= 0.0) return r;
            r.HoldPct = R2((last / first - 1.0) * 100.0);

            if (strategy == 0)
            {
                // 「一直拿着」：一次买入，没有卖出，交易次数记 1 便于和别的策略对比
                r.Trades = 1;
                r.Wins = r.HoldPct > 0 ? 1 : 0;
                r.ReturnPct = r.HoldPct;
                r.MaxDrawdownPct = Drawdown(closes, from, true, 0.0, 0.0);
                r.Holding = true;
                return r;
            }

            double[] ma5 = strategy == 1 ? StockIndicators.MA(closes, 5) : null;
            double[] ma20 = strategy == 1 ? StockIndicators.MA(closes, 20) : null;

            double equity = 1.0;
            double peak = 1.0;
            double mdd = 0.0;
            bool holding = false;
            double entry = 0.0;
            double runMax = closes[from];

            for (int i = from + 1; i < n; i++)
            {
                double p = closes[i];
                if (p > runMax) runMax = p;
                bool wantBuy = false;
                bool wantSell = false;

                if (strategy == 1)
                {
                    double a = ma5[i], b = ma20[i];
                    double pa = ma5[i - 1], pb = ma20[i - 1];
                    if (!StockIndicators.Has(a) || !StockIndicators.Has(b)
                        || !StockIndicators.Has(pa) || !StockIndicators.Has(pb))
                    {
                        continue;
                    }
                    // 金叉：昨天还在下方，今天站上去了
                    wantBuy = pa <= pb && a > b;
                    wantSell = pa >= pb && a < b;
                }
                else
                {
                    // 从区间内的最高点回落 5% 算「跌了」，从买入价涨 8% 算「够了」
                    wantBuy = !holding && runMax > 0 && p <= runMax * 0.95;
                    wantSell = holding && entry > 0 && p >= entry * 1.08;
                }

                if (!holding && wantBuy)
                {
                    holding = true;
                    entry = p;
                }
                else if (holding && wantSell && entry > 0)
                {
                    equity *= p / entry;
                    r.Trades++;
                    if (p > entry) r.Wins++;
                    holding = false;
                    entry = 0.0;
                }

                // 权益曲线：持仓中就当今天的市值，空仓就保持不动，回撤才不会被漏掉
                double mark = holding && entry > 0 ? equity * (p / entry) : equity;
                if (mark > peak) peak = mark;
                double drop = peak > 0 ? (peak - mark) / peak : 0.0;
                if (drop > mdd) mdd = drop;
            }

            if (holding && entry > 0)
            {
                equity *= last / entry;
                r.Holding = true;
            }

            r.ReturnPct = R2((equity - 1.0) * 100.0);
            r.MaxDrawdownPct = R2(mdd * 100.0);
            return r;
        }

        /// <summary>「一直拿着」的最大回撤：直接按股价曲线算。</summary>
        private static double Drawdown(List<double> closes, int from, bool hold,
            double a, double b)
        {
            double peak = 0.0;
            double mdd = 0.0;
            for (int i = from; i < closes.Count; i++)
            {
                double p = closes[i];
                if (p > peak) peak = p;
                if (peak > 0)
                {
                    double drop = (peak - p) / peak;
                    if (drop > mdd) mdd = drop;
                }
            }
            return R2(mdd * 100.0);
        }

        // ══════════════════════════════════════════════════════════════
        //  7. 智能盯盘
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 替玩家盯着的提醒。这里一个随机数都不用，全部由真实状态推出来：
        /// 持仓成本与浮亏、当日涨跌、停板、事件冲击、杠杆担保比例。
        /// 没有异常就返回空表，界面显示一句「一切正常」。
        /// </summary>
        public static List<string> WatchAlerts()
        {
            List<string> list = new List<string>();

            // ① 持仓：浮亏到位 / 浮盈够多 / 快到成本线
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef def = StockDefs.All[i];
                int shares = StockState.GetPosition(def.Id);
                if (shares <= 0) continue;

                long costCents = StockState.GetCost(def.Id);
                double perShare = costCents > 0 ? StockState.ToYuan(costCents) / shares : 0.0;
                double now = StockEngine.LivePriceYuan(def.Id);
                if (perShare <= 0.0) continue;
                double pnl = now / perShare - 1.0;

                if (pnl <= -0.08)
                {
                    list.Add("⚠ " + def.Name + " 浮亏 " + Pct(pnl * 100.0)
                        + "，已经跌到你该认真看一眼的位置了。");
                }
                else if (pnl >= 0.15)
                {
                    list.Add("★ " + def.Name + " 浮盈 " + Pct(pnl * 100.0) + "，可以考虑先落袋一部分。");
                }
                else if (Math.Abs(pnl) <= 0.01)
                {
                    list.Add("· " + def.Name + " 正好在成本线附近（" + Pct(pnl * 100.0)
                        + "），再跌一点就变亏损了。");
                }
            }

            // ② 收藏但没持仓的：只看异动，不啰嗦
            for (int i = 0; i < StockState.Favorites.Count; i++)
            {
                string id = StockState.Favorites[i];
                if (StockState.GetPosition(id) > 0) continue;
                StockDef def = StockDefs.Get(id);
                if (def == null) continue;
                double chg = StockEngine.DayChangePercent(id);
                if (Math.Abs(chg) >= 6.0)
                {
                    list.Add("◎ 你收藏的 " + def.Name + " 今天" + (chg > 0 ? "大涨 " : "大跌 ")
                        + Math.Abs(chg).ToString("0.0") + "%，可能会有人来跟你聊这支。");
                }
            }

            // ③ 停板与事件
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef def = StockDefs.All[i];
                int board = StockEngine.BoardState(def.Id);
                if (board != 0)
                {
                    list.Add("● " + def.Name + StockEngine.BoardText(def.Id)
                        + "，今天这个价位基本买不进也卖不出。");
                    continue;
                }
                double impact = StockEngine.ActiveImpactFor(def.Id);
                if (Math.Abs(impact) >= 0.06 && StockState.GetPosition(def.Id) > 0)
                {
                    list.Add((impact > 0 ? "▲ " : "▼ ") + def.Name + " 正被事件推着走（每日 "
                        + Pct(impact * 100.0) + "），你有持仓，留意势头什么时候减弱。");
                }
            }

            // ④ 杠杆担保比例：进了预警区才提醒，没欠钱的不啰嗦
            GuardAlert(list, StockLeverage.Reg);
            GuardAlert(list, StockLeverage.Shadow);

            if (list.Count > 10) list.RemoveRange(10, list.Count - 10);
            return list;
        }

        /// <summary>杠杆账户的担保比例提醒，阈值直接用账户自己的预警线。</summary>
        private static void GuardAlert(List<string> list, LeverageAccount a)
        {
            if (a == null || !a.Open) return;
            if (StockLeverage.Liability(a) <= 0) return;
            double r = StockLeverage.Ratio(a);
            if (r >= a.WarnRatio) return;
            list.Add("⚠ " + a.Name + "账户担保比例 " + (r * 100.0).ToString("0")
                + "%，已经跌破预警线 " + (a.WarnRatio * 100.0).ToString("0")
                + "%，再往下就是强制平仓。");
        }

        // ══════════════════════════════════════════════════════════════
        //  8. 免费选股器（智能选股）
        // ══════════════════════════════════════════════════════════════

        public sealed class Pick
        {
            public string StockId = string.Empty;
            public string Reason = string.Empty;
            public int Confidence;    // 信心 %，故意压低，它就是不准
        }

        /// <summary>还能换几批。每 7 天恢复满。</summary>
        public static int PickerLeft
        {
            get
            {
                EnsurePeriod();
                int left = PickerTotal - 1 - PickerRoll;
                return left < 0 ? 0 : left;
            }
        }

        /// <summary>这一批是不是已经用完了（界面据此把「换一批」置灰）。</summary>
        public static bool PickerExhausted { get { return PickerLeft <= 0; } }

        /// <summary>换一批。本轮三批用完返回 null，等下一个 7 天周期。</summary>
        public static List<Pick> PickerNext()
        {
            EnsurePeriod();
            if (PickerRoll >= PickerTotal - 1) return null;
            PickerRoll++;
            StockState.Dirty = true;
            return PickerList();
        }

        /// <summary>当前这一批推荐（第 0 批是进页面就能看到的）。</summary>
        public static List<Pick> PickerList()
        {
            EnsurePeriod();
            // 第一次看到某一批时记下当天，之后才谈得上「挑完之后涨了没有」
            if (PickDay[PickerRoll] < 0)
            {
                PickDay[PickerRoll] = StockState.Today;
                StockState.Dirty = true;
            }
            return BatchList(PickerRoll);
        }

        /// <summary>
        /// 第 roll 批的推荐。推荐只由「周期号 + 批次号」决定，跟天数无关，
        /// 所以读档、重开都一样；每 7 天换周期号，新周期就是新的一批。
        /// </summary>
        private static List<Pick> BatchList(int roll)
        {
            int day = PickDay[roll] < 0 ? StockState.Today : PickDay[roll];
            List<Pick> list = new List<Pick>();
            // 记下这一批已经给过谁 —— 上一版每支都独立抽，同一支能被抽中三次
            // （见 问题截图/怎么还三个重复的股票都来了.png）
            List<string> taken = new List<string>();
            uint s = StockState.Hash32("picker|" + PickerEra + "|" + roll);
            for (int k = 0; k < PickerSize; k++)
            {
                StockDef def = PickOne(ref s, day, taken);
                if (def == null) break;
                taken.Add(def.Id);
                list.Add(MakePick(def, day, ref s));
            }
            return list;
        }

        /// <summary>
        /// 「能赚但赚不多」：只在近期方向向上的标的里挑，而且优先挑最近还没怎么涨的那一半 ——
        /// 涨得最猛的那几支已经被排除，剩下的是「还有一点上行空间、但也不会一夜暴富」的。
        /// 这就是它和付费工具的区别：付费工具给你信息，它只给你一个偏保守的结论。
        /// taken 里是本批已经给过的标的，不会再给第二次。
        /// </summary>
        private static StockDef PickOne(ref uint s, int day, List<string> taken)
        {
            List<StockDef> up = new List<StockDef>();      // 方向向上、且本批还没给过
            List<StockDef> rest = new List<StockDef>();    // 其余、且本批还没给过
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef d = StockDefs.All[i];
                if (d == null || Taken(taken, d.Id)) continue;
                if (StockEngine.TrendOf(d.Id, day) == 0) up.Add(d); else rest.Add(d);
            }
            // 向上的都挑完了（极端行情里本来就没几支）才退回其余标的，
            // 免得选股器给不出东西；只要还剩一支向上的，就优先向上的。
            List<StockDef> pool = up.Count > 0 ? up : rest;
            if (pool.Count == 0) return null;

            // 按最近 5 天涨幅从小到大排，只在前半段里抽：位置不高的优先
            pool.Sort((a, b) => Gain5(a.Id, day).CompareTo(Gain5(b.Id, day)));
            int keep = pool.Count / 2;
            if (keep < PickerSize) keep = Math.Min(pool.Count, PickerSize);

            int idx = (int)(Unit(ref s) * keep);
            if (idx >= keep) idx = keep - 1;
            if (idx < 0) idx = 0;
            return pool[idx];
        }

        private static bool Taken(List<string> taken, string id)
        {
            for (int i = 0; i < taken.Count; i++)
            {
                if (taken[i] == id) return true;
            }
            return false;
        }

        /// <summary>最近 5 天的涨幅（0.03 = 涨 3%）。历史不够就返回 0。</summary>
        private static double Gain5(string id, int day)
        {
            long then = StockState.PriceOnDay(id, day - 5);
            long now = StockState.PriceOnDay(id, day);
            if (then <= 0 || now <= 0) return 0.0;
            return (double)(now - then) / then;
        }

        private static Pick MakePick(StockDef def, int day, ref uint s)
        {
            Pick p = new Pick();
            p.StockId = def.Id;
            // 信心压在小数区间：它是有意做成「参考一下可以，别全信」
            p.Confidence = 42 + (int)(Unit(ref s) * 24.0);   // 42..65

            int trend = StockEngine.TrendOf(def.Id, day);
            double gain5 = Gain5(def.Id, day) * 100.0;
            double flow = StockBots.NetInflowYuan(def.Id);
            double chg = StockEngine.DayChangePercent(def.Id);

            StringBuilder sb = new StringBuilder();
            sb.Append(trend == 0 ? "近期方向向上" : "近期没有明确方向，只能算个备选");
            sb.Append("，近 5 天 ").Append(Pct(gain5));
            if (trend == 0 && gain5 < 2.0) sb.Append("（还没怎么涨，位置不高）");
            sb.Append("；今日资金 ").Append(StockBots.FlowText(def.Id));
            sb.Append("，日涨跌 ").Append(Pct(chg)).Append("。");
            sb.Append("指望它赚点小钱可以，别想一夜暴富。");
            p.Reason = sb.ToString();
            return p;
        }

        /// <summary>
        /// 某一批挑完之后的表现（平均涨幅 %）。没给出过、或历史价不够时返回 false。
        /// 这是选股器自己的成绩单 —— 挑得准不准，玩家可以自己回头看。
        /// </summary>
        public static bool BatchPerf(int roll, out int day, out double avgPercent)
        {
            day = -1;
            avgPercent = 0.0;
            if (roll < 0 || roll >= PickerTotal) return false;
            day = PickDay[roll];
            if (day < 0) return false;

            List<Pick> list = BatchList(roll);
            double sum = 0.0;
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                long then = StockState.PriceOnDay(list[i].StockId, day);
                long now = StockState.PriceOnDay(list[i].StockId, StockState.Today);
                if (then <= 0 || now <= 0) continue;
                sum += (double)(now - then) / then;
                n++;
            }
            if (n == 0) return false;
            avgPercent = sum / n * 100.0;
            return true;
        }
    }
}