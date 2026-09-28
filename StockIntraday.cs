using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

namespace StockMarket
{
    /// <summary>
    /// 分时图（日内图）。
    ///
    /// 先说清楚一件事：《深空当铺》的店里**没有时钟**，一天什么时候结束由玩家点「收工」决定
    /// （游戏里的 TurnManager 服务的是突袭/飞船的回合制玩法，跟开店无关）。
    /// 所以这里的 x 轴不是「分钟」，而是把营业日切成 48 段的「进度」，
    /// 刻度写 开盘 / ¼ / 半场 / ¾ / 现在。玩家要的「每分钟成交量」在这个游戏里只能这样落地。
    ///
    /// 关键安全设计：今天的收盘价在开店前就已经定死在 StockState.Prices 里。
    /// 如果日内路径朝它收敛，玩家就能从盘面走势反推今天涨还是跌 —— 那是无风险套利。
    /// 所以路径的前 47 段是从昨收出发的**均值回归随机游走，完全不看收盘价**，
    /// 只有第 48 段（收工瞬间）才跳到收盘价，效果像尾盘跳空。
    /// 盘中任何人（包括玩家）都推不出收盘价，套利的口子就堵住了。
    ///
    /// 推进节拍 = 客人推进（主）+ 时间兜底（辅）：
    ///   · 每接待完一位客人推进一段，段长自适应（剩余段数 ÷ 估计剩余客数），客人多少都能铺满；
    ///   · 超过 IdleSeconds 没客人再推一段，避免画面长时间不动；
    ///   · 一整天一位客人都没有时不自己走，免得空店也长出一条假曲线。
    /// </summary>
    public static class StockIntraday
    {
        /// <summary>一天的段数。</summary>
        public const int Total = 48;

        /// <summary>时间兜底：多久没有客人就自己推一段。</summary>
        public const float IdleSeconds = 45f;

        /// <summary>营业判定迟滞：服务客人的瞬间状态会抖，抖这么多秒才算真收工。</summary>
        private const float CloseDelay = 2f;

        /// <summary>PlayerStore.StoreState 的 OPEN 值（CLOSED=0 / OPEN=1 / MORNING=2）。</summary>
        private const int StoreOpenValue = 1;

        private static int _day = -1;        // 当前路径对应的游戏天数
        private static int _step;            // 已推进段数 0..Total
        private static int _served;          // 今天已接待的客人数
        private static float _idle;          // 距上次推进的秒数
        private static float _closeDelay;    // 疑似收工已经持续了多久
        private static bool _open;           // 是否营业中（带迟滞，UI 与 LivePrice 都看它）
        private static IntPtr _lastClient;   // 上一帧看到的客人指针，变了他就是换人了

        // 存档恢复用的暂存值：读档时先记下来，等 Poll 开店那一下再套用
        private static int _resumeStep;
        private static int _resumeServed;

        /// <summary>id → 长度 Total+1 的价格路径（元）。</summary>
        private static readonly Dictionary<string, double[]> _path = new Dictionary<string, double[]>();
        /// <summary>id → 长度 Total 的逐段成交量。</summary>
        private static readonly Dictionary<string, double[]> _vol = new Dictionary<string, double[]>();

        // ── 对外读数 ──────────────────────────────────────────────────

        /// <summary>营业中？盘中价与收盘价的切换就看这个。</summary>
        public static bool IsLive { get { return _open; } }

        public static int Step { get { return _step; } }
        public static int Served { get { return _served; } }
        public static double Progress { get { return Total > 0 ? (double)_step / Total : 0.0; } }

        /// <summary>盘中实时价（分）。路径还没建好时退回收盘价，不会抛。</summary>
        public static long PriceCents(string id)
        {
            double[] p;
            if (_path.TryGetValue(id, out p) && p != null && p.Length > 0)
            {
                int i = _step < 0 ? 0 : (_step >= p.Length ? p.Length - 1 : _step);
                long c = StockState.ToCents(Demand(id, p[i], StockEconomy.Factor(id)));
                if (c > 0) return c;
            }
            return StockState.GetPrice(id);
        }

        /// <summary>路径上第 i 段的价格（元）。挂在撮合上：判断这一段有没有打到挂单价。</summary>
        public static double PriceAt(string id, int i)
        {
            double[] p;
            if (_path.TryGetValue(id, out p) && p != null && p.Length > 0)
            {
                if (i < 0) i = 0;
                if (i >= p.Length) i = p.Length - 1;
                // 撮合必须用玩家看得见的那口价，否则挂单会按「没抬过的路径价」成交，
                // 等于把店铺需求白送给挂单的人
                return Demand(id, p[i], StockEconomy.Factor(id));
            }
            return StockState.ToYuan(StockState.GetPrice(id));
        }

        /// <summary>
        /// 把店铺需求叠到日内价上（并夹在涨跌停之内）。
        /// 这就是「卖货影响当天实盘」那一下：每笔成交都会让对应板块的报价往上抬一点，
        /// 抬的幅度上限见 StockEconomy.PulseCap。收工时由 ApplyToClose 并进收盘价。
        /// </summary>
        private static double Demand(string id, double yuan, double factor)
        {
            if (factor <= 1.000001 || yuan <= 0.0) return yuan;
            double prev = PrevClose(id, yuan);
            double lim = StockDefs.LimitOf(StockDefs.Get(id));
            double w = yuan * factor;
            if (w > prev * (1.0 + lim)) w = prev * (1.0 + lim);
            if (w < 1.0) w = 1.0;
            return w;
        }

        /// <summary>已走部分的价格路径（元），给图表画「画到一半」的实时感。</summary>
        public static double[] VisiblePath(string id)
        {
            // 教程演示路径：整条都算「可见」，这样图上是一条走完的日线
            if (_demo)
            {
                double[] d;
                if (_demoPath.TryGetValue(id, out d) && d != null) return d;
            }
            double[] p;
            if (!_path.TryGetValue(id, out p) || p == null) return new double[0];
            int n = _step + 1;
            if (n < 1) n = 1;
            if (n > p.Length) n = p.Length;
            double[] r = new double[n];
            Array.Copy(p, r, n);
            // 图上最后一点必须和读数同一口径，否则「图上是 12.30、现价写 12.60」
            double factor = StockEconomy.Factor(id);
            if (factor > 1.000001)
            {
                for (int i = 0; i < n; i++) r[i] = Demand(id, r[i], factor);
            }
            return r;
        }

        /// <summary>已走部分的逐段量。</summary>
        public static double[] VisibleVolume(string id)
        {
            if (_demo)
            {
                double[] d;
                if (_demoVol.TryGetValue(id, out d) && d != null) return d;
            }
            double[] v;
            if (!_vol.TryGetValue(id, out v) || v == null) return new double[0];
            int n = _step;
            if (n < 0) n = 0;
            if (n > v.Length) n = v.Length;
            double[] r = new double[n];
            Array.Copy(v, r, n);
            return r;
        }

        /// <summary>当日均价（黄线）：已走各段价格按该段成交量加权。</summary>
        public static double AveragePrice(string id)
        {
            if (_demo)
            {
                double[] dp, dv;
                if (_demoPath.TryGetValue(id, out dp) && dp != null && dp.Length > 0)
                {
                    _demoVol.TryGetValue(id, out dv);
                    double s = 0.0, w0 = 0.0;
                    for (int i = 1; i < dp.Length; i++)
                    {
                        double w = (dv != null && i - 1 < dv.Length && dv[i - 1] > 0.0) ? dv[i - 1] : 1.0;
                        s += dp[i] * w;
                        w0 += w;
                    }
                    return w0 > 0.0 ? s / w0 : dp[dp.Length - 1];
                }
            }
            double[] p;
            if (!_path.TryGetValue(id, out p) || p == null || p.Length == 0) return 0.0;
            int n = Mathf.Min(_step, p.Length - 1);
            if (n <= 0) return p[0];

            double[] v;
            if (!_vol.TryGetValue(id, out v) || v == null) return p[n];

            double sum = 0.0, weight = 0.0;
            for (int i = 0; i < n; i++)
            {
                double w = (i < v.Length && v[i] > 0.0) ? v[i] : 1.0;
                sum += p[i + 1] * w;
                weight += w;
            }
            return weight > 0.0 ? sum / weight : p[n];
        }

        /// <summary>昨收（元）。历史不足两天时退回今日收盘，路径会退化成一条平线。</summary>
        public static double PrevClose(string id, double fallback)
        {
            List<long> list;
            if (StockState.PriceHistory.TryGetValue(id, out list) && list != null && list.Count >= 2)
            {
                long v = list[list.Count - 2];
                if (v > 0) return StockState.ToYuan(v);
            }
            return fallback;
        }

        // ── 每帧推进 ──────────────────────────────────────────────────

        /// <summary>
        /// 由 Core.OnUpdate 每帧调用。刻意放在面板开关之外 ——
        /// 玩家关着面板专心做生意的时候，行情也该继续走。
        /// </summary>
        public static void Poll()
        {
            try
            {
                PlayerStore s = PlayerStore.instance;
                if (s == null || !StockState.Loaded)
                {
                    _open = false;
                    _closeDelay = 0f;
                    return;
                }

                bool raw = ReadOpen(s);
                if (raw)
                {
                    _closeDelay = 0f;
                    if (!_open) BeginDay(s);
                }
                else if (_open)
                {
                    // 服务客人的瞬间状态会抖一下，所以给两秒迟滞再认账
                    _closeDelay += Time.deltaTime;
                    if (_closeDelay >= CloseDelay) EndDay();
                }

                if (!_open) return;

                AdvanceByClient(s);
                AdvanceByIdle();
                StockOrders.Match(_step);
                AnnounceIntradayNews();
            }
            catch (Exception ex)
            {
                Core.Log.Warning("盘中推进失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 营业中判定。主用 storeState 的 OPEN，读不到就退回 CanEndDay()。
        /// 这里用 (int) 取值而不是写 PlayerStore.StoreState.OPEN ——
        /// 嵌套枚举在 Il2Cpp 生成代码里的名字不一定稳，数字是稳的。
        /// </summary>
        private static bool ReadOpen(PlayerStore s)
        {
            try { return (int)s.storeState == StoreOpenValue; }
            catch { }
            try { return s.CanEndDay(); }
            catch { }
            return false;
        }

        /// <summary>开店：建好今天全天 26 支标的的日内路径。</summary>
        private static void BeginDay(PlayerStore s)
        {
            int resumeStep = _resumeStep;
            int resumeServed = _resumeServed;
            _resumeStep = 0;
            _resumeServed = 0;

            _open = true;
            _day = StockState.Today;
            _idle = 0f;
            _closeDelay = 0f;
            _lastClient = IntPtr.Zero;
            // 先定「今天有没有盘中消息」，再建路径：消息冲击是并进路径里一起算的，
            // 所以必须排在 BuildAllPaths 前面。
            StockEngine.PlanIntradayEvent();
            // 资金流同理：路径里的斜坡要用今天的净流入，必须在建路径前算好
            StockBots.BeginDay();
            BuildAllPaths();

            // 营业中存档读回来的进度：路径是按同一天同一个种子重放的，结果逐位一致
            _step = Mathf.Clamp(resumeStep, 0, Total);
            _served = Mathf.Max(0, resumeServed);

            StockState.Dirty = true;
            Core.Log.Msg("[盘中] 开盘，日内路径已生成（" + StockDefs.All.Length + " 支），第 "
                + StockState.Today + " 天，续到第 " + _step + "/" + Total + " 段");
        }

        /// <summary>收工：价格归位到今日收盘价，并撤掉当日所有挂单。</summary>
        public static void EndDay()
        {
            if (!_open) return;
            _open = false;
            _step = Total;        // 最后一段就是今日收盘价，无缝接上结算价
            _served = 0;
            _idle = 0f;
            _closeDelay = 0f;
            _lastClient = IntPtr.Zero;

            int cancelled = StockOrders.CancelAll("收工");
            // 收工这一刻，把今天卖货攒下的需求并进今日收盘价（不做就是无风险套利）。
            // 并完顺手把日内路径的最后一点（=收盘价）改成同一个数，
            // 否则收工后图上最后一点和读数会对不上。
            StockEconomy.ApplyToClose();
            foreach (KeyValuePair<string, double[]> kv in _path)
            {
                double[] p = kv.Value;
                if (p == null || p.Length == 0) continue;
                long c = StockState.GetPrice(kv.Key);
                if (c > 0) p[p.Length - 1] = StockState.ToYuan(c);
            }
            StockState.Dirty = true;
            Core.Log.Msg("[盘中] 收盘，日内路径归位到今日收盘价；撤单 " + cancelled + " 笔");
        }

        /// <summary>调试用：直接推进若干段。</summary>
        public static void Skip(int segs)
        {
            if (!_open) return;
            int left = Total - 1 - _step;
            if (left <= 0) return;
            int adv = Mathf.Clamp(segs, 1, left);
            _step += adv;
            _idle = 0f;
            StockOrders.Match(_step);
        }

        // ── 推进 ──────────────────────────────────────────────────────

        /// <summary>
        /// 客人推进：currentClientInstance 的指针换人了，就说明上一位走了、下一位进来了，
        /// 也就是「完成了一次交互」。这条判定不依赖任何方法名的语义猜测。
        /// </summary>
        private static void AdvanceByClient(PlayerStore s)
        {
            IntPtr cur = IntPtr.Zero;
            try
            {
                StoreClientInstance ci = s.currentClientInstance;
                if (ci != null) cur = ci.Pointer;
            }
            catch { return; }

            if (cur == IntPtr.Zero || cur == _lastClient) return;
            _lastClient = cur;
            _served++;

            int left = Total - 1 - _step;      // 最后一段永远留给收盘跳空
            if (left <= 0) return;

            // 段长自适应：客人多就每段小、客人少就每段大，一天总能铺满
            int est = _served + QueuedCount(s) + 3;
            int adv = (int)Math.Round((double)left / Math.Max(1, est));
            if (adv < 1) adv = 1;
            if (adv > left) adv = left;

            _step += adv;
            _idle = 0f;
            Core.Debug("[盘中] 接待第 " + _served + " 位客人，推进 " + adv + " 段 → " + _step + "/" + Total);
        }

        /// <summary>时间兜底：长时间没客人也让图动一动。</summary>
        private static void AdvanceByIdle()
        {
            _idle += Time.deltaTime;
            if (_idle < IdleSeconds) return;
            _idle = 0f;
            if (_served == 0) return;                  // 空店不自己走
            if (_step >= Total - 1) return;            // 最后一段留给收盘跳空
            _step++;
            Core.Debug("[盘中] 时间兜底推进 → " + _step + "/" + Total);
        }

        /// <summary>排队人数。取不到就按 0，只影响段长估计。</summary>
        private static int QueuedCount(PlayerStore s)
        {
            try
            {
                StoreClientManager m = s.storeClientManager;
                if (m == null) return 0;
                Il2CppSystem.Collections.Generic.List<StoreClient> stack = m.clientStack;
                return stack != null ? stack.Count : 0;
            }
            catch { return 0; }
        }

        // ── 盘中快讯 ──────────────────────────────────────────────────

        /// <summary>今天已经播报过盘中快讯了吗（同一天只播一次）。</summary>
        private static int _announcedDay = -1;

        /// <summary>
        /// 盘中消息砸下来的那一段：往日志和面板底栏各播一次快讯。
        /// 只播一次，之后「今日快讯」由面板自己去 StockEngine.TodayIntradayHeadline() 取，
        /// 玩家什么时候打开面板都能看到今天发生过什么。
        /// </summary>
        private static void AnnounceIntradayNews()
        {
            if (_announcedDay == StockState.Today) return;
            int trigger = StockEngine.IntradayTriggerStep();
            if (trigger == int.MaxValue || _step < trigger) return;

            string head = StockEngine.TodayIntradayHeadline();
            if (head.Length == 0) return;

            _announcedDay = StockState.Today;
            StockEngine.FlashText = head;
            StockEngine.FlashImpact = StockEngine.TodayIntradayImpact();
            Core.Log.Msg("[盘中快讯] " + head);
        }

        // ── 路径合成 ──────────────────────────────────────────────────

        private static void BuildAllPaths()
        {
            _path.Clear();
            _vol.Clear();
            // 开店了：真实行情接管，教程那条演示路径到此为止
            DemoEnd();
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef def = StockDefs.All[i];
                if (def == null) continue;
                double close = StockState.ToYuan(StockState.GetPrice(def.Id));
                double prev = PrevClose(def.Id, close);
                double[] p, v;
                BuildPath(def, prev, close, out p, out v);
                _path[def.Id] = p;
                _vol[def.Id] = v;
            }
        }

        /// <summary>
        /// 生成一支标的的日内路径。
        ///
        /// 前 47 段是从昨收出发的均值回归随机游走，**完全不看 close**：
        /// 看一眼就让玩家能从盘面反推今天涨跌，那套利就没法堵了。
        /// 第 48 段才等于 close，对应「收工那一刻揭晓收盘价」。
        ///
        /// 种子 = 标的 Id + 游戏天数，所以同一天无论重放多少次结果都一样，
        /// 读档不用另存路径，重算即可。
        /// </summary>
        private static void BuildPath(StockDef def, double prevClose, double close,
            out double[] path, out double[] volume)
        {
            double[] p = new double[Total + 1];
            if (prevClose <= 0.0) prevClose = 0.01;
            if (close <= 0.0) close = prevClose;

            double sigma = def.Sigma > 0.0 ? def.Sigma : 0.05;
            double cap = StockDefs.LimitOf(def);        // 跟日线同一个涨跌停口径
            double amp = prevClose * sigma * 0.55;      // 日内振幅要远小于日线 Sigma
            double limit = prevClose * cap;

            // 今天的盘中消息（如果有）：从 trigger 段起把价格整体推上一个/下一个台阶。
            // 这不泄漏今天的收盘价 —— 路径和收盘价是被同一比例一起缩放的，
            // p[47] 与 p[48] 的相对关系没变，玩家推不出尾盘跳空往哪边。
            double shock = StockEngine.IntradayShockFor(def.Id);
            int trigger = StockEngine.IntradayTriggerStep();

            // 资金流斜坡：净流入为正，今天这条线就整体微微上倾，反之微微下倾。
            // 幅度比日线那一项大一点（盘面上要看得见「有人在收」），
            // 但远小于 amp 的随机摆动，所以玩家从斜率反推不出今天的收盘价。
            double slope = StockBots.NetFlowPct(def.Id) * StockBots.PathImpact;

            uint seed = StockState.Hash32(def.Id + "|intra|" + StockState.Today);
            double v = prevClose;
            p[0] = prevClose;
            for (int i = 1; i < Total; i++)
            {
                v += (StockState.NextUnit(ref seed) - 0.5) * 2.0 * amp;
                v += (prevClose - v) * 0.16;            // 均值回归：日内不该走成单边趋势
                // 冲击只作用在「未冲击的走位」上，不能拿上一段的结果再乘一次，否则会指数放大
                double w = i >= trigger ? v * (1.0 + shock) : v;
                w += prevClose * slope * ((double)i / Total);   // 资金流斜坡：按进度线性铺开
                if (w > prevClose + limit) w = prevClose + limit;   // 夹在板内要在冲击之后
                if (w < prevClose - limit) w = prevClose - limit;
                if (w < 0.01) w = 0.01;
                p[i] = w;
            }
            p[Total] = close;
            path = p;

            // 量：沿用 StockIndicators.BuildBars 的量级公式（振幅越大成交越活跃），
            // 免得分时量跟日线量差出好几个数量级。
            double[] vol = new double[Total];
            double unit = 20000.0 / Math.Max(1.0, def.BasePrice);
            uint vseed = StockState.Hash32(def.Id + "|intravol|" + StockState.Today);
            for (int i = 0; i < Total; i++)
            {
                double a = Math.Abs(p[i + 1] - p[i]) / Math.Max(0.01, p[i]);
                vol[i] = Math.Round(unit * (0.40 + a * 40.0) * (0.55 + StockState.NextUnit(ref vseed) * 0.90));
            }
            volume = vol;
        }

        // ── 教程演示路径 ──────────────────────────────────────────────

        // 新手引导会带玩家走「盘中实时交易」那一站，可这时候他多半还没开店：店门没开、
        // 没有客人，日内路径是空的，图上只有一句「接待第一位客人后开始记录」——
        // 讲什么他都看不到。所以临时铺一条演示路径给他看。
        //
        // 两条铁律：
        //   1. 只在这支标的还没有真实路径、且不在营业中时才铺（真实数据永远优先）；
        //   2. 只撤自己铺的那条（_demo 标记），玩家当天走出来的真实行情一律不碰。
        // 这样「?」→「带我走一遍」重看教程、或者反复进出这一页，都不会把玩家的盘中数据弄丢。
        private static readonly Dictionary<string, double[]> _demoPath = new Dictionary<string, double[]>();
        private static readonly Dictionary<string, double[]> _demoVol = new Dictionary<string, double[]>();
        private static bool _demo;
        private static string _demoId = "";

        /// <summary>现在图上画的是演示路径（教程用），不是玩家的真实行情。</summary>
        public static bool DemoActive { get { return _demo; } }

        /// <summary>铺一条演示路径。有真实数据、正在营业、或者已经铺过同一支时什么都不做。</summary>
        public static void DemoBegin(string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id) || _open) return;
                if (_path.ContainsKey(id)) return;          // 今天开过店，真实数据优先
                if (_demo && _demoId == id) return;

                StockDef def = StockDefs.Get(id);
                if (def == null) return;

                double close = StockState.ToYuan(StockState.GetPrice(id));
                double prev = PrevClose(id, close);
                double[] p, v;
                // 借真实那一套公式生成，只是不落进 _path/_vol —— 挂单撮合、实时价、
                // 结算读的都是那两张表，演示数据一律不进去掺和。
                BuildPath(def, prev, close, out p, out v);

                _demoPath.Clear();
                _demoVol.Clear();
                _demoPath[id] = p;
                _demoVol[id] = v;
                _demoId = id;
                _demo = true;
                Core.Log.Msg("[盘中] 已铺教程演示路径（" + def.Name + "），开店后自动换成真实行情。");
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[盘中] 铺演示路径失败：" + ex.Message);
            }
        }

        /// <summary>撤掉演示路径。只有确实铺过才动，玩家真实数据一律不碰。</summary>
        public static void DemoEnd()
        {
            if (!_demo && _demoPath.Count == 0) return;
            bool had = _demo;
            _demo = false;
            _demoId = "";
            _demoPath.Clear();
            _demoVol.Clear();
            if (had) Core.Log.Msg("[盘中] 教程演示路径已撤下。");
        }

        // ── 存档 ──────────────────────────────────────────────────────

        public static void Reset()
        {
            _day = -1;
            _step = 0;
            _served = 0;
            _idle = 0f;
            _closeDelay = 0f;
            _open = false;
            _announcedDay = -1;
            _lastClient = IntPtr.Zero;
            _resumeStep = 0;
            _resumeServed = 0;
            _path.Clear();
            _vol.Clear();
            _demo = false;
            _demoId = "";
            _demoPath.Clear();
            _demoVol.Clear();
        }

        /// <summary>
        /// 格式：天数|已推进段数|已接待客数。路径本身不入档，按同种子重放。
        /// 没开盘的日子必须写 -1 当哨兵：收工后 _step 停在收盘那一段，
        /// 直接把 _day 写进档，夜里存盘再读回来时 BeginDay 会当成「盘还没收」接着走，
        /// 次日一整天价格就钉在昨收上（等于白送套利）。
        /// </summary>
        public static string Dump()
        {
            return (_open ? _day : -1) + "|" + _step + "|" + _served;
        }

        public static void Parse(string text)
        {
            _day = -1;
            _resumeStep = 0;
            _resumeServed = 0;
            if (string.IsNullOrEmpty(text)) return;

            string[] f = text.Split('|');
            if (f.Length < 3) return;

            int day, step, served;
            if (!int.TryParse(f[0], out day)) return;
            if (!int.TryParse(f[1], out step)) return;
            if (!int.TryParse(f[2], out served)) return;

            // 存的是别的天的进度就丢掉，不能把昨天走到哪了当成今天
            if (day != StockState.Today) return;

            _day = day;
            _resumeStep = Mathf.Clamp(step, 0, Total);
            _resumeServed = Mathf.Max(0, served);
        }
    }
}