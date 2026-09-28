using System;
using System.Collections.Generic;
using System.Text;
using Il2Cpp;

namespace StockMarket
{
    /// <summary>
    /// 行情引擎：纯计算，不依赖 Unity 对象，便于离线跑数值回归。
    /// </summary>
    public static class StockEngine
    {
        // ── 确定性随机源（xorshift32），避免与游戏 RNG 相互干扰 ──────
        private static uint _seed = 2463534242u;

        public static void SeedRandom(uint seed)
        {
            _seed = seed == 0u ? 2463534242u : seed;
        }

        /// <summary>
        /// 换档 / 重开时把随机源拨回初始值，并把上一局没播完的盘中快讯清掉：
        /// 否则新档第一天的随机序列接着上一局的走，快讯弹窗也可能带过来。
        /// </summary>
        public static void Reset()
        {
            _seed = 2463534242u;
            FlashText = string.Empty;
            FlashImpact = 0.0;
        }

        private static uint NextUInt()
        {
            _seed ^= _seed << 13;
            _seed ^= _seed >> 17;
            _seed ^= _seed << 5;
            return _seed;
        }

        private static double NextDouble()
        {
            return (NextUInt() >> 8) / 16777216.0; // [0,1)
        }

        private static int NextRange(int minInclusive, int maxInclusive)
        {
            if (maxInclusive <= minInclusive) return minInclusive;
            return minInclusive + (int)(NextUInt() % (uint)(maxInclusive - minInclusive + 1));
        }

        /// <summary>给其它模块（老K 报价等）取用的伪随机，共用同一个确定性序列，不碰游戏 RNG。</summary>
        public static int RandRange(int minInclusive, int maxInclusive) { return NextRange(minInclusive, maxInclusive); }

        /// <summary>[0,1) 之间的伪随机。</summary>
        public static double RandUnit() { return NextDouble(); }

        /// <summary>Box-Muller 标准正态分布。</summary>
        private static double NextGaussian()
        {
            double u1 = 1.0 - NextDouble();
            double u2 = 1.0 - NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        // ── 公允价：由派系声望决定锚点 ────────────────────────────────
        public static double FairValue(StockDef def)
        {
            double rep = GetFactionReputation(def.FactionId);
            double factor = 1.0 + StockDefs.FairValueRepScale * (rep / 100.0);
            // 基本面乘数：财报变好 → 合理估值中枢慢慢抬上去。
            // 它每隔 21 天才挪一次，是慢变量，所以价格不会因为一份财报当天就跳，
            // 而是往后十几天里被均值回归一点点拉向新的中枢。
            double fundamental = StockFundamentals.ValuationFactor(def.Id);
            return def.BasePrice * factor * fundamental;
        }

        private static double GetFactionReputation(string factionId)
        {
            try
            {
                StoreReputation rep = StoreReputation.GetStoreReputation(factionId);
                if (rep != null) return rep.GetReputationExact();
            }
            catch (Exception ex)
            {
                Core.Debug("读取派系声望失败 " + factionId + "：" + ex.Message);
            }
            return 50.0; // 读不到时按中位处理，不影响主循环
        }

        // ── 日结算 ────────────────────────────────────────────────────

        /// <summary>
        /// 一条夜间新闻：正文 + 显示颜色（十六进制，不带 #）。
        /// 夜里播报的行情新闻要能一眼看出方向，涨红跌绿，光靠文字读不出来。
        /// </summary>
        public sealed class StockNews
        {
            public string Text;
            public string Color;
        }

        // 夜间新闻用色。注意：这些字是画在**游戏自己的夜间报告面板**上的，
        // 那块面板是米色纸底，不是本模组的深蓝面板 —— 所以这里必须用深色。
        // 之前那套 E5533D / 45C08A / 7fd8ff 是照模组面板配的，压到米色纸上一片糊。
        // 口径：拉动股价=红，打压股价=深绿，中性/结束/日常=黑。
        public const string NewsUp = "C0391F";      // 利好：拉动股价（红）
        public const string NewsDown = "14683A";    // 利空：打压股价（深绿）
        public const string NewsGold = "96600A";    // 分红到账（深琥珀，米色纸上也看得清）
        public const string NewsFlat = "1A1A1A";    // 中性播报（黑）
        public const string NewsWarn = "C0391F";    // 风险提示（红，与「利好」同色：都是要立刻看见的）

        /// <summary>
        /// 旧色 → 新色。老版本照模组深蓝面板配的那套浅色，被写进存档后就一直躺在
        /// 游戏自己的米色纸底夜间报告里，光改代码救不回来，得读档时扫一遍洗掉。
        /// </summary>
        private static readonly string[][] OldNewsColors =
        {
            new[] { "7fd8ff", "1A1A1A" },   // 浅蓝（中性播报）
            new[] { "ab05ff", "1A1A1A" },   // 紫（黑市执照）
            new[] { "9EC7DE", "1A1A1A" },   // 浅青（点缀）
            new[] { "9EB3CC", "1A1A1A" },   // 浅灰蓝（弱化）
            new[] { "45C08A", "14683A" },   // 亮绿（下跌）
            new[] { "E5533D", "C0391F" },   // 亮红（上涨）
            new[] { "ff5a5a", "C0391F" },   // 浅红（风险）
            new[] { "D05020", "C0391F" },   // 砖红（危险）
            new[] { "FFC24A", "96600A" }    // 亮金（分红）
        };

        /// <summary>
        /// 洗夜间报告里的旧条目。
        ///
        /// 为什么要洗：夜间报告的内容是**带 &lt;color=#xxxxxx&gt; 标签整条存进存档的**，
        /// 颜色是写死在字符串里的。所以换掉代码里的配色常量，对已经存下去的老条目
        /// 一点用都没有 —— 玩家重开多少次游戏，那几行浅蓝浅绿还是原样贴在米色纸上。
        /// 只动带【星际证券】前缀的自家条目，游戏自己的新闻一个字不碰。
        /// </summary>
        public static void RepairNightLog(PlayerStore store)
        {
            try
            {
                if (store == null) return;
                Il2CppSystem.Collections.Generic.List<string> logs = store.nightLogs;
                if (logs == null) return;

                int fixedCount = 0;
                for (int i = 0; i < logs.Count; i++)
                {
                    string s = logs[i];
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.IndexOf("【星际证券】", StringComparison.Ordinal) < 0) continue;
                    string t = Recolor(s);
                    if (!string.Equals(t, s, StringComparison.Ordinal))
                    {
                        logs[i] = t;
                        fixedCount++;
                    }
                }
                if (fixedCount > 0)
                {
                    Core.Log.Msg("[夜间报告] 已修正 " + fixedCount + " 条旧版配色/报错文案。");
                }
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[夜间报告] 修正旧条目失败：" + ex.Message);
            }
        }

        private static string Recolor(string s)
        {
            string t = s;
            for (int i = 0; i < OldNewsColors.Length; i++)
            {
                t = ReplaceHex(t, OldNewsColors[i][0], OldNewsColors[i][1]);
            }
            if (t.IndexOf("translation error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                t = StockGameEvents.FixLocErrorText(t);
            }
            return t;
        }

        /// <summary>不分大小写地把色值换掉。直接 Replace 会因为 <c>7fd8ff</c> 与 <c>7FD8FF</c> 对不上而漏掉。</summary>
        private static string ReplaceHex(string s, string from, string to)
        {
            int i = 0;
            for (int guard = 0; guard < 64; guard++)
            {
                int j = s.IndexOf(from, i, StringComparison.OrdinalIgnoreCase);
                if (j < 0) return s;
                s = s.Substring(0, j) + to + s.Substring(j + from.Length);
                i = j + to.Length;
            }
            return s;
        }

        /// <summary>推进一天。返回本日全部夜间新闻（可能为空）。</summary>
        public static List<StockNews> DailyTickNews()
        {
            List<StockNews> news = new List<StockNews>();

            // 先把盘中状态钉死在「已收工」，再动收盘价。
            // 不这么做的话：StockIntraday.Poll 有两秒迟滞，结算这一瞬间 _open 可能还是 true，
            // 撮合和杠杆强平就会拿到一段「盘中路途价」，跟收盘价口径对不上。
            StockIntraday.EndDay();
            // 实体经济结算：把今天卖出去的货值并进对应板块的收盘价，再做多日检测。
            // 必须排在下面重算收盘价之前 —— 并价是改「今天这一根」，重算是拿它当起点推明天。
            List<StockNews> econ = StockEconomy.Settle();

            StockState.Today++;
            // 空间站官方事件：先读一遍。新出现 / 已结束的顺便报进夜间新闻，
            // 后面每一支的价格循环里 EventImpactFor 会用到这份快照。
            List<StockNews> station = StockGameEvents.Refresh(true);
            string ended = AdvanceEvents();
            if (ended.Length > 0)
            {
                news.Add(new StockNews { Text = ended.Trim(), Color = NewsFlat });
            }
            for (int i = 0; i < station.Count; i++)
            {
                if (station[i] != null && !string.IsNullOrEmpty(station[i].Text)) news.Add(station[i]);
            }
            // 店铺经营实绩与它派生的事件，紧跟站内事件后面播
            for (int i = 0; i < econ.Count; i++)
            {
                if (econ[i] != null && !string.IsNullOrEmpty(econ[i].Text)) news.Add(econ[i]);
            }
            // 白天盘中发过的消息，夜里补播一次：整天关着面板做生意的玩家也不会错过
            StockNews recall = IntradayRecall();
            if (recall != null) news.Add(recall);
            StockNews rolled = RollNewEvent();
            if (rolled != null) news.Add(rolled);

            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef def = StockDefs.All[i];
                StockState.Prices[def.Id] = NextPrice(def, StockState.GetPrice(def.Id));
                StockState.RecordPrice(def.Id);
            }

            StockState.History.Add(new DaySnapshot
            {
                Day = StockState.Today,
                Pool = StockState.Pool,
                StockValue = StockState.TotalStockValue()
            });
            while (StockState.History.Count > StockDefs.HistoryLimit)
            {
                StockState.History.RemoveAt(0);
            }

            StockState.DayCounter++;
            StockState.Dirty = true;

            if (StockState.DayCounter >= StockDefs.CycleDays)
            {
                StockState.DayCounter = 0;
                long dividend = PayDividend();
                ApplyCycleReputation();
                ResetCycleAnchors();
                if (dividend > 0)
                {
                    news.Add(new StockNews
                    {
                        Text = "【周期结算】分红 +"
                            + StockState.ToYuan(dividend).ToString("N2") + " 元",
                        Color = NewsGold
                    });
                }
            }

            StockQuest.Sync();
            StockFriend.Tick();
            // 好友消息：新消息只在导航/列表上冒红色数字气泡，玩家不看面板就不知道，
            // 所以夜里补一条中性播报提一句（不写内容，进【好友】页才看得到）。
            int unreadBefore = StockChat.UnreadTotal();
            StockChat.Tick();
            int unreadNow = StockChat.UnreadTotal();
            if (unreadNow > unreadBefore)
            {
                news.Add(new StockNews
                {
                    Text = "【好友】有 " + (unreadNow - unreadBefore) + " 条新消息，去【好友】页看看。",
                    Color = NewsFlat
                });
            }
            // 杠杆账户的日结：先计息，再看担保比例有没有跌破强平线。
            // 这段只在保不住或贴近预警线时才出字，所以一律按风险提示标红。
            string lev = StockLeverage.DailySettle();
            if (lev.Length > 0)
            {
                news.Add(new StockNews { Text = lev.Trim(), Color = NewsWarn });
            }
            return news;
        }

        /// <summary>推进一天，把本日新闻拼成一行（调试面板的「快进」用）。</summary>
        public static string DailyTick()
        {
            List<StockNews> news = DailyTickNews();
            string notice = string.Empty;
            for (int i = 0; i < news.Count; i++)
            {
                if (news[i] == null || string.IsNullOrEmpty(news[i].Text)) continue;
                notice = (notice + " " + news[i].Text).Trim();
            }
            return notice;
        }

        /// <summary>
        /// 周期分红：持仓过结算日就按市值派息，空间站股给得多、黑市股象征性给一点。
        /// 这是「钱能自己长大」的那条线——只靠价差的话，玩家赚的永远比不上卖货一天，
        /// 长期持仓也就没有意义。返回派息总额（分）。
        ///
        /// 但有个前提：必须持满一个完整周期（见 StockState.HeldFullCycle）。
        /// 否则结算前一天买入就能白吃一次分红，等于无风险套利。
        /// </summary>
        public static long PayDividend()
        {
            long total = 0;
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef c = StockDefs.All[i];
                int held = StockState.GetPosition(c.Id);
                if (held <= 0) continue;
                if (!StockState.HeldFullCycle(c.Id)) continue;

                long value = StockState.GetPrice(c.Id) * held;
                double rate = c.Category == StockCategory.Station
                    ? StockDefs.StationDividend
                    : StockDefs.BlackDividend;
                total += (long)Math.Round(value * rate);
            }
            if (total > 0)
            {
                StockState.Pool += total;
                StockState.TotalDividend += total;
                StockState.Dirty = true;
            }
            return total;
        }

        // ── 趋势档位（牛 / 熊 / 震荡）────────────────────────────────
        /// <summary>FNV-1a 字符串哈希。用来把「股票 Id + 天数」映射成稳定的伪随机档位。</summary>
        private static uint Hash(string s)
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619u;
            }
            return h;
        }

        /// <summary>
        /// 每支股票的趋势档长度（5~11 天）。由 Id 决定，避免所有股票同时掉头。
        /// 缩短档长是为了「来回倒腾」：档越短，掉头的次数越多，玩家越有得操作。
        /// </summary>
        private static int TrendLengthOf(string id)
        {
            return 5 + (int)(Hash(id + "|len") % 7u);
        }

        /// <summary>
        /// 某支股票在第 day 天的趋势档：0=牛市 1=熊市 2=震荡。
        /// 刻意不落存档：同样的天数必然算出同样的档位，所以读档、快进、回档都不会
        /// 让走势对不上，也省掉一套序列化格式。
        /// </summary>
        public static int TrendOf(string id, int day)
        {
            if (day < 0) day = 0;
            int len = TrendLengthOf(id);
            uint r = Hash(id + "|" + (day / len)) % 100u;
            // 80% 的时间有明确方向（42% 上涨 / 38% 下跌），只剩 20% 横盘。
            // 横盘太多会「看着没意思」，方向明确才有得赚、也才有得亏。
            if (r < 42u) return 0;   // 牛市
            if (r < 80u) return 1;   // 熊市
            return 2;                // 震荡
        }

        public static int TrendOf(string id)
        {
            return TrendOf(id, StockState.Today);
        }

        /// <summary>
        /// 趋势档的中文名，给界面显示用。
        /// 刻意不用「牛市 / 熊市 / 震荡」这类行话——新手看到这三个词完全不知道
        /// 该买还是该卖。直接写清楚方向：上涨趋势 / 下跌趋势 / 横盘。
        /// </summary>
        public static string TrendText(string id)
        {
            switch (TrendOf(id))
            {
                case 0: return "上涨趋势↑";
                case 1: return "下跌趋势↓";
                default: return "横盘—";
            }
        }

        /// <summary>趋势档的一句话解释，鼠标悬停或新手提示里用。</summary>
        public static string TrendHint(string id)
        {
            switch (TrendOf(id))
            {
                case 0: return "上涨趋势：这几天大概率整体往上走，适合持有或逢低买入。";
                case 1: return "下跌趋势：这几天大概率整体往下走，别急着抄底，可先卖出避险。";
                default: return "横盘：价格上下小幅波动，没有明确方向，适合低买高卖做差价。";
            }
        }

        /// <summary>昨日的涨跌幅（相对值）。用作动量：昨天涨了，今天更容易接着涨。</summary>
        private static double LastChange(string id)
        {
            List<long> list;
            if (!StockState.PriceHistory.TryGetValue(id, out list) || list == null || list.Count < 2) return 0.0;
            long prev = list[list.Count - 2];
            long last = list[list.Count - 1];
            if (prev <= 0) return 0.0;
            return (double)(last - prev) / prev;
        }

        /// <summary>
        /// 带内松、带外紧的均值回归。回归太强就没有趋势（价格永远弹回公允价），
        /// 完全没有又会发散到离谱。折中：偏离 50% 以内每天只拉 2.5%，让趋势跑得起来；
        /// 一旦超出 55%/45% 的带，回归力度随偏离量线性上升，把价格拽回来。
        /// </summary>
        private static double MeanReversion(double current, double fair)
        {
            if (fair <= 0) return 0.0;
            double dev = current / fair - 1.0;
            double pull;
            if (dev > StockDefs.PullBandUp)
            {
                pull = StockDefs.PullBase + StockDefs.PullSlope * (dev - StockDefs.PullBandUp);
            }
            else if (dev < -StockDefs.PullBandDown)
            {
                pull = StockDefs.PullBase + StockDefs.PullSlope * (-dev - StockDefs.PullBandDown);
            }
            else
            {
                pull = StockDefs.PullBase;
            }
            if (pull > StockDefs.PullMax) pull = StockDefs.PullMax;
            return (fair - current) * pull;
        }

        private static long NextPrice(StockDef def, long currentCents)
        {
            double current = StockState.ToYuan(currentCents);
            if (current <= 0) current = def.BasePrice;
            double fair = FairValue(def);

            int trend = TrendOf(def.Id, StockState.Today);
            double driftRate;
            double momentum;
            if (trend == 0) { driftRate = StockDefs.TrendBullDrift; momentum = StockDefs.TrendMomentum; }
            else if (trend == 1) { driftRate = -StockDefs.TrendBearDrift; momentum = StockDefs.TrendMomentum; }
            else { driftRate = 0.0; momentum = StockDefs.TrendRangeMomentum; }

            double drift = current * driftRate;                          // 趋势漂移
            double momo = current * momentum * LastChange(def.Id);       // 动量延续
            double noise = NextGaussian() * def.Sigma * current;         // 噪声
            double shock = EventImpactFor(def.Id) * current;             // 事件冲击
            double revert = MeanReversion(current, fair);                // 温和回归
            // 资金流：散户 / 机构 / 操盘手今天对这支的净买卖。
            // 系数刻意压得很小（见 StockBots.PriceImpact），趋势和噪声仍然是主力，
            // 否则玩家盯住「主力净流入」就能反推收盘价，等于白送套利。
            double flow = StockBots.PriceTermYuan(def.Id, current);
            // 操盘手放出来的市场传闻：量级比正式事件小一大截，只够把当天价格推偏一点点。
            // 七成顺着趋势（帮着拉抬），三成反着放（骗接盘），所以传闻不能无脑跟。
            double rumor = StockReview.RumorTermYuan(def.Id, current);
            // 宏观物价层：这个板块的物价指数今天挪了多少，按 ±3% 的封顶并进来。
            // 它不是事件，是「站里这些东西整体贵了/便宜了」，所以走独立的通道。
            double macro = StockMacro.PriceTermYuan(def.Id, current);

            double next = current + drift + momo + noise + shock + revert + flow + rumor + macro;

            // 单日涨跌幅上限 = 该层区的涨跌停（撞到就是封板）
            double cap = StockDefs.LimitOf(def);
            double upper = current * (1.0 + cap);
            double lower = current * (1.0 - cap);
            if (next > upper) next = upper;
            if (next < lower) next = lower;
            if (next < 1.0) next = 1.0; // 价格不归零

            // 周期累计涨跌幅上限（防止单周期内被趋势+事件叠加打爆）
            long anchorCents;
            if (!StockState.CycleStart.TryGetValue(def.Id, out anchorCents) || anchorCents <= 0)
            {
                anchorCents = currentCents;
                StockState.CycleStart[def.Id] = anchorCents;
            }
            double anchor = StockState.ToYuan(anchorCents);
            double cycleUpper = anchor * (1.0 + StockDefs.CycleCapGain);
            double cycleLower = anchor * (1.0 + StockDefs.CycleCapLoss);
            if (next > cycleUpper) next = cycleUpper;
            if (next < cycleLower) next = cycleLower;

            return StockState.ToCents(next);
        }

        private static void ResetCycleAnchors()
        {
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                string id = StockDefs.All[i].Id;
                StockState.CycleStart[id] = StockState.GetPrice(id);
            }
        }

        // ── 事件 ──────────────────────────────────────────────────────
        /// <summary>这条事件打不打某支标的（随机单支 / 指定几支 / 全表）。</summary>
        private static bool TargetsStock(StockEventDef def, ActiveEvent ev, string stockId)
        {
            return TargetsStockPublic(def, ev, stockId);
        }

        /// <summary>同上，给基本面/公告模块判断「这条事件跟这家公司有关」用。</summary>
        public static bool TargetsStockPublic(StockEventDef def, ActiveEvent ev, string stockId)
        {
            if (def == null) return false;
            if (def.Targets == null) return ev != null && ev.TargetStockId == stockId;
            for (int t = 0; t < def.Targets.Length; t++)
            {
                if (def.Targets[t] == stockId) return true;
            }
            return false;
        }

        private static double EventImpactFor(string stockId)
        {
            double sum = 0;
            for (int i = 0; i < StockState.Events.Count; i++)
            {
                ActiveEvent ev = StockState.Events[i];
                StockEventDef def = StockDefs.GetEvent(ev.DefId);
                if (def == null) continue;
                if (TargetsStock(def, ev, stockId)) sum += def.Impact;
            }
            // 空间站官方事件（供水故障、停电、游客涌入、突袭黑市……）另算一路。
            // 它自己内部已经封过顶，这里直接叠加，跟模组自造事件共用下面这道总闸。
            sum += StockGameEvents.ImpactFor(stockId);
            // 同时挂三个事件时冲击会叠加，不封顶的话单日能砸出 -50% 以上
            if (sum > StockDefs.EventImpactCap) sum = StockDefs.EventImpactCap;
            if (sum < -StockDefs.EventImpactCap) sum = -StockDefs.EventImpactCap;
            return sum;
        }

        /// <summary>当前挂着的所有事件对某支标的的合计冲击（正=利好）。给 AI 席位判断消息面用。</summary>
        public static double ActiveImpactFor(string stockId)
        {
            return EventImpactFor(stockId);
        }

        /// <summary>今天那条「盘中发布」的消息对某支标的的冲击（0 = 没它的事）。</summary>
        public static double IntradayShockFor(string stockId)
        {
            for (int i = 0; i < StockState.Events.Count; i++)
            {
                ActiveEvent ev = StockState.Events[i];
                if (ev.IntradayDay != StockState.Today) continue;
                StockEventDef def = StockDefs.GetEvent(ev.DefId);
                if (def == null) continue;
                if (TargetsStock(def, ev, stockId)) return def.Impact;
            }
            return 0.0;
        }

        /// <summary>
        /// 今天的盘中消息在第几段砸下来（8~41 段，保证开盘后一会儿、又在收工前很久）。
        /// 由「事件 Id + 天数」定死，读档重进还是同一段，不会刷出第二条消息。
        /// 今天没有盘中消息就返回 int.MaxValue（永不触发）。
        /// </summary>
        public static int IntradayTriggerStep()
        {
            ActiveEvent ev;
            StockEventDef def = TodayIntradayDef(out ev);
            if (def == null) return int.MaxValue;
            uint h = StockState.Hash32(def.Id + "|nseg|" + StockState.Today);
            return 8 + (int)(h % 34u);
        }

        /// <summary>今天盘中发布的那条消息（只认挂在这一天的，其他事件不算）。</summary>
        private static StockEventDef TodayIntradayDef(out ActiveEvent found)
        {
            found = null;
            for (int i = 0; i < StockState.Events.Count; i++)
            {
                ActiveEvent ev = StockState.Events[i];
                if (ev.IntradayDay != StockState.Today) continue;
                StockEventDef def = StockDefs.GetEvent(ev.DefId);
                if (def == null) continue;
                found = ev;
                return def;
            }
            return null;
        }

        /// <summary>推进已有事件，返回「结束/进行中」的提示文案。</summary>
        private static string AdvanceEvents()
        {
            string notice = string.Empty;
            for (int i = StockState.Events.Count - 1; i >= 0; i--)
            {
                StockState.Events[i].DaysLeft--;
                if (StockState.Events[i].DaysLeft <= 0)
                {
                    StockEventDef def = StockDefs.GetEvent(StockState.Events[i].DefId);
                    if (def != null) notice += "【" + def.Name + "】已结束  ";
                    StockState.Events.RemoveAt(i);
                }
            }
            return notice;
        }

        /// <summary>
        /// 抽一条事件出来挂进事件表，并把「下次事件日」推到 2~7 天之后。
        /// intraday=true 表示这条消息走盘中（开盘时登记、当天就能买卖），
        /// false 表示走盘后（收工时当夜间新闻播，第二天才生效）。
        /// </summary>
        private static ActiveEvent SpawnEvent(bool intraday)
        {
            int total = 0;
            for (int i = 0; i < StockDefs.Events.Length; i++) total += StockDefs.Events[i].Weight;
            int roll = NextRange(1, total);
            StockEventDef picked = null;
            for (int i = 0; i < StockDefs.Events.Length; i++)
            {
                roll -= StockDefs.Events[i].Weight;
                if (roll <= 0) { picked = StockDefs.Events[i]; break; }
            }
            if (picked == null) return null;

            ActiveEvent ev = new ActiveEvent
            {
                DefId = picked.Id,
                DaysLeft = NextRange(picked.MinDays, picked.MaxDays),
                TargetStockId = null,
                IntradayDay = intraday ? StockState.Today : -1
            };
            if (picked.Targets == null)
            {
                StockDef target = StockDefs.All[NextRange(0, StockDefs.All.Length - 1)];
                ev.TargetStockId = target.Id;
            }

            StockState.Events.Add(ev);
            StockState.LastEventDay = StockState.Today;
            // 随机天数：每次都重抽一个 2~7 天的下次触发日，节奏不固定才没法提前埋伏
            StockState.NextEventDay = StockState.Today
                + NextRange(StockDefs.EventMinGapDays, StockDefs.EventMaxGapDays);
            return ev;
        }

        /// <summary>夜间播报新事件。没到下次事件日、或者未结算完就直接返回 null。</summary>
        private static StockNews RollNewEvent()
        {
            if (StockState.Today < StockState.NextEventDay) return null;
            if (StockState.Events.Count >= StockDefs.EventMaxActive) return null;

            ActiveEvent ev = SpawnEvent(false);
            if (ev == null) return null;
            StockEventDef picked = StockDefs.GetEvent(ev.DefId);
            return new StockNews
            {
                Text = Headline(picked, ev) + (picked != null ? picked.Summary : string.Empty),
                Color = picked == null ? NewsFlat
                    : (picked.Impact > 0 ? NewsUp : (picked.Impact < 0 ? NewsDown : NewsFlat))
            };
        }

        /// <summary>
        /// 开盘时决定「今天的消息是盘中发还是盘后发」。
        ///
        /// 盘中消息必须在开盘前就登记进事件表：分时路径是开盘一次性算好的
        /// （见 StockIntraday.BuildPath），冲击得在算路径的时候就带上，
        /// 否则玩家盘中看到消息、盘面却毫无反应。
        /// 掷硬币的结果由「newsslot + 天数」定死，读档重进也不会多刷一条。
        /// </summary>
        public static ActiveEvent PlanIntradayEvent()
        {
            if (StockState.Today < StockState.NextEventDay) return null;
            if (StockState.Events.Count >= StockDefs.EventMaxActive) return null;
            if ((int)(StockState.Hash32("newsslot|" + StockState.Today) % 100u) >= StockDefs.IntradayNewsChance)
            {
                // 剩下四成留到收工当夜间新闻，第二天才生效
                return null;
            }

            ActiveEvent ev = SpawnEvent(true);
            if (ev != null) ApplyIntradayShockToClose();
            return ev;
        }

        /// <summary>
        /// 把今天的盘中消息冲击并进「今日收盘价」，并夹在涨跌停之内。
        ///
        /// 这一步不做的话会白送钱：盘中消息砸下去、玩家低价接货，
        /// 收工时价格却又跳回那条消息之前定好的收盘价，等于无风险套利。
        /// 顺带把历史里今天的收盘价一起改掉，否则收工后界面显示的涨跌
        /// 会跟玩家整天看到的盘面对不上。
        /// </summary>
        private static void ApplyIntradayShockToClose()
        {
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef def = StockDefs.All[i];
                double shock = IntradayShockFor(def.Id);
                if (shock == 0.0) continue;

                double now = StockState.ToYuan(StockState.GetPrice(def.Id)) * (1.0 + shock);
                double lim = StockDefs.LimitOf(def);
                double baseYuan = StockState.ToYuan(PrevCloseCents(def.Id));
                if (now > baseYuan * (1.0 + lim)) now = baseYuan * (1.0 + lim);
                if (now < baseYuan * (1.0 - lim)) now = baseYuan * (1.0 - lim);
                if (now < 1.0) now = 1.0;

                StockState.Prices[def.Id] = StockState.ToCents(now);
                List<long> hist;
                if (StockState.PriceHistory.TryGetValue(def.Id, out hist) && hist != null && hist.Count > 0)
                {
                    hist[hist.Count - 1] = StockState.Prices[def.Id];
                }
                StockState.Dirty = true;
            }
        }

        /// <summary>
        /// 消息标题。严重性是「事件有多猛 + 还剩多少势头」的定性暗示：
        /// 刻意不写「还剩 N 天」——写清楚了玩家就能卡着最后一天进场白吃一波，
        /// 事件的持续性直接变成一道算术题。现在只能从严重程度和措辞去猜。
        /// </summary>
        public static string Headline(StockEventDef def, ActiveEvent ev)
        {
            if (def == null) return string.Empty;
            string target = string.Empty;
            if (ev != null && ev.TargetStockId != null)
            {
                StockDef t = StockDefs.Get(ev.TargetStockId);
                if (t != null) target = "（" + t.Name + "）";
            }
            int left = ev != null ? ev.DaysLeft : def.MaxDays;
            return "【" + def.Name + "】" + target + " " + SeverityText(def, left) + "。";
        }

        /// <summary>严重性 + 势头：越严重、越「未平」，大概率还没完。</summary>
        public static string SeverityText(StockEventDef def, int daysLeft)
        {
            double a = def != null ? Math.Abs(def.Impact) : 0.0;
            string scale = a >= 0.25 ? "市场剧震"
                : (a >= 0.19 ? "冲击剧烈" : (a >= 0.12 ? "行业震荡" : "影响有限"));
            string phase = daysLeft >= 4 ? "余波未平"
                : (daysLeft >= 2 ? "仍在发酵" : "势头渐弱");
            return scale + " · " + phase;
        }

        /// <summary>
        /// 今天盘中那条消息，已经砸下来了才返回（给界面上「今日快讯」用）。
        /// 没到发布时点不算——那会提前剧透。
        /// </summary>
        public static string TodayIntradayHeadline()
        {
            ActiveEvent ev;
            StockEventDef def = TodayIntradayDef(out ev);
            if (def == null) return string.Empty;
            if (StockIntraday.IsLive && StockIntraday.Step < IntradayTriggerStep()) return string.Empty;
            return Headline(def, ev);
        }

        /// <summary>今日盘中消息的方向（利空为负），给提示上色用。</summary>
        public static double TodayIntradayImpact()
        {
            ActiveEvent ev;
            StockEventDef def = TodayIntradayDef(out ev);
            return def != null ? def.Impact : 0.0;
        }

        // 盘中快讯的弹窗提示：只播一次，界面取走后清空
        public static string FlashText = string.Empty;
        public static double FlashImpact;

        /// <summary>取走待播的盘中快讯（取完就清空，不会重复弹）。</summary>
        public static string TakeFlash(out double impact)
        {
            string t = FlashText;
            impact = FlashImpact;
            FlashText = string.Empty;
            return t;
        }

        /// <summary>
        /// 夜里补播「今天白天盘中发过的那条消息」。
        /// 调用点已经过了 Today++，所以这里的「今天」要减一天。
        /// </summary>
        private static StockNews IntradayRecall()
        {
            for (int i = 0; i < StockState.Events.Count; i++)
            {
                ActiveEvent ev = StockState.Events[i];
                if (ev.IntradayDay != StockState.Today - 1) continue;
                StockEventDef def = StockDefs.GetEvent(ev.DefId);
                if (def == null) continue;
                return new StockNews
                {
                    Text = "【盘中回顾】" + Headline(def, ev),
                    Color = def.Impact > 0 ? NewsUp : (def.Impact < 0 ? NewsDown : NewsFlat)
                };
            }
            return null;
        }

        /// <summary>调试用：无视间隔与并发上限，立刻抽一个事件出来。</summary>
        public static string ForceEvent()
        {
            ActiveEvent ev = SpawnEvent(false);
            if (ev == null) return string.Empty;
            return Headline(StockDefs.GetEvent(ev.DefId), ev);
        }

        public static string ActiveEventSummary()
        {
            if (StockState.Events.Count == 0) return "市场平稳，暂无异常事件。";
            string text = string.Empty;
            for (int i = 0; i < StockState.Events.Count; i++)
            {
                StockEventDef def = StockDefs.GetEvent(StockState.Events[i].DefId);
                if (def == null) continue;
                text += def.Name;
                if (StockState.Events[i].TargetStockId != null)
                {
                    StockDef t = StockDefs.Get(StockState.Events[i].TargetStockId);
                    if (t != null) text += "·" + t.Name;
                }
                text += "（" + SeverityText(def, StockState.Events[i].DaysLeft) + "）  ";
            }
            return text.Trim();
        }

        // ── 交易 ──────────────────────────────────────────────────────
        public static bool Buy(StockDef def, int shares, out string error)
        {
            return BuyAt(def, shares, LivePrice(def.Id), def.FeeRate, out error);
        }

        /// <summary>按指定单价、指定费率买入。老K 的场外推销走这条路（费率为 0）。</summary>
        public static bool BuyAt(StockDef def, int shares, long unitPrice, double feeRate, out string error)
        {
            error = null;
            if (shares <= 0) { error = "数量必须大于 0"; return false; }
            // 黑市股必须先开户。放在这里而不是界面层，是因为老K 的报价也走这条路径，
            // 只挡界面的话「接受老K 推销」还能绕过开户。
            if (def.NeedLicense && !StockState.License)
            {
                error = "黑市未开户，先去【黑市开户】办证明（" + StockUI.LicenseCost + " 元）";
                return false;
            }

            long cost = unitPrice * shares;
            long fee = (long)Math.Ceiling(cost * feeRate);
            long total = cost + fee;

            if (total < StockState.ToCents(StockDefs.MinTradeValue))
            {
                error = "单笔交易额不得低于 " + StockDefs.MinTradeValue + " 元";
                return false;
            }
            if (StockState.Pool < total)
            {
                error = "账户资金不足（需 " + StockState.ToYuan(total).ToString("N2") + " 元）";
                return false;
            }

            StockState.Pool -= total;
            int before = StockState.GetPosition(def.Id);
            StockState.Positions[def.Id] = before + shares;
            StockState.CostBasis[def.Id] = StockState.GetCost(def.Id) + total; // 手续费计入成本
            StockState.MarkBought(def.Id, before);
            // 成交流水记在这里：BuyAt 是所有买入的唯一出口（面板、盘中挂单、老K 推销）
            StockJournal.Add(def.Id, 0, shares, unitPrice, fee, 0);
            StockState.Dirty = true;
            return true;
        }

        public static bool Sell(StockDef def, int shares, out string error)
        {
            return SellAt(def, shares, LivePrice(def.Id), def.FeeRate, out error);
        }

        /// <summary>按指定单价、指定费率卖出。老K 的场外收购走这条路（费率为 0）。</summary>
        public static bool SellAt(StockDef def, int shares, long unitPrice, double feeRate, out string error)
        {
            error = null;
            if (shares <= 0) { error = "数量必须大于 0"; return false; }

            int held = StockState.GetPosition(def.Id);
            if (held < shares) { error = "持仓不足，当前 " + held + " 股"; return false; }

            long gross = unitPrice * shares;
            long fee = (long)Math.Ceiling(gross * feeRate);
            long net = gross - fee;

            long cost = StockState.GetCost(def.Id);
            long costPortion = shares >= held ? cost : cost * shares / held;
            long gain = net - costPortion;
            StockState.RealizedPnl += gain;                // 落袋的那部分才计入已实现盈亏
            StockState.SellCount++;
            if (gain > StockState.MaxSingleProfit) StockState.MaxSingleProfit = gain;

            StockState.Pool += net;
            StockState.Positions[def.Id] = held - shares;
            if (StockState.Positions[def.Id] == 0)
            {
                StockState.CostBasis[def.Id] = 0;
                StockState.MarkCleared(def.Id);
            }
            else
            {
                StockState.CostBasis[def.Id] = cost - costPortion;
            }
            // 卖出的盈亏当场算好存进流水：持仓成本在下一笔买入时就被摊掉了，
            // 事后再想反推「那笔到底赚多少」是算不出来的
            StockJournal.Add(def.Id, 1, shares, unitPrice, fee, gain);
            StockState.Dirty = true;
            return true;
        }

        public static void SellAll(StockDef def)
        {
            int held = StockState.GetPosition(def.Id);
            if (held <= 0) return;
            string err;
            Sell(def, held, out err);
        }

        // ── 与店铺资金互转 ────────────────────────────────────────────
        public static bool TransferIn(PlayerStore store, int yuan, out string error)
        {
            error = null;
            if (store == null) { error = "存档未就绪"; return false; }
            if (yuan <= 0) { error = "金额必须大于 0"; return false; }
            if (store.playerCash < yuan) { error = "店铺现金不足"; return false; }

            store.playerCash -= yuan;
            StockState.Pool += StockState.ToCents(yuan);
            StockState.Dirty = true;
            return true;
        }

        public static bool TransferOut(PlayerStore store, int yuan, out string error)
        {
            error = null;
            if (store == null) { error = "存档未就绪"; return false; }
            if (yuan <= 0) { error = "金额必须大于 0"; return false; }

            long cents = StockState.ToCents(yuan);
            if (StockState.Pool < cents) { error = "证券账户余额不足"; return false; }

            StockState.Pool -= cents;
            store.playerCash += yuan;
            StockState.Dirty = true;
            return true;
        }

        // ── 派系声望联动 ──────────────────────────────────────────────
        public static void ApplyCycleReputation()
        {
            long totalValue = StockState.TotalStockValue();
            if (totalValue <= 0) return;

            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef def = StockDefs.All[i];
                long marketValue = StockState.GetPrice(def.Id) * StockState.GetPosition(def.Id);
                if (marketValue <= 0) continue;

                // 同时重仓敌对阵营 -> 声望停滞
                if (IsBlockedByHostile(def, totalValue, marketValue)) continue;

                double yuan = StockState.ToYuan(marketValue);
                int gain = (int)Math.Floor(yuan / StockDefs.RepPerMarketValue);
                if (gain <= 0) continue;
                if (gain > StockDefs.RepCycleCap) gain = StockDefs.RepCycleCap;

                try
                {
                    StoreReputation rep = StoreReputation.GetStoreReputation(def.FactionId);
                    if (rep != null) rep.ModProgressReputation(gain);
                }
                catch (Exception ex)
                {
                    Core.Debug("调整声望失败 " + def.FactionId + "：" + ex.Message);
                }
            }
        }

        private static bool IsBlockedByHostile(StockDef def, long totalValue, long myValue)
        {
            double myShare = (double)myValue / totalValue;
            if (myShare < StockDefs.RepConflictThreshold) return false;

            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef other = StockDefs.All[i];
                if (!StockDefs.AreHostile(def, other)) continue;
                long otherValue = StockState.GetPrice(other.Id) * StockState.GetPosition(other.Id);
                if (otherValue <= 0) continue;
                if ((double)otherValue / totalValue >= StockDefs.RepConflictThreshold) return true;
            }
            return false;
        }

        // ── 实时价 ────────────────────────────────────────────────────
        /// <summary>
        /// 营业中返回分时图的实时盘口价，其余时间返回今日收盘价。
        ///
        /// 为什么要这层：今天的收盘价在开店前就已经定死在 StockState.Prices 里了
        /// （全工程只有 DailyTick 和 ResetToDefaults 两处写它）。行情页原来的「涨跌」列
        /// 直接显示「今日收盘 / 昨收 − 1」，等于开盘就把答案写在黑板上；这时候要是还能
        /// 按盘中价自由买卖，玩家只要开盘买、临收盘卖就稳赚。所以盘中一切「玩家看得见的价」
        /// 和「成交用的价」都走这里，收盘价只在收工那一刻才揭晓。
        ///
        /// 注意：分红、周期锚点、杠杆强平都在非营业时段发生，那些地方继续用
        /// StockState.GetPrice（收盘价口径）才是对的，不要顺手改成这里。
        /// </summary>
        public static long LivePrice(string id)
        {
            if (StockIntraday.IsLive)
            {
                long p = StockIntraday.PriceCents(id);
                if (p > 0) return p;
            }
            return StockState.GetPrice(id);
        }

        public static double LivePriceYuan(string id) { return StockState.ToYuan(LivePrice(id)); }

        /// <summary>昨收（分）。涨跌停的基准，历史不足两天就退回今日价。</summary>
        private static long PrevCloseCents(string id)
        {
            List<long> list;
            if (StockState.PriceHistory.TryGetValue(id, out list) && list != null && list.Count >= 2)
            {
                long v = list[list.Count - 2];
                if (v > 0) return v;
            }
            return StockState.GetPrice(id);
        }

        // ── 涨跌停 ────────────────────────────────────────────────────
        /// <summary>
        /// 现在是不是封在板上：1=涨停 -1=跌停 0=没封。
        /// 盘中按实时价跟「昨收 ± 涨跌停」比，收工后按今收比 —— 同一套口径，
        /// 所以盘中看到的价格不会因为「收工」这个动作突然变个说法。
        /// </summary>
        public static int BoardState(string id)
        {
            long prev = PrevCloseCents(id);
            if (prev <= 0) return 0;

            List<long> list;
            long now = StockIntraday.IsLive ? LivePrice(id)
                : (StockState.PriceHistory.TryGetValue(id, out list) && list != null && list.Count > 0
                    ? list[list.Count - 1] : StockState.GetPrice(id));

            double lim = StockDefs.LimitOf(StockDefs.Get(id));
            long up = (long)Math.Round(prev * (1.0 + lim));
            long down = (long)Math.Round(prev * (1.0 - lim));
            if (now >= up - 1) return 1;
            if (now <= down + 1) return -1;
            return 0;
        }

        /// <summary>封板标记文字，没封返回空串。</summary>
        public static string BoardText(string id)
        {
            int s = BoardState(id);
            return s > 0 ? "涨停" : (s < 0 ? "跌停" : string.Empty);
        }

        /// <summary>涨跌停价（元），给界面显示「板价」用。</summary>
        public static double LimitPriceYuan(string id, bool up)
        {
            double prev = StockState.ToYuan(PrevCloseCents(id));
            double lim = StockDefs.LimitOf(StockDefs.Get(id));
            return prev * (up ? 1.0 + lim : 1.0 - lim);
        }

        /// <summary>盘中口径的持仓市值（分）。收盘价口径的 StockState.TotalStockValue 保持不动。</summary>
        public static long LiveTotalStockValue()
        {
            long sum = 0;
            foreach (KeyValuePair<string, int> kv in StockState.Positions)
            {
                if (kv.Value > 0) sum += LivePrice(kv.Key) * kv.Value;
            }
            return sum;
        }

        // ── 汇总数据（供 UI 使用）────────────────────────────────────
        public static long PoolYuan() { return (long)StockState.ToYuan(StockState.Pool); }
        public static long StockValueYuan() { return (long)StockState.ToYuan(LiveTotalStockValue()); }
        public static long TotalCostYuan() { return (long)StockState.ToYuan(StockState.TotalCost()); }
        public static long TotalAssetYuan() { return PoolYuan() + StockValueYuan(); }

        /// <summary>
        /// 浮动盈亏：持仓市值 − 持仓成本。因为买入成本里含了手续费，
        /// 刚买完股价一动没动时它也是负的（就是那笔手续费），这是正常的「浮亏」。
        /// </summary>
        public static long FloatingPnlYuan() { return StockValueYuan() - TotalCostYuan(); }

        /// <summary>已实现盈亏：卖出落袋部分的累计。</summary>
        public static long RealizedPnlYuan() { return (long)StockState.ToYuan(StockState.RealizedPnl); }

        /// <summary>累计分红。</summary>
        public static long DividendYuan() { return (long)StockState.ToYuan(StockState.TotalDividend); }

        /// <summary>历史单笔最大实现盈利（元）。</summary>
        public static long MaxSingleProfitYuan() { return (long)StockState.ToYuan(StockState.MaxSingleProfit); }

        /// <summary>总收益 = 浮动盈亏 + 已实现盈亏 + 累计分红。这才是「玩到现在赚了多少」。</summary>
        public static long TotalGainYuan() { return FloatingPnlYuan() + RealizedPnlYuan() + DividendYuan(); }

        public static double ProfitRate()
        {
            long cost = TotalCostYuan();
            if (cost <= 0) return 0;
            return (double)FloatingPnlYuan() / cost * 100.0;
        }

        /// <summary>持仓占总资产的比例（%）。</summary>
        public static double PositionShare()
        {
            long total = TotalAssetYuan();
            if (total <= 0) return 0;
            return (double)StockValueYuan() / total * 100.0;
        }

        /// <summary>
        /// 日涨跌（%）：相对上一交易日的涨跌幅。
        /// 参照必须是「昨日收盘」，不能拿周期锚点顶替——周期锚点是 7 天前的价，
        /// 算出来的是周涨幅，写在「涨跌」列里会和走势图对不上。
        ///
        /// 盘中用实时价对昨收，收工后才换成「今收对昨收」。原来直接读 PriceHistory 的末两项，
        /// 而末项就是**今天已经定好的收盘价**，等于把「今天涨还是跌」提前告诉玩家；
        /// 改成盘中走实时价，这个泄漏就堵住了。
        /// </summary>
        public static double DayChangePercent(string stockId)
        {
            List<long> list;
            if (!StockState.PriceHistory.TryGetValue(stockId, out list) || list == null || list.Count < 2) return 0;

            long prev = list[list.Count - 2];                      // 昨收
            long now = StockIntraday.IsLive
                ? LivePrice(stockId)                               // 盘中：实时价
                : list[list.Count - 1];                            // 收盘后：今日收盘
            if (prev <= 0) return 0;
            return (double)(now - prev) / prev * 100.0;
        }

        public static string RiskStars(int risk)
        {
            string s = string.Empty;
            for (int i = 0; i < 5; i++) s += i < risk ? "★" : "☆";
            return s;
        }

        /// <summary>调试用：把当前账目与行情打成一段文本，方便贴日志核对。</summary>
        public static string DebugDump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("═══ 星际证券 · 状态快照 ═══");
            sb.AppendLine("run=" + StockState.RunId + "　槽位=" + StockState.SaveSlotId
                + "　经济版本=" + StockDefs.EconomyVersion);
            sb.AppendLine("今日=" + StockState.Today + " 天　周期=" + StockState.DayCounter
                + "/" + StockDefs.CycleDays + "　上次事件日=" + StockState.LastEventDay);
            sb.AppendLine("股票账户=" + StockState.ToYuan(StockState.Pool).ToString("N2")
                + " 元　持仓市值=" + StockState.ToYuan(StockState.TotalStockValue()).ToString("N2")
                + " 元　总资产=" + TotalAssetYuan().ToString("N2") + " 元");
            sb.AppendLine("浮动盈亏=" + FloatingPnlYuan().ToString("N2")
                + " 元　已实现=" + RealizedPnlYuan().ToString("N2")
                + " 元　累计分红=" + DividendYuan().ToString("N2")
                + " 元　总收益=" + TotalGainYuan().ToString("N2")
                + " 元（浮动收益率 " + ProfitRate().ToString("0.0") + "%）");
            sb.AppendLine("黑市开户=" + (StockState.License ? "是" : "否")
                + "　资产历史点数=" + StockState.History.Count);

            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef def = StockDefs.All[i];
                long price = StockState.GetPrice(def.Id);
                long anchor;
                StockState.CycleStart.TryGetValue(def.Id, out anchor);
                int shares = StockState.GetPosition(def.Id);
                long value = price * shares;
                long cost = StockState.GetCost(def.Id);
                List<long> hist;
                int points = StockState.PriceHistory.TryGetValue(def.Id, out hist) && hist != null ? hist.Count : 0;
                sb.AppendLine("　" + def.Name + "(" + def.Id + ")　现价=" + StockState.ToYuan(price).ToString("0.00")
                    + "（基准 " + def.BasePrice + "）　公允=" + FairValue(def).ToString("0.00")
                    + "　日涨跌=" + DayChangePercent(def.Id).ToString("0.00") + "%"
                    + "　周期锚=" + StockState.ToYuan(anchor).ToString("0.00")
                    + "　持仓=" + shares + " 股　市值=" + StockState.ToYuan(value).ToString("0.00")
                    + "　成本=" + StockState.ToYuan(cost).ToString("0.00")
                    + "　历史点=" + points
                    + "　盘中=" + StockIntraday.PriceAt(def.Id, StockIntraday.Step).ToString("0.00")
                    + "　段=" + StockIntraday.Step + "/" + StockIntraday.Total);
            }

            sb.AppendLine("活跃事件=" + (StockState.Events.Count == 0 ? "无" : string.Empty));
            for (int i = 0; i < StockState.Events.Count; i++)
            {
                ActiveEvent ev = StockState.Events[i];
                StockEventDef def = StockDefs.GetEvent(ev.DefId);
                // 目标要分三种情况看：随机单支（TargetStockId）、指定几支（Targets）、全表
                string targets;
                if (ev.TargetStockId != null) targets = "单支:" + ev.TargetStockId;
                else if (def != null && def.Targets != null && def.Targets.Length > 0)
                {
                    targets = string.Join(",", def.Targets);
                }
                else targets = "全表";
                sb.AppendLine("　" + (def != null ? def.Name : ev.DefId)
                    + "　剩 " + ev.DaysLeft + " 天"
                    + "　目标=" + targets
                    + "　每日冲击=" + (def != null ? (def.Impact * 100.0).ToString("0.0") + "%" : "?")
                    + "　发布=" + (ev.IntradayDay >= 0 ? "盘中第" + ev.IntradayDay + "天" : "盘后"));
            }
            return sb.ToString();
        }
    }
}
