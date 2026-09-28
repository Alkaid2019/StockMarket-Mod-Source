using System;
using System.Collections.Generic;
using System.Text;

namespace StockMarket
{
    /// <summary>一笔盘中限价挂单。Side = 0 买入 / 1 卖出。</summary>
    public sealed class PendingOrder
    {
        public string DefId;
        public int Side;      // 0 = 买，1 = 卖
        public int Shares;
        public long Price;    // 每股限价（分）
        public long Frozen;   // 买入已冻结的资金（分，含手续费）；卖出恒为 0

        public long Gross { get { return Price * Shares; } }
    }

    /// <summary>
    /// 盘中挂单（限价单）。
    ///
    /// 为什么要有它：分时图如果只用来「看」，短线盯盘就没了落点。
    /// 挂单让玩家能说「跌到 9 块我买、涨到 12 块我卖」，然后去忙生意，
    /// 价格打到了自动成交 —— 这才是盯盘该有的手感。
    ///
    /// 规则：
    ///   · 只在营业日内有效，收工自动全部撤销；
    ///   · 下单即冻结：买单冻资金（含手续费），卖单冻股数；
    ///   · 成交价就是挂单价（限价单，不做滑点）；
    ///   · 冻结的股数只记在这里的 Frozen 表里，**不动 StockState.Positions**，
    ///     否则持仓市值、浮动盈亏、分红资格全都会被污染。
    /// </summary>
    public static class StockOrders
    {
        /// <summary>
        /// 同时最多挂几笔，免得挂单一多面板就糊了。
        /// 这个数必须和 StockUI 的 MaxOrderRows 一致 —— 挂单列表只铺得下 4 行，
        /// 上限放宽会出现「挂了却看不见」的单子。
        /// </summary>
        public const int MaxOrders = 4;

        public static readonly List<PendingOrder> Orders = new List<PendingOrder>();

        /// <summary>卖出被冻结的股数：id → 股数。只用于「可卖数量」校验。</summary>
        private static readonly Dictionary<string, int> Frozen = new Dictionary<string, int>();

        public static int Count { get { return Orders.Count; } }

        public static int FrozenShares(string defId)
        {
            int n;
            return Frozen.TryGetValue(defId, out n) ? n : 0;
        }

        /// <summary>已冻结的资金合计（分），给界面显示。</summary>
        public static long TotalFrozen()
        {
            long sum = 0;
            for (int i = 0; i < Orders.Count; i++)
            {
                if (Orders[i] != null) sum += Orders[i].Frozen;
            }
            return sum;
        }

        // ── 下单 ──────────────────────────────────────────────────────

        /// <summary>挂单。成功返回 null，失败返回给玩家看的原因。</summary>
        public static string Place(string defId, int side, int shares, long price)
        {
            if (shares <= 0) return "数量必须大于 0";
            if (price <= 0) return "挂单价必须大于 0";

            StockDef def = StockDefs.Get(defId);
            if (def == null) return "这支标的已下架";
            if (!StockIntraday.IsLive) return "现在不是营业时间，挂单只在营业日内有效";
            if (Orders.Count >= MaxOrders) return "最多同时挂 " + MaxOrders + " 笔，先撤掉几笔";
            if (def.NeedLicense && !StockState.License)
                return "黑市未开户，先去【黑市开户】办证明（" + StockUI.LicenseCost + " 元）";

            long gross = price * shares;
            long fee = (long)Math.Ceiling(gross * def.FeeRate);

            if (side == 0)
            {
                long frozen = gross + fee;
                if (StockState.Pool < frozen)
                {
                    return "账户资金不足（需冻结 " + StockState.ToYuan(frozen).ToString("N2") + " 元）";
                }
                StockState.Pool -= frozen;
                Orders.Add(new PendingOrder
                {
                    DefId = defId, Side = 0, Shares = shares, Price = price, Frozen = frozen
                });
                Core.Log.Msg("[挂单] 买入 " + def.Name + " " + shares + " 股 @ "
                    + StockState.ToYuan(price).ToString("0.00") + " 元，冻结 "
                    + StockState.ToYuan(frozen).ToString("N2") + " 元");
            }
            else
            {
                int avail = StockState.GetPosition(defId) - FrozenShares(defId);
                if (avail < shares)
                {
                    return "可用持仓不足（可卖 " + avail + " 股）";
                }
                Frozen[defId] = FrozenShares(defId) + shares;
                Orders.Add(new PendingOrder
                {
                    DefId = defId, Side = 1, Shares = shares, Price = price, Frozen = 0
                });
                Core.Log.Msg("[挂单] 卖出 " + def.Name + " " + shares + " 股 @ "
                    + StockState.ToYuan(price).ToString("0.00") + " 元，冻结 " + shares + " 股");
            }

            StockState.Dirty = true;
            return null;
        }

        // ── 撮合 ──────────────────────────────────────────────────────

        /// <summary>
        /// 每推进一段撮合一次：这一段的最高 / 最低价碰到挂单价就算成交。
        /// 由 StockIntraday.Poll 调用，没有挂单时是空转。
        /// </summary>
        public static void Match(int step)
        {
            if (Orders.Count == 0 || step <= 0) return;

            for (int i = Orders.Count - 1; i >= 0; i--)
            {
                PendingOrder o = Orders[i];
                if (o == null) { Orders.RemoveAt(i); continue; }

                StockDef def = StockDefs.Get(o.DefId);
                if (def == null) { Orders.RemoveAt(i); continue; }

                double a = StockIntraday.PriceAt(o.DefId, step - 1);
                double b = StockIntraday.PriceAt(o.DefId, step);
                double lo = Math.Min(a, b);
                double hi = Math.Max(a, b);
                double target = StockState.ToYuan(o.Price);

                // 买单要等价格跌到挂单价；卖单要等价格涨到挂单价
                bool hit = o.Side == 0 ? lo <= target : hi >= target;
                if (!hit) continue;

                string err;
                if (o.Side == 0)
                {
                    // 先把冻结的钱还回账户，再走标准成交路径 —— 这样成本、盈亏、手续费
                    // 全部复用 StockEngine.BuyAt，不另写一套记账。
                    StockState.Pool += o.Frozen;
                    if (!StockEngine.BuyAt(def, o.Shares, o.Price, def.FeeRate, out err))
                    {
                        StockState.Pool -= o.Frozen;   // 成交不了就把冻结按回去，下一段再试
                        continue;
                    }
                }
                else
                {
                    if (!StockEngine.SellAt(def, o.Shares, o.Price, def.FeeRate, out err))
                    {
                        // 持仓被别处卖掉了：撤掉这一笔，别让它一直挂着
                        ReleaseFrozenShares(o);
                        Orders.RemoveAt(i);
                        StockState.Dirty = true;
                        continue;
                    }
                    ReleaseFrozenShares(o);
                }

                Orders.RemoveAt(i);
                StockState.Dirty = true;
                Core.Log.Msg("[挂单成交] " + (o.Side == 0 ? "买入" : "卖出") + " " + def.Name + " "
                    + o.Shares + " 股 @ " + StockState.ToYuan(o.Price).ToString("0.00") + " 元");
            }
        }

        // ── 撤单 ──────────────────────────────────────────────────────

        /// <summary>撤单并解冻。成功返回 null，失败返回原因。</summary>
        public static string Cancel(int index)
        {
            if (index < 0 || index >= Orders.Count) return "这一笔挂单已经不在了";
            PendingOrder o = Orders[index];
            Orders.RemoveAt(index);
            Release(o);
            StockState.Dirty = true;

            StockDef def = StockDefs.Get(o.DefId);
            Core.Log.Msg("[挂单] 撤单 " + (def != null ? def.Name : o.DefId) + " "
                + (o.Side == 0 ? "买入" : "卖出") + " " + o.Shares + " 股");
            return null;
        }

        /// <summary>撤掉全部挂单并解冻。返回撤销笔数。收工与调试面板用。</summary>
        public static int CancelAll(string reason)
        {
            if (Orders.Count == 0) return 0;
            int n = Orders.Count;
            for (int i = 0; i < Orders.Count; i++) Release(Orders[i]);
            Orders.Clear();
            Frozen.Clear();
            StockState.Dirty = true;
            Core.Log.Msg("[挂单] 撤销全部挂单 " + n + " 笔（" + reason + "）");
            return n;
        }

        private static void Release(PendingOrder o)
        {
            if (o == null) return;
            if (o.Side == 0)
            {
                if (o.Frozen > 0) StockState.Pool += o.Frozen;   // 买入：把钱退回去
            }
            else
            {
                ReleaseFrozenShares(o);                          // 卖出：把股数解冻
            }
        }

        private static void ReleaseFrozenShares(PendingOrder o)
        {
            if (o == null) return;
            int left = FrozenShares(o.DefId) - o.Shares;
            if (left > 0) Frozen[o.DefId] = left;
            else Frozen.Remove(o.DefId);
        }

        // ── 存档 ──────────────────────────────────────────────────────

        public static void Reset()
        {
            Orders.Clear();
            Frozen.Clear();
        }

        /// <summary>格式：defId:方向:股数:单价:冻结金额;…（多笔用分号分隔）</summary>
        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Orders.Count; i++)
            {
                PendingOrder o = Orders[i];
                if (o == null) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(o.DefId).Append(':').Append(o.Side).Append(':')
                  .Append(o.Shares).Append(':').Append(o.Price).Append(':').Append(o.Frozen);
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
                if (f.Length < 5) continue;
                if (StockDefs.Get(f[0]) == null) continue;      // 已下架的标的直接丢

                int side, shares;
                long price, frozen;
                if (!int.TryParse(f[1], out side)) continue;
                if (!int.TryParse(f[2], out shares)) continue;
                if (!long.TryParse(f[3], out price)) continue;
                if (!long.TryParse(f[4], out frozen)) continue;
                if (shares <= 0 || price <= 0) continue;

                PendingOrder o = new PendingOrder
                {
                    DefId = f[0],
                    Side = side == 0 ? 0 : 1,
                    Shares = shares,
                    Price = price,
                    Frozen = frozen
                };
                Orders.Add(o);
                if (o.Side == 1) Frozen[o.DefId] = FrozenShares(o.DefId) + o.Shares;

                if (Orders.Count >= MaxOrders) break;
            }
        }
    }
}