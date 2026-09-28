using System;
using System.Collections.Generic;
using System.Text;

namespace StockMarket
{
    /// <summary>
    /// 老K 开出的一笔场外报价。
    /// Direction = 0：他想买走你手上的持仓（收购）；
    /// Direction = 1：他想把自己的货卖给你（推销）。
    /// </summary>
    public sealed class FriendOffer
    {
        public string DefId;
        public int Direction;
        public int Shares;
        public long Price;      // 每股单价（分）
        public int DaysLeft;

        public long Total { get { return Price * Shares; } }
    }

    /// <summary>
    /// AI 好友「老K」。
    ///
    /// 定位：给新手一个「有人来找我谈生意」的交互感，同时让持仓有个额外的出货/进货口。
    /// 出价围绕公允价浮动——大多数时候在 ±6% 内，10% 概率出现明显吃亏或明显占便宜的大单，
    /// 所以玩家既不会觉得他是送钱的，也不会觉得他是来坑人的，只会「想再等等看有没有更好的单」。
    ///
    /// 场外交易不收手续费（这是老K 的门路），玩家的盈亏完全来自出价本身。
    /// </summary>
    public static class StockFriend
    {
        public const string Name = "老K";

        /// <summary>距下一笔报价还有几天。归零且没有在挂的报价时才生成新的。</summary>
        private static int _cooldown = 2;

        public static bool HasOffer { get { return StockState.Offer != null; } }

        public static void Reset()
        {
            _cooldown = 2;
            StockState.Offer = null;
        }

        /// <summary>调试用：无视冷却，立刻生成一笔报价。</summary>
        public static bool ForceOffer()
        {
            _cooldown = 0;
            StockState.Offer = null;
            TryGenerate();
            return StockState.Offer != null;
        }

        // ── 每日推进 ──────────────────────────────────────────────────
        /// <summary>由 StockEngine.DailyTick 每天调一次：推进报价有效期与冷却。</summary>
        public static void Tick()
        {
            FriendOffer o = StockState.Offer;
            if (o != null)
            {
                o.DaysLeft--;
                if (o.DaysLeft <= 0)
                {
                    StockDef def = StockDefs.Get(o.DefId);
                    Core.Log.Msg("[" + Name + "] 报价过期：" + (def != null ? def.Name : o.DefId) + "（" + o.Shares + " 股）");
                    StockState.Offer = null;
                    _cooldown = StockEngine.RandRange(2, 4);
                    StockState.Dirty = true;
                }
                return; // 手上还挂着一单时不催新的
            }

            if (_cooldown > 0)
            {
                _cooldown--;
                return;
            }
            TryGenerate();
        }

        /// <summary>生成一笔新报价。条件不满足（没钱 / 没持仓）时只推后一两天再试。</summary>
        private static void TryGenerate()
        {
            int held = StockQuest.HeldCount();
            int dir = held > 0 && StockEngine.RandUnit() < 0.5 ? 0 : 1;

            FriendOffer offer = dir == 0 ? MakeBuyout() : MakePitch();
            if (offer == null)
            {
                _cooldown = StockEngine.RandRange(1, 2);
                return;
            }

            StockState.Offer = offer;
            StockState.Dirty = true;

            StockDef def = StockDefs.Get(offer.DefId);
            string verb = offer.Direction == 0 ? "收购你的" : "向你推销";
            Core.Log.Msg("[" + Name + "] " + verb + "「" + def.Name + "」" + offer.Shares + " 股，"
                + "单价 " + StockState.ToYuan(offer.Price).ToString("0.00") + " 元"
                + "（公允 " + StockEngine.FairValue(def).ToString("0.00") + " 元），剩 " + offer.DaysLeft + " 天");
        }

        /// <summary>老K 收购玩家手上的持仓。</summary>
        private static FriendOffer MakeBuyout()
        {
            List<StockDef> pool = new List<StockDef>();
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                if (StockState.GetPosition(StockDefs.All[i].Id) > 0) pool.Add(StockDefs.All[i]);
            }
            if (pool.Count == 0) return null;

            StockDef def = pool[StockEngine.RandRange(0, pool.Count - 1)];
            int heldShares = StockState.GetPosition(def.Id);
            int shares = StockEngine.RandRange(1, Math.Min(heldShares, 12));

            return new FriendOffer
            {
                DefId = def.Id,
                Direction = 0,
                Shares = shares,
                Price = QuotePrice(def),
                DaysLeft = StockEngine.RandRange(2, 3)
            };
        }

        /// <summary>老K 向玩家推销一支股票。黑市股要有开户证明才会拿出来。</summary>
        private static FriendOffer MakePitch()
        {
            List<StockDef> pool = new List<StockDef>();
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef def = StockDefs.All[i];
                if (def.NeedLicense && !StockState.License) continue;
                pool.Add(def);
            }
            if (pool.Count == 0) return null;

            StockDef picked = pool[StockEngine.RandRange(0, pool.Count - 1)];
            long price = QuotePrice(picked);

            // 买不起就少卖点；一股都买不起这单就不开了
            long affordable = StockState.Pool / Math.Max(1L, price);
            if (affordable < 1) return null;

            return new FriendOffer
            {
                DefId = picked.Id,
                Direction = 1,
                Shares = StockEngine.RandRange(1, (int)Math.Min(15L, affordable)),
                Price = price,
                DaysLeft = StockEngine.RandRange(2, 3)
            };
        }

        /// <summary>
        /// 报价单价（分）：以公允价 × 系数。
        /// 90% 的单落在 0.94~1.06，10% 的极端单落在 0.80~0.88 或 1.12~1.20——
        /// 极端单两边都有，所以既可能捡漏也可能被宰，这正是「想再等等」的动力来源。
        /// </summary>
        private static long QuotePrice(StockDef def)
        {
            double k;
            if (StockEngine.RandUnit() < 0.10)
            {
                k = StockEngine.RandUnit() < 0.5
                    ? 0.80 + StockEngine.RandUnit() * 0.08
                    : 1.12 + StockEngine.RandUnit() * 0.08;
            }
            else
            {
                k = 0.94 + StockEngine.RandUnit() * 0.12;
            }

            double fair = StockEngine.FairValue(def);
            long cents = (long)Math.Round(fair * k * StockDefs.PriceScale);
            return cents < 1 ? 1 : cents;
        }

        // ── 玩家操作 ──────────────────────────────────────────────────
        /// <summary>接受报价。返回错误文本，成功返回 null。</summary>
        public static string Accept()
        {
            FriendOffer o = StockState.Offer;
            if (o == null) return Name + " 现在没有报价";
            StockDef def = StockDefs.Get(o.DefId);
            if (def == null) { StockState.Offer = null; return "这支标的已下架"; }

            string err;
            if (o.Direction == 0)
            {
                if (!StockEngine.SellAt(def, o.Shares, o.Price, 0.0, out err)) return err;
                Core.Log.Msg("[结果] " + Name + " 收购成交：卖出「" + def.Name + "」" + o.Shares + " 股，"
                    + "进账 " + StockState.ToYuan(o.Total).ToString("N2") + " 元（免手续费）");
            }
            else
            {
                if (!StockEngine.BuyAt(def, o.Shares, o.Price, 0.0, out err)) return err;
                Core.Log.Msg("[结果] " + Name + " 推销成交：买入「" + def.Name + "」" + o.Shares + " 股，"
                    + "支出 " + StockState.ToYuan(o.Total).ToString("N2") + " 元（免手续费）");
            }

            StockState.Offer = null;
            _cooldown = StockEngine.RandRange(3, 5);
            StockState.Dirty = true;
            return null;
        }

        /// <summary>拒绝报价。</summary>
        public static string Reject()
        {
            FriendOffer o = StockState.Offer;
            if (o == null) return Name + " 现在没有报价";
            StockDef def = StockDefs.Get(o.DefId);
            Core.Log.Msg("[" + Name + "] 你拒绝了报价（" + (def != null ? def.Name : o.DefId) + "）");
            StockState.Offer = null;
            _cooldown = StockEngine.RandRange(2, 4);
            StockState.Dirty = true;
            return null;
        }

        /// <summary>
        /// 还价：按对玩家有利的方向调 10%。老K 有 45% 概率接受，接受后报价保留，
        /// 玩家再点一次「接受」才真正成交；拒绝则整单作废。
        /// </summary>
        public static bool Counter(out string message)
        {
            FriendOffer o = StockState.Offer;
            if (o == null)
            {
                message = Name + " 现在没有报价";
                return false;
            }

            StockDef def = StockDefs.Get(o.DefId);
            string name = def != null ? def.Name : o.DefId;

            if (StockEngine.RandUnit() >= 0.45)
            {
                Core.Log.Msg("[" + Name + "] 还价被拒（" + name + "），报价作废");
                StockState.Offer = null;
                _cooldown = StockEngine.RandRange(2, 4);
                StockState.Dirty = true;
                message = Name + " 摇了摇头：「这个价免谈。」报价作废。";
                return false;
            }

            // 方向 0（他收购）：玩家要求加价；方向 1（他推销）：玩家要求降价
            double k = o.Direction == 0 ? 1.10 : 0.90;
            o.Price = Math.Max(1L, (long)Math.Round(o.Price * k));
            StockState.Dirty = true;

            Core.Log.Msg("[" + Name + "] 还价成功（" + name + "），新单价 "
                + StockState.ToYuan(o.Price).ToString("0.00") + " 元");
            message = Name + " 想了想：「行，就按 " + StockState.ToYuan(o.Price).ToString("0.00")
                + " 元。」再点一次「接受」成交。";
            return true;
        }

        /// <summary>报价的一句话描述，给面板和底栏用。</summary>
        public static string Describe(FriendOffer o)
        {
            if (o == null) return Name + " 暂时没有动静。";
            StockDef def = StockDefs.Get(o.DefId);
            string name = def != null ? def.Name : o.DefId;
            string verb = o.Direction == 0 ? "想收购你的" : "想卖给你";
            return Name + " " + verb + "「" + name + "」" + o.Shares + " 股，"
                + "单价 " + StockState.ToYuan(o.Price).ToString("0.00") + " 元，"
                + "合计 " + StockState.ToYuan(o.Total).ToString("0.00") + " 元（剩 " + o.DaysLeft + " 天）";
        }

        // ── 存档 ──────────────────────────────────────────────────────
        /// <summary>格式：冷却天数|defId:方向:股数:单价:剩余天数</summary>
        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(_cooldown);
            FriendOffer o = StockState.Offer;
            if (o != null)
            {
                sb.Append('|').Append(o.DefId).Append(':').Append(o.Direction).Append(':')
                  .Append(o.Shares).Append(':').Append(o.Price).Append(':').Append(o.DaysLeft);
            }
            return sb.ToString();
        }

        public static void Parse(string text)
        {
            _cooldown = 2;
            StockState.Offer = null;
            if (string.IsNullOrEmpty(text)) return;

            string[] parts = text.Split('|');
            int cd;
            if (int.TryParse(parts[0], out cd)) _cooldown = cd;
            if (parts.Length < 2) return;

            string[] f = parts[1].Split(':');
            if (f.Length < 5 || StockDefs.Get(f[0]) == null) return;

            int dir, shares, days;
            long price;
            if (!int.TryParse(f[1], out dir) || !int.TryParse(f[2], out shares)) return;
            if (!long.TryParse(f[3], out price) || !int.TryParse(f[4], out days)) return;
            if (shares <= 0 || price <= 0) return;

            StockState.Offer = new FriendOffer
            {
                DefId = f[0],
                Direction = dir == 0 ? 0 : 1,
                Shares = shares,
                Price = price,
                DaysLeft = days
            };
        }
    }
}
