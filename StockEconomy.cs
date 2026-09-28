using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2Cpp;

namespace StockMarket
{
    /// <summary>
    /// 实体经济联动：你店里卖出去的东西，反过来影响行情。
    ///
    /// 三件事，从轻到重：
    ///   1）当天实盘脉冲 —— 每卖出一笔，按货值给对应需求口加一点「需求」，
    ///      盘中报价当场抬起来（走 StockIntraday），收工时并进当日收盘价，
    ///      再靠日线本来就有的均值回归慢慢消化。
    ///   2）多日旺季 —— 某个需求口连着几天走货旺，触发一条「需求走强」事件，
    ///      自动进事件公告页 / 夜间报告 / 事件冲击计算。
    ///   3）积压负反馈 —— 连着放量好几天、板块也涨了不少之后，触发「货源积压」回调。
    ///      专门用来堵「刷卖货把股价顶上去」变成印钞机这个口子：
    ///      脉冲封顶 5%，事件是有限天数的，赚完的一定会被回调吃掉一段。
    /// 另有一条独立通道：卖违禁货/赃货给罪犯买家，会把治安部的注意力引过来，
    /// 黑市股挨打、治安部股受益；卖得不算多的时候，则是黑市货源走俏。
    ///
    /// 派生事件全家福（都只由本模块按天检测挂上去，不进随机抽取池）：
    ///   需求走强 / 货源积压 / 抢购潮 / 价格通胀 / 供应中断 / 连带景气 / 零售全面回暖
    ///   / 黑市货源走俏 / 治安部专项巡查 / 治安部行动经费加码
    ///
    /// 数值全部集中在本文件顶部的常量区，改平衡不用翻别处。
    /// </summary>
    public static class StockEconomy
    {
        // ══════════════ 数值（自行平衡）══════════════
        /// <summary>单支标的当天因店铺需求最多抬这么多。</summary>
        public const double PulseCap = 0.05;

        // ══════════════ 产能折算：门槛跟着玩家自己的产量走 ══════════════
        // 同一个 300 元，开局那间小铺三天都卖不出来，晚期的旺铺只是一个上午的量。
        // 所以经营门槛不再写死「多少元」，改成「你平时产能的多少倍」：
        //     产能 = 这个口子最近一段时间的日均货值（新档期按口子股价给个兜底值）
        //     门槛 = 产能 × 倍数
        // 前期产能小 → 卖几十元就触发；后期产量大了 → 要卖几百上千元才触发。
        // 物价涨、行情涨，玩家的报价跟着涨，产能自然抬着门槛一起走，不用再手调数值。
        // 而且门槛追的是「平时的你」，不会出现「正常做买卖天天挨罚」。

        /// <summary>产能的滑动更新率：0.12 ≈ 八天时间常数。太灵敏会被单日大单带得乱跳。</summary>
        private const double PaceAlpha = 0.12;
        /// <summary>新档期的兜底产能（元/天），还要乘口子价格系数。开张头几天还没有历史时用它。</summary>
        private const double PaceDayBase = 25.0;
        /// <summary>
        /// 宏观物价层的口径产能：喂给 StockMacro 的货值要先按「今天相当于平时多少」
        /// 折算回这个量级。NPC 店铺的买卖盘是写死的（一天百来块），后期玩家的绝对
        /// 货值能顶它好几倍，不折算的话整条供需线会常年焊在「供不应求」上、
        /// 物价指数一直钉在 +22%，通胀事件天天报。
        /// 折算之后物价层只认「今天比平时多干了多少」，跟门槛、脉冲一个口径。
        /// </summary>
        private const double MacroRefPace = 25.0;
        /// <summary>折算基准价：口子均价 15 元时价格系数为 1.0。</summary>
        private const double PriceRef = 15.0;
        /// <summary>价格系数上下限，免得极端行情把起步门槛拉飞。</summary>
        private const double PriceFactorMin = 0.5;
        private const double PriceFactorMax = 2.2;

        // 各门槛相对产能的倍数。括号里是折算前的旧绝对值（当年按「产能 72 元/天」校准的），
        // 想找回旧手感就往注释里的数字上靠。
        /// <summary>走货旺：一天走了平时的 1.8 倍（旧值 144 元/天）。</summary>
        private const double HotMul = 1.8;
        /// <summary>积压：最近 3 天累计 ≥ 旺线 × 这个倍数（旧值 360 元/3 天）。</summary>
        private const double StaleMul = 3.5;
        /// <summary>抢购潮：一天走了平时的 5.8 倍（旧值 416 元/天）。</summary>
        private const double SurgeMul = 5.8;
        /// <summary>没开张：一天不到平时的三分之一（旧值 24 元/天）。</summary>
        private const double ColdMul = 0.33;
        /// <summary>累计通胀：累计到平时 17.8 天的量（旧值 1280 元）。</summary>
        private const double InflateMul = 17.8;
        /// <summary>违禁货被巡查（旧值 200 元/天）。</summary>
        private const double CrimeRaidMul = 2.78;
        /// <summary>违禁货走俏（旧值 120 元/天）。</summary>
        private const double CrimeHotMul = 1.67;
        /// <summary>脉冲半饱和点：平时两天的货值，脉冲走到一半（旧值 150 元）。</summary>
        private const double PulseMul = 2.0;
        /// <summary>夜间「店铺实绩」的播报线：够到平时的一半才值得报一条（旧值 20 元）。</summary>
        private const double ReportMul = 0.5;
        /// <summary>竞争衰减标尺（旧值 2400 元）。</summary>
        private const double CompeteMul = 33.0;
        /// <summary>竞争衰减的下限（别家店铺再多，也不会把你的影响压到零）。</summary>
        private const double CompeteFloor = 0.45;
        /// <summary>连续走货旺几天 → 触发「需求走强」。</summary>
        private const int HotStreak = 3;
        /// <summary>同一口子两次事件之间至少隔几天，免得连着报同一条。</summary>
        private const int HotCooldown = 8;
        /// <summary>积压看的是「最近几天累计货值」，不看连续天数 —— 一天猛卖也能一天顶出来。</summary>
        private const int StaleWindow = 3;
        /// <summary>积压事件自己的冷却。跟热销分开算，否则热销刚挂上就把积压挡住了。</summary>
        private const int StaleCooldown = 6;
        private const int CrimeStreak = 2;
        private const int CrimeCooldown = 10;

        private const double HotImpact = 0.016;
        private const double StaleImpact = -0.024;
        private const double RaidDownImpact = -0.030;
        private const double RaidUpImpact = 0.022;

        // ── 第二批派生事件：细一点的经营纹理 ──
        // 门槛都压在「正常卖货挣得到、但不会一卖就炸」的位置，
        // 单次影响 1.0%~2.2%，比随机事件的 10%~26% 小一大截：
        // 玩家能自己造出来的行情只能是「补贴」，不该盖过市场本身。
        private const int SurgeCooldown = 10;
        private const double SurgeImpact = 0.022;
        private const int InflateCooldown = 15;
        private const double InflateImpact = 0.018;
        /// <summary>旺过之后连着几天没开张 → 供应中断。</summary>
        private const int ColdStreak = 4;
        private const int ColdCooldown = 12;
        private const double ColdImpact = -0.016;
        /// <summary>一个口子旺，隔壁口子跟着沾光。</summary>
        private const double SpillImpact = 0.010;
        private const int SpillCooldown = 8;
        /// <summary>同一天有几个口子都走货旺 → 全站零售回暖。</summary>
        private const int BoomBuckets = 3;
        private const int BoomCooldown = 14;
        private const double BoomImpact = 0.012;
        private const int CrimeHotCooldown = 8;
        private const double CrimeHotImpact = 0.015;

        // ══════════════ 六个需求口 ══════════════
        public const int BucketCount = 6;
        private static readonly string[] BucketName =
        {
            "餐饮饮品", "日用百货", "医药耗材", "军械装备", "奢侈品", "电子器件"
        };
        /// <summary>每个需求口对应的标的。</summary>
        private static readonly string[][] BucketStocks =
        {
            new[] { "LL", "KFC", "PEPS", "AGRI" },
            new[] { "AGRI", "MID", "HW" },
            new[] { "DRUG", "RMED" },
            new[] { "GUN", "SEC", "RAY", "REV" },
            new[] { "UL", "CHAN", "TOUR", "HOTEL" },
            new[] { "APPL", "MID", "HW", "RCEL" }
        };
        /// <summary>商品名/类型里的关键词，命中即归这个口子。</summary>
        private static readonly string[][] BucketKeys =
        {
            // 餐饮饮品
            new[] { "食", "菜", "肉", "果", "莓", "菌", "蘑", "咖啡", "豆", "茶", "酒", "饮料", "汽水", "可乐",
                    "水", "奶", "面包", "饼", "饭", "汤", "糖", "盐", "香料", "营养", "干粮", "罐头", "淀粉",
                    "food", "meal", "meat", "nutrifruit", "bloomberry", "fillerweed", "mushroom", "cap",
                    "coffee", "bean", "beer", "drink", "water", "cola", "soda", "starch", "ration", "canned",
                    "cheese", "milk", "bread", "rice", "snack", "seed", "crop", "plant", "grow" },
            // 日用百货
            new[] { "布", "衣", "裤", "鞋", "背包", "工具", "零件", "材料", "塑料", "金属", "绳", "纸", "箱", "桶",
                    "灯", "棉", "布", "皮革", "家具", "清洁", "厨",
                    "kotton", "cloth", "fabric", "backpack", "bag", "tool", "part", "scrap", "plastic",
                    "metal", "material", "rope", "tape", "boot", "glove", "suit", "uniform", "junk",
                    "crate", "box", "lamp", "leather", "furniture" },
            // 医药耗材
            new[] { "药", "针", "绷带", "抗生素", "止痛", "兴奋剂", "注射", "毒", "化学", "试剂", "光谱",
                    "菌盖", "梦尘", "神经", "医用", "救护", "兴奋",
                    "med", "pharma", "drug", "pill", "vial", "syringe", "bandage", "antibiotic", "stim",
                    "chem", "reagent", "toxin", "nerve", "gas", "dust", "extract" },
            // 军械装备
            new[] { "枪", "弹", "炮", "手雷", "炸药", "雷管", "火药", "军械", "护甲", "装甲", "头盔", "防弹",
                    "消音", "瞄", "握把", "枪管", "匕首", "刀", "警棍", "军火",
                    "grenade", "gun", "pistol", "rifle", "ammo", "bullet", "magazine", "barrel",
                    "silencer", "suppressor", "scope", "bipod", "compensator", "armor", "weapon", "knife",
                    "blade", "baton", "kevlar" },
            // 奢侈品
            new[] { "宝石", "首饰", "项链", "戒指", "钻", "古董", "香水", "艺术", "画", "金条", "奢侈", "丝",
                    "jewel", "gem", "diamond", "antique", "perfume", "luxur", "art", "painting",
                    "gold", "silver", "necklace", "ring", "silk", "velvet" },
            // 电子器件
            new[] { "芯片", "电路", "电脑", "终端", "屏幕", "显示", "手机", "通讯", "摄像", "传感", "硬盘",
                    "数据", "电池", "芯", "机",
                    "chip", "circuit", "computer", "terminal", "screen", "display", "phone", "comm",
                    "radio", "sensor", "camera", "drive", "data", "battery", "cell", "device" }
        };
        // ══════════════ 归类：先读商品自带标签，猜是最后一步 ══════════════
        // 游戏给每件货都贴了类型标签，界面上显示的就是这批词（食品 / 工具 / 模组 / 化学用品 / 奢侈品…）：
        //   item.GetGameItemType()               → 这件货带的类型常量（小写串，可能不止一个）
        //   TypeHelper.GetTypeDisplayName(常量)   → 常量对应的显示名，跟界面同一份本地化文案
        //   TypeHelper.ALL_TYPES_LIST            → 全量常量表，第一次有成交时抄一份打进日志便于核对
        // 所以「这是哪门子货」直接问游戏，不用拿商品名去猜有没有「药」字。
        // 下面四层是兜底：读不到标签才依次退回 目录表 → 类型串 → 关键词 → 货值。
        /// <summary>商品自带标签 → 需求口（中英都收，小写子串匹配），下标即需求口。</summary>
        private static readonly string[][] LabelKeys =
        {
            // 餐饮饮品
            new[] { "食品", "食物", "饮品", "饮料", "食材", "酒", "水",
                    "food", "drink", "beverage", "water", "ingredient", "alcohol", "crop", "meal", "ration" },
            // 日用百货
            new[] { "工具", "材料", "日用品", "日用", "容器", "家具", "生活用品", "杂物", "零件",
                    "tool", "material", "household", "container", "furniture", "utility", "fixture" },
            // 医药耗材
            new[] { "医疗", "医药", "药品", "药物", "药", "化学", "试剂", "物质",
                    "medical", "med", "chemical", "chem", "substance", "pharma", "drug" },
            // 军械装备
            new[] { "武器", "军械", "军火", "枪", "弹药", "护甲", "爆炸物", "冷兵器",
                    "weapon", "gun", "firearm", "ammo", "armor", "melee", "explosive" },
            // 奢侈品
            new[] { "奢侈", "珠宝", "首饰", "古董", "艺术品", "收藏品",
                    "luxury", "luxur", "jewel", "gem", "antique", "perfume", "collectible" },
            // 电子器件
            new[] { "模组", "模块", "电子", "芯片", "电路", "机械", "仪器",
                    "module", "electronic", "chip", "circuit", "device", "machine", "battery", "tech" }
        };

        /// <summary>标签命中顺序：先具体后兜底（材料/工具这种最宽泛的标签压到最后）。</summary>
        private static readonly int[] LabelOrder = { 4, 2, 3, 5, 0, 1 };
        /// <summary>类型常量 → 显示名（界面同款文案）；读不到记空串，不再重复问。</summary>
        private static readonly Dictionary<string, string> _labelCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>类型表只在第一次有成交时抄一遍打进日志。</summary>
        private static bool _vocabTried;

        /// <summary>游戏目录 → 需求口（标签读不到时的第一层兜底）。</summary>
        private static readonly Dictionary<string, int> DirBucket = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "FoodItemDirectory", 0 }, { "WineDirectory", 0 }, { "HydroponicDirectory", 0 }, { "HusbandryDirectory", 0 },
            { "MedsItemDirectory", 2 }, { "OrganDirectory", 2 },
            { "GunsItemDirectory", 3 }, { "GunModDirectory", 3 }, { "MeleeWeaponItemDirectory", 3 },
            { "ExplosiveItemDirectory", 3 }, { "ArmorItemDirectory", 3 },
            { "MaterialDirectory", 1 }, { "ConstructionItemDirectory", 1 }, { "ToolDirectory", 1 },
            { "ContainerItemDirectory", 1 }, { "FurnitureItemDirectory", 1 }, { "AmenitiesItemDirectory", 1 },
            { "TechnicianBackpackDirectory", 1 }, { "KeyItemDirectory", 1 }, { "MiscItemDirectory", 1 },
            { "EquipmentDirectory", 1 }, { "UnusedDirectory", 1 },
            { "ModuleDirectory", 5 }, { "ModItemDirectory", 5 }, { "ShipItemDirectory", 5 },
            { "ShipSystemDirectory", 5 }, { "StationMachinery", 5 }, { "RuinedMachineDirectory", 5 }
        };

        /// <summary>奢侈品没有专属目录（珠宝古董都堆在杂项里），先用类型串把它挑出来。</summary>
        private static readonly string[] LuxuryTokens =
        {
            "luxur", "jewel", "gem", "diamond", "antique", "perfume", " art", "painting",
            "gold", "silver", "necklace", "ring", "silk", "velvet", "high_value", "valuable"
        };

        /// <summary>游戏 itemType 串 → 需求口（模组自造货没进目录时用；按子串匹配）。</summary>
        private static readonly string[][] TypeBucketKeys =
        {
            new[] { "food", "ingredient", "meal", "meat", "crop", "plant", "seed", "drink", "beverage", "water",
                    "wine", "beer", "alcohol", "liquid", "nutrient", "consumable_food" },
            new[] { "household", "material", "tool", "container", "furniture", "amenit", "scrap", "junk", "utility",
                    "construction", "paper", "cloth", "textile", "fixture", "decoration" },
            new[] { "med", "drug", "pharma", "chem", "reagent", "stim", "organ", "heal", "syringe", "antibiotic", "toxin" },
            new[] { "weapon", "gun", "ammo", "firearm", "melee", "explosive", "armor", "protection", "baton", "blade", "military" },
            new[] { "luxur", "jewel", "antique", "perfume", "collectible", "artifact" },
            new[] { "electronic", "module", "chip", "circuit", "device", "machine", "battery", "terminal", "sensor", "robot", "tech" }
        };

        /// <summary>identifier → 需求口。第一次用到时从游戏目录整表建一次，之后只查表。</summary>
        private static Dictionary<string, int> _idBucket;
        private static bool _dirTried;
        private static readonly Dictionary<string, int> _idCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>兜底关键词的匹配顺序：先具体后宽泛，避免「弹药显示器」被当成电子货。</summary>
        private static readonly int[] MatchOrder = { 3, 2, 4, 5, 0, 1 };
        /// <summary>归不进口子时按货值兜底：贵的算奢侈，其余算日用。</summary>
        private const double LuxValueLine = 400.0;

        /// <summary>口子之间的连带关系（客流外溢 / 军需配套 / 上层消费带动）。</summary>
        private static readonly int[][] Neighbor =
        {
            new[] { 1, 4 },   // 餐饮饮品 → 日用百货 / 奢侈品
            new[] { 0, 5 },   // 日用百货 → 餐饮饮品 / 电子器件
            new[] { 3, 0 },   // 医药耗材 → 军械装备 / 餐饮饮品
            new[] { 2, 5 },   // 军械装备 → 医药耗材 / 电子器件
            new[] { 0, 1 },   // 奢侈品 → 餐饮饮品 / 日用百货
            new[] { 3, 1 }    // 电子器件 → 军械装备 / 日用百货
        };

        /// <summary>全表 26 支（「零售全面回暖」这种全站事件要一支不落地点到）。</summary>
        private static readonly string[] AllIds =
        {
            "LL", "KFC", "AGRI", "PEPS", "MID", "HW",
            "TOUR", "HOTEL", "UL", "CHAN", "ENER", "BMW", "BENZ", "APPL",
            "SEC", "RAY", "BOEI", "BANK",
            "BM", "FORGE", "GUN", "CART", "DRUG",
            "RMED", "RCEL", "REV"
        };

        // ══════════════ 违禁通道 ══════════════
        private static readonly string[] CrimeFactionKeys = { "crime", "black", "cartel" };
        private static readonly string[] CrimeKeys = { "违禁", "赃", "毒", "contraband", "stolen", "black market" };
        private static readonly string[] CrimeStockIds = { "BM", "FORGE", "GUN", "CART", "DRUG" };
        private static readonly HashSet<string> CrimeStocks = new HashSet<string>(CrimeStockIds);

        // ══════════════ 事件表 ══════════════
        // 这些事件**不进随机抽取池**（StockDefs.Events 里没有它们，权重是 0 也不会被抽到）：
        // 它们是玩家自己经营出来的，只由本模块按多日检测的结果定向挂上去。
        // StockDefs.GetEvent 会回落到这里查，所以价格冲击、事件公告页、夜间报告
        // 全部照常工作，不需要额外接线。
        public static readonly StockEventDef[] Events = BuildEvents();
        private static readonly Dictionary<string, StockEventDef> EventIndex = BuildIndex();

        /// <summary>按 Id 找本模块的事件；找不到返回 null。</summary>
        public static StockEventDef FindEvent(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            StockEventDef d;
            return EventIndex.TryGetValue(id, out d) ? d : null;
        }

        private static string HotId(int b) { return "ECON_HOT_" + b; }
        private static string StaleId(int b) { return "ECON_STALE_" + b; }
        private static string SurgeId(int b) { return "ECON_SURGE_" + b; }
        private static string InflateId(int b) { return "ECON_INFLATE_" + b; }
        private static string ColdId(int b) { return "ECON_CUT_" + b; }
        private static string SpillId(int b) { return "ECON_SPILL_" + b; }
        public const string RaidDownId = "ECON_RAID_BM";
        public const string RaidUpId = "ECON_RAID_SEC";
        public const string BoomId = "ECON_BOOM";
        public const string CrimeHotId = "ECON_CRIME_HOT";

        private static StockEventDef[] BuildEvents()
        {
            List<StockEventDef> list = new List<StockEventDef>();
            for (int b = 0; b < BucketCount; b++)
            {
                string n = BucketName[b];
                list.Add(new StockEventDef
                {
                    Id = HotId(b), Name = n + "需求走强", Weight = 0, MinDays = 3, MaxDays = 4,
                    Impact = HotImpact, Targets = BucketStocks[b],
                    Summary = "站内零售端连续多日抢购" + n + "，批发商开始加价补货，相关企业营收预期上修。"
                });
                list.Add(new StockEventDef
                {
                    Id = StaleId(b), Name = n + "货源积压", Weight = 0, MinDays = 3, MaxDays = 3,
                    Impact = StaleImpact, Targets = BucketStocks[b],
                    Summary = "连续多日大量" + n + "涌入市场，零售端消化不动，批发价回落，相关企业被下调预期。"
                });
                list.Add(new StockEventDef
                {
                    Id = SurgeId(b), Name = n + "抢购潮", Weight = 0, MinDays = 2, MaxDays = 2,
                    Impact = SurgeImpact, Targets = BucketStocks[b],
                    Summary = "一天之内大批" + n + "被扫空，批发端当天就抬价补货，相关企业营收预期上修。"
                });
                list.Add(new StockEventDef
                {
                    Id = InflateId(b), Name = n + "价格通胀", Weight = 0, MinDays = 3, MaxDays = 4,
                    Impact = InflateImpact, Targets = BucketStocks[b],
                    Summary = "站里" + n + "的出货量长期堆得太高，批发价一路走高，相关企业毛利改善。"
                });
                list.Add(new StockEventDef
                {
                    Id = ColdId(b), Name = n + "供应中断", Weight = 0, MinDays = 2, MaxDays = 2,
                    Impact = ColdImpact, Targets = BucketStocks[b],
                    Summary = "零售端已经好些天没见到" + n + "上架，老主顾转去别家，相关企业订单预期被下调。"
                });
                list.Add(new StockEventDef
                {
                    Id = SpillId(b), Name = n + "连带景气", Weight = 0, MinDays = 2, MaxDays = 2,
                    Impact = SpillImpact, Targets = BucketStocks[b],
                    Summary = n + "的抢购把人流带到了隔壁摊位，连带生意一起好起来。"
                });
            }
            list.Add(new StockEventDef
            {
                Id = BoomId, Name = "零售全面回暖", Weight = 0, MinDays = 2, MaxDays = 2,
                Impact = BoomImpact, Targets = AllIds,
                Summary = "站里好几个板块同时走货旺，零售端整体回暖，全站买卖都活络了起来。"
            });
            list.Add(new StockEventDef
            {
                Id = CrimeHotId, Name = "黑市货源走俏", Weight = 0, MinDays = 2, MaxDays = 2,
                Impact = CrimeHotImpact, Targets = new[] { "BM", "FORGE", "GUN", "CART", "DRUG" },
                Summary = "你店里出手的货还没引来人查，黑市上已经有人开始打听下批货源了。"
            });
            list.Add(new StockEventDef
            {
                Id = RaidDownId, Name = "治安部专项巡查", Weight = 0, MinDays = 2, MaxDays = 3,
                Impact = RaidDownImpact, Targets = new[] { "BM", "FORGE", "GUN", "CART", "DRUG" },
                Summary = "你店里出手的违禁货数量太大，治安部顺藤摸瓜盯上了这条线，黑市商户集体停业避险。"
            });
            list.Add(new StockEventDef
            {
                Id = RaidUpId, Name = "治安部行动经费加码", Weight = 0, MinDays = 2, MaxDays = 3,
                Impact = RaidUpImpact, Targets = new[] { "SEC", "RAY", "BOEI" },
                Summary = "巡查行动加码，治安部紧急追加装备与行动经费，军工企业订单看涨。"
            });
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

        // ══════════════ 运行时状态 ══════════════
        private static int _day = -1;                              // 累计值属于哪一天
        private static readonly double[] _value = new double[BucketCount];
        private static readonly double[] _applied = new double[BucketCount];  // 已经并进收盘价的快照
        private static readonly int[] _streak = new int[BucketCount];
        private static readonly int[] _cool = new int[BucketCount];
        private static readonly int[] _coolStale = new int[BucketCount];
        private static double _crimeValue;
        private static double _appliedCrime;
        private static int _crimeStreak;
        private static int _crimeCool;
        // ── 第二批事件的账本 ──
        private static readonly double[] _total = new double[BucketCount];     // 累计货值（跨天不清）
        private static readonly int[] _inflate = new int[BucketCount];         // 通胀事件已经报过几次
        private static readonly bool[] _ever = new bool[BucketCount];          // 这个口子旺过没有
        private static readonly int[] _cold = new int[BucketCount];            // 连续几天没开张
        private static readonly int[] _coolSurge = new int[BucketCount];
        private static readonly int[] _coolInflate = new int[BucketCount];
        private static readonly int[] _coolCold = new int[BucketCount];
        private static readonly int[] _coolSpill = new int[BucketCount];
        private static int _boomCool;
        private static int _crimeHotCool;
        /// <summary>各口子门槛的价格折算系数，按天刷新（用收盘价算，日内不抖）。</summary>
        private static readonly double[] _pfactor = new double[BucketCount];
        /// <summary>系数算的是哪一天，避免日内反复重算。</summary>
        private static int _pfactorDay = -999;
        /// <summary>违禁货那条线用的系数（黑市五支的均价）。</summary>
        private static double _crimeFactor = 1.0;
        /// <summary>各口子的产能：平时一天走多少货（元）。每天结算时往今天的货值靠 PaceAlpha 一步。</summary>
        private static readonly double[] _avg = new double[BucketCount];
        private static double _avgCrime;
        /// <summary>产能吃过兜底之后的结果，门槛与脉冲都用它；跟价格系数一样按天缓存。</summary>
        private static readonly double[] _pace = new double[BucketCount];
        private static double _paceCrime;
        /// <summary>喂给宏观物价层的折算货值（今天相当于平时多少，按 MacroRefPace 归一），免得天天新开数组。</summary>
        private static readonly double[] _normBuf = new double[BucketCount];
        /// <summary>最近 StaleWindow 天的货值；[b,0] 是今天，[b,1] 昨天……用来判「累计放量」。</summary>
        private static readonly double[,] _win = new double[BucketCount, StaleWindow];
        /// <summary>当天已经记过账的 item.uniqueId，两个钩子同时命中时靠它去重。</summary>
        private static readonly HashSet<int> _seen = new HashSet<int>();
        /// <summary>当天已经确认是「买进来的」item.uniqueId：这类货不许再算成需求。</summary>
        private static readonly HashSet<int> _boughtToday = new HashSet<int>();
        /// <summary>今天已经原文打印过多少笔卖出 / 买入流水（每天结算时归零）。</summary>
        private static int _sellLogged;
        private static int _buyLogged;

        private static readonly Dictionary<string, int[]> Member = BuildMembers();
        private static readonly HashSet<string> Logged = new HashSet<string>();

        private static Dictionary<string, int[]> BuildMembers()
        {
            Dictionary<string, List<int>> tmp = new Dictionary<string, List<int>>();
            for (int b = 0; b < BucketCount; b++)
            {
                for (int i = 0; i < BucketStocks[b].Length; i++)
                {
                    string id = BucketStocks[b][i];
                    List<int> list;
                    if (!tmp.TryGetValue(id, out list)) { list = new List<int>(); tmp[id] = list; }
                    list.Add(b);
                }
            }
            Dictionary<string, int[]> map = new Dictionary<string, int[]>();
            foreach (KeyValuePair<string, List<int>> kv in tmp) map[kv.Key] = kv.Value.ToArray();
            return map;
        }

        // ══════════════ 采集 ══════════════
        // 方向约定（游戏这几个钩子的名字是站在「客人」角度写的，很容易接反）：
        //   客人把货卖给你 → 玩家买入 → StoreClient.OnItemSold / PlayerStore.BuyItem / PlayerStore.OnItemBought
        //   客人把你的货买走 → 玩家卖出 → StoreClient.OnItemBought / PlayerStore.SellItem /
        //                                 PlayerStore.PerformOnSoldCheck / PlayerStore.OnItemsSold
        // 只有「卖出」才加需求；「买入」只打一条日志、把 uid 记进 _boughtToday。
        // 这样即使某个 Sold 名字的钩子其实跑在买入路径上，那件货也会被挡在门外，不会把进货算成生意。

        /// <summary>玩家一次卖掉一批（PlayerStore.OnItemsSold 钩子）。没有买家身份，走通用归类。</summary>
        public static void SoldBatch(Il2CppSystem.Collections.Generic.List<GameItem> items, string tag)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++) Sold(items[i], null, 0L, tag);
        }

        /// <summary>玩家卖出（客人买走）。amount 传成交额时以成交额为准。</summary>
        public static void Sold(GameItem item, StoreClient client, long amount, string tag)
        {
            try
            {
                if (item == null || !StockState.Loaded) return;

                int uid = Uid(item);
                if (uid != 0 && _boughtToday.Contains(uid))
                {
                    // 这笔是刚买进来的货，算需求等于自己给自己放水。但要说一声：
                    // 之前这里只写 Verbose 日志，玩家卖了自己刚收的货会以为整条记账坏了。
                    Log(TextOf(item), 0, ValueOf(item, amount), false, "卖出", tag, null, false,
                        "今天刚从客人手里买进来的货，不计需求");
                    return;
                }
                if (uid != 0 && !_seen.Add(uid)) return;     // 两个钩子都报了同一笔

                double value = ValueOf(item, amount);
                if (value < 1.0) return;

                string text = TextOf(item);
                string how;
                int b = Classify(item, text, value, out how);
                string flag = DirFlag(client);
                if (flag != null) how += "｜" + flag;
                _day = StockState.Today;
                _value[b] += value;
                _total[b] += value;
                bool crime = IsCrime(item, client, text);
                if (crime) _crimeValue += value;
                StockState.Dirty = true;

                Log(text, b, value, crime, "卖出", tag, how);
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[实体] 记账失败：" + ex.Message);
            }
        }

        /// <summary>玩家买入（客人卖给你 / 下单进货）：只留个痕，不进需求。</summary>
        public static void Bought(GameItem item, StoreClient client, long amount, string tag)
        {
            try
            {
                if (item == null || !StockState.Loaded) return;
                int uid = Uid(item);
                if (uid != 0) _boughtToday.Add(uid);

                double value = ValueOf(item, amount);
                Log(TextOf(item), 0, value, false, "买入", tag, DirFlag(client), true);
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[实体] 买入留痕失败：" + ex.Message);
            }
        }

        private static int Uid(GameItem item)
        {
            try { return item.uniqueId; } catch { return 0; }
        }

        /// <summary>
        /// 钩子方向自查：游戏这几个方法名是站在「客人」角度写的，很容易接反。
        /// 有客人对象时把那一刻客人自己的买卖标记抄进日志，一眼就能看出方向对不对。
        /// </summary>
        private static string DirFlag(StoreClient client)
        {
            if (client == null) return null;
            try
            {
                return "客人标记 买" + (client.IsClientBuying() ? "√" : "×")
                     + "卖" + (client.IsClientSelling() ? "√" : "×");
            }
            catch { return null; }
        }

        /// <summary>卖出流水每天至少原文打印这么多笔（超出部分降到 Verbose，不影响记账）。</summary>
        private const int LogSellQuota = 12;
        /// <summary>买入流水每天只留几笔，且同一件货一局只打一次（进货本来就不进需求）。</summary>
        private const int LogBuyQuota = 4;

        /// <summary>
        /// 交易流水。买卖各配一份额度、每天结算时归零，额度内直接进日志（不用开 Verbose）：
        ///   卖出 —— 每天前 12 笔，玩家最想知道「这笔到底记上没有」
        ///   买入 —— 每天前 4 笔，且同一件货一局只打一次，免得一条牙膏刷三行
        /// 额度用完的照常记账，只是降到 Verbose 级。
        /// note 用于「记账但不算需求」这类要单独说明的流水。
        /// </summary>
        private static void Log(string text, int bucket, double value, bool crime, string dir, string tag, string how,
                                bool bought = false, string note = null)
        {
            string line = "【" + dir + "·" + tag + "】「" + text + "」货值 " + value.ToString("0.##") + " 元";
            if (note != null) line += "（" + note + "）";
            else if (bought) line += "（进货，不计需求" + (string.IsNullOrEmpty(how) ? "" : "，" + how) + "）";
            else
            {
                line += " → " + BucketName[bucket];
                if (!string.IsNullOrEmpty(how)) line += "（" + how + "）";
                if (crime) line += "｜违禁";
            }

            if (bought)
            {
                if (Logged.Add("买" + text) && _buyLogged < LogBuyQuota) { _buyLogged++; Core.Log.Msg("[实体] " + line); }
                else Core.Debug("[实体] " + line);
                return;
            }
            if (note != null)
            {
                // 这种流水一件货只报一次，不占卖出额度
                if (Logged.Add("卖" + text)) Core.Log.Msg("[实体] " + line);
                else Core.Debug("[实体] " + line);
                return;
            }
            if (_sellLogged < LogSellQuota) { _sellLogged++; Core.Log.Msg("[实体] " + line); }
            else if (Logged.Add("卖" + text)) Core.Debug("[实体] " + line);
        }

        private static long SafeUnitValue(GameItem item)
        {
            try { return item.unitValue; } catch { return 0L; }
        }

        private static double ValueOf(GameItem item, long soldAmount)
        {
            double v = 0.0;
            try
            {
                int n = item.unitCount;
                if (n < 1) n = 1;
                v = (double)SafeUnitValue(item) * n;
            }
            catch { }
            double s = soldAmount < 0 ? -soldAmount : soldAmount;
            if (s > v) v = s;      // 钩子给的是成交额时以它为准，给的是单价时按货值算
            return v;
        }

        /// <summary>商品显示名 + 自带标签，小写化后供日志与兜底匹配。</summary>
        private static string TextOf(GameItem item)
        {
            string name = "";
            try { name = item.GetDisplayName(false) ?? ""; } catch { }
            string labels = LabelText(item);
            return labels.Length > 0 ? (name + labels).ToLowerInvariant() : name.ToLowerInvariant();
        }

        /// <summary>游戏给这件货打的类型串（小写，前面带空格）。</summary>
        private static string TokenText(GameItem item)
        {
            try
            {
                Il2CppSystem.Collections.Generic.List<string> types = item.GetGameItemType();
                if (types == null || types.Count == 0) return "";
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < types.Count; i++)
                {
                    string s = types[i];
                    if (!string.IsNullOrEmpty(s)) sb.Append(' ').Append(s);
                }
                return sb.ToString().ToLowerInvariant();
            }
            catch { return ""; }
        }

        private static string IdOf(GameItem item)
        {
            try { return item.identifier; } catch { return null; }
        }

        /// <summary>
        /// 这件货算哪个需求口。优先级：自带标签 → 目录 → 类型串 → 关键词 → 货值。
        /// how 里带回判断依据（标签会把命中的那个词一起写出来），日志里会打出来，接错了能一眼看见。
        /// </summary>
        private static int Classify(GameItem item, string text, double value, out string how)
        {
            if (!_vocabTried) DumpTypeVocab();

            string id = IdOf(item);
            string tokens = TokenText(item);
            string labels = LabelText(item);
            int b = value >= LuxValueLine ? 4 : 1;
            how = "按货值";

            int cached;
            if (!string.IsNullOrEmpty(id) && _idCache.TryGetValue(id, out cached))
            {
                how = "缓存";
                return cached;
            }

            string hit;
            if (labels.Length > 0 && LabelBucket(labels, out b, out hit)) { how = "标签·" + hit; }
            else if (tokens.Length > 0 && HitAny(tokens, LuxuryTokens)) { b = 4; how = "类型·奢侈"; }
            else if (!string.IsNullOrEmpty(id) && DirLookup(id, out b)) { how = "目录"; }
            else if (tokens.Length > 0 && TypeBucket(tokens, out b)) { how = "类型"; }
            else if (KeywordBucket(text, out b)) { how = "关键词"; }

            if (!string.IsNullOrEmpty(id)) _idCache[id] = b;
            return b;
        }

        /// <summary>类型常量 → 界面上的显示名（食品/工具/模组/化学用品/奢侈品…）；读不到返回空串。</summary>
        private static string LabelOf(string typeConst)
        {
            if (string.IsNullOrEmpty(typeConst)) return "";
            string cached;
            if (_labelCache.TryGetValue(typeConst, out cached)) return cached;

            string label = "";
            try { label = TypeHelper.GetTypeDisplayName(typeConst) ?? ""; } catch { }
            if (string.Equals(label, typeConst, StringComparison.OrdinalIgnoreCase)) label = "";
            _labelCache[typeConst] = label;
            return label;
        }

        /// <summary>这件货自带的全部标签（常量 + 显示名，小写，前面带空格）。</summary>
        private static string LabelText(GameItem item)
        {
            try
            {
                Il2CppSystem.Collections.Generic.List<string> types = item.GetGameItemType();
                if (types == null || types.Count == 0) return "";
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < types.Count; i++)
                {
                    string c = types[i];
                    if (string.IsNullOrEmpty(c)) continue;
                    sb.Append(' ').Append(c);
                    string label = LabelOf(c);
                    if (label.Length > 0) sb.Append(' ').Append(label);
                }
                return sb.ToString().ToLowerInvariant();
            }
            catch { return ""; }
        }

        /// <summary>标签 → 需求口，LabelOrder 顺序命中即返回；hit 带回命中的那个词。</summary>
        private static bool LabelBucket(string labels, out int bucket, out string hit)
        {
            for (int k = 0; k < LabelOrder.Length; k++)
            {
                int b = LabelOrder[k];
                string[] keys = LabelKeys[b];
                for (int i = 0; i < keys.Length; i++)
                    if (labels.IndexOf(keys[i], StringComparison.Ordinal) >= 0) { bucket = b; hit = keys[i]; return true; }
            }
            bucket = -1;
            hit = null;
            return false;
        }

        /// <summary>
        /// 第一次有成交时把游戏的全量类型表（常量 = 界面显示名）打进日志，
        /// 方便核对映射；读不到就静默退回目录/关键词，不影响记账。
        /// </summary>
        private static void DumpTypeVocab()
        {
            if (_vocabTried) return;
            _vocabTried = true;
            try
            {
                Il2CppSystem.Collections.Generic.List<string> all = TypeHelper.ALL_TYPES_LIST;
                if (all == null || all.Count == 0)
                {
                    Core.Debug("[实体] 游戏类型表读不到，归类改用目录/关键词兜底");
                    return;
                }
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < all.Count; i++)
                {
                    string c = all[i];
                    if (string.IsNullOrEmpty(c)) continue;
                    sb.Append(c).Append('=').Append(LabelOf(c).Length > 0 ? LabelOf(c) : "?").Append("  ");
                }
                Core.Log.Msg("[实体] 游戏商品类型表 " + all.Count + " 项：" + sb.ToString().TrimEnd());
            }
            catch (Exception ex) { Core.Debug("[实体] 类型表读取失败：" + ex.Message); }
        }

        private static bool HitAny(string text, string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
                if (text.IndexOf(keys[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>类型串 → 需求口（TypeBucketKeys 的下标就是需求口）。</summary>
        private static bool TypeBucket(string tokens, out int bucket)
        {
            for (int b = 0; b < TypeBucketKeys.Length; b++)
            {
                string[] keys = TypeBucketKeys[b];
                for (int i = 0; i < keys.Length; i++)
                    if (tokens.IndexOf(keys[i], StringComparison.Ordinal) >= 0) { bucket = b; return true; }
            }
            bucket = -1;
            return false;
        }

        private static bool KeywordBucket(string text, out int bucket)
        {
            if (!string.IsNullOrEmpty(text))
            {
                for (int k = 0; k < MatchOrder.Length; k++)
                {
                    int b = MatchOrder[k];
                    string[] keys = BucketKeys[b];
                    for (int i = 0; i < keys.Length; i++)
                        if (text.IndexOf(keys[i], StringComparison.Ordinal) >= 0) { bucket = b; return true; }
                }
            }
            bucket = -1;
            return false;
        }

        /// <summary>查游戏目录表；没进目录（模组自造货）返回 false。</summary>
        private static bool DirLookup(string id, out int bucket)
        {
            BuildIdBucket();
            if (_idBucket != null && _idBucket.TryGetValue(id, out bucket)) return true;
            bucket = -1;
            return false;
        }

        /// <summary>
        /// 把游戏各目录下的商品 id 全部抄一份，整局只做一次（第一次有成交时才建）。
        /// 抄不到就退回类型串/关键词，不会因为某个目录改名就把整条记账链断掉。
        /// </summary>
        private static void BuildIdBucket()
        {
            if (_dirTried) return;
            _dirTried = true;

            Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int ok = 0, fail = 0;
            foreach (KeyValuePair<string, int> kv in DirBucket)
            {
                try
                {
                    Il2CppSystem.Collections.Generic.List<string> ids = DirectoryMaster.GetIdentifierList<GameItem>(kv.Key);
                    if (ids == null) { fail++; continue; }
                    for (int i = 0; i < ids.Count; i++)
                    {
                        string one = ids[i];
                        if (string.IsNullOrEmpty(one) || map.ContainsKey(one)) continue;
                        map[one] = kv.Value;
                    }
                    ok++;
                }
                catch { fail++; }
            }
            _idBucket = map;

            if (map.Count == 0)
            {
                // 目录还没初始化好（比如刚进游戏），这次不算数，下次成交再试
                _dirTried = false;
                Core.Log.Warning("[实体] 商品分类表本轮没抄到东西，先走类型串/关键词，下次成交重试。");
                return;
            }
            _idCache.Clear();   // 表来了，之前靠兜底算出来的结论作废，重新按目录认

            StringBuilder line = new StringBuilder();
            for (int b = 0; b < BucketCount; b++)
            {
                int n = 0;
                foreach (KeyValuePair<string, int> kv in map)
                    if (kv.Value == b) n++;
                if (n > 0) line.Append(BucketName[b]).Append(' ').Append(n).Append(" / ");
            }
            Core.Log.Msg("[实体] 商品分类表就绪：目录 " + ok + "/" + (ok + fail) + "，商品 " + map.Count
                + " 件（" + line.ToString().TrimEnd(' ', '/') + "）；没进目录的货走类型串/关键词兜底。");
        }

        private static bool IsCrime(GameItem item, StoreClient client, string text)
        {
            try
            {
                if (client != null)
                {
                    string f = null;
                    try { f = client.clientFaction; } catch { }
                    if (!string.IsNullOrEmpty(f))
                    {
                        for (int i = 0; i < CrimeFactionKeys.Length; i++)
                        {
                            if (f.IndexOf(CrimeFactionKeys[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        }
                    }
                    try { if (client.IsSellingContraband()) return true; } catch { }
                    try { if (client.IsSellingStolen()) return true; } catch { }
                }
            }
            catch { }
            for (int i = 0; i < CrimeKeys.Length; i++)
            {
                if (text.IndexOf(CrimeKeys[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // ══════════════ 对外读数（物价层 / 界面用）══════════════

        /// <summary>这个需求口叫什么。</summary>
        public static string BucketLabel(int b)
        {
            return b >= 0 && b < BucketCount ? BucketName[b] : "?";
        }

        /// <summary>这个需求口挂着哪几支标的。</summary>
        public static string[] BucketMembers(int b)
        {
            return b >= 0 && b < BucketCount ? BucketStocks[b] : null;
        }

        /// <summary>这支标的挂在哪几个需求口上（可能在不止一个口子里）。</summary>
        public static int[] BucketsOf(string id)
        {
            int[] bs;
            if (!string.IsNullOrEmpty(id) && Member.TryGetValue(id, out bs)) return bs;
            return null;
        }

        /// <summary>这个口子今天卖出去多少（元）。</summary>
        public static double TodayValue(int b)
        {
            return b >= 0 && b < BucketCount ? _value[b] : 0.0;
        }

        /// <summary>这个口子累计卖出去多少（元）。</summary>
        public static double TotalValue(int b)
        {
            return b >= 0 && b < BucketCount ? _total[b] : 0.0;
        }

        /// <summary>
        /// 这件货算哪个需求口。给「物价乘数」用：读档前或算不出来时返回 -1。
        /// </summary>
        public static int BucketOf(GameItem item)
        {
            try
            {
                if (item == null) return -1;
                string how;
                return Classify(item, TextOf(item), ValueOf(item, 0L), out how);
            }
            catch { return -1; }
        }

        /// <summary>
        /// 这件货按当前的物价水平该卖几倍价（夹在 0.92~1.12）。
        /// 归不进口子就返回 1.0，不影响原来的定价。
        /// </summary>
        public static double PriceMulOf(GameItem item)
        {
            int b = BucketOf(item);
            return b < 0 ? 1.0 : StockMacro.PriceMul(b);
        }

        // ══════════════ 门槛折算（产能 × 价格兜底）══════════════

        /// <summary>
        /// 刷新六个口子（以及违禁货那条线）的价格系数与产能。
        /// 价格用收盘价算、产能只在结算时动，所以一天之内不会抖；每天结算时重算一次。
        /// </summary>
        private static void RefreshPriceFactor()
        {
            if (_pfactorDay == StockState.Today) return;
            _pfactorDay = StockState.Today;
            for (int b = 0; b < BucketCount; b++)
            {
                _pfactor[b] = PriceFactorOf(BucketStocks[b]);
                _pace[b] = PaceOf(_avg[b], _pfactor[b]);
            }
            _crimeFactor = PriceFactorOf(CrimeStockIds);
            _paceCrime = PaceOf(_avgCrime, _crimeFactor);
        }

        /// <summary>
        /// 产能：平时一天走多少货（元）。还没卖出过东西时按「起步基准 × 口子价格系数」兜底，
        /// 免得新档期门槛趴在地上、卖两件货就报积压。
        /// </summary>
        private static double PaceOf(double avg, double pf)
        {
            double floor = PaceDayBase * pf;
            return avg > floor ? avg : floor;
        }

        /// <summary>把今天的货值并进产能。头一次有货值直接拿它当起点，之后按 PaceAlpha 慢慢靠。</summary>
        private static double Learn(double avg, double today)
        {
            if (today <= 0.0) return avg * (1.0 - PaceAlpha);   // 空一天，产能跟着缩一点
            if (avg <= 0.0) return today;
            return avg + (today - avg) * PaceAlpha;
        }

        /// <summary>一组标的的均价 ÷ 基准价，夹在 PriceFactorMin~PriceFactorMax。</summary>
        private static double PriceFactorOf(string[] ids)
        {
            if (ids == null || ids.Length == 0) return 1.0;
            double sum = 0.0;
            int n = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                long cents = StockState.GetPrice(ids[i]);
                if (cents > 0) { sum += StockState.ToYuan(cents); n++; }
            }
            if (n == 0) return 1.0;
            double f = sum / n / PriceRef;
            if (f < PriceFactorMin) f = PriceFactorMin;
            if (f > PriceFactorMax) f = PriceFactorMax;
            return f;
        }

        // 门槛一律写成「产能 × 倍数」：产能是玩家自己平时走多快，所以前期门槛低、后期门槛高。

        /// <summary>「今天这个口子算走货旺」的当日门槛。</summary>
        private static double HotLine(int b) { return _pace[b] * HotMul; }
        /// <summary>「今天这个口子算没开张」的当日门槛。</summary>
        private static double ColdLine(int b) { return _pace[b] * ColdMul; }
        /// <summary>单日「抢购潮」的当日门槛。</summary>
        private static double SurgeLine(int b) { return _pace[b] * SurgeMul; }
        /// <summary>最近几天累计到这个量算「货源积压」。</summary>
        private static double StaleLine(int b) { return HotLine(b) * StaleMul; }
        /// <summary>累计货值到「一份」的通胀线。</summary>
        private static double InflateLine(int b) { return _pace[b] * InflateMul; }
        /// <summary>违禁货「被盯上」的当日线。</summary>
        private static double CrimeRaidLine { get { return _paceCrime * CrimeRaidMul; } }
        /// <summary>违禁货「货源走俏」的当日线。</summary>
        private static double CrimeHotLine { get { return _paceCrime * CrimeHotMul; } }
        /// <summary>脉冲的双曲标尺：走平时两天的量，脉冲走到一半。</summary>
        private static double PulseScaleOf(int b) { return _pace[b] * PulseMul; }
        private static double PulseScaleCrime { get { return _paceCrime * PulseMul; } }

        // ══════════════ 脉冲 ══════════════

        /// <summary>
        /// 店铺需求把今天的盘口价抬高多少倍（1.0 = 没影响）。
        /// 返回的是「相对已并入收盘价那一份的增量」：收工并过一次之后，
        /// 只要当天没有新成交，这里就回到 1.0，不会因为重复调用而越叠越高。
        /// </summary>
        public static double Factor(string id)
        {
            if (string.IsNullOrEmpty(id)) return 1.0;
            if (_day != StockState.Today) return 1.0;   // 累计值不属于今天，一律不认

            int[] buckets;
            if (!Member.TryGetValue(id, out buckets)) return 1.0;
            RefreshPriceFactor();

            double now = 0.0, was = 0.0;
            for (int i = 0; i < buckets.Length; i++)
            {
                // NPC 竞争压价：这个口子你卖得越多，别家店铺跟进得越凶，
                // 你自己那笔的边际拉动就越小。两个读数乘的是同一个系数，
                // 所以「已并入 / 未并入」的比值不变，幂等性还在。
                double damp = CompeteDamp(buckets[i]);
                double scale = PulseScaleOf(buckets[i]);
                now += PulseOf(_value[buckets[i]], scale) * damp;
                was += PulseOf(_applied[buckets[i]], scale) * damp;
            }
            if (CrimeStocks.Contains(id))
            {
                double cs = PulseScaleCrime;
                now += PulseOf(_crimeValue, cs);
                was += PulseOf(_appliedCrime, cs);
            }
            if (now > PulseCap) now = PulseCap;
            if (was > PulseCap) was = PulseCap;
            if (now <= was) return 1.0;
            return (1.0 + now) / (1.0 + was);
        }

        /// <summary>货值 → 需求强度（0..PulseCap）。双曲饱和，第一块钱最有效、越卖越钝。</summary>
        private static double PulseOf(double value, double scale)
        {
            if (value <= 0.0) return 0.0;
            if (scale <= 0.0) scale = PulseMul * PaceDayBase;
            return PulseCap * (value / (value + scale));
        }

        /// <summary>
        /// NPC 竞争压价系数（0.45~1.0）：一个口子累计出货越多，说明这门生意越好做，
        /// 别家店铺跟进来得越多，你自己那点货的边际定价权就越小。
        /// 只压「脉冲」（当天行情拉动），不压事件门槛 —— 事件照旧按真实货值判定。
        /// </summary>
        private static double CompeteDamp(int b)
        {
            double scale = _pace[b] * CompeteMul;
            if (scale <= 0.0) scale = PaceDayBase * CompeteMul;
            double d = 1.0 / (1.0 + _total[b] / scale);
            return d < CompeteFloor ? CompeteFloor : d;
        }

        /// <summary>今日店铺景气度的一句话，给界面/日志用。</summary>
        public static string MoodText()
        {
            int top = -1;
            double best = 0.0;
            for (int b = 0; b < BucketCount; b++)
            {
                if (_value[b] > best) { best = _value[b]; top = b; }
            }
            if (top < 0) return "今天还没卖出去什么东西。";
            RefreshPriceFactor();
            return BucketName[top] + "走货 " + best.ToString("0") + " 元，需求 +"
                + (PulseOf(best, PulseScaleOf(top)) * 100.0).ToString("0.0") + "%"
                + (_crimeValue > 0.0 ? "；违禁货 " + _crimeValue.ToString("0") + " 元" : "");
        }

        // ══════════════ 收工：并进收盘价 ══════════════

        /// <summary>
        /// 把今天累计的需求脉冲并进当日收盘价（并按昨收夹在涨跌停内）。
        ///
        /// 这一步不做的话就是白送钱：盘中卖货把报价抬起来，玩家按抬高的价卖掉持仓，
        /// 收工时价格又跳回没抬之前定好的收盘价 —— 等于无风险套利。
        /// 并进去之后价格就地站住，第二天再靠日线的均值回归慢慢消化。
        /// 幂等：并过之后 Factor 回到 1.0，重复调用不会再叠一次。
        /// </summary>
        public static void ApplyToClose()
        {
            try
            {
                if (!StockState.Loaded) return;
                bool changed = false;
                for (int i = 0; i < StockDefs.All.Length; i++)
                {
                    StockDef f = StockDefs.All[i];
                    if (f == null) continue;
                    double factor = Factor(f.Id);
                    if (factor <= 1.000001) continue;

                    double prev = StockState.ToYuan(PrevCloseCents(f.Id));
                    double lim = StockDefs.LimitOf(f);
                    double yuan = StockState.ToYuan(StockState.GetPrice(f.Id)) * factor;
                    if (yuan > prev * (1.0 + lim)) yuan = prev * (1.0 + lim);
                    if (yuan < prev * (1.0 - lim)) yuan = prev * (1.0 - lim);
                    if (yuan < 1.0) yuan = 1.0;

                    StockState.Prices[f.Id] = StockState.ToCents(yuan);
                    List<long> hist;
                    if (StockState.PriceHistory.TryGetValue(f.Id, out hist) && hist != null && hist.Count > 0)
                    {
                        hist[hist.Count - 1] = StockState.Prices[f.Id];
                    }
                    changed = true;
                }

                // 快照：脉冲已经落在价里了，Factor 从这一刻起回到 1.0
                for (int b = 0; b < BucketCount; b++) _applied[b] = _value[b];
                _appliedCrime = _crimeValue;
                if (changed)
                {
                    StockState.Dirty = true;
                    Core.Log.Msg("[实体] 店铺需求已并入收盘价（" + StockState.Today + " 日）。");
                }
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[实体] 并入收盘价失败：" + ex.Message);
            }
        }

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

        // ══════════════ 日结算：多日检测 ══════════════

        /// <summary>
        /// 推进一天：先兜底并入收盘价，再做多日检测，最后翻篇清空当日累计。
        /// 由 StockEngine.DailyTickNews 在收盘价重算之前调用。
        /// </summary>
        public static List<StockEngine.StockNews> Settle()
        {
            List<StockEngine.StockNews> news = new List<StockEngine.StockNews>();
            try
            {
                if (!StockState.Loaded) return news;
                // 门槛系数按天刷新：用今天的收盘价算，六个口子各一个（贵票门槛高、便宜票门槛低）。
                RefreshPriceFactor();
                // 兜底：万一收工那一下没走到（比如整天没开门），这里补上，
                // 否则「今天卖的东西」会漏进明天的收盘价里。
                ApplyToClose();

                ReportDay(news);

                // 宏观物价层：拿今天的实绩去推进六个口子的供需线，判定通胀/通缩/供求状态，
                // 并挂上对应的定向事件。必须排在这份当日累计被清零之前 —— 它要用 _value。
                // 口径先折算：NPC 的买卖盘是常量，玩家绝对货值一路涨上去会把供需线顶爆，
                // 所以喂进去的是「今天相当于平时的多少倍」（乘回 MacroRefPace）。
                for (int b = 0; b < BucketCount; b++)
                {
                    _normBuf[b] = _pace[b] > 0.0 ? _value[b] * MacroRefPace / _pace[b] : _value[b];
                }
                StockMacro.Settle(news, _normBuf);

                int hotBuckets = 0;
                for (int b = 0; b < BucketCount; b++)
                {
                    if (_cool[b] > 0) _cool[b]--;
                    if (_coolStale[b] > 0) _coolStale[b]--;
                    if (_coolSurge[b] > 0) _coolSurge[b]--;
                    if (_coolInflate[b] > 0) _coolInflate[b]--;
                    if (_coolCold[b] > 0) _coolCold[b]--;
                    if (_coolSpill[b] > 0) _coolSpill[b]--;
                    // 滚动窗口：把今天的货值推进去，窗口里是最近 StaleWindow 天（含今天）
                    for (int k = StaleWindow - 1; k > 0; k--) _win[b, k] = _win[b, k - 1];
                    _win[b, 0] = _value[b];
                    double recent = 0.0;
                    for (int k = 0; k < StaleWindow; k++) recent += _win[b, k];

                    bool hot = _value[b] >= HotLine(b);
                    if (hot) hotBuckets++;
                    _streak[b] = hot ? _streak[b] + 1 : 0;
                    // 「今天没开张」的连数：只有以前旺过的口子才允许升级成供应中断，
                    // 从没做过的生意天天挨罚，玩家只会觉得莫名其妙。
                    _cold[b] = _value[b] < ColdLine(b) ? _cold[b] + 1 : 0;

                    // 连着几天走货旺 → 需求走强。
                    // 注意这里**不重置** _streak：热销只是「开始旺」，旺了更久才会积压，
                    // 重置的话连续天数永远停在 3，积压那条路根本走不到。
                    bool hotFired = false;
                    if (hot && _streak[b] >= HotStreak && _cool[b] <= 0)
                    {
                        StockEventDef def = FindEvent(HotId(b));
                        if (def != null && Push(def, news)) { _cool[b] = HotCooldown; hotFired = true; }
                    }
                    // 一个口子旺，隔壁跟着沾光（隔壁 8 天内只连带一次）
                    if (hotFired && Neighbor[b] != null)
                    {
                        for (int k = 0; k < Neighbor[b].Length; k++)
                        {
                            int nb = Neighbor[b][k];
                            if (_coolSpill[nb] > 0) continue;
                            StockEventDef sp = FindEvent(SpillId(nb));
                            if (sp != null && Push(sp, news)) _coolSpill[nb] = SpillCooldown;
                        }
                    }
                    // 单日爆量 → 抢购潮。当天那份脉冲已经在盘里了，这条再给两天持续。
                    // 抢购潮先判：一天就被扫空的货是被市场吃掉了，不该转头又算成积压。
                    bool surgeFired = false;
                    if (_value[b] >= SurgeLine(b) && _coolSurge[b] <= 0)
                    {
                        StockEventDef def = FindEvent(SurgeId(b));
                        if (def != null && Push(def, news)) { _coolSurge[b] = SurgeCooldown; surgeFired = true; }
                    }
                    if (surgeFired) _win[b, 0] = 0.0;   // 这天的货被消化掉了，不进积压窗口
                    // 最近几天累计货值到线 → 货源积压，把这段涨幅收回去一部分。
                    // 这里看的是**累计货值**而不是「连着旺了几天」：一天猛卖顶到线当天就能中，
                    // 断断续续卖满同样的量也算数，不用连着熬好几天。
                    if (!surgeFired && recent >= StaleLine(b) && _coolStale[b] <= 0)
                    {
                        StockEventDef def = FindEvent(StaleId(b));
                        if (def != null && Push(def, news))
                        {
                            _coolStale[b] = StaleCooldown;
                            _streak[b] = 0;
                            for (int k = 0; k < StaleWindow; k++) _win[b, k] = 0.0;   // 清窗，免得明天接着连报
                        }
                    }
                    // 累计出货堆到这个量级 → 批发端通胀；每再堆一份，还能再来一次
                    if (_total[b] >= InflateLine(b) * (_inflate[b] + 1) && _coolInflate[b] <= 0)
                    {
                        StockEventDef def = FindEvent(InflateId(b));
                        if (def != null && Push(def, news)) { _coolInflate[b] = InflateCooldown; _inflate[b]++; }
                    }
                    // 旺过之后连着几天没开张 → 供应中断
                    if (_ever[b] && _cold[b] >= ColdStreak && _coolCold[b] <= 0)
                    {
                        StockEventDef def = FindEvent(ColdId(b));
                        if (def != null && Push(def, news)) { _coolCold[b] = ColdCooldown; _cold[b] = 0; }
                    }

                    if (hot) _ever[b] = true;
                }

                // 产能推进：判定都做完了才学今天的货值。放在最后，
                // 今天的爆量才不会顺手把今天的门槛抬高、把自己挡在门外。
                for (int b = 0; b < BucketCount; b++) _avg[b] = Learn(_avg[b], _value[b]);
                _avgCrime = Learn(_avgCrime, _crimeValue);

                // 同一天好几个口子一起旺 → 全站零售回暖
                if (_boomCool > 0) _boomCool--;
                if (hotBuckets >= BoomBuckets && _boomCool <= 0 && Push(FindEvent(BoomId), news))
                    _boomCool = BoomCooldown;

                // 违禁货卖得不少但还没到被巡查的量 → 黑市货源走俏
                if (_crimeHotCool > 0) _crimeHotCool--;
                if (_crimeValue >= CrimeHotLine && _crimeValue < CrimeRaidLine && _crimeHotCool <= 0
                    && Push(FindEvent(CrimeHotId), news))
                    _crimeHotCool = CrimeHotCooldown;

                if (_crimeCool > 0) _crimeCool--;
                _crimeStreak = _crimeValue >= CrimeRaidLine ? _crimeStreak + 1 : 0;
                if (_crimeStreak >= CrimeStreak && _crimeCool <= 0)
                {
                    bool a = Push(FindEvent(RaidDownId), news);
                    bool c = Push(FindEvent(RaidUpId), news);
                    if (a || c) { _crimeCool = CrimeCooldown; _crimeStreak = 0; }
                }

                for (int b = 0; b < BucketCount; b++)
                {
                    _value[b] = 0.0;
                    _applied[b] = 0.0;
                }
                _crimeValue = 0.0;
                _appliedCrime = 0.0;
                _seen.Clear();
                _boughtToday.Clear();
                _sellLogged = 0;      // 日志额度按天给，昨天刷屏不影响今天
                _buyLogged = 0;
                StockState.Dirty = true;
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[实体] 结算失败：" + ex.Message);
            }
            return news;
        }

        /// <summary>夜里报一笔今天的经营实绩：只报最大的那一口 + 违禁货，不刷屏。</summary>
        private static void ReportDay(List<StockEngine.StockNews> news)
        {
            int top = -1;
            double best = 0.0;
            for (int b = 0; b < BucketCount; b++)
            {
                if (_value[b] > best) { best = _value[b]; top = b; }
            }
            // 报不报也按产能走：小于平时一半的日子不算「实绩」，没必要占用一条夜间新闻；
            // 早市卖五六元就够格，后期得卖出几百元才值得播。
            if (top >= 0 && best >= _pace[top] * ReportMul)
            {
                news.Add(new StockEngine.StockNews
                {
                    Text = "【店铺实绩】" + BucketName[top] + "今天出货约 " + best.ToString("0") + " 元，"
                        + "批发端补货转急，相关标的买盘走强（需求 +"
                        + (PulseOf(best, PulseScaleOf(top)) * 100.0).ToString("0.0") + "%）。",
                    Color = StockEngine.NewsUp
                });
            }
            if (_crimeValue >= _paceCrime * ReportMul)
            {
                news.Add(new StockEngine.StockNews
                {
                    Text = "【店铺实绩】今天有约 " + _crimeValue.ToString("0")
                        + " 元的来路不明货物出手，治安部那边的注意力被引过来了。",
                    Color = StockEngine.NewsWarn
                });
            }
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
                Core.Debug("[实体] 事件位已满，" + def.Name + " 这次不挂了。");
                return false;
            }

            StockState.Events.Add(new ActiveEvent
            {
                DefId = def.Id,
                DaysLeft = StockEngine.RandRange(def.MinDays, def.MaxDays),
                TargetStockId = null,
                IntradayDay = -1        // 走盘后：夜里播报，第二天生效
            });
            StockState.Dirty = true;
            news.Add(new StockEngine.StockNews
            {
                Text = "【" + def.Name + "】" + def.Summary,
                Color = def.Impact > 0 ? StockEngine.NewsUp : StockEngine.NewsDown
            });
            Core.Log.Msg("[实体] 触发事件：" + def.Name + "（" + StockCount(def) + " 支标的）");
            return true;
        }

        private static int StockCount(StockEventDef def)
        {
            return def.Targets == null ? StockDefs.All.Length : def.Targets.Length;
        }

        // ══════════════ 存档 ══════════════

        public static void Reset()
        {
            _day = -1;
            for (int b = 0; b < BucketCount; b++)
            {
                _value[b] = 0.0;
                _applied[b] = 0.0;
                _streak[b] = 0;
                _cool[b] = 0;
                _coolStale[b] = 0;
                _total[b] = 0.0;
                _inflate[b] = 0;
                _ever[b] = false;
                _cold[b] = 0;
                _coolSurge[b] = 0;
                _coolInflate[b] = 0;
                _coolCold[b] = 0;
                _coolSpill[b] = 0;
                for (int k = 0; k < StaleWindow; k++) _win[b, k] = 0.0;
                _avg[b] = 0.0;
                _pace[b] = 0.0;
            }
            // 门槛折算系数是缓存：换存档必须重算，不然新档头一天会沿用上个存档的股价口径
            _pfactorDay = -999;
            _crimeFactor = 1.0;
            for (int b = 0; b < BucketCount; b++) _pfactor[b] = 1.0;
            _avgCrime = 0.0;
            _paceCrime = 0.0;
            _crimeValue = 0.0;
            _appliedCrime = 0.0;
            _crimeStreak = 0;
            _crimeCool = 0;
            _boomCool = 0;
            _crimeHotCool = 0;
            _seen.Clear();
            _boughtToday.Clear();
            _sellLogged = 0;
            _buyLogged = 0;
        }

        /// <summary>
        /// 格式：版本|天数|当日货值×6|已并快照×6|违禁×2|连续天数×6|热销冷却×6|积压冷却×6|违禁连续|违禁冷却
        ///      |累计货值×6|旺过×6|没开张天数×6|爆量冷却×6|通胀冷却×6|断供冷却×6|连带冷却×6|通胀次数×6|回暖冷却|黑市走俏冷却
        ///      |近几天货值窗口×6×3
        /// 后面九段是第二批事件加的，老存档读不到就按默认值走；
        /// 最后那 18 个数是「积压按累计货值判」之后加的滚动窗口，老档同样读不到就留空窗。
        /// 末尾 7 个数是「门槛按产能走」之后加的产能（各口子平时一天走多少货，元；
        /// 前六个是口子，第七个是违禁货那条线），老档读不到就按新档起步产能走。
        /// </summary>
        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("1|").Append(_day).Append('|');
            Join(sb, _value);
            sb.Append('|');
            Join(sb, _applied);
            sb.Append('|').Append(_crimeValue.ToString("0.##", CultureInfo.InvariantCulture))
              .Append('|').Append(_appliedCrime.ToString("0.##", CultureInfo.InvariantCulture)).Append('|');
            for (int b = 0; b < BucketCount; b++) sb.Append(_streak[b]).Append(',');
            sb.Append('|');
            for (int b = 0; b < BucketCount; b++) sb.Append(_cool[b]).Append(',');
            sb.Append('|');
            for (int b = 0; b < BucketCount; b++) sb.Append(_coolStale[b]).Append(',');
            sb.Append('|').Append(_crimeStreak).Append('|').Append(_crimeCool);
            // ── 第二批 ──
            sb.Append('|');
            Join(sb, _total);
            sb.Append('|');
            for (int b = 0; b < BucketCount; b++) sb.Append(_ever[b] ? 1 : 0).Append(',');
            sb.Append('|');
            for (int b = 0; b < BucketCount; b++) sb.Append(_cold[b]).Append(',');
            sb.Append('|');
            for (int b = 0; b < BucketCount; b++) sb.Append(_coolSurge[b]).Append(',');
            sb.Append('|');
            for (int b = 0; b < BucketCount; b++) sb.Append(_coolInflate[b]).Append(',');
            sb.Append('|');
            for (int b = 0; b < BucketCount; b++) sb.Append(_coolCold[b]).Append(',');
            sb.Append('|');
            for (int b = 0; b < BucketCount; b++) sb.Append(_coolSpill[b]).Append(',');
            sb.Append('|');
            for (int b = 0; b < BucketCount; b++) sb.Append(_inflate[b]).Append(',');
            sb.Append('|').Append(_boomCool).Append('|').Append(_crimeHotCool);
            // ── 第三批：积压的滚动窗口（每个口子最近 StaleWindow 天，第 0 位是今天）──
            sb.Append('|');
            for (int b = 0; b < BucketCount; b++)
            {
                for (int k = 0; k < StaleWindow; k++)
                {
                    sb.Append(_win[b, k].ToString("0.##", CultureInfo.InvariantCulture)).Append(',');
                }
            }
            // ── 第四批：产能（各口子平时一天走多少货，元；末尾一个给违禁货）──
            sb.Append('|');
            Join(sb, _avg);
            sb.Append(',').Append(_avgCrime.ToString("0.##", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static void Join(StringBuilder sb, double[] arr)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(arr[i].ToString("0.##", CultureInfo.InvariantCulture));
            }
        }

        public static void Parse(string text)
        {
            Reset();
            if (string.IsNullOrEmpty(text)) return;

            string[] f = text.Split('|');
            if (f.Length < 11) return;

            int day;
            if (!int.TryParse(f[1], out day)) return;
            _day = day;
            Read(f[2], _value);
            Read(f[3], _applied);
            _crimeValue = Num(f[4]);
            _appliedCrime = Num(f[5]);
            ReadInts(f[6], _streak);
            ReadInts(f[7], _cool);
            ReadInts(f[8], _coolStale);
            int cs, cc;
            if (int.TryParse(f[9], out cs)) _crimeStreak = cs;
            if (int.TryParse(f[10], out cc)) _crimeCool = cc;
            // ── 第二批：老存档（只有 11 段）读不到，保持默认值即可 ──
            if (f.Length >= 12) Read(f[11], _total);
            if (f.Length >= 13) ReadFlags(f[12], _ever);
            if (f.Length >= 14) ReadInts(f[13], _cold);
            if (f.Length >= 15) ReadInts(f[14], _coolSurge);
            if (f.Length >= 16) ReadInts(f[15], _coolInflate);
            if (f.Length >= 17) ReadInts(f[16], _coolCold);
            if (f.Length >= 18) ReadInts(f[17], _coolSpill);
            if (f.Length >= 19) ReadInts(f[18], _inflate);
            int bc, chc;
            if (f.Length >= 20 && int.TryParse(f[19], out bc)) _boomCool = bc;
            if (f.Length >= 21 && int.TryParse(f[20], out chc)) _crimeHotCool = chc;
            // ── 第三批：积压窗口。老存档（21 段以内）读不到，留空窗即可 ──
            if (f.Length >= 22) ReadWin(f[21]);
            // ── 第四批：产能。老存档（22 段以内）读不到，走起步兜底值 ──
            if (f.Length >= 23) ReadAvgs(f[22]);
            StockState.Dirty = true;
        }

        private static void ReadFlags(string text, bool[] into)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] f = text.Split(',');
            int n = f.Length < into.Length ? f.Length : into.Length;
            for (int i = 0; i < n; i++)
            {
                int v;
                if (int.TryParse(f[i], out v)) into[i] = v != 0;
            }
        }

        private static void Read(string text, double[] into)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] f = text.Split(',');
            int n = f.Length < into.Length ? f.Length : into.Length;
            for (int i = 0; i < n; i++) into[i] = Num(f[i]);
        }

        /// <summary>读积压窗口：一个长条平铺着「口子 × 窗口天数」，第 0 位是当天。</summary>
        private static void ReadWin(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] f = text.Split(',');
            int i = 0;
            for (int b = 0; b < BucketCount; b++)
            {
                for (int k = 0; k < StaleWindow; k++)
                {
                    if (i >= f.Length) return;
                    _win[b, k] = Num(f[i]);
                    i++;
                }
            }
        }

        /// <summary>读产能：前六个是口子的日均货值，第七个是违禁货那条线。</summary>
        private static void ReadAvgs(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] f = text.Split(',');
            int n = f.Length < BucketCount ? f.Length : BucketCount;
            for (int i = 0; i < n; i++) _avg[i] = Num(f[i]);
            if (f.Length > BucketCount) _avgCrime = Num(f[BucketCount]);
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

        private static double Num(string s)
        {
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return 0.0;
        }
    }
}