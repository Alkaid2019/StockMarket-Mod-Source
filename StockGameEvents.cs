using System;
using System.Collections.Generic;
using System.Text;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace StockMarket
{
    /// <summary>
    /// 把《深空当铺》自己的「空间站事件」接进股市。
    ///
    /// 背景：游戏里那套 StoreEventManager 管的是站内事件 —— 供水系统故障、全站停电、
    /// 游客涌入、治安部突袭黑市、下层区暴乱、戒严、工人罢工、卡特尔来访……这些事
    /// 玩家在日历和操作界面里本来就看得见，但以前跟股市毫无关系，等于两条平行线。
    /// 这里做的事就一件：每天开局时把游戏当前的活跃事件读出来，翻译成「哪些标的
    /// 今天该涨、哪些该跌」，喂给 StockEngine.NextPrice 里那一路事件冲击。
    ///
    /// 三个原则：
    ///   1) 只读不写。绝不碰游戏的事件数据，读崩了也只是这条不生效（全程 try/catch）。
    ///   2) 冲击要比模组自造的「市场事件」小一档。玩家自己在日历里能看到的事件
    ///      本来就该有影响，但不该盖过行情本身的噪声和趋势。
    ///   3) 剩余天数尽量用游戏自己算。StoreEventManager.GetEventDayType 前后探一遍
    ///      就能把这个事件的连续区间量出来，比猜 duration 字段可靠；探不出来才退化。
    /// </summary>
    public static class StockGameEvents
    {
        /// <summary>所有空间站事件对单支标的的合计每日冲击上限（正负同）。</summary>
        public const double Cap = 0.22;

        /// <summary>面板左栏最多列几条。</summary>
        public const int MaxRows = 5;

        // ── 层区分组（跟 StockDefs 里的标的分布对齐）──────────────────
        private static readonly string[] G_LOW = { "LL", "KFC", "AGRI", "PEPS", "MID", "HW" };
        private static readonly string[] G_UP = { "TOUR", "HOTEL", "UL", "CHAN", "ENER", "BMW", "BENZ", "APPL" };
        private static readonly string[] G_SEC = { "SEC", "RAY", "BOEI", "BANK" };
        private static readonly string[] G_ARM = { "SEC", "RAY" };
        private static readonly string[] G_BM = { "BM", "FORGE", "GUN", "CART", "DRUG" };
        private static readonly string[] G_REV = { "RMED", "RCEL", "REV" };
        private static readonly string[] G_FOOD = { "LL", "KFC", "AGRI" };
        private static readonly string[] G_DRINK = { "PEPS" };
        private static readonly string[] G_RICH = { "TOUR", "HOTEL", "UL", "CHAN" };
        private static readonly string[] G_TECH = { "MID", "HW", "APPL" };
        private static readonly string[] G_CAR = { "BMW", "BENZ" };
        private static readonly string[] G_ENER = { "ENER" };
        private static readonly string[] G_AIR = { "BOEI" };
        private static readonly string[] G_BANK = { "BANK" };
        private static readonly string[] G_AGRI = { "AGRI" };
        private static readonly string[] G_FORGE = { "FORGE" };
        private static readonly string[] G_DRUG = { "DRUG" };
        private static readonly string[] G_CART = { "CART" };
        private static readonly string[] G_RMED = { "RMED" };
        /// <summary>日用百货：跟店铺经营里的「日用百货」需求口对齐（AGRI 净水 / MID 电子 / HW 电子）。</summary>
        private static readonly string[] G_HOUSE = { "AGRI", "MID", "HW" };

        // ── 对外读数 ──────────────────────────────────────────────────

        /// <summary>一条正在生效的空间站事件（已翻译成股市语言）。</summary>
        public sealed class View
        {
            public string Key;        // 去重用的稳定键（游戏事件的 identifier）
            public string Name;       // 中文名
            public string Area;       // 下层区 / 上层区 / 治安部 / 黑市 / 革命军 / 全站
            public string Kind;       // 威胁 / 常态 / 氛围 / 固有
            public string Summary;    // 一句话简介
            public string Targets;    // 「卡什尼科夫 ↑ · 麦大劳 ↓」
            public int Left = -1;     // 预计还要几天结束，-1 = 长期持续 / 算不出
            public int Total = -1;
            public bool Hidden;       // 未公开事件（面板只报数量，不点名）
            public double Net;        // 净方向（判色用）

            internal string[] Ids;
            internal double[] Per;
        }

        private static readonly List<View> _active = new List<View>();
        private static readonly Dictionary<string, double> _byStock = new Dictionary<string, double>();

        /// <summary>本轮有 N 条未公开事件（面板只报数量）。</summary>
        public static int HiddenCount { get; private set; }

        /// <summary>当前生效的（可见的）空间站事件，面板左栏直接读它。</summary>
        public static List<View> Active { get { return _active; } }

        /// <summary>读了但没公开的条数。</summary>
        public static int Hidden { get { return HiddenCount; } }

        /// <summary>这个标的本日被空间站事件推了/压了多少（已封顶）。给价格公式用。</summary>
        public static double ImpactFor(string stockId)
        {
            double v;
            if (stockId != null && _byStock.TryGetValue(stockId, out v)) return v;
            return 0.0;
        }

        // ── 每天读一次 ────────────────────────────────────────────────

        /// <summary>
        /// 重新读游戏事件。dayTick=true 表示这是每日推进时调的，会顺带产出
        /// 「新出现 / 已结束」的夜间新闻；面板刷新时传 false，只更新数据不出新闻。
        /// </summary>
        public static List<StockEngine.StockNews> Refresh(bool dayTick)
        {
            List<StockEngine.StockNews> news = new List<StockEngine.StockNews>();
            try
            {
                HiddenCount = 0;
                _active.Clear();
                _byStock.Clear();

                if (!StockState.Loaded) { _primed = false; return news; }

                List<StoreEvent> raw = Fetch();
                int today = Today();
                if (today != _spanDay) { _spanDay = today; _spanCache.Clear(); }

                HashSet<string> seen = new HashSet<string>();
                for (int i = 0; i < raw.Count; i++)
                {
                    View v = Resolve(raw[i], today);
                    if (v == null || v.Key == null) continue;
                    if (!seen.Add(v.Key)) continue;   // 同一个事件可能被登记两次
                    _active.Add(v);
                }
                Accumulate();

                if (dayTick)
                {
                    if (_primed)
                    {
                        for (int i = 0; i < _active.Count; i++)
                        {
                            View v = _active[i];
                            if (v.Hidden || _prev.Contains(v.Key)) continue;
                            news.Add(new StockEngine.StockNews
                            {
                                Text = "【空间站事件】" + v.Name + "（" + v.Area + "）——"
                                    + v.Summary + " " + EndText(v.Left),
                                Color = Tone(v.Net)
                            });
                        }
                        foreach (KeyValuePair<string, string> kv in _prevName)
                        {
                            if (seen.Contains(kv.Key)) continue;
                            news.Add(new StockEngine.StockNews
                            {
                                Text = "【空间站事件】" + kv.Value + " 已结束。",
                                Color = StockEngine.NewsFlat
                            });
                        }
                    }
                    // 一行诊断日志：出问题时一眼能看出是「没读到事件」还是「没匹配上规则」
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < _active.Count; i++)
                    {
                        if (sb.Length > 0) sb.Append("、");
                        sb.Append(_active[i].Name);
                    }
                    Core.Log.Msg("[游戏事件] 活跃 " + _active.Count + " 条"
                        + (HiddenCount > 0 ? "（另有 " + HiddenCount + " 条未公开）" : "")
                        + (sb.Length > 0 ? "：" + sb : ""));
                }

                _prev.Clear();
                _prevName.Clear();
                for (int i = 0; i < _active.Count; i++)
                {
                    _prev.Add(_active[i].Key);
                    _prevName[_active[i].Key] = _active[i].Name;
                }
                _primed = true;
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[游戏事件] 读取失败：" + ex.Message);
            }
            return news;
        }

        private static readonly HashSet<string> _prev = new HashSet<string>();
        private static readonly Dictionary<string, string> _prevName = new Dictionary<string, string>();
        private static bool _primed;

        /// <summary>
        /// 换档 / 重开时清空。去重表、跨度缓存留着的话，新档第一天会把上一局的
        /// 事件当成「早就见过」——夜间新闻不播、跨度天数也用旧值。
        /// </summary>
        public static void Reset()
        {
            _active.Clear();
            _byStock.Clear();
            _prev.Clear();
            _prevName.Clear();
            _spanCache.Clear();
            _spanDay = -2;
            HiddenCount = 0;
            _primed = false;
        }

        // ── 读游戏数据 ────────────────────────────────────────────────

        private static List<StoreEvent> Fetch()
        {
            List<StoreEvent> list = new List<StoreEvent>();
            try
            {
                StoreStation st = StoreStation.instance;
                if (st == null) return list;
                StoreEventManager mgr = st.storeEventManager;
                if (mgr == null) return list;
                Il2CppSystem.Collections.Generic.List<StoreEvent> raw = mgr.GetActiveEvents();
                if (raw == null) return list;
                for (int i = 0; i < raw.Count; i++)
                {
                    StoreEvent e = raw[i];
                    if (e != null) list.Add(e);
                }
            }
            catch (Exception ex)
            {
                Core.Debug("[游戏事件] 取活跃事件失败：" + ex.Message);
            }
            return list;
        }

        private static int Today()
        {
            try
            {
                StoreStation st = StoreStation.instance;
                return st == null ? -1 : st.dayCounter;
            }
            catch { return -1; }
        }

        private static View Resolve(StoreEvent e, int today)
        {
            string ident = Str(() => (string)e.identifier);
            string disp = Str(() => (string)e.displayName);
            string news = Str(() => (string)e.newsName);
            string price = Str(() => (string)e.GetEventPriceEffect());
            int type = Num(() => (int)e.eventType);
            int area = Num(() => (int)e.eventArea);

            // 本地化名只在 displayName 拿不到人话时去查，避免每次刷新都刷一串日志
            string locName = string.Empty;
            if (disp.Length == 0 || LooksLikeKey(disp)) locName = Localized(ident, disp);

            // 注意：故意不把 newsDescription 放进匹配串。
            // 描述文字又长又泛（「供水」这种词在检修事件里也会出现），
            // 拿它做关键词匹配全是误伤；identifier / 名字 / 价格影响这三样够准。
            // Space() 把 PascalCase 拆成词：游戏很多事件的 identifier 是
            // ScheduledElectricalMaintenance 这种连写，不拆的话「electrical maintenance」
            // 永远匹配不上，事件就掉进兜底分支了。
            string hay = (Space(ident) + " " + Space(news) + " " + Space(disp) + " "
                + Space(price) + " " + Space(locName)).ToLowerInvariant();

            View v = new View();
            v.Key = ident.Length > 0 ? ident : (disp.Length > 0 ? disp : news);
            if (v.Key.Length == 0) return null;
            v.Kind = KindOf(type);

            Rule r = Find(hay);
            Seg[] segs;
            if (r != null)
            {
                v.Name = r.Name;
                v.Area = r.Area;
                v.Summary = r.Summary;
                segs = r.Segs;
            }
            else
            {
                // 没写规则的事件也不能丢：名字照显示，冲击走「派系势力增减」兜底
                // 名字一律不用 identifier —— 那是给规则表查的机器键，玩家不该看见。
                // 本地化名（已在 Localized 里过了「翻译错误」筛）→ 游戏给的人话名字 → 中文兜底。
                string nm = locName;
                if (nm.Length == 0) nm = Human(disp) ? disp : (Human(news) ? news : string.Empty);
                v.Name = nm.Length > 0 ? Clean(nm) : "未备案的站内异动";
                v.Area = AreaOf(area);
                v.Summary = "这条异动的具体影响还没摸清，先按势力格局的变化粗算。";
                segs = FromMods(e);
                if (v.Name.Length > 0)
                {
                    Core.Debug("[游戏事件] 未匹配规则：" + ident + " / " + disp
                        + " / " + price + " → 走兜底");
                }
            }
            // 兜底也不许把机器键端给玩家
            if (string.IsNullOrEmpty(v.Name)) v.Name = "站内异动";

            v.Left = -1; v.Total = -1;
            Span(e, today, out v.Left, out v.Total);
            // 隐藏判定必须在这儿算：下面把冲击按系数缩的时候要用到它，
            // 而 v.Hidden 要到函数末尾才赋值（原来在这里读，永远是 false，
            // 未公开事件等于没折减）。
            bool hidden = IsHidden(e);
            double coef = CoefOf(type) * (hidden ? 0.6 : 1.0) * Taper(v.Left, v.Total);

            // 展开成「标的 → 每日冲击」
            List<string> ids = new List<string>();
            List<double> per = new List<double>();
            if (segs != null)
            {
                for (int i = 0; i < segs.Length; i++)
                {
                    Seg s = segs[i];
                    if (s == null || s.Ids == null) continue;
                    double val = s.Impact * coef;
                    for (int k = 0; k < s.Ids.Length; k++)
                    {
                        int idx = ids.IndexOf(s.Ids[k]);
                        if (idx >= 0) per[idx] += val;
                        else { ids.Add(s.Ids[k]); per.Add(val); }
                    }
                }
            }
            v.Ids = ids.ToArray();
            v.Per = per.ToArray();

            // 影响清单 + 净方向
            StringBuilder tb = new StringBuilder();
            double net = 0;
            for (int i = 0; i < v.Ids.Length; i++)
            {
                net += v.Per[i];
                double a = Math.Abs(v.Per[i]);
                if (a < 0.004) continue;
                if (tb.Length > 0) tb.Append(" · ");
                StockDef sd = StockDefs.Get(v.Ids[i]);
                tb.Append(sd != null ? sd.Name : v.Ids[i]);
                tb.Append(v.Per[i] > 0 ? " ↑" : " ↓");
            }
            v.Net = net;
            v.Targets = tb.Length > 0 ? tb.ToString() : "影响面太散，先观察";

            // 未公开事件不点名，也不进左栏
            if (hidden) { v.Hidden = true; HiddenCount++; }
            return v;
        }

        private static bool IsHidden(StoreEvent e)
        {
            return Flag(() => e.isHidden) || Flag(() => e.isHiddenFromNewsPaper);
        }

        private static void Accumulate()
        {
            for (int i = 0; i < _active.Count; i++)
            {
                View v = _active[i];
                for (int k = 0; k < v.Ids.Length; k++)
                {
                    double cur;
                    _byStock.TryGetValue(v.Ids[k], out cur);
                    _byStock[v.Ids[k]] = cur + v.Per[k];
                }
            }
            // 多条事件同时压在同一支上会叠加，不封顶的话单日能砸穿涨跌停
            List<string> keys = new List<string>(_byStock.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                double val = _byStock[keys[i]];
                if (val > Cap) val = Cap;
                if (val < -Cap) val = -Cap;
                _byStock[keys[i]] = val;
            }
        }

        // ── 剩余天数 ──────────────────────────────────────────────────

        private const int ProbeBack = 45;
        private const int ProbeAhead = 45;
        private const int MaxSpan = 45;
        private const int DayTypeNotPart = 3;   // StoreEventManager.EventDayType.NotPartOfEvent

        private static int _spanDay = -2;
        private static readonly Dictionary<string, int[]> _spanCache = new Dictionary<string, int[]>();

        /// <summary>
        /// 算出「这个事件从哪天到哪天」。首选游戏自己的 GetEventDayType：
        /// 前后各探一段，把连续命中的区间首尾拿出来，就是事件的起止日。
        /// 探不出来（接口语义跟预期不一致）就退回 duration / totalDuration。
        /// </summary>
        private static void Span(StoreEvent e, int today, out int left, out int total)
        {
            left = -1; total = -1;
            try
            {
                if (e.isPermanent) return;
            }
            catch { }

            string key = Str(() => (string)e.identifier);
            int[] cached;
            if (key.Length > 0 && _spanCache.TryGetValue(key, out cached) && cached != null)
            {
                left = cached[0]; total = cached[1];
                return;
            }

            if (today > 0)
            {
                try
                {
                    int first = int.MaxValue, last = int.MinValue;
                    for (int d = today - ProbeBack; d <= today + ProbeAhead; d++)
                    {
                        int t;
                        try { t = (int)StoreEventManager.GetEventDayType(e, d); }
                        catch { t = DayTypeNotPart; }
                        if (t == DayTypeNotPart) continue;
                        if (d < first) first = d;
                        if (d > last) last = d;
                    }
                    if (first != int.MaxValue && last >= first && last - first + 1 <= MaxSpan)
                    {
                        left = last - today;
                        total = last - first + 1;
                        if (key.Length > 0) _spanCache[key] = new[] { left, total };
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Core.Debug("[游戏事件] 天数探测失败：" + ex.Message);
                }
            }

            // 退路：duration 当成「还剩几天」，totalDuration 当成「一共几天」
            int dur = Num(() => e.duration);
            int tot = Num(() => e.totalDuration);
            if (tot > 0)
            {
                total = tot;
                left = dur < 0 ? tot : (dur > tot ? tot : dur);
            }
            else if (dur > 0)
            {
                left = dur; total = dur;
            }
            if (key.Length > 0) _spanCache[key] = new[] { left, total };
        }

        /// <summary>「预计 N 天后结束 / 今天结束 / 长期持续」。</summary>
        public static string EndText(int left)
        {
            if (left < 0) return "长期持续。";
            if (left <= 0) return "今天结束。";
            return "预计 " + left + " 天后结束。";
        }

        // ── 匹配规则 ──────────────────────────────────────────────────

        private sealed class Seg
        {
            public string[] Ids;
            public double Impact;
        }

        private sealed class Rule
        {
            public string[] Any;   // 命中任意一条即算；一条里用空格分隔的词必须同时出现
            public string Name;
            public string Area;
            public string Summary;
            public Seg[] Segs;
        }

        private static Seg Sg(string[] ids, double impact)
        {
            return new Seg { Ids = ids, Impact = impact };
        }

        private static readonly Rule[] Rules =
        {
            // ── 派系操作（event_op_*，玩家能主动下的那种命令）────────────
            new Rule { Any = new[] { "gang war", "gang_war", "帮派火并" },
                Name = "帮派火并", Area = "下层区",
                Summary = "两伙人在下层区当街开火，街面生意停摆，军火却越来越紧俏。",
                Segs = new[] { Sg(G_BM, 0.05), Sg(G_ARM, 0.04), Sg(G_FOOD, -0.04) } },

            new Rule { Any = new[] { "weapon shipment", "weapon_shipment", "军火走私" },
                Name = "军火走私船靠港", Area = "全站",
                Summary = "一批军火悄悄进了站，黑市货源充足，治安部的面子挂不住。",
                Segs = new[] { Sg(G_BM, 0.06), Sg(G_SEC, -0.03) } },

            new Rule { Any = new[] { "mob secret", "mob_secret", "黑市密约" },
                Name = "黑市密约", Area = "黑市",
                Summary = "黑市几个头目关起门来谈大生意，风声还没漏到外面。",
                Segs = new[] { Sg(G_BM, 0.04) } },

            new Rule { Any = new[] { "mobilization", "总动员" },
                Name = "总动员", Area = "全站",
                Summary = "治安部把人全拉去集结了，站里到处是岗哨，做生意的都缩着。",
                Segs = new[] { Sg(G_SEC, 0.07), Sg(G_REV, -0.05), Sg(G_LOW, -0.03) } },

            new Rule { Any = new[] { "raid upper", "raid_upper", "搜查上层" },
                Name = "上层区搜查", Area = "上层区",
                Summary = "治安部把上层区翻了个遍，奢侈品和酒店生意先冷下来。",
                Segs = new[] { Sg(G_RICH, -0.06), Sg(G_SEC, 0.03) } },

            new Rule { Any = new[] { "raid sec", "raid_sec", "治安部内查" },
                Name = "治安部内查", Area = "治安部",
                Summary = "治安部自己人被查了，队伍一时缓不过来，黑市趁机喘口气。",
                Segs = new[] { Sg(G_SEC, -0.07), Sg(G_BM, 0.04) } },

            new Rule { Any = new[] { "raid bm", "raid_bm", "突袭黑市" },
                Name = "突袭黑市", Area = "黑市",
                Summary = "治安部直接扑进黑市，摊位掀了一半，货全扣了。",
                Segs = new[] { Sg(G_BM, -0.09) } },

            new Rule { Any = new[] { "raid rev", "raid_rev", "围剿革命军" },
                Name = "围剿革命军", Area = "革命军",
                Summary = "治安部对革命军据点动手，地下买卖全停了。",
                Segs = new[] { Sg(G_REV, -0.08), Sg(G_SEC, 0.05) } },

            new Rule { Any = new[] { "patrol", "巡逻" },
                Name = "加强巡逻", Area = "全站",
                Summary = "街面巡逻加密，黑市买卖不好做，下层区老百姓反倒敢出门了。",
                Segs = new[] { Sg(G_BM, -0.05), Sg(G_ARM, 0.03), Sg(G_LOW, 0.02) } },

            new Rule { Any = new[] { "crackdown", "严打" },
                Name = "全站严打", Area = "全站",
                Summary = "全站范围的严打，黑市全线收缩，治安部的装备订单跟着涨。",
                Segs = new[] { Sg(G_BM, -0.08), Sg(G_SEC, 0.05) } },

            new Rule { Any = new[] { "martial law", "martial_law", "戒严" },
                Name = "全站戒严", Area = "全站",
                Summary = "全站戒严，街上没人，店门全关，只有治安部的车在跑。",
                Segs = new[] { Sg(G_LOW, -0.06), Sg(G_RICH, -0.05), Sg(G_SEC, 0.06),
                               Sg(G_BM, -0.06), Sg(G_REV, -0.04) } },

            new Rule { Any = new[] { "budget increase", "budget_increase", "预算增加" },
                Name = "治安部预算增加", Area = "治安部",
                Summary = "治安部拿到更多预算，装备和航空订单走强，下层区的税负怕是也要跟着涨。",
                Segs = new[] { Sg(G_SEC, 0.06), Sg(G_BANK, 0.03), Sg(G_LOW, -0.02) } },

            new Rule { Any = new[] { "riot", "暴乱" },
                Name = "下层区暴乱", Area = "下层区",
                Summary = "下层区闹起来了，商铺被砸，治安部忙着压场子，黑市趁机抬价。",
                Segs = new[] { Sg(G_LOW, -0.07), Sg(G_ARM, 0.05), Sg(G_BM, 0.04) } },

            new Rule { Any = new[] { "strike", "罢工" },
                Name = "工人罢工", Area = "下层区",
                Summary = "工人撂挑子了，下层区供货吃紧，革命军那边士气倒是涨了一截。",
                Segs = new[] { Sg(G_FOOD, -0.05), Sg(G_REV, 0.04), Sg(G_ARM, -0.02) } },

            new Rule { Any = new[] { "medical aid", "medical_aid", "医疗援助" },
                Name = "医疗援助", Area = "革命军",
                Summary = "一批医疗物资进了站，红星制药的订单排到了下个月。",
                Segs = new[] { Sg(G_RMED, 0.07), Sg(G_REV, 0.03), Sg(G_BANK, -0.02) } },

            new Rule { Any = new[] { "food drive", "food_drive", "食物救济" },
                Name = "食物救济", Area = "下层区",
                Summary = "站里发救济粮，快餐和饮料摊子生意火爆，治安部被骂抠门。",
                Segs = new[] { Sg(G_FOOD, 0.05), Sg(G_DRINK, 0.03), Sg(G_SEC, -0.02) } },

            new Rule { Any = new[] { "household relief", "household_relief", "家庭补贴" },
                Name = "家庭补贴", Area = "下层区",
                Summary = "站里给住户发了补贴，下层区的钱袋子松了，快餐和电子都好卖。",
                Segs = new[] { Sg(G_LOW, 0.05), Sg(G_RICH, 0.02), Sg(G_BANK, -0.02) } },

            // ── 治安 / 司法类常规事件 ───────────────────────────────────
            new Rule { Any = new[] { "evidence locker", "evidence_locker", "证物柜" },
                Name = "证物柜失窃", Area = "治安部",
                Summary = "治安部的证物柜被人撬了，案卷丢了一堆，黑市那边笑得合不拢嘴。",
                Segs = new[] { Sg(G_SEC, -0.05), Sg(G_FORGE, 0.05), Sg(G_BM, 0.03) } },

            new Rule { Any = new[] { "wanted", "通缉犯落网" },
                Name = "通缉犯落网", Area = "治安部",
                Summary = "悬赏榜上那个名字被划掉了，治安部扬眉吐气，黑市暂时夹起尾巴。",
                Segs = new[] { Sg(G_ARM, 0.04), Sg(G_BM, -0.04) } },

            new Rule { Any = new[] { "mob justice", "mob_justice", "私刑" },
                Name = "下层区私刑", Area = "下层区",
                Summary = "有人绕开治安部自己动了手，街面上人心惶惶，秩序成了紧俏货。",
                Segs = new[] { Sg(G_LOW, -0.04), Sg(G_ARM, 0.04) } },

            // ── 资源 / 设施类 ────────────────────────────────────────────
            new Rule { Any = new[] { "water breakdown", "water treatment", "water_breakdown", "供水" },
                Name = "供水系统故障", Area = "全站",
                Summary = "站里的水处理系统坏了，净水成了硬通货，抢修队连夜开工。",
                Segs = new[] { Sg(G_AGRI, 0.08), Sg(G_FOOD, 0.03), Sg(G_LOW, -0.03) } },

            new Rule { Any = new[] { "water maintenance", "water_maintenance", "treatment plant maintenance", "水处理检修" },
                Name = "水处理设施检修", Area = "全站",
                Summary = "水处理厂按计划停机检修，供水中断一阵，净水价格短期走俏。",
                Segs = new[] { Sg(G_AGRI, 0.04) } },

            new Rule { Any = new[] { "blackout", "power outage", "power_outage", "停电" },
                Name = "全站大停电", Area = "全站",
                Summary = "站里一片漆黑，生产停摆、电子设备烧了不少，医院全靠备用电撑着。",
                Segs = new[] { Sg(G_TECH, -0.07), Sg(G_LOW, -0.04), Sg(G_ENER, 0.05), Sg(G_RMED, 0.03) } },

            new Rule { Any = new[] { "power shortage", "power_shortage", "电力短缺" },
                Name = "电力供应紧张", Area = "全站",
                Summary = "电网负荷告急，开始限供，能源商成了香饽饽。",
                Segs = new[] { Sg(G_ENER, 0.07), Sg(G_TECH, -0.05), Sg(G_LOW, -0.03) } },

            new Rule { Any = new[] { "power maintenance", "power_maintenance", "电厂检修" },
                Name = "电厂例行检修", Area = "全站",
                Summary = "电厂按计划停机检修，电力略微紧张，能源价格短期偏强。",
                Segs = new[] { Sg(G_ENER, 0.03), Sg(G_TECH, -0.02) } },

            new Rule { Any = new[] { "ice asteroid", "ice_asteroid", "miner return", "采矿队返航" },
                Name = "采矿队返航", Area = "下层区",
                Summary = "去冰质小行星的矿工回来了，船舱里装满了水和矿，下层区物价松一口气。",
                Segs = new[] { Sg(G_AGRI, -0.05), Sg(G_FOOD, -0.02), Sg(G_CART, 0.04) } },

            new Rule { Any = new[] { "mining expedition", "mining_return", "采矿远征" },
                Name = "采矿远征队归来", Area = "全站",
                Summary = "远征队带着矿石回来了，原料供应宽松，化工和重工成本下降。",
                Segs = new[] { Sg(G_CART, -0.04), Sg(G_ENER, -0.03) } },

            new Rule { Any = new[] { "nutrifruit harvest", "nutritional fruit harvest", "营养果丰收" },
                Name = "营养果丰收", Area = "下层区",
                Summary = "这一季营养果长得特别好，食品供应宽松，价格往下走。",
                Segs = new[] { Sg(G_AGRI, 0.05), Sg(G_FOOD, 0.03) } },

            new Rule { Any = new[] { "nutrifruit grow", "nutrifruit growth", "营养果生长" },
                Name = "营养果长势喜人", Area = "下层区",
                Summary = "农场的营养果苗长势不错，下一季的供应有指望了，食品商先松口气。",
                Segs = new[] { Sg(G_AGRI, 0.03) } },

            new Rule { Any = new[] { "bad harvest", "歉收" },
                Name = "营养果歉收", Area = "下层区",
                Summary = "这一季营养果收成很差，食品供应吃紧，价格往上抬。",
                Segs = new[] { Sg(G_AGRI, -0.06), Sg(G_FOOD, -0.03) } },

            new Rule { Any = new[] { "seed heist", "seed_heist", "种子" },
                Name = "种子失窃", Area = "下层区",
                Summary = "农场的种子被偷了一批，下一季的收成悬了，黑市上却多了货源。",
                Segs = new[] { Sg(G_AGRI, -0.04), Sg(G_BM, 0.03) } },

            new Rule { Any = new[] { "counterfeit", "假药" },
                Name = "假药流入市场", Area = "黑市",
                Summary = "查出一批假冒的医疗用品，正牌药品被连累，治安部脸上也不好看。",
                Segs = new[] { Sg(G_DRUG, -0.06), Sg(G_RMED, -0.03), Sg(G_SEC, 0.03) } },

            new Rule { Any = new[] { "oxymore", "奥克西莫" },
                Name = "奥克西莫浪潮", Area = "黑市",
                Summary = "站里嗑奥克西莫的人突然多了一大批，止痛药供不应求，事故也跟着多了。",
                Segs = new[] { Sg(G_DRUG, 0.07), Sg(G_RMED, -0.04), Sg(G_ARM, 0.03) } },

            // 原版事件：identifier = storeroom_excavated，游戏自带的数据就是
            // 「HOUSEHOLD_GOOD 的价值 -20，原因：日用品过剩」——是对日用品的**利空**。
            // 所以这条只认「储藏室被挖开」这两件事一起出现，别再拿 storeroom 一个词瞎接，
            // 免得同系列别的储藏室事件被一起算成利好。
            new Rule { Any = new[] { "storeroom excavated", "storeroom_excavated", "秘密储藏室", "日用品过剩" },
                Name = "日用品过剩", Area = "下层区",
                Summary = "有人翻出个秘密储藏室，日用品一下子全涌进市场，批发价当场被砸了下来。",
                Segs = new[] { Sg(G_HOUSE, -0.055) } },

            new Rule { Any = new[] { "construction", "施工" },
                Name = "新工程开工", Area = "全站",
                Summary = "站里立起围挡开工，建材和重工订单先热了起来。",
                Segs = new[] { Sg(G_CART, 0.04), Sg(G_ENER, 0.02) } },

            new Rule { Any = new[] { "meteor shower", "meteor_shower", "流星雨" },
                Name = "流星雨来袭", Area = "全站",
                Summary = "一场流星雨擦着站体过去，外壳受创，维修订单砸下来。",
                Segs = new[] { Sg(G_AIR, -0.05), Sg(G_ARM, 0.03), Sg(G_LOW, -0.02) } },

            // ── 客流 / 商业类 ────────────────────────────────────────────
            new Rule { Any = new[] { "cruise ship", "cruise_ship", "游客", "游轮" },
                Name = "游客涌入", Area = "上层区",
                Summary = "一艘观光船靠港，站里一下挤满了生面孔，酒店、酒吧、奢侈品全被买空。",
                Segs = new[] { Sg(G_RICH, 0.08), Sg(G_DRINK, 0.04), Sg(G_FOOD, 0.03) } },

            new Rule { Any = new[] { "new transit", "transit_line", "新航线" },
                Name = "新航线开通", Area = "全站",
                Summary = "一条新航线挂牌，来往的人变多了，航空和住宿先受益。",
                Segs = new[] { Sg(G_AIR, 0.04), Sg(G_RICH, 0.03) } },

            new Rule { Any = new[] { "cultural celebration", "文化庆典" },
                Name = "文化庆典", Area = "上层区",
                Summary = "站里办起了庆典，街上热闹，饮料和住宿生意都跟着好。",
                Segs = new[] { Sg(G_RICH, 0.04), Sg(G_DRINK, 0.03) } },

            new Rule { Any = new[] { "artist showcase", "艺术展" },
                Name = "艺术家作品展", Area = "上层区",
                Summary = "上层区办了场作品展，来的都是有钱人，奢侈品柜台围满了人。",
                Segs = new[] { Sg(G_RICH, 0.04) } },

            new Rule { Any = new[] { "art contest", "艺术比赛" },
                Name = "艺术比赛落幕", Area = "上层区",
                Summary = "艺术比赛评完了奖，上层区的话题度涨了一波，奢侈品沾了点光。",
                Segs = new[] { Sg(G_RICH, 0.03) } },

            new Rule { Any = new[] { "tech demo", "tech_demo", "技术展示" },
                Name = "科技展示日", Area = "上层区",
                Summary = "站里办了场科技展示，电子厂商的订单和股价一起往上走。",
                Segs = new[] { Sg(G_TECH, 0.05) } },

            // "eletrical" 是游戏里的拼写笔误（ScheduledEletricalMaintenance），
            // 照抄一遍，否则这条规则永远命中不了。
            new Rule { Any = new[] { "electrical maintenance", "eletrical maintenance", "电路检修" },
                Name = "电路计划检修", Area = "全站",
                Summary = "电路按计划停机检修，电费短期偏紧，能源商乐见其成。",
                Segs = new[] { Sg(G_ENER, 0.03), Sg(G_TECH, -0.02) } },

            new Rule { Any = new[] { "recycling", "回收" },
                Name = "环保回收运动", Area = "全站",
                Summary = "站里搞起了回收运动，化工的原料成本下来了，食品反而被嫌不干净。",
                Segs = new[] { Sg(G_CART, -0.03), Sg(G_AGRI, 0.02) } },

            new Rule { Any = new[] { "community garden", "社区花园" },
                Name = "社区花园扩建", Area = "下层区",
                Summary = "社区花园又扩了一块，下层区的菜价松了松。",
                Segs = new[] { Sg(G_AGRI, 0.03) } },

            new Rule { Any = new[] { "water fountain", "喷泉" },
                Name = "喷泉落成", Area = "上层区",
                Summary = "广场上的喷泉揭幕了，站里多了个打卡点，住宿和饮水生意都沾光。",
                Segs = new[] { Sg(G_AGRI, 0.02), Sg(G_RICH, 0.02) } },

            new Rule { Any = new[] { "spacewalk", "太空行走" },
                Name = "例行太空行走训练", Area = "治安部",
                Summary = "治安部组织了例行舱外训练，航空部门的出勤率被表扬了一通。",
                Segs = new[] { Sg(G_AIR, 0.02) } },

            new Rule { Any = new[] { "peat", "泥炭" },
                Name = "泥炭开采推进", Area = "全站",
                Summary = "泥炭开采往前推了一步，化工原料宽裕了些。",
                Segs = new[] { Sg(G_CART, 0.02) } },

            new Rule { Any = new[] { "all quiet", "平静" },
                Name = "风平浪静", Area = "全站",
                Summary = "这几天站里没什么事发生，市场情绪平稳，钱也愿意出来转一转。",
                Segs = new[] { Sg(G_BANK, 0.015), Sg(G_RICH, 0.01) } },

            // ── 固有事件（游戏按规矩一定会来的那几件）──────────────────
            new Rule { Any = new[] { "rent due", "rent day", "rent_due", "缴租" },
                Name = "缴租日", Area = "下层区",
                Summary = "又到缴租日，手头紧的人得割点东西换钱，投资的钱也往回收。",
                Segs = new[] { Sg(G_LOW, -0.02), Sg(G_BANK, 0.02) } },

            new Rule { Any = new[] { "mortgage", "还贷" },
                Name = "还贷日", Area = "治安部",
                Summary = "还贷日到了，资金面偏紧，银行反而成了赢家。",
                Segs = new[] { Sg(G_BANK, 0.03), Sg(G_LOW, -0.02) } },

            new Rule { Any = new[] { "cartel visit", "cartel_visit", "卡特尔来访" },
                Name = "卡特尔来访", Area = "黑市",
                Summary = "卡特尔的头目亲自来站里转了一圈，黑市上下都客气得很。",
                Segs = new[] { Sg(G_BM, 0.06), Sg(G_SEC, -0.02) } },

            new Rule { Any = new[] { "lottery", "抽奖" },
                Name = "抽奖开奖", Area = "全站",
                Summary = "抽奖开奖了，站里到处在传谁中了头奖，消费气氛一下热起来。",
                Segs = new[] { Sg(G_BANK, 0.03), Sg(G_DRINK, 0.03), Sg(G_FOOD, 0.02) } },
        };

        private static Rule Find(string hay)
        {
            if (string.IsNullOrEmpty(hay)) return null;
            for (int i = 0; i < Rules.Length; i++)
            {
                Rule r = Rules[i];
                if (r.Any == null) continue;
                for (int k = 0; k < r.Any.Length; k++)
                {
                    if (Match(hay, r.Any[k])) return r;
                }
            }
            return null;
        }

        /// <summary>关键词里用空格分隔的词必须同时出现（顺序不限）。</summary>
        private static bool Match(string hay, string keys)
        {
            int from = 0;
            while (true)
            {
                int sp = keys.IndexOf(' ', from);
                string tok = sp < 0 ? keys.Substring(from) : keys.Substring(from, sp - from);
                if (tok.Length > 0 && !Contains(hay, tok)) return false;
                if (sp < 0) return true;
                from = sp + 1;
            }
        }

        private static bool Contains(string hay, string key)
        {
            // 中文直接子串匹配；英文按词匹配，免得 "peat" 命中 "repeat"、"riot" 命中 "patriot"
            if (IsCjk(key)) return hay.IndexOf(key, StringComparison.Ordinal) >= 0;
            int from = 0;
            while (true)
            {
                int i = hay.IndexOf(key, from, StringComparison.Ordinal);
                if (i < 0) return false;
                int end = i + key.Length;
                bool lok = i == 0 || !IsWord(hay[i - 1]);
                bool rok = end >= hay.Length || !IsWord(hay[end]);
                if (lok && rok) return true;
                from = i + 1;
            }
        }

        private static bool IsWord(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
        }

        private static bool IsCjk(string s)
        {
            for (int i = 0; i < s.Length; i++) if (s[i] >= 0x2E80) return true;
            return false;
        }

        // ── 兜底：用势力增减量粗算 ────────────────────────────────────

        private const double ModUnit = 0.02;   // 每 1 点势力变化算 2% 冲击
        private const double ModCap = 0.09;    // 单组封顶

        private static Seg[] FromMods(StoreEvent e)
        {
            List<Seg> list = new List<Seg>();
            Add(list, G_ARM, Num(() => e.secPowerMod) * ModUnit);
            Add(list, G_REV, Num(() => e.revPowerMod) * ModUnit);
            Add(list, G_BM, Num(() => e.bmPowerMod) * ModUnit);
            double unrest = -Num(() => e.civilUnrestMod) * ModUnit;
            Add(list, G_LOW, unrest);
            Add(list, G_UP, Num(() => e.upperLevelFriendlinessMod) * ModUnit + unrest * 0.5);
            return list.ToArray();
        }

        private static void Add(List<Seg> list, string[] ids, double impact)
        {
            if (ids == null || ids.Length == 0) return;
            if (impact > ModCap) impact = ModCap;
            if (impact < -ModCap) impact = -ModCap;
            if (Math.Abs(impact) < 0.005) return;
            list.Add(new Seg { Ids = ids, Impact = impact });
        }

        // ── 系数 ──────────────────────────────────────────────────────

        /// <summary>事件类型系数：氛围类只是背景音，固有事件也不是冲着股市来的。</summary>
        private static double CoefOf(int type)
        {
            if (type == 2) return 0.45;   // COSMETIC
            if (type == 3) return 0.50;   // INNATE
            return 1.0;                    // NORMALE / THREAT
        }

        /// <summary>越接近结束，冲击越小（长期事件不衰减，但也压一档）。</summary>
        private static double Taper(int left, int total)
        {
            if (left < 0 || total <= 1) return 0.75;
            double k = (double)left / total;
            if (k < 0) k = 0;
            if (k > 1) k = 1;
            return 0.55 + 0.45 * k;
        }

        private static string KindOf(int type)
        {
            if (type == 1) return "威胁";
            if (type == 2) return "氛围";
            if (type == 3) return "固有";
            return "常态";
        }

        private static string AreaOf(int area)
        {
            if (area == 0) return "上层区";
            if (area == 1) return "下层区";
            return "全站";
        }

        private static string Tone(double net)
        {
            // 夜间报告是米色纸底，颜色一律跟 StockEngine 那套深色口径走
            if (net > 0.0001) return StockEngine.NewsUp;
            if (net < -0.0001) return StockEngine.NewsDown;
            return StockEngine.NewsFlat;
        }

        // ── 安全取值 / 本地化 ─────────────────────────────────────────

        private static string Str(Func<string> f)
        {
            try { string s = f(); return s == null ? string.Empty : s.Trim(); }
            catch { return string.Empty; }
        }

        private static int Num(Func<int> f)
        {
            try { return f(); } catch { return 0; }
        }

        private static bool Flag(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }

        private static readonly Dictionary<string, string> _loc = new Dictionary<string, string>();

        /// <summary>
        /// 本地化名。游戏的 displayName 有时直接就是人话，有时给的是键名，
        /// 后者才去 Mechanic 表里查一遍；查不到返回空，交给调用方兜底。
        /// </summary>
        private static string Localized(string ident, string disp)
        {
            string basis = ident.Length > 0 ? ident : disp;
            if (basis.Length == 0) return string.Empty;
            string hit;
            if (_loc.TryGetValue(basis, out hit)) return hit;

            string result = string.Empty;
            string[] roots = Roots(basis);
            string[] tails = { "", "_display", "_news", "_desc", "_name" };
            for (int i = 0; i < roots.Length && result.Length == 0; i++)
            {
                for (int k = 0; k < tails.Length && result.Length == 0; k++)
                {
                    string v = Loc(roots[i] + tails[k]);
                    if (Human(v) && v != basis) result = v;
                }
            }
            _loc[basis] = result;
            return result;
        }

        /// <summary>键名可能的写法：原文、补 event_ 前缀、去掉已知前缀等等。</summary>
        private static string[] Roots(string key)
        {
            List<string> list = new List<string>();
            list.Add(key);
            if (key.StartsWith("event_", StringComparison.Ordinal))
            {
                string t = key.Substring(6);
                list.Add(t);
                if (t.StartsWith("normal_", StringComparison.Ordinal)) list.Add(t.Substring(7));
                if (t.StartsWith("op_", StringComparison.Ordinal)) list.Add(t.Substring(3));
                if (t.StartsWith("cosmetic_", StringComparison.Ordinal)) list.Add(t.Substring(9));
                if (t.StartsWith("innate_", StringComparison.Ordinal)) list.Add(t.Substring(7));
            }
            else
            {
                list.Add("event_" + key);
                list.Add("event_normal_" + key);
                list.Add("event_op_" + key);
            }
            return list.ToArray();
        }

        private static Il2CppReferenceArray<Il2CppSystem.Object> _noArgs;

        private static string Loc(string key)
        {
            if (key.Length == 0) return string.Empty;
            try
            {
                if (_noArgs == null) _noArgs = new Il2CppReferenceArray<Il2CppSystem.Object>(0);
                string v = LocHelper.Get("Mechanic", key, _noArgs);
                if (v == null) return string.Empty;
                v = v.Trim();
                // 查不到键时 LocHelper 会把「Translation Error: xxx in Mechanic」当结果返回，
                // 这东西一旦漏进夜间新闻就是玩家眼里的 bug。在这里拦死。
                if (LocError(v)) return string.Empty;
                return v;
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// LocHelper 查不到键时的返回值长这样：Translation Error: ScheduledElectricalMaintenance in Mechanic
        /// 它不是文案，是报错，绝不能上屏幕。
        /// </summary>
        private static bool LocError(string s)
        {
            return !string.IsNullOrEmpty(s)
                && s.IndexOf("translation error", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 看起来像键名而不是人话。先统一小写再判，
        /// 这样 ScheduledElectricalMaintenance 这类 PascalCase 连写也能被认出来。
        /// </summary>
        private static bool LooksLikeKey(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 3) return false;
            s = s.ToLowerInvariant();
            bool letter = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 'a' && c <= 'z') { letter = true; continue; }
                if (c >= '0' && c <= '9') continue;
                if (c == '_' || c == '.' || c == '/') continue;
                return false;
            }
            return letter;
        }

        /// <summary>
        /// 能不能拿给玩家看：非空、不像键名、不是本地化报错。
        /// 「给玩家看的东西」一律先过这道闸。
        /// </summary>
        private static bool Human(string s)
        {
            return !string.IsNullOrEmpty(s) && !LooksLikeKey(s) && !LocError(s);
        }

        /// <summary>
        /// 把 PascalCase / camelCase 连写拆成空格分隔的词。
        /// 游戏里大量事件 identifier 是 ScheduledElectricalMaintenance 这种连写，
        /// 不拆的话「electrical maintenance」这类关键词永远匹配不上，规则表全废。
        /// </summary>
        private static string Space(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            StringBuilder b = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 'A' && c <= 'Z' && i > 0)
                {
                    char p = s[i - 1];
                    bool prevWord = (p >= 'a' && p <= 'z') || (p >= '0' && p <= '9');
                    bool nextLower = i + 1 < s.Length && s[i + 1] >= 'a' && s[i + 1] <= 'z';
                    if (prevWord || nextLower) b.Append(' ');
                }
                b.Append(c);
            }
            return b.ToString();
        }

        /// <summary>机器键 → 规则表里的中文名。认不出来返回空。</summary>
        public static string NameForKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            Rule r = Find(Space(key).ToLowerInvariant());
            return r != null && !string.IsNullOrEmpty(r.Name) ? r.Name : string.Empty;
        }

        /// <summary>
        /// 把本地化报错串换成人话。
        /// 老版本存档里会残留这种条目：「Translation Error 'ScheduledEletricalMaintenance' in Mechanic」，
        /// 它躺在夜间报告里跟报错截图一样难看，读档时要顺手洗掉。
        /// 能查到规则名就用规则名，查不到就叫「站内异动」。
        /// </summary>
        public static string FixLocErrorText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = s.IndexOf("translation error", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return s;

            // 报错串有两种写法：Translation Error: KEY in Mechanic / Translation Error 'KEY' in Mechanic
            int p = i + 17;                                   // "translation error".Length
            while (p < s.Length && !IsKeyChar(s[p])) p++;     // 跳过冒号、空格、引号
            int q = p;
            while (q < s.Length && IsKeyChar(s[q])) q++;
            string key = q > p ? s.Substring(p, q - p) : string.Empty;

            int e = q;
            int tail = s.IndexOf(" in Mechanic", q, StringComparison.OrdinalIgnoreCase);
            if (tail >= 0) e = tail + 12;                     // 连尾巴一起吃掉
            else while (e < s.Length && (s[e] == '\'' || s[e] == '"')) e++;

            string name = NameForKey(key);
            if (name.Length == 0) name = "站内异动";
            return s.Substring(0, i) + name + s.Substring(e);
        }

        private static bool IsKeyChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9') || c == '_';
        }

        /// <summary>把键名整理成人话一点的兜底名（没有本地化时用）。</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            string t = s;
            if (t.StartsWith("event_", StringComparison.Ordinal)) t = t.Substring(6);
            t = t.Replace("_", " ").Trim();
            if (t.Length > 0) t = char.ToUpper(t[0]) + t.Substring(1);
            return t;
        }
    }
}