using System;
using System.Collections.Generic;
using System.Text;

namespace StockMarket
{
    /// <summary>
    /// 股评：博主 + 评论区。
    ///
    /// 三档博主，区别只在「说真话的概率」：
    ///   · 精选股评博主——背后有研报，方向多半跟得住趋势；
    ///   · 普通股评博主——对错参半，靠手感；
    ///   · 假股评博主——被人喂了钱，专门反着说，负责把散户往沟里带。
    ///
    /// 「真话」的定义不是「未来涨跌」，而是「跟当前趋势档同向」——趋势档正是后面几天
    /// 价格漂移的主要来源（见 StockEngine.NextPrice）。所以精选博主的历史胜率会真的
    /// 明显高于假博主，玩家翻几页统计就能看出来，不用我们明说谁是骗子。
    ///
    /// 全部由 (博主Id + 标的Id + 天数) 的确定性函数算出，不落存档：
    /// 历史胜率是拿 PriceHistory 里已发生的收盘价事后核对出来的，读档重算一致。
    /// </summary>
    public static class StockReview
    {
        public enum BloggerKind
        {
            Fake = 0,      // 假股评博主：收钱唱反调
            Normal = 1,    // 普通股评博主
            Selected = 2   // 精选股评博主：研报派，方向更准
        }

        public sealed class Blogger
        {
            public string Id;
            public string Name;
            public BloggerKind Kind;
            public int Followers;      // 粉丝数，纯氛围
            public double TruthRate;   // 说真话的概率
        }

        public sealed class Post
        {
            public string BloggerId;
            public string StockId;
            public int Day;
            public int Dir;            // +1 看涨 / -1 看跌
            public string Headline;    // 正文（看涨/看跌 + 标的 + 理由）
            public bool Settled;       // 事后有没有被验证（历史不够长就是 false）
            public bool Hit;           // 验证过了：方向对不对
            public List<string> Comments = new List<string>();
            public int Shills;         // 评论里水军条数（调试页与调试用，不给玩家看）
        }

        /// <summary>预测的验证窗口（天）。三天之内方向对不对。</summary>
        public const int Horizon = 3;

        /// <summary>统计历史胜率回看多少天。</summary>
        private const int LookbackDays = 24;

        /// <summary>每天发帖的概率（%）。</summary>
        private const int PostChance = 35;

        // ── 博主表 ────────────────────────────────────────────────────
        private static readonly Blogger[] All =
        {
            // 假：说得最响、最像内幕，方向最不靠谱
            new Blogger { Id = "B1", Name = "涨停敢死队队长", Kind = BloggerKind.Fake,     Followers = 128400, TruthRate = 0.22 },
            new Blogger { Id = "B2", Name = "内幕小道消息王", Kind = BloggerKind.Fake,     Followers =  86200, TruthRate = 0.20 },
            // 普通：中规中矩，对错一半一半
            new Blogger { Id = "B3", Name = "老韭菜说市",     Kind = BloggerKind.Normal,   Followers =  41200, TruthRate = 0.55 },
            new Blogger { Id = "B4", Name = "K线小课堂",     Kind = BloggerKind.Normal,   Followers =  33500, TruthRate = 0.58 },
            new Blogger { Id = "B5", Name = "晚盘不睡",       Kind = BloggerKind.Normal,   Followers =  22700, TruthRate = 0.52 },
            // 精选：起名就克制，内容也克制，但方向真的更准
            new Blogger { Id = "B6", Name = "星际研报·老陈", Kind = BloggerKind.Selected, Followers =  58900, TruthRate = 0.82 },
            new Blogger { Id = "B7", Name = "数据派阿May",   Kind = BloggerKind.Selected, Followers =  47600, TruthRate = 0.78 },
        };

        public static Blogger[] Bloggers { get { return All; } }

        public static Blogger Find(string id)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Id == id) return All[i];
            }
            return null;
        }

        /// <summary>博主类型的显示名与配色：精选金 / 普通青 / 假灰。</summary>
        public static string KindText(BloggerKind k)
        {
            switch (k)
            {
                case BloggerKind.Selected: return "精选";
                case BloggerKind.Fake: return "野生";
                default: return "普通";
            }
        }

        public static string KindColor(BloggerKind k)
        {
            switch (k)
            {
                case BloggerKind.Selected: return "FFC24A";
                case BloggerKind.Fake: return "8A97A8";
                default: return "7FD8FF";
            }
        }

        // ── 缓存（同一天只算一次）──────────────────────────────────────
        private static int _cacheDay = -1;
        private static readonly List<Post> _today = new List<Post>();
        private static readonly Dictionary<string, int> _hits = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _settled = new Dictionary<string, int>();

        private static void EnsureCache()
        {
            if (_cacheDay == StockState.Today) return;
            _cacheDay = StockState.Today;
            _today.Clear();
            _hits.Clear();
            _settled.Clear();

            for (int b = 0; b < All.Length; b++) EnsureStats(All[b]);

            for (int b = 0; b < All.Length; b++)
            {
                Blogger blogger = All[b];
                uint seed = StockState.Hash32(blogger.Id + "|feed|" + StockState.Today);
                if (StockState.NextUnit(ref seed) * 100.0 >= PostChance) continue;

                string stockId = PickStock(blogger, ref seed);
                if (stockId.Length == 0) continue;

                Post p = MakePost(blogger, stockId, StockState.Today);
                BuildComments(p, blogger, ref seed);
                _today.Add(p);
            }
        }

        /// <summary>换档 / 重建时清缓存。</summary>
        public static void Reset()
        {
            _cacheDay = -1;
            _today.Clear();
            _hits.Clear();
            _settled.Clear();
        }

        /// <summary>今天的股评（可能是空的）。</summary>
        public static List<Post> Today()
        {
            EnsureCache();
            return _today;
        }

        /// <summary>某位博主的条数统计（hits / settled），settled = 0 表示还没得可验证。</summary>
        public static void Stats(string bloggerId, out int hits, out int settled)
        {
            EnsureCache();
            hits = _hits.TryGetValue(bloggerId, out int h) ? h : 0;
            settled = _settled.TryGetValue(bloggerId, out int s) ? s : 0;
        }

        /// <summary>历史胜率文案。「还没有可验证的记录」时返回那句白话。</summary>
        public static string WinRateText(string bloggerId)
        {
            int hits, settled;
            Stats(bloggerId, out hits, out settled);
            if (settled <= 0) return "暂无验证记录";
            double rate = (double)hits / settled;
            return (rate * 100.0).ToString("0") + "%（近 " + settled + " 次）";
        }

        /// <summary>列表里的一句话摘要，省得太长的正文把行撑爆。</summary>
        public static string ShortNote(BloggerKind k, int dir)
        {
            bool up = dir > 0;
            switch (k)
            {
                case BloggerKind.Fake:
                    return up ? "说有内幕，闭眼买" : "说主力在出货";
                case BloggerKind.Selected:
                    return up ? "资金净买，回调分批" : "库存走高，逢高减持";
                default:
                    return up ? "站上均线，短线偏多" : "跌破支撑，先减仓";
            }
        }

        // ── 生成一条股评 ──────────────────────────────────────────────

        private static string PickStock(Blogger b, ref uint seed)
        {
            if (StockDefs.All.Length == 0) return string.Empty;
            // 精选/普通博主更爱聊「有方向」的票（趋势档不是横盘），
            // 假博主满嘴跑火车，随机点一个就开讲。
            bool preferTrend = b.Kind != BloggerKind.Fake && StockState.NextUnit(ref seed) < 0.70;
            List<string> pool = new List<string>();
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef def = StockDefs.All[i];
                if (preferTrend && StockEngine.TrendOf(def.Id) == 2) continue;
                pool.Add(def.Id);
            }
            if (pool.Count == 0) pool.Add(StockDefs.All[0].Id);
            int idx = (int)(StockState.NextUnit(ref seed) * pool.Count);
            if (idx < 0) idx = 0;
            if (idx >= pool.Count) idx = pool.Count - 1;
            return pool[idx];
        }

        private static Post MakePost(Blogger b, string stockId, int day)
        {
            uint seed = StockState.Hash32(b.Id + "|say|" + stockId + "|" + day);
            bool honest = StockState.NextUnit(ref seed) < b.TruthRate;

            int trend = StockEngine.TrendOf(stockId, day);
            int trendDir = trend == 0 ? 1 : (trend == 1 ? -1 : 0);
            // 物价 / 行情热度：这支票所在行当近 5 日在往上走时，嘴上最热的那帮人先热起来
            double heat = Heat(stockId, day);

            int dir;
            if (trendDir == 0) dir = StockState.NextUnit(ref seed) < 0.5 + heat * 0.16 ? 1 : -1; // 横盘时热度决定嘴朝哪边
            else dir = honest ? trendDir : -trendDir;                                // 说真话 / 反着说
            // 热度高的时候连唱反调的也容易被带偏：本来要喊跌的，改口跟着喊涨
            if (dir < 0 && heat > 0.40 && StockState.NextUnit(ref seed) < (heat - 0.40) * 0.5) dir = 1;

            StockDef def = StockDefs.Get(stockId);
            double price = StockState.ToYuan(StockState.PriceOnDay(stockId, day));
            if (price <= 0.0) price = def != null ? def.BasePrice : 10.0;
            double target = price * (1.0 + (dir > 0 ? 0.18 : -0.14));

            Post p = new Post
            {
                BloggerId = b.Id,
                StockId = stockId,
                Day = day,
                Dir = dir,
                Headline = Headline(b, def, dir, price, target, ref seed)
            };

            // 事后核对：拿历史里已发生的收盘价比一比
            long a = StockState.PriceOnDay(stockId, day);
            long c = StockState.PriceOnDay(stockId, day + Horizon);
            if (a > 0 && c > 0)
            {
                p.Settled = true;
                p.Hit = (c - a) * dir > 0;
            }
            return p;
        }

        /// <summary>
        /// 行情热度（-1 ~ +1）：这支票所在行当近 5 日的平均涨幅，±8% 折算到两端。
        /// 只用历史收盘价推，所以任何一天重算结果都一样，不落存档也不会漂。
        /// </summary>
        private static double Heat(string stockId, int day)
        {
            int[] bs = StockEconomy.BucketsOf(stockId);
            if (bs == null || bs.Length == 0) return 0.0;

            double sum = 0.0;
            int n = 0;
            for (int i = 0; i < bs.Length; i++)
            {
                string[] ids = StockEconomy.BucketMembers(bs[i]);
                if (ids == null) continue;
                for (int k = 0; k < ids.Length; k++)
                {
                    long now = StockState.PriceOnDay(ids[k], day);
                    long then = StockState.PriceOnDay(ids[k], day - 5);
                    if (now > 0 && then > 0) { sum += (double)(now - then) / then; n++; }
                }
            }
            if (n == 0) return 0.0;
            double r = sum / n;
            if (r > 0.08) r = 0.08;
            if (r < -0.08) r = -0.08;
            return r / 0.08;
        }

        private static string Headline(Blogger b, StockDef def, int dir, double price,
            double target, ref uint seed)
        {
            string name = def != null ? def.Name : "这支票";
            string tag = def != null ? StockDefs.Tag(def) : string.Empty;
            bool up = dir > 0;

            if (b.Kind == BloggerKind.Fake)
            {
                // 假博主的口吻：笃定、内幕、给死目标价
                return up
                    ? "【看涨】" + name + tag + " 有大资金在收，我用内部消息确认过，"
                        + "目标价 " + target.ToString("0.00") + " 元，闭眼买。"
                    : "【看跌】" + name + tag + " 主力已经在出货了，别做最后的接盘侠，"
                        + "先看 " + target.ToString("0.00") + " 元。";
            }
            if (b.Kind == BloggerKind.Normal)
            {
                return up
                    ? "【看涨】" + name + tag + " 站上均线，量能跟得上，"
                        + "短线偏多，看到 " + target.ToString("0.00") + " 元附近。"
                    : "【看跌】" + name + tag + " 跌破短期支撑，反弹乏力，"
                        + "先减仓，等企稳再说。";
            }
            // 精选：有数据、有条件、不喊口号
            return up
                ? "【看涨】" + name + tag + "近几日资金持续净买、成本端压力缓和，"
                    + "估值中枢有望上移到 " + target.ToString("0.00") + " 元，回调可分批。"
                : "【看跌】" + name + tag + "库存与负债双双走高，需求端未见改善，"
                    + "合理区间下修至 " + target.ToString("0.00") + " 元，逢高减持。";
        }

        // ── 评论区 ────────────────────────────────────────────────────

        private static readonly string[] Nicknames =
        {
            "空间站打工人", "楼下卖面的老李", "夜班保安", "回收站老王", "便利店打工人",
            "穷游背包客", "管道维修工", "货运调度小周", "诊所护士", "旧书摊主",
            "三号舱住户", "上夜班的", "修空调的", "食堂帮厨", "跑腿小哥"
        };

        private static readonly string[] ShillUp =
        {
            "老师说得对，我已经上车了", "跟着老师做两回了，都是赚的",
            "这票基本面确实硬，我也加仓了", "目标价到了再走，不急",
            "感谢老师，昨天按你说的买了"
        };

        private static readonly string[] ShillDown =
        {
            "早就提醒要跑了，你们非不听", "我昨天清仓了，落袋为安",
            "老师分析在理，别硬扛", "这种走势还拿着就是赌", "谢谢老师，跑得及时"
        };

        // 水军也会唱反调，而且是「本来想卖，被劝服了」这种，第一眼看不出来
        private static readonly string[] ShillDoubtUp =
        {
            "我本来打算卖的，看老师这么一说又拿住了", "有点慌，但先信老师一次"
        };

        private static readonly string[] ShillDoubtDown =
        {
            "我本来想抄底的，被老师劝住了", "本来还抱着希望，看完有点怕"
        };

        // 行情热起来（通胀期物价连着走高）时，水军的马屁会拍得更露骨 ——
        // 这也是「大家一起热」的痕迹，但混在满屏的附和里照样不好认。
        private static readonly string[] ShillHotUp =
        {
            "这波不上车就晚了，我已经把家底都压上了",
            "老师一句话顶我三年经验，闭着眼买都不会亏",
            "再不买就真没机会了，明天开盘直接冲"
        };

        private static readonly string[] ShillHotDown =
        {
            "还不跑等着归零吗，我全清了",
            "这票已经烂透了，别指望反弹",
            "谢谢老师救命，再晚一天我就全赔进去了"
        };

        private static readonly string[] RealUp =
        {
            "又骗我接盘吧", "我昨天刚卖，气死了", "涨了三天了还能追？",
            "小仓位试试水", "这票我拿了半个月才回本，别乱吹"
        };

        private static readonly string[] RealDown =
        {
            "别唱空了，我满仓呢", "跌这么点就慌？", "越跌越买，怕什么",
            "等跌透了再说，现在不接", "这票跌了半年了，还没到底？"
        };

        private static readonly string[] RealNeutral =
        {
            "路过看看，没仓位", "看不太懂，先观望", "有数据吗？别光靠嘴说",
            "这种分析我一天能写十条", "愿赌服输，自己拿主意"
        };

        private static void BuildComments(Post p, Blogger b, ref uint seed)
        {
            int count = 3 + (int)(StockState.NextUnit(ref seed) * 3.0);   // 3~5 条
            if (count > 5) count = 5;
            int shills = StockState.NextUnit(ref seed) < 0.6 ? 1 : 2;
            if (shills > count) shills = count;

            // 哪几条是水军：随机挑，且不总在开头 —— 排头全是马屁精就太假了
            List<int> shillAt = new List<int>();
            while (shillAt.Count < shills)
            {
                int at = (int)(StockState.NextUnit(ref seed) * count);
                if (at < 0) at = 0;
                if (at >= count) at = count - 1;
                if (!shillAt.Contains(at)) shillAt.Add(at);
            }

            double heat = Heat(p.StockId, p.Day);
            for (int i = 0; i < count; i++)
            {
                string nick = Nicknames[(int)(StockState.NextUnit(ref seed) * Nicknames.Length) % Nicknames.Length];
                string text;
                if (shillAt.Contains(i))
                {
                    string[] pool = p.Dir > 0 ? ShillUp : ShillDown;
                    text = pool[(int)(StockState.NextUnit(ref seed) * pool.Length) % pool.Length];
                    // 三成换成「差点卖 / 差点买，被劝住」那种，掩饰一下
                    if (StockState.NextUnit(ref seed) < 0.30)
                    {
                        string[] doubt = p.Dir > 0 ? ShillDoubtUp : ShillDoubtDown;
                        text = doubt[(int)(StockState.NextUnit(ref seed) * doubt.Length) % doubt.Length];
                    }
                    // 行情热起来的时候，马屁拍得更露骨：越热越容易换到那一档
                    if (heat > 0.45 && StockState.NextUnit(ref seed) < (heat - 0.45) * 0.8)
                    {
                        string[] hot = p.Dir > 0 ? ShillHotUp : ShillHotDown;
                        text = hot[(int)(StockState.NextUnit(ref seed) * hot.Length) % hot.Length];
                    }
                }
                else
                {
                    double r = StockState.NextUnit(ref seed);
                    string[] pool = r < 0.40 ? (p.Dir > 0 ? RealUp : RealDown)
                        : (r < 0.75 ? RealNeutral : RealUp);
                    text = pool[(int)(StockState.NextUnit(ref seed) * pool.Length) % pool.Length];
                }
                p.Comments.Add(nick + "：" + text);
            }
            p.Shills = shills;
        }

        // ── 历史胜率：把回看期内的帖子重放一遍，逐条核对 ──────────────

        private static void EnsureStats(Blogger b)
        {
            // 开局头几天「三天后」的收盘价还不存在，帖子没法拿行情核对，于是整张表
            // 全是「暂无验证记录」——精选和野生看着一模一样，这功能就废了。
            // 现在的口径是：能核对的用真实结果，核不了的按 TruthRate 合成一条，
            // 全程只走确定性哈希（博主 + 标的 + 天数），读档重算逐位一致。
            // 回看期也不再把起始日截到第 1 天，第 0 天之前那 24 天照样算。
            // 另外今天这条不统计——它是「预测」，还没到能算账的时候。
            int from = StockState.Today - LookbackDays;
            for (int day = from; day < StockState.Today; day++)
            {
                uint seed = StockState.Hash32(b.Id + "|feed|" + day);
                if (StockState.NextUnit(ref seed) * 100.0 >= PostChance) continue;
                string stockId = PickStock(b, ref seed);
                if (stockId.Length == 0) continue;
                Post p = MakePost(b, stockId, day);

                bool hit;
                if (p.Settled) hit = p.Hit;
                else
                {
                    uint h = StockState.Hash32(b.Id + "|hit|" + stockId + "|" + day);
                    hit = StockState.NextUnit(ref h) < b.TruthRate;
                }

                if (hit) _hits[b.Id] = (_hits.TryGetValue(b.Id, out int c) ? c : 0) + 1;
                _settled[b.Id] = (_settled.TryGetValue(b.Id, out int s) ? s : 0) + 1;
            }
        }

        // ── 市场传闻（操盘手放出来的假消息）──────────────────────────

        /// <summary>
        /// 今天这支标的上有没有传闻。返回空串表示没有。
        /// 操盘手在自己坐庄的票上放风：七成顺着趋势放（帮着拉抬，听起来也像真的），
        /// 三成反着放——反着的那批就是用来骗接盘的假消息。
        /// </summary>
        public static string RumorText(string stockId)
        {
            if (!HasRumor(stockId)) return string.Empty;
            StockDef def = StockDefs.Get(stockId);
            string name = def != null ? def.Name : stockId;
            int dir = RumorDir(stockId);
            bool up = dir > 0;
            string[] pool = up
                ? new[]
                {
                    "有消息说" + name + "接下来要拿到一笔大订单",
                    "听说" + name + "的大股东要增持",
                    "有人在传" + name + "下期财报会大幅超预期"
                }
                : new[]
                {
                    "有人在传" + name + "被查账了",
                    "听说" + name + "的大客户跑了",
                    "有消息说" + name + "下期财报要爆雷"
                };
            uint seed = StockState.Hash32(stockId + "|rumor|" + StockState.Today);
            return pool[(int)(StockState.NextUnit(ref seed) * pool.Length) % pool.Length]
                + "　<color=#" + Palette.HexOf(Palette.Muted) + ">（来源不明，真假自辨）</color>";
        }

        /// <summary>传闻对收盘价的影响（元）。量级比正式事件小一大截。</summary>
        public static double RumorTermYuan(string stockId, double currentYuan)
        {
            if (!HasRumor(stockId)) return 0.0;
            uint seed = StockState.Hash32(stockId + "|rumormag|" + StockState.Today);
            double mag = 0.005 + StockState.NextUnit(ref seed) * 0.007;   // 0.5% ~ 1.2%
            return currentYuan * mag * RumorDir(stockId);
        }

        private static bool HasRumor(string stockId)
        {
            // 只在操盘手真正坐庄的那些票上放风，否则「传闻」到处都是就不值钱了
            if (StockBots.OperatorOn(stockId) == null) return false;
            uint seed = StockState.Hash32(stockId + "|rumor|" + StockState.Today);
            return StockState.NextUnit(ref seed) < 0.14;
        }

        private static int RumorDir(string stockId)
        {
            uint seed = StockState.Hash32(stockId + "|rumordir|" + StockState.Today);
            bool alongTrend = StockState.NextUnit(ref seed) < 0.70;
            int trend = StockEngine.TrendOf(stockId);
            int trendDir = trend == 0 ? 1 : (trend == 1 ? -1 : 0);
            if (trendDir == 0) return 1;
            return alongTrend ? trendDir : -trendDir;
        }

        /// <summary>今天有传闻的标的都在这里，给界面用。</summary>
        public static List<string> TodayRumors()
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                string id = StockDefs.All[i].Id;
                if (HasRumor(id)) ids.Add(id);
            }
            return ids;
        }
    }
}