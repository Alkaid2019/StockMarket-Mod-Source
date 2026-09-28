using System;
using System.Collections.Generic;
using System.Text;

namespace StockMarket
{
    /// <summary>一笔成交流水。Side = 0 买入 / 1 卖出。</summary>
    public sealed class TradeRec
    {
        public int Day;         // 第几个游戏日
        public string DefId;
        public int Side;        // 0 = 买，1 = 卖
        public int Shares;
        public long Price;      // 每股成交价（分）
        public long Fee;        // 手续费（分）
        public long Pnl;        // 这一笔的已实现盈亏（分）；买入恒为 0

        /// <summary>成交金额（分，不含手续费）。</summary>
        public long Gross { get { return Price * Shares; } }
    }

    /// <summary>
    /// 成交流水（交易明细记录页的数据源）。
    ///
    /// 为什么单独存一份：StockState 只记「现在持仓多少、总共赚了多少」这类快照，
    /// 玩家想知道「我那天那笔卖得对不对」时没有任何东西可查 —— 卖出后成本被抹掉，
    /// 就再也算不出那一笔到底赚了多少。所以每笔成交都留一条记录，盈亏当场算好存下来。
    ///
    /// 记录只在 StockEngine.BuyAt / SellAt 里追加 —— 那两个方法是所有成交的唯一出口
    /// （面板下单、盘中挂单撮合、老K 的场外推销全都走它们），所以这里不会漏记也不会重记。
    ///
    /// 只留最近 MaxRecords 条，超了丢最老的：存档不能被流水撑大，
    /// 而玩家真正会翻的也就是最近这一段。
    /// </summary>
    public static class StockJournal
    {
        /// <summary>最多留几条。再多存档就明显变大了。</summary>
        public const int MaxRecords = 240;

        public static readonly List<TradeRec> All = new List<TradeRec>();

        public static void Reset()
        {
            All.Clear();
        }

        /// <summary>记一笔成交。pnl 只在卖出时有意义。</summary>
        public static void Add(string defId, int side, int shares, long price, long fee, long pnl)
        {
            if (shares <= 0) return;
            All.Add(new TradeRec
            {
                Day = StockState.Today,
                DefId = defId,
                Side = side == 0 ? 0 : 1,
                Shares = shares,
                Price = price,
                Fee = fee,
                Pnl = pnl
            });
            while (All.Count > MaxRecords) All.RemoveAt(0);
            StockState.Dirty = true;
        }

        /// <summary>累计手续费（分）。明细页顶上显示「一共交了多少过路费」。</summary>
        public static long TotalFee()
        {
            long sum = 0;
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i] != null) sum += All[i].Fee;
            }
            return sum;
        }

        /// <summary>累计买入 / 卖出笔数。</summary>
        public static void CountSides(out int buys, out int sells)
        {
            buys = 0;
            sells = 0;
            for (int i = 0; i < All.Count; i++)
            {
                TradeRec r = All[i];
                if (r == null) continue;
                if (r.Side == 0) buys++; else sells++;
            }
        }

        /// <summary>卖出里的赢家 / 输家笔数，明细页显示胜率用。</summary>
        public static void CountWins(out int wins, out int losses)
        {
            wins = 0;
            losses = 0;
            for (int i = 0; i < All.Count; i++)
            {
                TradeRec r = All[i];
                if (r == null || r.Side != 1) continue;
                if (r.Pnl > 0) wins++;
                else if (r.Pnl < 0) losses++;
            }
        }

        // ── 存档 ──────────────────────────────────────────────────────
        // 格式：day:defId:方向:股数:单价:手续费:盈亏;…（多笔用分号分隔）

        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < All.Count; i++)
            {
                TradeRec r = All[i];
                if (r == null) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(r.Day).Append(':').Append(r.DefId).Append(':').Append(r.Side).Append(':')
                  .Append(r.Shares).Append(':').Append(r.Price).Append(':').Append(r.Fee).Append(':')
                  .Append(r.Pnl);
            }
            return sb.ToString();
        }

        public static void Parse(string text)
        {
            Reset();
            if (string.IsNullOrEmpty(text)) return;

            string[] items = text.Split(';');
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].Length == 0) continue;
                string[] f = items[i].Split(':');
                if (f.Length < 7) continue;
                if (StockDefs.Get(f[1]) == null) continue;   // 已下架的标的直接丢

                int day, side, shares;
                long price, fee, pnl;
                if (!int.TryParse(f[0], out day)) continue;
                if (!int.TryParse(f[2], out side)) continue;
                if (!int.TryParse(f[3], out shares)) continue;
                if (!long.TryParse(f[4], out price)) continue;
                if (!long.TryParse(f[5], out fee)) continue;
                if (!long.TryParse(f[6], out pnl)) continue;
                if (shares <= 0 || price <= 0) continue;

                All.Add(new TradeRec
                {
                    Day = day,
                    DefId = f[1],
                    Side = side == 0 ? 0 : 1,
                    Shares = shares,
                    Price = price,
                    Fee = fee,
                    Pnl = pnl
                });
            }
            while (All.Count > MaxRecords) All.RemoveAt(0);
        }
    }
}
