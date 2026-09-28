using System;
using System.Collections.Generic;
using System.Text;
using Il2Cpp;
using Il2CppDict = Il2CppSystem.Collections.Generic.Dictionary<string, string>;

namespace StockMarket
{
    public sealed class DaySnapshot
    {
        public int Day;
        public long Pool;        // 股票账户资金（分）
        public long StockValue;  // 持仓市值（分）
    }

    public sealed class ActiveEvent
    {
        public string DefId;
        public int DaysLeft;
        public string TargetStockId; // 仅 CRASH / SURGE 这类随机单支事件使用
        /// <summary>这条消息是哪一天走「盘中发布」的；-1 表示走盘后（夜间新闻）。</summary>
        public int IntradayDay = -1;
    }

    /// <summary>
    /// 全部运行时状态 + 存档读写。数据落在 PlayerStore.modData，随存档自动保存。
    /// 价格一律以「分」为单位存 long，避免浮点漂移。
    /// </summary>
    public static class StockState
    {
        public const string KeyMeta = "sm_meta";
        public const string KeyPrice = "sm_px";
        public const string KeyPos = "sm_pos";
        public const string KeyCost = "sm_cost";
        public const string KeyEvent = "sm_ev";
        public const string KeyHistory = "sm_hist";
        public const string KeyPriceHistory = "sm_phist";
        public const string KeyCycleStart = "sm_cstart";
        public const string KeyFavorites = "sm_fav";
        public const string KeyPositionStart = "sm_postart";
        public const string KeyQuest = "sm_quest";
        public const string KeyFriend = "sm_friend";
        public const string KeyChat = "sm_chat";
        public const string KeyLeverage = "sm_lev";
        public const string KeyIntra = "sm_intra";
        public const string KeyOrder = "sm_order";
        public const string KeyVip = "sm_vip";
        public const string KeyJournal = "sm_journal";
        public const string KeyEcon = "sm_econ";
        public const string KeyMacro = "sm_macro";
        public const string FormatVersion = "1";

        public static bool Loaded;

        public static string RunId = string.Empty;
        public static int SaveSlotId;            // 存档位号：同一个 run 存进两个槽位时靠它区分
        public static long Pool;                 // 股票账户余额（分）
        public static bool License;              // 黑市开户证明
        public static int DayCounter;            // 距上次周期结算的天数
        public static int LastEventDay = -999;   // 上次触发事件的天数
        /// <summary>下一次事件触发的天数（每次都随机重抽，所以节奏不固定）。</summary>
        public static int NextEventDay = 3;
        public static int Today;                 // 当前游戏天数

        /// <summary>已实现盈亏（分）：卖出时「净收入 − 对应成本份额」的累计，落袋为准。</summary>
        public static long RealizedPnl;
        /// <summary>累计分红（分）：历史周期派息总额，单独记一笔，方便玩家看清钱的来源。</summary>
        public static long TotalDividend;
        /// <summary>当前持仓的建仓日（游戏天数），-1 表示空仓。用来判断有没有「持满一个完整周期」。</summary>
        public static readonly Dictionary<string, int> PositionStart = new Dictionary<string, int>();

        // ── 新手任务 ──────────────────────────────────────────────────
        /// <summary>任务完成位图（第 i 位 = 第 i 条已完成）。</summary>
        public static int QuestDone;
        /// <summary>任务已领奖位图。</summary>
        public static int QuestClaimed;
        /// <summary>累计卖出次数（任务「落袋为安」用）。</summary>
        public static int SellCount;
        /// <summary>历史单笔最大实现盈利（分），可能为负。</summary>
        public static long MaxSingleProfit;
        /// <summary>新手引导进度：-1 = 未开始 / 已结束，其余为当前步号。</summary>
        public static int TutorialStep = -1;
        /// <summary>
        /// 「点进去自动弹一次」的本页导览位图：第 page 位为 1 表示那一页已经弹过了。
        /// 主线走完后玩家第一次进某一页就自动放一遍该页导览，看过的页不再打扰。
        /// </summary>
        public static int TourSeen;

        // ── AI 好友「老K」──────────────────────────────────────────────
        /// <summary>当前待处理的报价，null 表示老K暂时没动作。</summary>
        public static FriendOffer Offer;

        public static readonly Dictionary<string, long> Prices = new Dictionary<string, long>();
        public static readonly Dictionary<string, int> Positions = new Dictionary<string, int>();
        public static readonly Dictionary<string, long> CostBasis = new Dictionary<string, long>();
        public static readonly Dictionary<string, long> CycleStart = new Dictionary<string, long>();
        public static readonly List<ActiveEvent> Events = new List<ActiveEvent>();
        public static readonly List<DaySnapshot> History = new List<DaySnapshot>();
        /// <summary>每支股票的收盘价历史（分），用于交易页的走势图。</summary>
        public static readonly Dictionary<string, List<long>> PriceHistory = new Dictionary<string, List<long>>();
        /// <summary>收藏的标的 Id，按收藏先后排列，列表里会置顶显示。</summary>
        public static readonly List<string> Favorites = new List<string>();

        public static bool Dirty;

        // ── 单位换算 ──────────────────────────────────────────────────
        public static double ToYuan(long cents) { return cents / StockDefs.PriceScale; }
        public static long ToCents(double yuan) { return (long)Math.Round(yuan * StockDefs.PriceScale); }
        public static double PriceYuan(string id) { return ToYuan(GetPrice(id)); }

        public static long GetPrice(string id)
        {
            long p;
            return Prices.TryGetValue(id, out p) && p > 0 ? p : ToCents(StockDefs.Get(id) != null ? StockDefs.Get(id).BasePrice : 100);
        }

        public static int GetPosition(string id)
        {
            int n;
            return Positions.TryGetValue(id, out n) ? n : 0;
        }

        public static long GetCost(string id)
        {
            long c;
            return CostBasis.TryGetValue(id, out c) ? c : 0L;
        }

        /// <summary>建仓日；没有记录返回 -1。</summary>
        public static int GetPositionStart(string id)
        {
            int d;
            return PositionStart.TryGetValue(id, out d) ? d : -1;
        }

        /// <summary>
        /// 这支持仓是否「持满了一个完整周期」。派息的前提条件：
        /// 买入当周期不派息，否则在结算前一天买入就能白拿一次分红，等于无风险套利。
        /// </summary>
        public static bool HeldFullCycle(string id)
        {
            int start = GetPositionStart(id);
            return start >= 0 && Today - start >= StockDefs.CycleDays;
        }

        /// <summary>从空仓变成有仓：记下建仓日。加仓不重置，避免"最后一天加仓套分红"。</summary>
        public static void MarkBought(string id, int before)
        {
            if (before <= 0) PositionStart[id] = Today;
        }

        /// <summary>清仓：抹掉建仓日。</summary>
        public static void MarkCleared(string id)
        {
            PositionStart[id] = -1;
        }

        // ── 收藏 ──────────────────────────────────────────────────────
        public static bool IsFavorite(string id)
        {
            return !string.IsNullOrEmpty(id) && Favorites.Contains(id);
        }

        /// <summary>切换收藏，返回切换后是否已收藏。</summary>
        public static bool ToggleFavorite(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            if (Favorites.Remove(id)) { Dirty = true; return false; }
            Favorites.Add(id);
            Dirty = true;
            return true;
        }

        /// <summary>持仓市值合计（分）。</summary>
        public static long TotalStockValue()
        {
            long sum = 0;
            foreach (KeyValuePair<string, int> kv in Positions)
            {
                if (kv.Value > 0) sum += GetPrice(kv.Key) * kv.Value;
            }
            return sum;
        }

        /// <summary>持仓成本合计（分）。</summary>
        public static long TotalCost()
        {
            long sum = 0;
            foreach (KeyValuePair<string, long> kv in CostBasis) sum += kv.Value;
            return sum;
        }

        /// <summary>记录当日收盘价。由 DailyTick 每支股票各调一次。</summary>
        public static void RecordPrice(string id)
        {
            List<long> list;
            if (!PriceHistory.TryGetValue(id, out list) || list == null)
            {
                list = new List<long>();
                PriceHistory[id] = list;
            }
            list.Add(GetPrice(id));
            while (list.Count > StockDefs.HistoryLimit) list.RemoveAt(0);
        }

        /// <summary>
        /// 取第 day 天的收盘价（分）。历史里没有那一天就返回 0。
        ///
        /// 收盘价每天按顺序追加一条，所以「距今多少个交易日」就等于从尾部往回退几步。
        /// 股评的事后核对（三天后涨没涨）就靠它，不必另外存一份「预测结果」。
        /// </summary>
        public static long PriceOnDay(string id, int day)
        {
            List<long> list;
            if (!PriceHistory.TryGetValue(id, out list) || list == null || list.Count == 0) return 0;
            int back = Today - day;
            if (back < 0) return 0;
            int idx = list.Count - 1 - back;
            if (idx < 0 || idx >= list.Count) return 0;
            return list[idx];
        }

        /// <summary>
        /// 取某支股票的走势序列（元），保证至少有一个点。
        /// 只有当末尾和实时价对不上时才补一个点：RecordPrice 在日结算里刚记过当天的收盘价，
        /// 无条件再补一个就会多出一个重复点，最后一段线永远是平的、横轴刻度也会重复。
        /// </summary>
        public static List<double> PriceSeries(string id, int maxPoints)
        {
            List<double> result = new List<double>();
            List<long> list;
            if (PriceHistory.TryGetValue(id, out list) && list != null)
            {
                int start = list.Count > maxPoints ? list.Count - maxPoints : 0;
                for (int i = start; i < list.Count; i++) result.Add(ToYuan(list[i]));
            }
            double now = StockEngine.LivePriceYuan(id);
            if (result.Count == 0 || Math.Abs(result[result.Count - 1] - now) > 0.0001) result.Add(now);
            return result;
        }

        // ── 初始化 ────────────────────────────────────────────────────
        public static void ResetToDefaults(string runId)
        {
            RunId = runId ?? string.Empty;
            Pool = 0;
            License = false;
            DayCounter = 0;
            LastEventDay = -999;
            NextEventDay = 3;
            Today = 0;
            RealizedPnl = 0;
            TotalDividend = 0;
            QuestDone = 0;
            QuestClaimed = 0;
            SellCount = 0;
            MaxSingleProfit = 0;
            TutorialStep = -1;
            TourSeen = 0;
            Offer = null;
            StockFriend.Reset();
            StockChat.Reset();
            StockLeverage.Reset();
            StockIntraday.Reset();
            StockOrders.Reset();
            StockBots.Reset();
            StockReview.Reset();
            StockVip.Reset();
            StockJournal.Reset();
            StockEconomy.Reset();
            StockMacro.Reset();
            StockGameEvents.Reset();
            StockEngine.Reset();
            Prices.Clear();
            Positions.Clear();
            CostBasis.Clear();
            CycleStart.Clear();
            PositionStart.Clear();
            Events.Clear();
            History.Clear();
            PriceHistory.Clear();
            Favorites.Clear();
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef d = StockDefs.All[i];
                Prices[d.Id] = ToCents(d.BasePrice);
                CycleStart[d.Id] = ToCents(d.BasePrice);
                Positions[d.Id] = 0;
                CostBasis[d.Id] = 0;
                PositionStart[d.Id] = -1;
                PriceHistory[d.Id] = new List<long>();
            }
            Dirty = true;
        }

        /// <summary>确保当前存档的状态已就绪。换档（runID 或存档位变化）会自动重建。</summary>
        public static void EnsureLoaded(PlayerStore store)
        {
            if (store == null) return;
            string run = store.runID ?? string.Empty;
            int slot = SlotOf(store);

            if (Loaded && RunId == run && SaveSlotId == slot && run.Length > 0) return;

            Il2CppDict data = store.modData;
            string meta = Read(data, KeyMeta);
            string metaRun = ReadField(meta, "run");
            string metaSlot = ReadField(meta, "slot");

            // 存档身份 = runID + 存档位号。同一个 run 存进两个槽位时 runID 是一样的，
            // 只比 runID 会把上一个槽位留在内存里的账目当成这个槽位的，所以槽位也要比。
            // 老存档的 meta 里没有 slot 字段，这种情况只比 runID，不做无谓的重建。
            bool slotMismatch = !string.IsNullOrEmpty(metaSlot) && ParseInt(metaSlot, slot) != slot;

            if (string.IsNullOrEmpty(meta) || metaRun != run || slotMismatch)
            {
                // 新档或换档：重建，避免串档
                ResetToDefaults(run);
                SaveSlotId = slot;
                BackfillHistory();
                Loaded = true;
                Core.Debug("新建股票账目，run=" + run + " 槽位=" + slot);
                return;
            }

            // 股价量级调整过：老存档里的价位是旧基价算出来的，留着会和新基价打架
            // （均值回归要几十天才能收敛回去），直接重建。
            if (ReadField(meta, "econ") != StockDefs.EconomyVersion)
            {
                ResetToDefaults(run);
                SaveSlotId = slot;
                BackfillHistory();
                Loaded = true;
                Core.Log.Msg("股价量级已调整，股票账目按新基价重建。");
                return;
            }

            try
            {
                ResetToDefaults(run);
                SaveSlotId = slot;
                DayCounter = ParseInt(ReadField(meta, "day"), 0);
                Pool = ParseLong(ReadField(meta, "pool"), 0);
                License = ReadField(meta, "lic") == "1";
                LastEventDay = ParseInt(ReadField(meta, "evday"), -999);
                // 老存档没有「下次事件日」：按上一次事件往后挪几天，别一读档就立刻爆一条
                NextEventDay = ParseInt(ReadField(meta, "evnext"), -1);
                if (NextEventDay < 0) NextEventDay = LastEventDay > -900 ? LastEventDay + 3 : 3;
                Today = ParseInt(ReadField(meta, "today"), 0);
                RealizedPnl = ParseLong(ReadField(meta, "rpnl"), 0);
                TotalDividend = ParseLong(ReadField(meta, "div"), 0);
                QuestDone = ParseInt(ReadField(meta, "qdone"), 0);
                QuestClaimed = ParseInt(ReadField(meta, "qclaim"), 0);
                SellCount = ParseInt(ReadField(meta, "sell"), 0);
                MaxSingleProfit = ParseLong(ReadField(meta, "msp"), 0);
                TutorialStep = ParseInt(ReadField(meta, "tut"), -1);
                // 教程从 5 步扩到 8 步后，老的完成哨兵 5 会被当成「第 6 步」而重弹引导，
                // 这里统一迁到新哨兵 100（和 StockUI.TutorialDone 保持一致）。
                if (TutorialStep == 5) TutorialStep = 100;
                TourSeen = ParseInt(ReadField(meta, "tseen"), 0);

                ParseLongMap(Read(data, KeyPrice), Prices);
                ParseIntMap(Read(data, KeyPos), Positions);
                ParseLongMap(Read(data, KeyCost), CostBasis);
                ParseLongMap(Read(data, KeyCycleStart), CycleStart);
                ParseIntMap(Read(data, KeyPositionStart), PositionStart);
                ParseEvents(Read(data, KeyEvent));
                ParseHistory(Read(data, KeyHistory));
                ParsePriceHistory(Read(data, KeyPriceHistory));
                ParseFavorites(Read(data, KeyFavorites));
                StockFriend.Parse(Read(data, KeyFriend));
                StockChat.Parse(Read(data, KeyChat));
                StockLeverage.Parse(Read(data, KeyLeverage));
                StockVip.Parse(Read(data, KeyVip));
                StockJournal.Parse(Read(data, KeyJournal));
                StockEconomy.Parse(Read(data, KeyEcon));
                StockMacro.Parse(Read(data, KeyMacro));
                // 盘中进度与挂单：二者的解析都依赖 Today 和 Prices，所以必须排在这两样之后
                StockIntraday.Parse(Read(data, KeyIntra));
                StockOrders.Parse(Read(data, KeyOrder));

                // 老存档没有建仓日这一项：已经有仓的按「早就持满一个周期」处理，
                // 否则这些持仓会永远拿不到分红（建仓日一直是 -1）。
                for (int i = 0; i < StockDefs.All.Length; i++)
                {
                    string id = StockDefs.All[i].Id;
                    if (GetPosition(id) > 0 && GetPositionStart(id) < 0) PositionStart[id] = Today - StockDefs.CycleDays;
                }

                // 股票池换过（标的增删）时，老存档里会留着已经下架的标的：
                // 它们在界面上看不见，市值却还在算，会让「持仓市值」对不上持仓页。
                PruneUnknown();

                BackfillHistory();

                Loaded = true;
                Dirty = false;
                Core.Debug("载入股票账目：天数=" + DayCounter + " 余额=" + ToYuan(Pool));
            }
            catch (Exception ex)
            {
                Core.Log.Warning("读取股票存档失败，已重置：" + ex.Message);
                ResetToDefaults(run);
                SaveSlotId = slot;
                Loaded = true;
            }
        }

        /// <summary>摘掉存档里已经不在当前股票池中的条目（换池后遗留的孤儿）。</summary>
        private static void PruneUnknown()
        {
            int before = Prices.Count + Positions.Count + CostBasis.Count
                       + CycleStart.Count + PositionStart.Count + PriceHistory.Count;
            PruneMap(Prices);
            PruneMap(Positions);
            PruneMap(CostBasis);
            PruneMap(CycleStart);
            PruneMap(PositionStart);
            PruneMap(PriceHistory);
            Favorites.RemoveAll(id => StockDefs.Get(id) == null);
            int after = Prices.Count + Positions.Count + CostBasis.Count
                      + CycleStart.Count + PositionStart.Count + PriceHistory.Count;
            if (after != before) Core.Debug("清理已下架标的的存档条目：" + (before - after) + " 项");
        }

        /// <summary>
        /// 先收集再删除：不能在遍历字典的过程中直接 Remove，会抛「集合已被修改」。
        /// </summary>
        private static void PruneMap<T>(Dictionary<string, T> map)
        {
            List<string> drop = null;
            foreach (string id in map.Keys)
            {
                if (StockDefs.Get(id) == null)
                {
                    if (drop == null) drop = new List<string>();
                    drop.Add(id);
                }
            }
            if (drop == null) return;
            for (int i = 0; i < drop.Count; i++) map.Remove(drop[i]);
        }

        // ── 历史回溯 ──────────────────────────────────────────────────

        /// <summary>
        /// 把每支标的的历史补到 BackfillDays 天。老存档最多只记了 60 天，
        /// 技术分析开「3 月 / 半年 / 一年」档会是空的，长周期均线和 MACD 也算不出来。
        ///
        /// 做法是「倒着走」：从已有数据最早的那一天开始，按各标的自己的波动率
        /// 往前倒推随机行情，再接到真实数据前面。倒着推而不是从某个初始价正着推，
        /// 是为了保证接缝连续——生成出来的最后一点正好是真实数据的前一步，图上不会有断崖。
        /// </summary>
        private static void BackfillHistory()
        {
            int added = 0;
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef def = StockDefs.All[i];
                List<long> list;
                if (!PriceHistory.TryGetValue(def.Id, out list) || list == null)
                {
                    list = new List<long>();
                    PriceHistory[def.Id] = list;
                }
                int missing = StockDefs.BackfillDays - list.Count;
                if (missing <= 0) continue;

                long anchor = list.Count > 0 && list[0] > 0 ? list[0] : GetPrice(def.Id);
                double price = ToYuan(anchor);
                double sigma = def.Sigma > 0.0 ? def.Sigma : 0.05;
                uint seed = Hash32(def.Id + "|backfill");

                List<long> older = new List<long>(missing);
                for (int k = 0; k < missing; k++)
                {
                    // 倒着走一步：昨天 = 今天 / (1 + 当日涨跌幅)
                    double step = (NextUnit(ref seed) - 0.5) * 2.0 * sigma;
                    price = price / (1.0 + step);
                    if (price < 1.0) price = 1.0;
                    older.Add(ToCents(price));
                }
                older.Reverse();          // 翻成「从旧到新」
                older.AddRange(list);     // 接到真实数据前面
                PriceHistory[def.Id] = older;
                added++;
            }
            if (added > 0)
            {
                Core.Log.Msg("回溯补齐历史行情：" + added + " 支标的，各 "
                    + StockDefs.BackfillDays + " 天。");
            }
        }

        /// <summary>FNV-1a，用来给「回溯行情」和「合成成交量」取稳定的种子。</summary>
        public static uint Hash32(string s)
        {
            uint h = 2166136261u;
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619u;
                }
            }
            return h == 0u ? 1u : h;
        }

        /// <summary>xorshift32 取 [0,1)。</summary>
        public static double NextUnit(ref uint seed)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            return (seed >> 8) / 16777216.0;
        }

        /// <summary>某支标的目前攒了多少天历史。</summary>
        public static int HistoryDays(string id)
        {
            List<long> list;
            return PriceHistory.TryGetValue(id, out list) && list != null ? list.Count : 0;
        }

        /// <summary>
        /// 取最近 days 天的收盘价（元）。天数不够就有多少给多少，不会补零——
        /// 补零会让图从 0 元开始画，看起来像退市。
        /// </summary>
        public static List<long> PriceWindow(string id, int days)
        {
            List<long> result = new List<long>();
            List<long> list;
            if (!PriceHistory.TryGetValue(id, out list) || list == null) return result;
            int start = list.Count > days ? list.Count - days : 0;
            for (int i = start; i < list.Count; i++) result.Add(list[i]);
            return result;
        }

        /// <summary>存档位号。取不到就按 0 处理，不影响 runID 比对。</summary>
        private static int SlotOf(PlayerStore store)
        {
            try { return store.saveSlotId; }
            catch { return 0; }
        }

        /// <summary>把运行时状态写回 modData。在游戏存盘前调用。</summary>
        public static void Flush(PlayerStore store)
        {
            if (store == null || !Loaded) return;
            try
            {
                // 存档位换了却还没重新载入：宁可不写，也不要把上一个档的账目写进新档
                if ((store.runID ?? string.Empty) != RunId)
                {
                    Core.Log.Warning("当前存档与股票账目不一致，已跳过写入以免串档。");
                    return;
                }

                Il2CppDict data = store.modData;
                if (data == null)
                {
                    data = new Il2CppDict();
                    store.modData = data;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("v").Append(FormatVersion);
                sb.Append("|run=").Append(RunId);
                sb.Append("|day=").Append(DayCounter);
                sb.Append("|pool=").Append(Pool);
                sb.Append("|lic=").Append(License ? "1" : "0");
                sb.Append("|evday=").Append(LastEventDay);
                sb.Append("|evnext=").Append(NextEventDay);
                sb.Append("|today=").Append(Today);
                sb.Append("|rpnl=").Append(RealizedPnl);
                sb.Append("|div=").Append(TotalDividend);
                sb.Append("|qdone=").Append(QuestDone);
                sb.Append("|qclaim=").Append(QuestClaimed);
                sb.Append("|sell=").Append(SellCount);
                sb.Append("|msp=").Append(MaxSingleProfit);
                sb.Append("|tut=").Append(TutorialStep);
                sb.Append("|tseen=").Append(TourSeen);
                sb.Append("|econ=").Append(StockDefs.EconomyVersion);
                sb.Append("|slot=").Append(SaveSlotId);
                Write(data, KeyMeta, sb.ToString());

                Write(data, KeyPrice, DumpMap(Prices));
                Write(data, KeyPos, DumpMap(Positions));
                Write(data, KeyCost, DumpMap(CostBasis));
                Write(data, KeyCycleStart, DumpMap(CycleStart));
                Write(data, KeyPositionStart, DumpMap(PositionStart));
                Write(data, KeyEvent, DumpEvents());
                Write(data, KeyHistory, DumpHistory());
                Write(data, KeyPriceHistory, DumpPriceHistory());
                Write(data, KeyFavorites, DumpFavorites());
                Write(data, KeyFriend, StockFriend.Dump());
                Write(data, KeyChat, StockChat.Dump());
                Write(data, KeyLeverage, StockLeverage.Dump());
                Write(data, KeyVip, StockVip.Dump());
                Write(data, KeyJournal, StockJournal.Dump());
                Write(data, KeyEcon, StockEconomy.Dump());
                Write(data, KeyMacro, StockMacro.Dump());
                // 盘中进度必须落盘：中途退出游戏再读档时，分时图要接着走，不能从开盘重来
                Write(data, KeyIntra, StockIntraday.Dump());
                Write(data, KeyOrder, StockOrders.Dump());

                Dirty = false;
            }
            catch (Exception ex)
            {
                Core.Log.Warning("写入股票存档失败：" + ex.Message);
            }
        }

        public static void Clear(PlayerStore store)
        {
            // 顺序不能反：Flush 见到 Loaded=false 会直接返回，那样 modData 里的旧账目根本没被清掉
            ResetToDefaults(store != null ? store.runID : string.Empty);
            SaveSlotId = SlotOf(store);
            Loaded = true;
            Flush(store);
        }

        // ── modData 低层读写 ─────────────────────────────────────────
        private static string Read(Il2CppDict data, string key)
        {
            if (data == null) return null;
            string value;
            return data.TryGetValue(key, out value) ? value : null;
        }

        private static void Write(Il2CppDict data, string key, string value)
        {
            if (data == null) return;
            data[key] = value ?? string.Empty;
        }

        // ── 序列化辅助 ───────────────────────────────────────────────
        private static string ReadField(string meta, string name)
        {
            if (string.IsNullOrEmpty(meta)) return null;
            string[] parts = meta.Split('|');
            string prefix = name + "=";
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return parts[i].Substring(prefix.Length);
                }
            }
            return null;
        }

        private static int ParseInt(string text, int fallback)
        {
            int v;
            return int.TryParse(text, out v) ? v : fallback;
        }

        private static long ParseLong(string text, long fallback)
        {
            long v;
            return long.TryParse(text, out v) ? v : fallback;
        }

        private static string DumpMap<T>(Dictionary<string, T> map)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, T> kv in map)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        private static void ParseLongMap(string text, Dictionary<string, long> map)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] parts = text.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string[] kv = parts[i].Split('=');
                if (kv.Length != 2) continue;
                map[kv[0]] = ParseLong(kv[1], 0);
            }
        }

        private static void ParseIntMap(string text, Dictionary<string, int> map)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] parts = text.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string[] kv = parts[i].Split('=');
                if (kv.Length != 2) continue;
                map[kv[0]] = ParseInt(kv[1], 0);
            }
        }

        private static string DumpEvents()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Events.Count; i++)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(Events[i].DefId).Append(':')
                  .Append(Events[i].DaysLeft).Append(':')
                  .Append(Events[i].TargetStockId ?? string.Empty).Append(':')
                  .Append(Events[i].IntradayDay);
            }
            return sb.ToString();
        }

        private static void ParseEvents(string text)
        {
            Events.Clear();
            if (string.IsNullOrEmpty(text)) return;
            string[] parts = text.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string[] f = parts[i].Split(':');
                if (f.Length < 2 || StockDefs.GetEvent(f[0]) == null) continue;
                Events.Add(new ActiveEvent
                {
                    DefId = f[0],
                    DaysLeft = ParseInt(f[1], 0),
                    TargetStockId = f.Length > 2 && f[2].Length > 0 ? f[2] : null,
                    IntradayDay = f.Length > 3 ? ParseInt(f[3], -1) : -1
                });
            }
        }

        private static string DumpHistory()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < History.Count; i++)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(History[i].Day).Append(',')
                  .Append(History[i].Pool).Append(',')
                  .Append(History[i].StockValue);
            }
            return sb.ToString();
        }

        private static void ParseHistory(string text)
        {
            History.Clear();
            if (string.IsNullOrEmpty(text)) return;
            string[] parts = text.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string[] f = parts[i].Split(',');
                if (f.Length != 3) continue;
                History.Add(new DaySnapshot
                {
                    Day = ParseInt(f[0], 0),
                    Pool = ParseLong(f[1], 0),
                    StockValue = ParseLong(f[2], 0)
                });
            }
        }

        private static string DumpPriceHistory()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, List<long>> kv in PriceHistory)
            {
                if (kv.Value == null || kv.Value.Count == 0) continue;
                if (sb.Length > 0) sb.Append('|');
                sb.Append(kv.Key).Append(':');
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(kv.Value[i]);
                }
            }
            return sb.ToString();
        }

        private static void ParsePriceHistory(string text)
        {
            PriceHistory.Clear();
            if (string.IsNullOrEmpty(text)) return;
            string[] parts = text.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                int colon = parts[i].IndexOf(':');
                if (colon <= 0) continue;
                string id = parts[i].Substring(0, colon);
                List<long> list = new List<long>();
                string[] nums = parts[i].Substring(colon + 1).Split(',');
                for (int n = 0; n < nums.Length; n++)
                {
                    long v;
                    if (long.TryParse(nums[n], out v)) list.Add(v);
                }
                PriceHistory[id] = list;
            }
        }

        private static string DumpFavorites()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Favorites.Count; i++)
            {
                if (string.IsNullOrEmpty(Favorites[i])) continue;
                if (sb.Length > 0) sb.Append('|');
                sb.Append(Favorites[i]);
            }
            return sb.ToString();
        }

        private static void ParseFavorites(string text)
        {
            Favorites.Clear();
            if (string.IsNullOrEmpty(text)) return;
            string[] parts = text.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                // 标的下架后存档里可能留着旧 Id，这里顺手滤掉，免得列表里出现空行
                if (!string.IsNullOrEmpty(parts[i]) && StockDefs.Get(parts[i]) != null) Favorites.Add(parts[i]);
            }
        }
    }
}
