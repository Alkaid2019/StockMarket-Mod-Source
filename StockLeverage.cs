using System;
using System.Collections.Generic;
using System.Text;

namespace StockMarket
{
    /// <summary>
    /// 一个杠杆账户。融资融券和场外配资共用这套结构，差别全在参数上：
    /// 正规的杠杆低、利息便宜、爆仓线松；野路子的杠杆高、利息贵、跌一点就平。
    /// </summary>
    internal sealed class LeverageAccount
    {
        public string Name = "";        // 「融资融券」/「场外配资」
        public bool Open;               // 是否已开户
        public int Leverage;            // 当前选择的杠杆倍数
        public int MaxLeverage;         // 可选的最大倍数
        public double DailyRate;        // 日利率
        public double OpenFee;          // 开户费（元）
        public double MinAssets;        // 开户门槛：总资产（元）
        public double KeepRatio;        // 自有资金亏到只剩这个比例就强平

        public long Debt;               // 借入未还的现金（分）
        public long PaidInterest;       // 累计已付利息（分）
        public readonly Dictionary<string, int> Short = new Dictionary<string, int>();

        /// <summary>强平线（担保比例）。每次借款时按当时的杠杆重新算，高杠杆自然就贴着成本线。</summary>
        public double CallRatio = 1.05;
        /// <summary>预警线。到这条线只是提示，还没动手。</summary>
        public double WarnRatio = 1.15;

        public bool Liquidated;         // 上一轮结算是否发生了强平
        public string LastNotice = "";  // 强平时给玩家看的一句话

        public bool Idle { get { return Debt <= 0 && Short.Count == 0; } }
    }

    /// <summary>
    /// 杠杆交易。核心是一个「担保比例」：
    ///     担保比例 = (股票账户余额 + 持仓市值) / (欠款 + 融券欠的股市值)
    /// 借钱买股票后，股价跌会让分子变小、分母不变，比例就往下掉；掉到强平线就自动平仓。
    /// 杠杆越高，同样的跌幅让比例掉得越快——这就是「10 倍杠杆跌 10% 就爆仓」的由来，
    /// 不需要额外写规则，比例本身就体现了。
    /// </summary>
    internal static class StockLeverage
    {
        // ── 融资融券（正规）───────────────────────────────────────────
        // 门槛高、杠杆低（2 倍）、利息便宜、爆仓线松，适合拿一段时间的稳健加仓。
        public const double RegOpenFee = 50.0;
        public const double RegMinAssets = 300.0;
        public const int RegMaxLeverage = 2;
        public const double RegDailyRate = 0.0005;   // 日息 0.05%，年化约 20%
        public const double RegKeepRatio = 0.35;     // 自有资金亏掉 65% 才强平

        // ── 场外配资（野路子）────────────────────────────────────────
        // 门槛低、杠杆 3/5/8 倍、利息是正规的 6 倍、自有资金亏掉 80% 就平。
        // 高杠杆下「亏 80% 自有资金」换算成股价只要跌 10% 左右，所以是「跌一点就爆」。
        public const double ShadowOpenFee = 20.0;
        public const double ShadowMinAssets = 100.0;
        public const double ShadowDailyRateValue = 0.0030; // 日息 0.30%，年化约 109%
        public const double ShadowKeepRatio = 0.20;
        public static readonly int[] ShadowLevels = { 3, 5, 8 };

        public static readonly LeverageAccount Reg = new LeverageAccount
        {
            Name = "融资融券",
            MaxLeverage = RegMaxLeverage,
            Leverage = RegMaxLeverage,
            DailyRate = RegDailyRate,
            OpenFee = RegOpenFee,
            MinAssets = RegMinAssets,
            KeepRatio = RegKeepRatio
        };

        public static readonly LeverageAccount Shadow = new LeverageAccount
        {
            Name = "场外配资",
            MaxLeverage = 8,
            Leverage = 5,
            DailyRate = ShadowDailyRateValue,
            OpenFee = ShadowOpenFee,
            MinAssets = ShadowMinAssets,
            KeepRatio = ShadowKeepRatio
        };

        // ── 计算 ──────────────────────────────────────────────────────

        /// <summary>融券欠的股票市值（分）：借来的股数 × 现价。</summary>
        public static long ShortLiability(LeverageAccount a)
        {
            long sum = 0;
            foreach (KeyValuePair<string, int> kv in a.Short)
            {
                // 走盘中价：担保比例是玩家盯着看的数，用收盘价算等于把今天的答案先递过去。
                // 强平在收工时跑，那时 LivePrice 已经等于收盘价（见 StockEngine.DailyTickNews 首行）。
                if (kv.Value > 0) sum += StockEngine.LivePrice(kv.Key) * kv.Value;
            }
            return sum;
        }

        /// <summary>负债合计（分）：欠款 + 融券市值。</summary>
        public static long Liability(LeverageAccount a)
        {
            return a.Debt + ShortLiability(a);
        }

        /// <summary>
        /// 担保比例。没有负债时返回一个很大的数（表示「无风险」），
        /// 免得界面上显示成 0% 让人以为要爆仓了。
        /// </summary>
        public static double Ratio(LeverageAccount a)
        {
            long liab = Liability(a);
            if (liab <= 0) return 99.0;
            long assets = StockState.Pool + StockEngine.LiveTotalStockValue();
            return assets / (double)liab;
        }

        /// <summary>还能借多少（分）。按「借完之后担保比例不低于强平线的 1.25 倍」倒推。</summary>
        public static long MaxBorrow(LeverageAccount a)
        {
            if (!a.Open) return 0;
            // 自有资金 = 现有资产 − 现有负债
            long own = StockState.Pool + StockEngine.LiveTotalStockValue() - Liability(a);
            if (own <= 0) return 0;
            // 总资产最多做到自有资金的 Leverage 倍，所以最多还能借 (Leverage−1)×自有 − 已借
            long cap = (long)(own * (a.Leverage - 1));
            long room = cap - a.Debt;
            return room > 0 ? room : 0;
        }

        /// <summary>借满之后杠杆是多少倍，给界面显示用。</summary>
        public static double CurrentLeverage(LeverageAccount a)
        {
            long own = StockState.Pool + StockEngine.LiveTotalStockValue() - Liability(a);
            if (own <= 0) return 0.0;
            return (StockState.Pool + StockEngine.LiveTotalStockValue()) / (double)own;
        }

        /// <summary>
        /// 借款后重算预警线和强平线。用「自有资金亏掉多少」来定，
        /// 而不是写死一个百分比——写死的话 8 倍杠杆一开局就已经在强平线下面了。
        /// </summary>
        private static void RefreshLines(LeverageAccount a)
        {
            double r0 = Ratio(a);
            if (r0 >= 99.0) { a.CallRatio = 1.05; a.WarnRatio = 1.15; return; }
            double span = r0 - 1.0;
            if (span < 0.01) span = 0.01;
            a.CallRatio = 1.0 + span * a.KeepRatio;
            a.WarnRatio = 1.0 + span * (a.KeepRatio + 0.18);
        }

        // ── 开户 ──────────────────────────────────────────────────────

        public static string Open(LeverageAccount a, int leverage, out string error)
        {
            error = null;
            if (a.Open) { error = a.Name + "已经开过户了"; return null; }

            long assets = StockEngine.TotalAssetYuan();
            if (assets < a.MinAssets)
            {
                error = a.Name + "开户要求总资产不低于 " + a.MinAssets.ToString("0") + " 元，你现在 " + assets + " 元";
                return null;
            }
            // 开户费是从「股票账户余额」里扣的。只看总资产是不够的——
            // 钱全买了股票时总资产够、余额却是 0，扣完余额会变成负数。
            long fee = StockState.ToCents(a.OpenFee);
            if (StockState.Pool < fee)
            {
                error = "开户费 " + a.OpenFee.ToString("0") + " 元要从股票账户余额里扣，先去【资金划转】转入";
                return null;
            }
            if (leverage > 0 && leverage <= a.MaxLeverage) a.Leverage = leverage;
            a.Open = true;
            StockState.Pool -= fee;
            StockState.Dirty = true;

            string msg = "已开通【" + a.Name + "】，开户费 " + a.OpenFee.ToString("0")
                + " 元，杠杆 " + a.Leverage + " 倍，日息 "
                + (a.DailyRate * 100.0).ToString("0.00") + "%";
            Core.Log.Msg("[杠杆] " + msg);
            return msg;
        }

        /// <summary>调整场外配资的杠杆档位。只能在无负债时改，否则风险敞口会莫名其妙变大。</summary>
        public static string SetLeverage(LeverageAccount a, int leverage, out string error)
        {
            error = null;
            if (!a.Open) { error = a.Name + "还没开户"; return null; }
            if (!a.Idle) { error = "还有借款或融券没还清，先还清再改杠杆倍数"; return null; }
            a.Leverage = leverage;
            StockState.Dirty = true;
            return "杠杆倍数已设为 " + leverage + " 倍";
        }

        // ── 融资：借钱进股票账户 ──────────────────────────────────────

        public static string Borrow(LeverageAccount a, double yuan, out string error)
        {
            error = null;
            if (!a.Open) { error = a.Name + "还没开户，先去【杠杆交易】页开户"; return null; }
            if (yuan < 1.0) { error = "借款金额至少 1 元"; return null; }

            long want = StockState.ToCents(yuan);
            long room = MaxBorrow(a);
            if (room <= 0) { error = "按当前杠杆已经借满了，想多借先追加自有资金"; return null; }
            if (want > room)
            {
                error = "超出杠杆上限，最多还能借 " + StockState.ToYuan(room).ToString("0") + " 元";
                return null;
            }

            a.Debt += want;
            StockState.Pool += want;
            StockState.Dirty = true;
            RefreshLines(a);
            Core.Log.Msg("[杠杆] " + a.Name + " 融资 " + yuan.ToString("0") + " 元，欠款合计 "
                + StockState.ToYuan(a.Debt).ToString("0") + " 元");
            return "融资成功，借到 " + yuan.ToString("0") + " 元已进股票账户，记得这钱是要还的。";
        }

        /// <summary>还款。从股票账户扣钱，直接冲减欠款。</summary>
        public static string Repay(LeverageAccount a, double yuan, out string error)
        {
            error = null;
            if (!a.Open) { error = a.Name + "还没开户"; return null; }
            if (a.Debt <= 0) { error = "现在没有欠款要还"; return null; }
            if (yuan < 1.0) { error = "还款金额至少 1 元"; return null; }

            long pay = StockState.ToCents(yuan);
            // 先夹到欠款上限再查余额：想一次还清时输入的金额通常大于欠款，
            // 原顺序会误报「余额不足」，其实按欠款额扣是够的。
            if (pay > a.Debt) pay = a.Debt;
            if (StockState.Pool < pay)
            {
                error = "股票账户余额不足（只有 "
                    + StockState.ToYuan(StockState.Pool).ToString("0") + " 元）";
                return null;
            }

            StockState.Pool -= pay;
            a.Debt -= pay;
            StockState.Dirty = true;
            if (a.Idle) RefreshLines(a);
            Core.Log.Msg("[杠杆] " + a.Name + " 还款 " + StockState.ToYuan(pay).ToString("0")
                + " 元，剩余欠款 " + StockState.ToYuan(a.Debt).ToString("0") + " 元");
            return "已还款 " + StockState.ToYuan(pay).ToString("0") + " 元，剩余欠款 "
                + StockState.ToYuan(a.Debt).ToString("0") + " 元。";
        }

        // ── 融券：借股票卖出，等跌了买回来还 ──────────────────────────

        public static string ShortSell(LeverageAccount a, StockDef def, int shares, out string error)
        {
            error = null;
            if (!a.Open) { error = a.Name + "还没开户"; return null; }
            if (shares <= 0) { error = "数量必须大于 0"; return null; }
            if (def == null) { error = "标的不存在"; return null; }
            if (def.NeedLicense && !StockState.License)
            {
                error = "黑市未开户，先去【黑市开户】办证明（" + StockUI.LicenseCost + " 元）";
                return null;
            }

            long price = StockEngine.LivePrice(def.Id);
            long gross = price * shares;
            if (StockState.ToYuan(gross) < StockDefs.MinTradeValue)
            {
                error = "单笔交易额不得低于 " + StockDefs.MinTradeValue + " 元";
                return null;
            }
            // 融券会把负债做大，先确认做完还在强平线之上，否则一开仓就被平
            long liabAfter = Liability(a) + gross;
            long assets = StockState.Pool + StockEngine.LiveTotalStockValue();
            if (assets / (double)liabAfter <= a.CallRatio * 1.02)
            {
                error = "按当前担保比例，这个量做空会立刻触及强平线，先少做一点或追加资金";
                return null;
            }

            a.Short[def.Id] = GetShort(a, def.Id) + shares;
            StockState.Pool += gross;      // 借来的股卖掉，钱进账户（但这钱不是你的）
            StockState.Dirty = true;
            RefreshLines(a);
            Core.Log.Msg("[杠杆] " + a.Name + " 融券卖出 " + def.Name + " " + shares + " 股，得款 "
                + StockState.ToYuan(gross).ToString("0") + " 元");
            return "融券卖出 " + def.Name + " " + shares + " 股，得款 "
                + StockState.ToYuan(gross).ToString("0") + " 元。跌了买回来还券就赚差价，涨了就得亏。";
        }

        public static string BuyToCover(LeverageAccount a, StockDef def, int shares, out string error)
        {
            error = null;
            if (def == null) { error = "标的不存在"; return null; }
            int have = GetShort(a, def.Id);
            if (have <= 0) { error = "没有【" + def.Name + "】的融券要还"; return null; }
            if (shares <= 0) { error = "数量必须大于 0"; return null; }
            if (shares > have) shares = have;

            long price = StockEngine.LivePrice(def.Id);
            long gross = price * shares;
            if (StockState.Pool < gross)
            {
                error = "股票账户余额不足（买回需 "
                    + StockState.ToYuan(gross).ToString("0") + " 元）";
                return null;
            }

            StockState.Pool -= gross;
            int left = have - shares;
            if (left > 0) a.Short[def.Id] = left; else a.Short.Remove(def.Id);
            StockState.Dirty = true;
            if (a.Idle) RefreshLines(a);
            Core.Log.Msg("[杠杆] " + a.Name + " 买券还券 " + def.Name + " " + shares
                + " 股，花费 " + StockState.ToYuan(gross).ToString("0") + " 元");
            return "已买回 " + def.Name + " " + shares + " 股还券，花费 "
                + StockState.ToYuan(gross).ToString("0") + " 元。";
        }

        public static int GetShort(LeverageAccount a, string id)
        {
            int n;
            return a.Short.TryGetValue(id, out n) ? n : 0;
        }

        // ── 每日结算 ──────────────────────────────────────────────────

        /// <summary>
        /// 每天结算一次：先计息，再看担保比例有没有跌破强平线。
        /// 返回需要提示给玩家的文字（可能为空）。
        /// </summary>
        public static string DailySettle()
        {
            StringBuilder sb = new StringBuilder();
            SettleOne(Reg, sb);
            SettleOne(Shadow, sb);
            return sb.ToString().Trim();
        }

        private static void SettleOne(LeverageAccount a, StringBuilder sb)
        {
            a.Liquidated = false;
            if (!a.Open) return;

            long liab = Liability(a);
            if (liab <= 0) { a.LastNotice = ""; return; }

            // 利息按「欠款 + 融券市值」计，融券不能白借
            long interest = (long)Math.Ceiling(liab * a.DailyRate);
            if (interest > 0)
            {
                a.Debt += interest;
                a.PaidInterest += interest;
                StockState.Dirty = true;
            }

            double ratio = Ratio(a);
            if (ratio < a.CallRatio)
            {
                ForceClose(a);
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(a.LastNotice);
            }
            else if (ratio < a.WarnRatio)
            {
                a.LastNotice = "【" + a.Name + "】担保比例 " + (ratio * 100.0).ToString("0")
                    + "%，已经贴近预警线 " + (a.WarnRatio * 100.0).ToString("0")
                    + "%，再跌就要被强制平仓了。";
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(a.LastNotice);
            }
            else
            {
                a.LastNotice = "";
            }
        }

        /// <summary>
        /// 强制平仓，按真实顺序走三步：
        /// ① 卖持仓 + 立刻拿钱抵债，卖到担保比例回到强平线之上为止；
        /// ② 用账户里的钱买券还券，能还多少还多少；
        /// ③ 还有欠款就直接从股票账户划走抵债。
        /// 没钱还的那部分融券会原样留着继续算负债——强平不等于免除债务。
        /// </summary>
        private static void ForceClose(LeverageAccount a)
        {
            StringBuilder log = new StringBuilder();
            long recovered = 0;
            int guard;

            // 先把手头的现金直接抵债
            recovered += RepayWithPool(a);

            // ① 卖现货。每卖一支立刻把钱拿去抵债 —— 只卖不还的话担保比例
            // 一点都不会变（卖出只是把股票换成等额现金，分子分母都不动），
            // 原来的写法就是一路把仓位全卖光、比例却纹丝不动，最后靠 guard 才停下。
            guard = 0;
            while (Ratio(a) < a.CallRatio && a.Debt > 0 && guard++ < 200)
            {
                string worst = null;
                long best = 0;
                foreach (KeyValuePair<string, int> kv in StockState.Positions)
                {
                    if (kv.Value <= 0) continue;
                    long v = StockState.GetPrice(kv.Key) * kv.Value;
                    if (v > best) { best = v; worst = kv.Key; }
                }
                if (worst == null) break;

                StockDef def = StockDefs.Get(worst);
                int held = StockState.GetPosition(worst);
                if (def == null || held <= 0) break;
                string err;
                if (!StockEngine.SellAt(def, held, StockState.GetPrice(worst), 0.0, out err))
                {
                    Core.Log.Warning("[杠杆] 强平卖出失败：" + err);
                    break;
                }
                recovered += RepayWithPool(a);
                if (log.Length > 0) log.Append('、');
                log.Append(def.Name);
            }

            // ② 还融券
            guard = 0;
            while (a.Short.Count > 0 && guard++ < 200)
            {
                string coverId = null;
                long best = 0;
                foreach (KeyValuePair<string, int> kv in a.Short)
                {
                    if (kv.Value <= 0) continue;
                    long v = StockState.GetPrice(kv.Key) * kv.Value;
                    if (v > best) { best = v; coverId = kv.Key; }
                }
                if (coverId == null) break;

                long price = StockState.GetPrice(coverId);
                int have = GetShort(a, coverId);
                int can = price > 0 ? (int)Math.Min(have, StockState.Pool / price) : 0;
                if (can <= 0) break;   // 账户没钱了，剩下的融券留着继续算负债

                StockState.Pool -= price * can;
                if (have - can > 0) a.Short[coverId] = have - can; else a.Short.Remove(coverId);
                recovered += price * can;
            }

            // ③ 余额抵债（① 之后可能还剩一点）
            recovered += RepayWithPool(a);

            a.Liquidated = true;
            StockState.Dirty = true;
            a.LastNotice = "【" + a.Name + "】担保比例跌破强平线，已被强制平仓"
                + (log.Length > 0 ? "（卖掉：" + log + "）" : "")
                + "，回收约 " + StockState.ToYuan(recovered).ToString("0") + " 元，"
                + "剩余欠款 " + StockState.ToYuan(a.Debt).ToString("0") + " 元。";
            Core.Log.Msg("[杠杆] " + a.LastNotice);
        }

        /// <summary>把股票账户余额拿去冲抵欠款，返回实际抵掉的金额（分）。</summary>
        private static long RepayWithPool(LeverageAccount a)
        {
            if (a.Debt <= 0 || StockState.Pool <= 0) return 0;
            long take = Math.Min(StockState.Pool, a.Debt);
            StockState.Pool -= take;
            a.Debt -= take;
            return take;
        }

        // ── 存档 ──────────────────────────────────────────────────────

        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            DumpOne(Reg, "R", sb);
            if (sb.Length > 0) sb.Append('|');
            DumpOne(Shadow, "S", sb);
            return sb.ToString();
        }

        private static void DumpOne(LeverageAccount a, string tag, StringBuilder sb)
        {
            sb.Append(tag).Append(':')
              .Append(a.Open ? "1" : "0").Append(':')
              .Append(a.Leverage).Append(':')
              .Append(a.Debt).Append(':')
              .Append(a.PaidInterest).Append(':')
              .Append(a.CallRatio.ToString("0.0000")).Append(':')
              .Append(a.WarnRatio.ToString("0.0000")).Append(':');
            foreach (KeyValuePair<string, int> kv in a.Short)
            {
                if (kv.Value <= 0) continue;
                sb.Append(';').Append(kv.Key).Append('=').Append(kv.Value);
            }
        }

        public static void Parse(string text)
        {
            Reset();
            if (string.IsNullOrEmpty(text)) return;
            string[] parts = text.Split('|');
            for (int i = 0; i < parts.Length; i++) ParseOne(parts[i]);
        }

        private static void ParseOne(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int colon = text.IndexOf(':');
            if (colon <= 0) return;
            string tag = text.Substring(0, colon);
            LeverageAccount a = tag == "R" ? Reg : (tag == "S" ? Shadow : null);
            if (a == null) return;

            string[] f = text.Substring(colon + 1).Split(':');
            if (f.Length < 2) return;
            a.Open = f[0] == "1";
            a.Leverage = ParseInt(f[1], a.Leverage);
            if (f.Length > 2) a.Debt = ParseLong(f[2], 0);
            if (f.Length > 3) a.PaidInterest = ParseLong(f[3], 0);
            if (f.Length > 4) a.CallRatio = ParseDouble(f[4], 1.05);
            if (f.Length > 5) a.WarnRatio = ParseDouble(f[5], 1.15);

            // 融券明细挂在最后一段，用 ; 分隔
            if (f.Length > 6 && f[6].Length > 0)
            {
                string[] items = f[6].Split(';');
                for (int i = 0; i < items.Length; i++)
                {
                    int eq = items[i].IndexOf('=');
                    if (eq <= 0) continue;
                    string id = items[i].Substring(0, eq);
                    int n = ParseInt(items[i].Substring(eq + 1), 0);
                    if (n > 0 && StockDefs.Get(id) != null) a.Short[id] = n;
                }
            }
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }

        private static long ParseLong(string s, long fallback)
        {
            long v;
            return long.TryParse(s, out v) ? v : fallback;
        }

        private static double ParseDouble(string s, double fallback)
        {
            double v;
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        /// <summary>清空两个账户，回到未开户状态。</summary>
        public static void Reset()
        {
            ResetOne(Reg, RegMaxLeverage, RegDailyRate, RegOpenFee, RegMinAssets, RegKeepRatio);
            ResetOne(Shadow, 5, ShadowDailyRateValue, ShadowOpenFee, ShadowMinAssets, ShadowKeepRatio);
        }

        private static void ResetOne(LeverageAccount a, int leverage, double rate,
            double fee, double minAssets, double keep)
        {
            a.Open = false;
            a.Leverage = leverage;
            a.DailyRate = rate;
            a.OpenFee = fee;
            a.MinAssets = minAssets;
            a.KeepRatio = keep;
            a.Debt = 0;
            a.PaidInterest = 0;
            a.CallRatio = 1.05;
            a.WarnRatio = 1.15;
            a.Liquidated = false;
            a.LastNotice = "";
            a.Short.Clear();
        }
    }
}
