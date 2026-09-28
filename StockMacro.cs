using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StockMarket
{
    /// <summary>
    /// 宏观物价层：站里不只你一家店，别的铺子每天也在进货出货。
    ///
    /// 六个需求口各有一条自己的供需线（不做全局大指数，日用涨不等于军械涨）：
    ///   · 每天按天哈希派生出 NPC 店铺的供货量与进货量 —— 确定性的，读档重放一模一样，
    ///     所以这两样一个字节都不用落存档，只有「跑到哪了」的结果状态要存；
    ///   · 需求 - 供给 = 当天的净缺口。缺口天天叠进一个会自己消退的「供需压力」里；
    ///   · 压力换算成物价指数（1.00 为基准），指数每天往目标挪一小步，不会一天跳到位；
    ///   · 指数只要动了，当天就按 ±3% 的封顶并进这个板块的收盘价。
    ///
    /// 四个状态：供不应求 / 供过于求（看压力）、通货膨胀 / 通缩（看指数）。
    /// 状态一翻转就挂一条定向事件（和 StockEconomy 的派生事件同一套机制，不进随机池）；
    /// 通胀或通缩还排着一条「第二环」，2~3 天后跟上来：限价令 / 价格补贴 / 黑市套利，
    /// 通缩那边是抛售潮 / 减产保价。
    ///
    /// 阈值对玩家不可见 —— 面板上只看得见指数和状态，具体差多少只有买了「物价雷达」才看得到。
    ///
    /// 数值全部集中在下面常量区，改平衡不用翻别处。
    /// </summary>
    public static class StockMacro
    {
        // ══════════════ 数值（自行平衡）══════════════
        /// <summary>NPC 店铺一个口子一天平均出多少货（元）。</summary>
        private const double NpcSupplyBase = 152.0;
        private const double NpcSupplySwing = 74.0;
        /// <summary>NPC 店铺一个口子一天平均吃进多少货（元）。</summary>
        private const double NpcDemandBase = 146.0;
        private const double NpcDemandSwing = 70.0;

        /// <summary>供需压力每天自然消退掉的比例：市场会自己找平，不会一直紧张下去。</summary>
        private const double PressDecay = 0.72;
        /// <summary>压力换算成物价指数偏移用的标尺（元）。调大 = 物价更稳。</summary>
        private const double PressScale = 1600.0;
        /// <summary>物价指数最多偏离基准这么多（±22%）。</summary>
        private const double IndexCap = 0.22;
        /// <summary>物价指数一天最多走这么多（平滑，不会一天跳到顶）。</summary>
        private const double IndexStep = 0.010;
        /// <summary>物价并入收盘价的单日上限：物价层每天最多推动板块 ±3%。</summary>
        private const double CloseCap = 0.03;

        /// <summary>物价乘数夹到商品出价上的范围：涨不过 +12%，跌不过 -8%。</summary>
        public const double PriceMulMin = 0.92;
        public const double PriceMulMax = 1.12;

        /// <summary>物价指数偏离 ±6% 就算这个口子通胀 / 通缩了。</summary>
        public const double InflateLine = 0.06;
        /// <summary>供需压力越过这条线算「供不应求」，反向是「供过于求」。</summary>
        public const double ShortLine = 230.0;
        /// <summary>同一类状态事件两次之间至少隔几天。</summary>
        private const int StateCool = 8;
        /// <summary>通胀 / 通缩的第二环几天后跟上来。</summary>
        private const int ChainMin = 2;
        private const int ChainMax = 3;

        private const double InflateImpact = 0.017;
        private const double DeflateImpact = -0.015;
        private const double ShortImpact = 0.011;
        private const double GlutImpact = -0.013;

        private const double ChainCapImpact = -0.016;    // 限价令：涨价被摁住
        private const double ChainSubImpact = 0.016;     // 价格补贴：销量回来了
        private const double ChainBmImpact = 0.015;      // 黑市套利：货从正规渠道漏到黑市
        private const double ChainDumpImpact = -0.018;   // 抛售潮
        private const double ChainCutImpact = 0.014;     // 减产保价

        // ══════════════ 状态 ══════════════
        /// <summary>物价指数，1.00 = 基准。六个口子各一条。</summary>
        private static readonly double[] _index = new double[StockEconomy.BucketCount];
        /// <summary>供需压力（元）。正 = 供不应求，负 = 供过于求。</summary>
        private static readonly double[] _press = new double[StockEconomy.BucketCount];
        /// <summary>今天指数挪了多少，用来并进收盘价。落存档 —— 否则读档当天这条漂移就丢了。</summary>
        private static readonly double[] _drift = new double[StockEconomy.BucketCount];
        /// <summary>上一次报出去的状态，只在翻转时挂事件。</summary>
        private static readonly int[] _state = new int[StockEconomy.BucketCount];
        private static readonly int[] _cool = new int[StockEconomy.BucketCount];
        /// <summary>排着的第二环：第几天触发；-1 表示没排。</summary>
        private static readonly int[] _chainDay = new int[StockEconomy.BucketCount];
        /// <summary>排着的第二环是哪种。</summary>
        private static readonly int[] _chainKind = new int[StockEconomy.BucketCount];

        // ══════════════ 状态名 ══════════════
        /// <summary>平稳。</summary>
        public const int StCalm = 0;
        /// <summary>通货膨胀：物价指数涨过线。</summary>
        public const int StInflate = 1;
        /// <summary>通缩：物价指数跌过线。</summary>
        public const int StDeflate = 2;
        /// <summary>供不应求：进货抢不过出货。</summary>
        public const int StShort = 3;
        /// <summary>供过于求：货堆着卖不掉。</summary>
        public const int StGlut = 4;

        public static string StateName(int st)
        {
            switch (st)
            {
                case StInflate: return "通货膨胀";
                case StDeflate: return "通缩";
                case StShort: return "供不应求";
                case StGlut: return "供过于求";
                default: return "平稳";
            }
        }

        // ══════════════ 事件表 ══════════════
        // 和 StockEconomy 那批一样：Weight = 0、不进随机抽取池，只由本模块按供需线定向挂上去。
        // StockDefs.GetEvent 会回落到这里查，所以价格冲击 / 事件公告页 / 夜间报告全都照常工作。
        public static readonly StockEventDef[] Events = BuildEvents();
        private static readonly Dictionary<string, StockEventDef> EventIndex = BuildIndex();

        public static StockEventDef FindEvent(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            StockEventDef d;
            return EventIndex.TryGetValue(id, out d) ? d : null;
        }

        private static string StateId(int b, int st)
        {
            switch (st)
            {
                case StInflate: return "MACRO_INF_" + b;
                case StDeflate: return "MACRO_DEF_" + b;
                case StShort: return "MACRO_SHORT_" + b;
                default: return "MACRO_GLUT_" + b;
            }
        }

        /// <summary>第二环的五种：0 限价令 / 1 价格补贴 / 2 黑市套利（通胀后），3 抛售潮 / 4 减产保价（通缩后）。</summary>
        private static string ChainId(int b, int kind) { return "MACRO_CH" + kind + "_" + b; }

        private static string ChainName(int kind, string n)
        {
            switch (kind)
            {
                case 0: return n + "限价令";
                case 1: return n + "价格补贴";
                case 2: return "黑市转手" + n;
                case 3: return n + "抛售潮";
                default: return n + "减产保价";
            }
        }

        private static string ChainSummary(int kind, string n)
        {
            switch (kind)
            {
                case 0: return "物价涨得太扎眼，市场管理方直接对" + n + "下了一道限价令，摊主只能把价格标签重新写一遍。";
                case 1: return "为了压住" + n + "的涨势，市场管理方放出一笔进货补贴，摊主补货的胆子又回来了。";
                case 2: return n + "的价差被有心人盯上，正规渠道的货悄悄转手流进了黑市，两边都有人赚。";
                case 3: return n + "越跌越没人接，摊主怕砸手里，争先恐后往外抛，价格被踩得更低。";
                default: return n + "的出货方宁可少做也不肯再降，几家同时缩量保价，价格总算站住了。";
            }
        }

        private static double ChainImpact(int kind)
        {
            switch (kind)
            {
                case 0: return ChainCapImpact;
                case 1: return ChainSubImpact;
                case 2: return ChainBmImpact;
                case 3: return ChainDumpImpact;
                default: return ChainCutImpact;
            }
        }

        private static string[] BlackMarket = { "BM", "FORGE", "GUN", "CART", "DRUG" };

        private static StockEventDef[] BuildEvents()
        {
            List<StockEventDef> list = new List<StockEventDef>();
            for (int b = 0; b < StockEconomy.BucketCount; b++)
            {
                string n = StockEconomy.BucketLabel(b);
                string[] stocks = StockEconomy.BucketMembers(b);

                list.Add(new StockEventDef
                {
                    Id = StateId(b, StInflate), Name = n + "物价走高", Weight = 0, MinDays = 3, MaxDays = 4,
                    Impact = InflateImpact, Targets = stocks,
                    Summary = "站里" + n + "连着好些天供不上货，价格标签一路往上改，做这行的企业毛利跟着改善。"
                });
                list.Add(new StockEventDef
                {
                    Id = StateId(b, StDeflate), Name = n + "价格走低", Weight = 0, MinDays = 3, MaxDays = 4,
                    Impact = DeflateImpact, Targets = stocks,
                    Summary = "别家店铺的" + n + "一批压一批往外甩，价格越卖越低，做这行的企业被下调预期。"
                });
                list.Add(new StockEventDef
                {
                    Id = StateId(b, StShort), Name = n + "供不应求", Weight = 0, MinDays = 2, MaxDays = 3,
                    Impact = ShortImpact, Targets = stocks,
                    Summary = "站里" + n + "的货架老是空的，批发商排队等货，短期成交价比往常好谈。"
                });
                list.Add(new StockEventDef
                {
                    Id = StateId(b, StGlut), Name = n + "供过于求", Weight = 0, MinDays = 2, MaxDays = 3,
                    Impact = GlutImpact, Targets = stocks,
                    Summary = "这阵子" + n + "进货的人太多，货架堆得满满当当，摊主开始互相压价清库存。"
                });

                for (int k = 0; k < 5; k++)
                {
                    string[] tgt = k == 2 ? BlackMarket : stocks;
                    list.Add(new StockEventDef
                    {
                        Id = ChainId(b, k), Name = ChainName(k, n), Weight = 0, MinDays = 2, MaxDays = 3,
                        Impact = ChainImpact(k), Targets = tgt,
                        Summary = ChainSummary(k, n)
                    });
                }
            }
            return list.ToArray();
        }

        private static Dictionary<string, StockEventDef> BuildIndex()
        {
            Dictionary<string, StockEventDef> map = new Dictionary<string, StockEventDef>();
            for (int i = 0; i < Events.Length; i++)
            {
                if (Events[i] != null && !string.IsNullOrEmpty(Events[i].Id)) map[Events[i].Id] = Events[i];
            }
            return map;
        }

        // ══════════════ NPC 店铺的当日进出货（确定性派生，不落存档）══════════════
        private static uint Seed(int b, string tag, int day)
        {
            return StockState.Hash32("macro|" + b + "|" + tag + "|" + day);
        }

        private static double Roll(int b, string tag, int day)
        {
            uint s = Seed(b, tag, day);
            return StockState.NextUnit(ref s);
        }

        /// <summary>第 day 天这个口子上 NPC 店铺一共出了多少货（元）。</summary>
        public static double NpcSupplyAt(int b, int day)
        {
            return NpcSupplyBase + NpcSupplySwing * (Roll(b, "sup", day) * 2.0 - 1.0);
        }

        /// <summary>第 day 天这个口子上 NPC 店铺一共吃进多少货（元）。</summary>
        public static double NpcDemandAt(int b, int day)
        {
            return NpcDemandBase + NpcDemandSwing * (Roll(b, "dem", day) * 2.0 - 1.0);
        }

        /// <summary>今天的 NPC 供货量（面板上显示用）。</summary>
        public static double NpcSupply(int b) { return NpcSupplyAt(b, StockState.Today); }

        /// <summary>今天的 NPC 进货量（面板上显示用）。</summary>
        public static double NpcDemand(int b) { return NpcDemandAt(b, StockState.Today); }

        // ══════════════ 对外读数 ══════════════
        public static double Index(int b)
        {
            return b >= 0 && b < StockEconomy.BucketCount ? _index[b] : 1.0;
        }

        /// <summary>相对基准的涨跌幅（%）。+8.4 就是物价指数 108.4。</summary>
        public static double RatePct(int b)
        {
            return (Index(b) - 1.0) * 100.0;
        }

        public static double Press(int b)
        {
            return b >= 0 && b < StockEconomy.BucketCount ? _press[b] : 0.0;
        }

        /// <summary>当前状态（StCalm / StInflate / StDeflate / StShort / StGlut）。</summary>
        public static int State(int b)
        {
            if (b < 0 || b >= StockEconomy.BucketCount) return StCalm;
            double rate = _index[b] - 1.0;
            if (rate >= InflateLine) return StInflate;
            if (rate <= -InflateLine) return StDeflate;
            if (_press[b] >= ShortLine) return StShort;
            if (_press[b] <= -ShortLine) return StGlut;
            return StCalm;
        }

        /// <summary>距通胀线还差几个百分点（已过线或 0 表示已经到了/不算）。</summary>
        public static double ToInflatePct(int b)
        {
            double d = (InflateLine - (Index(b) - 1.0)) * 100.0;
            return d < 0.0 ? 0.0 : d;
        }

        /// <summary>距通缩线还差几个百分点。</summary>
        public static double ToDeflatePct(int b)
        {
            double d = (InflateLine + (Index(b) - 1.0)) * 100.0;
            return d < 0.0 ? 0.0 : d;
        }

        /// <summary>距「供不应求」还差多少压力值（0 = 已经到了）。</summary>
        public static double ToShort(int b)
        {
            double d = ShortLine - _press[b];
            return d < 0.0 ? 0.0 : d;
        }

        /// <summary>距「供过于求」还差多少压力值（0 = 已经到了）。</summary>
        public static double ToGlut(int b)
        {
            double d = ShortLine + _press[b];
            return d < 0.0 ? 0.0 : d;
        }

        /// <summary>排着的第二环还有几天到（-1 = 没排）。</summary>
        public static int ChainIn(int b)
        {
            if (b < 0 || b >= StockEconomy.BucketCount || _chainDay[b] <= 0) return -1;
            int d = _chainDay[b] - StockState.Today;
            return d < 0 ? 0 : d;
        }

        /// <summary>排着的第二环叫什么（没排返回空串）。</summary>
        public static string ChainName(int b)
        {
            if (b < 0 || b >= StockEconomy.BucketCount || _chainDay[b] <= 0) return "";
            return ChainName(_chainKind[b], StockEconomy.BucketLabel(b));
        }

        /// <summary>状态的一句话解释，给面板用。</summary>
        public static string StateText(int b)
        {
            int st = State(b);
            double r = RatePct(b);
            switch (st)
            {
                case StInflate:
                    return "别家店铺补不上货，价格连着走高，现在比平时贵 " + r.ToString("0.0") + "%，做这行的毛利在改善。";
                case StDeflate:
                    return "同行一批压一批往外甩，卖价一路走低，现在比平时便宜 " + (-r).ToString("0.0") + "%，做这行的被压得很难受。";
                case StShort:
                    return "货架常常是空的、批发商排队等货，但价格还没真正抬起来 —— 再紧一阵就要开始涨价了。";
                case StGlut:
                    return "进货的人太多，货堆在架子上卖不动，摊主开始互相压价，价格快要往下走了。";
                default:
                    return "供求基本持平，价格稳在基准附近。";
            }
        }

        /// <summary>物价乘数：这个口子的货现在该按几倍价成交（夹在 0.92~1.12）。</summary>
        public static double PriceMul(int b)
        {
            double v = Index(b);
            if (v > PriceMulMax) v = PriceMulMax;
            if (v < PriceMulMin) v = PriceMulMin;
            return v;
        }

        /// <summary>
        /// 物价层今天推这支标的多少元。一支标的挂在几个口子上时取平均 ——
        /// 同时吃两个口子的反向物价，就该互相抵掉。
        /// </summary>
        public static double PriceTermYuan(string stockId, double current)
        {
            if (!StockState.Loaded) return 0.0;
            int[] bs = StockEconomy.BucketsOf(stockId);
            if (bs == null || bs.Length == 0) return 0.0;

            double sum = 0.0;
            for (int i = 0; i < bs.Length; i++) sum += _drift[bs[i]];
            double drift = sum / bs.Length;
            if (drift > CloseCap) drift = CloseCap;
            if (drift < -CloseCap) drift = -CloseCap;
            return current * drift;
        }

        // ══════════════ 日结算 ══════════════
        /// <summary>
        /// 推进一天：先按 NPC 进出货与玩家实绩重算供需线，再判定状态、挂定向事件、推第二环。
        /// 由 StockEconomy.Settle 在当日累计清零之前调用。playerValue 是它折算过的当日货值：
        /// 玩家的绝对金额先按「今天相当于平时多少倍」归一到常量口径，NPC 买卖盘才不会被后期产量顶爆。
        /// </summary>
        public static void Settle(List<StockEngine.StockNews> news, double[] playerValue)
        {
            try
            {
                if (!StockState.Loaded) return;
                int day = StockState.Today + 1;      // 结算这一步跑在 Today++ 之前，算的是新一天

                int topBucket = -1;
                double topDrift = 0.0;

                for (int b = 0; b < StockEconomy.BucketCount; b++)
                {
                    double player = playerValue != null && b < playerValue.Length ? playerValue[b] : 0.0;
                    double supply = NpcSupplyAt(b, day);
                    double demand = NpcDemandAt(b, day) + player;

                    // 供需压力：缺口叠进压力里，同时自然消退一部分
                    _press[b] = _press[b] * PressDecay + (demand - supply);

                    // 指数朝目标挪一小步
                    double target = 1.0 + Clamp(_press[b] / PressScale, -IndexCap, IndexCap);
                    double prev = _index[b];
                    double step = target - prev;
                    if (step > IndexStep) step = IndexStep;
                    else if (step < -IndexStep) step = -IndexStep;
                    _index[b] = prev + step;
                    _drift[b] = step;

                    if (_cool[b] > 0) _cool[b]--;

                    int st = State(b);
                    if (st != _state[b])
                    {
                        if (st == StCalm)
                        {
                            _state[b] = st;
                        }
                        else if (_cool[b] <= 0)
                        {
                            // 事件位满了就先不认这个状态，明天接着试 ——
                            // 认下来的话这条就永远报不出去了
                            if (Push(FindEvent(StateId(b, st)), news))
                            {
                                _cool[b] = StateCool;
                                _state[b] = st;
                                // 通货膨胀 / 通缩还排一条第二环，隔两三天跟上来
                                if (st == StInflate) QueueChain(b, day, 0, 3);
                                else if (st == StDeflate) QueueChain(b, day, 3, 2);
                            }
                        }
                    }

                    // 排着的第二环到点了
                    if (_chainDay[b] > 0 && day >= _chainDay[b])
                    {
                        Push(FindEvent(ChainId(b, _chainKind[b])), news);
                        _chainDay[b] = -1;
                    }

                    double ad = Math.Abs(_drift[b]);
                    if (ad >= 0.008 && ad > Math.Abs(topDrift)) { topDrift = _drift[b]; topBucket = b; }
                }

                if (topBucket >= 0) news.Add(MacroLine(topBucket));
                StockState.Dirty = true;
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[物价] 结算失败：" + ex.Message);
            }
        }

        /// <summary>排一条第二环：kind 起、共 kinds 种里按哈希挑一种，day + ChainMin~ChainMax 天触发。</summary>
        private static void QueueChain(int b, int day, int kind, int kinds)
        {
            uint h = StockState.Hash32("chain|" + b + "|" + day + "|" + kind);
            int pick = kind + (int)(h % (uint)kinds);
            _chainDay[b] = day + ChainMin + (int)((h >> 8) % (uint)(ChainMax - ChainMin + 1));
            _chainKind[b] = pick;
        }

        /// <summary>物价层当天的夜间播报：只报动得最大的那一个口子，且要真的动了不少。</summary>
        private static StockEngine.StockNews MacroLine(int b)
        {
            double r = RatePct(b);
            string dir = _drift[b] > 0 ? "继续走高" : "还在往下掉";
            string tail = "现在整体" + (r >= 0 ? "比基准贵 " + r.ToString("0.0") + "%" : "比平时便宜 " + (-r).ToString("0.0") + "%") + "。";
            return new StockEngine.StockNews
            {
                Text = "【物价】" + StockEconomy.BucketLabel(b) + "今天的报价" + dir
                    + "（今日 " + (_drift[b] * 100.0).ToString("+0.0;-0.0") + "%），" + tail,
                Color = _drift[b] > 0 ? StockEngine.NewsUp : StockEngine.NewsDown
            };
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>把一条定向事件挂进事件表。事件位满了 / 已经挂着同一条时返回 false。</summary>
        private static bool Push(StockEventDef def, List<StockEngine.StockNews> news)
        {
            if (def == null) return false;
            for (int i = 0; i < StockState.Events.Count; i++)
            {
                if (StockState.Events[i] != null && StockState.Events[i].DefId == def.Id) return false;
            }
            if (StockState.Events.Count >= StockDefs.EventMaxActive)
            {
                Core.Debug("[物价] 事件位已满，" + def.Name + " 这次不挂了。");
                return false;
            }

            StockState.Events.Add(new ActiveEvent
            {
                DefId = def.Id,
                DaysLeft = StockEngine.RandRange(def.MinDays, def.MaxDays),
                TargetStockId = null,
                IntradayDay = -1
            });
            StockState.Dirty = true;
            news.Add(new StockEngine.StockNews
            {
                Text = "【" + def.Name + "】" + def.Summary,
                Color = def.Impact > 0 ? StockEngine.NewsUp : StockEngine.NewsDown
            });
            Core.Log.Msg("[物价] 触发事件：" + def.Name + "（物价指数 "
                + Index(FindBucketOfEvent(def)).ToString("0.000") + "）");
            return true;
        }

        /// <summary>事件 Id 里最后一段（或者 MACRO_CH2_3 这种中间那段）就是口子下标。</summary>
        private static int FindBucketOfEvent(StockEventDef def)
        {
            try
            {
                string[] p = def.Id.Split('_');
                int b;
                if (p.Length > 0 && int.TryParse(p[p.Length - 1], out b) && b >= 0 && b < StockEconomy.BucketCount) return b;
            }
            catch { }
            return 0;
        }

        // ══════════════ 存档 ══════════════
        public static void Reset()
        {
            for (int b = 0; b < StockEconomy.BucketCount; b++)
            {
                _index[b] = 1.0;
                _press[b] = 0.0;
                _drift[b] = 0.0;
                _state[b] = StCalm;
                _cool[b] = 0;
                _chainDay[b] = -1;
                _chainKind[b] = 0;
            }
        }

        /// <summary>
        /// 格式：物价指数×6 | 供需压力×6 | 当日漂移×6 | 已报状态×6 | 状态冷却×6 | 第二环触发日×6 | 第二环种类×6
        /// 只有「跑到哪了」的结果状态要存，NPC 进出货是哈希派生的，每天重算，不进存档。
        /// </summary>
        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            Join(sb, _index); sb.Append('|');
            Join(sb, _press); sb.Append('|');
            Join(sb, _drift); sb.Append('|');
            JoinInts(sb, _state); sb.Append('|');
            JoinInts(sb, _cool); sb.Append('|');
            JoinInts(sb, _chainDay); sb.Append('|');
            JoinInts(sb, _chainKind);
            return sb.ToString();
        }

        public static void Parse(string text)
        {
            Reset();
            if (string.IsNullOrEmpty(text)) return;
            string[] f = text.Split('|');
            if (f.Length >= 7)
            {
                Read(f[0], _index);
                Read(f[1], _press);
                Read(f[2], _drift);
                ReadInts(f[3], _state);
                ReadInts(f[4], _cool);
                ReadInts(f[5], _chainDay);
                ReadInts(f[6], _chainKind);
                // 缺段的老档（理论上不会有）按默认值走，异常值夹回合法区间
                for (int b = 0; b < StockEconomy.BucketCount; b++)
                {
                    if (_index[b] < 1.0 - IndexCap) _index[b] = 1.0 - IndexCap;
                    if (_index[b] > 1.0 + IndexCap) _index[b] = 1.0 + IndexCap;
                    if (_state[b] < StCalm || _state[b] > StGlut) _state[b] = StCalm;
                    if (_chainDay[b] < -1) _chainDay[b] = -1;
                    if (_chainKind[b] < 0 || _chainKind[b] > 4) _chainKind[b] = 0;
                }
            }
            StockState.Dirty = true;
        }

        private static void Join(StringBuilder sb, double[] arr)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(arr[i].ToString("0.####", CultureInfo.InvariantCulture));
            }
        }

        private static void JoinInts(StringBuilder sb, int[] arr)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(arr[i]);
            }
        }

        private static void Read(string text, double[] into)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] f = text.Split(',');
            int n = f.Length < into.Length ? f.Length : into.Length;
            for (int i = 0; i < n; i++)
            {
                double v;
                if (double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) into[i] = v;
            }
        }

        private static void ReadInts(string text, int[] into)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] f = text.Split(',');
            int n = f.Length < into.Length ? f.Length : into.Length;
            for (int i = 0; i < n; i++)
            {
                int v;
                if (int.TryParse(f[i], out v)) into[i] = v;
            }
        }
    }
}