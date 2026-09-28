using System;

namespace StockMarket
{
    /// <summary>一条阶段任务的定义。判定与进度都从当前账目实时算出来，不额外存进度。</summary>
    public sealed class QuestDef
    {
        public string Title;
        public string Desc;
        public int Reward;      // 奖励（元）
    }

    /// <summary>
    /// 新手任务系统。
    ///
    /// 设计意图（对标大厂新手线）：每一条都对应一个「玩家本来就会做、但不知道为什么要做」的动作，
    /// 用现金奖励把它变成一次明确的目标，六条走完刚好把股票系统的核心循环跑一遍：
    /// 买入 → 卖出 → 拿分红 → 攒市值 → 赚一笔 → 分散持仓。
    ///
    /// 完成与领奖用两个位图存在存档里（sm_meta 的 qdone / qclaim），
    /// 进度本身不存——直接从 Positions / RealizedPnl 这些账目算，永远不会和实际对不上。
    /// </summary>
    public static class StockQuest
    {
        /// <summary>
        /// 六条任务的奖励总额上限（元）。任务奖励是「启动资金」，不是收入来源，
        /// 给多了会让玩家觉得炒股不如做任务，所以整条新手线加起来封在 300。
        /// </summary>
        public const int RewardCapTotal = 300;

        // 奖励之和正好等于 RewardCapTotal，多一分都没有。
        public static readonly QuestDef[] All =
        {
            new QuestDef { Title = "开张第一单", Desc = "买入任意一支股票，1 股也算。", Reward = 20 },
            new QuestDef { Title = "落袋为安",   Desc = "完成一次卖出，把浮盈变成真钱。", Reward = 40 },
            new QuestDef { Title = "第一笔分红", Desc = "持仓跨过结算周期（7 天）领分红。", Reward = 50 },
            new QuestDef { Title = "小有积蓄",   Desc = "让持仓市值达到 50 元。", Reward = 50 },
            new QuestDef { Title = "赚一笔",     Desc = "单笔卖出实现盈利 20 元以上。", Reward = 60 },
            new QuestDef { Title = "分散风险",   Desc = "同时持有 3 支不同股票。", Reward = 80 },
        };

        /// <summary>已经发出去的奖励总额（元）。用来卡死 300 的上限。</summary>
        public static int ClaimedRewardTotal()
        {
            int sum = 0;
            for (int i = 0; i < All.Length; i++)
            {
                if (IsClaimed(i)) sum += All[i].Reward;
            }
            return sum;
        }

        /// <summary>任务总数。</summary>
        public static int Count { get { return All.Length; } }

        /// <summary>已买入的标的支数。</summary>
        public static int HeldCount()
        {
            int n = 0;
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                if (StockState.GetPosition(StockDefs.All[i].Id) > 0) n++;
            }
            return n;
        }

        /// <summary>当前是否满足第 i 条任务的完成条件。</summary>
        public static bool Satisfied(int i)
        {
            switch (i)
            {
                case 0: return HeldCount() > 0;
                case 1: return StockState.SellCount > 0;
                case 2: return StockState.TotalDividend > 0;
                case 3: return StockState.ToYuan(StockEngine.LiveTotalStockValue()) >= 50.0;
                case 4: return StockState.ToYuan(StockState.MaxSingleProfit) >= 20.0;
                case 5: return HeldCount() >= 3;
                default: return false;
            }
        }

        /// <summary>进度文本，例如「3 / 3 支」。给列表右侧显示用。</summary>
        public static string Progress(int i)
        {
            switch (i)
            {
                case 0: return HeldCount() > 0 ? "已买入" : "未买入";
                case 1: return StockState.SellCount + " 次";
                case 2: return StockEngine.DividendYuan().ToString("N0") + " 元";
                case 3: return StockEngine.StockValueYuan().ToString("N0") + " / 50 元";
                case 4: return StockEngine.MaxSingleProfitYuan().ToString("N0") + " / 20 元";
                case 5: return HeldCount() + " / 3 支";
                default: return "-";
            }
        }

        public static bool IsDone(int i)
        {
            return (StockState.QuestDone & (1 << i)) != 0;
        }

        public static bool IsClaimed(int i)
        {
            return (StockState.QuestClaimed & (1 << i)) != 0;
        }

        /// <summary>
        /// 刷新完成状态：把「已经满足但还没标记」的任务补上位。
        /// 每帧或每次刷新面板时调用都很便宜（只有 6 次比较）。
        /// 返回新完成的任务条数。
        /// </summary>
        public static int Sync()
        {
            int newly = 0;
            for (int i = 0; i < All.Length; i++)
            {
                if (IsDone(i) || !Satisfied(i)) continue;
                StockState.QuestDone |= 1 << i;
                newly++;
                Core.Log.Msg("[任务] 完成「" + All[i].Title + "」，可领取 " + All[i].Reward + " 元奖励");
            }
            if (newly > 0) StockState.Dirty = true;
            return newly;
        }

        /// <summary>领取奖励。返回错误文本，成功返回 null。</summary>
        public static string Claim(int i)
        {
            if (i < 0 || i >= All.Length) return "任务不存在";
            if (!IsDone(i)) return "任务还没完成";
            if (IsClaimed(i)) return "这份奖励已经领过了";

            // 双保险：奖励表加起来正好是 300，这里再卡一道。
            // 万一以后有人改了某条的 Reward 忘了重算总和，也不会超发。
            int paid = ClaimedRewardTotal();
            if (paid + All[i].Reward > RewardCapTotal)
            {
                Core.Log.Warning("[任务] 奖励总额已达上限，拒绝发放：" + All[i].Title);
                return "新手任务奖励总额上限 " + RewardCapTotal + " 元，已经发满了";
            }

            StockState.QuestClaimed |= 1 << i;
            StockState.Pool += StockState.ToCents(All[i].Reward);
            StockState.Dirty = true;
            Core.Log.Msg("[任务] 领取「" + All[i].Title + "」奖励 " + All[i].Reward
                + " 元（累计 " + (paid + All[i].Reward) + "/" + RewardCapTotal + "）");

            // 领奖必须立刻落盘。只标 Dirty 的话，玩家领完直接退游戏，
            // 下次进档这一位又变回「未领」，看起来就像能重复领奖。
            StockState.Flush(Il2Cpp.PlayerStore.instance);
            return null;
        }

        /// <summary>还没领奖的已完成任务数，导航栏上用来提示。</summary>
        public static int ClaimableCount()
        {
            int n = 0;
            for (int i = 0; i < All.Length; i++)
            {
                if (IsDone(i) && !IsClaimed(i)) n++;
            }
            return n;
        }

        /// <summary>已领奖任务数。</summary>
        public static int ClaimedCount()
        {
            int n = 0;
            for (int i = 0; i < All.Length; i++)
            {
                if (IsClaimed(i)) n++;
            }
            return n;
        }

        /// <summary>下一个还没完成的任务下标，全完成返回 -1。总览页提示用。</summary>
        public static int NextOpen()
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (!IsDone(i)) return i;
            }
            return -1;
        }

        /// <summary>调试用：把所有任务直接标成已完成（奖励仍需手动领）。</summary>
        public static void ForceDoneAll()
        {
            int all = 0;
            for (int i = 0; i < All.Length; i++) all |= 1 << i;
            StockState.QuestDone = all;
            StockState.Dirty = true;
            Core.Log.Msg("[任务] 调试：全部 " + All.Length + " 条任务已标记为完成");
        }
    }
}
