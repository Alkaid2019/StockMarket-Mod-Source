namespace StockMarket
{
    public enum StockCategory
    {
        Station = 0,      // 空间站股：低风险、低手续费、有周期分红，鼓励长期持仓
        BlackMarket = 1   // 黑市股：高风险、高手续费、日内博弈
    }

    public sealed class StockDef
    {
        public string Id;
        public string Name;       // 公司名（仿现实品牌改一个字，避免侵权）
        public string FactionId;
        public StockCategory Category;
        public string Region;     // 层区（下层区 / 上层区 / 治安部 / 黑市 / 革命军）
        public string Business;   // 业务类别（游戏内的物品门类）
        public int BasePrice;     // 基础价（元）
        public double Sigma;      // 日波动率（高斯噪声标准差）
        public double FeeRate;    // 手续费率（买、卖各收一次）
        public int Risk;          // 风险星级 1..5
        public bool NeedLicense;  // 是否需要黑市开户证明
    }

    public sealed class StockEventDef
    {
        public string Id;
        public string Name;
        public string Summary;    // 一句话简介：说清这件事到底发生了什么、为什么影响股价
        public int Weight;        // 抽取权重
        public int MinDays;
        public int MaxDays;
        public double Impact;     // 每日冲击（比例，正负均可）
        public string[] Targets;  // 目标股票 Id；为空表示随机单支
        public string GameEventId; // 对应的游戏原生事件 Id，用于事件联动
    }

    public static class StockDefs
    {
        // ── 趋势档位 ──────────────────────────────────────────────────
        // 这是整个行情系统的「刺激感」来源。只有均值回归的话，价格永远在公允价
        // 附近来回摆，玩家看不到「在涨」，也就没有追涨杀跌的冲动（实测 672 天里
        // 全部标的都困在基准价 ±30% 内）。加一个持续 6~14 天的趋势档之后，
        // 牛市能连着涨十几天，熊市能连着跌，才谈得上「趋势」和「拿得住」。
        public const double TrendBullDrift = 0.026;   // 牛市每日基础漂移 +2.6%
        public const double TrendBearDrift = 0.024;   // 熊市每日基础漂移 -2.4%
        public const double TrendMomentum = 0.45;     // 牛熊市动量：昨天涨 10%，今天多涨 4.5%
        public const double TrendRangeMomentum = 0.06;// 震荡市几乎无动量

        // ── 均值回归（带内松、带外紧）────────────────────────────────
        // 回归太强 → 没有趋势；完全没有 → 价格会发散到离谱。
        // 现在带内每天只拉 1.6%，带子放宽到 +75% / −65%：
        // 目的是让一轮牛市能真涨出 50% 以上，玩家才敢拿、才舍得卖。
        public const double PullBase = 0.016;         // 带内每日回归比例
        public const double PullSlope = 0.18;         // 带外每多偏离 100% 追加的回归比例
        public const double PullBandUp = 0.75;        // 高于公允价 75% 起开始强拉
        public const double PullBandDown = 0.65;      // 低于公允价 65% 起开始强拉
        public const double PullMax = 0.60;           // 单日回归比例上限

        public const double FairValueRepScale = 0.40;  // 派系声望对公允价的放大上限（±40%）
        public const double CycleCapGain = 1.10;       // 单周期收益上限 +110%
        public const double CycleCapLoss = -0.80;      // 单周期亏损下限 -80%
        public const int CycleDays = 7;                // 运营周期长度，需用 TurnManager.DAYS_PER_MONTH 校准
        public const int HistoryLimit = 400;           // 图表保留的历史天数（够 1 年档位用）
        // 技术分析最长的档位是「一年」。老存档最多只记了 60 天，直接开一年档会是空的，
        // 所以进档时按现有定价模型把缺的那段回溯补出来，让长周期均线/MACD 一开局就有值。
        public const int BackfillDays = 365;
        // 事件间隔改成「随机天数」：每次都抽一个 2~7 天的下次触发日。
        // 原来固定 1~3 天的概率滚动，玩家摸清节奏后能提前埋伏，随机天数才猜不到。
        public const int EventMinGapDays = 2;          // 事件最小间隔（天）
        public const int EventMaxGapDays = 7;          // 事件最大间隔（天）
        public const int EventMaxActive = 3;           // 同时激活的事件上限
        public const double EventImpactCap = 0.50;     // 多事件叠加后的每日冲击上限，防止三连击把价格打穿
        // 消息落在盘中的概率（%）。盘中消息当天就能买卖，盘后消息只能等下一天，
        // 两种都要有，玩家才不敢「听到消息就无脑梭哈」。
        public const int IntradayNewsChance = 60;

        // ── 涨跌停（按层区）──────────────────────────────────────────
        // 每支标的当天最多涨/跌这么多，到板就锁价（跌停照样有人接、涨停照样有人卖，
        // 所以封板当天仍可按板价成交，只是价格再也走不动了）。
        //
        // 数值是拿离线推演定的：把每支标的按层区跑 900 天，统计「撞板天数占比」。
        // 一开始按 8/10/10/15/25 配，治安部/上层区/革命军 的撞板率高达 27%/40%/43%，
        // 几乎天天封板，等于把行情钉死；放宽到下面这组之后，撞板率落在 10~14%，
        // 一支股票大约八天封一次板——看得见、够刺激，又不至于失去交易机会。
        // 层区越乱、波动越大，板就越宽：治安部管得最严，黑市基本没人管。
        public const double LimitSecurity = 0.12;      // 治安部 ±12%
        public const double LimitLower = 0.15;         // 下层区 ±15%
        public const double LimitUpper = 0.20;         // 上层区 ±20%
        public const double LimitRevolution = 0.30;    // 革命军 ±30%
        public const double LimitBlack = 0.40;         // 黑市 ±40%

        /// <summary>该标的的涨跌停幅度（0.12 = ±12%）。</summary>
        public static double LimitOf(StockDef d)
        {
            if (d == null) return LimitLower;
            switch (d.Region)
            {
                case "治安部": return LimitSecurity;
                case "上层区": return LimitUpper;
                case "革命军": return LimitRevolution;
                case "黑市": return LimitBlack;
                default: return LimitLower;
            }
        }

        // ── 周期分红 ──────────────────────────────────────────────────
        // 原本 3.5% / 1.5% 每 7 天，折年化 +180%，把价差完全盖住了——玩家躺着不动
        // 钱也在涨，「总会涨回来」的观感就是这么来的。现在削到 1.2% / 0.5%
        // （年化约 +62% / +26%），分红退回「长期持仓的一点补贴」，收益主力交还给价差。
        //
        // 另外加了「持满一个完整周期才派息」：买入当周期不派息，
        // 否则周期最后一天买入能白拿一次分红，等于无风险套利。
        public const double StationDividend = 0.012;   // 空间站股每周期派息 = 持仓市值 × 1.2%
        public const double BlackDividend = 0.005;     // 黑市股每周期派息 = 持仓市值 × 0.5%

        // 派系声望联动
        // 基价缩了约 10 倍，这个门槛也得跟着缩，否则玩家满仓也换不来 1 点声望，整块功能形同虚设
        public const double RepPerMarketValue = 100.0; // 每 100 元持仓市值 = 1 点声望进度
        public const int RepCycleCap = 15;              // 单周期单派系声望增长上限
        public const double RepConflictThreshold = 0.30;// 敌对双方各占持仓 30% 则声望停滞
        public const int PlacementRepThreshold = 70;    // 低价配售解锁声望线
        public const double PlacementDiscount = 0.85;   // 配售价 = 现价 × 0.85
        public const int PlacementValueLimit = 500;     // 单次配售上限（元）

        // 交易规则
        public const int MinTradeValue = 1;             // 最小交易额（元）——留到 1 元，实现 1 股起购
        public const double PriceScale = 100.0;         // 价格内部以「分」存储，避免浮点漂移

        // 经济版本：股价量级调整过就 +1，老存档的价位会和新基价打架，靠它触发重建
        public const string EconomyVersion = "2";

        // ── 股票标的池（26 支）────────────────────────────────────────
        // 定价基准：玩家早期店铺现金约 200~300 元（见实机截图），
        // 所以单价定在 8~32 元，一次能买十几到几十股，而不是被 100 股起的金额挡在门外。
        //
        // 名字统一为「公司名」，另挂一个 [层区-业务] 标签：
        // 公司名照现实里的知名品牌只改一个字（避免侵权），读起来一眼就认得出；
        // 层区严格照游戏自己的声望面板（下层区 / 上层区 / 治安部 / 黑市 / 革命军），
        // 不再自造「安保」「游客」这类分区；FactionId 一律复用游戏已有的派系 Id，
        // 这样声望联动、事件冲击都能直接对上，以后加股票只要照抄一行。
        public static readonly StockDef[] All =
        {
            // ── 下层区：民生必需品，价格低、波动相对小，适合新手起步 ──
            // 手续费 0.8%：最便宜的一档，鼓励新手在这里反复练手。
            new StockDef { Id = "LL",    Name = "麦大劳",   Region = "下层区", Business = "快餐",  FactionId = "FACTION_LOWER_LEVEL", Category = StockCategory.Station, BasePrice =  8, Sigma = 0.060, FeeRate = 0.008, Risk = 2, NeedLicense = false },
            new StockDef { Id = "KFC",   Name = "肯德鸡",   Region = "下层区", Business = "快餐",  FactionId = "FACTION_LOWER_LEVEL", Category = StockCategory.Station, BasePrice = 10, Sigma = 0.065, FeeRate = 0.008, Risk = 2, NeedLicense = false },
            new StockDef { Id = "AGRI",  Name = "农天山泉", Region = "下层区", Business = "净水",  FactionId = "FACTION_LOWER_LEVEL", Category = StockCategory.Station, BasePrice =  9, Sigma = 0.062, FeeRate = 0.008, Risk = 2, NeedLicense = false },
            new StockDef { Id = "PEPS",  Name = "摆事可乐", Region = "下层区", Business = "饮料",  FactionId = "FACTION_LOWER_LEVEL", Category = StockCategory.Station, BasePrice = 12, Sigma = 0.075, FeeRate = 0.008, Risk = 3, NeedLicense = false },
            new StockDef { Id = "MID",   Name = "小迷",     Region = "下层区", Business = "电子",  FactionId = "FACTION_LOWER_LEVEL", Category = StockCategory.Station, BasePrice = 11, Sigma = 0.080, FeeRate = 0.008, Risk = 3, NeedLicense = false },
            new StockDef { Id = "HW",    Name = "华威",     Region = "下层区", Business = "电子",  FactionId = "FACTION_LOWER_LEVEL", Category = StockCategory.Station, BasePrice = 13, Sigma = 0.085, FeeRate = 0.008, Risk = 3, NeedLicense = false },

            // ── 上层区：奢侈品与高端消费，贵、波动中上 ──
            // 手续费 1.2%：东西贵、波动大，来回倒腾的成本也高一些。
            new StockDef { Id = "TOUR",  Name = "星巴客",   Region = "上层区", Business = "咖啡",  FactionId = "FACTION_UPPER_LEVEL", Category = StockCategory.Station, BasePrice = 15, Sigma = 0.100, FeeRate = 0.012, Risk = 3, NeedLicense = false },
            new StockDef { Id = "HOTEL", Name = "希而顿",   Region = "上层区", Business = "住宿",  FactionId = "FACTION_UPPER_LEVEL", Category = StockCategory.Station, BasePrice = 16, Sigma = 0.105, FeeRate = 0.012, Risk = 3, NeedLicense = false },
            new StockDef { Id = "UL",    Name = "路易威腾", Region = "上层区", Business = "奢侈品", FactionId = "FACTION_UPPER_LEVEL", Category = StockCategory.Station, BasePrice = 18, Sigma = 0.085, FeeRate = 0.012, Risk = 2, NeedLicense = false },
            new StockDef { Id = "CHAN",  Name = "香奈尔",   Region = "上层区", Business = "奢侈品", FactionId = "FACTION_UPPER_LEVEL", Category = StockCategory.Station, BasePrice = 20, Sigma = 0.090, FeeRate = 0.012, Risk = 2, NeedLicense = false },
            new StockDef { Id = "ENER",  Name = "埃森美孚", Region = "上层区", Business = "能源",  FactionId = "FACTION_UPPER_LEVEL", Category = StockCategory.Station, BasePrice = 20, Sigma = 0.110, FeeRate = 0.012, Risk = 3, NeedLicense = false },
            new StockDef { Id = "BMW",   Name = "宝玛",     Region = "上层区", Business = "汽车",  FactionId = "FACTION_UPPER_LEVEL", Category = StockCategory.Station, BasePrice = 22, Sigma = 0.118, FeeRate = 0.012, Risk = 3, NeedLicense = false },
            new StockDef { Id = "BENZ",  Name = "奔弛",     Region = "上层区", Business = "汽车",  FactionId = "FACTION_UPPER_LEVEL", Category = StockCategory.Station, BasePrice = 24, Sigma = 0.125, FeeRate = 0.012, Risk = 3, NeedLicense = false },
            new StockDef { Id = "APPL",  Name = "苹菓",     Region = "上层区", Business = "电子",  FactionId = "FACTION_UPPER_LEVEL", Category = StockCategory.Station, BasePrice = 26, Sigma = 0.095, FeeRate = 0.012, Risk = 2, NeedLicense = false },

            // ── 治安部：军工与金融，波动最小，是「存钱罐」那一档 ──
            // 手续费 0.6%：全表最低，配合低波动，适合把利润先停在这里避险。
            new StockDef { Id = "SEC",   Name = "洛克希得", Region = "治安部", Business = "军械",  FactionId = "FACTION_SECURITY",   Category = StockCategory.Station, BasePrice = 12, Sigma = 0.048, FeeRate = 0.006, Risk = 1, NeedLicense = false },
            new StockDef { Id = "RAY",   Name = "镭神",     Region = "治安部", Business = "军械",  FactionId = "FACTION_SECURITY",   Category = StockCategory.Station, BasePrice = 14, Sigma = 0.052, FeeRate = 0.006, Risk = 1, NeedLicense = false },
            new StockDef { Id = "BOEI",  Name = "波茵747",     Region = "治安部", Business = "航空",  FactionId = "FACTION_SECURITY",   Category = StockCategory.Station, BasePrice = 16, Sigma = 0.058, FeeRate = 0.006, Risk = 2, NeedLicense = false },
            new StockDef { Id = "BANK",  Name = "汇通银行", Region = "治安部", Business = "金融",  FactionId = "FACTION_SECURITY",   Category = StockCategory.Station, BasePrice = 25, Sigma = 0.068, FeeRate = 0.006, Risk = 1, NeedLicense = false },

            // ── 黑市：高风险、高手续费，必须先开户 ──
            // 手续费 4.5%：进出一次就要 9%，逼玩家「要么别碰，要么拿住」。
            new StockDef { Id = "BM",    Name = "黑鲨物流", Region = "黑市", Business = "走私",   FactionId = "FACTION_BLACK_MARKET", Category = StockCategory.BlackMarket, BasePrice = 24, Sigma = 0.180, FeeRate = 0.045, Risk = 4, NeedLicense = true },
            new StockDef { Id = "FORGE", Name = "德莱鲁",   Region = "黑市", Business = "证件",   FactionId = "FACTION_BLACK_MARKET", Category = StockCategory.BlackMarket, BasePrice = 18, Sigma = 0.185, FeeRate = 0.045, Risk = 4, NeedLicense = true },
            new StockDef { Id = "GUN",   Name = "卡什尼科夫", Region = "黑市", Business = "军火", FactionId = "FACTION_BLACK_MARKET", Category = StockCategory.BlackMarket, BasePrice = 28, Sigma = 0.230, FeeRate = 0.045, Risk = 5, NeedLicense = true },
            new StockDef { Id = "CART",  Name = "巴斯福",   Region = "黑市", Business = "化工",   FactionId = "FACTION_CARTEL",       Category = StockCategory.BlackMarket, BasePrice = 32, Sigma = 0.245, FeeRate = 0.045, Risk = 5, NeedLicense = true },
            new StockDef { Id = "DRUG",  Name = "拜尔制药", Region = "黑市", Business = "违禁药", FactionId = "FACTION_CARTEL",       Category = StockCategory.BlackMarket, BasePrice = 30, Sigma = 0.260, FeeRate = 0.045, Risk = 5, NeedLicense = true },

            // ── 革命军：地下渠道，波动最大，纯博差价 ──
            // 手续费 2.5%：比空间站贵、比黑市便宜，是「高风险但还能反复做」的一档。
            new StockDef { Id = "RMED",  Name = "红星制药", Region = "革命军", Business = "医疗", FactionId = "FACTION_REVOLUTION",  Category = StockCategory.Station, BasePrice = 14, Sigma = 0.150, FeeRate = 0.025, Risk = 4, NeedLicense = false },
            new StockDef { Id = "RCEL",  Name = "红隼通讯", Region = "革命军", Business = "通讯", FactionId = "FACTION_REVOLUTION",  Category = StockCategory.Station, BasePrice = 16, Sigma = 0.165, FeeRate = 0.025, Risk = 4, NeedLicense = false },
            new StockDef { Id = "REV",   Name = "卡秋莎重工", Region = "革命军", Business = "军火", FactionId = "FACTION_REVOLUTION", Category = StockCategory.Station, BasePrice = 20, Sigma = 0.220, FeeRate = 0.025, Risk = 5, NeedLicense = false },
        };

        /// <summary>列表里显示的标签，形如「[下层区-快餐]」。</summary>
        public static string Tag(StockDef d)
        {
            return "[" + d.Region + "-" + d.Business + "]";
        }

        /// <summary>标签 + 公司名的完整写法，给底栏提示、老K 报价这类地方用。</summary>
        public static string FullName(StockDef d)
        {
            return d.Name + " " + Tag(d);
        }

        /// <summary>全部标的 Id。给「全表生效」的事件用。</summary>
        public static string[] AllIds()
        {
            string[] ids = new string[All.Length];
            for (int i = 0; i < All.Length; i++) ids[i] = All[i].Id;
            return ids;
        }

        // ── 行情事件表 ────────────────────────────────────────────────
        // 权重不用凑满 100，抽取时按总和归一化。正负事件大致各半，
        // 影响面覆盖「单支 / 几支 / 全表」三档，避免每天都是同一批股票在动。
        //
        // 每条事件都带一句 Summary（简介）：事件页会原样显示，
        // 让玩家知道「到底发生了什么、为什么会影响到这几支股票」，
        // 而不是只看到一个名字和一个百分比。
        //
        // 按层区分组排列，和股票池的分区一一对应：黑市 / 革命军 / 治安部 / 上层区 / 下层区 / 全区域。
        public static readonly StockEventDef[] Events =
        {
            // ══════════════════ 黑市线 ══════════════════
            new StockEventDef { Id = "CRACKDOWN", Name = "黑市严打", Weight = 16, MinDays = 2, MaxDays = 2, Impact = -0.240, Targets = new[] { "BM", "CART", "GUN" },
                Summary = "治安部对黑市展开集中打击，走私货被大量查扣，黑市商户停摆避险。" },
            new StockEventDef { Id = "SWEEP", Name = "治安部清查", Weight = 12, MinDays = 2, MaxDays = 2, Impact = -0.200, Targets = new[] { "BM", "FORGE", "DRUG", "GUN" },
                Summary = "治安部逐街清查黑市窝点，假证、违禁药等地下生意被迫中断。" },
            new StockEventDef { Id = "DRUGBUST", Name = "缉毒行动", Weight = 10, MinDays = 2, MaxDays = 2, Impact = -0.260, Targets = new[] { "DRUG", "CART" },
                Summary = "治安部突击缉毒，制毒窝点被端，违禁药与化工渠道同时受创。" },
            new StockEventDef { Id = "BMBOOM", Name = "黑市繁荣", Weight = 14, MinDays = 2, MaxDays = 3, Impact = 0.200, Targets = new[] { "BM", "FORGE", "DRUG", "GUN" },
                Summary = "空间站管控松动，地下交易需求集中释放，黑市各类生意火爆。" },
            new StockEventDef { Id = "BMCRASH", Name = "黑市崩盘", Weight = 7, MinDays = 1, MaxDays = 2, Impact = -0.280, Targets = new[] { "BM", "FORGE", "CART", "DRUG", "GUN" },
                Summary = "黑市资金链断裂引发连锁抛售，地下商号接连爆雷。" },
            new StockEventDef { Id = "WANTED", Name = "星际通缉", Weight = 10, MinDays = 2, MaxDays = 2, Impact = -0.220, Targets = new[] { "BM", "GUN" },
                Summary = "星际通缉令高悬，走私与军火商不敢露面，交易量骤减。" },
            new StockEventDef { Id = "BMPORT", Name = "秘密货运港口启用", Weight = 12, MinDays = 2, MaxDays = 3, Impact = 0.200, Targets = new[] { "BM" },
                Summary = "黑市启用一处隐蔽货运港口，走私物流运输效率大幅提升。" },
            new StockEventDef { Id = "FORGEBREAK", Name = "假证原料断供", Weight = 11, MinDays = 2, MaxDays = 3, Impact = -0.210, Targets = new[] { "FORGE" },
                Summary = "制作伪造证件的核心原料被查封，证件企业产能受限。" },
            new StockEventDef { Id = "BMAUCTION", Name = "地下拍卖开幕", Weight = 13, MinDays = 1, MaxDays = 2, Impact = 0.220, Targets = new[] { "GUN", "CART" },
                Summary = "黑市大型地下拍卖会开启，军火、化工物资交易热度暴涨。" },
            new StockEventDef { Id = "BMBUST", Name = "黑市大宗交易查扣", Weight = 10, MinDays = 1, MaxDays = 2, Impact = -0.240, Targets = new[] { "BM", "CART", "GUN" },
                Summary = "治安部突袭黑市大宗交易现场，大量货物被收缴。" },
            new StockEventDef { Id = "BMMINE", Name = "隐匿矿场开采", Weight = 11, MinDays = 3, MaxDays = 4, Impact = 0.180, Targets = new[] { "CART" },
                Summary = "黑市控制的隐秘矿场投产，化工原料供给增加，成本下降。" },

            // ══════════════════ 革命军线 ══════════════════
            new StockEventDef { Id = "REBELWIN", Name = "革命军大捷", Weight = 10, MinDays = 2, MaxDays = 2, Impact = 0.240, Targets = new[] { "REV", "RCEL", "RMED" },
                Summary = "革命军取得一场关键胜利，缴获与补给充足，旗下企业订单激增。" },
            new StockEventDef { Id = "REBELOSS", Name = "革命军受挫", Weight = 10, MinDays = 2, MaxDays = 2, Impact = -0.210, Targets = new[] { "REV", "RCEL", "RMED" },
                Summary = "革命军攻势受阻、据点丢失，地下渠道的物资与订单一起缩水。" },
            new StockEventDef { Id = "REVHOSP", Name = "地下医院扩建", Weight = 12, MinDays = 2, MaxDays = 3, Impact = 0.170, Targets = new[] { "RMED" },
                Summary = "革命军扩建地下战地医院，医疗物资采购需求上涨。" },
            new StockEventDef { Id = "REVWIRE", Name = "通讯线路抢修", Weight = 11, MinDays = 1, MaxDays = 3, Impact = 0.150, Targets = new[] { "RCEL" },
                Summary = "红隼通讯完成多条地下线路抢修，通讯网络恢复全覆盖。" },
            new StockEventDef { Id = "REVTEST", Name = "武器试验成功", Weight = 12, MinDays = 2, MaxDays = 3, Impact = 0.190, Targets = new[] { "REV" },
                Summary = "卡秋莎重工新型武器测试取得成功，装备订单增加。" },
            new StockEventDef { Id = "REVCUT", Name = "补给线遭切断", Weight = 10, MinDays = 2, MaxDays = 3, Impact = -0.180, Targets = new[] { "REV" },
                Summary = "革命军一条关键补给线被切断，物资生产交付受阻。" },
            new StockEventDef { Id = "REVPROP", Name = "解放宣传铺开", Weight = 11, MinDays = 2, MaxDays = 4, Impact = 0.140, Targets = new[] { "RMED", "RCEL" },
                Summary = "革命军大范围开展宣传动员，大量设备、物资需求上升。" },
            new StockEventDef { Id = "REVFORM", Name = "战地药品配方优化", Weight = 10, MinDays = 2, MaxDays = 3, Impact = 0.130, Targets = new[] { "RMED" },
                Summary = "红星制药改良战地药品配方，降低原料消耗，生产成本下降。" },
            new StockEventDef { Id = "REVJAM", Name = "通讯信号干扰装置量产", Weight = 11, MinDays = 2, MaxDays = 3, Impact = 0.140, Targets = new[] { "RCEL" },
                Summary = "红隼通讯实现信号干扰设备量产，革命军装备采购需求上涨。" },
            new StockEventDef { Id = "REVAIR", Name = "重工工厂遭到空袭", Weight = 9, MinDays = 2, MaxDays = 4, Impact = -0.200, Targets = new[] { "REV" },
                Summary = "卡秋莎重工地下工厂遭遇空袭，生产线损毁，装备交付延期。" },
            new StockEventDef { Id = "REVBASE", Name = "战地通讯基站批量部署", Weight = 10, MinDays = 2, MaxDays = 3, Impact = 0.160, Targets = new[] { "RCEL" },
                Summary = "革命军大规模搭建地下通讯基站，通讯设备订单大幅增加。" },
            new StockEventDef { Id = "REVBLOCK", Name = "战地医疗物资封锁", Weight = 9, MinDays = 2, MaxDays = 3, Impact = -0.150, Targets = new[] { "RMED" },
                Summary = "治安部封锁医疗物资流向，红星制药原料供给紧张。" },

            // ══════════════════ 治安部线 ══════════════════
            new StockEventDef { Id = "CONFLICT", Name = "派系冲突", Weight = 14, MinDays = 1, MaxDays = 3, Impact = -0.160, Targets = new[] { "SEC", "RAY", "REV" },
                Summary = "治安部与革命军爆发正面冲突，双方军工产能都被打乱。" },
            new StockEventDef { Id = "ARMSRACE", Name = "军备竞赛", Weight = 10, MinDays = 2, MaxDays = 3, Impact = 0.190, Targets = new[] { "SEC", "RAY", "BOEI", "REV" },
                Summary = "各方疯狂扩军，军械、飞行器订单排到明年，军工股全线受益。" },
            new StockEventDef { Id = "SECBID", Name = "军备招标开启", Weight = 13, MinDays = 2, MaxDays = 3, Impact = 0.160, Targets = new[] { "SEC", "RAY", "BOEI" },
                Summary = "治安部公开军备采购招标，本地军械、航空企业迎来订单机会。" },
            new StockEventDef { Id = "SECCUT", Name = "治安部人员缩编", Weight = 10, MinDays = 2, MaxDays = 3, Impact = -0.130, Targets = new[] { "SEC", "RAY" },
                Summary = "空间站财政压缩预算，治安部裁员，军械采购需求缩减。" },
            new StockEventDef { Id = "BANKKINDLE", Name = "保险业务扩容", Weight = 11, MinDays = 2, MaxDays = 3, Impact = 0.110, Targets = new[] { "BANK" },
                Summary = "汇通银行拓展星际保险业务，金融板块收入预期上涨。" },
            new StockEventDef { Id = "SECPATROL", Name = "边境巡逻任务增加", Weight = 12, MinDays = 2, MaxDays = 4, Impact = 0.140, Targets = new[] { "SEC", "RAY", "BOEI" },
                Summary = "边境异动，巡逻任务增多，弹药、飞行器采购量提升。" },
            new StockEventDef { Id = "SECRULE", Name = "监管新规落地", Weight = 10, MinDays = 2, MaxDays = 3, Impact = -0.120, Targets = new[] { "BANK" },
                Summary = "空间站出台金融监管新规，银行业务受到限制。" },
            new StockEventDef { Id = "BOEIMAINT", Name = "飞行器维保大单", Weight = 11, MinDays = 2, MaxDays = 3, Impact = 0.130, Targets = new[] { "BOEI" },
                Summary = "治安部签订长期飞行器维护合同，航空企业获得稳定业务收入。" },
            new StockEventDef { Id = "SECQUALITY", Name = "军械质量抽检不合格", Weight = 9, MinDays = 2, MaxDays = 3, Impact = -0.140, Targets = new[] { "SEC", "RAY" },
                Summary = "抽检发现军械存在质量缺陷，军工企业短期采购订单暂停。" },
            new StockEventDef { Id = "BANKINSURE", Name = "星际安保保险热销", Weight = 10, MinDays = 2, MaxDays = 3, Impact = 0.100, Targets = new[] { "BANK" },
                Summary = "跨星域出行需求上涨，汇通银行星际安保保险销量走高。" },
            new StockEventDef { Id = "SECCANNON", Name = "新式火炮列装", Weight = 11, MinDays = 2, MaxDays = 4, Impact = 0.150, Targets = new[] { "SEC", "RAY" },
                Summary = "治安部批量采购新式火炮，军工产品迎来大规模列装订单。" },
            new StockEventDef { Id = "BANKCHECK", Name = "跨境金融调查启动", Weight = 8, MinDays = 2, MaxDays = 3, Impact = -0.090, Targets = new[] { "BANK" },
                Summary = "治安部开展跨境资金排查，市场担忧银行业务受限。" },

            // ══════════════════ 上层区线 ══════════════════
            new StockEventDef { Id = "TOURISM", Name = "旅游旺季", Weight = 16, MinDays = 2, MaxDays = 3, Impact = 0.130, Targets = new[] { "TOUR", "HOTEL", "UL", "BENZ" },
                Summary = "客流高峰到来，餐饮住宿与奢侈品消费同步走高。" },
            new StockEventDef { Id = "UPVIP", Name = "星际贵宾到访", Weight = 11, MinDays = 2, MaxDays = 3, Impact = 0.140, Targets = new[] { "UL", "CHAN", "HOTEL", "TOUR" },
                Summary = "外星富商组团到访上层区，奢侈品、高端服务业客流大增。" },
            new StockEventDef { Id = "UPESTATE", Name = "高端地产项目停工", Weight = 9, MinDays = 2, MaxDays = 4, Impact = -0.120, Targets = new[] { "ENER", "BMW" },
                Summary = "上层区高端地产项目暂缓施工，相关资本预期走低。" },
            new StockEventDef { Id = "UPLIMIT", Name = "限量新品发售", Weight = 12, MinDays = 1, MaxDays = 2, Impact = 0.130, Targets = new[] { "UL", "CHAN" },
                Summary = "奢侈品牌发布限定商品，预售火爆带动品牌股价上涨。" },
            new StockEventDef { Id = "UPSECURITY", Name = "上层区安保升级", Weight = 10, MinDays = 2, MaxDays = 3, Impact = -0.100, Targets = new[] { "TOUR", "HOTEL" },
                Summary = "上层区全面加强门禁安检，游客减少，高端消费客流下降。" },
            new StockEventDef { Id = "UPSPACE", Name = "私人航天项目立项", Weight = 9, MinDays = 3, MaxDays = 4, Impact = 0.150, Targets = new[] { "BMW", "BENZ", "ENER" },
                Summary = "私人星际载具项目正式立项，汽车、能源企业拿到大额订单。" },
            new StockEventDef { Id = "UPEXPO", Name = "星际奢侈品展会开幕", Weight = 10, MinDays = 2, MaxDays = 3, Impact = 0.120, Targets = new[] { "UL", "CHAN" },
                Summary = "跨空间站奢侈品展会在上层区举办，奢侈品牌订单快速上涨。" },
            new StockEventDef { Id = "UPQUOTA", Name = "高端能源配额缩减", Weight = 9, MinDays = 2, MaxDays = 3, Impact = -0.110, Targets = new[] { "ENER", "BMW", "BENZ" },
                Summary = "上层区收紧高端能源供给配额，能源企业与高端车企产能受限。" },
            new StockEventDef { Id = "UPVIPORDER", Name = "名流私人订单暴增", Weight = 10, MinDays = 2, MaxDays = 3, Impact = 0.130, Targets = new[] { "BMW", "BENZ" },
                Summary = "上层富豪大量定制私人星际载具，车企私人订单激增。" },
            new StockEventDef { Id = "UPTAX", Name = "高端消费税率上调", Weight = 9, MinDays = 2, MaxDays = 4, Impact = -0.100, Targets = new[] { "UL", "CHAN", "TOUR" },
                Summary = "上层区提高奢侈品消费税，高端商品购买力下滑，销量萎缩。" },
            new StockEventDef { Id = "UPPHONE", Name = "高端智能终端发布会", Weight = 11, MinDays = 1, MaxDays = 2, Impact = 0.120, Targets = new[] { "APPL" },
                Summary = "苹菓发布新一代旗舰智能设备，市场热度推高公司股价。" },

            // ══════════════════ 下层区线 ══════════════════
            new StockEventDef { Id = "HARVEST", Name = "物资丰收", Weight = 16, MinDays = 2, MaxDays = 2, Impact = 0.110, Targets = new[] { "LL", "KFC", "AGRI" },
                Summary = "下层区物资丰产，原料便宜、成本下降，民生企业利润变厚。" },
            new StockEventDef { Id = "LOWFOOD", Name = "下层区食材供应中断", Weight = 12, MinDays = 2, MaxDays = 3, Impact = -0.140, Targets = new[] { "LL", "KFC" },
                Summary = "跨层食材运输暂停，快餐企业原材料紧缺，营收预期下滑。" },
            new StockEventDef { Id = "LOWSUB", Name = "社区消费补贴落地", Weight = 12, MinDays = 2, MaxDays = 3, Impact = 0.130, Targets = new[] { "LL", "KFC", "PEPS" },
                Summary = "下层区发放民生消费补贴，居民购买力提升，本土消费企业受益。" },
            new StockEventDef { Id = "LOWDELIVERY", Name = "外卖配送系统瘫痪", Weight = 10, MinDays = 1, MaxDays = 2, Impact = -0.110, Targets = new[] { "LL", "KFC" },
                Summary = "下层区物流终端故障，外卖业务停滞，餐饮企业订单减少。" },
            new StockEventDef { Id = "LOWPRE", Name = "预制菜新品上线", Weight = 11, MinDays = 2, MaxDays = 3, Impact = 0.120, Targets = new[] { "LL", "KFC" },
                Summary = "快餐厂商推出全新预制食品，市场订单快速增长。" },
            new StockEventDef { Id = "LOWBLACKOUT", Name = "下层区停电检修", Weight = 10, MinDays = 1, MaxDays = 2, Impact = -0.090, Targets = new[] { "MID", "HW", "AGRI" },
                Summary = "全域供电系统检修，工厂短暂停工，生产进度延后。" },
            new StockEventDef { Id = "LOWSCRAP", Name = "回收物资收购涨价", Weight = 10, MinDays = 2, MaxDays = 3, Impact = 0.080, Targets = new[] { "MID", "HW", "AGRI" },
                Summary = "下层区上调废旧物资收购价格，电子、净水企业原材料采购成本下降。" },
            new StockEventDef { Id = "LOWBUS", Name = "公共交通涨价", Weight = 9, MinDays = 2, MaxDays = 3, Impact = -0.070, Targets = new[] { "LL", "KFC", "PEPS" },
                Summary = "通勤票价上调，居民外出消费意愿降低，餐饮饮料订单减少。" },
            new StockEventDef { Id = "LOWCHIP", Name = "本土芯片良率突破", Weight = 11, MinDays = 2, MaxDays = 4, Impact = 0.110, Targets = new[] { "MID", "HW" },
                Summary = "下层芯片工厂工艺改良，芯片良品率提升，生产成本大幅下降。" },
            new StockEventDef { Id = "LOWRAT", Name = "鼠患大面积爆发", Weight = 9, MinDays = 1, MaxDays = 2, Impact = -0.100, Targets = new[] { "LL", "KFC", "PEPS" },
                Summary = "下层区鼠患蔓延，食品仓储受损，食品企业存货损耗增加。" },
            new StockEventDef { Id = "LOWWATER", Name = "社区饮水改造工程", Weight = 10, MinDays = 3, MaxDays = 4, Impact = 0.090, Targets = new[] { "AGRI" },
                Summary = "社区老旧供水管道翻新，农天山泉拿下长期供水供应合同。" },

            // ══════════════════ 民生 · 物流 · 大盘 ══════════════════
            new StockEventDef { Id = "DROUGHT", Name = "水资源紧缺", Weight = 8, MinDays = 2, MaxDays = 2, Impact = 0.220, Targets = new[] { "AGRI", "PEPS" },
                Summary = "供水紧张，净水与饮料变成抢手货，售价上涨带动营收。" },
            new StockEventDef { Id = "LOGISTICS", Name = "航运中断", Weight = 14, MinDays = 2, MaxDays = 2, Impact = -0.130, Targets = new[] { "PEPS", "BM", "ENER" },
                Summary = "货运航线中断，饮料、能源的原料进不来，交付被迫延后。" },
            new StockEventDef { Id = "OUTBREAK", Name = "疫病蔓延", Weight = 10, MinDays = 2, MaxDays = 3, Impact = -0.170, Targets = new[] { "LL", "KFC", "RMED" },
                Summary = "疫病扩散，餐饮客流骤减，医药需求虽涨但产能跟不上。" },
            new StockEventDef { Id = "MEDBOOM", Name = "医疗紧缺", Weight = 10, MinDays = 2, MaxDays = 2, Impact = 0.200, Targets = new[] { "RMED" },
                Summary = "医疗物资全面告急，制药企业开足马力，订单与药价齐升。" },
            new StockEventDef { Id = "TECH", Name = "技术突破", Weight = 9, MinDays = 2, MaxDays = 3, Impact = 0.180, Targets = new[] { "HW", "MID", "APPL", "RCEL" },
                Summary = "关键技术取得突破，电子与通讯企业拿到新一轮订单。" },
            new StockEventDef { Id = "ENERGYCUT", Name = "能源限供", Weight = 12, MinDays = 2, MaxDays = 2, Impact = -0.150, Targets = new[] { "ENER" },
                Summary = "空间站限制能源配额，能源企业开工不足，营收承压。" },
            new StockEventDef { Id = "BANKRUN", Name = "挤兑风波", Weight = 10, MinDays = 2, MaxDays = 2, Impact = -0.110, Targets = new[] { "BANK", "UL", "CHAN" },
                Summary = "储户集中提款，银行流动性吃紧，高端消费也被拖累。" },
            new StockEventDef { Id = "SHORTS", Name = "做空传闻", Weight = 11, MinDays = 1, MaxDays = 3, Impact = -0.140, Targets = new[] { "BANK", "APPL", "BMW" },
                Summary = "市场流传大额做空消息，投资者恐慌抛售，股价被动下挫。" },

            // ══════════════════ 全区域 ══════════════════
            new StockEventDef { Id = "FESTIVAL", Name = "站庆狂欢", Weight = 12, MinDays = 3, MaxDays = 3, Impact = 0.120, Targets = AllIds(),
                Summary = "空间站举办庆典，全域消费与交易活跃，几乎所有标的都跟着涨。" },
            new StockEventDef { Id = "CRASH", Name = "股票崩盘", Weight = 7, MinDays = 1, MaxDays = 1, Impact = -0.450, Targets = AllIds(),
                Summary = "市场信心崩塌引发全面踩踏，各层区股票无一幸免。" },
            new StockEventDef { Id = "SURGE", Name = "派系暴涨", Weight = 7, MinDays = 1, MaxDays = 1, Impact = 0.420, Targets = AllIds(),
                Summary = "大量资金同时涌入，全市场出现普涨式逼空行情。" },
            new StockEventDef { Id = "ALLTRADE", Name = "空间站跨区贸易协定签署", Weight = 8, MinDays = 3, MaxDays = 5, Impact = 0.100, Targets = AllIds(),
                Summary = "五大区域贸易壁垒降低，全区域商业往来活跃，整体市场利好。" },
            new StockEventDef { Id = "ALLENERGY", Name = "全区域能源价格暴跌", Weight = 7, MinDays = 2, MaxDays = 4, Impact = 0.080, Targets = AllIds(),
                Summary = "空间站能源产能过剩，能源售价下跌，全行业运营成本降低。" },
            new StockEventDef { Id = "ALLPLAGUE", Name = "全区域疫病预警发布", Weight = 7, MinDays = 2, MaxDays = 4, Impact = -0.110, Targets = AllIds(),
                Summary = "疫病风险预警发布，居民减少外出消费，全市场预期走弱。" },
            new StockEventDef { Id = "ALLTAXCUT", Name = "全空间站税收减免法案", Weight = 8, MinDays = 3, MaxDays = 5, Impact = 0.090, Targets = AllIds(),
                Summary = "空间站临时下调企业税率，各企业利润预期得到提升。" },

            // ══════════════════ 宏观物价 ══════════════════
            // 这一组盯的是「钱和货的价格」，和 StockMacro 那条供需线是一套语言：
            // 那边是慢慢磨出来的物价指数，这边是突然砸下来的宏观消息面。
            new StockEventDef { Id = "MACROCPIUP", Name = "全站物价指数上行", Weight = 10, MinDays = 3, MaxDays = 4, Impact = 0.090, Targets = AllIds(),
                Summary = "统计处公布的物价指数连续第三个月上行，各家企业名义营收普遍改善，只是居民开始捂紧钱包。" },
            new StockEventDef { Id = "MACROCPIDOWN", Name = "全站物价指数回落", Weight = 10, MinDays = 3, MaxDays = 4, Impact = -0.080, Targets = AllIds(),
                Summary = "统计处的物价指数连续回落，账面上的营收跟着缩水，市场开始担心通缩。" },
            new StockEventDef { Id = "MACROFUEL", Name = "燃料与能源附加费上调", Weight = 11, MinDays = 2, MaxDays = 3, Impact = -0.110, Targets = new[] { "PEPS", "ENER", "MID", "AGRI" },
                Summary = "能源批发价上调，运输与生产成本同步走高，吃这口饭的企业毛利被压薄。" },
            new StockEventDef { Id = "MACROFREIGHT", Name = "运费联合上浮", Weight = 11, MinDays = 2, MaxDays = 3, Impact = -0.100, Targets = new[] { "PEPS", "BM", "ENER", "MID" },
                Summary = "几家航运公司同时上调运费，进货成本整体抬高，货越重的行当越难受。" },
            new StockEventDef { Id = "MACROTARIFF", Name = "进口关税加征", Weight = 9, MinDays = 2, MaxDays = 4, Impact = -0.120, Targets = new[] { "APPL", "HW", "RCEL", "UL" },
                Summary = "跨区进口关税加征，靠外部供应链吃饭的企业成本骤增。" },
            new StockEventDef { Id = "MACROSUBFOOD", Name = "主食保供补贴投放", Weight = 12, MinDays = 2, MaxDays = 3, Impact = 0.100, Targets = new[] { "LL", "KFC", "AGRI" },
                Summary = "管理方为保证下层区吃得上饭投放保供补贴，平价食品走量明显回升。" },
            new StockEventDef { Id = "MACROCAPDAILY", Name = "日用品价格管控", Weight = 10, MinDays = 2, MaxDays = 3, Impact = -0.090, Targets = new[] { "AGRI", "MID", "HW" },
                Summary = "日用品涨得太快，管理方直接管住终端零售价，摊主的进货价被压了下来。" },
            new StockEventDef { Id = "MACROHOARD", Name = "批发商囤货待涨", Weight = 11, MinDays = 2, MaxDays = 3, Impact = 0.110, Targets = new[] { "AGRI", "MID", "PEPS" },
                Summary = "批发商赌后市还要涨，成批把货压在仓库里不出手，市面上一时半会儿见不到货。" },
            new StockEventDef { Id = "MACROSHORTAGE", Name = "全站日用品短缺", Weight = 9, MinDays = 2, MaxDays = 2, Impact = 0.130, Targets = new[] { "AGRI", "MID" },
                Summary = "补货船期误点，日用品货架大面积空置，短期只有手里有货的人说了算。" },
            new StockEventDef { Id = "MACROOVERSTOCK", Name = "仓库积压爆仓", Weight = 10, MinDays = 2, MaxDays = 3, Impact = -0.110, Targets = new[] { "MID", "HW", "AGRI" },
                Summary = "前几个月进的货全堵在仓库里，仓储费压得喘不过气，只能割价甩货。" },
            new StockEventDef { Id = "MACROINFLWAVE", Name = "通胀预期升温", Weight = 9, MinDays = 2, MaxDays = 3, Impact = 0.120, Targets = new[] { "UL", "CHAN", "ENER", "BANK" },
                Summary = "大家认定钱要毛了，抢着把钱换成实物和硬资产，保值的行当被抬了起来。" },
            new StockEventDef { Id = "MACRODEFLWAVE", Name = "通缩螺旋担忧", Weight = 8, MinDays = 2, MaxDays = 4, Impact = -0.130, Targets = new[] { "BANK", "UL", "CHAN", "ENER" },
                Summary = "价格越跌越没人买、越没人买越跌，市场担心陷进通缩螺旋，先砸了再说。" },

            // ══════════════════ 宏观供需 ══════════════════
            new StockEventDef { Id = "MACROWAGE", Name = "工资集体谈判达成", Weight = 11, MinDays = 2, MaxDays = 3, Impact = 0.090, Targets = new[] { "LL", "KFC", "PEPS", "TOUR" },
                Summary = "下层区工资集体谈判达成，工人手里多了点闲钱，最先花掉的就是吃喝玩乐。" },
            new StockEventDef { Id = "MACROSTRIKE", Name = "物流工会罢工", Weight = 9, MinDays = 2, MaxDays = 3, Impact = -0.140, Targets = new[] { "PEPS", "BM", "MID", "AGRI" },
                Summary = "物流工会罢工，进出货全卡在码头上，靠周转吃饭的生意最先受不了。" },
            new StockEventDef { Id = "MACROHARVEST", Name = "全站丰收季", Weight = 12, MinDays = 2, MaxDays = 3, Impact = 0.110, Targets = new[] { "LL", "KFC", "AGRI", "PEPS" },
                Summary = "水培区和牧区同时丰收，食材供应宽松，做吃喝的原料成本降了一截。" },
            new StockEventDef { Id = "MACROARMSORDER", Name = "军需采购大单落地", Weight = 10, MinDays = 2, MaxDays = 4, Impact = 0.150, Targets = new[] { "GUN", "SEC", "RAY", "BOEI" },
                Summary = "一份多年期军需采购大单落地，拿得到份额的厂子等于把几年的饭都订出去了。" },
            new StockEventDef { Id = "MACROMEDSTOCK", Name = "医疗储备计划启动", Weight = 11, MinDays = 2, MaxDays = 3, Impact = 0.120, Targets = new[] { "RMED", "DRUG" },
                Summary = "管理方启动医疗物资储备计划，成批采购药品与耗材，订单直接砸到厂里。" },
            new StockEventDef { Id = "MACROCHIPBAN", Name = "关键芯片出口管制", Weight = 9, MinDays = 2, MaxDays = 3, Impact = -0.130, Targets = new[] { "APPL", "RCEL" },
                Summary = "上游对关键芯片实施出口管制，靠外购芯片装机的企业排产直接被打乱。" },
            new StockEventDef { Id = "MACROCHIPDIY", Name = "本土替代方案获批", Weight = 10, MinDays = 2, MaxDays = 4, Impact = 0.120, Targets = new[] { "MID", "HW" },
                Summary = "本土自研替代方案通过验证并获批量产，一直被卡脖子的环节总算有人接上了。" },
            new StockEventDef { Id = "MACROTOURSUB", Name = "旅游消费券发放", Weight = 12, MinDays = 2, MaxDays = 3, Impact = 0.120, Targets = new[] { "TOUR", "HOTEL", "UL" },
                Summary = "管理方发放旅游消费券，站内短途客流明显回暖，吃住玩一起沾光。" },
            new StockEventDef { Id = "MACROSECBUDGET", Name = "治安预算追加", Weight = 10, MinDays = 2, MaxDays = 4, Impact = 0.130, Targets = new[] { "SEC", "RAY", "BOEI" },
                Summary = "治安部年度预算被临时追加，装备与维保的账期一下子好谈了。" },
            new StockEventDef { Id = "MACROBMTAX", Name = "黑市税卡收紧", Weight = 10, MinDays = 2, MaxDays = 3, Impact = -0.150, Targets = new[] { "BM", "FORGE", "CART", "DRUG" },
                Summary = "进出黑市的税卡收紧，每一手都要多过一道关，黑市商户的周转被掐住。" },
            new StockEventDef { Id = "MACROBMHARD", Name = "黑市改用硬通货结算", Weight = 9, MinDays = 2, MaxDays = 3, Impact = 0.140, Targets = new[] { "BM", "GUN", "CART" },
                Summary = "黑市大额交易改用硬通货结算，现货被人抢着收，手上压着货的商户笑到最后。" },
            new StockEventDef { Id = "MACROREVHARVEST", Name = "革命军控制区丰收", Weight = 10, MinDays = 2, MaxDays = 3, Impact = 0.130, Targets = new[] { "REV", "RCEL", "RMED" },
                Summary = "革命军控制区这一季收成不错，后方的补给压力松了，前线的采购也敢下单了。" },
            new StockEventDef { Id = "MACROREVCUT", Name = "革命军资金链吃紧", Weight = 10, MinDays = 2, MaxDays = 3, Impact = -0.120, Targets = new[] { "REV", "RMED" },
                Summary = "革命军的经费来源被掐了一段，几笔尾款拖着不结，供应商先扛不住了。" },
            new StockEventDef { Id = "MACRORATEUP", Name = "央行上调基准利率", Weight = 9, MinDays = 3, MaxDays = 4, Impact = -0.100, Targets = AllIds(),
                Summary = "央行上调基准利率，借钱变贵了，全市场的估值中枢跟着往下挪。" },
            new StockEventDef { Id = "MACRORATEDOWN", Name = "央行下调基准利率", Weight = 9, MinDays = 3, MaxDays = 4, Impact = 0.090, Targets = AllIds(),
                Summary = "央行下调基准利率，资金成本降下来，全市场的风险偏好一起抬头。" },
        };

        public static StockDef Get(string id)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Id == id) return All[i];
            }
            return null;
        }

        public static StockEventDef GetEvent(string id)
        {
            for (int i = 0; i < Events.Length; i++)
            {
                if (Events[i].Id == id) return Events[i];
            }
            // 店铺经营派生出来的定向事件（热销 / 积压 / 治安部巡查）挂在 StockEconomy 里，
            // 宏观物价的那批（通胀 / 通缩 / 供不应求 / 第二环）挂在 StockMacro 里，
            // 它们都不进随机抽取池，但价格冲击、事件公告页、夜间报告都得能查到定义。
            StockEventDef d = StockEconomy.FindEvent(id);
            return d != null ? d : StockMacro.FindEvent(id);
        }

        /// <summary>空间站股与黑市股互为「敌对」，用于声望抵消判定。</summary>
        public static bool AreHostile(StockDef a, StockDef b)
        {
            if (a == null || b == null || a == b) return false;
            return a.Category != b.Category;
        }
    }
}
