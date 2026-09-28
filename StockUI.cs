using System;
using System.Collections.Generic;
using System.Text;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace StockMarket
{
    /// <summary>
    /// 【星际证券】主面板。整块界面是自建 UGUI，对标 PSPDA 的视觉语言：
    /// 深蓝底 + 圆角描边卡片 + 琥珀金数值，标题/正文/弱化三级字色。
    ///
    /// 结构（尺寸不写死，按屏幕算：1280×720 时约 1254×690，1920×1009 时约 1894×978）：
    ///   · 顶栏 —— 标题 + 五个数据块（总资产 / 总收益 / 浮动盈亏 / 可用资金 / 持仓市值）
    ///   · 左侧栏 —— 四个大分类 + 当前分类下的页面入口，选中态高亮
    ///   · 内容区 —— 当前页，每个分区一张圆角卡片
    ///   · 底栏 —— 状态提示 + 界面缩放 −/+/=
    ///
    /// 输入走游戏自带的 EventSystem（按钮就是真 UGUI Button，悬停自动高亮），
    /// 画布最底层铺一张全屏透明拦截层，挡住穿透到游戏本体的点击。
    /// </summary>
    internal static class StockUI
    {
        /// <summary>黑市开户证明价格。</summary>
        public const int LicenseCost = 200;

        // ── 面板尺寸 ──────────────────────────────────────────────────
        // 以前这块写死 1244×700：在 1280×720 的窗口里刚好铺满，切到 1920×1009
        // 就只占屏幕的 65%，右边空一大片、字也显得小，玩家反馈「界面有点挤」。
        // 现在全部由 Layout() 按当前屏幕算出来，面板在任意分辨率下都贴着屏幕边，
        // 内容区跟着变宽变高，表格列、图表、行数一起摊开。
        // 注意：这些量必须保持 static（不是 const），否则改不了。
        private static float W = 1244f;
        private static float H = 700f;
        private static float Pad = 12f;
        private static float HeaderY = 12f;
        private static float HeaderH = 54f;
        private static float BodyY = 74f;
        private static float BodyH = 560f;
        private static float FooterY = 650f;
        private static float FooterH = 30f;
        private static float NavW = 156f;
        private static float NavGap = 14f;
        private static float ContentX = 182f;
        private static float ContentW = 1050f;

        /// <summary>
        /// 按当前屏幕算出整套版式尺寸。Build() 一开始就得调，之后所有页都按它摆。
        /// 1280×720 上算出来跟老的 1244×700 几乎一样（不会退化），
        /// 1920×1009 上则铺到 1894×978，内容区宽了 57%、高了 51%。
        /// </summary>
        private static void Layout()
        {
            // 下限 600 是算过的：再矮的话 BodyH 撑不住最少 6 行 + 分页 + 图例，
            // 内容会压到面板外面去。
            //
            // 排版单位 = 屏幕像素 ÷ 界面缩放：画布上 1 单位 = 缩放倍数个像素，
            // 所以屏幕在画布坐标系里只有 Screen/缩放 那么大。旧写法直接拿
            // Screen 尺寸当单位，一旦放大（或换到更小的分辨率）面板就被撑出屏幕，
            // 边上的按钮点不到（见问题截图）。折进来之后，缩放只让内容变大，
            // 面板永远刚好卡在屏幕里。
            float k = _scale <= 0.01f ? 1f : _scale;
            float sw = Screen.width / k;
            float sh = Screen.height / k;

            // 留 26 / 30 的余量，顺便取偶数避免半像素导致描边发虚
            W = Mathf.Floor((sw - 26f) * 0.5f) * 2f;
            H = Mathf.Floor((sh - 30f) * 0.5f) * 2f;

            Pad = 12f;
            HeaderY = 12f;
            HeaderH = 54f;
            BodyY = 74f;
            FooterH = 30f;
            FooterY = H - 48f;
            BodyH = FooterY - 10f - BodyY;

            // 左栏跟着屏宽走一点：窄屏 176，宽屏最多 210。
            // 下限从 152 提到 168 是因为导航文案前面挂了图标（「▤ 交易明细记录」≈131px），
            // 而 MakeButton 左对齐只给 14px 内边距，152 的栏放不下会顶出去。
            // 再提到 176：图标从「文字前缀」改成 22px 的色块徽章后，文案还要再让 33px，
            // 169 的栏刚好放不下最长的那行「盘中实时交易 ★」（≈131px），会顶到内容区上。
            NavW = Mathf.Round(Mathf.Clamp(W * 0.135f, 176f, 210f));
            NavGap = 14f;
            ContentX = Pad + NavW + NavGap;
            ContentW = W - ContentX - Pad;

            // 一页能列几行，也是按内容区高度算：1280 屏还是 8 行，1920 屏一页看 14 行
            RowStep = 42f;
            RowH = 38f;
            PageRows = Mathf.Clamp((int)((BodyH - 192f) / RowStep), 6, 14);

            // 技术分析：主图占大头，两个副图平分剩下的
            float techFlex = Mathf.Max(210f, BodyH - 146f - 56f);
            TechMainH = Mathf.Round(techFlex * 0.58f);
            TechSubH = Mathf.Round((techFlex - TechMainH - 4f) * 0.5f);
            TcMainY = 130f;
            TcT1Y = TcMainY + TechMainH + 4f;
            TcSub1Y = TcT1Y + 24f;
            TcT2Y = TcSub1Y + TechSubH + 4f;
            TcSub2Y = TcT2Y + 24f;

            // 盘中交易：上面一张分时图卡，剩下的给「挂单表单 + 挂单列表」
            IntraTopH = Mathf.Min(Mathf.Round(BodyH * 0.59f), 520f);
            IntraChartH = IntraTopH - 90f;
            IntraFormY = IntraTopH + 8f;
            IntraFormH = BodyH - IntraFormY;

            // 杠杆页：左卡（表单）按比例，右卡吃掉剩下的
            LvLeftW = Mathf.Clamp(Mathf.Round(ContentW * 0.42f), 440f, 760f);

            // 高级工具页：左卡只放六行功能名，窄一点；右卡留给正文
            VipLeftW = Mathf.Clamp(Mathf.Round(ContentW * 0.28f), 272f, 400f);
            // 右卡从上往下：标题 12 / 适合谁 48 / 标的行 78 / 正文 124 …，正文起点跟着板高微调
            VipBodyY = BodyH >= 480f ? 124f : 116f;

            // 智能选股页：左卡（推荐）略微多吃一点，右卡放命中率与说明
            PkLeftW = Mathf.Clamp(Mathf.Round(ContentW * 0.54f), 460f, 720f);

            // 行情表列宽：按权重摊满内容区（左边给星标列留 38，右边留 44）
            // 右留白从 24 加到 44：最后一列「状态」原来贴着卡片右描边，玩家反馈看着像被切了。
            float qTotal = 0f;
            for (int i = 0; i < QuoteColWeight.Length; i++) qTotal += QuoteColWeight[i];
            for (int i = 0; i < QuoteColGap.Length; i++) qTotal += QuoteColGap[i];
            // 下限从 1f 降到 0.4f：原来 ContentW < 1154 时列宽一点都不缩，
            // 整张表按原始权重摊出去会右溢出，最后一列「状态」被窗口裁掉
            // （见 问题截图/可交易文本偏移.png）。
            float qk = Mathf.Max(0.4f, (ContentW - 106f) / qTotal);
            float qx = 62f;
            for (int i = 0; i < QuoteColWeight.Length; i++)
            {
                QuoteColX[i] = qx;
                QuoteColW[i] = Mathf.Round(QuoteColWeight[i] * qk);
                qx += QuoteColW[i] + (i < QuoteColGap.Length ? QuoteColGap[i] : 0f);
            }

            // 持仓表同理：两端各留 24
            float hTotal = 0f;
            for (int i = 0; i < HoldColWeight.Length; i++) hTotal += HoldColWeight[i];
            for (int i = 0; i < HoldColGap.Length; i++) hTotal += HoldColGap[i];
            float hk = Mathf.Max(0.4f, (ContentW - 48f) / hTotal);
            float hx = 24f;
            for (int i = 0; i < HoldColWeight.Length; i++)
            {
                HoldColX[i] = hx;
                HoldColW[i] = Mathf.Round(HoldColWeight[i] * hk);
                hx += HoldColW[i] + (i < HoldColGap.Length ? HoldColGap[i] : 0f);
            }
        }

        // ── 字号层级 ──────────────────────────────────────────────────
        private const float FsTitle = 30f;
        private const float FsPageTitle = 26f;
        private const float FsCardTitle = 22f;
        private const float FsBody = 20f;
        private const float FsCell = 18f;
        private const float FsSmall = 15f;

        // ── 页号 ──────────────────────────────────────────────────────
        // 页号 = NavTitles 的下标 = NavGroupPages 里的引用 = Guides 的下标，
        // 四处必须同步。下面按「分类」成段排，加页时插在所属分类里，
        // 然后挨个把这段后面的号顺延（其余代码全走常量，不用动）。
        //
        //   主页：总览 → 事件公告 → 公司财报 → 股市股评 → 智能选股 → 高级工具 → 新手任务
        //   交易：行情报价 → 交易下单 → 盘中实时交易 → 我的持仓 → 交易明细记录 → 技术分析 → 杠杆交易
        //   资金：资产走势 → 资金划转 → 黑市开户
        //   其它：好友 → 调试工具
        //
        // 分类内部排序按「玩家实际会走的路」：先在行情页挑 → 去交易下单 → 想盯盘挂单 →
        // 看持仓 → 翻明细 → 想深究再看技术分析 → 最后才是杠杆这种高风险玩法。
        private const int PageOverview = 0;
        private const int PageEvent = 1;
        private const int PageReport = 2;
        private const int PageReview = 3;
        private const int PagePicker = 4;
        private const int PageVip = 5;
        private const int PageQuest = 6;

        private const int PageQuote = 7;
        private const int PageTrade = 8;
        private const int PageIntra = 9;
        private const int PageHold = 10;
        private const int PageJournal = 11;
        private const int PageTech = 12;
        private const int PageLever = 13;

        private const int PageAsset = 14;
        private const int PageFund = 15;
        private const int PageLicense = 16;

        private const int PageFriend = 17;
        private const int PageDebug = 18;
        private const int PageCount = 19;

        // 这张表必须严格按页号排（NavTitles[page]）：侧栏文案、切页日志、
        // 分组的排布全都直接用它索引。之前把「调试工具」挪到数组末尾，
        // 结果 PageDebug(12) 显示成「公司财报」、点「股市股评」进的是财报页。
        private static readonly string[] NavTitles =
        {
            "总览", "事件公告", "公司财报", "股市股评", "智能选股", "高级工具", "新手任务",
            "行情报价", "交易下单", "盘中实时交易", "我的持仓", "交易明细记录", "技术分析", "杠杆交易",
            "资产走势", "资金划转", "黑市开户",
            "好友",
            "调试工具"
        };

        // ── 左侧两级导航 ──────────────────────────────────────────────
        // 上面 2×2 是分类，下面只列当前分类的页面。
        // 列表竖向可用高度 = BodyH - 88，每项 46，所以一组最多放 8 项。
        // 「资金」这一组是给银行留的位置，加银行时只要往这里补一个页号。
        private static readonly string[] NavGroups = { "主页", "交易", "资金", "其它" };
        private static readonly int[][] NavGroupPages =
        {
            new[] { PageOverview, PageEvent, PageReport, PageReview, PagePicker, PageVip, PageQuest },
            new[] { PageQuote, PageTrade, PageIntra, PageHold, PageJournal, PageTech, PageLever },
            new[] { PageAsset, PageFund, PageLicense },
            new[] { PageFriend, PageDebug },
        };
        private static int _navGroup;
        private static readonly UiButton[] _navGroupBtn = new UiButton[NavGroups.Length];

        // ── 导航图标 ──────────────────────────────────────────────────
        // 一列纯文字看着眼晕，所以每页配一个图形前缀，靠「形状 + 位置」而不是靠读字认入口。
        // 字体是 Noto Sans SC，emoji 一概没有（🔍🌐💰 这类会变空白），所以这里只用
        // 图形符号区的字：几何图形、箭头、少量杂项符号。仍然按页号排，缺一个就错位。
        //
        // 每个图标在画之前都要过一遍 Fonts.Has()，字体缺字就自动省掉这个图标 ——
        // 宁可某一行没图标，也不能在侧栏上排出一列方块。
        private static readonly string[] NavIcons =
        {
            "◎",   // 0  总览（靶心：一眼看全盘）
            "⚑",   // 1  事件公告（旗子：挂出来的消息）
            "▦",   // 2  公司财报（表格）
            "✎",   // 3  股市股评（笔：有人写观点）
            "✧",   // 4  智能选股（闪：工具替你挑）
            "⚙",   // 5  高级工具（齿轮）
            "☑",   // 6  新手任务（打勾框）
            "⇅",   // 7  行情报价（上下箭头：涨跌）
            "☰",   // 8  交易下单（单子）
            "◔",   // 9  盘中实时交易（切了一块的饼）
            "¥",   // 10 我的持仓（钱）
            "▤",   // 11 交易明细记录（账本横线）
            "⊙",   // 12 技术分析（放大镜）
            "⚖",   // 13 杠杆交易（天平）
            "↗",   // 14 资产走势（向上的线）
            "⇄",   // 15 资金划转（对调箭头）
            "◈",   // 16 黑市开户（证章）
            "☺",   // 17 好友（笑脸）
            "⚒",   // 18 调试工具（工具）
        };

        // 上面这些图形字 Noto Sans SC 大半都没有（实测 19 个里只有 8 个能画），
        // 缺的那个不能就这么空着 —— 一列里有的带图标有的不带，看着像坏了。
        // 所以每一页再配一个「单字徽章」兜底：底色按分组上色，里面写这个字。
        // 汉字是 CJK 字体一定有的，不会再出现方块或空白。
        private static readonly string[] NavChars =
        {
            "盘",   // 0  总览
            "报",   // 1  事件公告
            "财",   // 2  公司财报
            "评",   // 3  股市股评
            "选",   // 4  智能选股
            "具",   // 5  高级工具
            "任",   // 6  新手任务
            "价",   // 7  行情报价
            "单",   // 8  交易下单
            "时",   // 9  盘中实时交易
            "仓",   // 10 我的持仓
            "账",   // 11 交易明细记录
            "析",   // 12 技术分析
            "杠",   // 13 杠杆交易
            "势",   // 14 资产走势
            "转",   // 15 资金划转
            "户",   // 16 黑市开户
            "友",   // 17 好友
            "调",   // 18 调试工具
        };

        /// <summary>徽章里画什么：字体有的图形字优先，缺字就用单字兜底。</summary>
        private static string NavMark(int page)
        {
            string g = IconOf(page);
            if (g.Length > 0) return g;
            return page >= 0 && page < NavChars.Length ? NavChars[page] : "";
        }

        /// <summary>
        /// 核心玩法页：新手必须走的买卖闭环（看行情 → 下单 → 看持仓 → 看图 → 盘中挂单）。
        /// 这几页在侧栏上挂一颗星，其余功能页不带星，免得星一多就等于没标。
        /// 盘中实时交易也算核心 —— 它和「交易下单」是同一个动作的两种下法。
        /// </summary>
        private static bool IsCorePage(int page)
        {
            return page == PageQuote || page == PageTrade || page == PageIntra
                || page == PageHold || page == PageTech;
        }

        /// <summary>这一页属于哪个分组（下标 = NavGroups）。找不到按最后一组算。</summary>
        private static int NavGroupOf(int page)
        {
            for (int g = 0; g < NavGroupPages.Length; g++)
            {
                int[] set = NavGroupPages[g];
                for (int i = 0; i < set.Length; i++) if (set[i] == page) return g;
            }
            return NavGroupPages.Length - 1;
        }

        /// <summary>
        /// 导航图标的颜色：按所属分组上色。
        /// 原来整列图标跟正文一个灰，扫过去分不出哪是哪（见 问题截图/左边能不能用类似⭐…），
        /// 现在每一组一个色：主页青、交易金、资金绿、其它蓝紫。
        /// </summary>
        private static Color NavIconColor(int page)
        {
            switch (NavGroupOf(page))
            {
                case 0: return Palette.Cyan;      // 主页
                case 1: return Palette.Gold;      // 交易
                case 2: return Palette.Down;      // 资金
                default: return Palette.Series1;  // 其它
            }
        }

        /// <summary>图标前缀；字体缺这个字就返回空串。</summary>
        private static string IconOf(int page)
        {
            if (page < 0 || page >= NavIcons.Length) return string.Empty;
            string s = NavIcons[page];
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (!Fonts.Has(s[0])) return string.Empty;
            return s;
        }

        /// <summary>核心玩法的星标；⭐ 缺字就退成 ★。</summary>
        private static string CoreMark()
        {
            return Fonts.Has('⭐') ? "⭐" : "★";
        }

        /// <summary>
        /// 侧栏按钮上的文案：名称 + 核心星标 / 待领奖提醒。
        /// 图标不再拼在文字里 —— 它现在是行左边那颗色块徽章（见 AddNavBadge），
        /// 字体缺字也有单字兜底，不会再出现「有的行有图标有的行没有」。
        /// 好友页的未读也不在这里拼，走导航上那颗红色数字气泡（见 BuildNav 里的 NavBadge）。
        /// </summary>
        private static string NavLabel(int page)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(NavTitles[page]);
            if (IsCorePage(page)) sb.Append(" <color=").Append(Palette.HexOf(Palette.Gold)).Append(">")
                .Append(CoreMark()).Append("</color>");
            // 任务页有可领奖励时，入口上再补一颗星
            if (page == PageQuest && StockQuest.ClaimableCount() > 0)
                sb.Append(" <color=").Append(Palette.HexOf(Palette.Gold)).Append(">★</color>");
            return sb.ToString();
        }

        // ── 新手引导 ──────────────────────────────────────────────────
        /// <summary>
        /// 引导走完（或跳过）后 TutorialStep 记成这个值，之后不再自动弹。
        /// 老存档里这个哨兵是 5（当时教程正好 5 步）；教程扩到 8 步后，5 会被当成
        /// 「第 6 步」一进档就弹引导，所以挪到 100，并在载入时把老的 5 迁过来
        /// （见 StockState 载入 tut 的那一行）。
        /// </summary>
        private const int TutorialDone = 100;

        private sealed class TutStep
        {
            public string Title;
            public string Body;
            public int Page;      // 这一步讲哪一页，点「下一步」会自动切过去
            public string Zone;   // 指向这一页的哪一小块（见 TutRect），"" 表示不指目标
            // 要玩家真的动手做一次才自动往下走："transfer" 转入股市 / "buy" 买入 / "sell" 卖出。
            // "" 表示纯讲解，读完点「下一步」。带 Need 的步会临时放开页面交互（见 SetPageInput），
            // 所以只给主线里「不亲手做一次就没法开局」的步挂。
            public string Need;
            // 气泡的固定贴边位置："" 走默认的「围着目标找空位」；"left"/"right" 表示钉在内容区顶边，
            // 分别靠左、靠右摆。带 Need 的步必须用它 —— 玩家要点的那排按钮就在内容区下方，
            // 默认算法在窄屏上找不到空位，会把气泡压到按钮上，那一步就点不了了。
            public string Bubble;
        }

        // 正文里的关键词着色。这里写死字面量而不是 Palette.HexOf()：
        // 这些字段比 Palette 那些静态字段先初始化，调用会拿到默认色。
        private const string TK = "<color=#FFC24A>";   // 金：功能名 / 按钮 / 要点的词
        private const string TR = "<color=#E5533D>";   // 红：风险、会被扣钱、别做的事
        private const string TC = "<color=#9EC7DE>";   // 青：补充说明
        private const string TE = "</color>";

        // 教程分两层：
        //   主线——不看完就没法开局的「买卖闭环」：转钱 → 挑股 → 买入 → 看盈亏 → 卖出 → 领任务。
        //     只讲股票交易必须用到的核心玩法，其他功能一律不塞进主线。
        //   本页导览——「点进去再弹」：主线走完之后，玩家第一次进某一页就自动放一遍该页的导览；
        //     也可以在任意页点右上角的「?」看说明卡，再点「带我走一遍」重看。
        //     同一页的步必须挨在一起，页面导览靠取本页步号的 min/max 来跑。
        //
        // 一步只讲一件事：气泡贴在目标块旁边让开它，箭头指过去，目标描一圈金边
        // —— 像软件讲自己的界面那样，讲到哪里指到哪里，而不是一巴掌糊一整页字。
        // 排版约定：标题一句话（≤14 字），正文 1~2 句、段落之间空一行（\n\n）。
        // 正文压到这个量是刻意的：气泡挡着界面，写多了没人读，讲完一步就往下走。
        private static readonly TutStep[] TutorialMain =
        {
            // ── 总览：先认地方 ──
            new TutStep
            {
                Page = PageOverview, Zone = "title",
                Title = "欢迎来到星际证券",
                Body = "这是空间站的股票市场，面板上每个词都是白话。跟着箭头走完这一遍，"
                     + "你就知道怎么" + TK + "买、怎么卖、在哪看赚了多少" + TE + "。\n\n"
                     + "忘了随时点右上角的 " + TK + "「?」" + TE
                     + "，里面有「带我走一遍」能重放这段引导。",
            },
            new TutStep
            {
                Page = PageOverview, Zone = "nav",
                Title = "左边导航怎么用",
                Body = "上面四个方块是分类（" + TK + "主页 / 交易 / 资金 / 其它" + TE
                     + "），点一下展开，下面那串才是具体入口。\n\n"
                     + "后面带 " + TK + "★" + TE + " 的是必用的核心功能，跟着它走就不会漏。",
            },
            new TutStep
            {
                Page = PageOverview, Zone = "topstat",
                Title = "总览页看什么",
                Body = "顶上一排是你现在的身家：" + TK + "总资产" + TE + "（店铺现金 + 股票账户 + 持仓市值）、"
                     + TK + "总收益" + TE + "、以及只看手上没卖股票的 " + TK + "浮动盈亏" + TE + "。\n\n"
                     + "下面「运营概览」是四笔账：还有几天结算、手上有几支、卖出赚了多少、分红拿了多少。",
            },

            // ── 资金：先把钱搬进来 ──
            new TutStep
            {
                Page = PageFund, Zone = "wallets",
                Title = "股票账户和店铺现金是两个口袋",
                Body = "左边是店铺现金，右边是股票账户，" + TK + "两个口袋各管各的，不会自动互通" + TE + "。\n\n"
                     + "买卖只动股票账户，所以第一步得先把钱搬过来。",
            },
            new TutStep
            {
                Page = PageFund, Zone = "fundin", Need = "transfer", Bubble = "right",
                Title = "动手：把钱搬进来",
                Body = "金额已经帮你填好 " + TK + "100 元" + TE + "，直接点 " + TK + "「转入股市」" + TE
                     + " 就行。\n\n"
                     + TR + "这一步必须亲手做一次" + TE + "，做完自动进下一步。",
            },

            // ── 行情：挑一支 ──
            new TutStep
            {
                Page = PageQuote, Zone = "list", Need = "pick",
                Title = "动手：挑一支股票",
                Body = "一行一支：" + TK + "「标签」" + TE + " 写它属于哪一层、靠什么赚钱，"
                     + TK + "「近期方向」" + TE + " 是对接下来几天的判断。\n\n"
                     + TR + "点任意一行" + TE + " 直接进它的下单页 —— 必须真的点一下。",
            },

            // ── 买入流程：选标的 → 填数量 → 点买入 ──
            new TutStep
            {
                Page = PageTrade, Zone = "bottom",
                Title = "先选股、再填数量",
                Body = "左上角的下拉框换股票；中间那排 " + TK + "−10 / −1 / +1 / +10" + TE
                     + " 调数量，也能直接在输入框里敲，最少 " + TK + "1 股" + TE + "。\n\n"
                     + "右边写着「最多可买多少股」，按你现在股票账户的钱算。",
            },
            new TutStep
            {
                Page = PageTrade, Zone = "tradebtn", Need = "buy", Bubble = "left",
                Title = "动手：点「买入」",
                Body = "填好数量（默认 10 股，钱不够就改成 1 股）点 " + TK + "「买入」" + TE
                     + "，成交后自动进下一步。\n\n"
                     + "手续费买卖各收一次，刚买完会显示小亏一点。",
            },
            new TutStep
            {
                Page = PageTrade, Zone = "tradebtn", Need = "sell", Bubble = "left",
                Title = "动手：点「卖出」",
                Body = "填好要卖几股，点 " + TK + "「卖出」" + TE + "，钱就回到股票账户。\n\n"
                     + TR + "这一步也要亲手做一次" + TE + "；刚买完就卖会亏掉那笔手续费。",
            },

            // ── 盘中实时交易：除了直接买卖，还有「挂着价让它自己成交」这条路 ──
            new TutStep
            {
                Page = PageIntra, Zone = "top",
                Title = "盘中实时交易",
                Body = "白线是此刻成交价，黄线是今天的均价，灰虚线是昨天的收盘价；"
                     + TK + "鼠标压上去" + TE + "能读出那一段的价格。\n\n"
                     + "下面那排 " + TK + "「挂单」" + TE + "：填「跌到多少买」或「涨到多少卖」，打到价自动成交；"
                     + TR + "收工没成交的会自动撤销" + TE + "。",
            },

            // ── 智能选股：不会挑就先用它，免费但别全信 ──
            new TutStep
            {
                Page = PagePicker, Zone = "pickerL",
                Title = "不会挑就先用它",
                Body = TK + "【智能选股】" + TE + "是白送的：一次给 "
                     + TK + StockVip.PickerSize + " 支" + TE + "，每 " + StockVip.PickerCycleDays
                     + " 天恢复 " + StockVip.PickerTotal + " 批。\n\n"
                     + TR + "它只是根据免费数据分析的，不一定稳定，更适合不会挑的新手。" + TE,
            },

            // ── 新手任务：领启动资金 ──
            new TutStep
            {
                Page = PageQuest, Zone = "top",
                Title = "顺手把启动资金领了",
                Body = "六条任务照着上面这几步设计，做到就能点 " + TK + "「领奖」" + TE
                     + " 拿现金，全领完是 " + TK + StockQuest.RewardCapTotal + " 元" + TE + "。",
            },
        };


        // 下面是「本页导览」的讲稿：主线不讲的页面全部收在这里，玩家第一次点进去时自动弹一遍，
        // 也可以随时点右上角「?」→「带我走一遍」重看。数组顺序不影响玩法，但同一页的步必须挨在一起。
        private static readonly TutStep[] TutorialPro =
        {
            // ── 技术分析 ──
            new TutStep
            {
                Page = PageTech, Zone = "top",
                Title = "K 线怎么看",
                Body = "蜡烛的粗实体是当天的 " + TK + "开盘价到收盘价" + TE
                     + "，上下两根细线是当天的最高价和最低价。" + TC + "红涨绿跌。" + TE + "\n\n"
                     + "该页面看不懂完全不影响你赚钱，这是为更硬核的玩家设计的，不想看就跳过这一段。",
            },
            new TutStep
            {
                Page = PageTech, Zone = "bottom",
                Title = "均线和下面两个副图",
                Body = TK + "MA5 / MA20" + TE + " 是最近 5 天、20 天的平均价，价格站上均线说明最近在走强；"
                     + "也能换成布林带看上下轨。\n\n"
                     + "下面两个副图可以轮换看 " + TK + "成交量 / MACD / KDJ / RSI" + TE + "，"
                     + "用来看「现在是不是炒得太热了」。右上角 ◀ ▶ 直接换股票，不用回行情页；"
                     + "看完点「去下单」就到交易页，点「返回」退回刚才那一页。",
            },

            // 盘中交易已升格成主线的一站（见 TutorialMain），这里不再重复讲一遍

            // ── 杠杆 ──
            new TutStep
            {
                Page = PageLever, Zone = "left",
                Title = "左边这排数字",
                Body = TK + "担保比例" + TE + " 是「你自己的钱 ÷ 借来的钱」，这个数越低越危险。\n\n"
                     + "下面写着欠款、利息和可用额度，再往下的输入框就是操作金额。",
            },
            new TutStep
            {
                Page = PageLever, Zone = "left",
                Title = "六个按钮干什么",
                Body = "开户 → 融资借钱 → 还款 → 融券卖出 → 买券还券 → 一键还清，六个按钮就是全部操作。\n\n"
                     + TC + "总资产 300 元可开融资融券，100 元可开场外配资。" + TE
                     + "搞不清就先点「一键还清」，把欠的全都还掉。",
            },
            new TutStep
            {
                Page = PageLever, Zone = "right",
                Title = "爆仓是什么意思",
                Body = "右边的「怎么玩（大白话）」和风险提示，建议先读一遍。\n\n"
                     + TR + "你出的自有资金亏到一定比例会被强制平仓（爆仓）" + TE
                     + "，剩下的欠款和利息还是要还。" + TC + "新手完全可以先不碰。" + TE,
            },

            // ── 事件公告 ──
            new TutStep
            {
                Page = PageEvent, Zone = "gevents",
                Title = "左栏是站里真发生的事",
                Body = "这页分成两栏。左边 " + TK + "「空间站事件」" + TE
                     + "读的是游戏自己的事件 —— 供水系统故障、全站停电、游客涌入、"
                     + "治安部突袭黑市，都是你在日历里能看到的那批。\n\n"
                     + "每条都写了 " + TK + "会推高还是打压哪些股票" + TE
                     + "（红涨绿跌），右边还标着 " + TK + "预计几天后结束" + TE + "。",
            },
            new TutStep
            {
                Page = PageEvent, Zone = "right",
                Title = "右栏是行情模拟出来的",
                Body = "右边 " + TK + "「市场事件」" + TE
                     + "是模组自己抽的行情故事，只给一个每日冲击百分比，时间不会明说，"
                     + "只写「势头正猛 / 正在减弱」。\n\n"
                     + TC + "两栏都会实打实改股价，收工后还会以夜间新闻再提你一遍。" + TE,
            },

            // ── 黑市开户 ──
            new TutStep
            {
                Page = PageLicense, Zone = "top",
                Title = "黑市股是什么",
                Body = "带 " + TK + "[黑市-xxx]" + TE + " 标签的股票，收益和波动都比别处大，但要先办张证明。\n\n"
                     + TR + "没办之前，那几支的「买入」按钮是灰的，点不动。" + TE,
            },
            new TutStep
            {
                Page = PageLicense, Zone = "bottom",
                Title = "200 元买张门票",
                Body = "花 " + TK + "200 元店铺现金" + TE + " 买一张许可证，一次买断、永久有效。\n\n"
                     + TC + "提醒：黑市手续费 4.5% 是全场最贵的一档，一进一出就吃掉近一成。" + TE,
            },

            // ── 公司财报 ──
            new TutStep
            {
                Page = PageReport, Zone = "list",
                Title = "财报怎么读",
                Body = "一行一家公司：" + TK + "营收 / 净利 / 负债率 / 市盈率 / 分红预案" + TE + "，翻页就能横向比。\n\n"
                     + "数字好不一定马上涨，但它会慢慢把股价往公允价那边拉。",
            },
            new TutStep
            {
                Page = PageReport, Zone = "bottom",
                Title = "点一行看细节",
                Body = "点上面任意一行，下面那块就换成这家公司的 " + TK + "股东名单和公告" + TE + "。\n\n"
                     + TC + "每 21 天出新一期。" + TE,
            },

            // ── 股市股评 ──
            new TutStep
            {
                Page = PageReview, Zone = "list",
                Title = "博主、水军和传闻",
                Body = "各家博主天天在喊涨喊跌，一帖一个标的和目标价。\n\n"
                     + TR + "评论区还会混进水军" + TE + "，光看名字和位置认不出来。"
                     + TC + "传闻只在有操盘手的股票上才会出现。" + TE,
            },
            new TutStep
            {
                Page = PageReview, Zone = "bottom",
                Title = "只看历史胜率",
                Body = "点上面任意一位博主，下面就是他这帖的正文、传闻和评论区。\n\n"
                     + TK + "「历史胜率」" + TE + " 才是关键：精选博主明显更准，野生博主基本反着来。"
                     + TC + "谁的胜率高就跟谁。" + TE,
            },

            // ── 高级工具 ──
            new TutStep
            {
                Page = PageVip, Zone = "left",
                Title = "七项工具是什么",
                Body = "左边七项都是 " + TK + "一次性买断" + TE + "："
                     + TC + "逐笔成交 500 / 十档盘口 600 / 资金透视 700 / AI 诊股 800 / 高级回测 900 / 主力大单·智能盯盘 1000 / 物价雷达 600 元" + TE + "。\n\n"
                     + "花的是 " + TK + "店铺现金" + TE + "，开通后永久有效，以后再点开不花钱。",
            },
            new TutStep
            {
                Page = PageVip, Zone = "right",
                Title = "那要不要买",
                Body = TR + "它们只是把已经看得到的东西讲得更细，不会多给你一条内幕消息。" + TE + "\n\n"
                     + TC + "一件都不买也完全不影响你赚钱。" + TE,
            },

            // 智能选股已升格成主线的一站（见 TutorialMain），这里不再重复讲一遍

            // ── 资产走势 ──
            new TutStep
            {
                Page = PageAsset, Zone = "top",
                Title = "这条线画的是什么",
                Body = "是你的 " + TK + "全部身家" + TE + "（店铺现金 + 股票账户 + 持仓市值）随天数变化，"
                     + TC + "不是某支股票的价格。" + TE + "\n\n"
                     + "刚开户那几天是平的，因为你还没把钱转进来，别以为坏了。",
            },
            new TutStep
            {
                Page = PageAsset, Zone = "bottom",
                Title = "下面的每日明细",
                Body = "历史每天的余额和持仓市值都在下面那张表里，想知道哪天亏的钱就去那儿翻。\n\n"
                     + TC + "看这条线只要关心一件事：整体在往上走就行，某一天回撤别慌。" + TE,
            },

            // ── 好友 ──
            new TutStep
            {
                Page = PageFriend, Zone = "left",
                Title = "你的头像和好友",
                Body = "左上是 " + TK + "你的头像" + TE + "，头像下面那个圆点是 "
                     + TK + "在线 / 离线开关" + TE + "：绿色在线、灰色离线。\n\n"
                     + "下面是好友列表，" + TK + "名字右边的红色数字" + TE + " 是没读过的消息数，"
                     + "点一下他就进去看。",
            },
            new TutStep
            {
                Page = PageFriend, Zone = "right",
                Title = "对话和底部那一条",
                Body = "右边是聊天记录，往下翻是他发来的消息。\n\n"
                     + "底部怎么用，看他是哪种人：老K（谈买卖）和要卖你消息的人，底下是 "
                     + TK + "接受 / 还价 / 拒绝" + TE + " 三颗按钮；"
                     + "纯闲聊的那几位（强哥、胖墩），底下是 "
                     + TK + "一个输入框加【发送】" + TE + "，你想说什么自己敲，按回车也能发出去。\n\n"
                     + TR + "离线状态下只能接受或拒绝，还价要先把状态切成在线。" + TE
                     + TC + " 右上角的靠谱度星越多，他说的消息越可信。" + TE,
            },
        };

        /// <summary>主线步数：整段新手引导只走这么多站（只讲核心买卖闭环）。</summary>
        private static int TutTotal
        {
            get { return TutorialMain.Length; }
        }

        /// <summary>两段讲稿加起来的总站数（主线 + 各页导览），页号↔步号的映射按这个来。</summary>
        private static int TutAllCount
        {
            get { return TutorialMain.Length + TutorialPro.Length; }
        }

        /// <summary>把「0..TutAllCount-1」的连续步号映射成具体那一步。</summary>
        private static TutStep TutAt(int step)
        {
            return step < TutorialMain.Length
                ? TutorialMain[step]
                : TutorialPro[step - TutorialMain.Length];
        }

        /// <summary>这一页是不是主线里讲过的（讲过的就不再「点进去自动弹」）。</summary>
        private static bool PageInMain(int page)
        {
            for (int i = 0; i < TutorialMain.Length; i++)
            {
                if (TutorialMain[i].Page == page) return true;
            }
            return false;
        }

        /// <summary>这一页有没有分步导览（调试页没有）。</summary>
        private static bool PageHasTour(int page)
        {
            for (int i = 0; i < TutAllCount; i++) if (TutAt(i).Page == page) return true;
            return false;
        }

        /// <summary>
        /// 取某一页的步号区间。步骤在同一页里是连排的，所以直接取 min/max 就够，
        /// 不用另建一张「页 → 步」的表（建了反而容易和数组对不上）。
        /// </summary>
        private static void PageTourRange(int page, out int from, out int to)
        {
            from = -1;
            to = -1;
            for (int i = 0; i < TutAllCount; i++)
            {
                if (TutAt(i).Page != page) continue;
                if (from < 0) from = i;
                to = i;
            }
        }

        /// <summary>
        /// 把步里的 Zone 名字翻成面板坐标下的矩形（原点＝面板左上角）。
        /// 一律按当前 Layout() 的尺寸现算，所以换分辨率、改版式都不用动教程数据。
        /// 返回宽高为 0 表示这一步不指目标（气泡居中摆）。
        /// </summary>
        private static Rect TutRect(string zone)
        {
            float bx = ContentX, by = BodyY, bw = ContentW, bh = BodyH;
            switch (zone)
            {
                case "nav":      return new Rect(Pad, BodyY, NavW, BodyH);
                case "header":   return new Rect(ContentX, 6f, ContentW, 64f);
                // 顶栏右上角那颗带圈的「?」（跟 BuildHeader 里的 helpX 一个算法）
                case "help":     return new Rect(W - Pad - 34f - 8f - 34f, HeaderY + 10f, 34f, 34f);
                case "title":    return new Rect(bx + 16f, by + 6f, Mathf.Min(460f, bw * 0.5f), 42f);
                case "top":      return new Rect(bx, by, bw, Mathf.Min(bh * 0.42f, 214f));
                // 总览页：顶栏那排身家数字 + 下面整张「运营概览」卡。
                // 从 y=2 一直盖到 by+196，才是玩家红框框住的那一片
                // （见 问题截图/框选少了 应该是框选我红框里的内容.png）
                case "topstat":  return new Rect(bx - 10f, 2f, bw + 20f, by + 196f);
                case "mid":      return new Rect(bx, by + bh * 0.30f, bw, 150f);
                case "bottom":   return new Rect(bx, by + bh * 0.55f, bw, bh * 0.45f);
                case "left":     return new Rect(bx, by, bw * 0.44f, bh);
                // 智能选股页左边那张卡（宽度跟 BuildPicker 里的 PkLeftW 一致，金边才贴得住卡片）
                case "pickerL":  return new Rect(bx, by, PkLeftW, bh);
                case "right":    return new Rect(bx + bw * 0.46f, by, bw * 0.54f, bh);
                case "toolbar":  return new Rect(bx + 24f, by + 6f, bw - 48f, 50f);
                case "list":     return new Rect(bx + 24f, by + 70f, bw - 48f, Mathf.Min(252f, bh - 150f));
                // 事件公告页左栏「空间站事件」（宽度跟 BuildEvent 里的 colW 同口径，金边才贴得住）
                case "gevents":
                {
                    float cw = Mathf.Floor((bw - 48f - 12f) * 0.5f);
                    return new Rect(bx + 24f, by + 88f, cw, Mathf.Min(300f, bh - 150f));
                }
                case "footer":   return new Rect(bx, by + bh - 56f, bw, 50f);
                case "wallets":  return new Rect(bx + 24f, by + 56f, bw - 48f, 96f);
                case "amount":   return new Rect(bx + 24f, by + 174f, bw - 48f, 82f);
                case "transfer": return new Rect(bx + 24f, by + 262f, bw - 48f, 118f);
                // 资金划转页左半边那两颗「转入股市 / 全部转入股市」，跟 BuildFund 里的摆法一致
                case "fundin":
                    return new Rect(bx + 24f, by + 262f, (bw - 48f - 18f) * 0.5f, 118f);
                // 交易下单页右边那张卡的「买入 / 卖出 / 清仓」那排，按 BuildTrade 的公式现算
                case "tradebtn":
                {
                    float ccH = Mathf.Clamp(bh - 244f, 240f, 420f);
                    float pickW = Mathf.Clamp(Mathf.Round(bw * 0.44f), 440f, 900f);
                    float ox = bx + pickW + 16f;
                    return new Rect(ox + 24f, by + ccH + 12f + 186f, bw - pickW - 16f - 48f, 40f);
                }
                default:         return new Rect(0f, 0f, 0f, 0f);
            }
        }

        /// <summary>
        /// 估一段富文本占几行。气泡高度必须在摆位置之前定下来，而 TMP 的
        /// preferredHeight 要等排版跑完才有值，所以按「半角宽」自己数一遍：
        /// 汉字算 2 个半角、数字字母算 1 个，units 就是一行能放几个半角。
        /// </summary>
        private static int EstLines(string s, int units)
        {
            if (string.IsNullOrEmpty(s)) return 1;
            if (units < 8) units = 8;
            int lines = 1, col = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<')
                {
                    int e = s.IndexOf('>', i);
                    if (e > i) { i = e; continue; }
                }
                if (c == '\n') { lines++; col = 0; continue; }
                int w = c < 128 ? 1 : 2;
                if (col + w > units) { lines++; col = 0; }
                col += w;
            }
            return lines;
        }

        // ── 每页的「?」说明卡 ────────────────────────────────────────
        // 横向讲「这页有什么」，跟分步气泡是两套东西：
        // 气泡是纵向的「带我走一遍」，说明卡是「先概览一眼」。
        // 数组下标必须等于页号，18 页一页不能少（跟 NavTitles 一样按页号排）。
        private static readonly string[] Guides =
        {
            /* 0 总览 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "一眼看全部：运营周期还剩几天、手里几支股票、赚了多少、分红拿了多少，下面列出全部 26 支标的。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "左侧导航换页；想看某支的走势，去【行情报价】点它那一行。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "黑市那几支要先办证明，不然买入按钮是灰的。",

            /* 1 事件公告 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "左栏「空间站事件」直接读游戏自己的事件（供水故障、停电、游客涌入、突袭黑市…），"
            + "每条都写清会推高还是打压哪些股票，并标出预计几天后结束；"
            + "右栏「市场事件」是模组抽的行情故事，只给每日冲击百分比。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "顶部细线是周期进度条，走到头就是结算日；两栏的冲击都实打实改股价，"
            + "收工后还会以夜间新闻再提一遍。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "左栏能看准收尾时间，右栏不会明说（只写「势头正猛 / 正在减弱」）。"
            + "别等事件结束前一天才追进去，那时候冲击已经衰减得差不多了。",

            /* 2 公司财报 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "26 家公司的营收 / 净利 / 负债率 / 市盈率 / 分红预案，一行一家，方便横向比。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "点任意一行，下面换成那家公司的股东名单和公告；底部翻页。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "每 21 天出新一期。财报差不一定马上跌，但长期会把股价往公允价拉。",

            /* 3 股市股评 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "各家博主天天喊涨喊跌，评论区还会混进水军；传闻只在有操盘手的股票上出现。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "上面点一个博主，下面看他这帖的正文、传闻和评论区。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "别看谁嗓门大，只看「历史胜率」：精选博主明显更准，野生的基本反着来。",

            /* 4 智能选股 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "免费的选股推荐：每批 3 支，每 7 天恢复 3 批；右边是它挑完之后实际涨了多少。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "点「换一批」看下一组；看中哪支就点它去交易页下单。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "它只在近期方向向上的标的里挑，还专挑没怎么涨的，所以是「能赚点小钱、但赚不多」。"
            + "只是根据空间站免费数据分析提供的，不一定稳定，更适合不会挑的新手。",

            /* 5 高级工具 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "七项一次性买断的付费功能：逐笔成交 / 十档盘口 / 资金透视 / AI 诊股 / 高级回测 / 智能盯盘 / 物价雷达。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "点左边任意一项，右边看它是什么、适合谁，再点「开通」花店铺现金买断。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "前六项只把已经看得到的东西讲得更细，不会多给你内幕消息，一件不买也不影响赚钱。"
            + "物价雷达是例外：站里六个行当的物价、供需和触发线本来不显示，只有它能看。",

            /* 6 新手任务 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "六条新手任务，做到就能点「领奖」拿现金，全领完 300 元。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "点「领奖」拿钱；右上角「重看教程」重走一遍引导。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "奖励总额封顶 300 元，不会超发。"
            + "老K 的报价已经搬到【好友】页了，那边有未读消息时导航上会冒红色数字气泡。",

            /* 7 行情报价 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "26 支股票的总表，一页 8 支。标签列写 [层区-业务]，近期方向是系统的判断。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "搜索框按名字或代码筛；底部翻页；最左边的 ★ 是收藏，收藏会排到最前面。"
            + "点任意一行先看它的技术分析，图上有「去下单」直接买。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "「资金」列是今天别人的净买入：红字有人在抢、绿字有人在跑，它比价格先动。",

            /* 8 交易下单 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "选一支股票、填数量、买入或卖出。上面是它的 K 线小图，下面是下单区。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "左上角下拉换标的；中间步进按钮或直接输入数量（1 股起）；右边「买入 / 卖出」。"
            + "鼠标压在小图上能读那天的开 / 高 / 低 / 收，图上右键可以关掉读数。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "买入立刻扣手续费，刚买完显示浮亏是正常的。费率治安部最低、黑市最贵。",

            /* 9 盘中实时交易 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "营业日之内的实时行情：实时价 / 均价 / 昨收 / 成交量，外加挂单表格。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "鼠标压在图上会出现竖线，读出那一段的价格 / 均价 / 成交量；不想看时在图上右键关掉。\n"
            + "填「跌到多少买」或「涨到多少卖」后点挂单；下面的列表可以撤单。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "收工时没成交的挂单会自动撤销，冻住的钱和股数会原样还给你。",

            /* 10 我的持仓 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "手里每支股票的股数 / 成本 / 现价 / 盈亏，以及已实现盈亏和累计分红。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "行尾的「卖出」可以快速清掉一支，不用回交易页。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "浮动盈亏是账面数，卖出那一刻才变成真钱。持满一个完整周期才派息。",

            /* 11 交易明细记录 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "每一笔成交的流水：哪天、哪支、买了还是卖了、多少股、什么价、交了多少手续费、这笔赚亏多少。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "顶部读数：累计手续费、已实现净盈亏、买入卖出笔数、卖出胜率。"
            + "下面按时间倒序列出最近 240 笔，「只看卖出」开关可以把买入过滤掉，专心复盘哪几笔亏了。\n"
            + "点任意一行就能看这支标的的技术分析图，看完点「返回」回到这页。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "这笔盈亏是「这一笔卖出的股票」对「它对应的买入成本」算的，"
            + "跟你现在的总收益不是一回事。只留最近 240 笔，更早的会被挤掉。",

            /* 12 技术分析 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "K 线图 + 均线 + 两个副图（成交量 / MACD / KDJ / RSI），给愿意多看一眼的人。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "左上角切 1 周 / 1 月 / 3 月 / 6 月 / 1 年；右上角 ◀ ▶ 换股票；点副图标题换内容。"
            + "右上角还有「去下单」和「返回」：从行情页点进来的话，看完图直接下单，或退回去接着挑。"
            + "鼠标压在三张图上都能读那一格的数，图上右键开关读数。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "看不懂不影响赚钱。它只描述已经发生的事，不预测明天。",

            /* 13 杠杆交易 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "用借来的钱放大收益：融资是借钱买股，融券是先借股票卖掉、等跌下去买回来还。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "左卡里六个按钮：开户 / 融资借钱 / 还款 / 融券卖出 / 买券还券 / 一键还清。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "自有资金亏到一定比例会被强制平仓，欠款和利息还是要还。新手可以先不碰。",

            /* 14 资产走势 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "你的全部身家（店铺现金 + 股票账户 + 持仓市值）随天数变化的曲线。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "下面列出历史每日的余额与持仓市值明细，想知道哪天亏的钱就去那儿翻。"
            + "鼠标压在曲线上能读那天三条线的数值，图上右键开关读数。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "刚开户那几天是平的，因为钱还没转进来。整体往上走就行，单日回撤别慌。",

            /* 15 资金划转 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "店铺现金和股票账户之间搬钱。两个口袋互不相通，只能手动转。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "调金额（−100 / −10 / +10 / +100 或直接输入）后点「转入股市」或「提现到店铺」。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "卖出股票的钱留在股票账户，要拿去进货得先提现。",

            /* 16 黑市开户 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "办一张黑市交易许可证，办完才能买卖带 [黑市-xxx] 标签的股票。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "花 200 元店铺现金买，一次买断、永久有效，买完回行情页正常买卖。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "黑市手续费 4.5% 是全场最贵，一进一出就吃掉近一成。",

            /* 17 好友 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "像聊天软件一样的好友页：左边是你的头像和好友列表，右边是你和这位好友的对话。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "点头像下面那个圆点切换在线 / 离线；点好友看他发来的消息，"
            + "底部按钮处理他的提议（接受 / 还价 / 拒绝）。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "离线时只能接受或拒绝，还价要在线才行。买来的消息不一定准，"
            + "先看右上的靠谱度：星越多，说真话的概率越高。",

            /* 18 调试工具 */
            "<color=#FFC24A>这页干什么</color>\n"
            + "开发用的开关：加钱、加天数、立刻触发事件、一键开通全部高级工具。\n\n"
            + "<color=#9EC7DE>常用操作</color>\n"
            + "左边是读数，右边是按钮，想验哪块点哪块。\n\n"
            + "<color=#E5533D>别踩坑</color>\n"
            + "这些按钮会直接改存档数值，正式玩的时候最好别乱点。",
        };

        // ── 运行时状态 ────────────────────────────────────────────────
        private static Canvas _canvas;
        private static GameObject _root;
        private static bool _open;
        private static bool _mouseBlocked;

        private static int _page = PageOverview;
        private static float _scale = 1f;
        private const float ScaleMin = 0.5f;
        private const float ScaleMax = 1.4f;
        // 建面板那一刻的屏幕尺寸。分辨率 / 显示方式一变就得重排：面板尺寸是按当时的
        // 屏幕算死的，不重排就会「膨胀」到屏幕外（见问题截图）。
        private static int _screenW, _screenH;

        /// <summary>
        /// 界面缩放的实际上限：ScaleMax 之外还要受屏幕尺寸限制。
        /// 版式是按「屏幕 ÷ 缩放」算的（见 Layout），缩放一旦超过
        /// min(屏宽/1124, 屏高/634)，画布上的排版单位就不够 1098×604 那么大的
        /// 内容区，交易页的操作行、技术分析的第二副图、左导航会一起溢出屏幕。
        /// 1124 / 634 = 面板最小可用尺寸（1098×604）加上 Layout 里那 26 / 30 的余量。
        /// </summary>
        private static float ScaleLimit()
        {
            float byScreen = Mathf.Min(Screen.width / 1124f, Screen.height / 634f);
            // 向下取两位小数，别让面板卡在「差一两个像素」的边缘上
            byScreen = Mathf.Floor(byScreen * 100f) / 100f;
            return Mathf.Clamp(Mathf.Min(ScaleMax, byScreen), ScaleMin, ScaleMax);
        }
        private static string _status = "从左侧选一项进入。黑市股需要先在【黑市开户】办证明。";
        private static bool _statusError;

        private static string _selectedId = "SEC";   // 当前选中的标的 Id（不是下标，列表会因收藏/搜索重排）
        private static int _amount = 10;      // 默认下单量；最小 1 股（AddAmount 的下限）
        private static int _fundAmount = 100;
        private static int _chartFrames;

        // 列表分页与筛选
        // 每页行数 / 行高都按屏幕算（见 Layout）：1280 屏 8 行，1920 屏 14 行
        private static int PageRows = 8;
        private static float RowStep = 42f;
        private static float RowH = 38f;
        private static int _quotePage, _holdPage, _tradePage;
        private static string _query = "";    // 行情页搜索词

        private static readonly GameObject[] _pages = new GameObject[PageCount];
        private static readonly UiButton[] _navs = new UiButton[PageCount];

        private static TextMeshProUGUI _statusText;
        /// <summary>底栏那枚「读数 开/关」按钮。右键开关是个隐藏手势，误触之后必须有个
        /// 看得见的地方能点回来（见问题截图/鼠标放上去 无法显示详细信息了）。</summary>
        private static UiButton _chartInfoBtn;
        private static readonly TextMeshProUGUI[] _statValue = new TextMeshProUGUI[5];

        // 总览页
        private static readonly TextMeshProUGUI[] _ovValue = new TextMeshProUGUI[4];
        private static TextMeshProUGUI _ovHold;

        // 行情页
        private static TextMeshProUGUI[,] _qCell;
        private static GameObject[] _qRow;
        private static UiButton[] _qStar;
        private static TextMeshProUGUI _qPageText;
        private static UiButton _qPrev, _qNext;
        private static TextMeshProUGUI _qEmpty;
        private static TMP_InputField _qSearch;
        private static UiButton _qClear;

        // 持仓页
        private static TextMeshProUGUI[,] _hCell;
        private static GameObject[] _hRow;
        private static TextMeshProUGUI _hSummary;
        private static TextMeshProUGUI _hEmpty;
        private static TextMeshProUGUI _hPageText;
        private static UiButton _hPrev, _hNext;

        // 交易明细记录页
        private static TextMeshProUGUI[,] _jnCell;
        private static GameObject[] _jnRow;
        /// <summary>每行对应的标的 Id，点击时用。</summary>
        private static string[] _jnId;
        private static TextMeshProUGUI _jnSummary, _jnEmpty, _jnPageText;
        private static UiButton _jnPrev, _jnNext, _jnFilter;
        private static int _jnPage;
        private static bool _jnOnlySell;

        // 交易页
        private static UiButton[] _tPick;
        private static TMP_InputField _tQtyInput;   // 可自定义买入/卖出股数
        private static TextMeshProUGUI _tQtyLab;    // 「数量（股）」标签，未开户时一起收起
        private static TextMeshProUGUI _tQtyHint;   // 右侧「约需 X 元」
        private static TextMeshProUGUI _tQtyWarn;   // 未开户时顶掉输入框的整行警告
        private static bool _qtyGuard;              // 回填输入框时屏蔽 onValueChanged，避免递归
        private static TextMeshProUGUI _tName;
        private static TextMeshProUGUI _tTag;
        private static readonly TextMeshProUGUI[] _tStat = new TextMeshProUGUI[6];
        private static TextMeshProUGUI _tPriceLab;   // 「现价」那个标签，盘中/收盘要改字面意思
        private static RectTransform _tChart;
        private static RectTransform _tVol;         // 交易页小图下方的成交量副图
        private static UiButton _tChartToggle;
        private static UiButton _tBuy;
        private static TextMeshProUGUI _tPickPageText;
        private static UiButton _tPickPrev, _tPickNext;
        private static bool _chartAsLine;

        // ── 技术分析页 ────────────────────────────────────────────────
        /// <summary>时间档：天数就是取最近多少个交易日。</summary>
        private static readonly string[] RangeNames = { "1周", "1月", "3月", "6月", "1年" };
        private static readonly int[] RangeDays = { 7, 30, 90, 180, 365 };
        /// <summary>副图可选的指标，顺序就是点标题按钮时的轮换顺序。</summary>
        private static readonly string[] SubNames = { "成交量 VOL", "MACD", "KDJ", "RSI", "OBV", "CCI" };

        private static RectTransform _tcMain, _tcSub1, _tcSub2;
        private static TextMeshProUGUI _tcName, _tcTag, _tcInfo;
        /// <summary>两个副图的标题本身就是按钮，点一下在 SubNames 里往后轮换。</summary>
        private static UiButton _tcSubTitle1, _tcSubTitle2;
        private static TextMeshProUGUI _tcSubLegend1, _tcSubLegend2;
        private static readonly UiButton[] _tcRange = new UiButton[RangeNames.Length];
        private static int _tcRangeIdx = 1;          // 默认「1月」
        private static UiButton _tcKind, _tcOverlay, _tcPrev, _tcNext;
        /// <summary>技术分析页右下角的「去下单 / 返回」：从行情页点进来的买卖闭环。</summary>
        private static UiButton _tcGo, _tcBack;
        private static int _techBack = PageQuote;   // 「返回」回到哪一页
        /// <summary>标的下拉框：按钮显示当前标的，点开弹出一张按层区分组的列表。盘中页另有一个。</summary>
        private static UiDrop _tcDrop;
        /// <summary>下拉列表的分组顺序，用的就是游戏自己的层区名。</summary>
        private static readonly string[] TechGroups = { "下层区", "上层区", "治安部", "黑市", "革命军" };
        /// <summary>主图 / 副图尺寸与纵向位置，全部由 Layout() 按内容区高度摊开。</summary>
        private static float TechMainH = 210f;
        private static float TechSubH = 74f;
        private static float TcMainY = 130f, TcT1Y = 344f, TcSub1Y = 368f, TcT2Y = 446f, TcSub2Y = 470f;
        private static bool _tcAsLine;
        private static int _tcOverlayIdx;            // 0=无 1=均线 2=布林带
        private static int _tcSubIdx1;               // 副图①默认成交量
        private static int _tcSubIdx2 = 1;           // 副图②默认 MACD

        // ── 盘中交易页 ────────────────────────────────────────────────
        /// <summary>挂单列表最多显示几行。必须和 StockOrders.MaxOrders 一致，否则会漏显示。</summary>
        private const int MaxOrderRows = 4;
        /// <summary>分时图高度与上下两张卡的纵向位置，由 Layout() 按内容区高度算。</summary>
        private static float IntraChartH = 236f;
        private static float IntraTopH = 330f;
        private static float IntraFormY = 338f;
        private static float IntraFormH = 214f;
        // 悬停读数的小标签（挂在图上，别太大挡行情）。跟通用读数层是同一档字号与纸面，
        // 两张图上的读数长得一样才不显得杂（见问题截图/优化 UI不匹配…）
        private const float HoverTagW = 168f;
        private const float HoverTagH = 62f;

        private static RectTransform _itChart;
        private static TextMeshProUGUI _itLive;        // 右上角实时读数
        private static UiDrop _itDrop;
        private static readonly UiButton[] _itSideBtn = new UiButton[2];
        private static int _itSide;                    // 0=买 1=卖
        private static TMP_InputField _itQtyInput;
        private static TMP_InputField _itPriceInput;
        private static bool _itGuard;                  // 回填输入框时屏蔽 onValueChanged
        private static int _itQty = 10;                // 挂单股数
        private static long _itPriceCents;             // 挂单限价（分），0 表示还没填、跟现价走
        private static TextMeshProUGUI _itAvail;       // 可用资金 / 可卖持仓
        private static readonly GameObject[] _itOrdRow = new GameObject[MaxOrderRows];
        /// <summary>挂单列表的单元格，按 row*4+col 取用：名称 / 方向 / 股数 / 挂单价。</summary>
        private static readonly TextMeshProUGUI[] _itOrdCell = new TextMeshProUGUI[MaxOrderRows * 4];
        private static readonly UiButton[] _itOrdCancel = new UiButton[MaxOrderRows];
        private static TextMeshProUGUI _itOrdEmpty;
        private static UiButton _itOrdClear;           // 「全部撤销」
        // 分时图是活的：记下上次画的是哪个状态，只有变了才重画。
        // 不像别的图只画两帧（那两帧是等布局稳定），这里要跟着盘中推进一直动。
        private static int _itDrawnStep = -1;
        private static int _itDrawnDay = -1;
        private static bool _itDrawnLive;
        private static bool _itDrawnDemo;              // 上次画的是不是教程演示路径
        private static double _itDrawnDemand = 1.0;    // 上次画的时候店铺需求抬了多少倍
        // 鼠标悬停在分时图上：一条竖线 + 点 + 小标签，读那一段的价格
        private static RectTransform _itHover;
        private static RectTransform _itHoverLine;
        private static RectTransform _itHoverDot;
        private static GameObject _itHoverTag;
        private static TextMeshProUGUI _itHoverText;
        private static int _itHoverIdx = -1;           // 现在停在第几段，-1 = 没在图上
        private static string _itHoverKey = "";        // 状态指纹，没变就不重排版

        // ── 通用图表悬停读数 ──────────────────────────────────────────
        /// <summary>
        /// 一张图上的读数层：一条竖线 + 一个小标签，鼠标压上去就报这一格的数。
        ///
        /// 两个关键约定：
        ///   · 这一层挂在图槽**外面**。图槽每重画一次都会 Clear 掉自己的子物件，
        ///     读数层要是画进去，图一更新就跟着被擦没了（分时图那层也是这么处理的）。
        ///   · 数据在画图的时候顺手存进来。读数只在「图上有什么」的基础上报数，
        ///     自己再算一遍迟早会跟画出来的对不上。
        /// </summary>
        private sealed class ChartTip
        {
            public RectTransform Host;        // 命中区 = 图表槽本身
            public RectTransform Line;        // 竖线
            public GameObject Tag;            // 小标签
            public TextMeshProUGUI Text;
            public float PadL, PadT;          // 画图区左上角在宿主里的位置
            public float PlotW, PlotH;        // 画图区尺寸
            public string Title;              // 标签名头（「成交量」「MACD」…）
            public bool Prices;               // 主图：报开/高/低/收；副图只报量或指标
            public string[] Labels;           // 横轴标签
            public List<Bar> Bars;            // K 线 / 量柱
            public List<ChartSeries> Lines;   // 线条读数
            public double[] Extra;            // 附加数列（MACD 柱）
            public string ExtraName;
            public int Idx = -1;
            /// <summary>数据版本号：每次重画喂新数据时 +1。读数只在「换了一格」或「图重画了」
            /// 的时候才重排文字，不然每帧拼一次富文本，握着鼠标不动也在白烧 CPU。</summary>
            public int Ver;
            /// <summary>上一次排版时认的版本号，跟 Ver 一起构成指纹。</summary>
            public int KeyVer = -1;

            public void Hide()
            {
                Idx = -1;
                KeyVer = -1;
                Ui.SetActive(Tag, false);
                Ui.SetActive(Line != null ? Line.gameObject : null, false);
            }

            public void Show()
            {
                Ui.SetActive(Line != null ? Line.gameObject : null, true);
                Ui.SetActive(Tag, true);
            }
        }

        private static readonly List<ChartTip> _tips = new List<ChartTip>();
        // 图上读数层的小标签：字号刻意压到 13，够读就行，别把行情挡住。
        // 纸面走「卡片色 + 透明度」，跟面板里其它卡片是一套料子，而不是引导气泡那种撞色
        // （见问题截图/优化 UI不匹配 而且窗口没有透明度…）。
        private const float TipFont = 13f;
        private const float TipW = 172f;
        private const float TipLineH = 17f;
        /// <summary>读数标签纸面的透明度：要能看清底下压着哪根蜡烛，又不能跟行情糊成一片。</summary>
        private const float TipAlpha = 0.88f;

        // 各页读数层的引用。建画布时跟着图槽一起建，画图时顺手把数据喂进去
        private static ChartTip _tipTradeMain, _tipTradeVol;
        private static ChartTip _tipTechMain, _tipTechSub1, _tipTechSub2;
        private static ChartTip _tipAsset;

        // ── 杠杆交易页 ────────────────────────────────────────────────
        /// <summary>融资融券只有一档（2 倍），场外配资有三档，界面按这个表铺按钮。</summary>
        private static readonly int[] RegLevels = { StockLeverage.RegMaxLeverage };
        /// <summary>杠杆页左卡宽度（右卡吃掉剩下的），由 Layout() 按内容区宽度算。</summary>
        private static float LvLeftW = 440f;
        private static UiButton _lvTabReg, _lvTabShadow;
        private static bool _lvShadow;               // 当前看的是不是「场外配资」
        private static TextMeshProUGUI _lvTitle, _lvRatio, _lvRatioHint, _lvRows, _lvNotice;
        private static TextMeshProUGUI _lvPlay, _lvRisk;
        private static TextMeshProUGUI _lvPickName;
        private static UiButton _lvPickPrev, _lvPickNext;
        private static TMP_InputField _lvAmountInput, _lvShareInput;
        private static bool _lvGuard;
        private static UiButton _lvOpen, _lvBorrow, _lvRepay, _lvShort, _lvCover, _lvSettle;
        private static readonly UiButton[] _lvLevels = new UiButton[3];
        private static int _lvAmount = 100;
        private static int _lvShares = 10;

        // 走势页
        private static RectTransform _aChart;

        // 事件页
        // 左栏是「空间站事件」——直接读游戏自己的 StoreEventManager，玩家在日历里
        // 看得见的那些事（供水故障、停电、游客涌入、突袭黑市……）；
        // 右栏是模组自造的「市场事件」。两栏分开摆，因为两者的可信度与口径不一样：
        // 左边是站里真发生的事，右边是行情模拟抽出来的行情故事。
        private const int EventCardRows = 3;
        private const int GameEventRows = StockGameEvents.MaxRows;
        private static TextMeshProUGUI _evInfo;
        private static TextMeshProUGUI _evLHead, _evRHead;
        private static readonly GameObject[] _geRow = new GameObject[GameEventRows];
        private static readonly TextMeshProUGUI[] _geName = new TextMeshProUGUI[GameEventRows];
        private static readonly TextMeshProUGUI[] _geLeft = new TextMeshProUGUI[GameEventRows];
        private static readonly TextMeshProUGUI[] _geTarget = new TextMeshProUGUI[GameEventRows];
        private static readonly TextMeshProUGUI[] _geDesc = new TextMeshProUGUI[GameEventRows];
        private static readonly Image[] _geStrip = new Image[GameEventRows];
        private static GameObject _geEmptyBox;
        private static int _geRows = GameEventRows;   // 实际建成几行（矮屏会自动收）
        private static readonly GameObject[] _evCard = new GameObject[EventCardRows];
        private static readonly TextMeshProUGUI[] _evName = new TextMeshProUGUI[EventCardRows];
        private static readonly TextMeshProUGUI[] _evSummary = new TextMeshProUGUI[EventCardRows];
        private static readonly TextMeshProUGUI[] _evTarget = new TextMeshProUGUI[EventCardRows];
        private static readonly TextMeshProUGUI[] _evImpact = new TextMeshProUGUI[EventCardRows];
        private static readonly TextMeshProUGUI[] _evDays = new TextMeshProUGUI[EventCardRows];
        private static readonly Image[] _evStrip = new Image[EventCardRows];
        private static Image _evCycleBar;
        private static GameObject _evEmptyBox;

        // 资金页
        private static TextMeshProUGUI _fdCash, _fdPool, _fdHint;
        private static TMP_InputField _fdAmountInput;  // 可自定义划转金额
        private static bool _fdGuard;
        private static UiButton _fdIn, _fdOut;

        // 开户页
        private static TextMeshProUGUI _lcInfo;
        private static UiButton _lcBuy;

        // 调试页
        // 读数分左右两块：左边是账户与事件这类短行，右边是股评胜率与涨跌榜。
        // 挤成一整块时 17 行文字会画到卡片外面（见 问题截图），分栏后再截断兜底。
        private static TextMeshProUGUI _dbgText;
        private static TextMeshProUGUI _dbgTextR;

        // 任务页
        private static TextMeshProUGUI _qstProg;
        private static readonly TextMeshProUGUI[] _qstIcon = new TextMeshProUGUI[6];
        private static readonly TextMeshProUGUI[] _qstTitle = new TextMeshProUGUI[6];
        private static readonly TextMeshProUGUI[] _qstDesc = new TextMeshProUGUI[6];
        private static readonly TextMeshProUGUI[] _qstProgEach = new TextMeshProUGUI[6];
        private static readonly UiButton[] _qstBtn = new UiButton[6];

        // ── 好友页（聊天软件式：左栏我的头像 + 好友列表，右栏对话）──────
        /// <summary>右栏最多显示几条气泡（从最新一条往上摆，摆不下就不摆了）。</summary>
        private const int ChatRows = 9;
        private static int _chatPick;
        private static Image _navBadge;                       // 导航上那颗红色数字气泡
        private static TextMeshProUGUI _navBadgeTx;
        private static Image _navGrpBadge;                    // 分类块「其它」上的同款气泡
        private static TextMeshProUGUI _navGrpBadgeTx;
        private static readonly UiButton[] _chRow = new UiButton[StockChat.All.Length];
        private static readonly Image[] _chRowAva = new Image[StockChat.All.Length];
        private static readonly TextMeshProUGUI[] _chRowAvaTx = new TextMeshProUGUI[StockChat.All.Length];
        private static readonly TextMeshProUGUI[] _chRowName = new TextMeshProUGUI[StockChat.All.Length];
        private static readonly TextMeshProUGUI[] _chRowPrev = new TextMeshProUGUI[StockChat.All.Length];
        /// <summary>行右边那列「最后一句是第几天说的」，照微信的列表样子摆。</summary>
        private static readonly TextMeshProUGUI[] _chRowTime = new TextMeshProUGUI[StockChat.All.Length];
        private static readonly Image[] _chRowBadge = new Image[StockChat.All.Length];
        private static readonly TextMeshProUGUI[] _chRowBadgeTx = new TextMeshProUGUI[StockChat.All.Length];
        private static UiButton _chMeDot;                     // 我的在线状态点（绿=在线 / 灰=离线，点它切换）
        private static TextMeshProUGUI _chMeStatus;
        private static TextMeshProUGUI _chListInfo;
        private static Image _chHeadAva;
        private static TextMeshProUGUI _chHeadAvaTx, _chHeadName, _chHeadSub, _chHeadRel;
        private static readonly Image[] _chBubble = new Image[ChatRows];
        private static readonly TextMeshProUGUI[] _chBubbleTx = new TextMeshProUGUI[ChatRows];
        private static TextMeshProUGUI _chHint;
        private static UiButton _chAccept, _chCounter, _chReject;
        /// <summary>闲聊好友专用：自己敲一句话发出去（原来是从 5 句里随机抽一句）。</summary>
        private static TMP_InputField _chSay;
        private static UiButton _chSend;

        // ── 高级工具页 ────────────────────────────────────────────────
        /// <summary>左卡（功能清单）宽度与右卡正文起始高度，由 Layout() 按内容区算。</summary>
        private static float VipLeftW = 320f;
        private static float VipBodyY = 124f;
        private static readonly UiButton[] _vipTab = new UiButton[StockVip.FeatCount];
        private static TextMeshProUGUI _vipInfo;
        private static TextMeshProUGUI _vipTitle, _vipWhom, _vipName, _vipBody, _vipBody2, _vipHint;
        private static UiButton _vipBuy, _vipPrev, _vipNext;
        private static int _vipBit = StockVip.FeatTape;
        /// <summary>高级工具页当前看的标的：前六项功能要选股，物价雷达不用（它按行当看）。</summary>
        private static string _vipId = "";

        // ── 智能选股页 ────────────────────────────────────────────────
        private static float PkLeftW = 560f;
        private static readonly GameObject[] _pkRow = new GameObject[StockVip.PickerSize];
        private static readonly TextMeshProUGUI[] _pkName = new TextMeshProUGUI[StockVip.PickerSize];
        private static readonly TextMeshProUGUI[] _pkReason = new TextMeshProUGUI[StockVip.PickerSize];
        private static readonly TextMeshProUGUI[] _pkConf = new TextMeshProUGUI[StockVip.PickerSize];
        private static TextMeshProUGUI _pkInfo, _pkLeft, _pkStats, _pkNote;
        private static UiButton _pkNext;
        /// <summary>每行一张透明点击层，点了直接去那支的交易页。</summary>
        private static readonly UiButton[] _pkHit = new UiButton[StockVip.PickerSize];
        /// <summary>当前这一批每行对应的标的 Id，点击时用。</summary>
        private static readonly string[] _pkId = new string[StockVip.PickerSize];

        // ── 新手引导（分步气泡）──────────────────────────────────────
        // 铺满整个面板但不铺遮罩：气泡贴着目标块摆，箭头指过去，目标描一圈金边，
        // 目标本身和一整页都还看得见 —— 这就是「讲到哪指到哪」的做法。
        private static GameObject _tutRoot, _tutBubble;
        private static Image _tutRim, _tutFace;
        private static readonly Image[] _tutEdge = new Image[4];
        // 灯光聚焦：四块压暗板围着目标拼一圈，目标本身留空不盖（见 PlaceDim）
        private static readonly Image[] _tutDim = new Image[4];
        private static TextMeshProUGUI _tutStepText, _tutTitle, _tutBody, _tutArrow, _tutFoot;
        private static Image _tutBarBg, _tutBarFill;
        private static UiButton _tutNext, _tutSkip, _tutPrev;

        /// <summary>气泡教程开着没有。</summary>
        private static bool _tourOpen;
        /// <summary>true = 从「?」里的本页导览进来的：不写存档、走完不结束整段引导。</summary>
        private static bool _tourPage;
        /// <summary>本页导览走完后，本次打开面板期间不再自动接回全局引导。</summary>
        private static bool _tourSkipAuto;
        private static int _tourFrom, _tourTo, _tourAt;

        // ── 每页的「?」说明卡 ─────────────────────────────────────────
        private static GameObject _hintRoot, _hintCard;
        private static Image _hintRim, _hintFace;
        private static TextMeshProUGUI _hintTitle, _hintBody;
        private static UiButton _hintWalk, _hintOk;
        /// <summary>当前打开说明卡的页号，-1 表示没开。</summary>
        private static int _hintPage = -1;

        // 引导显示期间要把页面内容整体关掉交互，否则拖动鼠标会误点到下面的按钮
        private static readonly CanvasGroup[] _pageCg = new CanvasGroup[PageCount];

        // 面板拖动：手柄就是标题栏那一条（左上角 214×74），拖动位置在本次游戏内记住
        private const float DragHandleW = 214f;
        private const float DragHandleH = 74f;
        private static RectTransform _dragHandle;
        private static Vector2 _panelPos;
        private static bool _dragging;
        private static Vector2 _dragStartMouse;
        private static Vector2 _dragStartPos;

        // ══════════════════════════════════════════════════════════════
        //  生命周期
        // ══════════════════════════════════════════════════════════════

        public static void OnSceneLoaded()
        {
            try
            {
                if (_canvas != null) UnityEngine.Object.Destroy(_canvas.gameObject);
            }
            catch { }

            // 场景重载会重建 OverlayHandler，先把可能残留的屏蔽栈还回去
            BlockWorldMouse(false);

            _canvas = null;
            _root = null;
            _open = false;
            _page = PageOverview;
            _status = "从左侧选一项进入。黑市股需要先在【黑市开户】办证明。";
            _statusError = false;
            _chartFrames = 0;
            _amount = 10;
            _fundAmount = 100;
            _selectedId = StockDefs.All.Length > 0 ? StockDefs.All[0].Id : "";
            _quotePage = _holdPage = _tradePage = 0;
            _jnPage = 0;
            _query = "";
            Array.Clear(_pages, 0, PageCount);
            Array.Clear(_navs, 0, PageCount);
            Array.Clear(_navGroupBtn, 0, _navGroupBtn.Length);
            _navGroup = 0;
            _qCell = null;
            _qRow = null;
            _qStar = null;
            _hCell = null;
            _hRow = null;
            _jnCell = null;
            _jnRow = null;
            _jnId = null;
            _jnSummary = _jnEmpty = _jnPageText = null;
            _jnPrev = _jnNext = _jnFilter = null;
            _tPick = null;
            _tChart = null;
            _tChartToggle = null;
            _tBuy = null;
            _chartAsLine = false;
            _tVol = null;
            _tcMain = _tcSub1 = _tcSub2 = null;
            _tcName = _tcTag = _tcInfo = null;
            _tcSubTitle1 = _tcSubTitle2 = null;
            _tcSubLegend1 = _tcSubLegend2 = null;
            _tcKind = _tcOverlay = _tcPrev = _tcNext = null;
            _tcGo = _tcBack = null;
            Array.Clear(_tcRange, 0, _tcRange.Length);
            _tcDrop = null;
            _itDrop = null;
            _itChart = null;
            _itLive = _itAvail = _itOrdEmpty = null;
            _itOrdClear = null;
            _itQtyInput = _itPriceInput = null;
            _itSideBtn[0] = _itSideBtn[1] = null;
            _itSide = 0;
            _itQty = 10;
            _itPriceCents = 0;
            _itGuard = false;
            _itDrawnStep = -1;
            _itDrawnDay = -1;
            _itDrawnLive = false;
            _itDrawnDemo = false;
            _itHover = _itHoverLine = _itHoverDot = null;
            _itHoverTag = null;
            _itHoverText = null;
            _itHoverIdx = -1;
            _itHoverKey = "";
            // 换场景等于所有读数层都跟着画布一起没了，留着这些引用没意义
            _tipTradeMain = _tipTradeVol = null;
            _tipTechMain = _tipTechSub1 = _tipTechSub2 = null;
            _tipAsset = null;
            _tips.Clear();
            Array.Clear(_itOrdRow, 0, _itOrdRow.Length);
            Array.Clear(_itOrdCell, 0, _itOrdCell.Length);
            Array.Clear(_itOrdCancel, 0, _itOrdCancel.Length);
            _lvTabReg = _lvTabShadow = null;
            _lvTitle = _lvRatio = _lvRatioHint = _lvRows = _lvNotice = null;
            _lvPlay = _lvRisk = null;
            _lvPickName = null;
            _lvPickPrev = _lvPickNext = null;
            _lvAmountInput = _lvShareInput = null;
            _lvGuard = false;
            _lvShadow = false;
            _lvOpen = _lvBorrow = _lvRepay = _lvShort = _lvCover = _lvSettle = null;
            Array.Clear(_lvLevels, 0, _lvLevels.Length);
            _aChart = null;
            _qPageText = _qEmpty = _hPageText = _tPickPageText = null;
            _qSearch = null;
            _qPrev = _qNext = _qClear = _hPrev = _hNext = _tPickPrev = _tPickNext = null;
            _hSummary = _hEmpty = _evInfo = null;
            _evLHead = _evRHead = null;
            for (int i = 0; i < GameEventRows; i++)
            {
                _geRow[i] = null;
                _geName[i] = _geLeft[i] = _geTarget[i] = _geDesc[i] = null;
                _geStrip[i] = null;
            }
            _geEmptyBox = null;
            for (int i = 0; i < EventCardRows; i++)
            {
                _evCard[i] = null;
                _evName[i] = _evSummary[i] = _evTarget[i] = _evImpact[i] = _evDays[i] = null;
                _evStrip[i] = null;
            }
            _evCycleBar = null;
            _evEmptyBox = null;
            _dbgText = null;
            _dbgTextR = null;
            _tName = null;
            _tQtyInput = null;
            _tQtyLab = null;
            _tQtyHint = null;
            _tQtyWarn = null;
            _qtyGuard = false;
            _tTag = null;
            _fdAmountInput = null;
            _fdGuard = false;
            _fdIn = _fdOut = _lcBuy = null;
            _statusText = null;
            _chartInfoBtn = null;
            for (int i = 0; i < 5; i++) _statValue[i] = null;
            for (int i = 0; i < 4; i++) _ovValue[i] = null;
            for (int i = 0; i < 6; i++) _tStat[i] = null;
            _tPriceLab = null;
            _qstProg = null;
            for (int i = 0; i < _qstIcon.Length; i++)
            {
                _qstIcon[i] = null;
                _qstTitle[i] = null;
                _qstDesc[i] = null;
                _qstProgEach[i] = null;
                _qstBtn[i] = null;
            }
            _navBadge = null;
            _navBadgeTx = null;
            _navGrpBadge = null;
            _navGrpBadgeTx = null;
            _chMeDot = null;
            _chMeStatus = _chListInfo = null;
            _chHeadAva = null;
            _chHeadAvaTx = _chHeadName = _chHeadSub = _chHeadRel = null;
            Array.Clear(_chRow, 0, _chRow.Length);
            Array.Clear(_chRowAva, 0, _chRowAva.Length);
            Array.Clear(_chRowAvaTx, 0, _chRowAvaTx.Length);
            Array.Clear(_chRowName, 0, _chRowName.Length);
            Array.Clear(_chRowPrev, 0, _chRowPrev.Length);
            Array.Clear(_chRowTime, 0, _chRowTime.Length);
            Array.Clear(_chRowBadge, 0, _chRowBadge.Length);
            Array.Clear(_chRowBadgeTx, 0, _chRowBadgeTx.Length);
            Array.Clear(_chBubble, 0, _chBubble.Length);
            Array.Clear(_chBubbleTx, 0, _chBubbleTx.Length);
            _chHint = null;
            _chAccept = _chCounter = _chReject = null;
            _chSay = null;
            _chSend = null;
            Array.Clear(_vipTab, 0, _vipTab.Length);
            _vipInfo = null;
            _vipTitle = _vipWhom = _vipName = _vipBody = _vipBody2 = _vipHint = null;
            _vipBuy = _vipPrev = _vipNext = null;
            _vipBit = StockVip.FeatTape;
            _vipId = "";
            Array.Clear(_pkRow, 0, _pkRow.Length);
            Array.Clear(_pkName, 0, _pkName.Length);
            Array.Clear(_pkReason, 0, _pkReason.Length);
            Array.Clear(_pkConf, 0, _pkConf.Length);
            _pkInfo = _pkLeft = _pkStats = _pkNote = null;
            _pkNext = null;
            Array.Clear(_pkHit, 0, _pkHit.Length);
            Array.Clear(_pkId, 0, _pkId.Length);
            _tutRoot = _tutBubble = null;
            _tutRim = _tutFace = null;
            Array.Clear(_tutEdge, 0, _tutEdge.Length);
            Array.Clear(_tutDim, 0, _tutDim.Length);
            _tutStepText = _tutTitle = _tutBody = _tutArrow = null;
            _tutNext = _tutSkip = _tutPrev = null;
            _hintRoot = _hintCard = null;
            _hintRim = _hintFace = null;
            _hintTitle = _hintBody = null;
            _hintWalk = _hintOk = null;
            _hintPage = -1;
            Array.Clear(_pageCg, 0, PageCount);
            _dragHandle = null;
            _dragging = false;
        }

        /// <summary>面板是否处于打开状态。</summary>
        public static bool IsOpen { get { return _open; } }

        /// <summary>股票账目上有没有留下任何痕迹。用来判断是不是真·新档（见 Toggle）。</summary>
        private static bool IsFreshSave()
        {
            if (StockState.Today > 0) return false;
            if (StockState.Pool > 0) return false;
            if (StockState.SellCount > 0 || StockState.RealizedPnl != 0) return false;
            if (StockState.TotalDividend != 0) return false;
            if (StockState.QuestDone != 0 || StockState.QuestClaimed != 0) return false;
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                if (StockState.GetPosition(StockDefs.All[i].Id) > 0) return false;
            }
            return true;
        }

        /// <summary>F7 或 ESC 菜单入口调用。</summary>
        public static void Toggle()
        {
            try
            {
                if (_open)
                {
                    Close();
                    return;
                }

                if (!StockEntry.InSave)
                {
                    Core.Log.Warning("尚未进入存档，无法打开星际证券。");
                    return;
                }

                PlayerStore store = PlayerStore.instance;
                if (store == null)
                {
                    Core.Log.Warning("尚未进入存档，无法打开星际证券。");
                    return;
                }
                StockState.EnsureLoaded(store);

                if (_canvas == null || !Alive(_canvas.gameObject))
                {
                    if (!Build()) return;
                }

                LogOp("打开面板");
                _open = true;
                // 「本次打开期间不再自动接回整段引导」这条只挡一次开面板，重新打开就恢复
                _tourSkipAuto = false;
                Ui.SetActive(_canvas.gameObject, true);
                BlockWorldMouse(true);

                // 只有真正的新档才自动弹引导。tut 字段存在存档里，但老存档没这一项、
                // 或者玩家上次没存档就退出时都会读成 -1，光看它会把引导又弹一遍，
                // 所以再叠加一条「账目上一点痕迹都没有」才算新档。
                if (StockState.TutorialStep < 0 && IsFreshSave())
                {
                    StockState.TutorialStep = 0;
                    StockState.Dirty = true;
                    Core.Log.Msg("[引导] 新手引导开始");
                }

                SetStatus("按 ESC 或点右上角 × 关闭面板。", false);
                ShowPage(_page, false);
                if (StockFriend.HasOffer)
                {
                    SetStatus("老K 有新的报价，去【好友】页找他谈。", false);
                    Refresh();
                }
            }
            catch (Exception ex)
            {
                Core.Log.Error("开关星际证券面板失败：" + ex);
            }
        }

        /// <summary>关闭面板。走完整流程：隐藏画布 + 归还鼠标屏蔽栈。</summary>
        public static void Close()
        {
            try
            {
                if (!_open) return;
                LogOp("关闭面板");
                _open = false;
                _dragging = false;
                CloseHint();
                HideReadouts();
                Ui.SetActive(_canvas != null ? _canvas.gameObject : null, false);
                BlockWorldMouse(false);
            }
            catch (Exception ex)
            {
                Core.Log.Error("关闭星际证券面板失败：" + ex);
            }
        }

        /// <summary>
        /// 顶栏那个「日/夜」按钮：在深色和亮色之间来回换，并记住选择。
        /// 颜色是建面板时烙进 Image/Text 里的，改 Palette 不会自己生效，
        /// 所以换完得整块拆了重建 —— 跟切场景走的是同一条路，只是不重置
        /// 玩家正在看的东西（页号、搜索词、选中标的、各页页码全都留着）。
        /// </summary>
        private static void ToggleTheme()
        {
            try
            {
                bool toLight = !Palette.IsLight;
                LogOp(toLight ? "切换风格：亮色" : "切换风格：深色");
                Palette.SetTheme(toLight ? Palette.Theme.Light : Palette.Theme.Dark);
                Core.LightThemePref = toLight;

                if (!RebuildCanvas())
                {
                    // 重建失败就干脆把面板收掉，别留个半死不活的画布
                    Core.Log.Warning("换风格后重建面板失败，面板已关闭。");
                    return;
                }
                SetStatus(toLight
                    ? "已换成亮色风格。顶栏「夜」按钮可以换回深色。"
                    : "已换回深色风格。顶栏「日」按钮可以换成亮色。", false);
                // 左下角那个常驻入口也吃 Palette，一并重建，免得它还是旧配色
                StockEntry.Rebuild();
            }
            catch (Exception ex)
            {
                Core.Log.Error("切换界面风格失败：" + ex);
            }
        }

        /// <summary>
        /// 拆掉整块画布按当前参数重建：页号、搜索词、选中标的、各页页码都留着。
        /// 「切深浅配色」「改缩放」「分辨率变了」走的是同一条路 —— 颜色和尺寸都是
        /// 建面板时烙进 Image/Text/RectTransform 里的，改完只能重建。
        /// 失败时把面板收掉，不留半死不活的画布。
        /// </summary>
        private static bool RebuildCanvas()
        {
            int page = _page;
            if (_canvas != null) UnityEngine.Object.Destroy(_canvas.gameObject);
            // 下面这些句柄全都指着刚销毁的那批物件，Build 会重新灌一遍；
            // 先清空是为了 Build 半路失败时不留下悬空引用。
            _canvas = null;
            _root = null;
            _dragHandle = null;
            _dragging = false;
            _hintPage = -1;
            Array.Clear(_pages, 0, PageCount);
            Array.Clear(_navs, 0, PageCount);
            Array.Clear(_navGroupBtn, 0, _navGroupBtn.Length);
            Array.Clear(_pageCg, 0, PageCount);
            _tutRoot = _tutBubble = null;
            _tutRim = _tutFace = null;
            Array.Clear(_tutEdge, 0, _tutEdge.Length);
            Array.Clear(_tutDim, 0, _tutDim.Length);
            _hintRoot = _hintCard = null;
            _hintRim = _hintFace = null;
            // 读数层也是刚被销毁的那批：列表不清空的话，每次重建（切配色、改缩放、
            // 换分辨率）都往 _tips 里再堆六个死物件，越攒越多还得每帧遍历。
            // 新的一批由 Build 里的 MakeTip 重新登记。
            _tips.Clear();
            _tipTradeMain = _tipTradeVol = null;
            _tipTechMain = _tipTechSub1 = _tipTechSub2 = null;
            _tipAsset = null;
            _itHover = null;
            _itHoverLine = _itHoverDot = null;
            _itHoverTag = null;
            _itHoverText = null;
            _itHoverIdx = -1;
            _itHoverKey = "";
            // 分时图是按「状态签名」决定重不重画的（见 Tick）：段位、天数、开收工没变就不重画。
            // 重建之后新的图槽是空的，可签名还是老值，于是图上什么都不画 —— 这就是
            // 「切深浅配色后盘中交易页变空白」的原因（见用户反馈）。签名打回 -1 逼它重画。
            _itDrawnStep = -1;
            _itDrawnDay = -1;
            _itDrawnLive = false;
            _itDrawnDemo = false;

            if (!Build())
            {
                _open = false;
                BlockWorldMouse(false);
                return false;
            }

            _page = page;
            if (_open)
            {
                Ui.SetActive(_canvas.gameObject, true);
                ShowPage(page, false);
            }
            return true;
        }

        /// <summary>输入框是否正被聚焦。Il2Cpp 绑定里取 isFocused 可能抛，一律吞掉。</summary>
        private static bool InputFocused(TMP_InputField f)
        {
            if (f == null) return false;
            try { return f.isFocused; }
            catch { return false; }
        }

        /// <summary>
        /// 面板里是否有输入框正在输入。ESC 要先让输入框失焦，不能顺手把面板也关了。
        /// </summary>
        public static bool IsTyping
        {
            get
            {
                return InputFocused(_qSearch) || InputFocused(_tQtyInput) || InputFocused(_fdAmountInput)
                    || InputFocused(_lvAmountInput) || InputFocused(_lvShareInput)
                    || InputFocused(_itQtyInput) || InputFocused(_itPriceInput)
                    || InputFocused(_chSay);
            }
        }

        private static bool Alive(GameObject go)
        {
            try { return go != null; } catch { return false; }
        }

        /// <summary>
        /// 借游戏 OverlayHandler 的鼠标屏蔽栈：面板打开期间把世界交互挡在外面。
        /// 我们自己那张全屏拦截层只能拦住 EventSystem，拦不住游戏直接读鼠标的那条路，
        /// 所以必须和游戏自己的弹窗一样，往 mouseBlockStacks 里压一层。
        /// </summary>
        private static void BlockWorldMouse(bool on)
        {
            try
            {
                if (on == _mouseBlocked) return;
                OverlayHandler handler = OverlayHandler.current;
                if (handler == null)
                {
                    _mouseBlocked = false;
                    return;
                }

                if (on)
                {
                    handler.BlockMouse();
                    _mouseBlocked = true;
                }
                else
                {
                    _mouseBlocked = false;
                    // 玩家可能按过游戏的强制解除键把栈清空了，别减成负数
                    if (handler.mouseBlockStacks > 0) handler.UnblockMouse();
                }
            }
            catch (Exception ex)
            {
                _mouseBlocked = false;
                Core.Log.Warning("切换鼠标屏蔽失败：" + ex.Message);
            }
        }

        /// <summary>由 Core.OnUpdate 每帧调用。图表要等布局稳定，所以延迟两帧重绘。</summary>
        public static void Tick()
        {
            if (!_open) return;
            // 窗口被拖动 / 换了分辨率或显示方式：面板尺寸是按建面板那一刻的屏幕算的，
            // 得按新尺寸重排一次，否则会被撑到屏幕外点不到
            CheckScreenChange();
            // 盘中快讯：消息砸下来的那一段，底栏弹一次提示（利空标红），
            // 玩家不用一直盯着事件页也能第一时间知道出事了。
            double flashImpact;
            string flash = StockEngine.TakeFlash(out flashImpact);
            if (flash.Length > 0) SetStatus(flash, flashImpact < 0);
            Ui.UpdateHover();
            UpdateDrag();
            // 分时图跟别的图不一样：它得一直动。盘中每推进一段、或者开/收工状态一变就重画，
            // 但同一状态里绝不重画 —— 一张图有上百个节点，每帧重建会把帧率吃掉。
            if (_page == PageIntra)
            {
                if (_itDrawnStep != StockIntraday.Step || _itDrawnDay != StockState.Today
                    || _itDrawnLive != StockIntraday.IsLive || _itDrawnDemo != StockIntraday.DemoActive
                    || _itDrawnDemand != StockEconomy.Factor(Current() != null ? Current().Id : string.Empty))
                {
                    DrawIntraChart();
                    // 读数和挂单列表也得跟着段位走，否则图在动、上面的数字是死的
                    RefreshIntra();
                }
            }
            // 全部图表读数（分时 + 技术分析主副图 + 交易页小图与量图 + 走势页）统一在这里刷，
            // 内含「图上右键开关」和「不在图上就收起来」
            UpdateReadouts();
            if (_chartFrames <= 0) return;
            _chartFrames--;
            if (_page == PageTrade) DrawTradeChart();
            else if (_page == PageAsset) DrawAssetChart();
            else if (_page == PageTech) DrawTechChart();
        }

        /// <summary>
        /// 拖动面板：按住左上角标题栏那一条（「星际证券」标题所在区域）拖，松手停下。
        /// 手柄刻意只覆盖标题栏——那里没有任何按钮，所以按住拖动不会顺手点到东西。
        /// </summary>
        private static void UpdateDrag()
        {
            try
            {
                if (_root == null) return;
                RectTransform rt = Ui.Rect(_root);
                if (rt == null) return;

                if (!_dragging)
                {
                    if (_dragHandle == null) return;
                    if (!Input.GetMouseButtonDown(0)) return;
                    if (!RectTransformUtility.RectangleContainsScreenPoint(_dragHandle, Input.mousePosition, null))
                    {
                        return;
                    }
                    _dragging = true;
                    _dragStartMouse = Input.mousePosition;
                    _dragStartPos = rt.anchoredPosition;
                    return;
                }

                if (Input.GetMouseButton(0))
                {
                    float s = _scale <= 0.01f ? 1f : _scale;
                    Vector2 mouse = Input.mousePosition;
                    Vector2 delta = mouse - _dragStartMouse;
                    rt.anchoredPosition = ClampPanelPos(_dragStartPos + delta / s);
                    return;
                }

                _dragging = false;
                _panelPos = rt.anchoredPosition;
                Core.Log.Msg("[操作] 面板拖动到 " + _panelPos.x.ToString("0") + ", " + _panelPos.y.ToString("0"));
            }
            catch (Exception ex)
            {
                _dragging = false;
                Core.Debug("[界面] 拖动面板失败：" + ex.Message);
            }
        }

        /// <summary>把面板位置限制在屏幕内（面板比屏幕还大时就锁在正中）。</summary>
        private static Vector2 ClampPanelPos(Vector2 pos)
        {
            float s = _scale <= 0.01f ? 1f : _scale;
            float maxX = Mathf.Max(0f, Screen.width * 0.5f / s - W * 0.5f);
            float maxY = Mathf.Max(0f, Screen.height * 0.5f / s - H * 0.5f);
            pos.x = Mathf.Clamp(pos.x, -maxX, maxX);
            pos.y = Mathf.Clamp(pos.y, -maxY, maxY);
            return pos;
        }

        // ══════════════════════════════════════════════════════════════
        //  构建
        // ══════════════════════════════════════════════════════════════

        private static bool Build()
        {
            try
            {
                // 先把版式尺寸按当前屏幕算出来，后面所有页都按它摆。
                // 屏幕上撑不住默认缩放时（小窗口 + ×1.0 也可能超上限）先夹紧再算，
                // 否则开局就是溢出状态（见 ScaleLimit）。
                _scale = Mathf.Clamp(_scale, ScaleMin, ScaleLimit());
                Layout();
                // 记账：之后屏幕一变就拿这个比（见 CheckScreenChange）
                _screenW = Screen.width;
                _screenH = Screen.height;
                Ui.ResetHovers();
                // 重建画布 = 旧的读数层全没了。列表里要是留着它们，鼠标一压上去
                // 就会去碰已经销毁的物件，所以从头开始收。
                _tips.Clear();
                _canvas = Ui.NewCanvas("StockMarketCanvas");
                if (_canvas == null)
                {
                    Core.Log.Error("创建星际证券画布失败。");
                    return false;
                }

                // 全屏拦截层：挡住穿透到游戏本体的点击（必须先建，后面才压得住）
                Image blocker = Ui.MakeImage(_canvas.transform, "Blocker", new Color(0f, 0f, 0f, 0.004f), true);
                if (blocker != null) Ui.Stretch(blocker.gameObject, 0f);

                _root = Ui.New("Panel", _canvas.transform);
                // 位置沿用上次拖动后的结果，重建（切场景）不会跳回正中
                _panelPos = ClampPanelPos(_panelPos);
                Ui.PlaceCentered(_root, _panelPos.x, _panelPos.y, W, H);

                Image bg = Ui.MakeSliced(_root.transform, "PanelBg", Ui.Card(), Palette.PageBg, true);
                if (bg != null) Ui.Stretch(bg.gameObject, 0f);
                Image rim = Ui.MakeSliced(_root.transform, "PanelRim", Ui.Card(), Palette.CardRim, false);
                if (rim != null)
                {
                    Ui.Stretch(rim.gameObject, 0f);
                    Image face = Ui.MakeSliced(_root.transform, "PanelFace", Ui.Card(), Palette.PageBg, false);
                    if (face != null) Ui.Stretch(face.gameObject, 2f);
                }

                Transform root = _root.transform;
                BuildHeader(root);
                BuildNav(root);
                BuildFooter(root);

                _pages[PageOverview] = BuildOverview(root);
                _pages[PageQuote] = BuildQuote(root);
                _pages[PageTech] = BuildTech(root);
                _pages[PageIntra] = BuildIntra(root);
                _pages[PageHold] = BuildHold(root);
                _pages[PageJournal] = BuildJournal(root);
                _pages[PageTrade] = BuildTrade(root);
                _pages[PageLever] = BuildLever(root);
                _pages[PageAsset] = BuildAsset(root);
                _pages[PageEvent] = BuildEvent(root);
                _pages[PageReport] = BuildReport(root);
                _pages[PageReview] = BuildReview(root);
                _pages[PageVip] = BuildVip(root);
                _pages[PagePicker] = BuildPicker(root);
                _pages[PageQuest] = BuildQuest(root);
                _pages[PageFund] = BuildFund(root);
                _pages[PageLicense] = BuildLicense(root);
                _pages[PageFriend] = BuildChat(root);
                _pages[PageDebug] = BuildDebug(root);

                BuildTutorial(root);
                BuildHint(root);

                // 给每个页面挂一个 CanvasGroup，引导期间整体关掉交互（见 RefreshTutorial）
                for (int i = 0; i < PageCount; i++)
                {
                    if (_pages[i] == null) continue;
                    _pageCg[i] = Ui.Add<CanvasGroup>(_pages[i], "Page" + i + ".CanvasGroup");
                }

                Ui.SetScale(_canvas, _scale);
                Core.Log.Msg("星际证券面板已重建：" + W + "x" + H + " 像素单位，屏幕 "
                    + Screen.width + "x" + Screen.height
                    + "　内容区 " + ContentW + "x" + BodyH + "　每页 " + PageRows + " 行");
                return true;
            }
            catch (Exception ex)
            {
                Core.Log.Error("构建星际证券面板失败：" + ex);
                _canvas = null;
                _root = null;
                return false;
            }
        }

        /// <summary>顶栏：标题 + 四个数据块 + 关闭按钮。</summary>
        private static void BuildHeader(Transform root)
        {
            TextMeshProUGUI title = Ui.MakeText(root, "Title", "星际证券", FsTitle,
                Palette.Title, Ui.AlignLeft, false);
            if (title != null) Ui.Place(title.gameObject, Pad, HeaderY, 138f, HeaderH);

            // 标题栏就是拖动手柄：一块全透明、不接收射线（命中判定自算）的矩形
            Image handle = Ui.MakeImage(root, "DragHandle", new Color(0f, 0f, 0f, 0f), false);
            if (handle != null)
            {
                Ui.Place(handle.gameObject, 0f, 0f, DragHandleW, DragHandleH);
                _dragHandle = Ui.Rect(handle.gameObject);
            }
            TextMeshProUGUI dragHint = Ui.MakeText(root, "DragHint", "按住拖动", 13f,
                Palette.Muted, Ui.AlignLeft, false);
            if (dragHint != null) Ui.Place(dragHint.gameObject, 142f, HeaderY + 32f, 70f, 18f);

            string[] labels = { "总资产", "总收益", "浮动盈亏", "可用资金", "持仓市值" };
            // 五个数据块把标题和关闭按钮之间的整条铺满，宽屏上不会挤在左边
            const float gap = 8f;
            float x0 = Pad + 196f;
            // 右边三个 34 的方块：主题 / 「?」/「×」，每个之间留 8。
            // 数据块让出这三个方块加 12 的空当。
            float themeX = W - Pad - 34f - 8f - 34f - 8f - 34f;
            float helpX = W - Pad - 34f - 8f - 34f;
            float x1 = themeX - 12f;
            float bw = Mathf.Max(96f, (x1 - x0 - gap * 4f) / 5f);
            for (int i = 0; i < 5; i++)
            {
                float x = x0 + i * (bw + gap);
                Ui.MakeSlot(root, "Stat" + i, x, HeaderY, bw, HeaderH, Palette.SlotBg);
                TextMeshProUGUI lab = Ui.MakeText(root, "StatLab" + i, labels[i], FsSmall,
                    Palette.Muted, Ui.AlignCenter, false);
                if (lab != null) Ui.Place(lab.gameObject, x + 6f, HeaderY + 6f, bw - 12f, 18f);
                _statValue[i] = Ui.MakeText(root, "StatVal" + i, "-", FsCardTitle,
                    Palette.Title, Ui.AlignCenter, false);
                if (_statValue[i] != null)
                {
                    Ui.Place(_statValue[i].gameObject, x + 4f, HeaderY + 25f, bw - 8f, 26f);
                }
            }

            // 风格切换：按钮上写的是「点了会换成哪个」——深色时显示「日」，亮色时显示「夜」。
            UiButton theme = Ui.MakeButton(root, "Theme", Palette.IsLight ? "夜" : "日", FsBody,
                Palette.BtnIdle, Palette.Gold, ToggleTheme, Ui.AlignCenter, Ui.Circle());
            if (theme != null) theme.Place(themeX, HeaderY + 10f, 34f, 34f);

            // 带圈的「?」：点开就是当前这一页的说明卡。做成圆形，跟关闭按钮区分开。
            UiButton help = Ui.MakeButton(root, "Help", "?", FsBody, Palette.BtnBlue,
                Palette.Title, () => OpenHint(_page), Ui.AlignCenter, Ui.Circle());
            if (help != null) help.Place(helpX, HeaderY + 10f, 34f, 34f);

            UiButton close = Ui.MakeButton(root, "Close", "×", FsBody, Palette.BtnRed,
                Palette.Title, Close, Ui.AlignCenter);
            if (close != null) close.Place(W - Pad - 34f, HeaderY + 10f, 34f, 34f);
        }

        /// <summary>
        /// 左侧栏：两级导航。上面 2×2 排四个分类（看盘 / 交易 / 资金 / 其它），
        /// 下面只列当前分类的页面。以前 13 个入口一字排开，末颗已经顶到底栏，
        /// 再加「银行」必然溢出，所以这里改成分组。
        /// </summary>
        private static void BuildNav(Transform root)
        {
            const float chipGap = 6f;
            const float chipH = 34f;
            const float chipStepY = 38f;
            float chipW = (NavW - chipGap) * 0.5f;
            for (int g = 0; g < NavGroups.Length; g++)
            {
                int idx = g;
                UiButton b = Ui.MakeButton(root, "NavGroup" + g, NavGroups[g], FsSmall,
                    Palette.BtnIdle, Palette.Sub, () => ShowNavGroup(idx), Ui.AlignCenter);
                if (b == null) continue;
                b.Place(Pad + (g % 2) * (chipW + chipGap),
                    BodyY + 2f + (g / 2) * chipStepY, chipW, chipH);
                _navGroupBtn[g] = b;

                // 好友未读也要在分类块上冒一颗：分组收起来的时候，
                // 只挂在「好友」按钮上的气泡是看不见的，玩家就漏消息了。
                if (g == GroupOfPage(PageFriend))
                {
                    const float gs = 20f;
                    float gx = chipW - gs - 4f;
                    _navGrpBadge = MakeCircle(b.Go.transform, "NavGrpBadge", Palette.Up);
                    if (_navGrpBadge != null) Ui.Place(_navGrpBadge.gameObject, gx, 4f, gs, gs);
                    _navGrpBadgeTx = Ui.MakeText(b.Go.transform, "NavGrpBadgeTx", "", 13f,
                        Palette.Hex("FFFFFF"), Ui.AlignCenter, false);
                    if (_navGrpBadgeTx != null) Ui.Place(_navGrpBadgeTx.gameObject, gx, 4f, gs, gs);
                }
            }

            for (int i = 0; i < PageCount; i++)
            {
                int target = i;
                // 字号用 FsCell(18) 而不是 FsBody(20)：挂上图标后 20px 的
                // 「▤ 交易明细记录」要 150px，而左对齐按钮的可用宽度只有栏宽 − 28。
                UiButton b = Ui.MakeButton(root, "Nav" + i, NavLabel(i), FsCell,
                    Palette.BtnIdle, Palette.Sub, () => ShowPage(target), Ui.AlignLeft);
                if (b == null) continue;
                _navs[i] = b;
                AddNavBadge(b, i);
            }

            // 好友未读：导航上挂一颗真圆形红色数字气泡（不再用 ⭐）。
            // 挂在「好友」按钮下面，随按钮一起移动 / 隐藏，不用单独算位置。
            UiButton navFriend = _navs[PageFriend];
            if (navFriend != null)
            {
                const float bs = 26f;
                float bx = NavW - bs - 10f;
                _navBadge = MakeCircle(navFriend.Go.transform, "NavBadge", Palette.Up);
                if (_navBadge != null) Ui.Place(_navBadge.gameObject, bx, 7f, bs, bs);
                _navBadgeTx = Ui.MakeText(navFriend.Go.transform, "NavBadgeTx", "", 14f,
                    Palette.Hex("FFFFFF"), Ui.AlignCenter, false);
                if (_navBadgeTx != null) Ui.Place(_navBadgeTx.gameObject, bx, 7f, bs, bs);
            }

            // 图标是按字体实际字形筛出来的，缺哪个字在这里留一条记录，
            // 免得侧栏上少了个图标却没人知道是字体没有还是写错了
            int shown = 0, fallback = 0;
            StringBuilder miss = new StringBuilder();
            for (int i = 0; i < NavIcons.Length && i < PageCount; i++)
            {
                if (IconOf(i).Length > 0) { shown++; continue; }
                fallback++;
                if (miss.Length > 0) miss.Append('、');
                miss.Append(NavTitles[i]);
            }
            Core.Log.Msg("[导航] 图形字可用 " + shown + "/" + PageCount
                + (fallback > 0 ? "，其余 " + fallback + " 项用单字徽章：" + miss : ""));

            // 初始分组跟着当前页走；Build 期间不写日志
            ShowNavGroup(GroupOfPage(_page), false);
        }

        /// <summary>
        /// 导航行左边那颗图标徽章：圆角色块（按分组上色）+ 白色图形字 / 单字兜底。
        /// 文案跟着右移让位。
        ///
        /// 原来图标是拼在按钮文字里的，而 Noto Sans SC 没有 ⚑✎⚙☰◔⚖ 这些字，
        /// 缺字的那 11 行就整行没有图标（见 问题截图/这四个前面也加上表情.png 等）。
        /// 现在改成独立的色块：字体缺字有单字兜底，色块本身也让图标跟正文分得开。
        /// </summary>
        private static void AddNavBadge(UiButton b, int page)
        {
            const float s = 22f;      // 徽章边长
            const float bx = 6f;      // 离按钮左边缘
            const float gap = 5f;     // 徽章和文案之间
            float by = (40f - s) * 0.5f;
            Image chip = Ui.MakeSliced(b.Go.transform, "NavIcon", Ui.Chip(), NavIconColor(page), false);
            if (chip != null) Ui.Place(chip.gameObject, bx, by, s, s);
            TextMeshProUGUI tx = Ui.MakeText(b.Go.transform, "NavIconTx", NavMark(page), 15f,
                MarkOn(NavIconColor(page)), Ui.AlignCenter, false);
            if (tx != null) Ui.Place(tx.gameObject, bx, by, s, s);
            // 文案给徽章让位：左内缩从 14 挪到「徽章右边 + gap」，右边留 6（见 Layout 里 NavW 的注释）
            if (b.Label != null)
            {
                RectTransform tr = Ui.Rect(b.Label.gameObject);
                if (tr != null)
                {
                    tr.offsetMin = new Vector2(bx + s + gap, 0f);
                    tr.offsetMax = new Vector2(-6f, 0f);
                }
            }
        }

        /// <summary>
        /// 徽章里那个字的颜色：底色亮就用深字、底色暗就用白字。
        /// 深色主题下分组色偏亮（金 FFC24A、青 9EC7DE），亮色主题下偏深，
        /// 一律用白字会有一半看不清。
        /// </summary>
        private static Color MarkOn(Color bg)
        {
            float lum = 0.299f * bg.r + 0.587f * bg.g + 0.114f * bg.b;
            return lum > 0.62f ? Palette.Hex("1B2D3F") : Palette.Hex("FFFFFF");
        }

        /// <summary>页号属于哪个分类。</summary>
        private static int GroupOfPage(int page)
        {
            for (int g = 0; g < NavGroupPages.Length; g++)
            {
                int[] set = NavGroupPages[g];
                for (int i = 0; i < set.Length; i++)
                {
                    if (set[i] == page) return g;
                }
            }
            return 0;
        }

        private static void ShowNavGroup(int g)
        {
            ShowNavGroup(g, true);
        }

        /// <summary>切分类：只把该分类下的页面按钮排出来，其余收起来。</summary>
        private static void ShowNavGroup(int g, bool log)
        {
            if (g < 0 || g >= NavGroups.Length) return;
            if (log && g != _navGroup) LogOp("导航分类 → " + NavGroups[g]);
            _navGroup = g;

            for (int i = 0; i < _navGroupBtn.Length; i++)
            {
                UiButton b = _navGroupBtn[i];
                if (b == null) continue;
                bool on = i == g;
                b.SetBg(on ? Palette.NavOn : Palette.BtnIdle);
                b.SetTextColor(on ? Palette.Title : Palette.Sub);
            }

            // 分类块 2 行 + 8 的间隔，页面按钮从这里往下排
            float y0 = BodyY + 2f + 2f * 38f + 10f;
            int[] set = NavGroupPages[g];
            for (int i = 0; i < PageCount; i++)
            {
                UiButton b = _navs[i];
                if (b == null) continue;
                int slot = -1;
                for (int k = 0; k < set.Length; k++)
                {
                    if (set[k] == i) { slot = k; break; }
                }
                bool show = slot >= 0;
                b.SetActive(show);
                if (show) b.Place(Pad, y0 + slot * 46f, NavW, 40f);
            }
        }

        /// <summary>底栏：状态提示 + 缩放按钮。</summary>
        private static void BuildFooter(Transform root)
        {
            _statusText = Ui.MakeText(root, "Status", _status, 17f, Palette.Muted, Ui.AlignLeft, false);
            // 右边要留出「读数」那枚开关（84 + 8 缝）和三个缩放键（102）
            if (_statusText != null) Ui.Place(_statusText.gameObject, Pad, FooterY, W - Pad * 2f - 210f, FooterH);

            // 图表读数开关摆成常驻的小按钮：右键开关玩家不一定知道，误触关掉之后
            // 只会觉得「鼠标放上去不报数了，功能坏了」。这枚按钮把状态写在脸上，
            // 点一下就回来（见问题截图/鼠标放上去 无法显示详细信息了）。
            _chartInfoBtn = Ui.MakeButton(root, "ChartInfo", "", FsSmall, Palette.BtnIdle,
                Palette.Sub, ToggleChartInfo, Ui.AlignCenter);
            if (_chartInfoBtn != null) _chartInfoBtn.Place(W - Pad - 198f, FooterY, 84f, 30f);
            RefreshChartInfoBtn();
            Core.Log.Msg("[图表] 悬停读数：" + (Core.ChartInfoPref ? "开启" : "关闭")
                + "（在图上右键，或点底栏的「读数」按钮切换）");

            float bx = W - Pad - 102f;
            UiButton down = Ui.MakeButton(root, "ScaleDown", "-", FsCell, Palette.BtnIdle,
                Palette.Sub, () => SetScale(_scale - 0.1f), Ui.AlignCenter);
            if (down != null) down.Place(bx, FooterY, 30f, 30f);
            UiButton up = Ui.MakeButton(root, "ScaleUp", "+", FsCell, Palette.BtnIdle,
                Palette.Sub, () => SetScale(_scale + 0.1f), Ui.AlignCenter);
            if (up != null) up.Place(bx + 36f, FooterY, 30f, 30f);
            UiButton reset = Ui.MakeButton(root, "ScaleReset", "=", FsCell, Palette.BtnIdle,
                Palette.Sub, () => SetScale(1f), Ui.AlignCenter);
            if (reset != null) reset.Place(bx + 72f, FooterY, 30f, 30f);
        }

        /// <summary>
        /// 底栏那枚开关跟着状态走：开着是亮底青字，关掉是暗底灰字 —— 一眼能看出
        /// 「鼠标放上去没数」到底是功能坏了还是被自己关掉了。
        /// </summary>
        private static void RefreshChartInfoBtn()
        {
            if (_chartInfoBtn == null) return;
            bool on = Core.ChartInfoPref;
            _chartInfoBtn.SetBg(on ? Palette.BtnIdle : Palette.Disabled);
            _chartInfoBtn.SetLabel(on ? "读数 开" : "读数 关",
                on ? Palette.Cyan : Palette.DisabledFg);
        }

        private static void SetScale(float scale)
        {
            float before = _scale;
            float next = Mathf.Clamp(scale, ScaleMin, ScaleLimit());
            if (Mathf.Abs(next - before) < 0.001f)
            {
                SetStatus("界面缩放已经是 ×" + before.ToString("0.00") + " 了。", false);
                return;
            }
            _scale = next;
            LogOp("界面缩放 " + before.ToString("0.00") + " → " + _scale.ToString("0.00"));
            Ui.SetScale(_canvas, _scale);
            // 版式尺寸是把缩放折进去算的（见 Layout）：改完必须整块重排，
            // 否则内容变大了、面板还按老尺寸摆，两边对不上
            RebuildCanvas();
            SetStatus("界面缩放 ×" + _scale.ToString("0.00")
                + "（面板会跟着重新排一次，永远卡在屏幕里）", false);
        }

        /// <summary>
        /// 分辨率 / 显示方式变了：面板的版式尺寸是按建面板那一刻的屏幕算死的，
        /// 不重排就会「膨胀」到屏幕外，边上的按钮点不到（见问题截图）。
        /// 每帧只比两个整数，开销可以忽略。
        /// </summary>
        private static void CheckScreenChange()
        {
            if (Screen.width == _screenW && Screen.height == _screenH) return;
            Core.Log.Msg("[界面] 屏幕 " + _screenW + "x" + _screenH + " → "
                + Screen.width + "x" + Screen.height + "，按新尺寸重排面板");
            // 先记账再重建：万一重建失败，也不至于每帧都重排一次转不出来
            _screenW = Screen.width;
            _screenH = Screen.height;
            // 换到更小的窗口 / 分辨率后，原先的缩放倍数可能已经超过新屏幕撑得住的上限
            // （见 ScaleLimit），夹回来再重排，否则内容会溢出到屏幕外
            float cap = ScaleLimit();
            if (_canvas != null && _scale > cap + 0.001f)
            {
                _scale = cap;
                Ui.SetScale(_canvas, _scale);
                Core.Log.Msg("[界面] 屏幕变小，缩放自动夹到 ×" + _scale.ToString("0.00"));
            }
            if (_canvas != null) RebuildCanvas();
        }

        /// <summary>新建一个页面容器并放到内容区。</summary>
        private static GameObject NewPage(Transform root, string name)
        {
            GameObject page = Ui.New(name, root);
            Ui.Place(page, ContentX, BodyY, ContentW, BodyH);
            Ui.SetActive(page, false);
            return page;
        }

        /// <summary>卡片标题。</summary>
        private static void CardTitle(Transform parent, string name, float x, float y, string text, float size)
        {
            TextMeshProUGUI t = Ui.MakeText(parent, name, text, size, Palette.Title, Ui.AlignLeft, false);
            if (t != null) Ui.Place(t.gameObject, x, y, 320f, size + 4f);
        }

        // ── 总览 ──────────────────────────────────────────────────────

        private static GameObject BuildOverview(Transform root)
        {
            GameObject page = NewPage(root, "PageOverview");

            Ui.MakeCard(page.transform, "OvCardA", 0f, 0f, ContentW, 190f);
            CardTitle(page.transform, "OvTitleA", 24f, 12f, "运营概览", FsPageTitle);

            // 四个标签必须跟 RefreshOverview 里喂的值一一对上：
            // 原来第 3 项写的是「黑市开户」，值却是已实现盈亏，对不上号
            // （见 问题截图/运营概览里的黑市开户没什么用换成别的.png）
            string[] labels = { "运营周期", "持仓标的", "已实现盈亏", "累计分红" };
            float bw = (ContentW - 48f - 20f) * 0.5f;
            for (int i = 0; i < 4; i++)
            {
                float x = 24f + (i % 2) * (bw + 20f);
                float y = 56f + (i / 2) * 62f;
                TextMeshProUGUI lab = Ui.MakeText(page.transform, "OvLab" + i, labels[i], FsSmall,
                    Palette.Muted, Ui.AlignLeft, false);
                if (lab != null) Ui.Place(lab.gameObject, x, y, bw, 18f);
                _ovValue[i] = Ui.MakeText(page.transform, "OvVal" + i, "-", FsCardTitle,
                    Palette.Gold, Ui.AlignLeft, false);
                if (_ovValue[i] != null) Ui.Place(_ovValue[i].gameObject, x, y + 20f, bw, 30f);
            }

            Ui.MakeCard(page.transform, "OvCardB", 0f, 202f, ContentW, BodyH - 202f);
            CardTitle(page.transform, "OvTitleB", 24f, 214f, "持仓明细", FsPageTitle);
            _ovHold = Ui.MakeText(page.transform, "OvHold", "", FsCell, Palette.CardBody,
                Ui.AlignTopLeft, true);
            if (_ovHold != null) Ui.Place(_ovHold.gameObject, 24f, 256f, ContentW - 48f, BodyH - 202f - 78f);
            return page;
        }

        // ── 行情报价 ──────────────────────────────────────────────────

        // 列布局：名称（公司名）｜标签 [层区-业务]｜现价｜涨跌｜资金｜近期方向｜状态
        // 列的 X / 宽不再写死，按权重摊到「内容区宽 − 星标列 − 两边内缩」上
        // （见 Layout），这样 1920 屏上表格会一起变宽，不会在右边留一大片空。
        // 「资金」= 今天的散户/机构/操盘手净流入，是免费能看的那一行公开信息；
        // 逐笔明细和主力席位留给四期的付费「资金透视」。
        private static readonly float[] QuoteColWeight = { 160f, 200f, 92f, 104f, 118f, 158f, 106f };
        private static readonly float[] QuoteColGap = { 16f, 22f, 20f, 20f, 16f, 16f };
        private static readonly float[] QuoteColX = new float[7];
        private static readonly float[] QuoteColW = new float[7];
        private static readonly string[] QuoteHead = { "名称", "标签", "现价", "涨跌", "资金", "近期方向", "状态" };
        private const int QuoteCols = 7;

        /// <summary>行情表每列的左右内缩。表头和单元格共用，保证两者的边界完全对齐。</summary>
        private static float QuotePad(int c)
        {
            return c == 0 ? 10f : (c == 1 ? 6f : (c == 6 ? 4f : 6f));
        }

        private static GameObject BuildQuote(Transform root)
        {
            GameObject page = NewPage(root, "PageQuote");
            Ui.MakeCard(page.transform, "QCard", 0f, 0f, ContentW, BodyH);
            CardTitle(page.transform, "QTitle", 24f, 12f, "行情报价", FsPageTitle);

            // 搜索框：输入即过滤，不需要回车确认。贴在标题行右侧，宽屏上自动变宽。
            const float clearW = 64f;
            float searchW = Mathf.Clamp(ContentW * 0.34f, 260f, 460f);
            float searchX = ContentW - 24f - clearW - 8f - searchW;
            _qSearch = Ui.MakeInput(page.transform, "QSearch", "搜索名称或代码", 17f,
                Palette.SlotBg, Palette.Title, Palette.Muted, OnQueryChanged);
            if (_qSearch != null) Ui.Place(_qSearch.gameObject, searchX, 16f, searchW, 34f);
            _qClear = Ui.MakeButton(page.transform, "QClear", "清除", 17f, Palette.BtnIdle,
                Palette.Sub, ClearQuery, Ui.AlignCenter);
            if (_qClear != null) _qClear.Place(ContentW - 24f - clearW, 16f, clearW, 34f);

            TextMeshProUGUI starHead = Ui.MakeText(page.transform, "QHeadStar", "★", FsSmall,
                Palette.GoldSoft, Ui.AlignCenter, false);
            if (starHead != null) Ui.Place(starHead.gameObject, 26f, 58f, 30f, 20f);

            for (int c = 0; c < QuoteCols; c++)
            {
                // 表头对齐必须和单元格一致：现价/涨跌是右对齐的数字，
                // 表头却居中，看下来就是「表头在中间、数字贴右边」的错位感。
                // 内缩也要用同一套，否则右边界会差几个像素。
                int align = (c == 0 || c == 1) ? Ui.AlignLeft
                    : ((c == 2 || c == 3 || c == 4) ? Ui.AlignRight : Ui.AlignCenter);
                float pad = QuotePad(c);
                TextMeshProUGUI h = Ui.MakeText(page.transform, "QHead" + c, QuoteHead[c], FsSmall,
                    Palette.Muted, align, false);
                if (h != null)
                    Ui.Place(h.gameObject, QuoteColX[c] + pad, 58f, QuoteColW[c] - pad * 2f, 20f);
            }

            _qCell = new TextMeshProUGUI[PageRows, QuoteCols];
            _qRow = new GameObject[PageRows];
            _qStar = new UiButton[PageRows];
            for (int i = 0; i < PageRows; i++)
            {
                int slot = i;
                float y = 84f + i * RowStep;
                UiButton row = Ui.MakeButton(page.transform, "QRow" + i, "", FsCell,
                    Palette.A(Palette.SlotBg, 0.75f), Palette.CardBody,
                    () => OpenTradeRow(slot), Ui.AlignLeft);
                if (row != null)
                {
                    row.Place(24f, y, ContentW - 48f, RowH);
                    _qRow[i] = row.Go;
                }
                for (int c = 0; c < QuoteCols; c++)
                {
                    int align = (c == 0 || c == 1) ? Ui.AlignLeft : ((c == 5 || c == 6) ? Ui.AlignCenter : Ui.AlignRight);
                    float fs = c == 1 ? 14f : (c == 0 ? 17f : (c == 2 ? 16f : 15f));
                    TextMeshProUGUI cell = Ui.MakeText(page.transform, "QC" + i + "_" + c, "-", fs,
                        Palette.CardBody, align, false);
                    if (cell != null)
                    {
                        float pad = QuotePad(c);
                        Ui.Place(cell.gameObject, QuoteColX[c] + pad, y + (RowH - 22f) * 0.5f,
                            QuoteColW[c] - pad * 2f, 22f);
                    }
                    _qCell[i, c] = cell;
                }
                // 星标最后建，压在这一行之上：点星不会顺带触发「去下单」
                UiButton star = Ui.MakeButton(page.transform, "QStar" + i, "☆", 18f,
                    Palette.A(Palette.SlotBg, 0.01f), Palette.Muted,
                    () => ToggleFavSlot(slot), Ui.AlignCenter);
                if (star != null)
                {
                    star.Place(26f, y + (RowH - 30f) * 0.5f, 30f, 30f);
                    _qStar[i] = star;
                }
            }

            _qEmpty = Ui.MakeText(page.transform, "QEmpty", "", FsBody, Palette.Muted,
                Ui.AlignCenter, false);
            if (_qEmpty != null) Ui.Place(_qEmpty.gameObject, 40f, 210f, ContentW - 80f, 30f);

            // 术语白话化：不写「牛市 / 熊市 / 震荡」，直接告诉玩家这三种方向该怎么操作。
            // 色值一律从 Palette 反推——之前这里手抄十六进制，把涨跌抄反了。
            // 文案压到一行：加上「资金」那条之后整行比内容区还宽，右侧被卡片边裁掉半个字。
            // 「资金」到底是什么挪进了新手教程第二步，那里有地方展开说。
            TextMeshProUGUI hint = Ui.MakeText(page.transform, "QHint",
                "<color=" + Palette.HexOf(Palette.Up) + ">↑ 上涨趋势</color>：可持有或逢低买　"
                + "<color=" + Palette.HexOf(Palette.Down) + ">↓ 下跌趋势</color>：先避险　"
                + "<color=" + Palette.HexOf(Palette.Muted) + ">— 横盘</color>：适合低买高卖　"
                + "<color=" + Palette.HexOf(Palette.Gold) + ">资金</color>：今日他人净买入/卖出",
                FsSmall, Palette.Muted, Ui.AlignCenter, false);
            if (hint != null) Ui.Place(hint.gameObject, 24f, BodyH - 32f, ContentW - 48f, 22f);

            BuildPager(page.transform, "Q", BodyH - 72f, out _qPrev, out _qPageText, out _qNext,
                () => TurnPage(ref _quotePage, -1, "行情报价", false, true),
                () => TurnPage(ref _quotePage, 1, "行情报价", false, true));
            return page;
        }

        // ── 我的持仓 ──────────────────────────────────────────────────

        // 列 X / 宽按权重摊满内容区（见 Layout），持仓列在宽屏上也跟着散开
        private static readonly float[] HoldColWeight = { 230f, 120f, 190f, 180f, 182f };
        private static readonly float[] HoldColGap = { 20f, 20f, 30f, 30f };
        private static readonly float[] HoldColX = new float[5];
        private static readonly float[] HoldColW = new float[5];
        private static readonly string[] HoldHead = { "名称", "持股", "成本", "市值", "浮动盈亏" };

        private static GameObject BuildHold(Transform root)
        {
            GameObject page = NewPage(root, "PageHold");
            Ui.MakeCard(page.transform, "HCard", 0f, 0f, ContentW, BodyH);
            CardTitle(page.transform, "HTitle", 24f, 12f, "我的持仓", FsPageTitle);

            _hSummary = Ui.MakeText(page.transform, "HSummary", "", FsSmall, Palette.Gold,
                Ui.AlignRight, false);
            if (_hSummary != null) Ui.Place(_hSummary.gameObject, 340f, 22f, ContentW - 364f, 22f);

            for (int c = 0; c < 5; c++)
            {
                TextMeshProUGUI h = Ui.MakeText(page.transform, "HHead" + c, HoldHead[c], FsSmall,
                    Palette.Muted, c == 0 ? Ui.AlignLeft : Ui.AlignRight, false);
                if (h != null) Ui.Place(h.gameObject, HoldColX[c], 58f, HoldColW[c], 20f);
            }

            _hCell = new TextMeshProUGUI[PageRows, 5];
            _hRow = new GameObject[PageRows];
            for (int i = 0; i < PageRows; i++)
            {
                int slot = i;
                float y = 84f + i * RowStep;
                UiButton row = Ui.MakeButton(page.transform, "HRow" + i, "", FsCell,
                    Palette.A(Palette.SlotBg, 0.75f), Palette.CardBody,
                    () => OpenTradeHeldRow(slot), Ui.AlignLeft);
                if (row != null)
                {
                    row.Place(24f, y, ContentW - 48f, RowH);
                    _hRow[i] = row.Go;
                }
                for (int c = 0; c < 5; c++)
                {
                    TextMeshProUGUI cell = Ui.MakeText(page.transform, "HC" + i + "_" + c, "-", FsCell,
                        Palette.CardBody, c == 0 ? Ui.AlignLeft : Ui.AlignRight, false);
                    if (cell != null)
                    {
                        float pad = c == 0 ? 16f : 8f;
                        Ui.Place(cell.gameObject, HoldColX[c] + pad, y + (RowH - 22f) * 0.5f,
                            HoldColW[c] - pad * 2f, 22f);
                    }
                    _hCell[i, c] = cell;
                }
            }

            _hEmpty = Ui.MakeText(page.transform, "HEmpty",
                "暂无持仓。\n去【交易下单】选中标的买入，资金不够就先在【资金划转】里转入。",
                FsCell, Palette.Muted, Ui.AlignCenter, true);
            if (_hEmpty != null) Ui.Place(_hEmpty.gameObject, 40f, 200f, ContentW - 80f, 80f);

            BuildPager(page.transform, "H", BodyH - 72f, out _hPrev, out _hPageText, out _hNext,
                () => TurnPage(ref _holdPage, -1, "我的持仓", true, false),
                () => TurnPage(ref _holdPage, 1, "我的持仓", true, false));
            return page;
        }

        // ── 交易明细记录 ──────────────────────────────────────────────
        // 数据源是 StockJournal（成交流水），不是 StockState 的快照：
        // 持仓成本在下一笔买入时就被摊掉了，事后再想反推「那笔到底赚多少」是算不出来的，
        // 所以盈亏在成交当时就算好存下来了（见 StockEngine.SellAt）。

        // 列宽按权重摊。日期列要给到 130 才放得下「第 1234 天」，
        // 股数/手续费列在最小屏（ContentW≈784）下会紧一点，靠单元格里省掉「 股」「 元」这些后缀来让位。
        private static readonly float[] JnColWeight = { 130f, 190f, 76f, 104f, 118f, 104f, 168f };
        private static readonly float[] JnColX = new float[7];
        private static readonly float[] JnColW = new float[7];
        private static readonly string[] JnHead = { "日期", "标的", "方向", "股数", "成交价", "手续费", "本笔盈亏" };

        /// <summary>单元格左右内缩。表头必须跟单元格用同一套内缩，否则左对齐的列会差出 12px、右对齐的差 8px。</summary>
        private static float JnPad(int col) { return col <= 1 ? 12f : 8f; }

        private static GameObject BuildJournal(Transform root)
        {
            GameObject page = NewPage(root, "PageJournal");
            Ui.MakeCard(page.transform, "JCard", 0f, 0f, ContentW, BodyH);
            CardTitle(page.transform, "JTitle", 24f, 12f, "交易明细记录", FsPageTitle);

            // 两行读数：第一行是「花掉多少 / 落袋多少」，第二行是笔数与胜率。
            // 挤成一行会顶到标题，所以宁可占两行 —— 这两行加起来正好和下面的
            // 过滤按钮、提示文字同高，不额外吃高度。
            _jnSummary = Ui.MakeText(page.transform, "JSummary", "", FsSmall, Palette.Gold,
                Ui.AlignRight, true);
            if (_jnSummary != null)
            {
                Ui.Truncate(_jnSummary);
                Ui.Place(_jnSummary.gameObject, 300f, 20f, ContentW - 324f, 46f);
            }

            // 过滤开关放在表头右边，跟标题同一行，不额外占高度
            _jnFilter = Ui.MakeButton(page.transform, "JFilter", "", FsSmall, Palette.BtnIdle,
                Palette.Sub, ToggleJournalFilter, Ui.AlignCenter);
            if (_jnFilter != null) _jnFilter.Place(24f, 54f, 132f, 30f);

            TextMeshProUGUI note = Ui.MakeText(page.transform, "JNote",
                "点任意一行看它的技术分析", FsSmall, Palette.Cyan, Ui.AlignLeft, false);
            if (note != null)
            {
                Ui.Truncate(note);
                Ui.Place(note.gameObject, 168f, 58f, 300f, 22f);
            }

            // 列宽按权重摊满内容区（两端各留 24）
            float total = 0f;
            for (int i = 0; i < JnColWeight.Length; i++) total += JnColWeight[i];
            float k = Mathf.Max(0.4f, (ContentW - 48f) / total);
            float x = 24f;
            for (int i = 0; i < JnColWeight.Length; i++)
            {
                JnColX[i] = x;
                JnColW[i] = Mathf.Round(JnColWeight[i] * k);
                x += JnColW[i];
            }

            for (int c = 0; c < JnHead.Length; c++)
            {
                TextMeshProUGUI h = Ui.MakeText(page.transform, "JHead" + c, JnHead[c], FsSmall,
                    Palette.Muted, c <= 1 ? Ui.AlignLeft : Ui.AlignRight, false);
                if (h != null)
                    Ui.Place(h.gameObject, JnColX[c] + JnPad(c), 92f, JnColW[c] - JnPad(c) * 2f, 20f);
            }

            _jnCell = new TextMeshProUGUI[PageRows, JnHead.Length];
            _jnRow = new GameObject[PageRows];
            _jnId = new string[PageRows];
            for (int i = 0; i < PageRows; i++)
            {
                float y = 116f + i * RowStep;
                int rowIdx = i;
                // 行本身就是按钮，点整行去看这支的技术分析。
                // 原来这里是「行条 + 透明点击层 + 单元格」三层写法，结果单元格一个都渲染不出来
                // （见 问题截图/该界面什么都看不见.png）；改回跟行情表、持仓表同一套写法。
                UiButton row = Ui.MakeButton(page.transform, "JRow" + i, "", FsCell,
                    Palette.A(Palette.SlotBg, 0.75f), Palette.CardBody,
                    () => OpenTechFromJournal(rowIdx), Ui.AlignLeft);
                if (row != null)
                {
                    row.Place(24f, y, ContentW - 48f, RowH);
                    _jnRow[i] = row.Go;
                }
                for (int c = 0; c < JnHead.Length; c++)
                {
                    TextMeshProUGUI cell = Ui.MakeText(page.transform, "JC" + i + "_" + c, "-", FsCell,
                        Palette.CardBody, c <= 1 ? Ui.AlignLeft : Ui.AlignRight, false);
                    if (cell != null)
                        Ui.Place(cell.gameObject, JnColX[c] + JnPad(c), y + (RowH - 22f) * 0.5f,
                            JnColW[c] - JnPad(c) * 2f, 22f);
                    _jnCell[i, c] = cell;
                }
            }

            _jnEmpty = Ui.MakeText(page.transform, "JEmpty",
                "还没有成交记录。\n去【行情报价】挑一支，在【交易下单】买入，成交后这里就会多一行。",
                FsCell, Palette.Muted, Ui.AlignCenter, true);
            if (_jnEmpty != null) Ui.Place(_jnEmpty.gameObject, 40f, 200f, ContentW - 80f, 80f);

            BuildPager(page.transform, "J", BodyH - 62f, out _jnPrev, out _jnPageText, out _jnNext,
                () => TurnJournalPage(-1), () => TurnJournalPage(1));
            return page;
        }

        /// <summary>当前要显示的流水（按时间倒序）。只看卖出时把买入过滤掉。</summary>
        private static List<TradeRec> JournalList()
        {
            List<TradeRec> list = new List<TradeRec>();
            for (int i = StockJournal.All.Count - 1; i >= 0; i--)
            {
                TradeRec r = StockJournal.All[i];
                if (r == null) continue;
                if (_jnOnlySell && r.Side != 1) continue;
                list.Add(r);
            }
            return list;
        }

        private static void ToggleJournalFilter()
        {
            _jnOnlySell = !_jnOnlySell;
            LogOp("交易明细：" + (_jnOnlySell ? "只看卖出" : "显示全部"));
            _jnPage = 0;
            Refresh();
        }

        private static void TurnJournalPage(int delta)
        {
            List<TradeRec> list = JournalList();
            int pages = Mathf.Max(1, Mathf.CeilToInt((float)list.Count / PageRows));
            int next = Mathf.Clamp(_jnPage + delta, 0, pages - 1);
            if (next == _jnPage)
            {
                LogOp("交易明细翻页：已经是" + (delta > 0 ? "最后一页" : "第一页") + "，忽略");
                SetStatus("交易明细已经是" + (delta > 0 ? "最后一页" : "第一页") + "了。", false);
                Refresh();
                return;
            }
            _jnPage = next;
            LogOp("交易明细翻到第 " + (_jnPage + 1) + " / " + pages + " 页");
            SetStatus("交易明细第 " + (_jnPage + 1) + " / " + pages + " 页，共 " + list.Count + " 笔。", false);
            Refresh();
        }

        private static void RefreshJournal()
        {
            if (_jnCell == null) return;

            int buys, sells;
            StockJournal.CountSides(out buys, out sells);
            int wins, losses;
            StockJournal.CountWins(out wins, out losses);
            int decided = wins + losses;
            string winRate = decided > 0 ? (wins * 100 / decided) + "%" : "—";

            if (_jnSummary != null)
            {
                double realized = StockState.ToYuan(StockState.RealizedPnl);
                _jnSummary.text = "累计手续费 " + Money(StockState.ToYuan(StockJournal.TotalFee()))
                    + " 元　已实现净盈亏 <color=" + Palette.HexOf(Palette.Pnl(realized)) + ">"
                    + Sign(realized) + Money(realized) + " 元</color>\n"
                    + "买入 " + buys + " 笔 / 卖出 " + sells + " 笔　卖出胜率 " + winRate
                    + "（" + wins + " 赢 " + losses + " 亏）";
            }
            if (_jnFilter != null)
            {
                _jnFilter.SetText(_jnOnlySell ? "只看卖出：开" : "只看卖出：关");
                _jnFilter.SetBg(_jnOnlySell ? Palette.BtnBlue : Palette.BtnIdle);
                _jnFilter.SetTextColor(_jnOnlySell ? Palette.Title : Palette.Sub);
            }

            List<TradeRec> list = JournalList();
            int pages = Mathf.Max(1, Mathf.CeilToInt((float)list.Count / PageRows));
            _jnPage = Mathf.Clamp(_jnPage, 0, pages - 1);

            bool empty = list.Count == 0;
            Ui.SetActive(_jnEmpty != null ? _jnEmpty.gameObject : null, empty);
            for (int i = 0; i < PageRows; i++)
            {
                int idx = _jnPage * PageRows + i;
                bool has = idx < list.Count;
                // 行条和单元格是兄弟节点（单元格没挂在行条下面），所以两边都要收，
                // 否则空表时会留下一排空槽，和「还没有成交记录」叠在一起
                Ui.SetActive(_jnRow != null ? _jnRow[i] : null, has);
                for (int c = 0; c < JnHead.Length; c++)
                {
                    if (_jnCell[i, c] == null) continue;
                    Ui.SetActive(_jnCell[i, c].gameObject, has);
                }
                if (!has)
                {
                    if (_jnId != null) _jnId[i] = null;
                    continue;
                }

                TradeRec r = list[idx];
                StockDef def = StockDefs.Get(r.DefId);
                string name = def != null ? def.Name : r.DefId;
                bool sell = r.Side == 1;
                if (_jnId != null) _jnId[i] = r.DefId;

                SetJnCell(i, 0, "第" + r.Day + "天", Palette.Muted);
                SetJnCell(i, 1, name, Palette.CardBody);
                // 买红卖绿，跟面板其它地方的涨跌色同一套习惯
                SetJnCell(i, 2, sell ? "卖出" : "买入", sell ? Palette.Down : Palette.Up);
                SetJnCell(i, 3, r.Shares.ToString(), Palette.CardBody);
                SetJnCell(i, 4, StockState.ToYuan(r.Price).ToString("N2"), Palette.CardBody);
                SetJnCell(i, 5, StockState.ToYuan(r.Fee).ToString("N2"), Palette.Muted);
                if (sell)
                {
                    double pnl = StockState.ToYuan(r.Pnl);
                    SetJnCell(i, 6, Sign(pnl) + Money(pnl), Palette.Pnl(pnl));
                }
                else
                {
                    SetJnCell(i, 6, "—", Palette.Muted);
                }
            }

            if (_jnPageText != null)
            {
                _jnPageText.text = "第 " + (_jnPage + 1) + " / " + pages + " 页　共 " + list.Count
                    + " 笔" + (_jnOnlySell ? "卖出" : "") + "　按时间倒序，只留最近 "
                    + StockJournal.MaxRecords + " 笔";
            }
            DimPager(_jnPrev, _jnNext, _jnPage, pages);
        }

        private static void SetJnCell(int row, int col, string text, Color color)
        {
            TextMeshProUGUI t = _jnCell[row, col];
            if (t == null) return;
            t.text = text;
            t.color = color;
        }

        // ── 交易下单 ──────────────────────────────────────────────────

        private static GameObject BuildTrade(Transform root)
        {
            GameObject page = NewPage(root, "PageTrade");

            // ── 走势图卡：主图 + 成交量副图 ──────────────────────────────
            // 高度按「下面的下单卡至少留 232」倒推：232 = 标题 32 + 两行数据 84 +
            // 数量行 34 + 步进 30 + 操作 34 + 底距 12（见 问题截图：原来 BodyH 小的时候
            // 下单卡只剩 222 高，最后一行按钮压在卡片描边上）。
            float chartCardH = Mathf.Clamp(BodyH - 244f, 240f, 420f);
            float chartExtra = chartCardH - 296f;
            float mainH = Mathf.Max(96f, 148f + chartExtra * 0.7f);
            float volH = Mathf.Max(46f, 68f + chartExtra * 0.3f);
            Ui.MakeCard(page.transform, "TCardChart", 0f, 0f, ContentW, chartCardH);
            CardTitle(page.transform, "TChartTitle", 24f, 12f, "近期走势", FsCardTitle);
            // 选中标的的 [层区-业务] 标签，紧跟在图表标题后面
            _tTag = Ui.MakeText(page.transform, "TChartTag", "", 16f, Palette.Cyan,
                Ui.AlignLeft, false);
            if (_tTag != null) Ui.Place(_tTag.gameObject, 130f, 18f, 250f, 22f);
            TextMeshProUGUI sub = Ui.MakeText(page.transform, "TChartSub",
                "纵轴：元", FsSmall, Palette.Muted, Ui.AlignRight, false);
            if (sub != null) Ui.Place(sub.gameObject, 420f, 18f, 200f, 20f);
            _tChartToggle = Ui.MakeButton(page.transform, "TChartKind", "", 16f,
                Palette.BtnIdle, Palette.Sub, ToggleChartKind, Ui.AlignCenter);
            if (_tChartToggle != null)
            {
                _tChartToggle.Place(ContentW - 24f - 190f, 10f, 190f, 34f);
                _tChartToggle.SetText(ChartKindText());
            }
            Image chart = Ui.MakeSlot(page.transform, "TChartSlot", 24f, 48f, ContentW - 48f, mainH, Palette.SlotBg);
            if (chart != null) _tChart = Ui.Rect(chart.gameObject);
            _tipTradeMain = MakeTip(_tChart, "TMain", false, "走势");

            // 成交量副图：柱越高＝当天买卖越活跃，配合上面的 K 线看量价配合
            float volTitleY = 48f + mainH + 6f;
            TextMeshProUGUI volTitle = Ui.MakeText(page.transform, "TVolTitle",
                "成交量　柱越高＝当天买卖越活跃", FsSmall, Palette.Muted, Ui.AlignLeft, false);
            if (volTitle != null) Ui.Place(volTitle.gameObject, 24f, volTitleY, 420f, 18f);
            TextMeshProUGUI volHint = Ui.MakeText(page.transform, "TVolHint",
                "要 MACD / KDJ / RSI / BOLL 去【技术分析】", FsSmall,
                Palette.Muted, Ui.AlignRight, false);
            if (volHint != null) Ui.Place(volHint.gameObject, ContentW - 24f - 400f, volTitleY, 400f, 18f);
            Image vol = Ui.MakeSlot(page.transform, "TVolSlot", 24f, volTitleY + 22f,
                ContentW - 48f, volH, Palette.SlotBg);
            if (vol != null) _tVol = Ui.Rect(vol.gameObject);
            _tipTradeVol = MakeTip(_tVol, "TVol", true, "成交量");

            // ── ① 选标的 ─────────────────────────────────────────────
            float cardsY = chartCardH + 12f;
            float cardH = BodyH - cardsY;
            float pickW = Mathf.Clamp(Mathf.Round(ContentW * 0.44f), 440f, 900f);
            Ui.MakeCard(page.transform, "TCardPick", 0f, cardsY, pickW, cardH);
            CardTitle(page.transform, "TPickTitle", 24f, cardsY + 10f, "① 选择标的", FsBody);

            // 标的多了要分页，翻页控件塞在卡片标题行右侧（跟着卡片宽走）
            _tPickPrev = Ui.MakeButton(page.transform, "TPickPrev", "◀", 16f, Palette.BtnIdle,
                Palette.Sub, () => TurnPage(ref _tradePage, -1, "选择标的", false, false), Ui.AlignCenter);
            if (_tPickPrev != null) _tPickPrev.Place(pickW - 150f, cardsY + 10f, 32f, 28f);
            _tPickPageText = Ui.MakeText(page.transform, "TPickPage", "", 16f, Palette.Muted,
                Ui.AlignCenter, false);
            if (_tPickPageText != null) Ui.Place(_tPickPageText.gameObject, pickW - 102f, cardsY + 13f, 46f, 22f);
            _tPickNext = Ui.MakeButton(page.transform, "TPickNext", "▶", 16f, Palette.BtnIdle,
                Palette.Sub, () => TurnPage(ref _tradePage, 1, "选择标的", false, false), Ui.AlignCenter);
            if (_tPickNext != null) _tPickNext.Place(pickW - 56f, cardsY + 10f, 32f, 28f);

            // 每页行数多了，标的按钮排数也跟着多（两列）
            int pickRows = (PageRows + 1) / 2;
            float pickTop = cardsY + 40f;
            float pickBottom = cardsY + cardH - 14f;
            float pickStep = Mathf.Min((pickBottom - pickTop) / pickRows, 56f);
            float pickBh = Mathf.Min(pickStep - 6f, 50f);
            float pickBw = (pickW - 58f) * 0.5f;
            _tPick = new UiButton[PageRows];
            for (int i = 0; i < PageRows; i++)
            {
                int slot = i;
                float x = 24f + (i % 2) * (pickBw + 10f);
                float y = pickTop + (i / 2) * pickStep;
                UiButton b = Ui.MakeButton(page.transform, "TPick" + i, "", 16f,
                    Palette.BtnIdle, Palette.Sub, () => PickSlot(slot), Ui.AlignCenter);
                if (b == null) continue;
                b.Place(x, y, pickBw, pickBh);
                _tPick[i] = b;
            }

            // ── ② 下单 ───────────────────────────────────────────────
            float ow = ContentW - pickW - 16f;
            float ox = pickW + 16f;
            Ui.MakeCard(page.transform, "TCardOrder", ox, cardsY, ow, cardH);
            CardTitle(page.transform, "TOrderTitle", ox + 24f, cardsY + 10f, "② 下单", FsBody);
            _tName = Ui.MakeText(page.transform, "TName", "", 18f, Palette.Cyan, Ui.AlignRight, false);
            if (_tName != null) Ui.Place(_tName.gameObject, ox + 160f, cardsY + 12f, ow - 184f, 24f);

            string[] statLabels = { "现价", "涨跌", "近期方向", "持仓", "最多可买", "风险" };
            float colW = (ow - 48f - 20f) / 3f;
            float colStep = colW + 10f;
            for (int i = 0; i < statLabels.Length; i++)
            {
                float x = ox + 24f + (i % 3) * colStep;
                // 第一行从 34 起：标题「② 下单」占到 32，原来从 28 起会压在标题上
                float y = cardsY + ((i / 3) == 0 ? 34f : 76f);
                TextMeshProUGUI lab = Ui.MakeText(page.transform, "TStatLab" + i, statLabels[i], FsSmall,
                    Palette.Muted, Ui.AlignLeft, false);
                if (lab != null) Ui.Place(lab.gameObject, x, y, colW, 16f);
                if (i == 0) _tPriceLab = lab;
                _tStat[i] = Ui.MakeText(page.transform, "TStatVal" + i, "-", 19f,
                    Palette.Title, Ui.AlignLeft, false);
                if (_tStat[i] != null) Ui.Place(_tStat[i].gameObject, x, y + 16f, colW, 22f);
            }

            // 下单数量：可以直接敲数字，也可以用下面的步进按钮微调
            _tQtyLab = Ui.MakeText(page.transform, "TQtyLab", "数量", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (_tQtyLab != null) Ui.Place(_tQtyLab.gameObject, ox + 24f, cardsY + 120f, 52f, 20f);

            _tQtyInput = Ui.MakeInput(page.transform, "TQty", "股数", 17f,
                Palette.SlotBg, Palette.Title, Palette.Muted, OnQtyChanged);
            if (_tQtyInput != null) Ui.Place(_tQtyInput.gameObject, ox + 84f, cardsY + 116f, 130f, 28f);

            _tQtyHint = Ui.MakeText(page.transform, "TQtyHint", "", FsSmall, Palette.Title,
                Ui.AlignLeft, false);
            if (_tQtyHint != null) Ui.Place(_tQtyHint.gameObject, ox + 224f, cardsY + 118f, ow - 248f, 24f);

            // 黑市未开户时，把输入框整行换成警告，免得玩家敲了半天才发现买不了
            _tQtyWarn = Ui.MakeText(page.transform, "TQtyWarn",
                "黑市股需要先在【黑市开户】办证明才能买入", FsSmall, Palette.Danger,
                Ui.AlignLeft, false);
            if (_tQtyWarn != null) Ui.Place(_tQtyWarn.gameObject, ox + 24f, cardsY + 118f, ow - 48f, 24f);
            if (_tQtyWarn != null) Ui.SetActive(_tQtyWarn.gameObject, false);

            // 步进 / 操作按钮跟着下单卡宽度摊开，但各自设上限，别在宽屏上拉成一条长条
            string[] steps = { "-10", "-1", "+1", "+10" };
            float stepW = Mathf.Min((ow - 48f - 30f) / 4f, 180f);
            for (int i = 0; i < 4; i++)
            {
                int delta = i < 2 ? (i == 0 ? -10 : -1) : (i == 2 ? 1 : 10);
                UiButton b = Ui.MakeButton(page.transform, "TStep" + i, steps[i], 16f,
                    Palette.BtnIdle, Palette.Sub, () => AddAmount(delta), Ui.AlignCenter);
                if (b != null) b.Place(ox + 24f + i * (stepW + 10f), cardsY + 150f, stepW, 30f);
            }

            string[] acts = { "买入", "卖出", "清仓" };
            Action[] handlers = { DoBuy, DoSell, DoSellAll };
            Color[] colors = { Palette.BtnBlue, Palette.BtnOrange, Palette.BtnRed };
            float actW = Mathf.Min((ow - 48f - 24f) / 3f, 300f);
            for (int i = 0; i < 3; i++)
            {
                UiButton b = Ui.MakeButton(page.transform, "TAct" + i, acts[i], FsCell,
                    colors[i], Palette.Title, handlers[i], Ui.AlignCenter);
                if (b != null) b.Place(ox + 24f + i * (actW + 12f), cardsY + 186f, actW, 34f);
                if (i == 0) _tBuy = b;
            }
            return page;
        }

        // ── 技术分析 ──────────────────────────────────────────────────
        //  一张主图 + 两个副图，全部挂在同一个时间档上，横轴对齐。
        //  纵向位置写在 TcMainY / TcT1Y / TcSub1Y / TcT2Y / TcSub2Y 里，
        //  由 Layout() 按内容区高度摊开：1280 屏是 130 / 344 / 368 / 446 / 470，
        //  1920 屏上主图和两个副图都会长高，不会在底部留一大片空。

        private static GameObject BuildTech(Transform root)
        {
            GameObject page = NewPage(root, "PageTech");
            Ui.MakeCard(page.transform, "TcCard", 0f, 0f, ContentW, BodyH);
            CardTitle(page.transform, "TcTitle", 24f, 12f, "技术分析", FsPageTitle);

            _tcName = Ui.MakeText(page.transform, "TcName", "", FsBody, Palette.Cyan,
                Ui.AlignLeft, false);
            if (_tcName != null) Ui.Place(_tcName.gameObject, 150f, 16f, 200f, 26f);
            _tcTag = Ui.MakeText(page.transform, "TcTag", "", FsSmall, Palette.Muted,
                Ui.AlignLeft, false);
            if (_tcTag != null) Ui.Place(_tcTag.gameObject, 352f, 20f, 220f, 22f);

            // 左右换标的：配合下面的下拉列表，翻相邻标的时不用来回找
            _tcPrev = Ui.MakeButton(page.transform, "TcPrev", "◀ 上一支", 16f, Palette.BtnIdle,
                Palette.Sub, () => TechStep(-1), Ui.AlignCenter);
            if (_tcPrev != null) _tcPrev.Place(ContentW - 24f - 402f, 12f, 88f, 34f);
            _tcNext = Ui.MakeButton(page.transform, "TcNext", "下一支 ▶", 16f, Palette.BtnIdle,
                Palette.Sub, () => TechStep(1), Ui.AlignCenter);
            if (_tcNext != null) _tcNext.Place(ContentW - 24f - 308f, 12f, 88f, 34f);

            // 「返回 → 去下单」：从行情页点一行进来的买卖闭环，看完图直接下单，不用再摸导航
            _tcBack = Ui.MakeButton(page.transform, "TcBack", "返回", 16f, Palette.BtnIdle,
                Palette.Sub, BackFromTech, Ui.AlignCenter);
            if (_tcBack != null) _tcBack.Place(ContentW - 24f - 204f, 12f, 88f, 34f);
            _tcGo = Ui.MakeButton(page.transform, "TcGo", "去下单", 16f, Palette.BtnGreen,
                Palette.Title, GoTradeFromTech, Ui.AlignCenter);
            if (_tcGo != null) _tcGo.Place(ContentW - 24f - 100f, 12f, 100f, 34f);

            // 时间档
            for (int i = 0; i < RangeNames.Length; i++)
            {
                int idx = i;
                UiButton b = Ui.MakeButton(page.transform, "TcRange" + i, RangeNames[i], 17f,
                    Palette.BtnIdle, Palette.Sub, () => SetRange(idx), Ui.AlignCenter);
                if (b == null) continue;
                b.Place(24f + i * 66f, 50f, 62f, 32f);
                _tcRange[i] = b;
            }

            _tcKind = Ui.MakeButton(page.transform, "TcKind", "", 17f, Palette.BtnIdle,
                Palette.Sub, ToggleTechKind, Ui.AlignCenter);
            if (_tcKind != null) _tcKind.Place(366f, 50f, 110f, 32f);

            _tcOverlay = Ui.MakeButton(page.transform, "TcOverlay", "", 17f, Palette.BtnIdle,
                Palette.Sub, CycleOverlay, Ui.AlignCenter);
            if (_tcOverlay != null)
                _tcOverlay.Place(490f, 50f, Mathf.Min(ContentW - 24f - 490f, 620f), 32f);

            // ── 标的下拉框 ────────────────────────────────────────────
            // 26 支平铺成网格太挤（一行 13 个，名字挤在一起看不清），
            // 改成「按钮 + 展开列表」：按钮显示当前标的，点开是一张按层区分组的表。
            TextMeshProUGUI dropLab = Ui.MakeText(page.transform, "TcDropLab", "标的", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (dropLab != null) Ui.Place(dropLab.gameObject, 24f, 94f, 44f, 22f);

            _tcInfo = Ui.MakeText(page.transform, "TcInfo", "", FsSmall, Palette.Muted,
                Ui.AlignRight, false);
            if (_tcInfo != null) Ui.Place(_tcInfo.gameObject, 520f, 94f, ContentW - 24f - 520f, 22f);

            Image main = Ui.MakeSlot(page.transform, "TcMainSlot", 24f, TcMainY, ContentW - 48f, TechMainH,
                Palette.SlotBg);
            if (main != null) _tcMain = Ui.Rect(main.gameObject);
            _tipTechMain = MakeTip(_tcMain, "TcMain", false, "K 线");

            _tcSubTitle1 = Ui.MakeButton(page.transform, "TcSubTitle1", "", 16f, Palette.BtnIdle,
                Palette.Sub, () => CycleSub(true), Ui.AlignLeft);
            if (_tcSubTitle1 != null) _tcSubTitle1.Place(24f, TcT1Y, 300f, 24f);
            _tcSubTitle2 = Ui.MakeButton(page.transform, "TcSubTitle2", "", 16f, Palette.BtnIdle,
                Palette.Sub, () => CycleSub(false), Ui.AlignLeft);
            if (_tcSubTitle2 != null) _tcSubTitle2.Place(24f, TcT2Y, 300f, 24f);

            // 每条线分别是什么，写在标题右边 —— 只有两条线却不说谁是谁，等于让人猜
            _tcSubLegend1 = Ui.MakeText(page.transform, "TcSubLegend1", "", FsSmall, Palette.Muted,
                Ui.AlignRight, false);
            if (_tcSubLegend1 != null)
                Ui.Place(_tcSubLegend1.gameObject, 336f, TcT1Y + 2f, ContentW - 24f - 336f, 22f);
            _tcSubLegend2 = Ui.MakeText(page.transform, "TcSubLegend2", "", FsSmall, Palette.Muted,
                Ui.AlignRight, false);
            if (_tcSubLegend2 != null)
                Ui.Place(_tcSubLegend2.gameObject, 336f, TcT2Y + 2f, ContentW - 24f - 336f, 22f);

            Image sub1 = Ui.MakeSlot(page.transform, "TcSub1Slot", 24f, TcSub1Y, ContentW - 48f, TechSubH,
                Palette.SlotBg);
            if (sub1 != null) _tcSub1 = Ui.Rect(sub1.gameObject);
            _tipTechSub1 = MakeTip(_tcSub1, "TcSub1", true, "副图");
            Image sub2 = Ui.MakeSlot(page.transform, "TcSub2Slot", 24f, TcSub2Y, ContentW - 48f, TechSubH,
                Palette.SlotBg);
            if (sub2 != null) _tcSub2 = Ui.Rect(sub2.gameObject);
            _tipTechSub2 = MakeTip(_tcSub2, "TcSub2", true, "副图");

            // 下拉整体（按钮 + 展开面板）必须最后建：面板要盖在主图上面，
            // 建早了就会被后面建的图表压住，展开也看不见。
            _tcDrop = new UiDrop("TcDrop", "技术分析", PickStock);
            _tcDrop.Build(page.transform, 72f, 88f, 430f, 32f, 24f, 126f, ContentW - 48f);
            return page;
        }

        // ── 通用标的下拉 ──────────────────────────────────────────────
        //  技术分析页和盘中交易页都要「选一支标的」，两页看的是同一个 _selectedId，
        //  所以下拉做成一个可复用的实例：谁页面上需要就 Build 一份，选中回调统一走 PickStock。
        //  26 支平铺成网格太挤（一行 13 个，名字挤在一起看不清），所以是「按钮 + 展开列表」。

        private sealed class UiDrop
        {
            public UiButton Btn;
            public UiButton[] Items = new UiButton[0];
            public GameObject Panel;
            public bool Open;

            private readonly string _node;     // 节点名前缀，避免两页的节点重名
            private readonly string _tag;      // 日志前缀
            private readonly Action<int> _pick;

            public UiDrop(string node, string tag, Action<int> pick)
            {
                _node = node;
                _tag = tag;
                _pick = pick;
            }

            /// <summary>
            /// pw 是展开面板的宽度。按层区分行，一行放得下 8 个 chip，26 支刚好五行铺完。
            /// </summary>
            public void Build(Transform parent, float bx, float by, float bw, float bh,
                float px, float py, float pw)
            {
                Btn = Ui.MakeButton(parent, _node + "Btn", "选择标的", 17f, Palette.BtnIdle,
                    Palette.Title, Toggle, Ui.AlignLeft);
                if (Btn != null) Btn.Place(bx, by, bw, bh);

                StockDef[] all = StockDefs.All;
                Items = new UiButton[all.Length];
                Panel = Ui.New(_node + "Panel", parent);
                if (Panel == null) return;

                const float rowH = 36f;
                const float chipX = 104f;
                const float chipW = 92f;
                const float chipStep = 96f;
                float ph = 10f + TechGroups.Length * rowH + 4f;

                // 用整张卡片当底板（带描边、不透明），展开时正好把下面的图盖住
                Ui.MakeCard(Panel.transform, "Bg", 0f, 0f, pw, ph);
                Ui.Place(Panel, px, py, pw, ph);

                int row = 0;
                for (int g = 0; g < TechGroups.Length; g++)
                {
                    string region = TechGroups[g];
                    // 先看这一层区有几支，空的直接跳过，不留空行
                    int count = 0;
                    for (int i = 0; i < all.Length; i++) if (all[i].Region == region) count++;
                    if (count == 0) continue;

                    float y = 10f + row * rowH;
                    TextMeshProUGUI lab = Ui.MakeText(Panel.transform, "G" + g, region, 15f,
                        Palette.Cyan, Ui.AlignRight, false);
                    if (lab != null) Ui.Place(lab.gameObject, 12f, y + 5f, 84f, 22f);

                    int slot = 0;
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i].Region != region) continue;
                        int idx = i;
                        UiButton b = Ui.MakeButton(Panel.transform, _node + "Item" + i,
                            all[i].Name, 14f, Palette.BtnIdle, Palette.Sub,
                            () => _pick(idx), Ui.AlignCenter);
                        if (b == null) { slot++; continue; }
                        b.Place(chipX + slot * chipStep, y, chipW, 30f);
                        Items[i] = b;
                        slot++;
                    }
                    row++;
                }

                Ui.SetActive(Panel, false);
            }

            public void Toggle()
            {
                Open = !Open;
                LogOp(_tag + "标的列表" + (Open ? "展开" : "收起"));
                Ui.SetActive(Panel, Open);
            }

            /// <summary>切页 / 选完标的都要收起，否则下次回到这一页会莫名其妙挂着列表。</summary>
            public void Close()
            {
                if (!Open) return;
                Open = false;
                Ui.SetActive(Panel, false);
            }

            /// <summary>按钮文案跟当前标的走，列表里当前这支点亮成蓝色，一眼看出在看谁。</summary>
            public void Sync(string selectedId)
            {
                StockDef def = StockDefs.Get(selectedId);
                if (Btn != null && def != null)
                    Btn.SetLabel(def.Name + "　" + StockDefs.Tag(def) + "　▼", Palette.Title);

                StockDef[] all = StockDefs.All;
                for (int i = 0; i < Items.Length; i++)
                {
                    UiButton b = Items[i];
                    if (b == null || i >= all.Length) continue;
                    bool on = all[i].Id == selectedId;
                    b.SetBg(on ? Palette.BtnBlue : Palette.BtnIdle);
                    b.SetTextColor(on ? Palette.Title : Palette.Sub);
                }
            }
        }

        /// <summary>
        /// 下拉里点了某支标的。技术分析页和盘中交易页共用这一份：
        /// 两页看的是同一个「当前标的」，在哪一页换，另一页也跟着换。
        /// </summary>
        private static void PickStock(int idx)
        {
            StockDef[] all = StockDefs.All;
            if (idx < 0 || idx >= all.Length) return;
            if (all[idx].Id == _selectedId) return;
            SelectStock(all[idx].Id);
        }

        // ── 盘中交易 ──────────────────────────────────────────────────
        //  只看一天的盘中走势，给短线盯盘用。白线是实时成交价，黄线是当日均价，
        //  柱子是每段成交量。横轴是营业日的进度而不是时刻 —— 游戏里的商店没有钟，
        //  一天什么时候结束由玩家点「收工」决定，所以走一格＝接待一位客人。
        //  版式（ContentW=1050 / BodyH=560）：
        //    12..46      标题 + 标的下拉 + 右上角实时读数
        //    54..290     分时图
        //    296..318    图例
        //    338..552    左：挂单表单　右：挂单列表

        private static GameObject BuildIntra(Transform root)
        {
            GameObject page = NewPage(root, "PageIntra");

            Ui.MakeCard(page.transform, "ItCardTop", 0f, 0f, ContentW, IntraTopH);
            CardTitle(page.transform, "ItTitle", 24f, 12f, "盘中交易", FsPageTitle);

            _itLive = Ui.MakeText(page.transform, "ItLive", "", 17f, Palette.Title,
                Ui.AlignRight, false);
            if (_itLive != null) Ui.Place(_itLive.gameObject, 590f, 18f, ContentW - 24f - 590f, 26f);

            Image chart = Ui.MakeSlot(page.transform, "ItChartSlot", 24f, 54f, ContentW - 48f,
                IntraChartH, Palette.SlotBg);
            if (chart != null) _itChart = Ui.Rect(chart.gameObject);

            // 悬停读价那一层单独盖在图槽上，不画进图里 —— 图每推进一段就整个重画一次，
            // 画进去的话这层会被一起清掉。raycast 全关，别抢走图的交互。
            GameObject hover = Ui.New("ItHover", page.transform);
            if (hover != null)
            {
                Ui.Place(hover, 24f, 54f, ContentW - 48f, IntraChartH);

                Image hline = Ui.MakeImage(hover.transform, "ItHoverLine",
                    new Color(Palette.Cyan.r, Palette.Cyan.g, Palette.Cyan.b, 0.45f), false);
                if (hline != null) _itHoverLine = Ui.Rect(hline.gameObject);

                Image hdot = Ui.MakeImage(hover.transform, "ItHoverDot", Palette.Title, false);
                if (hdot != null) _itHoverDot = Ui.Rect(hdot.gameObject);

                GameObject tag = Ui.New("ItHoverTag", hover.transform);
                if (tag != null)
                {
                    // 描边 + 半透明纸面，跟通用读数层、面板卡片同一套配色
                    Image tagRim = Ui.MakeSliced(tag.transform, "ItHoverTagRim", Ui.Card(),
                        Palette.A(Palette.CardRim, 0.85f), false);
                    if (tagRim != null) Ui.Stretch(tagRim.gameObject, 0f);
                    Image tagFace = Ui.MakeSliced(tag.transform, "ItHoverTagFace", Ui.Card(),
                        Palette.A(Palette.CardBg, TipAlpha), false);
                    if (tagFace != null) Ui.Stretch(tagFace.gameObject, 1f);
                    _itHoverText = Ui.MakeText(tag.transform, "ItHoverTagText", "", TipFont,
                        Palette.Sub, Ui.AlignTopLeft, false);
                    if (_itHoverText != null)
                    {
                        Ui.Place(_itHoverText.gameObject, 8f, 5f, HoverTagW - 16f, HoverTagH - 10f);
                        try { _itHoverText.lineSpacing = 1f; } catch { }
                    }
                    _itHoverTag = tag;
                    Ui.SetActive(tag, false);
                }

                _itHover = Ui.Rect(hover);
                Ui.SetActive(hover, false);
                // 这层是新物件，指纹也得清掉，不然会以为「还是刚才那一段」而不去摆位置
                _itHoverIdx = -1;
                _itHoverKey = "";
            }

            // 图例：一长串灰字没人看，把每条线按它在图上真实的颜色标出来
            // （见 问题截图/这页也太啰嗦了 而且气泡挡住内容….png）
            string legendTx =
                "<color=" + Palette.HexOf(Palette.Title) + ">白线</color>=实时成交价　"
                + "<color=" + Palette.HexOf(Palette.Gold) + ">黄线</color>=当日均价　"
                + "<color=" + Palette.HexOf(Palette.Muted) + ">灰虚线</color>=昨收　"
                + "<color=" + Palette.HexOf(Palette.Up) + ">柱子</color>=成交量　"
                + "<color=" + Palette.HexOf(Palette.Cyan) + ">（横轴是营业日进度，每接待一位客人挪一段）</color>";
            TextMeshProUGUI legend = Ui.MakeText(page.transform, "ItLegend", legendTx,
                16f, Palette.Sub, Ui.AlignLeft, false);
            if (legend != null) Ui.Place(legend.gameObject, 24f, IntraTopH - 34f, ContentW - 48f, 22f);

            // 下拉最后建：展开的面板要盖在分时图上面，建早了就会被图压住
            _itDrop = new UiDrop("ItDrop", "盘中交易", PickStock);
            _itDrop.Build(page.transform, 150f, 14f, 420f, 32f, 24f, 54f, ContentW - 48f);

            float formY = IntraFormY;
            float formH = IntraFormH;
            // 左卡放挂单表单，右卡吃掉剩下的宽度，挂单列表宽了才好对列
            float formW = Mathf.Clamp(Mathf.Round(ContentW * 0.46f), 460f, 760f);
            float listX = formW + 16f;
            float listW = ContentW - listX;

            // ── 左：挂单表单 ──────────────────────────────────────────
            Ui.MakeCard(page.transform, "ItCardForm", 0f, formY, formW, formH);
            CardTitle(page.transform, "ItFormTitle", 24f, formY + 10f, "挂单", FsBody);

            TextMeshProUGUI sideLab = Ui.MakeText(page.transform, "ItSideLab", "方向", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (sideLab != null) Ui.Place(sideLab.gameObject, 14f, formY + 49f, 44f, 24f);
            string[] sides = { "买入", "卖出" };
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                UiButton b = Ui.MakeButton(page.transform, "ItSide" + i, sides[i], FsCell,
                    Palette.BtnIdle, Palette.Sub, () => SetOrdSide(idx), Ui.AlignCenter);
                if (b == null) continue;
                b.Place(62f + i * 82f, formY + 44f, 76f, 30f);
                _itSideBtn[i] = b;
            }

            TextMeshProUGUI qtyLab = Ui.MakeText(page.transform, "ItQtyLab", "数量", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (qtyLab != null) Ui.Place(qtyLab.gameObject, 14f, formY + 87f, 44f, 24f);
            _itQtyInput = Ui.MakeInput(page.transform, "ItQty", "股数", 17f,
                Palette.SlotBg, Palette.Title, Palette.Muted, OnOrdQtyChanged);
            if (_itQtyInput != null) Ui.Place(_itQtyInput.gameObject, 62f, formY + 82f, 110f, 30f);
            // 步进键摊满表单卡右侧（宽屏上跟着变宽，但单个不超过 120）
            float itStepW = Mathf.Min((formW - 28f - 182f - 3f * 10f) / 4f, 120f);
            for (int i = 0; i < 4; i++)
            {
                int delta = i == 0 ? -10 : (i == 1 ? -1 : (i == 2 ? 1 : 10));
                UiButton b = Ui.MakeButton(page.transform, "ItQtyStep" + i,
                    (delta > 0 ? "+" : "") + delta, 16f, Palette.BtnIdle, Palette.Sub,
                    () => AddOrdQty(delta), Ui.AlignCenter);
                if (b != null) b.Place(182f + i * (itStepW + 10f), formY + 82f, itStepW, 30f);
            }

            TextMeshProUGUI priceLab = Ui.MakeText(page.transform, "ItPriceLab", "价格", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (priceLab != null) Ui.Place(priceLab.gameObject, 14f, formY + 121f, 44f, 24f);
            _itPriceInput = Ui.MakeInput(page.transform, "ItPrice", "元", 17f,
                Palette.SlotBg, Palette.Title, Palette.Muted, OnOrdPriceChanged);
            if (_itPriceInput != null) Ui.Place(_itPriceInput.gameObject, 62f, formY + 116f, 110f, 30f);
            string[] pdiff = { "-0.10", "-0.01", "+0.01", "+0.10" };
            for (int i = 0; i < 4; i++)
            {
                int delta = i == 0 ? -10 : (i == 1 ? -1 : (i == 2 ? 1 : 10));
                UiButton b = Ui.MakeButton(page.transform, "ItPriceStep" + i, pdiff[i], 15f,
                    Palette.BtnIdle, Palette.Sub, () => AddOrdPrice(delta), Ui.AlignCenter);
                if (b != null) b.Place(182f + i * (itStepW + 10f), formY + 116f, itStepW, 30f);
            }

            UiButton place = Ui.MakeButton(page.transform, "ItPlace", "挂单（价格打到就自动成交）",
                FsCell, Palette.BtnGreen, Palette.Title, DoPlaceOrder, Ui.AlignCenter);
            if (place != null) place.Place(14f, formY + 150f, formW - 28f, 34f);

            // 资金 / 进度拆成两行：以前挤成一行，长到卡片外面去了
            _itAvail = Ui.MakeText(page.transform, "ItAvail", "", 14f, Palette.Muted,
                Ui.AlignLeft, false);
            if (_itAvail != null) Ui.Place(_itAvail.gameObject, 14f, formY + 186f, formW - 28f, 32f);

            // ── 右：挂单列表 ──────────────────────────────────────────
            Ui.MakeCard(page.transform, "ItCardList", listX, formY, listW, formH);
            CardTitle(page.transform, "ItListTitle", listX + 24f, formY + 10f, "当前挂单", FsBody);

            // 列：标的名 / 方向 / 股数 / 挂单价 / ✕。列位置按卡片宽的百分比走，
            // 宽屏上会自动散开，不会挤在左边一小撮。
            string[] cols = { "标的", "方向", "股数", "挂单价" };
            float innerW = listW - 28f;
            float cellW = Mathf.Min(innerW * 0.26f, 240f);
            float[] colX =
            {
                listX + 14f,
                listX + 14f + innerW * 0.27f,
                listX + 14f + innerW * 0.39f,
                listX + 14f + innerW * 0.58f
            };
            float xBtnX = listX + listW - 50f;
            for (int c = 0; c < cols.Length; c++)
            {
                TextMeshProUGUI h = Ui.MakeText(page.transform, "ItCol" + c, cols[c], FsSmall,
                    Palette.Muted, Ui.AlignLeft, false);
                if (h != null) Ui.Place(h.gameObject, colX[c], formY + 38f, cellW, 20f);
            }

            for (int i = 0; i < MaxOrderRows; i++)
            {
                int row = i;
                float y = formY + 62f + i * 30f;
                GameObject line = Ui.New("ItOrd" + i, page.transform);
                if (line == null) continue;
                Ui.Place(line, 0f, 0f, ContentW, BodyH);
                _itOrdRow[i] = line;

                for (int c = 0; c < 4; c++)
                {
                    TextMeshProUGUI cell = Ui.MakeText(line.transform, "ItOrdCell" + i + "_" + c,
                        "-", 16f, Palette.CardBody, Ui.AlignLeft, false);
                    if (cell == null) continue;
                    Ui.Place(cell.gameObject, colX[c], y, cellW, 24f);
                    _itOrdCell[i * 4 + c] = cell;
                }
                UiButton x = Ui.MakeButton(line.transform, "ItOrdX" + i, "✕", 15f,
                    Palette.BtnRed, Palette.Title, () => CancelOrder(row), Ui.AlignCenter);
                if (x != null) x.Place(xBtnX, y - 2f, 36f, 26f);
                _itOrdCancel[i] = x;
                Ui.SetActive(line, false);
            }

            _itOrdEmpty = Ui.MakeText(page.transform, "ItOrdEmpty", "", FsSmall, Palette.Muted,
                Ui.AlignLeft, true);
            if (_itOrdEmpty != null) Ui.Place(_itOrdEmpty.gameObject, listX + 14f, formY + 62f, innerW - 40f, formH - 110f);

            _itOrdClear = Ui.MakeButton(page.transform, "ItOrdClear", "全部撤销", FsCell,
                Palette.BtnIdle, Palette.Sub, CancelOrders, Ui.AlignCenter);
            if (_itOrdClear != null) _itOrdClear.Place(listX + 14f, formY + formH - 34f, innerW, 30f);

            return page;
        }

        private static void RefreshIntra()
        {
            StockDef def = Current();
            if (def == null) return;
            if (_itDrop != null) _itDrop.Sync(_selectedId);

            for (int i = 0; i < 2; i++)
            {
                UiButton b = _itSideBtn[i];
                if (b == null) continue;
                bool on = i == _itSide;
                b.SetBg(on ? (i == 0 ? Palette.BtnBlue : Palette.BtnOrange) : Palette.BtnIdle);
                b.SetTextColor(on ? Palette.Title : Palette.Sub);
            }

            // 挂单价没填过就跟着实时价走，玩家一进来就有个合理起点
            if (_itPriceCents <= 0) _itPriceCents = StockEngine.LivePrice(def.Id);
            SyncOrdInputs();

            long live = StockEngine.LivePrice(def.Id);
            double chg = StockEngine.DayChangePercent(def.Id);
            double avg = StockIntraday.AveragePrice(def.Id);
            int board = StockEngine.BoardState(def.Id);
            bool demo = StockIntraday.DemoActive;
            if (_itLive != null)
            {
                // 演示路径是教程铺的假走势，读数得说清楚，别让玩家拿它当今天的行情
                _itLive.text = demo
                    ? "<color=#" + ColorHex(Palette.Cyan) + ">演示走势</color>"
                      + " <color=#" + ColorHex(Palette.Muted) + ">开店后换成今天的真实行情</color>"
                    : "实时 " + StockState.ToYuan(live).ToString("0.00")
                      + "　" + (StockIntraday.IsLive ? "盘中" : "已收工")
                      + " <color=#" + ColorHex(Palette.Change(chg)) + ">"
                      + Sign(chg) + chg.ToString("0.00") + "%</color>"
                      + "　均价 " + (avg > 0.0 ? avg.ToString("0.00") : "--")
                      + (board != 0
                          ? "　<color=#" + ColorHex(Palette.Change(board)) + ">"
                            + (board > 0 ? "涨停" : "跌停") + "</color>"
                          : string.Empty);
            }

            int sellable = StockState.GetPosition(def.Id) - StockOrders.FrozenShares(def.Id);
            if (_itAvail != null)
            {
                _itAvail.text = "可用 " + Money(StockEngine.PoolYuan()) + " 元"
                    + "　冻结 " + Money(StockState.ToYuan(StockOrders.TotalFrozen())) + " 元"
                    + "　可卖 " + Mathf.Max(0, sellable) + " 股"
                    + "\n" + (demo
                        ? "演示数据　今天还没有真实客人，先给你看一条示例走势"
                        : "进度 " + StockIntraday.Step + "/" + StockIntraday.Total + " 段"
                          + "　今天已接待 " + StockIntraday.Served + " 位客人");
            }

            RefreshOrdRows();
        }

        /// <summary>挂单列表：把队列里的单子铺成行，多出来的行收起来。</summary>
        private static void RefreshOrdRows()
        {
            List<PendingOrder> list = StockOrders.Orders;
            int n = list != null ? list.Count : 0;

            for (int i = 0; i < MaxOrderRows; i++)
            {
                bool on = i < n;
                if (_itOrdRow[i] != null) Ui.SetActive(_itOrdRow[i], on);
                if (!on) continue;

                PendingOrder o = list[i];
                StockDef d = o != null ? StockDefs.Get(o.DefId) : null;
                SetOrdCell(i, 0, d != null ? d.Name : "?");
                SetOrdCell(i, 1, o.Side == 0 ? "买入" : "卖出");
                SetOrdCell(i, 2, o.Shares + " 股");
                SetOrdCell(i, 3, StockState.ToYuan(o.Price).ToString("0.00") + " 元");
                TextMeshProUGUI side = _itOrdCell[i * 4 + 1];
                if (side != null) side.color = o.Side == 0 ? Palette.Up : Palette.Down;
                TextMeshProUGUI price = _itOrdCell[i * 4 + 3];
                if (price != null) price.color = Palette.Gold;
            }

            bool empty = n == 0;
            if (_itOrdEmpty != null)
            {
                Ui.SetActive(_itOrdEmpty.gameObject, empty);
                if (empty)
                {
                    // 挂单是收工自动撤的，说清楚免得玩家以为是自己丢的
                    _itOrdEmpty.text = StockIntraday.IsLive
                        ? "还没有挂单。填好方向和价格挂上去，价格打到就自动成交；\n"
                          + "收工时没成交的挂单会自动撤销，冻结的钱和股数一起还回来。"
                        : "现在不是营业时间（商店没开门），开店后才能挂单。";
                }
            }
            if (_itOrdClear != null) _itOrdClear.SetInteractable(!empty);
        }

        private static void SetOrdCell(int row, int col, string text)
        {
            TextMeshProUGUI t = _itOrdCell[row * 4 + col];
            if (t != null) t.text = text;
        }

        /// <summary>把 _itQty / _itPriceCents 回填进输入框（步进按钮、换标的之后调用）。</summary>
        private static void SyncOrdInputs()
        {
            _itGuard = true;
            try
            {
                // 正在打字就别回填，否则「12.3」会被当场改写成「12.30」，很难打
                if (_itQtyInput != null && !IsEditing(_itQtyInput))
                {
                    string q = _itQty.ToString();
                    try { _itQtyInput.SetTextWithoutNotify(q); }
                    catch { try { _itQtyInput.text = q; } catch { } }
                }
                if (_itPriceInput != null && _itPriceCents > 0 && !IsEditing(_itPriceInput))
                {
                    string p = StockState.ToYuan(_itPriceCents).ToString("0.00");
                    try { _itPriceInput.SetTextWithoutNotify(p); }
                    catch { try { _itPriceInput.text = p; } catch { } }
                }
            }
            catch { }
            _itGuard = false;
        }

        private static bool IsEditing(TMP_InputField input)
        {
            try { return input != null && input.isFocused; } catch { return false; }
        }

        /// <summary>
        /// 分时图。白线=实时价，黄线=当日均价，灰虚线=昨收，柱子=成交量。
        /// 这里顺手记下「画的是哪个状态」，Tick 靠它判断要不要重画。
        /// </summary>
        private static void DrawIntraChart()
        {
            _itDrawnStep = StockIntraday.Step;
            _itDrawnDay = StockState.Today;
            _itDrawnLive = StockIntraday.IsLive;
            _itDrawnDemo = StockIntraday.DemoActive;
            try
            {
                StockDef def = Current();
                _itDrawnDemand = StockEconomy.Factor(def != null ? def.Id : string.Empty);
                if (def == null || _itChart == null) return;

                long live = StockEngine.LivePrice(def.Id);
                StockChart.DrawIntraday(_itChart, ContentW - 48f, IntraChartH,
                    StockIntraday.VisiblePath(def.Id),
                    StockIntraday.VisibleVolume(def.Id),
                    StockIntraday.PrevClose(def.Id, StockState.ToYuan(live)),
                    StockIntraday.Total,
                    new[] { "开盘", "¼", "半场", "¾", "现在" });
            }
            catch (Exception ex)
            {
                Core.Log.Msg("绘制分时图失败：" + ex);
            }
        }

        /// <summary>
        /// 鼠标压在分时图上：一条竖线 + 一个点 + 一张小标签，报出那一段的价格。
        ///
        /// 坐标口径跟 StockChart.DrawIntraday 共用同一套（同一组边距常量、同一个量程算法），
        /// 所以竖线一定落在折线的点上，不会飘。悬停层是图槽的兄弟节点、不进图里 ——
        /// 图每推进一段就整个重画，画进去的话当场就被清掉了。
        /// </summary>
        private static void UpdateIntraHover()
        {
            try
            {
                if (_itHover == null || _itChart == null)
                {
                    SetIntraHoverVisible(false);
                    return;
                }
                StockDef def = Current();
                double[] path = def != null ? StockIntraday.VisiblePath(def.Id) : null;
                // 标的列表展开时别插脚：那会儿玩家是在挑股票，不是在读图
                if (def == null || path == null || path.Length < 2 || (_itDrop != null && _itDrop.Open)
                    || !Core.ChartInfoPref)
                {
                    SetIntraHoverVisible(false);
                    return;
                }

                // 摄像机传 null：画布是 Overlay，屏幕坐标就是世界坐标
                if (!RectTransformUtility.RectangleContainsScreenPoint(_itChart, Input.mousePosition, null))
                {
                    SetIntraHoverVisible(false);
                    return;
                }
                Vector2 local;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_itChart,
                    Input.mousePosition, null, out local))
                {
                    SetIntraHoverVisible(false);
                    return;
                }

                float cw = ContentW - 48f;
                float chh = IntraChartH;
                // 图槽轴心在左上角：local.x 就是从左边量，local.y 取负就是从上面量
                float mx = local.x;
                float padL = StockChart.IntraPadL, padT = StockChart.IntraPadT;
                float plotW = cw - padL - StockChart.IntraPadR;
                float plotH = chh - padT - StockChart.IntraPadB;
                if (plotW <= 10f || plotH <= 24f)
                {
                    SetIntraHoverVisible(false);
                    return;
                }

                float priceH = plotH - plotH * StockChart.IntraVolRatio - 6f;
                float slot = plotW / StockIntraday.Total;
                int idx = (int)Mathf.Floor((mx - padL) / slot);
                if (idx < 0) idx = 0;
                if (idx > path.Length - 1) idx = path.Length - 1;

                long live = StockEngine.LivePrice(def.Id);
                double prevClose = StockIntraday.PrevClose(def.Id, StockState.ToYuan(live));
                double min, max;
                StockChart.IntraRange(path, prevClose, out min, out max);
                double span = max - min;

                double price = path[idx];
                double pct = prevClose > 0.0 ? (price - prevClose) / prevClose * 100.0 : 0.0;
                double[] vol = StockIntraday.VisibleVolume(def.Id);
                double segVol = (vol != null && idx < vol.Length) ? vol[idx] : 0.0;

                // 同一段、同一个数就不重排版：每帧写一次 RectTransform，画布会被一直标脏
                string key = idx + "|" + price.ToString("0.0000") + "|"
                    + prevClose.ToString("0.0000") + "|" + segVol.ToString("0");
                if (_itHoverIdx == idx && _itHoverKey == key && _itHoverTag != null) return;
                _itHoverIdx = idx;
                _itHoverKey = key;

                float px = padL + (idx + 0.5f) * slot;
                float py = padT + (float)((1.0 - (price - min) / span) * priceH);

                SetIntraHoverVisible(true);
                if (_itHoverLine != null) Ui.Place(_itHoverLine.gameObject, px, padT, 1f, priceH);
                if (_itHoverDot != null) Ui.Place(_itHoverDot.gameObject, px - 4f, py - 4f, 8f, 8f);

                if (_itHoverTag != null)
                {
                    // 标签默认挂在竖线右边，右边放不下就翻到左边，再夹回图里
                    float tx = px + 12f;
                    if (tx + HoverTagW > cw - 4f) tx = px - HoverTagW - 12f;
                    tx = Mathf.Clamp(tx, 4f, Mathf.Max(4f, cw - HoverTagW - 4f));
                    float ty = Mathf.Clamp(py - HoverTagH * 0.5f, 4f,
                        Mathf.Max(4f, chh - HoverTagH - 4f));
                    Ui.Place(_itHoverTag, tx, ty, HoverTagW, HoverTagH);
                }
                if (_itHoverText != null)
                {
                    _itHoverText.text = "<color=" + Palette.HexOf(Palette.Muted) + ">第 "
                        + (idx + 1) + " / " + StockIntraday.Total + " 段"
                        + (StockIntraday.DemoActive ? "　演示" : "") + "</color>\n"
                        + "<size=16><color=" + Palette.HexOf(Palette.Title) + ">"
                        + price.ToString("0.00") + " 元</color></size>  <color="
                        + Palette.HexOf(Palette.Change(pct)) + ">" + Sign(pct)
                        + pct.ToString("0.00") + "%</color>\n"
                        + "<color=" + Palette.HexOf(Palette.Sub) + ">均 "
                        + AvgTo(path, vol, idx).ToString("0.00") + "　量 " + Amt(segVol)
                        + "</color>";
                }
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[盘中] 悬停读数失败：" + ex.Message);
                SetIntraHoverVisible(false);
            }
        }

        /// <summary>
        /// 悬停层的显隐。收起时顺手把指纹清掉，下次压上去才会重新排版。
        /// 这里不再拿指纹当「本来就是收着的」的凭据：指纹和实际显隐一旦对不上
        /// （教程撤演示数据、切页、重建画布都有可能），那层旧读数就会留在图上。
        /// 物件用 SetActive 兜底，它自己会判断状态没变就不动，开销可以忽略。
        /// </summary>
        private static void SetIntraHoverVisible(bool on)
        {
            if (!on)
            {
                _itHoverIdx = -1;
                _itHoverKey = "";
            }
            Ui.SetActive(_itHover != null ? _itHover.gameObject : null, on);
            if (_itHoverTag != null) Ui.SetActive(_itHoverTag, on);
        }

        /// <summary>第 idx 段为止的累计均价，口径跟图上那条黄线一致。</summary>
        private static double AvgTo(double[] path, double[] vol, int idx)
        {
            if (path == null || path.Length == 0) return 0.0;
            if (idx <= 0) return path[0];
            double sum = 0.0, weight = 0.0;
            for (int i = 0; i < idx && i + 1 < path.Length; i++)
            {
                double w = (vol != null && i < vol.Length && vol[i] > 0.0) ? vol[i] : 1.0;
                sum += path[i + 1] * w;
                weight += w;
            }
            return weight > 0.0 ? sum / weight : path[idx];
        }

        /// <summary>成交量数字太大，缩写成「1.2万」省地方。</summary>
        private static string Amt(double v)
        {
            if (v >= 10000.0) return (v / 10000.0).ToString("0.0") + "万";
            return Math.Round(v).ToString("0");
        }

        // ══════════════════════════════════════════════════════════════
        //  图表悬停读数：技术分析主图/两个副图、交易页小图与量图、资产走势
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 在一个图槽上盖一层读数（竖线 + 小标签）。位置尺寸照抄图槽，调用方不用再算一遍布局。
        /// sub = true 用副图边距，否则用主图边距 —— 反查鼠标压在哪一格必须跟画图共用同一套坐标。
        /// </summary>
        private static ChartTip MakeTip(RectTransform host, string name, bool sub, string title)
        {
            if (host == null) return null;
            try
            {
                GameObject layer = Ui.New(name + "Tip", host.parent);
                if (layer == null) return null;
                // 图槽是「左上角定位」摆出来的：anchoredPosition.x 从左边量，取负的 y 从上面量
                Ui.Place(layer, host.anchoredPosition.x, -host.anchoredPosition.y,
                    host.sizeDelta.x, host.sizeDelta.y);

                ChartTip tip = new ChartTip();
                tip.Host = host;
                tip.Title = title;
                float l, r, t, b;
                if (sub) StockChart.SubPads(out l, out r, out t, out b);
                else StockChart.MainPads(out l, out r, out t, out b);
                tip.PadL = l;
                tip.PadT = t;
                tip.PlotW = Mathf.Max(0f, host.sizeDelta.x - l - r);
                tip.PlotH = Mathf.Max(0f, host.sizeDelta.y - t - b);

                Image line = Ui.MakeImage(layer.transform, name + "TipLine",
                    new Color(Palette.Cyan.r, Palette.Cyan.g, Palette.Cyan.b, 0.45f), false);
                if (line != null) tip.Line = Ui.Rect(line.gameObject);

                GameObject tag = Ui.New(name + "TipTag", layer.transform);
                if (tag != null)
                {
                    // 描边 + 半透明纸面，跟面板里的卡片同一套配色：读数只是盖在图上的一层
                    // 注记，不该用引导气泡那种撞色，也不该把后面的行情糊死
                    Image rim = Ui.MakeSliced(tag.transform, name + "TipRim", Ui.Card(),
                        Palette.A(Palette.CardRim, 0.85f), false);
                    if (rim != null) Ui.Stretch(rim.gameObject, 0f);
                    Image face = Ui.MakeSliced(tag.transform, name + "TipFace", Ui.Card(),
                        Palette.A(Palette.CardBg, TipAlpha), false);
                    if (face != null) Ui.Stretch(face.gameObject, 1f);
                    tip.Text = Ui.MakeText(tag.transform, name + "TipText", "", TipFont,
                        Palette.Sub, Ui.AlignTopLeft, false);
                    if (tip.Text != null)
                    {
                        try { tip.Text.lineSpacing = 1f; } catch { }
                    }
                    tip.Tag = tag;
                }
                tip.Hide();
                _tips.Add(tip);
                return tip;
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[图表] 建读数层失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 每帧刷一遍全部图表读数。分时图走自己那一套（跟图一起推进），其余几张走 _tips。
        ///
        /// 「不在图上就收起来」是这里的铁律：从前是「不在这一页就跳过」，跳过的那些
        /// 读数会一直挂在自己那一页上，等玩家转回来就是一层旧数字浮在新图上
        /// （见用户反馈「过完新手教程再回来，之前的显示还绘制在上面」）。
        /// </summary>
        private static void UpdateReadouts()
        {
            try
            {
                // 右键开关：只有指针真的压在某张图上才认，免得把别处的右键也吃掉
                if (Input.GetMouseButtonDown(1) && OverAnyChart())
                {
                    ToggleChartInfo();
                    return;
                }
                if (!Core.ChartInfoPref) { HideReadouts(); return; }

                // 分时图：只在这一页才算数，别页一律收起
                if (_page == PageIntra) UpdateIntraHover();
                else SetIntraHoverVisible(false);

                for (int i = 0; i < _tips.Count; i++)
                {
                    ChartTip t = _tips[i];
                    if (t == null) continue;
                    // 宿主没在图上的（别页的图、被拆掉的图）一律收起来，绝不「留着不管」
                    if (t.Host == null || !t.Host.gameObject.activeInHierarchy) { t.Hide(); continue; }
                    TipFollow(t);
                }
            }
            catch (Exception ex) { Core.Log.Warning("[图表] 读数刷新失败：" + ex.Message); }
        }

        /// <summary>把所有读数收起来（分时 + 通用）。切页、关面板、撤演示路径时叫它。</summary>
        public static void HideReadouts()
        {
            try
            {
                SetIntraHoverVisible(false);
                for (int i = 0; i < _tips.Count; i++) if (_tips[i] != null) _tips[i].Hide();
            }
            catch { }
        }

        private static bool OverAnyChart()
        {
            // 分时图走的是自己那套读数层，不在 _tips 里，得单独看一眼，
            // 否则玩家在分时图上右键会「没反应」
            if (_itChart != null && _itChart.gameObject.activeInHierarchy
                && RectTransformUtility.RectangleContainsScreenPoint(_itChart, Input.mousePosition, null))
                return true;
            for (int i = 0; i < _tips.Count; i++)
            {
                ChartTip t = _tips[i];
                if (t == null || t.Host == null || !t.Host.gameObject.activeInHierarchy) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(t.Host, Input.mousePosition, null)) return true;
            }
            return false;
        }

        private static void ToggleChartInfo()
        {
            bool on = !Core.ChartInfoPref;
            Core.ChartInfoPref = on;
            LogOp("图表读数 → " + (on ? "开启" : "关闭"));
            RefreshChartInfoBtn();
            SetStatus(on ? "图表读数已开启：鼠标压在图上就报数（图上右键、或底栏「读数」都能切）。"
                         : "图表读数已关闭。底栏「读数」按钮、或在图上右键一次，都能打开。", false);
            if (!on)
            {
                for (int i = 0; i < _tips.Count; i++) if (_tips[i] != null) _tips[i].Hide();
                SetIntraHoverVisible(false);
            }
        }

        /// <summary>一张图上有多少格：以最长的那一列为准。</summary>
        private static int TipCount(ChartTip t)
        {
            int n = 0;
            if (t.Labels != null && t.Labels.Length > n) n = t.Labels.Length;
            if (t.Bars != null && t.Bars.Count > n) n = t.Bars.Count;
            return n;
        }

        /// <summary>让读数跟着鼠标走。同一格、同一个数就不重排版（每帧写 RectTransform 会把画布一直标脏）。</summary>
        private static void TipFollow(ChartTip t)
        {
            int n = TipCount(t);
            if (n < 2 || t.PlotW <= 10f || t.PlotH <= 10f) { t.Hide(); return; }
            // 标的下拉列表展开时别插脚：那会儿玩家是在挑股票，不是在读图
            if ((_itDrop != null && _itDrop.Open) || (_tcDrop != null && _tcDrop.Open)) { t.Hide(); return; }
            if (!RectTransformUtility.RectangleContainsScreenPoint(t.Host, Input.mousePosition, null)) { t.Hide(); return; }
            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(t.Host,
                Input.mousePosition, null, out local)) { t.Hide(); return; }

            float slot = t.PlotW / n;
            int idx = (int)Mathf.Floor((local.x - t.PadL) / slot);
            if (idx < 0) idx = 0;
            if (idx > n - 1) idx = n - 1;

            // 指纹 = 哪一格 + 图的版本号 + 标签确实是亮着的。三样都成立才复用上次的排版，
            // 连富文本都不用拼（拼字符串要分配、要查调色板，每帧一次是白烧的）。
            // 「亮着」这一条不能省：指纹和显隐一旦对不上（别处收过、重建过图层），
            // 指针明明压在图上却不报数 —— 见用户反馈「这个界面有时候拖进去不显示详细信息」。
            if (t.Idx == idx && t.KeyVer == t.Ver && t.Tag != null && t.Tag.activeSelf) return;
            t.Idx = idx;
            t.KeyVer = t.Ver;
            string body = TipBody(t, idx);

            t.Show();
            float x = t.PadL + (idx + 0.5f) * slot;
            if (t.Line != null) Ui.Place(t.Line.gameObject, x, t.PadT, 1f, t.PlotH);

            if (t.Tag == null) return;
            int lines = 1;
            for (int i = 0; i < body.Length; i++) if (body[i] == '\n') lines++;
            // 顶端/底端各留 6，行间留 1：写小了数字会被裁，写大了标签白白多占一块行情
            float th = lines * (TipLineH + 1f) + 12f;
            float hostW = t.Host.sizeDelta.x, hostH = t.Host.sizeDelta.y;
            // 标签默认挂竖线右边，右边放不下就翻到左边，再夹回图里
            float tx = x + 12f;
            if (tx + TipW > hostW - 4f) tx = x - TipW - 12f;
            tx = Mathf.Clamp(tx, 4f, Mathf.Max(4f, hostW - TipW - 4f));
            float ty = Mathf.Clamp(local.y - th * 0.5f, 4f, Mathf.Max(4f, hostH - th - 4f));
            Ui.Place(t.Tag, tx, ty, TipW, th);
            if (t.Text != null)
            {
                Ui.Place(t.Text.gameObject, 8f, 5f, TipW - 16f, th - 10f);
                t.Text.text = body;
            }
        }

        /// <summary>
        /// 鼠标压在第 idx 格上报什么数。顺序固定：横轴标签 → 收盘/涨跌 → 开高低量 → 各条线 → 附加柱。
        /// 副图（量图、指标图）不报四价，只报自己那一条，免得标签又高又杂。
        /// </summary>
        private static string TipBody(ChartTip t, int idx)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(128);
            string label = (t.Labels != null && idx < t.Labels.Length) ? t.Labels[idx] : ("第 " + (idx + 1) + " 格");
            sb.Append("<color=").Append(Palette.HexOf(Palette.Muted)).Append('>').Append(label);
            if (!string.IsNullOrEmpty(t.Title)) sb.Append("　").Append(t.Title);
            sb.Append("</color>");

            Bar b = default(Bar);
            bool hasBar = t.Bars != null && idx < t.Bars.Count;
            if (hasBar) b = t.Bars[idx];
            if (hasBar && t.Prices)
            {
                double prev = idx > 0 ? t.Bars[idx - 1].Close : b.Open;
                double pct = prev > 0.0 ? (b.Close - prev) / prev * 100.0 : 0.0;
                sb.Append("\n<size=15><color=").Append(Palette.HexOf(Palette.Title)).Append('>')
                  .Append(b.Close.ToString("0.00")).Append(" 元</color></size>  <color=")
                  .Append(Palette.HexOf(Palette.Change(pct))).Append('>')
                  .Append(Sign(pct)).Append(pct.ToString("0.00")).Append("%</color>");
                sb.Append("\n<color=").Append(Palette.HexOf(Palette.Sub)).Append(">开 ")
                  .Append(b.Open.ToString("0.00")).Append("　高 ").Append(b.High.ToString("0.00")).Append("</color>");
                sb.Append("\n<color=").Append(Palette.HexOf(Palette.Sub)).Append(">低 ")
                  .Append(b.Low.ToString("0.00")).Append("　量 ").Append(Amt(b.Volume)).Append("</color>");
            }
            else if (hasBar)
            {
                sb.Append("\n<color=").Append(Palette.HexOf(Palette.Sub)).Append(">量 ")
                  .Append(Amt(b.Volume)).Append("</color>");
            }

            if (t.Lines != null)
            {
                int per = 0;   // 一行放两条，第三条换行
                for (int s = 0; s < t.Lines.Count; s++)
                {
                    ChartSeries cs = t.Lines[s];
                    if (cs == null || cs.Values == null || idx >= cs.Values.Count) continue;
                    double v = cs.Values[idx];
                    if (double.IsNaN(v)) continue;
                    if (per == 0) sb.Append("\n<color=").Append(Palette.HexOf(Palette.Sub)).Append('>');
                    else sb.Append("　");
                    sb.Append(string.IsNullOrEmpty(cs.Label) ? "值" : cs.Label).Append(' ').Append(Num(v));
                    per++;
                    if (per == 2) { sb.Append("</color>"); per = 0; }
                }
                if (per > 0) sb.Append("</color>");
            }

            if (t.Extra != null && idx < t.Extra.Length && !double.IsNaN(t.Extra[idx]))
            {
                double v = t.Extra[idx];
                sb.Append("\n<color=").Append(Palette.HexOf(v >= 0 ? Palette.Up : Palette.Down)).Append('>')
                  .Append(string.IsNullOrEmpty(t.ExtraName) ? "柱" : t.ExtraName).Append(' ')
                  .Append(Num(v)).Append("</color>");
            }

            // 这里原来还跟一行「右键 关闭读数」：开关的操作和状态已经摆在底栏那枚
            // 「读数」按钮上了，标签本身少一行就少挡一截行情（见问题截图/字有点大了）
            return sb.ToString();
        }

        /// <summary>
        /// 把「这张图上画了什么」塞进读数层。重画时顺手调用：图上换了数据，
        /// 读数还报老数就荒唐了。指纹一并清掉，逼下一帧重排一次。
        /// </summary>
        private static void TipData(ChartTip t, string title, string[] labels, List<Bar> bars,
            bool prices, List<ChartSeries> lines, double[] extra, string extraName)
        {
            if (t == null) return;
            t.Title = title;
            t.Labels = labels;
            t.Bars = bars;
            t.Prices = prices;
            t.Lines = lines;
            t.Extra = extra;
            t.ExtraName = extraName;
            t.Idx = -1;
            t.KeyVer = -1;
            // 版本号 +1：读数看到「图重画过了」才会重新拼文字。
            // 不这么做的话，每帧都要拼一遍富文本才敢判断内容有没有变，白烧 CPU。
            t.Ver++;
        }

        /// <summary>指标数字：过万缩写成「万」，其余两位小数。</summary>
        private static string Num(double v)
        {
            if (Math.Abs(v) >= 10000.0) return (v / 10000.0).ToString("0.0") + "万";
            return v.ToString("0.00");
        }

        private static void SetOrdSide(int side)
        {
            if (_itSide == side) return;
            _itSide = side;
            LogOp("盘中挂单方向 → " + (side == 0 ? "买入" : "卖出"));
            Refresh();
        }

        private static void AddOrdQty(int delta)
        {
            _itQty = Mathf.Clamp(_itQty + delta, 1, QtyMax);
            LogOp("盘中挂单股数 → " + _itQty + " 股");
            SyncOrdInputs();
            Refresh();
        }

        /// <summary>deltaCents 以「分」为单位，按钮上的文案按「元」写。</summary>
        private static void AddOrdPrice(int deltaCents)
        {
            long cur = _itPriceCents > 0 ? _itPriceCents : StockEngine.LivePrice(_selectedId);
            long next = cur + deltaCents;
            if (next < 1) next = 1;
            _itPriceCents = next;
            LogOp("盘中挂单价格 → " + StockState.ToYuan(next).ToString("0.00") + " 元");
            SyncOrdInputs();
            Refresh();
        }

        private static void OnOrdQtyChanged(string text)
        {
            if (_itGuard) return;
            int v;
            if (!TryParsePositive(text, out v)) return;
            v = Mathf.Clamp(v, 1, QtyMax);
            if (v == _itQty) return;
            _itQty = v;
            LogOp("盘中挂单股数 → " + _itQty + " 股");
            Refresh();
        }

        /// <summary>挂单价允许小数（12.3 / 12.34），所以不能复用只收整数的 TryParsePositive。</summary>
        private static void OnOrdPriceChanged(string text)
        {
            if (_itGuard) return;
            if (string.IsNullOrEmpty(text)) return;
            double v;
            if (!double.TryParse(text.Trim(), out v)) return;
            if (v <= 0.0) return;

            long cents = (long)Math.Round(v * StockDefs.PriceScale);
            if (cents < 1) cents = 1;
            if (cents == _itPriceCents) return;
            _itPriceCents = cents;
            LogOp("盘中挂单价格 → " + StockState.ToYuan(cents).ToString("0.00") + " 元");
            Refresh();
        }

        private static void DoPlaceOrder()
        {
            try
            {
                StockState.EnsureLoaded(PlayerStore.instance);
                StockDef def = Current();
                if (def == null) return;    // 没选中标的时点挂单按钮，别拿空引用去取现价
                long price = _itPriceCents > 0 ? _itPriceCents : StockEngine.LivePrice(def.Id);
                LogOp("挂单 " + def.Name + " " + (_itSide == 0 ? "买入" : "卖出") + " " + _itQty
                    + " 股 @ " + StockState.ToYuan(price).ToString("0.00") + " 元");

                string error = StockOrders.Place(def.Id, _itSide, _itQty, price);
                if (error == null)
                {
                    string done = "已挂上：" + def.Name + " " + (_itSide == 0 ? "买入" : "卖出")
                        + " " + _itQty + " 股 @ " + StockState.ToYuan(price).ToString("0.00")
                        + " 元，价格打到就自动成交。";
                    Core.Log.Msg("[结果] " + done);
                    SetStatus(done, false);
                }
                else
                {
                    Core.Log.Msg("[结果] 挂单失败：" + error);
                    SetStatus(error, true);
                }
            }
            catch (Exception ex)
            {
                SetStatus("挂单失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void CancelOrder(int index)
        {
            LogOp("撤销第 " + (index + 1) + " 笔挂单");
            string error = StockOrders.Cancel(index);
            SetStatus(error == null ? "已撤销第 " + (index + 1) + " 笔挂单。" : error, error != null);
            Refresh();
        }

        private static void CancelOrders()
        {
            LogOp("撤销全部挂单");
            int n = StockOrders.CancelAll("手动撤销");
            SetStatus(n > 0 ? "已撤销 " + n + " 笔挂单，冻结的资金和股数都还回来了。" : "当前没有挂单。",
                false);
            Refresh();
        }

        // ── 杠杆交易 ──────────────────────────────────────────────────
        //  上半部分是两个账户的切换，下面左右分栏：
        //    左边（LvLeftW 宽）——账户实况 + 所有操作按钮，按「开户 → 借钱 → 交易 → 还款」的顺序排
        //    右边（吃掉剩下的宽）——大白话的玩法说明 + 风险提示，新手不用去翻别的资料
        //  两张卡的宽度和右栏两段文字的高度都跟着内容区走（见 Layout），
        //  右栏文字之所以要按高度切：「怎么玩」是折行文本，给少了会顶到「风险提示」上。
        private static GameObject BuildLever(Transform root)
        {
            GameObject page = NewPage(root, "PageLever");

            _lvTabReg = Ui.MakeButton(page.transform, "LvTabReg", "融资融券（正规）", FsBody,
                Palette.BtnBlue, Palette.Title, () => LvTab(false), Ui.AlignCenter);
            if (_lvTabReg != null) _lvTabReg.Place(0f, 0f, 300f, 42f);
            _lvTabShadow = Ui.MakeButton(page.transform, "LvTabShadow", "场外配资（野路子）", FsBody,
                Palette.BtnIdle, Palette.Sub, () => LvTab(true), Ui.AlignCenter);
            if (_lvTabShadow != null) _lvTabShadow.Place(310f, 0f, 300f, 42f);

            float cardY = 52f;
            float cardH = BodyH - cardY;
            float lw = LvLeftW;
            float rx = lw + 16f;
            float rw = ContentW - rx;
            Ui.MakeCard(page.transform, "LvCardL", 0f, cardY, lw, cardH);
            Ui.MakeCard(page.transform, "LvCardR", rx, cardY, rw, cardH);

            _lvTitle = Ui.MakeText(page.transform, "LvTitle", "", FsCardTitle, Palette.Title,
                Ui.AlignLeft, false);
            if (_lvTitle != null) Ui.Place(_lvTitle.gameObject, 24f, 62f, 176f, 30f);

            // 杠杆档位：融资融券只有 2 倍一档，场外配资有 3/5/8 三档
            for (int i = 0; i < _lvLevels.Length; i++)
            {
                int idx = i;
                UiButton b = Ui.MakeButton(page.transform, "LvLevel" + i, "", 16f, Palette.BtnIdle,
                    Palette.Sub, () => LvSetLevel(idx), Ui.AlignCenter);
                if (b == null) continue;
                b.Place(206f + i * 72f, 62f, 68f, 28f);
                _lvLevels[i] = b;
            }

            // 融券标的：和【交易下单】共用同一个「当前选中标的」，这里也能左右翻
            TextMeshProUGUI pickLab = Ui.MakeText(page.transform, "LvPickLab", "融券标的", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (pickLab != null) Ui.Place(pickLab.gameObject, 24f, 96f, 70f, 24f);
            _lvPickPrev = Ui.MakeButton(page.transform, "LvPickPrev", "◀", 15f, Palette.BtnIdle,
                Palette.Sub, () => TechStep(-1), Ui.AlignCenter);
            if (_lvPickPrev != null) _lvPickPrev.Place(96f, 94f, 32f, 26f);
            _lvPickName = Ui.MakeText(page.transform, "LvPickName", "", 17f, Palette.Cyan,
                Ui.AlignCenter, false);
            if (_lvPickName != null) Ui.Place(_lvPickName.gameObject, 132f, 96f, 174f, 24f);
            _lvPickNext = Ui.MakeButton(page.transform, "LvPickNext", "▶", 15f, Palette.BtnIdle,
                Palette.Sub, () => TechStep(1), Ui.AlignCenter);
            if (_lvPickNext != null) _lvPickNext.Place(310f, 94f, 32f, 26f);

            Ui.MakeSlot(page.transform, "LvRatioSlot", 24f, 124f, lw - 48f, 72f, Palette.SlotBg);
            TextMeshProUGUI ratioLab = Ui.MakeText(page.transform, "LvRatioLab",
                "担保比例　越高越安全", FsSmall, Palette.Muted, Ui.AlignLeft, false);
            if (ratioLab != null) Ui.Place(ratioLab.gameObject, 38f, 130f, 260f, 18f);
            _lvRatio = Ui.MakeText(page.transform, "LvRatio", "-", 34f, Palette.Title,
                Ui.AlignLeft, false);
            if (_lvRatio != null) Ui.Place(_lvRatio.gameObject, 38f, 150f, 200f, 42f);
            _lvRatioHint = Ui.MakeText(page.transform, "LvRatioHint", "", FsSmall, Palette.Muted,
                Ui.AlignRight, false);
            if (_lvRatioHint != null) Ui.Place(_lvRatioHint.gameObject, 200f, 162f, lw - 224f, 20f);

            // 明细区：两列排 3 行。原来 5 行单列一直垂到 348，最后一行「累计付息」
            // 和下面的金额输入行贴死了（见 问题截图），压成 3 行后中间还能塞下操作提示。
            _lvRows = Ui.MakeText(page.transform, "LvRows", "", 15f, Palette.CardBody,
                Ui.AlignTopLeft, true);
            if (_lvRows != null)
            {
                Ui.Place(_lvRows.gameObject, 24f, 204f, lw - 48f, 72f);
                Ui.Truncate(_lvRows);
            }

            // 下面这一串从卡片底往上倒排：BodyH 小的时候整体上移，
            // 不会像写死的 446 / 482 那样被卡片底边切掉、也不会压住操作提示。
            float bot = cardY + cardH;
            float actY2 = bot - 46f;
            float actY1 = actY2 - 36f;
            float stpY2 = actY1 - 36f;
            float stpY1 = stpY2 - 34f;
            float inpY = stpY1 - 34f;

            // 输入行：金额（借钱 / 还款用）与股数（融券 / 买券用）
            TextMeshProUGUI amtLab = Ui.MakeText(page.transform, "LvAmtLab", "金额", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (amtLab != null) Ui.Place(amtLab.gameObject, 24f, inpY + 4f, 40f, 24f);
            _lvAmountInput = Ui.MakeInput(page.transform, "LvAmount", "元", 17f,
                Palette.SlotBg, Palette.Title, Palette.Muted, OnLvAmountChanged);
            if (_lvAmountInput != null) Ui.Place(_lvAmountInput.gameObject, 66f, inpY, 120f, 30f);
            TextMeshProUGUI yuan = Ui.MakeText(page.transform, "LvYuan", "元", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (yuan != null) Ui.Place(yuan.gameObject, 190f, inpY + 4f, 22f, 24f);

            TextMeshProUGUI shLab = Ui.MakeText(page.transform, "LvShLab", "股数", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (shLab != null) Ui.Place(shLab.gameObject, 214f, inpY + 4f, 40f, 24f);
            _lvShareInput = Ui.MakeInput(page.transform, "LvShares", "股", 17f,
                Palette.SlotBg, Palette.Title, Palette.Muted, OnLvSharesChanged);
            if (_lvShareInput != null) Ui.Place(_lvShareInput.gameObject, 256f, inpY, 120f, 30f);
            TextMeshProUGUI gu = Ui.MakeText(page.transform, "LvGu", "股", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (gu != null) Ui.Place(gu.gameObject, 380f, inpY + 4f, 22f, 24f);

            // 步进键摊满左卡（单个不超过 120），操作按钮按左卡宽三分
            string[] amtSteps = { "-100", "-10", "+10", "+100" };
            int[] amtDeltas = { -100, -10, 10, 100 };
            float amtStepW = Mathf.Min((lw - 48f - 30f) / 4f, 120f);
            for (int i = 0; i < 4; i++)
            {
                int d = amtDeltas[i];
                UiButton b = Ui.MakeButton(page.transform, "LvAmtStep" + i, amtSteps[i], 16f,
                    Palette.BtnIdle, Palette.Sub, () => AddLvAmount(d), Ui.AlignCenter);
                if (b != null) b.Place(24f + i * (amtStepW + 10f), stpY1, amtStepW, 30f);
            }

            string[] shSteps = { "-10", "-1", "+1", "+10" };
            int[] shDeltas = { -10, -1, 1, 10 };
            for (int i = 0; i < 4; i++)
            {
                int d = shDeltas[i];
                UiButton b = Ui.MakeButton(page.transform, "LvShStep" + i, shSteps[i], 16f,
                    Palette.BtnIdle, Palette.Sub, () => AddLvShares(d), Ui.AlignCenter);
                if (b != null) b.Place(24f + i * (amtStepW + 10f), stpY2, amtStepW, 30f);
            }

            string[] acts = { "开户", "融资借钱", "还款", "融券卖出", "买券还券", "一键还清" };
            Action[] handlers = { LvOpen, LvBorrow, LvRepay, LvShort, LvCover, LvSettle };
            Color[] colors =
            {
                Palette.BtnGreen, Palette.BtnBlue, Palette.BtnIdle,
                Palette.BtnOrange, Palette.BtnBlue, Palette.BtnRed
            };
            float actW = Mathf.Min((lw - 48f - 24f) / 3f, 220f);
            for (int i = 0; i < acts.Length; i++)
            {
                float x = 24f + (i % 3) * (actW + 12f);
                float y = (i / 3) == 0 ? actY1 : actY2;
                UiButton b = Ui.MakeButton(page.transform, "LvAct" + i, acts[i], 16f,
                    colors[i], Palette.Title, handlers[i], Ui.AlignCenter);
                if (b == null) continue;
                b.Place(x, y, actW, 32f);
                switch (i)
                {
                    case 0: _lvOpen = b; break;
                    case 1: _lvBorrow = b; break;
                    case 2: _lvRepay = b; break;
                    case 3: _lvShort = b; break;
                    case 4: _lvCover = b; break;
                    default: _lvSettle = b; break;
                }
            }

            // 操作提示夹在明细行和输入行中间的空档里（原来是钉在卡片底往上 38，
            // 正好压在「一键还清」那一行按钮上）
            _lvNotice = Ui.MakeText(page.transform, "LvNotice", "", 15f, Palette.Gold,
                Ui.AlignLeft, false);
            if (_lvNotice != null)
                Ui.Place(_lvNotice.gameObject, 24f, inpY - 26f, lw - 48f, 18f);

            // ── 右栏：怎么玩 + 风险 ────────────────────────────────────
            // 「怎么玩」是折行文本，高度必须按右卡实高切：给少了 TMP 会在 wrap 模式下
            // 越过矩形继续往下画，把「风险提示」整块压住（见 问题截图）。
            float playH = Mathf.Round(cardH * 0.55f);
            float riskY = 98f + playH + 42f;
            TextMeshProUGUI playTitle = Ui.MakeText(page.transform, "LvPlayTitle",
                "怎么玩（大白话）", FsCardTitle, Palette.Title, Ui.AlignLeft, false);
            if (playTitle != null) Ui.Place(playTitle.gameObject, rx + 24f, 62f, 300f, 30f);
            _lvPlay = Ui.MakeText(page.transform, "LvPlay", "", 16f, Palette.CardBody,
                Ui.AlignTopLeft, true);
            if (_lvPlay != null) Ui.Place(_lvPlay.gameObject, rx + 24f, 98f, rw - 48f, playH);

            TextMeshProUGUI riskTitle = Ui.MakeText(page.transform, "LvRiskTitle",
                "风险提示", FsCardTitle, Palette.Danger, Ui.AlignLeft, false);
            if (riskTitle != null) Ui.Place(riskTitle.gameObject, rx + 24f, riskY - 34f, 300f, 30f);
            _lvRisk = Ui.MakeText(page.transform, "LvRisk", "", 16f, Palette.CardBody,
                Ui.AlignTopLeft, true);
            if (_lvRisk != null)
                Ui.Place(_lvRisk.gameObject, rx + 24f, riskY, rw - 48f, cardY + cardH - 8f - riskY);
            return page;
        }

        // ── 资产走势 ──────────────────────────────────────────────────

        private static GameObject BuildAsset(Transform root)
        {
            GameObject page = NewPage(root, "PageAsset");
            Ui.MakeCard(page.transform, "ACard", 0f, 0f, ContentW, BodyH);
            CardTitle(page.transform, "ATitle", 24f, 12f, "资产走势", FsPageTitle);

            string legend = "<color=#9EC7DE>■</color>总资产　<color=#45C08A>■</color>可用资金　"
                + "<color=#FFC24A>■</color>持仓市值";
            TextMeshProUGUI lg = Ui.MakeText(page.transform, "ALegend", legend, FsSmall,
                Palette.Muted, Ui.AlignRight, false);
            if (lg != null) Ui.Place(lg.gameObject, ContentW - 24f - 340f, 22f, 340f, 20f);

            TextMeshProUGUI unit = Ui.MakeText(page.transform, "AUnit", "纵轴：元", FsSmall,
                Palette.Muted, Ui.AlignRight, false);
            if (unit != null) Ui.Place(unit.gameObject, ContentW - 24f - 480f, 22f, 120f, 20f);

            Image chart = Ui.MakeSlot(page.transform, "ASlot", 24f, 56f, ContentW - 48f, BodyH - 80f, Palette.SlotBg);
            if (chart != null) _aChart = Ui.Rect(chart.gameObject);
            _tipAsset = MakeTip(_aChart, "Asset", false, "资产");
            return page;
        }

        // ── 事件公告 ──────────────────────────────────────────────────

        private static GameObject BuildEvent(Transform root)
        {
            GameObject page = NewPage(root, "PageEvent");
            Ui.MakeCard(page.transform, "ECard", 0f, 0f, ContentW, BodyH);
            CardTitle(page.transform, "ETitle", 24f, 12f, "事件公告", FsPageTitle);

            _evInfo = Ui.MakeText(page.transform, "EInfo", "", FsSmall, Palette.Muted,
                Ui.AlignLeft, false);
            if (_evInfo != null) Ui.Place(_evInfo.gameObject, 24f, 50f, ContentW - 48f, 20f);

            // 周期进度条：走到头就是结算日，持仓那天会派息
            Ui.MakeSlot(page.transform, "ECycleBg", 24f, 76f, ContentW - 48f, 6f, Palette.SlotBg);
            _evCycleBar = Ui.MakeImage(page.transform, "ECycleFill", Palette.GoldSoft, false);
            if (_evCycleBar != null) Ui.Place(_evCycleBar.gameObject, 24f, 76f, ContentW - 48f, 6f);

            // 左右两栏：左边是游戏自己的空间站事件（真事），右边是模组的市场事件（行情模拟）。
            // 分成两栏而不是混成一列，是因为两者的来源和可信度根本不同 ——
            // 左边玩家在游戏日历里能对上，右边只是「行情故事」，混着放会误导。
            const float gap = 12f;
            float colW = Mathf.Floor((ContentW - 48f - gap) / 2f);
            float colR = 24f + colW + gap;
            const float headY = 96f, headH = 22f, bodyTop = 122f;
            float area = BodyH - bodyTop - 10f;

            _evLHead = Ui.MakeText(page.transform, "ELHead", "空间站事件 · 游戏内实时",
                FsBody, Palette.Cyan, Ui.AlignLeft, false);
            if (_evLHead != null) Ui.Place(_evLHead.gameObject, 24f, headY, colW, headH);
            if (_evLHead != null) Ui.Truncate(_evLHead);

            _evRHead = Ui.MakeText(page.transform, "ERHead", "市场事件 · 行情模拟",
                FsBody, Palette.GoldSoft, Ui.AlignLeft, false);
            if (_evRHead != null) Ui.Place(_evRHead.gameObject, colR, headY, colW, headH);
            if (_evRHead != null) Ui.Truncate(_evRHead);

            // ── 左栏：空间站事件（从游戏里读来的）──────────────────────
            // 行数在矮屏上自动收，保证每行至少放得下「名字 + 影响标的」两行字；
            // 简介那行只有行高够时才摆，摆不下就干脆不建，免得跟上一行叠在一起。
            int geRows = GameEventRows;
            float geH = Mathf.Floor((area - (geRows - 1) * 6f) / geRows);
            while (geH < 62f && geRows > 2)
            {
                geRows--;
                geH = Mathf.Floor((area - (geRows - 1) * 6f) / geRows);
            }
            _geRows = geRows;
            bool geWide = geH >= 74f;
            for (int i = 0; i < geRows; i++)
            {
                GameObject row = Ui.New("GeRow" + i, page.transform);
                if (row == null) continue;
                Ui.Place(row, 24f, bodyTop + i * (geH + 6f), colW, geH);
                _geRow[i] = row;
                Transform rp = row.transform;

                Ui.MakeSlot(rp, "Bg", 0f, 0f, colW, geH, Palette.SlotBg);
                _geStrip[i] = Ui.MakeImage(rp, "Strip", Palette.Muted, false);
                if (_geStrip[i] != null) Ui.Place(_geStrip[i].gameObject, 0f, 0f, 5f, geH);

                _geName[i] = Ui.MakeText(rp, "Name", "", FsBody, Palette.Title,
                    Ui.AlignLeft, false);
                if (_geName[i] != null) Ui.Place(_geName[i].gameObject, 16f, 6f,
                    Mathf.Min(colW - 206f, 480f), 24f);
                if (_geName[i] != null) Ui.Truncate(_geName[i]);

                // 「预计 N 天后结束」——这是玩家要的：按游戏内持续时间推算的收尾时间
                _geLeft[i] = Ui.MakeText(rp, "Left", "", FsSmall, Palette.GoldSoft,
                    Ui.AlignRight, false);
                if (_geLeft[i] != null) Ui.Place(_geLeft[i].gameObject, colW - 196f, 6f, 180f, 24f);
                if (_geLeft[i] != null) Ui.Truncate(_geLeft[i]);

                _geTarget[i] = Ui.MakeText(rp, "Target", "", FsSmall, Palette.Sub,
                    Ui.AlignLeft, false);
                if (_geTarget[i] != null) Ui.Place(_geTarget[i].gameObject, 16f,
                    geWide ? geH - 41f : 30f, colW - 32f, 18f);
                if (_geTarget[i] != null) Ui.Truncate(_geTarget[i]);

                if (geWide)
                {
                    _geDesc[i] = Ui.MakeText(rp, "Desc", "", FsSmall, Palette.CardBody,
                        Ui.AlignLeft, false);
                    if (_geDesc[i] != null) Ui.Place(_geDesc[i].gameObject, 16f, geH - 22f,
                        colW - 32f, 20f);
                    if (_geDesc[i] != null) Ui.Truncate(_geDesc[i]);
                }
            }

            _geEmptyBox = Ui.New("GeEmpty", page.transform);
            if (_geEmptyBox != null)
            {
                Ui.Place(_geEmptyBox, 24f, bodyTop, colW, geH);
                Ui.MakeSlot(_geEmptyBox.transform, "Bg", 0f, 0f, colW, geH, Palette.SlotBg);
                TextMeshProUGUI t = Ui.MakeText(_geEmptyBox.transform, "Text",
                    "站里风平浪静，暂时没有会带动行情的事发生。",
                    FsSmall, Palette.Muted, Ui.AlignCenter, true);
                if (t != null) Ui.Place(t.gameObject, 16f, geH * 0.5f - 14f, colW - 32f, 28f);
            }

            // ── 右栏：市场事件（模组自己的行情模拟）────────────────────
            float ecH = Mathf.Floor((area - (EventCardRows - 1) * 8f) / EventCardRows);
            float ecStep = ecH + 8f;
            for (int i = 0; i < EventCardRows; i++)
            {
                // 一张事件卡的所有零件挂在一个空容器下，显隐只要开关容器
                GameObject card = Ui.New("EvCard" + i, page.transform);
                if (card == null) continue;
                Ui.Place(card, colR, bodyTop + i * ecStep, colW, ecH);
                _evCard[i] = card;
                Transform cp = card.transform;

                Ui.MakeSlot(cp, "Bg", 0f, 0f, colW, ecH, Palette.SlotBg);
                _evStrip[i] = Ui.MakeImage(cp, "Strip", Palette.Muted, false);
                if (_evStrip[i] != null) Ui.Place(_evStrip[i].gameObject, 0f, 0f, 5f, ecH);

                _evName[i] = Ui.MakeText(cp, "Name", "", FsCardTitle, Palette.Title,
                    Ui.AlignLeft, false);
                if (_evName[i] != null) Ui.Place(_evName[i].gameObject, 18f, 10f,
                    Mathf.Min(colW - 200f, 620f), 28f);
                if (_evName[i] != null) Ui.Truncate(_evName[i]);

                _evImpact[i] = Ui.MakeText(cp, "Impact", "", FsPageTitle, Palette.Up,
                    Ui.AlignRight, false);
                if (_evImpact[i] != null) Ui.Place(_evImpact[i].gameObject, colW - 186f, 12f, 168f, 32f);

                TextMeshProUGUI lab = Ui.MakeText(cp, "ImpactLab", "每日冲击", FsSmall,
                    Palette.Muted, Ui.AlignRight, false);
                if (lab != null) Ui.Place(lab.gameObject, colW - 186f, 46f, 168f, 16f);

                _evSummary[i] = Ui.MakeText(cp, "Summary", "", 15f, Palette.CardBody,
                    Ui.AlignTopLeft, true);
                if (_evSummary[i] != null) Ui.Place(_evSummary[i].gameObject, 18f, 44f,
                    colW - 36f, Mathf.Max(36f, ecH - 100f));
                if (_evSummary[i] != null) Ui.Truncate(_evSummary[i]);

                _evTarget[i] = Ui.MakeText(cp, "Target", "", FsSmall, Palette.Sub,
                    Ui.AlignLeft, false);
                if (_evTarget[i] != null) Ui.Place(_evTarget[i].gameObject, 18f, ecH - 52f, colW - 36f, 20f);
                if (_evTarget[i] != null) Ui.Truncate(_evTarget[i]);

                _evDays[i] = Ui.MakeText(cp, "Days", "", FsSmall, Palette.Muted,
                    Ui.AlignLeft, false);
                if (_evDays[i] != null) Ui.Place(_evDays[i].gameObject, 18f, ecH - 30f, colW - 36f, 18f);
                if (_evDays[i] != null) Ui.Truncate(_evDays[i]);
            }

            // 无事件时的占位卡，别让玩家看到一片空白
            _evEmptyBox = Ui.New("EvEmpty", page.transform);
            if (_evEmptyBox != null)
            {
                Ui.Place(_evEmptyBox, colR, bodyTop, colW, ecH);
                Ui.MakeSlot(_evEmptyBox.transform, "Bg", 0f, 0f, colW, ecH, Palette.SlotBg);
                TextMeshProUGUI t = Ui.MakeText(_evEmptyBox.transform, "Text",
                    "市场平稳，暂无异常事件。\n持仓过结算日会派息，趁行情安静的时候可以多囤点。",
                    FsSmall, Palette.Muted, Ui.AlignCenter, true);
                if (t != null) Ui.Place(t.gameObject, 20f, ecH * 0.5f - 30f, colW - 40f, 60f);
            }
            return page;
        }

        // ── 公司财报（基本面） ────────────────────────────────────────

        // 上半张表就是「对比视图」：每支标的的营收 / 净利 / 负债率 / 市盈率 / 分红预案
        // 摊在一行里，翻页就能横向比。点任意一行，下半张卡换成那家公司的公告与股东。
        private static GameObject[] _rpRow;
        private static TextMeshProUGUI[,] _rpCell;
        private static UiButton _rpPrev, _rpNext;
        private static TextMeshProUGUI _rpPageText, _rpInfo, _rpName, _rpHolder, _rpNotice;
        private static int _rpPage;
        private static string _rpPick;
        // 一页 6 家：8 行时第 8 行会压在底部的翻页条上（见 问题截图），
        // 26 家按 6 行分是 5 页，翻页条和分页文案都能说清，行距也更松快。
        private const int ReportRows = 6;

        /// <summary>财报列表就是全部标的，按行情页同样的顺序。</summary>
        private static List<StockDef> ReportList()
        {
            List<StockDef> list = new List<StockDef>();
            for (int i = 0; i < StockDefs.All.Length; i++) list.Add(StockDefs.All[i]);
            return list;
        }

        private static GameObject BuildReport(Transform root)
        {
            GameObject page = NewPage(root, "PageReport");

            // 上卡 312 高（标题 12..42 + 副标题 44..62 + 表头 64..82 + 6 行 × 30 到 264 +
            // 翻页条 274..304），下卡吃掉剩下的高度。
            // 每行留 30 而不是 26：营收 / 净利那两格是上下两行小字，挤到 26 会压住下一行。
            float hA = 312f;
            float hB = Mathf.Max(150f, BodyH - hA - 10f);
            Ui.MakeCard(page.transform, "RpCardA", 0f, 0f, ContentW, hA);
            Ui.MakeCard(page.transform, "RpCardB", 0f, hA + 10f, ContentW, hB);

            CardTitle(page.transform, "RpTitle", 24f, 12f, "公司财报", FsPageTitle);
            _rpInfo = Ui.MakeText(page.transform, "RpInfo", "", FsSmall, Palette.Muted,
                Ui.AlignLeft, false);
            if (_rpInfo != null) Ui.Place(_rpInfo.gameObject, 24f, 44f, ContentW - 48f, 18f);

            string[] head = { "名称", "营收 / 同比", "净利 / 同比", "负债率", "市盈率", "分红预案" };
            float[] hx = { 18f, 276f, 454f, 632f, 750f, 868f };
            float[] hw = { 250f, 170f, 170f, 110f, 110f, 164f };
            for (int c = 0; c < head.Length; c++)
            {
                TextMeshProUGUI h = Ui.MakeText(page.transform, "RpHead" + c, head[c], FsSmall,
                    Palette.Muted, c == 0 ? Ui.AlignLeft : Ui.AlignRight, false);
                if (h != null) Ui.Place(h.gameObject, hx[c], 64f, hw[c], 18f);
            }

            _rpRow = new GameObject[ReportRows];
            _rpCell = new TextMeshProUGUI[ReportRows, head.Length];
            for (int i = 0; i < ReportRows; i++)
            {
                int slot = i;
                float y = 86f + i * 30f;
                UiButton row = Ui.MakeButton(page.transform, "RpRow" + i, "", FsCell,
                    Palette.A(Palette.SlotBg, 0.75f), Palette.CardBody,
                    () => PickReport(slot), Ui.AlignLeft);
                if (row != null)
                {
                    row.Place(24f, y, ContentW - 48f, 28f);
                    _rpRow[i] = row.Go;
                }
                for (int c = 0; c < head.Length; c++)
                {
                    TextMeshProUGUI cell = Ui.MakeText(page.transform, "RpC" + i + "_" + c, "-",
                        c == 0 ? 16f : 14f, Palette.CardBody,
                        c == 0 ? Ui.AlignLeft : Ui.AlignRight, false);
                    if (cell != null)
                        Ui.Place(cell.gameObject, hx[c], y, hw[c], 28f);
                    _rpCell[i, c] = cell;
                }
            }

            BuildPager(page.transform, "Rp", hA - 38f, out _rpPrev, out _rpPageText, out _rpNext,
                () => TurnReportPage(-1), () => TurnReportPage(1));

            // ── 下卡：选中那家的公告与股东 ──
            float by = hA + 10f;
            _rpName = Ui.MakeText(page.transform, "RpName", "", FsCardTitle, Palette.Title,
                Ui.AlignLeft, false);
            if (_rpName != null) Ui.Place(_rpName.gameObject, 24f, by + 10f, ContentW - 48f, 30f);

            _rpHolder = Ui.MakeText(page.transform, "RpHolder", "", FsSmall, Palette.Cyan,
                Ui.AlignLeft, false);
            if (_rpHolder != null) Ui.Place(_rpHolder.gameObject, 24f, by + 42f, ContentW - 48f, 20f);

            _rpNotice = Ui.MakeText(page.transform, "RpNotice", "", 15f, Palette.CardBody,
                Ui.AlignTopLeft, true);
            if (_rpNotice != null)
            {
                Ui.Place(_rpNotice.gameObject, 24f, by + 66f, ContentW - 48f, hB - 78f);
                // 公告条数不固定，超出下卡就切掉，别又画到面板外面
                Ui.Truncate(_rpNotice);
            }
            return page;
        }

        private static void TurnReportPage(int delta)
        {
            List<StockDef> list = ReportList();
            int pages = PageCountOf(list, ReportRows);
            int next = Mathf.Clamp(_rpPage + delta, 0, pages - 1);
            if (next == _rpPage)
            {
                LogOp("公司财报翻页：已经是" + (delta > 0 ? "最后一页" : "第一页") + "，忽略");
                SetStatus("公司财报已经是" + (delta > 0 ? "最后一页" : "第一页") + "了。", false);
                Refresh();
                return;
            }
            _rpPage = next;
            LogOp("公司财报翻到第 " + (_rpPage + 1) + " / " + pages + " 页");
            SetStatus("公司财报第 " + (_rpPage + 1) + " / " + pages + " 页。", false);
            Refresh();
        }

        private static void PickReport(int slot)
        {
            StockDef def = At(ReportList(), _rpPage, slot, ReportRows);
            if (def == null) return;
            _rpPick = def.Id;
            LogOp("查看【" + StockDefs.FullName(def) + "】的财报");
            SetStatus("已选中 " + StockDefs.FullName(def) + "，下面是它的股东与公告。", false);
            Refresh();
        }

        private static void RefreshReport()
        {
            if (_rpCell == null) return;
            List<StockDef> list = ReportList();
            int pages = PageCountOf(list, ReportRows);

            if (_rpInfo != null)
                _rpInfo.text = "第 " + StockFundamentals.PeriodIndex() + " 期　每 "
                    + StockFundamentals.PeriodDays + " 天发布一次"
                    + "　共 " + StockDefs.All.Length + " 家 / 每页 " + ReportRows + " 家"
                    + "　点任一行看它的股东与公告"
                    + (StockFundamentals.IsFreshReport() ? "　<color=" + Palette.HexOf(Palette.Gold)
                        + ">今天刚出新财报</color>" : "");

            for (int i = 0; i < ReportRows; i++)
            {
                StockDef def = At(list, _rpPage, i, ReportRows);
                bool show = def != null;
                Ui.SetActive(_rpRow != null ? _rpRow[i] : null, show);
                if (!show)
                {
                    for (int c = 0; c < _rpCell.GetLength(1); c++)
                    {
                        if (_rpCell[i, c] != null) Ui.SetActive(_rpCell[i, c].gameObject, false);
                    }
                    continue;
                }

                StockFundamentals.Fin f = StockFundamentals.Current(def.Id);
                bool picked = _rpPick == def.Id;
                string rowName = tunedName(def);
                string[] texts =
                {
                    rowName,
                    "<size=13>" + Money2(f.Revenue) + "</size>\n<size=11>" + Pct2(f.RevenueGrowth) + "</size>",
                    "<size=13>" + Money2(f.Profit) + "</size>\n<size=11>" + Pct2(f.ProfitGrowth) + "</size>",
                    (f.DebtRatio * 100.0).ToString("0.0") + "%",
                    PeText(def.Id),
                    f.Profit > 0 ? "每10股派 " + (f.Eps * 3.0).ToString("0.00") + " 元" : "不分红"
                };
                Color[] colors =
                {
                    picked ? Palette.Gold : Palette.Title,
                    f.RevenueGrowth >= 0 ? Palette.Up : Palette.Down,
                    f.ProfitGrowth >= 0 ? Palette.Up : Palette.Down,
                    f.DebtRatio >= 0.70 ? Palette.Down : Palette.Muted,
                    Palette.Muted,
                    f.Profit > 0 ? Palette.Muted : Palette.Down
                };
                for (int c = 0; c < texts.Length; c++)
                {
                    TextMeshProUGUI cell = _rpCell[i, c];
                    if (cell == null) continue;
                    Ui.SetActive(cell.gameObject, true);
                    cell.text = texts[c];
                    cell.color = colors[c];
                }
            }

            DimPager(_rpPrev, _rpNext, _rpPage, pages);
            if (_rpPageText != null)
                _rpPageText.text = "第 " + (_rpPage + 1) + " / " + pages + " 页　共 "
                    + list.Count + " 家";

            // 详情：没点过就默认跟着本页第一行，别让下面空着
            StockDef pick = StockDefs.Get(_rpPick);
            if (pick == null) pick = At(list, _rpPage, 0, ReportRows);
            if (pick == null) return;
            _rpPick = pick.Id;

            if (_rpName != null)
                _rpName.text = StockDefs.FullName(pick) + "　<color=" + Palette.HexOf(Palette.Muted) + ">第 "
                    + StockFundamentals.Current(pick.Id).Period + " 期财报</color>";
            if (_rpHolder != null) _rpHolder.text = "股东：" + StockFundamentals.ShareholderText(pick.Id);
            if (_rpNotice != null) _rpNotice.text = StockFundamentals.NoticeText(pick.Id);
        }

        /// <summary>财报行里的公司名：选中那行加一颗小星，便于一眼看到自己在看谁。</summary>
        private static string tunedName(StockDef def)
        {
            bool picked = _rpPick == def.Id;
            return (picked ? "★ " : "") + def.Name;
        }

        private static string PeText(string id)
        {
            double pe = StockFundamentals.Pe(id);
            return pe > 0 ? pe.ToString("0.0") + " 倍" : "亏损";
        }

        private static string Pct2(double v)
        {
            return (v >= 0 ? "+" : "") + (v * 100.0).ToString("0.0") + "%";
        }

        private static string Money2(double yuan)
        {
            double abs = Math.Abs(yuan);
            if (abs >= 100000000.0) return (yuan / 100000000.0).ToString("0.00") + "亿";
            if (abs >= 10000.0) return (yuan / 10000.0).ToString("0.0") + "万";
            return yuan.ToString("0") + "元";
        }

        // ── 股市股评（博主 + 评论区）─────────────────────────────────

        // 上卡是今天各家博主的发言列表，点一条，下卡给正文、传闻和评论区。
        // 「谁说得准」不靠嘴说，靠下卡那行历史胜率——精选博主会自然排到前面，
        // 假博主也一样有粉丝有气势，但胜率骗不了人。
        private static GameObject[] _rvRow;
        private static TextMeshProUGUI[,] _rvCell;
        private static TextMeshProUGUI _rvInfo, _rvHead, _rvBody, _rvRumor, _rvComments;
        private static int _rvPick;
        // 一页 6 条：博主一共 7 位，同时开麦最多 7 条，第 7 条这一版先不列（副标题里写明了）。
        // 从 8 行压到 6 行是为了给下面的评论区腾高度——原来 8 行把下卡挤到只剩 50px 高，
        // 6 条评论直接画到面板外面去了（见 问题截图）。
        private const int ReviewRows = 6;

        private static GameObject BuildReview(Transform root)
        {
            GameObject page = NewPage(root, "PageReview");

            float hA = 250f;
            float hB = Mathf.Max(150f, BodyH - hA - 10f);
            Ui.MakeCard(page.transform, "RvCardA", 0f, 0f, ContentW, hA);
            Ui.MakeCard(page.transform, "RvCardB", 0f, hA + 10f, ContentW, hB);

            CardTitle(page.transform, "RvTitle", 24f, 12f, "股市股评", FsPageTitle);
            _rvInfo = Ui.MakeText(page.transform, "RvInfo", "", FsSmall, Palette.Muted,
                Ui.AlignLeft, false);
            if (_rvInfo != null) Ui.Place(_rvInfo.gameObject, 24f, 44f, ContentW - 48f, 18f);

            string[] head = { "博主", "类型", "标的", "方向", "目标价", "一句话" };
            float[] hx = { 18f, 226f, 314f, 512f, 610f, 738f };
            float[] hw = { 200f, 80f, 190f, 90f, 120f, 294f };
            for (int c = 0; c < head.Length; c++)
            {
                TextMeshProUGUI h = Ui.MakeText(page.transform, "RvHead" + c, head[c], FsSmall,
                    Palette.Muted, (c == 1 || c == 3) ? Ui.AlignCenter : (c == 0 ? Ui.AlignLeft : Ui.AlignRight),
                    false);
                if (h != null) Ui.Place(h.gameObject, hx[c], 64f, hw[c], 18f);
            }

            _rvRow = new GameObject[ReviewRows];
            _rvCell = new TextMeshProUGUI[ReviewRows, head.Length];
            for (int i = 0; i < ReviewRows; i++)
            {
                int slot = i;
                float y = 82f + i * 26f;
                UiButton row = Ui.MakeButton(page.transform, "RvRow" + i, "", FsCell,
                    Palette.A(Palette.SlotBg, 0.75f), Palette.CardBody,
                    () => PickReview(slot), Ui.AlignLeft);
                if (row != null)
                {
                    row.Place(24f, y, ContentW - 48f, 24f);
                    _rvRow[i] = row.Go;
                }
                for (int c = 0; c < head.Length; c++)
                {
                    int align = (c == 1 || c == 3) ? Ui.AlignCenter : (c == 0 ? Ui.AlignLeft : Ui.AlignRight);
                    TextMeshProUGUI cell = Ui.MakeText(page.transform, "RvC" + i + "_" + c, "-",
                        c == 0 ? 16f : 14f, Palette.CardBody, align, false);
                    // 单元格与表头同一矩形：以前整体左移 8px，右对齐的列尾巴对不上表头
                    if (cell != null) Ui.Place(cell.gameObject, hx[c], y, hw[c], 24f);
                    _rvCell[i, c] = cell;
                }
            }

            float by = hA + 10f;
            _rvHead = Ui.MakeText(page.transform, "RvSel", "", FsBody, Palette.Title,
                Ui.AlignLeft, false);
            if (_rvHead != null) Ui.Place(_rvHead.gameObject, 24f, by + 8f, ContentW - 48f, 26f);

            _rvBody = Ui.MakeText(page.transform, "RvBody", "", 15f, Palette.CardBody,
                Ui.AlignTopLeft, true);
            if (_rvBody != null)
            {
                Ui.Place(_rvBody.gameObject, 24f, by + 38f, ContentW - 48f, 46f);
                Ui.Truncate(_rvBody);   // 多行文字才真的截断，见 Ui.Truncate
            }

            _rvRumor = Ui.MakeText(page.transform, "RvRumor", "", FsSmall, Palette.Gold,
                Ui.AlignLeft, false);
            if (_rvRumor != null) Ui.Place(_rvRumor.gameObject, 24f, by + 88f, ContentW - 48f, 20f);

            _rvComments = Ui.MakeText(page.transform, "RvComments", "", 15f, Palette.Muted,
                Ui.AlignTopLeft, true);
            if (_rvComments != null)
            {
                Ui.Place(_rvComments.gameObject, 24f, by + 112f, ContentW - 48f, hB - 118f);
                Ui.Truncate(_rvComments);   // 评论条数不固定，超出下卡就切掉
            }
            return page;
        }

        private static void PickReview(int slot)
        {
            List<StockReview.Post> posts = StockReview.Today();
            if (slot < 0 || slot >= posts.Count) return;
            StockReview.Post p = posts[slot];
            _rvPick = slot;
            StockReview.Blogger b = StockReview.Find(p.BloggerId);
            LogOp("查看【" + (b != null ? b.Name : p.BloggerId) + "】的股评");
            SetStatus("已选中 " + (b != null ? b.Name : "该博主") + " 的股评，下面是评论区。", false);
            Refresh();
        }

        private static void RefreshReview()
        {
            if (_rvCell == null) return;
            List<StockReview.Post> posts = StockReview.Today();

            if (_rvInfo != null)
            {
                _rvInfo.text = posts.Count == 0
                    ? "今天没有新的股评，明天再来看。共 " + StockReview.Bloggers.Length + " 位博主。"
                    : "今天 " + posts.Count + " 条股评　共 " + StockReview.Bloggers.Length
                        + " 位博主　「目标价」是三天内的看法"
                        + (posts.Count > ReviewRows ? "　（列表只列前 " + ReviewRows + " 条）" : "")
                        + "　点任一行看正文与评论区";
            }

            for (int i = 0; i < ReviewRows; i++)
            {
                bool show = i < posts.Count;
                Ui.SetActive(_rvRow != null ? _rvRow[i] : null, show);
                if (!show)
                {
                    for (int c = 0; c < _rvCell.GetLength(1); c++)
                    {
                        if (_rvCell[i, c] != null) Ui.SetActive(_rvCell[i, c].gameObject, false);
                    }
                    continue;
                }

                StockReview.Post p = posts[i];
                StockReview.Blogger b = StockReview.Find(p.BloggerId);
                StockDef def = StockDefs.Get(p.StockId);
                if (b == null || def == null) continue;

                double price = StockEngine.LivePriceYuan(p.StockId);
                double target = price * (1.0 + (p.Dir > 0 ? 0.18 : -0.14));
                bool picked = _rvPick == i;

                string[] texts =
                {
                    (picked ? "★ " : "") + b.Name,
                    StockReview.KindText(b.Kind),
                    def.Name + " <size=11>" + StockDefs.Tag(def) + "</size>",
                    p.Dir > 0 ? "看涨" : "看跌",
                    target.ToString("0.00") + " 元",
                    StockReview.ShortNote(b.Kind, p.Dir)
                };
                Color[] colors =
                {
                    picked ? Palette.Gold : Palette.Title,
                    Palette.Hex("#" + StockReview.KindColor(b.Kind)),
                    Palette.Muted,
                    p.Dir > 0 ? Palette.Up : Palette.Down,
                    Palette.CardBody,
                    Palette.Muted
                };
                for (int c = 0; c < texts.Length; c++)
                {
                    TextMeshProUGUI cell = _rvCell[i, c];
                    if (cell == null) continue;
                    Ui.SetActive(cell.gameObject, true);
                    cell.text = texts[c];
                    cell.color = colors[c];
                }
            }

            // 详情：没点过就默认第一条
            if (_rvPick < 0 || _rvPick >= posts.Count) _rvPick = posts.Count > 0 ? 0 : -1;
            if (_rvPick < 0)
            {
                if (_rvHead != null) _rvHead.text = "今天还没有人发股评";
                if (_rvBody != null) _rvBody.text = "";
                if (_rvRumor != null) _rvRumor.text = "";
                if (_rvComments != null) _rvComments.text = "";
                return;
            }

            StockReview.Post pickPost = posts[_rvPick];
            StockReview.Blogger pb = StockReview.Find(pickPost.BloggerId);
            if (pb == null) return;

            if (_rvHead != null)
            {
                _rvHead.text = "<color=#" + StockReview.KindColor(pb.Kind) + ">"
                    + StockReview.KindText(pb.Kind) + "股评博主</color>　" + pb.Name
                    + "　<color=" + Palette.HexOf(Palette.Muted) + ">粉丝 "
                    + FollowerText(pb.Followers) + "</color>"
                    + "　历史胜率 <color=" + Palette.HexOf(WinRateColor(pb.Id)) + ">"
                    + StockReview.WinRateText(pb.Id) + "</color>";
            }
            if (_rvBody != null) _rvBody.text = pickPost.Headline;

            if (_rvRumor != null)
            {
                string rumor = StockReview.RumorText(pickPost.StockId);
                _rvRumor.text = rumor.Length > 0 ? "市场传闻：" + rumor : "";
            }
            if (_rvComments != null)
            {
                StringBuilder cb = new StringBuilder();
                cb.Append("<color=").Append(Palette.HexOf(Palette.Muted))
                  .Append(">评论区 ").Append(pickPost.Comments.Count)
                  .Append(" 条（里面可能混着拿钱办事的水军，自己分辨）</color>\n");
                for (int i = 0; i < pickPost.Comments.Count; i++)
                {
                    cb.Append(pickPost.Comments[i]).Append('\n');
                }
                _rvComments.text = cb.ToString();
            }
        }

        private static Color WinRateColor(string bloggerId)
        {
            int hits, settled;
            StockReview.Stats(bloggerId, out hits, out settled);
            if (settled <= 0) return Palette.Muted;
            double rate = (double)hits / settled;
            if (rate >= 0.58) return Palette.Up;
            if (rate <= 0.42) return Palette.Down;
            return Palette.Muted;
        }

        private static string RumorSummary()
        {
            List<string> ids = StockReview.TodayRumors();
            if (ids.Count == 0) return "无";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                StockDef def = StockDefs.Get(ids[i]);
                if (def == null) continue;
                if (sb.Length > 0) sb.Append(" · ");
                sb.Append(def.Name);
            }
            return sb.ToString();
        }

        private static string FollowerText(int n)
        {
            if (n >= 10000) return (n / 10000.0).ToString("0.0") + " 万";
            return n.ToString();
        }

        // ── 高级工具（七项付费功能）───────────────────────────────────

        /// <summary>
        /// 左卡是功能清单（点一下切换），右卡是选中那项的正文。
        /// 没开通的项在右卡只显示「这是干什么的」加一个开通按钮；开通了才换成真数据。
        /// 前六项都挂在同一支标的上（右上角 ◀ ▶ 换股），换来换去不用回行情页；
        /// 物价雷达看的是六个行当，所以它不显示换股按钮。
        /// </summary>
        private static GameObject BuildVip(Transform root)
        {
            GameObject page = NewPage(root, "PageVip");
            float lw = VipLeftW;
            float rw = ContentW - lw - 10f;
            float rx = lw + 10f;

            Ui.MakeCard(page.transform, "VipCardL", 0f, 0f, lw, BodyH);
            Ui.MakeCard(page.transform, "VipCardR", rx, 0f, rw, BodyH);

            CardTitle(page.transform, "VipTitleL", 18f, 12f, "高级工具", FsPageTitle);
            _vipInfo = Ui.MakeText(page.transform, "VipInfo", "", FsSmall, Palette.Muted,
                Ui.AlignTopLeft, true);
            if (_vipInfo != null)
            {
                Ui.Place(_vipInfo.gameObject, 18f, 48f, lw - 36f, 52f);
                Ui.Truncate(_vipInfo);
            }

            for (int i = 0; i < StockVip.FeatCount; i++)
            {
                int bit = i;
                UiButton b = Ui.MakeButton(page.transform, "VipTab" + i, "", FsCell,
                    Palette.BtnIdle, Palette.Sub, () => PickVip(bit), Ui.AlignLeft);
                if (b == null) continue;
                b.Place(14f, 108f + i * 52f, lw - 28f, 46f);
                _vipTab[i] = b;
            }

            // 右卡：标题 + 开通按钮同一行，下面依次是「适合谁」「标的行」「正文」「脚注」
            _vipTitle = Ui.MakeText(page.transform, "VipTitle", "", FsCardTitle, Palette.Title,
                Ui.AlignLeft, false);
            if (_vipTitle != null)
            {
                Ui.Place(_vipTitle.gameObject, rx + 22f, 12f, rw - 258f, 30f);
                Ui.Truncate(_vipTitle);
            }
            _vipBuy = Ui.MakeButton(page.transform, "VipBuy", "", FsCell, Palette.BtnGreen,
                Palette.Title, BuyVip, Ui.AlignCenter);
            if (_vipBuy != null) _vipBuy.Place(rx + rw - 22f - 206f, 10f, 206f, 40f);

            _vipWhom = Ui.MakeText(page.transform, "VipWhom", "", FsSmall, Palette.Muted,
                Ui.AlignLeft, false);
            if (_vipWhom != null)
            {
                Ui.Place(_vipWhom.gameObject, rx + 22f, 46f, rw - 44f, 20f);
                Ui.Truncate(_vipWhom);
            }

            _vipPrev = Ui.MakeButton(page.transform, "VipPrev", "◀", FsCell, Palette.BtnIdle,
                Palette.Sub, () => StepVip(-1), Ui.AlignCenter);
            if (_vipPrev != null) _vipPrev.Place(rx + 22f, 76f, 44f, 34f);
            _vipNext = Ui.MakeButton(page.transform, "VipNext", "▶", FsCell, Palette.BtnIdle,
                Palette.Sub, () => StepVip(1), Ui.AlignCenter);
            if (_vipNext != null) _vipNext.Place(rx + rw - 22f - 44f, 76f, 44f, 34f);

            _vipName = Ui.MakeText(page.transform, "VipName", "", FsBody, Palette.Title,
                Ui.AlignCenter, false);
            if (_vipName != null)
            {
                Ui.Place(_vipName.gameObject, rx + 72f, 78f, rw - 144f, 30f);
                Ui.Truncate(_vipName);
            }

            float bodyH = BodyH - VipBodyY - 56f;
            _vipBody = Ui.MakeText(page.transform, "VipBody", "", 15f, Palette.CardBody,
                Ui.AlignTopLeft, true);
            if (_vipBody != null)
            {
                Ui.Place(_vipBody.gameObject, rx + 22f, VipBodyY, rw - 44f, bodyH);
                Ui.Truncate(_vipBody);
            }

            // 盘口要左右两栏才摆得下二十档，所以正文备了第二块（只有盘口用得上）
            _vipBody2 = Ui.MakeText(page.transform, "VipBody2", "", 15f, Palette.CardBody,
                Ui.AlignTopLeft, true);
            if (_vipBody2 != null)
            {
                Ui.Place(_vipBody2.gameObject, rx + 22f, VipBodyY, rw - 44f, bodyH);
                Ui.Truncate(_vipBody2);
                Ui.SetActive(_vipBody2.gameObject, false);
            }

            _vipHint = Ui.MakeText(page.transform, "VipHint", "", FsSmall, Palette.Muted,
                Ui.AlignTopLeft, true);
            if (_vipHint != null)
            {
                Ui.Place(_vipHint.gameObject, rx + 22f, BodyH - 48f, rw - 44f, 34f);
                Ui.Truncate(_vipHint);
            }

            if (string.IsNullOrEmpty(_vipId) || StockDefs.Get(_vipId) == null)
            {
                _vipId = StockDefs.Get(_selectedId) != null
                    ? _selectedId
                    : (StockDefs.All.Length > 0 ? StockDefs.All[0].Id : "");
            }
            return page;
        }

        private static void PickVip(int bit)
        {
            if (bit < 0 || bit >= StockVip.FeatCount || bit == _vipBit) return;
            _vipBit = bit;
            StockVip.Feature f = StockVip.Get(bit);
            LogOp("高级工具 → " + (f != null ? f.Name : "#" + bit));
            SetStatus(f != null ? "已切到【" + f.Name + "】。" : "", false);
            Refresh();
        }

        private static void StepVip(int dir)
        {
            if (StockDefs.All.Length == 0) return;
            int idx = 0;
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                if (StockDefs.All[i].Id == _vipId) { idx = i; break; }
            }
            idx = (idx + dir) % StockDefs.All.Length;
            if (idx < 0) idx += StockDefs.All.Length;
            _vipId = StockDefs.All[idx].Id;
            LogOp("高级工具换标的 → " + StockDefs.FullName(StockDefs.All[idx]));
            SetStatus("高级工具已切到 " + StockDefs.FullName(StockDefs.All[idx]) + "。", false);
            Refresh();
        }

        /// <summary>开通一项。钱从店铺现金扣（和黑市开户一致），不走股票账户。</summary>
        private static void BuyVip()
        {
            StockVip.Feature f = StockVip.Get(_vipBit);
            if (f == null) return;
            LogOp("开通【" + f.Name + "】(" + f.Price + " 元)");
            string err = StockVip.Unlock(_vipBit);
            if (err != null)
            {
                LogOp("开通【" + f.Name + "】失败：" + err);
                SetStatus("开通失败：" + err, true);
                Refresh();
                return;
            }
            SetStatus("已开通【" + f.Name + "】，扣店铺现金 " + f.Price + " 元，永久有效。", false);
            Refresh();
        }

        private static void RefreshVip()
        {
            if (_vipTab[0] == null) return;
            float lw = VipLeftW;
            float rw = ContentW - lw - 10f;
            float rx = lw + 10f;
            float bodyH = BodyH - VipBodyY - 56f;

            if (_vipInfo != null)
            {
                _vipInfo.text = "已开通 " + StockVip.UnlockedCount + " / " + StockVip.FeatCount + " 项\n"
                    + "<color=#FFC24A>一次性买断，永久有效</color>　" + StockVip.FeatCount + " 项合计 "
                    + StockVip.TotalPrice + " 元\n已花 " + StockVip.Spent + " 元";
            }

            for (int i = 0; i < StockVip.FeatCount; i++)
            {
                UiButton b = _vipTab[i];
                if (b == null) continue;
                StockVip.Feature f = StockVip.Get(i);
                if (f == null) continue;
                bool has = StockVip.Has(i);
                bool on = i == _vipBit;
                b.SetText((has ? "<color=#FFC24A>●</color> " : "<color=#6E819A>○</color> ")
                    + f.Name + "　<size=13>" + f.Price + " 元</size>");
                b.SetBg(on ? Palette.NavOn : Palette.BtnIdle);
                b.SetTextColor(on ? Palette.Title : (has ? Palette.CardBody : Palette.Sub));
            }

            StockVip.Feature cur = StockVip.Get(_vipBit);
            if (cur == null) return;
            bool ok = StockVip.Has(_vipBit);

            if (_vipTitle != null) _vipTitle.text = cur.Name;
            if (_vipWhom != null)
                _vipWhom.text = Ui.Cut("适合：" + cur.Whom + "　标价 " + cur.Price + " 元", FsSmall, rw - 44f);

            if (_vipBuy != null)
            {
                if (ok)
                {
                    _vipBuy.SetText("已开通");
                    _vipBuy.SetBg(Palette.SlotBg);
                    _vipBuy.SetTextColor(Palette.Sub);
                    _vipBuy.SetInteractable(false);
                }
                else
                {
                    _vipBuy.SetText("开通 " + cur.Price + " 元");
                    _vipBuy.SetBg(Palette.BtnGreen);
                    _vipBuy.SetTextColor(Palette.Title);
                    _vipBuy.SetInteractable(true);
                }
            }

            StockDef def = StockDefs.Get(_vipId);
            if (def == null && StockDefs.All.Length > 0)
            {
                def = StockDefs.All[0];
                _vipId = def.Id;
            }
            // 物价雷达看的是全站六个行当，跟具体选了哪支标的没关系：标的那一行直接收起来
            bool macro = _vipBit == StockVip.FeatMacro;
            if (_vipPrev != null) _vipPrev.SetActive(!macro);
            if (_vipNext != null) _vipNext.SetActive(!macro);
            if (_vipName != null)
            {
                if (macro)
                {
                    _vipName.text = "<size=15><color=#9EC7DE>全站六个行当 · 每天收工结算推进一次</color></size>";
                }
                else
                {
                    _vipName.text = def != null
                        ? def.Name + "　<size=13><color=#9EC7DE>" + StockDefs.Tag(def) + "</color></size>"
                            + "　<size=13><color=#9EB3CC>" + Money2(StockEngine.LivePriceYuan(def.Id))
                            + " 元</color></size>"
                        : "没有可选的标的";
                }
            }

            if (_vipHint != null)
            {
                if (macro && ok) _vipHint.text = "<color=#9EC7DE>物价每次收工结算推进一步，白天不会变；阈值就画在每行的刻度尺上。</color>";
                else _vipHint.text = ok
                    ? "<color=#9EC7DE>数据跟着盘中价刷新，收工后就定住了。</color>"
                    : "<color=#E5533D>这一项还没开通。</color>右上角按钮开通，钱从店铺现金扣。";
            }

            // 盘口用两块正文并排，其余功能只用左边那一块铺满
            bool depth = ok && _vipBit == StockVip.FeatDepth;
            if (_vipBody != null)
            {
                if (depth)
                {
                    float half = (rw - 58f) * 0.5f;
                    Ui.Place(_vipBody.gameObject, rx + 22f, VipBodyY, half, bodyH);
                }
                else
                {
                    Ui.Place(_vipBody.gameObject, rx + 22f, VipBodyY, rw - 44f, bodyH);
                }
                _vipBody.text = ok ? VipContent(_vipBit, def) : VipLocked(cur);
            }
            if (_vipBody2 != null)
            {
                Ui.SetActive(_vipBody2.gameObject, depth);
                if (depth)
                {
                    float half = (rw - 58f) * 0.5f;
                    Ui.Place(_vipBody2.gameObject, rx + 22f + half + 14f, VipBodyY, half, bodyH);
                    _vipBody2.text = VipDepthBids(def);
                }
            }
        }

        private static string VipLocked(StockVip.Feature f)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<size=17><color=#FFC24A>").Append(f.Name).Append("</color></size>")
              .Append("　<size=13><color=#9EB3CC>").Append(f.Price)
              .Append(" 元 · 一次性买断 · 永久有效</color></size>\n\n");
            sb.Append(f.What).Append("\n\n");
            sb.Append("<color=#9EC7DE>适合谁：").Append(f.Whom).Append("</color>\n\n");
            sb.Append("<color=#9EB3CC>开通后这一栏会显示：</color>\n").Append(VipPreview(f.Bit));
            return sb.ToString();
        }

        private static string VipPreview(int bit)
        {
            switch (bit)
            {
                case StockVip.FeatTape:
                    return "· 今天最近 12 笔成交：成交价 / 股数 / 主动买还是主动卖\n"
                         + "· 已成交总量与主动买占比";
                case StockVip.FeatDepth:
                    return "· 买卖各十档的挂单价与挂单量（左右分栏）\n"
                         + "· 十档买盘与卖盘的厚度对比";
                case StockVip.FeatFlow:
                    return "· 今日净流入拆成散户 / 机构 / 公司操盘手三份\n"
                         + "· 近 5 日、10 日累计净流入与看盘提醒";
                case StockVip.FeatAi:
                    return "· 近期方向 / 今日资金 / 基本面 / 市场事件四项打分与理由\n"
                         + "· 综合评分、结论和一句操作建议";
                case StockVip.FeatBacktest:
                    return "· 三种玩法在最近 " + StockVip.BacktestDays + " 天的真实回测\n"
                         + "· 总收益、最大回撤、交易次数、胜率，并与「一直拿着」对比";
                case StockVip.FeatWatch:
                    return "· 今天金额最大的 6 笔成交（价 / 量 / 方向 / 疑似席位）\n"
                         + "· 智能盯盘：持仓浮亏、涨跌异动、停板、事件冲击、杠杆预警";
                case StockVip.FeatMacro:
                    return "· 六个行当各自的物价指数与刻度尺（阈值画在上面）\n"
                         + "· 别家店铺今天的供货 / 吃进量，加上你自己卖出去的那一份\n"
                         + "· 每个行当距通胀线、通缩线还差几个点，已经排上队的下一步动作";
            }
            return string.Empty;
        }

        private static string VipContent(int bit, StockDef def)
        {
            if (def == null) return "没有可选的标的。";
            switch (bit)
            {
                case StockVip.FeatTape: return VipTape(def);
                case StockVip.FeatDepth: return VipDepthAsks(def);
                case StockVip.FeatFlow: return VipFlow(def);
                case StockVip.FeatAi: return VipAi(def);
                case StockVip.FeatBacktest: return VipBacktest(def);
                case StockVip.FeatWatch: return VipWatch(def);
                case StockVip.FeatMacro: return VipMacroPanel();
            }
            return string.Empty;
        }

        /// <summary>
        /// 物价雷达：六个行当各一行指数 + 刻度尺，下面一行是供需两边与「距线还差几个点」。
        /// 刻度尺的 ● 就是当前指数位置，正中间（第 5 格）是基准 100，两端是 ±9%，
        /// 通胀线 ±6% 正好落在第 2 格和第 8 格上，一眼能看出离那条线还有几格。
        /// </summary>
        private static string VipMacroPanel()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<size=13><color=#9EB3CC>六行当的物价与供需　刻度 ←便宜·基准·贵→（指数 100 为基准，偏离 ±6% 即通胀 / 通缩）</color></size>\n");

            for (int b = 0; b < StockEconomy.BucketCount; b++)
            {
                int st = StockMacro.State(b);
                double rate = StockMacro.RatePct(b);
                string col = (st == StockMacro.StInflate || st == StockMacro.StShort) ? "E5533D"
                           : (st == StockMacro.StDeflate || st == StockMacro.StGlut) ? "45C08A" : "9EB3CC";

                int pos = (int)Math.Round((rate / (StockMacro.InflateLine * 100.0 * 1.5) + 1.0) * 5.0);
                if (pos < 0) pos = 0;
                if (pos > 10) pos = 10;
                StringBuilder bar = new StringBuilder();
                for (int k = 0; k <= 10; k++) bar.Append(k == pos ? "●" : "○");

                sb.Append("<color=#").Append(col).Append(">●</color> ")
                  .Append(StockEconomy.BucketLabel(b)).Append("　")
                  .Append("<color=#").Append(col).Append(">")
                  .Append((100.0 + rate).ToString("0.0")).Append("</color>　")
                  .Append("<size=13>").Append(rate.ToString("+0.0;-0.0")).Append("%</size>　")
                  .Append("<size=12><color=#").Append(col).Append(">").Append(bar).Append("</color></size>　")
                  .Append("<color=#").Append(col).Append(">").Append(StockMacro.StateName(st)).Append("</color>\n");

                string chain = StockMacro.ChainIn(b) >= 0
                    ? "　已排队：" + StockMacro.ChainName(b) + "（" + StockMacro.ChainIn(b) + " 天后）" : "";
                sb.Append("<size=12><color=#9EB3CC>　　今天供货 ").Append(StockMacro.NpcSupply(b).ToString("0"))
                  .Append("／吃进 ").Append(StockMacro.NpcDemand(b).ToString("0"))
                  .Append("＋你 ").Append(StockEconomy.TodayValue(b).ToString("0"))
                  .Append("　").Append(MacroTip(b, st, rate)).Append(chain).Append("</color></size>\n");
            }
            return sb.ToString();
        }

        /// <summary>「距触发还差多少」这一句。已经在状态里就报超出多少，否则报还差几个点。</summary>
        private static string MacroTip(int b, int st, double rate)
        {
            double line = StockMacro.InflateLine * 100.0;
            switch (st)
            {
                case StockMacro.StInflate:
                    return "高出通胀线 " + (rate - line).ToString("0.0") + " 个点";
                case StockMacro.StDeflate:
                    return "低于通缩线 " + (-rate - line).ToString("0.0") + " 个点";
                case StockMacro.StShort:
                    return "距通胀线 " + (line - rate).ToString("0.0") + " 个点，供需先紧起来了";
                case StockMacro.StGlut:
                    return "距通缩线 " + (line + rate).ToString("0.0") + " 个点，货已经压住了";
                default:
                    return rate >= 0.0
                        ? "距通胀线 " + (line - rate).ToString("0.0") + " 个点"
                        : "距通缩线 " + (line + rate).ToString("0.0") + " 个点";
            }
        }

        private static string VipTape(StockDef def)
        {
            List<StockVip.TickRow> list = StockVip.Tape(def.Id, 12);
            StringBuilder sb = new StringBuilder();
            sb.Append("<color=#9EB3CC>现价 ").Append(Money2(StockEngine.LivePriceYuan(def.Id)))
              .Append(" 元　").Append(StockEngine.BoardText(def.Id))
              .Append("　今天最近 ").Append(list.Count).Append(" 笔成交（最新的在最上面）</color>\n\n");

            double vol = 0.0, buyVol = 0.0;
            for (int i = 0; i < list.Count; i++)
            {
                StockVip.TickRow t = list[i];
                vol += t.Shares;
                if (t.Buy) buyVol += t.Shares;
                sb.Append("<size=12><color=#9EB3CC>#").Append((i + 1).ToString("00"))
                  .Append("</color></size>　")
                  .Append(t.Price.ToString("0.00")).Append(" 元　")
                  .Append(t.Shares.ToString("N0")).Append(" 股　")
                  .Append(t.Buy ? "<color=#E5533D>主动买 ↑</color>" : "<color=#45C08A>主动卖 ↓</color>")
                  .Append('\n');
            }

            double ratio = vol > 0 ? buyVol / vol : 0.5;
            sb.Append("\n<color=#9EC7DE>合计 ").Append(StockVip.Big(vol)).Append(" 股　主动买占 ")
              .Append((ratio * 100.0).ToString("0")).Append("%　")
              .Append(ratio >= 0.55 ? "买方更急" : (ratio <= 0.45 ? "卖方更急" : "多空咬得很紧"))
              .Append("</color>");
            return sb.ToString();
        }

        /// <summary>盘口左栏：卖十到卖一（越靠下越近）。</summary>
        private static string VipDepthAsks(StockDef def)
        {
            List<StockVip.Level> bids = new List<StockVip.Level>();
            List<StockVip.Level> asks = new List<StockVip.Level>();
            StockVip.Depth(def.Id, 10, bids, asks);

            StringBuilder sb = new StringBuilder();
            sb.Append("<color=#45C08A>卖盘（上远下近）</color>\n");
            for (int i = asks.Count - 1; i >= 0; i--)
            {
                sb.Append("<size=12><color=#45C08A>卖").Append(i + 1).Append("</color></size>　")
                  .Append(asks[i].Price.ToString("0.00")).Append(" 元　")
                  .Append(asks[i].Shares.ToString("N0")).Append(" 股\n");
            }
            sb.Append("\n<color=#FFC24A>现价 ")
              .Append(Money2(StockEngine.LivePriceYuan(def.Id))).Append(" 元</color>");
            return sb.ToString();
        }

        /// <summary>盘口右栏：买一到买十，末尾附两侧厚度对比。</summary>
        private static string VipDepthBids(StockDef def)
        {
            List<StockVip.Level> bids = new List<StockVip.Level>();
            List<StockVip.Level> asks = new List<StockVip.Level>();
            StockVip.Depth(def.Id, 10, bids, asks);

            StringBuilder sb = new StringBuilder();
            sb.Append("<color=#E5533D>买盘（上近下远）</color>\n");
            double bidSum = 0.0, askSum = 0.0;
            for (int i = 0; i < bids.Count; i++)
            {
                bidSum += bids[i].Shares;
                sb.Append("<size=12><color=#E5533D>买").Append(i + 1).Append("</color></size>　")
                  .Append(bids[i].Price.ToString("0.00")).Append(" 元　")
                  .Append(bids[i].Shares.ToString("N0")).Append(" 股\n");
            }
            for (int i = 0; i < asks.Count; i++) askSum += asks[i].Shares;

            sb.Append("\n<color=#9EC7DE>十档买盘 ").Append(StockVip.Big(bidSum))
              .Append(" 股　十档卖盘 ").Append(StockVip.Big(askSum)).Append(" 股\n")
              .Append(bidSum >= askSum
                  ? "下方垫的比上方压的厚，买盘接得住。"
                  : "上方压的比下方垫的厚，往上走要先啃掉这些卖单。")
              .Append("</color>");
            return sb.ToString();
        }

        private static string VipFlow(StockDef def)
        {
            List<StockVip.FlowPart> parts = StockVip.FlowParts(def.Id);
            double total = 0.0;
            for (int i = 0; i < parts.Count; i++) total += parts[i].Yuan;

            StringBuilder sb = new StringBuilder();
            sb.Append("<color=#9EB3CC>今日净流入 —— 和行情页那列「资金」是同一个数，这里只是把它拆开了</color>\n\n");
            sb.Append("<size=20><color=").Append(Palette.HexOf(total >= 0 ? Palette.Up : Palette.Down))
              .Append(">").Append(VipSigned(total)).Append(" 元</color></size>\n")
              .Append("<color=#9EB3CC>").Append(StockBots.FlowText(def.Id)).Append("</color>\n\n");

            for (int i = 0; i < parts.Count; i++)
            {
                StockVip.FlowPart p = parts[i];
                sb.Append(p.Who).Append("　")
                  .Append("<color=").Append(Palette.HexOf(p.Yuan >= 0 ? Palette.Up : Palette.Down))
                  .Append(">").Append(p.Yuan >= 0 ? "净买入 " : "净卖出 ")
                  .Append(StockVip.Big(Math.Abs(p.Yuan))).Append(" 元</color>\n");
            }

            double d5 = StockVip.FlowSum(def.Id, 5);
            double d10 = StockVip.FlowSum(def.Id, 10);
            sb.Append("\n<color=#9EB3CC>近 5 日累计 ").Append(VipSigned(d5))
              .Append(" 元　近 10 日累计 ").Append(VipSigned(d10)).Append(" 元</color>\n\n");

            double retail = parts.Count > 0 ? parts[0].Yuan : 0.0;
            double inst = parts.Count > 1 ? parts[1].Yuan : 0.0;
            double op = parts.Count > 2 ? parts[2].Yuan : 0.0;
            sb.Append("<color=#9EC7DE>怎么看：</color>\n");
            if (inst > 0 && op >= 0) sb.Append("· 机构在买、操盘手也没跑，这种结构最干净。\n");
            else if (inst > 0) sb.Append("· 机构在接、操盘手在出，接得住但上方有抛压。\n");
            else if (op > 0) sb.Append("· 操盘手在自己拉、机构没跟，涨得快跌得也快。\n");
            else sb.Append("· 机构和操盘手同时在走，只剩散户在接，这种最危险。\n");
            if (retail > 0 && inst <= 0) sb.Append("· 散户在独扛，别当最后一个进来的。\n");
            if (d5 < 0) sb.Append("· 近 5 日累计是净流出，钱这几天一直在往外走。\n");
            return sb.ToString();
        }

        private static string VipAi(StockDef def)
        {
            StockVip.Diagnosis d = StockVip.Diagnose(def.Id);
            Color vc = d.Score >= 75 ? Palette.Up
                : (d.Score >= 60 ? Palette.Gold
                : (d.Score >= 45 ? Palette.Cyan : Palette.Down));

            StringBuilder sb = new StringBuilder();
            sb.Append("<size=34><color=").Append(Palette.HexOf(vc)).Append(">")
              .Append(d.Score).Append("</color></size>")
              .Append("<size=15><color=#9EB3CC> / 100</color></size>")
              .Append("　<size=22><color=").Append(Palette.HexOf(vc)).Append(">")
              .Append(d.Verdict).Append("</color></size>\n\n");
            sb.Append("<color=#9EB3CC>基分 50，四项各自加减，下面把每一项的分数都摊开写</color>\n\n");
            for (int i = 0; i < d.Notes.Count; i++) sb.Append("· ").Append(d.Notes[i]).Append('\n');
            sb.Append("\n<color=#FFC24A>结论：</color>").Append(d.Advice);
            return sb.ToString();
        }

        private static string VipBacktest(StockDef def)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<color=#9EB3CC>用真实的每日收盘价，把三种玩法在最近 ")
              .Append(StockVip.BacktestDays).Append(" 天各跑一遍（每次买入都全押）</color>\n\n");

            for (int i = 0; i < StockVip.Strategies.Length; i++)
            {
                StockVip.Backtest r = StockVip.Run(def.Id, i, StockVip.BacktestDays);
                sb.Append("<color=#FFC24A>").Append(StockVip.Strategies[i]).Append("</color>\n");
                sb.Append("　总收益 <color=").Append(Palette.HexOf(r.ReturnPct >= 0 ? Palette.Up : Palette.Down))
                  .Append(">").Append(VipSigned(r.ReturnPct)).Append("%</color>")
                  .Append("　最大回撤 ").Append(r.MaxDrawdownPct.ToString("0.0")).Append("%\n");

                if (i == 0)
                {
                    sb.Append("　<color=#9EB3CC>这是基准线，下面两条要比它高才算有用</color>\n\n");
                }
                else
                {
                    double diff = r.ReturnPct - r.HoldPct;
                    sb.Append("　<color=#9EB3CC>同期一直拿着 ").Append(VipSigned(r.HoldPct)).Append("%</color>")
                      .Append("　<color=").Append(Palette.HexOf(diff >= 0 ? Palette.Up : Palette.Down))
                      .Append(">").Append(diff >= 0 ? "跑赢基准 " : "跑输基准 ")
                      .Append(VipSigned(diff)).Append("%</color>\n");
                    sb.Append("　交易 ").Append(r.Trades).Append(" 次，赢 ").Append(r.Wins).Append(" 次")
                      .Append(r.Trades > 0 ? "（胜率 " + (r.Wins * 100 / r.Trades) + "%）" : "")
                      .Append(r.Holding ? "　<color=#9EB3CC>结束时仍持有</color>" : "")
                      .Append("\n\n");
                }
            }

            sb.Append("<color=#9EC7DE>回测只说明「过去这段路这么走划算」，不代表以后也一样。</color>");
            return sb.ToString();
        }

        private static string VipWatch(StockDef def)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<color=#FFC24A>【主力大单】今天金额最大的 6 笔</color>\n");

            List<StockVip.BigRow> big = StockVip.BigOrders(def.Id, 6);
            for (int i = 0; i < big.Count; i++)
            {
                StockVip.BigRow b = big[i];
                sb.Append("<size=12><color=#9EB3CC>#").Append(i + 1).Append("</color></size>　")
                  .Append(StockVip.Big(b.Price * b.Shares)).Append(" 元　")
                  .Append(b.Shares.ToString("N0")).Append(" 股　")
                  .Append(b.Buy ? "<color=#E5533D>主动买</color>" : "<color=#45C08A>主动卖</color>")
                  .Append("　<color=#9EB3CC>疑似 ").Append(b.Seat).Append("</color>\n");
            }

            sb.Append("\n<color=#FFC24A>【智能盯盘】你的持仓与收藏</color>\n");
            List<string> alerts = StockVip.WatchAlerts();
            if (alerts.Count == 0)
            {
                sb.Append("<color=#45C08A>· 一切正常</color>，没有需要你现在处理的异动。\n");
            }
            else
            {
                for (int i = 0; i < alerts.Count; i++) sb.Append(alerts[i]).Append('\n');
            }
            sb.Append("\n<color=#9EC7DE>盯盘不用开着行情页也会算，隔一阵进来看一眼就行。</color>");
            return sb.ToString();
        }

        private static string VipSigned(double v)
        {
            return (v >= 0 ? "＋" : "－") + Math.Abs(v).ToString("0.0");
        }

        // ── 智能选股（免费）───────────────────────────────────────────

        private static GameObject BuildPicker(Transform root)
        {
            GameObject page = NewPage(root, "PagePicker");
            float lw = PkLeftW;
            float rw = ContentW - lw - 10f;
            float rx = lw + 10f;

            Ui.MakeCard(page.transform, "PkCardL", 0f, 0f, lw, BodyH);
            Ui.MakeCard(page.transform, "PkCardR", rx, 0f, rw, BodyH);

            CardTitle(page.transform, "PkTitleL", 20f, 12f, "智能选股", FsPageTitle);
            _pkInfo = Ui.MakeText(page.transform, "PkInfo", "", FsSmall, Palette.Muted,
                Ui.AlignTopLeft, true);
            if (_pkInfo != null)
            {
                Ui.Place(_pkInfo.gameObject, 20f, 48f, lw - 40f, 44f);
                Ui.Truncate(_pkInfo);
            }

            const float rStep = 96f;
            for (int i = 0; i < StockVip.PickerSize; i++)
            {
                float y = 100f + i * rStep;
                Image slot = Ui.MakeSlot(page.transform, "PkSlot" + i, 20f, y, lw - 40f,
                    rStep - 12f, Palette.SlotBg);
                _pkRow[i] = slot != null ? slot.gameObject : null;

                // 透明点击层压在槽上、垫在文字下：点整行就去这支的交易页
                int slotIdx = i;
                UiButton hit = Ui.MakeButton(page.transform, "PkHit" + i, "", FsCell,
                    Palette.A(Palette.SlotBg, 0.01f), Palette.CardBody,
                    () => PickGo(slotIdx), Ui.AlignLeft);
                if (hit != null)
                {
                    hit.Place(20f, y, lw - 40f, rStep - 12f);
                    _pkHit[i] = hit;
                }

                _pkName[i] = Ui.MakeText(page.transform, "PkName" + i, "-", FsBody, Palette.Title,
                    Ui.AlignLeft, false);
                if (_pkName[i] != null)
                {
                    Ui.Place(_pkName[i].gameObject, 34f, y + 8f, lw - 190f, 26f);
                    Ui.Truncate(_pkName[i]);
                }
                _pkConf[i] = Ui.MakeText(page.transform, "PkConf" + i, "-", FsCell, Palette.Gold,
                    Ui.AlignRight, false);
                if (_pkConf[i] != null)
                {
                    Ui.Place(_pkConf[i].gameObject, lw - 34f - 140f, y + 10f, 140f, 24f);
                }
                _pkReason[i] = Ui.MakeText(page.transform, "PkReason" + i, "-", 15f, Palette.Muted,
                    Ui.AlignTopLeft, true);
                if (_pkReason[i] != null)
                {
                    Ui.Place(_pkReason[i].gameObject, 34f, y + 40f, lw - 68f, rStep - 54f);
                    Ui.Truncate(_pkReason[i]);
                }
            }

            _pkLeft = Ui.MakeText(page.transform, "PkLeft", "", FsSmall, Palette.Cyan,
                Ui.AlignLeft, false);
            if (_pkLeft != null)
            {
                Ui.Place(_pkLeft.gameObject, 20f, BodyH - 54f, lw - 250f, 40f);
                Ui.Truncate(_pkLeft);
            }
            _pkNext = Ui.MakeButton(page.transform, "PkNext", "换一批", FsCell, Palette.BtnGreen,
                Palette.Title, NextPicker, Ui.AlignCenter);
            if (_pkNext != null) _pkNext.Place(lw - 20f - 200f, BodyH - 58f, 200f, 44f);

            CardTitle(page.transform, "PkTitleR", rx + 20f, 12f, "它准不准", FsPageTitle);
            _pkStats = Ui.MakeText(page.transform, "PkStats", "", FsBody, Palette.CardBody,
                Ui.AlignTopLeft, true);
            if (_pkStats != null)
            {
                Ui.Place(_pkStats.gameObject, rx + 20f, 54f, rw - 40f, 152f);
                Ui.Truncate(_pkStats);
            }
            _pkNote = Ui.MakeText(page.transform, "PkNote", "", 15f, Palette.Muted,
                Ui.AlignTopLeft, true);
            if (_pkNote != null)
            {
                Ui.Place(_pkNote.gameObject, rx + 20f, 214f, rw - 40f, BodyH - 226f);
                Ui.Truncate(_pkNote);
            }
            return page;
        }

        private static void NextPicker()
        {
            LogOp("智能选股：换一批");
            List<StockVip.Pick> list = StockVip.PickerNext();
            if (list == null)
            {
                LogOp("智能选股：本轮三批已用完，忽略");
                SetStatus("本轮的 " + StockVip.PickerTotal + " 批用完了，"
                    + StockVip.PickerResetIn + " 天后恢复。", true);
                Refresh();
                return;
            }
            SetStatus("已换一批，本轮还剩 " + StockVip.PickerLeft + " 批。", false);
            Refresh();
        }

        /// <summary>智能选股第 slot 行点击：选中这支，直接进交易页下单。</summary>
        private static void PickGo(int slot)
        {
            if (slot < 0 || slot >= StockVip.PickerSize) return;
            string id = _pkId[slot];
            if (string.IsNullOrEmpty(id)) return;
            StockDef def = StockDefs.Get(id);
            if (def == null) return;
            LogOp("智能选股：去下单 " + def.Name + "(" + id + ")");
            OpenTradeFor(id);
        }

        private static void RefreshPicker()
        {
            if (_pkName[0] == null) return;
            float lw = PkLeftW;      // 与 BuildPicker 同口径，Ui.Cut 才知道能放多宽
            List<StockVip.Pick> list = StockVip.PickerList();

            if (_pkInfo != null)
            {
                _pkInfo.text = "第 " + (StockVip.PickerRoll + 1) + " / " + StockVip.PickerTotal
                    + " 批，每批 " + StockVip.PickerSize + " 支　<color=#FFC24A>完全免费</color>\n"
                    + "<color=#9EB3CC>每 " + StockVip.PickerCycleDays + " 天恢复 "
                    + StockVip.PickerTotal + " 批（还有 " + StockVip.PickerResetIn + " 天）　"
                    + "信心是它自己说的，不是胜率</color>";
            }

            for (int i = 0; i < StockVip.PickerSize; i++)
            {
                bool show = i < list.Count;
                Ui.SetActive(_pkRow[i], show);
                Ui.SetActive(_pkName[i] != null ? _pkName[i].gameObject : null, show);
                Ui.SetActive(_pkConf[i] != null ? _pkConf[i].gameObject : null, show);
                Ui.SetActive(_pkReason[i] != null ? _pkReason[i].gameObject : null, show);
                if (_pkHit[i] != null) _pkHit[i].SetActive(show);
                if (!show)
                {
                    _pkId[i] = null;
                    continue;
                }

                StockVip.Pick p = list[i];
                StockDef def = StockDefs.Get(p.StockId);
                if (def == null)
                {
                    _pkId[i] = null;
                    continue;
                }
                _pkId[i] = def.Id;

                double chg = StockEngine.DayChangePercent(def.Id);
                if (_pkName[i] != null)
                {
                    _pkName[i].text = Ui.Cut(def.Name + "　<size=13><color=#9EC7DE>"
                        + StockDefs.Tag(def) + "</color></size>　<size=13><color="
                        + Palette.HexOf(chg >= 0 ? Palette.Up : Palette.Down) + ">"
                        + Money2(StockEngine.LivePriceYuan(def.Id)) + " 元 " + StockVip.Pct(chg)
                        + "</color></size>", FsBody, lw - 190f);
                }
                if (_pkConf[i] != null) _pkConf[i].text = "信心 " + p.Confidence + "%";
                if (_pkReason[i] != null) _pkReason[i].text = p.Reason;
            }

            int left = StockVip.PickerLeft;
            if (_pkLeft != null)
            {
                _pkLeft.text = Ui.Cut(left > 0
                    ? "还能换 " + left + " 批　还有 " + StockVip.PickerResetIn + " 天恢复额度"
                    : "<color=#E5533D>本轮用完了，还有 " + StockVip.PickerResetIn + " 天恢复。</color>",
                    FsSmall, lw - 250f);
            }
            if (_pkNext != null)
            {
                bool can = !StockVip.PickerExhausted;
                _pkNext.SetInteractable(can);
                _pkNext.SetBg(can ? Palette.BtnGreen : Palette.SlotBg);
                _pkNext.SetTextColor(can ? Palette.Title : Palette.Sub);
            }

            if (_pkStats != null) _pkStats.text = PickerStatsText();
            if (_pkNote != null) _pkNote.text = PickerNote();
        }

        /// <summary>
        /// 右卡：这个选股器的成绩单。每批都记着「哪天挑的」，用当天的价和今天的价对比，
        /// 直接告诉玩家「挑完之后涨了多少」——比一个抽象的命中率诚实。
        /// </summary>
        private static string PickerStatsText()
        {
            StringBuilder sb = new StringBuilder();
            // 每批一行：批号 + 涨幅（大字）+ 是哪天挑的。一行放得下就别占两行，
            // 右卡上下两块的高度是定死的，写超了会被 Truncate 裁掉。
            sb.Append("<color=#9EB3CC>它挑完之后，到现在涨了多少</color>\n\n");

            int used = 0;
            for (int roll = 0; roll < StockVip.PickerTotal; roll++)
            {
                int day;
                double avg;
                if (!StockVip.BatchPerf(roll, out day, out avg)) continue;
                used++;

                sb.Append("<color=#9EC7DE>第 ").Append(roll + 1).Append(" 批</color>　");
                if (day >= StockState.Today)
                {
                    sb.Append("<size=15><color=#9EB3CC>今天刚挑的，还没到结算</color></size>\n");
                    continue;
                }

                Color c = avg >= 0.0001 ? Palette.Up : (avg <= -0.0001 ? Palette.Down : Palette.Muted);
                sb.Append("<size=22><color=").Append(Palette.HexOf(c)).Append(">")
                  .Append(avg >= 0 ? "+" : "").Append(avg.ToString("0.0")).Append("%</color></size>")
                  .Append("　<size=13><color=#9EB3CC>第 ").Append(day).Append(" 天挑的 3 支</color></size>\n");
            }

            if (used == 0)
            {
                sb.Append("<size=15><color=#9EB3CC>你还没用过选股器，用掉一批之后这里就会记下它的表现。</color></size>");
                return sb.ToString();
            }
            sb.Append("\n<size=13><color=#9EB3CC>按每批 3 支等权算，不含手续费。</color></size>");
            return sb.ToString();
        }

        private static string PickerNote()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<color=#FFC24A>它是怎么挑的</color>\n");
            sb.Append("· 只在「近期方向向上」的标的里挑，一支向上的都没有时才退回全池。\n");
            sb.Append("· 挑的时候还按最近 5 天涨幅排序，")
              .Append("<color=#9EC7DE>只在前半段里抽</color> —— 涨得最猛的那几支反而不要。\n");
            sb.Append("· 所以它给的是「还能赚点小钱、但不会一夜暴富」的那种，图个稳。\n");
            // 免费工具得把话说明白：它是拿公开数据凑出来的结论，别当圣旨
            sb.Append("<color=#E5533D>· 只是根据空间站免费数据分析提供的，不一定稳定，")
              .Append("更适合不会挑的新手。</color>\n\n");
            sb.Append("<color=#FFC24A>和付费工具有什么区别</color>\n");
            sb.Append("· 付费那七项里，前六项是把你 <color=#9EC7DE>已经能看到的东西</color> 讲得更细：")
              .Append("逐笔、盘口、资金拆分、打分、回测、盯盘；")
              .Append("<color=#9EC7DE>物价雷达</color>另外把平时看不见的物价、供需和触发线画给你看。\n");
            sb.Append("· 这个选股器直接给 <color=#E5533D>结论</color>，")
              .Append("而免费的结论也就只值这个价。\n\n");
            sb.Append("<color=#E5533D>每 ").Append(StockVip.PickerCycleDays)
              .Append(" 天恢复 ").Append(StockVip.PickerTotal).Append(" 批额度。</color>\n");
            sb.Append("<color=#9EC7DE>用法建议：开局拿它挑一支，把主线任务跑顺。</color>");
            return sb.ToString();
        }

        // ── 新手任务 ──────────────────────────────────────────────────

        /// <summary>
        /// 六条任务铺满整页。老K 的报价已经搬到【好友】页，这里只剩任务，
        /// 所以行距按内容区高度摊开（小屏夹到 46，大屏最多 64），底边留一句去向提示。
        /// </summary>
        private static GameObject BuildQuest(Transform root)
        {
            GameObject page = NewPage(root, "PageQuest");

            float qw = ContentW - 48f;
            Ui.MakeCard(page.transform, "QstCard", 0f, 0f, ContentW, BodyH);
            CardTitle(page.transform, "QstTitle", 24f, 12f, "新手任务", FsPageTitle);

            float progW = Mathf.Min(604f, ContentW * 0.58f);
            _qstProg = Ui.MakeText(page.transform, "QstProg", "", FsSmall, Palette.Gold,
                Ui.AlignRight, false);
            if (_qstProg != null) Ui.Place(_qstProg.gameObject,
                ContentW - 24f - 110f - 16f - progW, 22f, progW, 22f);

            UiButton again = Ui.MakeButton(page.transform, "QstTut", "重看教程", 16f, Palette.BtnIdle,
                Palette.Sub, RestartTutorial, Ui.AlignCenter);
            if (again != null) again.Place(ContentW - 24f - 110f, 14f, 110f, 32f);

            float step = Mathf.Clamp((BodyH - 96f) / StockQuest.Count, 46f, 64f);

            // 一条任务的四段：图标 / 名字 / 说明 / 进度 + 领奖按钮，宽度全部跟着内容区摊开
            float qBtnX = ContentW - 24f - 140f;
            float qPgX = qBtnX - 14f - 150f;
            float qDsX = 274f;
            float qDsW = Mathf.Max(160f, qPgX - 16f - qDsX);
            for (int i = 0; i < StockQuest.Count; i++)
            {
                int slot = i;
                float y = 56f + i * step;
                Ui.MakeSlot(page.transform, "QstRow" + i, 24f, y, qw, 40f,
                    Palette.A(Palette.SlotBg, 0.7f));

                _qstIcon[i] = Ui.MakeText(page.transform, "QstIcon" + i, "○", FsCell,
                    Palette.Muted, Ui.AlignCenter, false);
                if (_qstIcon[i] != null) Ui.Place(_qstIcon[i].gameObject, 30f, y + 9f, 26f, 24f);

                _qstTitle[i] = Ui.MakeText(page.transform, "QstName" + i, "", FsCell,
                    Palette.Title, Ui.AlignLeft, false);
                if (_qstTitle[i] != null) Ui.Place(_qstTitle[i].gameObject, 62f, y + 9f, 200f, 24f);

                _qstDesc[i] = Ui.MakeText(page.transform, "QstDesc" + i, "", 14f,
                    Palette.Muted, Ui.AlignLeft, false);
                if (_qstDesc[i] != null) Ui.Place(_qstDesc[i].gameObject, qDsX, y + 11f, qDsW, 22f);

                _qstProgEach[i] = Ui.MakeText(page.transform, "QstPg" + i, "", 15f,
                    Palette.Cyan, Ui.AlignRight, false);
                if (_qstProgEach[i] != null) Ui.Place(_qstProgEach[i].gameObject, qPgX, y + 10f, 150f, 22f);

                UiButton btn = Ui.MakeButton(page.transform, "QstBtn" + i, "领奖", 15f,
                    Palette.BtnGreen, Palette.Title, () => DoClaimQuest(slot), Ui.AlignCenter);
                if (btn != null) btn.Place(qBtnX, y + 5f, 140f, 30f);
                _qstBtn[i] = btn;
            }

            // 老K 的报价已经搬到【好友】页，这里只在底部留一句去向提示
            TextMeshProUGUI go = Ui.MakeText(page.transform, "QstGo",
                "老K 的报价搬到【好友】页了 —— 他挂出报价时，左边导航会冒一个红色数字气泡。",
                FsSmall, Palette.Muted, Ui.AlignCenter, false);
            if (go != null) Ui.Place(go.gameObject, 24f, BodyH - 44f, qw, 24f);

            return page;
        }

        /// <summary>
        /// 新手引导：气泡 + 箭头 + 目标描边。铺满整个面板，但刻意不铺全屏遮罩
        /// ——以前一层黑纱盖下来，被讲的那一页就全看不见了（在公司财报页尤其明显）。
        /// 现在气泡只占一小块、贴着目标摆，目标描一圈金边，一眼就知道在说哪里。
        /// 最后建，保证压在其它所有元素上面。
        /// </summary>
        private static void BuildTutorial(Transform root)
        {
            _tutRoot = Ui.New("Tutorial", root);
            if (_tutRoot == null) return;
            Ui.Place(_tutRoot, 0f, 0f, W, H);

            // 灯光聚焦：四块压暗板先建（在气泡、描边下面），摆位时围着目标拼一圈，
            // 中间那块留给目标本身 —— 于是「灯」就打在要讲的东西上。
            // raycast 必须为 false：板子只负责变暗，不能把玩家要点的按钮挡住。
            for (int i = 0; i < 4; i++)
            {
                _tutDim[i] = Ui.MakeImage(_tutRoot.transform, "TutDim" + i,
                    Palette.A(Palette.DimBase, 0.55f), false);
            }

            // 目标描边：四条细边拼一个矩形。比「挖洞遮罩」稳，也不会盖住目标本身。
            for (int i = 0; i < 4; i++)
            {
                _tutEdge[i] = Ui.MakeImage(_tutRoot.transform, "TutEdge" + i,
                    Palette.A(Palette.Gold, 0.85f), false);
            }

            _tutArrow = Ui.MakeText(_tutRoot.transform, "TutArrow", "▲", 28f,
                Palette.Gold, Ui.AlignCenter, false);

            // 气泡本体。宽高每步都不一样，所以边框和纸面都留着句柄，逐帧重摆。
            _tutBubble = Ui.New("TutBubble", _tutRoot.transform);
            if (_tutBubble == null) return;
            // 纸面刻意不用 CardBg：跟页面卡片一个色的话，气泡和正文糊在一起，
            // 盯两眼就晕（玩家反馈），所以换成独立的 TutBg + 加粗的金描边。
            _tutRim = Ui.MakeSliced(_tutBubble.transform, "TutRim", Ui.Card(), Palette.TutRim, false);
            _tutFace = Ui.MakeSliced(_tutBubble.transform, "TutFace", Ui.Card(), Palette.TutBg, true);

            // 标题 22（独占一行、金色），正文 18（段落之间空一行），序号 15
            _tutTitle = Ui.MakeText(_tutBubble.transform, "TutTitle", "", FsCardTitle,
                Palette.Gold, Ui.AlignLeft, false);
            if (_tutTitle != null)
            {
                Ui.Truncate(_tutTitle);   // 标题是单行，Truncate 不会真的生效（见 Ui.Truncate）
                try { _tutTitle.fontStyle = FontStyles.Bold; } catch { }
            }

            _tutStepText = Ui.MakeText(_tutBubble.transform, "TutStep", "", FsSmall,
                Palette.Muted, Ui.AlignRight, false);
            if (_tutStepText != null) Ui.Truncate(_tutStepText);

            _tutBody = Ui.MakeText(_tutBubble.transform, "TutBody", "", FsCell,
                Palette.CardBody, Ui.AlignTopLeft, true);
            if (_tutBody != null)
            {
                Ui.Truncate(_tutBody);
                try { _tutBody.lineSpacing = 6f; } catch { }
            }

            // 进度条：一条底 + 一条填充。主线一共 12 站，玩家看不到头就容易中途跑掉，
            // 给他一条「还剩多少」的可见刻度，比任何文案都管用。
            _tutBarBg = Ui.MakeImage(_tutBubble.transform, "TutBarBg", Palette.A(Palette.SlotBg, 1f), false);
            _tutBarFill = Ui.MakeImage(_tutBubble.transform, "TutBarFill", Palette.Gold, false);

            // 底部常驻提示：一定要让新手知道「忘了可以点右上角的 ?」——
            // 否则他一走神就只剩「跳过」这一条路可走。
            _tutFoot = Ui.MakeText(_tutBubble.transform, "TutFoot", "忘了？点右上角的「?」随时重看这页",
                FsSmall, Palette.Muted, Ui.AlignLeft, false);
            if (_tutFoot != null) Ui.Truncate(_tutFoot);

            _tutSkip = Ui.MakeButton(_tutBubble.transform, "TutSkip", "跳过教程", FsSmall,
                Palette.BtnIdle, Palette.Sub, TourSkip, Ui.AlignCenter);
            _tutPrev = Ui.MakeButton(_tutBubble.transform, "TutPrev", "上一步", FsCell,
                Palette.BtnIdle, Palette.Sub, TourPrev, Ui.AlignCenter);
            _tutNext = Ui.MakeButton(_tutBubble.transform, "TutNext", "下一步", FsCell,
                Palette.BtnGreen, Palette.Title, TourNext, Ui.AlignCenter);

            Ui.SetActive(_tutRoot, false);
        }

        /// <summary>
        /// 每页的「?」说明卡：只占内容区中间一块，不是全屏盖板。
        /// 卡片底部那个「带我走一遍」才启动这一页的分步气泡（调试页没有导览）。
        /// </summary>
        private static void BuildHint(Transform root)
        {
            _hintRoot = Ui.New("PageHint", root);
            if (_hintRoot == null) return;
            Ui.Place(_hintRoot, ContentX, BodyY, ContentW, BodyH);

            // 底下一层半透明：挡住误点到下面那页，但整页仍然看得见（不是黑屏）
            Image scrim = Ui.MakeImage(_hintRoot.transform, "HintScrim", Palette.Scrim, true);
            if (scrim != null) Ui.Stretch(scrim.gameObject, 0f);

            _hintCard = Ui.New("HintCard", _hintRoot.transform);
            if (_hintCard == null) return;
            // 说明卡跟引导气泡用同一套「讲解纸」配色，一眼就能认出是引导不是页面内容
            _hintRim = Ui.MakeSliced(_hintCard.transform, "HintRim", Ui.Card(), Palette.TutRim, false);
            _hintFace = Ui.MakeSliced(_hintCard.transform, "HintFace", Ui.Card(), Palette.TutBg, true);

            _hintTitle = Ui.MakeText(_hintCard.transform, "HintTitle", "", FsPageTitle,
                Palette.Gold, Ui.AlignLeft, false);
            if (_hintTitle != null) Ui.Truncate(_hintTitle);

            _hintBody = Ui.MakeText(_hintCard.transform, "HintBody", "", FsCell,
                Palette.CardBody, Ui.AlignTopLeft, true);
            if (_hintBody != null)
            {
                Ui.Truncate(_hintBody);
                try { _hintBody.lineSpacing = 6f; } catch { }
            }

            _hintWalk = Ui.MakeButton(_hintCard.transform, "HintWalk", "带我走一遍", FsCell,
                Palette.BtnGreen, Palette.Title, HintWalk, Ui.AlignCenter);
            _hintOk = Ui.MakeButton(_hintCard.transform, "HintOk", "知道了", FsCell,
                Palette.BtnIdle, Palette.Sub, CloseHint, Ui.AlignCenter);

            Ui.SetActive(_hintRoot, false);
        }

        // ── 资金划转 ──────────────────────────────────────────────────

        private static GameObject BuildFund(Transform root)
        {
            GameObject page = NewPage(root, "PageFund");
            Ui.MakeCard(page.transform, "FCard", 0f, 0f, ContentW, BodyH);
            CardTitle(page.transform, "FTitle", 24f, 12f, "资金划转", FsPageTitle);

            // 左右两个钱包卡各占一半，中间留 18 的缝
            float halfW = (ContentW - 48f - 18f) * 0.5f;
            Ui.MakeSlot(page.transform, "FCashSlot", 24f, 56f, halfW, 96f, Palette.SlotBg);
            TextMeshProUGUI cl = Ui.MakeText(page.transform, "FCashLab", "店铺现金", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (cl != null) Ui.Place(cl.gameObject, 38f, 68f, halfW - 28f, 18f);
            _fdCash = Ui.MakeText(page.transform, "FCashVal", "-", FsPageTitle, Palette.Gold,
                Ui.AlignLeft, false);
            if (_fdCash != null) Ui.Place(_fdCash.gameObject, 38f, 90f, halfW - 28f, 36f);

            float px = 24f + halfW + 18f;
            Ui.MakeSlot(page.transform, "FPoolSlot", px, 56f, halfW, 96f, Palette.SlotBg);
            TextMeshProUGUI pl = Ui.MakeText(page.transform, "FPoolLab", "股票账户", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (pl != null) Ui.Place(pl.gameObject, px + 14f, 68f, halfW - 28f, 18f);
            _fdPool = Ui.MakeText(page.transform, "FPoolVal", "-", FsPageTitle, Palette.Cyan,
                Ui.AlignLeft, false);
            if (_fdPool != null) Ui.Place(_fdPool.gameObject, px + 14f, 90f, halfW - 28f, 36f);

            // 划转金额：可以直接敲数字，也可以用下面的步进按钮快速调整
            TextMeshProUGUI amtLab = Ui.MakeText(page.transform, "FAmtLab", "① 划转金额", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (amtLab != null) Ui.Place(amtLab.gameObject, 24f, 180f, 130f, 24f);

            _fdAmountInput = Ui.MakeInput(page.transform, "FAmount", "金额", 18f,
                Palette.SlotBg, Palette.Title, Palette.Muted, OnFundAmountChanged);
            if (_fdAmountInput != null) Ui.Place(_fdAmountInput.gameObject, 160f, 174f, 180f, 30f);

            TextMeshProUGUI amtUnit = Ui.MakeText(page.transform, "FAmtUnit", "元", FsSmall,
                Palette.Muted, Ui.AlignLeft, false);
            if (amtUnit != null) Ui.Place(amtUnit.gameObject, 348f, 180f, 30f, 24f);

            TextMeshProUGUI amtHint = Ui.MakeText(page.transform, "FAmtHint",
                "可直接输入任意整数（最少 1 元）", FsSmall, Palette.Muted, Ui.AlignLeft, false);
            if (amtHint != null) Ui.Place(amtHint.gameObject, 390f, 180f, ContentW - 414f, 24f);

            string[] steps = { "-100", "-10", "+10", "+100" };
            int[] deltas = { -100, -10, 10, 100 };
            float fStepW = Mathf.Min((ContentW - 48f - 3f * 14f) / 4f, 260f);
            for (int i = 0; i < 4; i++)
            {
                int delta = deltas[i];
                UiButton b = Ui.MakeButton(page.transform, "FStep" + i, steps[i], FsCell,
                    Palette.BtnIdle, Palette.Sub, () => AddFundAmount(delta), Ui.AlignCenter);
                if (b != null) b.Place(24f + i * (fStepW + 14f), 212f, fStepW, 42f);
            }

            _fdIn = Ui.MakeButton(page.transform, "FIn", "", FsCell, Palette.BtnGreen,
                Palette.Title, () => DoTransfer(true, _fundAmount), Ui.AlignCenter);
            if (_fdIn != null) _fdIn.Place(24f, 268f, halfW, 46f);
            _fdOut = Ui.MakeButton(page.transform, "FOut", "", FsCell, Palette.BtnOrange,
                Palette.Title, () => DoTransfer(false, _fundAmount), Ui.AlignCenter);
            if (_fdOut != null) _fdOut.Place(px, 268f, halfW, 46f);

            UiButton allIn = Ui.MakeButton(page.transform, "FAllIn", "全部转入股市", FsCell,
                Palette.BtnIdle, Palette.Sub, () => DoTransfer(true, -1), Ui.AlignCenter);
            if (allIn != null) allIn.Place(24f, 328f, halfW, 46f);
            UiButton allOut = Ui.MakeButton(page.transform, "FAllOut", "全部提现到店铺", FsCell,
                Palette.BtnIdle, Palette.Sub, () => DoTransfer(false, -1), Ui.AlignCenter);
            if (allOut != null) allOut.Place(px, 328f, halfW, 46f);

            _fdHint = Ui.MakeText(page.transform, "FHint", "", 17f, Palette.Muted, Ui.AlignTopLeft, true);
            if (_fdHint != null) Ui.Place(_fdHint.gameObject, 24f, 392f, ContentW - 48f,
                Mathf.Max(120f, BodyH - 392f - 20f));
            return page;
        }

        // ── 黑市开户 ──────────────────────────────────────────────────

        private static GameObject BuildLicense(Transform root)
        {
            GameObject page = NewPage(root, "PageLicense");
            Ui.MakeCard(page.transform, "LCard", 0f, 0f, ContentW, BodyH);
            CardTitle(page.transform, "LTitle", 24f, 12f, "黑市开户", FsPageTitle);

            // 文字块尽量高，但内容就六行，撑太高反而空，所以封顶 420；
            // 开户按钮紧贴在文字块下面，不钉死在某个写死的 y 上。
            float lcBuyY = 56f + Mathf.Min(BodyH - 56f - 20f - 70f - 24f, 420f) + 24f;
            _lcInfo = Ui.MakeText(page.transform, "LInfo", "", FsBody, Palette.CardBody,
                Ui.AlignTopLeft, true);
            if (_lcInfo != null) Ui.Place(_lcInfo.gameObject, 24f, 56f, ContentW - 48f, lcBuyY - 56f - 24f);

            _lcBuy = Ui.MakeButton(page.transform, "LBuy", "", FsCardTitle, Palette.BtnBlue,
                Palette.Title, DoBuyLicense, Ui.AlignCenter);
            if (_lcBuy != null) _lcBuy.Place(24f, lcBuyY, ContentW - 48f, 70f);
            return page;
        }

        // ── 调试工具 ──────────────────────────────────────────────────

        /// <summary>调试页按钮：两列排布，左列偏「推进」，右列偏「加料 / 重置」。</summary>
        private static GameObject BuildDebug(Transform root)
        {
            GameObject page = NewPage(root, "PageDebug");
            Ui.MakeCard(page.transform, "DCard", 0f, 0f, ContentW, BodyH);
            CardTitle(page.transform, "DTitle", 24f, 12f, "调试工具", FsPageTitle);

            TextMeshProUGUI tip = Ui.MakeText(page.transform, "DTip",
                "这些按钮只影响当前存档，用来手动验证结算与走势，正式玩的时候别点。",
                FsSmall, Palette.Muted, Ui.AlignLeft, false);
            if (tip != null) Ui.Place(tip.gameObject, 24f, 50f, ContentW - 48f, 20f);

            string[] texts =
            {
                "快进 1 天", "快进 7 天（一个周期）", "触发随机事件",
                "清空所有事件", "股票账户 +1000 元", "店铺现金 +1000 元",
                "重置股票账目", "打印状态到日志", "老K 立刻报价",
                "任务全部标记完成", "盘中快进 8 段", "立刻收工（撤单）",
                "清空挂单", "开通全部高级工具", "好友来消息"
            };
            Action[] handlers =
            {
                () => DbgAdvance(1), () => DbgAdvance(StockDefs.CycleDays), DbgForceEvent,
                DbgClearEvents, DbgAddPool, DbgAddCash,
                DbgReset, DbgDump, DbgFriendOffer,
                DbgFinishQuests, DbgIntraSkip, DbgIntraClose,
                DbgOrderClear, DbgUnlockVip, DbgFriendPing
            };
            Color[] colors =
            {
                Palette.BtnGreen, Palette.BtnGreen, Palette.BtnBlue,
                Palette.BtnIdle, Palette.BtnBlue, Palette.BtnBlue,
                Palette.BtnRed, Palette.BtnIdle, Palette.BtnBlue,
                Palette.BtnGreen, Palette.BtnGreen, Palette.BtnOrange,
                Palette.BtnIdle, Palette.BtnGreen, Palette.BtnBlue
            };
            // 按钮列数跟着内容区走：窄屏三列，宽屏四列（15 个按钮刚好铺满 4 行），
            // 省下来的高度全给下面的读数区。
            int dbgCols = ContentW >= 980f ? 4 : 3;
            float bw = Mathf.Min((ContentW - 48f - (dbgCols - 1) * 14f) / dbgCols, 360f);
            int dbgRows = (texts.Length + dbgCols - 1) / dbgCols;
            for (int i = 0; i < texts.Length; i++)
            {
                float x = 24f + (i % dbgCols) * (bw + 14f);
                float y = 76f + (i / dbgCols) * 44f;
                UiButton b = Ui.MakeButton(page.transform, "DBtn" + i, texts[i], FsCell,
                    colors[i], Palette.Title, handlers[i], Ui.AlignCenter);
                if (b != null) b.Place(x, y, bw, 40f);
            }

            // 读数分左右两栏：左=账户 / 事件 / 席位，右=股评胜率 / 涨跌榜。
            // 高度按卡片实高算，再加一层 Truncate 兜底，绝不再画到卡片外面。
            float dTextY = 76f + dbgRows * 44f + 10f;
            float dTextH = BodyH - dTextY - 16f;
            float dHalf = (ContentW - 72f) * 0.5f;
            _dbgText = Ui.MakeText(page.transform, "DText", "", 15f, Palette.CardBody,
                Ui.AlignTopLeft, true);
            if (_dbgText != null)
            {
                Ui.Place(_dbgText.gameObject, 24f, dTextY, dHalf, dTextH);
                Ui.Truncate(_dbgText);
            }
            _dbgTextR = Ui.MakeText(page.transform, "DTextR", "", 15f, Palette.CardBody,
                Ui.AlignTopLeft, true);
            if (_dbgTextR != null)
            {
                Ui.Place(_dbgTextR.gameObject, 24f + dHalf + 24f, dTextY, dHalf, dTextH);
                Ui.Truncate(_dbgTextR);
            }
            return page;
        }

        /// <summary>快进若干天。走的是和日结算完全相同的入口，避免调试路径和真实路径算出两套数。</summary>
        private static void DbgAdvance(int days)
        {
            try
            {
                LogOp("调试：快进 " + days + " 天");
                PlayerStore store = PlayerStore.instance;
                if (store == null) { SetStatus("尚未进入存档，无法快进。", true); Refresh(); return; }
                StockState.EnsureLoaded(store);

                string notice = string.Empty;
                for (int i = 0; i < days; i++)
                {
                    string n = StockEngine.DailyTick();
                    if (!string.IsNullOrEmpty(n)) notice = n.Trim();
                }
                SetStatus("已快进 " + days + " 天，当前第 " + StockState.Today + " 天。"
                    + (notice.Length > 0 ? " " + notice : ""), false);
            }
            catch (Exception ex)
            {
                SetStatus("快进失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void DbgForceEvent()
        {
            try
            {
                LogOp("调试：触发随机事件");
                PlayerStore store = PlayerStore.instance;
                if (store == null) { SetStatus("尚未进入存档。", true); Refresh(); return; }
                StockState.EnsureLoaded(store);

                string notice = StockEngine.ForceEvent();
                SetStatus(notice.Length > 0
                    ? "已触发：" + notice.Trim() + "（下个交易日生效）"
                    : "事件表为空，没有可触发的事件。", notice.Length == 0);
            }
            catch (Exception ex)
            {
                SetStatus("触发事件失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void DbgClearEvents()
        {
            LogOp("调试：清空所有事件");
            StockState.Events.Clear();
            StockState.LastEventDay = StockState.Today;
            StockState.Dirty = true;
            SetStatus("已清空当前所有行情事件。", false);
            Refresh();
        }

        private static void DbgAddPool()
        {
            LogOp("调试：股票账户 +1000 元");
            StockState.Pool += StockState.ToCents(1000);
            StockState.Dirty = true;
            SetStatus("股票账户已加 1000 元测试金。", false);
            Refresh();
        }

        private static void DbgAddCash()
        {
            LogOp("调试：店铺现金 +1000 元");
            PlayerStore store = PlayerStore.instance;
            if (store == null) { SetStatus("尚未进入存档。", true); Refresh(); return; }
            store.playerCash += 1000;
            SetStatus("店铺现金已加 1000 元。", false);
            Refresh();
        }

        private static void DbgReset()
        {
            LogOp("调试：重置股票账目");
            // 走 Clear 而不是裸调 ResetToDefaults：Clear 会把重置后的状态立刻刷进 modData，
            // 否则内存里是新的、存档里还是旧的，下一次 EnsureLoaded 又给读回来
            StockState.Clear(PlayerStore.instance);
            SetStatus("股票账目已重置：价格回到基价、持仓与历史清空。", false);
            Refresh();
        }

        private static void DbgDump()
        {
            LogOp("调试：打印状态到日志");
            Core.Log.Msg(StockEngine.DebugDump());
            SetStatus("当前状态已打印到 MelonLoader 的 Latest.log。", false);
            Refresh();
        }

        // ── 新手任务 + 老K + 新手引导 ─────────────────────────────────

        private static void RefreshQuest()
        {
            StockQuest.Sync();

            int done = 0;
            for (int i = 0; i < StockQuest.Count; i++)
            {
                if (StockQuest.IsDone(i)) done++;
            }
            if (_qstProg != null)
            {
                int claimable = StockQuest.ClaimableCount();
                _qstProg.text = claimable > 0
                    ? "已完成 " + done + " / " + StockQuest.Count + "　<color=#FFC24A>可领 " + claimable + " 项</color>"
                    : "已完成 " + done + " / " + StockQuest.Count + "　已领 " + StockQuest.ClaimedCount() + " 项";
            }

            for (int i = 0; i < StockQuest.Count; i++)
            {
                bool ok = StockQuest.IsDone(i);
                bool claimed = StockQuest.IsClaimed(i);

                if (_qstIcon[i] != null)
                {
                    // 只用 ★ / ☆（游戏字体一定有），状态靠颜色区分：金=可领，绿=已领，灰=未完成
                    _qstIcon[i].text = ok ? "★" : "☆";
                    _qstIcon[i].color = claimed ? Palette.Down : (ok ? Palette.Gold : Palette.Muted);
                }
                if (_qstTitle[i] != null)
                {
                    _qstTitle[i].text = StockQuest.All[i].Title;
                    _qstTitle[i].color = ok && !claimed ? Palette.Title : Palette.Sub;
                }
                if (_qstDesc[i] != null)
                {
                    _qstDesc[i].text = StockQuest.All[i].Desc;
                    _qstDesc[i].color = ok && !claimed ? Palette.CardBody : Palette.Muted;
                }
                if (_qstProgEach[i] != null)
                {
                    _qstProgEach[i].text = StockQuest.Progress(i);
                    _qstProgEach[i].color = ok && !claimed ? Palette.Gold : Palette.Muted;
                }
                if (_qstBtn[i] != null)
                {
                    _qstBtn[i].SetText(claimed ? "已领" : (ok ? "领奖 +" + StockQuest.All[i].Reward : "未完成"));
                    SetBtnEnabled(_qstBtn[i], ok && !claimed, Palette.BtnGreen, true);
                }
            }
        }

        /// <summary>
        /// 按钮的可用态默认只是视觉：真点了会走到各自的处理里给出「现在没报价」之类的提示。
        /// hard=true 时才真正把交互关掉——领奖这种「一次性」按钮必须用 hard，
        /// 否则领完按钮看着是灰的却还能点，玩家会以为奖励能重复领。
        /// </summary>
        private static void SetBtnEnabled(UiButton b, bool on, Color baseColor, bool hard = false)
        {
            if (b == null) return;
            b.SetBg(on ? baseColor : Palette.Disabled);
            b.SetTextColor(on ? Palette.Title : Palette.DisabledFg);
            if (hard) b.SetInteractable(on);
        }

        private static void DoClaimQuest(int index)
        {
            LogOp("领取任务奖励：" + StockQuest.All[index].Title);
            string err = StockQuest.Claim(index);
            if (err != null)
            {
                SetStatus("[任务] " + err, true);
                Refresh();
                return;
            }
            SetStatus("[任务] 已领取「" + StockQuest.All[index].Title + "」奖励 "
                + StockQuest.All[index].Reward + " 元，已进股票账户。", false);
            Refresh();
        }

        private static void DoFriendAccept()
        {
            LogOp("接受老K 的报价");
            string err = StockFriend.Accept();
            if (err != null)
            {
                SetStatus("[老K] " + err, true);
                Refresh();
                return;
            }
            StockChat.Push("k", true, "行，就按这个价，成交。");
            StockChat.NoteOffer();
            StockChat.MarkRead(StockChat.Get("k"));
            SetStatus("[老K] 成交。他记下这笔账，过几天再来找你。", false);
            Refresh();
        }

        private static void DoFriendCounter()
        {
            if (!StockChat.Online)
            {
                SetStatus("[老K] 你现在是离线状态，只能接受或拒绝，切回在线才能还价。", true);
                Refresh();
                return;
            }
            LogOp("向老K 还价");
            string message;
            StockFriend.Counter(out message);
            StockChat.Push("k", false, message);
            StockChat.NoteOffer();
            StockChat.MarkRead(StockChat.Get("k"));
            SetStatus("[老K] " + message, false);
            Refresh();
        }

        private static void DoFriendReject()
        {
            LogOp("拒绝老K 的报价");
            string err = StockFriend.Reject();
            if (err != null)
            {
                SetStatus("[老K] " + err, true);
                Refresh();
                return;
            }
            StockChat.Push("k", true, "这单算了，我等等看。");
            StockChat.NoteOffer();
            StockChat.MarkRead(StockChat.Get("k"));
            SetStatus("[老K] 你拒绝了这单，他过几天再来。", false);
            Refresh();
        }

        // ── 好友页（聊天软件式：左栏我的头像 + 好友列表，右栏对话）──────

        /// <summary>圆形色块。Circle 精灵必须走 Simple —— 九宫格拉伸会把圆压成方块。</summary>
        private static Image MakeCircle(Transform parent, string name, Color color)
        {
            Image img = Ui.MakeImage(parent, name, color, false);
            if (img == null) return null;
            Sprite sp = Ui.Circle();
            if (sp != null)
            {
                img.sprite = sp;
                try { img.type = Image.Type.Simple; } catch { }
            }
            return img;
        }

        /// <summary>整段文字里最长一行的半角宽度（汉字算 2，富文本标签跳过）。气泡该多宽按它估。</summary>
        private static int MaxLineWidth(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int max = 0, cur = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<')
                {
                    int e = s.IndexOf('>', i);
                    if (e > i) { i = e; continue; }
                }
                if (c == '\n')
                {
                    if (cur > max) max = cur;
                    cur = 0;
                    continue;
                }
                cur += c < 128 ? 1 : 2;
            }
            return cur > max ? cur : max;
        }

        /// <summary>头像圆里的那个字（取名字第一个字）。</summary>
        private static string AvaChar(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            return name.Substring(0, 1);
        }

        private static GameObject BuildChat(Transform root)
        {
            GameObject page = NewPage(root, "PageFriend");

            float lw = Mathf.Clamp(ContentW * 0.34f, 340f, 460f);
            float rw = ContentW - lw - 12f;
            float rx = lw + 12f;
            Ui.MakeCard(page.transform, "ChCardL", 0f, 0f, lw, BodyH);
            Ui.MakeCard(page.transform, "ChCardR", rx, 0f, rw, BodyH);

            // ── 左栏顶上：我的头像 + 在线状态点 ──
            float ava = 64f;
            Image meAva = MakeCircle(page.transform, "ChMeAva", Palette.Hex("2E6C93"));
            if (meAva != null) Ui.Place(meAva.gameObject, 18f, 16f, ava, ava);
            TextMeshProUGUI meTx = Ui.MakeText(page.transform, "ChMeAvaTx", "我", FsCardTitle,
                Palette.Hex("FFFFFF"), Ui.AlignCenter, false);
            if (meTx != null) Ui.Place(meTx.gameObject, 18f, 16f, ava, ava);

            TextMeshProUGUI meName = Ui.MakeText(page.transform, "ChMeName", "你（店主）", FsBody,
                Palette.Title, Ui.AlignLeft, false);
            if (meName != null) Ui.Place(meName.gameObject, 94f, 20f, lw - 112f, 26f);

            _chMeStatus = Ui.MakeText(page.transform, "ChMeStatus", "", FsSmall, Palette.Muted,
                Ui.AlignLeft, false);
            if (_chMeStatus != null) Ui.Place(_chMeStatus.gameObject, 94f, 48f, lw - 112f, 22f);

            // 状态点：绿=在线 / 灰=离线，点一下切换。头像正下方，一眼看得见
            _chMeDot = Ui.MakeButton(page.transform, "ChMeDot", "", FsSmall, Palette.Down,
                Palette.Title, ToggleOnline, Ui.AlignCenter, Ui.Circle());
            if (_chMeDot != null) _chMeDot.Place(18f + (ava - 28f) * 0.5f, 16f + ava + 4f, 28f, 28f);

            TextMeshProUGUI dotHint = Ui.MakeText(page.transform, "ChMeDotHint",
                "点这个圆点切在线 / 离线", FsSmall, Palette.Muted, Ui.AlignLeft, false);
            if (dotHint != null) Ui.Place(dotHint.gameObject, 18f + ava + 6f, 16f + ava + 8f, lw - ava - 44f, 20f);

            Ui.MakeSlot(page.transform, "ChDivL", 14f, 120f, lw - 28f, 2f, Palette.A(Palette.Divider, 0.5f));

            _chListInfo = Ui.MakeText(page.transform, "ChListInfo", "", FsSmall, Palette.Gold,
                Ui.AlignLeft, false);
            if (_chListInfo != null) Ui.Place(_chListInfo.gameObject, 18f, 128f, lw - 36f, 20f);

            // ── 好友列表 ──
            // 行照微信的排法：左边头像，中间「名字 + 最后一句」，右边「天数 + 未读气泡」。
            // 名字和预览都是单行文字，绝不能开 TMP 的 Truncate —— 单行内容一旦超宽，
            // Truncate 会让整段一个字都不画，行里就只剩一个头像圆圈
            // （见 问题截图/优化过后左边太空了 可以做出微信的感觉.png）。
            // 所以宽度自己用 Ui.Cut() 先截短。
            int n = StockChat.All.Length;
            float y0 = 154f;
            float step = Mathf.Clamp((BodyH - 168f) / n, 44f, 62f);
            float rowH = step - 6f;
            float aS = Mathf.Min(40f, rowH - 8f);            // 头像
            float txX = 22f + aS + 12f;                      // 名字 / 预览的起始 x
            const float rightW = 56f;                        // 右边「天数 + 气泡」那一列
            float rightX = lw - 12f - rightW;
            float nameW = Mathf.Max(90f, rightX - txX - 6f);
            float badgeW = 24f;
            float badgeX = rightX + (rightW - badgeW) * 0.5f;
            for (int i = 0; i < n; i++)
            {
                int slot = i;
                float y = y0 + i * step;
                UiButton row = Ui.MakeButton(page.transform, "ChRow" + i, "", FsCell,
                    Palette.A(Palette.SlotBg, 0.75f), Palette.CardBody, () => PickChat(slot), Ui.AlignLeft);
                if (row != null)
                {
                    row.Place(12f, y, lw - 24f, rowH);
                    _chRow[i] = row;
                }

                _chRowAva[i] = MakeCircle(page.transform, "ChRowAva" + i, Palette.Muted);
                if (_chRowAva[i] != null) Ui.Place(_chRowAva[i].gameObject, 22f, y + (rowH - aS) * 0.5f, aS, aS);
                _chRowAvaTx[i] = Ui.MakeText(page.transform, "ChRowAvaTx" + i, "", FsCell,
                    Palette.Hex("FFFFFF"), Ui.AlignCenter, false);
                if (_chRowAvaTx[i] != null) Ui.Place(_chRowAvaTx[i].gameObject, 22f, y + (rowH - aS) * 0.5f, aS, aS);

                _chRowName[i] = Ui.MakeText(page.transform, "ChRowName" + i, "", FsCell,
                    Palette.CardBody, Ui.AlignLeft, false);
                if (_chRowName[i] != null)
                    Ui.Place(_chRowName[i].gameObject, txX, y + rowH * 0.5f - 23f, nameW, 24f);

                _chRowPrev[i] = Ui.MakeText(page.transform, "ChRowPrev" + i, "", 14f,
                    Palette.Muted, Ui.AlignLeft, false);
                if (_chRowPrev[i] != null)
                    Ui.Place(_chRowPrev[i].gameObject, txX, y + rowH * 0.5f + 1f, nameW, 20f);

                _chRowTime[i] = Ui.MakeText(page.transform, "ChRowTime" + i, "", 13f,
                    Palette.Muted, Ui.AlignRight, false);
                if (_chRowTime[i] != null)
                    Ui.Place(_chRowTime[i].gameObject, rightX, y + rowH * 0.5f - 21f, rightW, 18f);

                _chRowBadge[i] = MakeCircle(page.transform, "ChRowBadge" + i, Palette.Up);
                if (_chRowBadge[i] != null)
                    Ui.Place(_chRowBadge[i].gameObject, badgeX, y + rowH * 0.5f + 2f, badgeW, badgeW);
                _chRowBadgeTx[i] = Ui.MakeText(page.transform, "ChRowBadgeTx" + i, "", 14f,
                    Palette.Hex("FFFFFF"), Ui.AlignCenter, false);
                if (_chRowBadgeTx[i] != null)
                    Ui.Place(_chRowBadgeTx[i].gameObject, badgeX, y + rowH * 0.5f + 2f, badgeW, badgeW);
            }

            // ── 右栏：对话头 + 气泡 + 底部按钮 ──
            float hAva = 46f;
            _chHeadAva = MakeCircle(page.transform, "ChHeadAva", Palette.Muted);
            if (_chHeadAva != null) Ui.Place(_chHeadAva.gameObject, rx + 18f, 14f, hAva, hAva);
            _chHeadAvaTx = Ui.MakeText(page.transform, "ChHeadAvaTx", "", FsBody,
                Palette.Hex("FFFFFF"), Ui.AlignCenter, false);
            if (_chHeadAvaTx != null) Ui.Place(_chHeadAvaTx.gameObject, rx + 18f, 14f, hAva, hAva);

            _chHeadName = Ui.MakeText(page.transform, "ChHeadName", "", FsCardTitle,
                Palette.Title, Ui.AlignLeft, false);
            if (_chHeadName != null)
                Ui.Place(_chHeadName.gameObject, rx + 76f, 16f, rw - 76f - 18f - 240f, 26f);

            _chHeadRel = Ui.MakeText(page.transform, "ChHeadRel", "", FsSmall, Palette.Cyan,
                Ui.AlignRight, false);
            if (_chHeadRel != null) Ui.Place(_chHeadRel.gameObject, rx + rw - 18f - 236f, 20f, 236f, 22f);

            _chHeadSub = Ui.MakeText(page.transform, "ChHeadSub", "", FsSmall, Palette.Muted,
                Ui.AlignLeft, false);
            if (_chHeadSub != null)
            {
                Ui.Place(_chHeadSub.gameObject, rx + 76f, 44f, rw - 76f - 36f, 22f);
                Ui.Truncate(_chHeadSub);
            }

            Ui.MakeSlot(page.transform, "ChDivR", rx + 14f, 70f, rw - 28f, 2f, Palette.A(Palette.Divider, 0.5f));

            // 气泡：先全建出来，刷新时按「从最新一条往上摆」逐个摆位，摆不下的藏起来
            for (int i = 0; i < ChatRows; i++)
            {
                _chBubble[i] = Ui.MakeSliced(page.transform, "ChBub" + i, Ui.Chip(), Palette.SlotBg, false);
                _chBubbleTx[i] = Ui.MakeText(page.transform, "ChBubTx" + i, "", 16f,
                    Palette.CardBody, Ui.AlignTopLeft, true);
                if (_chBubbleTx[i] != null) Ui.Truncate(_chBubbleTx[i]);
            }

            float barY = BodyH - 18f - 46f;
            _chHint = Ui.MakeText(page.transform, "ChHint", "", FsSmall, Palette.Muted,
                Ui.AlignLeft, false);
            if (_chHint != null)
            {
                Ui.Place(_chHint.gameObject, rx + 18f, barY - 30f, rw - 36f, 24f);
                Ui.Truncate(_chHint);
            }

            _chAccept = Ui.MakeButton(page.transform, "ChAccept", "接受", FsCell, Palette.BtnGreen,
                Palette.Title, DoChatAccept, Ui.AlignCenter);
            _chCounter = Ui.MakeButton(page.transform, "ChCounter", "还价", FsCell, Palette.BtnBlue,
                Palette.Title, DoChatCounter, Ui.AlignCenter);
            _chReject = Ui.MakeButton(page.transform, "ChReject", "拒绝", FsCell, Palette.BtnOrange,
                Palette.Title, DoChatReject, Ui.AlignCenter);

            // 闲聊好友：自己敲一句发出去，不再从固定几句里随机抽
            const float sendW = 96f;
            _chSay = Ui.MakeInput(page.transform, "ChSay", "想说什么，自己敲一句…", FsCell,
                Palette.SlotBg, Palette.Title, Palette.Muted, null);
            if (_chSay != null)
            {
                Ui.Place(_chSay.gameObject, rx + 18f, barY, rw - 36f - sendW - 10f, 46f);
                try
                {
                    Action<string> cb = OnChatSubmit;
                    _chSay.onSubmit.RemoveAllListeners();
                    _chSay.onSubmit.AddListener(DelegateSupport.ConvertDelegate<UnityAction<string>>(cb));
                }
                catch (Exception ex) { Core.Debug("[界面] 绑定回车发送失败：" + ex.Message); }
            }
            _chSend = Ui.MakeButton(page.transform, "ChSend", "发送", FsCell, Palette.BtnGreen,
                Palette.Title, DoChatSend, Ui.AlignCenter);
            if (_chSend != null) _chSend.Place(rx + rw - 18f - sendW, barY, sendW, 46f);

            return page;
        }

        private static ChatFriend PickedFriend()
        {
            ChatFriend[] all = StockChat.All;
            if (_chatPick < 0 || _chatPick >= all.Length) _chatPick = 0;
            return all[_chatPick];
        }

        private static void RefreshChat()
        {
            ChatFriend[] all = StockChat.All;
            if (_chatPick < 0 || _chatPick >= all.Length) _chatPick = 0;
            ChatFriend p = all[_chatPick];
            // 玩家正看着这条对话，就先算读过了，下面画列表时红点直接消失
            if (p.Unread > 0) StockChat.MarkRead(p);

            // ── 我的状态 ──
            if (_chMeDot != null) _chMeDot.SetBg(StockChat.Online ? Palette.Down : Palette.Disabled);
            if (_chMeStatus != null)
            {
                _chMeStatus.text = StockChat.Online ? "在线 · 可以还价 / 砍价" : "离线 · 只能接受或拒绝";
                _chMeStatus.color = StockChat.Online ? Palette.Down : Palette.Muted;
            }
            if (_chListInfo != null)
            {
                int un = StockChat.UnreadTotal();
                _chListInfo.text = un > 0
                    ? "好友 " + all.Length + " 位　<color=#E5533D>未读 " + un + " 条</color>"
                    : "好友 " + all.Length + " 位　消息都看完了";
            }

            // ── 好友列表 ──
            // 宽度必须跟 BuildChat 里摆的时候一模一样，否则截出来的字和格子对不上
            float rlw = Mathf.Clamp(ContentW * 0.34f, 340f, 460f);
            float rStep = Mathf.Clamp((BodyH - 168f) / all.Length, 44f, 62f);
            float rRowH = rStep - 6f;
            float rAva = Mathf.Min(40f, rRowH - 8f);
            float rTxX = 22f + rAva + 12f;
            const float rRightW = 56f;
            float rNameW = Mathf.Max(90f, rlw - 12f - rRightW - rTxX - 6f);
            for (int i = 0; i < all.Length; i++)
            {
                ChatFriend f = all[i];
                bool picked = i == _chatPick;
                UiButton row = _chRow[i];
                if (row != null)
                {
                    row.SetBg(picked ? Palette.NavOn : Palette.A(Palette.SlotBg, 0.75f));
                    row.SetTextColor(picked ? Palette.Title : Palette.CardBody);
                }

                if (_chRowAva[i] != null) _chRowAva[i].color = Palette.Hex(f.ColorHex);
                if (_chRowAvaTx[i] != null) _chRowAvaTx[i].text = AvaChar(f.Name);

                ChatLine last = f.Log.Count > 0 ? f.Log[f.Log.Count - 1] : null;

                if (_chRowName[i] != null)
                {
                    // 名字很短，宽度基本都留给小一号的标签；标签放不下就截标签，
                    // 名字本身放不下才截名字（都是自己算宽度，不用 TMP 的 Truncate）
                    int room = Mathf.Max(6, (int)(rNameW / (FsCell * 0.5f)));
                    string nm = Ui.Cut(f.Name, FsCell, rNameW * 0.45f);
                    int tagRoom = room - MaxLineWidth(nm) - 1;
                    string tag = tagRoom >= 6 ? Ui.Cut(f.Tag, 11f, tagRoom * 5.5f) : "";
                    _chRowName[i].text = tag.Length == 0 ? nm
                        : nm + " <size=11><color=" + Palette.HexOf(Palette.Muted) + ">"
                          + tag + "</color></size>";
                    _chRowName[i].color = picked ? Palette.Title : Palette.CardBody;
                }

                if (_chRowPrev[i] != null)
                {
                    string prev = last == null ? "还没有消息"
                        : (last.Mine ? "你：" : "") + last.Text;
                    _chRowPrev[i].text = Ui.Cut(prev, 14f, rNameW);
                    _chRowPrev[i].color = f.Unread > 0 ? Palette.CardBody : Palette.Muted;
                }

                if (_chRowTime[i] != null)
                {
                    int gap = last == null ? -1 : StockState.Today - last.Day;
                    _chRowTime[i].text = last == null ? ""
                        : gap <= 0 ? "今天" : gap == 1 ? "昨天" : "第" + last.Day + "天";
                }

                bool unread = f.Unread > 0;
                Ui.SetActive(_chRowBadge[i] != null ? _chRowBadge[i].gameObject : null, unread);
                Ui.SetActive(_chRowBadgeTx[i] != null ? _chRowBadgeTx[i].gameObject : null, unread);
                if (unread && _chRowBadgeTx[i] != null)
                    _chRowBadgeTx[i].text = f.Unread > 9 ? "9+" : f.Unread.ToString();
            }

            // ── 右栏对话 ──
            // 两栏宽度先定下来：下面截副标题要用 rw
            float lw = Mathf.Clamp(ContentW * 0.34f, 340f, 460f);
            float rw = ContentW - lw - 12f;
            float rx = lw + 12f;
            if (_chHeadAva != null) _chHeadAva.color = Palette.Hex(p.ColorHex);
            if (_chHeadAvaTx != null) _chHeadAvaTx.text = AvaChar(p.Name);
            if (_chHeadName != null) _chHeadName.text = p.Name;
            if (_chHeadSub != null)
                _chHeadSub.text = Ui.Cut(p.Tag + "　" + p.Note, FsSmall, rw - 76f - 36f);
            if (_chHeadRel != null)
            {
                if (p.Kind == StockChat.KindInfo)
                {
                    _chHeadRel.text = "靠谱度 " + StockChat.RelStars(p);
                    _chHeadRel.color = Palette.Gold;
                }
                else
                {
                    _chHeadRel.text = p.Kind == StockChat.KindTrade ? "场外交易 · 免手续费" : "闲聊 · 不卖消息";
                    _chHeadRel.color = Palette.Cyan;
                }
            }

            float barY = BodyH - 18f - 46f;
            float areaTop = 82f;
            float areaBot = barY - 34f;
            float maxW = Mathf.Max(200f, rw * 0.62f);

            // 从最新一条往上摆：像聊天软件那样，最新的永远贴着输入区
            float y = areaBot;
            int shown = 0;
            for (int n = p.Log.Count - 1; n >= 0 && shown < ChatRows; n--)
            {
                ChatLine cl = p.Log[n];
                const float fs = 16f;
                int mw = MaxLineWidth(cl.Text);
                float bw = Mathf.Clamp(mw * fs * 0.5f + 32f, 150f, maxW);
                int lines = EstLines(cl.Text, Mathf.Max(8, (int)((bw - 32f) / (fs * 0.5f))));
                float bh = lines * (fs + 8f) + 18f;
                if (y - bh < areaTop) break;
                y -= bh;

                float bx = cl.Mine ? rx + rw - 18f - bw : rx + 18f;
                Image bg = _chBubble[shown];
                if (bg != null)
                {
                    Ui.SetActive(bg.gameObject, true);
                    Ui.Place(bg.gameObject, bx, y, bw, bh);
                    bg.color = cl.Mine ? Palette.NavOn : Palette.SlotBg;
                }
                TextMeshProUGUI tx = _chBubbleTx[shown];
                if (tx != null)
                {
                    Ui.SetActive(tx.gameObject, true);
                    Ui.Place(tx.gameObject, bx + 16f, y + 9f, bw - 32f, bh - 18f);
                    tx.text = cl.Text;
                    tx.color = cl.Mine ? Palette.Title : Palette.CardBody;
                }
                shown++;
                y -= 8f;
            }
            for (int i = shown; i < ChatRows; i++)
            {
                Ui.SetActive(_chBubble[i] != null ? _chBubble[i].gameObject : null, false);
                Ui.SetActive(_chBubbleTx[i] != null ? _chBubbleTx[i].gameObject : null, false);
            }

            // ── 底部：提示 + 三个按钮 ──
            bool trade = p.Kind == StockChat.KindTrade;
            bool info = p.Kind == StockChat.KindInfo;
            bool chat = p.Kind == StockChat.KindChat;

            FriendOffer o = StockState.Offer;
            bool pending = trade ? o != null : (info && p.HasInfo);

            if (_chHint != null)
            {
                string hint;
                if (trade)
                {
                    hint = o != null ? StockFriend.Describe(o)
                        : "老K 暂时没动静。他每隔几天来谈一笔：收你的货，或者往你手里塞货。";
                }
                else if (info)
                {
                    StockDef d = StockDefs.Get(p.InfoId);
                    hint = p.HasInfo
                        ? "他要 " + Money(StockState.ToYuan(p.InfoPrice))
                          + " 元卖你一条关于「" + (d != null ? d.Name : p.InfoId) + "」的消息（剩 "
                          + p.InfoDays + " 天）。"
                        : "他现在手上没有要卖的消息，过几天再来。";
                }
                else
                {
                    hint = "想说什么就自己敲一句，按回车或点【发送】发出去。";
                }
                if (!chat && !StockChat.Online) hint += "　<color=#E5533D>离线中：只能接受或拒绝。</color>";
                // 单行文字不能靠 TMP 的 Truncate，宽度自己截（见 Ui.Truncate）
                _chHint.text = Ui.Cut(hint, FsSmall, rw - 36f);
                _chHint.color = pending ? Palette.CardBody : Palette.Muted;
            }

            // 闲聊好友走「输入框 + 发送」，报价 / 情报那三颗按钮收起来
            Ui.SetActive(_chSay != null ? _chSay.gameObject : null, chat);
            if (_chSend != null)
            {
                Ui.SetActive(_chSend.Go, chat);
                SetBtnEnabled(_chSend, chat, Palette.BtnGreen);
            }

            if (_chAccept != null)
            {
                Ui.SetActive(_chAccept.Go, !chat);
                _chAccept.SetText(trade ? "接受报价" : "花钱听");
                SetBtnEnabled(_chAccept, pending, Palette.BtnGreen);
            }
            if (_chCounter != null)
            {
                Ui.SetActive(_chCounter.Go, !chat);
                _chCounter.SetText(trade
                    ? (o != null && o.Direction == 0 ? "还价 +10%" : "还价 −10%")
                    : "砍价 −30%");
                SetBtnEnabled(_chCounter, pending && StockChat.Online, Palette.BtnBlue);
            }
            if (_chReject != null)
            {
                Ui.SetActive(_chReject.Go, !chat);
                _chReject.SetText(trade ? "拒绝" : "不听");
                SetBtnEnabled(_chReject, pending, Palette.BtnOrange);
            }

            // 报价 / 情报三颗等分；闲聊的输入框和发送按钮在 BuildChat 里就摆好了
            if (!chat)
            {
                float bw2 = (rw - 36f - 2f * 14f) / 3f;
                float[] xs = { rx + 18f, rx + 18f + bw2 + 14f, rx + 18f + 2f * (bw2 + 14f) };
                if (_chAccept != null) _chAccept.Place(xs[0], barY, bw2, 46f);
                if (_chCounter != null) _chCounter.Place(xs[1], barY, bw2, 46f);
                if (_chReject != null) _chReject.Place(xs[2], barY, bw2, 46f);
            }
        }

        private static void PickChat(int slot)
        {
            ChatFriend[] all = StockChat.All;
            if (slot < 0 || slot >= all.Length) return;
            ChatFriend f = all[slot];
            LogOp("打开与【" + f.Name + "】的对话");
            _chatPick = slot;
            StockChat.MarkRead(f);
            SetStatus("正在和 " + f.Name + " " + f.Tag + " 聊天。", false);
            Refresh();
        }

        private static void ToggleOnline()
        {
            StockChat.Online = !StockChat.Online;
            StockState.Dirty = true;
            LogOp(StockChat.Online ? "切换为在线" : "切换为离线");
            SetStatus(StockChat.Online
                ? "已切换为在线：可以还价 / 砍价了。"
                : "已切换为离线：好友消息只能接受或拒绝，还价要切回在线。", false);
            Refresh();
        }

        private static void DoChatAccept()
        {
            ChatFriend f = PickedFriend();
            if (f == null) return;
            if (f.Kind == StockChat.KindTrade)
            {
                DoFriendAccept();
                return;
            }
            if (f.Kind == StockChat.KindChat)
            {
                // 闲聊好友的按钮换成了输入框 + 发送（见 DoChatSend），这颗不该被点到
                return;
            }

            LogOp("花钱买【" + f.Name + "】的消息");
            string err = StockChat.PayInfo(f);
            if (err != null)
            {
                SetStatus("[" + f.Name + "] " + err, true);
                Refresh();
                return;
            }
            SetStatus("[" + f.Name + "] 消息到手，信不信、动不动手自己拿主意。", false);
            Refresh();
        }

        /// <summary>回车提交输入框时的回调（onSubmit 只给个 string，内容忽略，直接发）。</summary>
        private static void OnChatSubmit(string s)
        {
            DoChatSend();
        }

        /// <summary>把输入框里自己敲的话发给当前好友（闲聊好友专用）。</summary>
        private static void DoChatSend()
        {
            ChatFriend f = PickedFriend();
            if (f == null) return;
            string text = _chSay != null ? (_chSay.text ?? string.Empty) : string.Empty;
            text = text.Trim();
            if (text.Length == 0)
            {
                SetStatus("先在输入框里敲一句再发。", true);
                return;
            }
            LogOp("给【" + f.Name + "】发消息：" + text);
            StockChat.Say(f, text);
            if (_chSay != null) { try { _chSay.SetTextWithoutNotify(""); } catch { } }
            SetStatus("[" + f.Name + "] 消息已发出，聊天记录里能看到。", false);
            Refresh();
        }

        private static void DoChatCounter()
        {
            ChatFriend f = PickedFriend();
            if (f == null) return;
            if (!StockChat.Online)
            {
                SetStatus("离线状态只能接受或拒绝，切回在线再谈价。", true);
                Refresh();
                return;
            }
            if (f.Kind == StockChat.KindTrade)
            {
                DoFriendCounter();
                return;
            }
            if (f.Kind != StockChat.KindInfo) return;

            LogOp("向【" + f.Name + "】砍价");
            string message;
            bool ok = StockChat.Haggle(f, out message);
            SetStatus("[" + f.Name + "] " + message, !ok);
            Refresh();
        }

        private static void DoChatReject()
        {
            ChatFriend f = PickedFriend();
            if (f == null) return;
            if (f.Kind == StockChat.KindTrade)
            {
                DoFriendReject();
                return;
            }
            if (f.Kind != StockChat.KindInfo) return;

            LogOp("不听【" + f.Name + "】的消息");
            StockChat.DropInfo(f);
            SetStatus("[" + f.Name + "] 这条消息不听了。", false);
            Refresh();
        }

        // ── 分步导览（气泡 + 箭头 + 目标描边）─────────────────────────
        // 三套东西共用一个气泡，靠 _tourPage 区分：
        //   · 整段新手引导：写存档（StockState.TutorialStep），关面板再开还记得走到哪；
        //   · 本页导览：从「?」说明卡进来，不写存档，走完把 _tourSkipAuto 立起来，
        //     本次打开面板期间不再把整段引导自动接回来；
        //   · 说明卡：另一套 UI（_hintRoot），跟气泡互斥。

        /// <summary>
        /// ESC 的分层处理：说明卡 → 引导气泡 → 面板本身。
        /// 返回 true 表示这一下 ESC 已经被吃掉了，调用方不要再关面板。
        /// </summary>
        public static bool EscBack()
        {
            if (_hintPage >= 0)
            {
                LogOp("关闭页说明");
                CloseHint();
                return true;
            }
            if (_tourOpen)
            {
                TourSkip();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 启动一段分步导览。page=true 表示从「?」里的本页导览进来的：
        /// 不写存档，走完也不会把整段新手引导标成已完成。
        /// </summary>
        private static void StartTour(int from, int to, int at, bool page)
        {
            if (to < from) return;
            _tourFrom = from;
            _tourTo = to;
            _tourAt = at < from ? from : (at > to ? to : at);
            _tourPage = page;
            _tourOpen = true;
            if (!page) _tourSkipAuto = false;
            CloseHint();
            Refresh();
        }

        /// <summary>收掉当前这段导览。整段引导走完（或跳过）要写存档；本页导览只是关掉。</summary>
        private static void EndTour()
        {
            if (!_tourPage)
            {
                FinishTutorial();
                return;
            }
            _tourOpen = false;
            _tourPage = false;
            _tourSkipAuto = true;   // 本次打开面板期间别再自动把整段引导接回来
            if (_tutRoot != null) Ui.SetActive(_tutRoot, false);
            SetPageInput(true);
            SetStatus("本页导览结束。想看别的页，点右上角的「?」。", false);
            Refresh();
        }

        private static void TourNext()
        {
            if (_tourAt >= _tourTo)
            {
                LogOp("教程：最后一站，收摊");
                EndTour();
                return;
            }
            LogOp("教程：第 " + (_tourAt - _tourFrom + 1) + " 站 → 第 " + (_tourAt - _tourFrom + 2) + " 站");
            _tourAt++;
            if (!_tourPage) StockState.TutorialStep = _tourAt;
            StockState.Dirty = true;
            Refresh();
        }

        private static void TourPrev()
        {
            if (_tourAt <= _tourFrom)
            {
                LogOp("教程：已经是第一站，忽略");
                return;
            }
            LogOp("教程：第 " + (_tourAt - _tourFrom + 1) + " 站 → 第 " + (_tourAt - _tourFrom) + " 站");
            _tourAt--;
            if (!_tourPage) StockState.TutorialStep = _tourAt;
            StockState.Dirty = true;
            Refresh();
        }

        private static void TourSkip()
        {
            LogOp(_tourPage ? "关闭本页导览" : "跳过新手引导");
            EndTour();
        }

        /// <summary>
        /// 分步引导期间把每个页面的交互整体开关。关掉是必要的：引导层只能拦住
        /// 「在内容区按下去、又在内容区抬起来」这一条路，鼠标拖出引导层再松开还是漏到下面。
        /// 两个例外：从「?」进来的回看不动页面（玩家是想边看边试着用），
        /// 以及当前这一站要玩家亲手做一次（转钱 / 买入 / 卖出），这时必须放开交互。
        /// </summary>
        private static void SetPageInput(bool on)
        {
            for (int i = 0; i < PageCount; i++)
            {
                CanvasGroup cg = _pageCg[i];
                if (cg == null) continue;
                cg.interactable = on;
                cg.blocksRaycasts = on;
            }
        }

        /// <summary>当前这一站是不是要玩家动手做一次（要就放开页面交互）。</summary>
        private static bool CurrentNeed()
        {
            if (!_tourOpen || _tourPage) return false;
            TutStep s = TutAt(_tourAt);
            return s != null && !string.IsNullOrEmpty(s.Need);
        }

        /// <summary>
        /// 当前这一站要玩家做的动作，现在到底做不做得成。
        /// 主线已经不给「跳过」了，但万一真的做不成（口袋没钱、手里没股），必须留一个口子，
        /// 否则玩家会被永久钉在这一站 —— 所以「跳过」露不露面由这里说了算。
        /// </summary>
        private static bool NeedFeasible()
        {
            TutStep s = TutAt(_tourAt);
            if (s == null || string.IsNullOrEmpty(s.Need)) return true;
            switch (s.Need)
            {
                case "transfer":
                {
                    PlayerStore store = PlayerStore.instance;
                    return store != null && store.playerCash >= 1;
                }
                case "buy":
                    return MaxBuyable() >= 1;
                case "sell":
                {
                    StockDef def = Current();
                    return def != null && StockState.GetPosition(def.Id) >= 1;
                }
                default:
                    return true;    // "pick" 只要点一行，永远做得成
            }
        }

        /// <summary>已经做过「自动换标的」兜底的步号，避免 SelectStock→Refresh 绕回来再兜一次。</summary>
        private static int _tutFixStep = -1;

        /// <summary>
        /// 走到「动手买入」那一站时，玩家可能挑了一支买不起 1 股的高价股，或者挑到没开户的黑市股
        /// —— 那样他点多少次「买入」都只会报错，人被卡在这一步出不去。
        /// 所以进这一站时先替他兜一下底：标的换不成能成交的、数量压到买得起的范围，
        /// 保证「强制走完一遍」不会变成「强制卡住」。
        /// </summary>
        private static void FixTutorialPick()
        {
            TutStep s = TutAt(_tourAt);
            if (s == null || s.Need != "buy") return;
            if (_tutFixStep == _tourAt) return;
            _tutFixStep = _tourAt;

            long pool = StockEngine.PoolYuan();
            if (pool < 1) return;

            StockDef def = Current();
            bool usable = def != null && MaxBuyable() >= 1
                && !(def.NeedLicense && !StockState.License);
            if (!usable)
            {
                StockDef[] all = StockDefs.All;
                string best = null;
                double bestPrice = double.MaxValue;
                for (int i = 0; i < all.Length; i++)
                {
                    StockDef d = all[i];
                    if (d == null || (d.NeedLicense && !StockState.License)) continue;
                    double price = StockEngine.LivePriceYuan(d.Id);
                    if (price <= 0 || price > pool) continue;
                    if (price < bestPrice) { bestPrice = price; best = d.Id; }
                }
                if (best == null) return;
                LogOp("教程：当前标的买不了，自动换成 " + StockDefs.Get(best).Name);
                SelectStock(best);
            }

            // 默认 10 股，遇到 20 元以上的标的钱就不够了 —— 压到「最多可买」以内，让这一下点得成
            long max = MaxBuyable();
            if (max >= 1 && _amount > max)
            {
                _amount = (int)Math.Min(max, QtyMax);
                SyncQtyInput();
                LogOp("教程：下单数量自动调整为 " + _amount + " 股（最多可买 " + max + " 股）");
            }
        }

        /// <summary>
        /// 玩家在带 Need 的那一站真的做成了动作（转钱 / 买入 / 卖出），自动往下走。
        /// 由 StockUI 自己的动作函数在成功后调用；不在那一站就什么都不做。
        /// </summary>
        public static void TourActionDone(string kind)
        {
            if (!CurrentNeed()) return;
            TutStep s = TutAt(_tourAt);
            if (s.Need != kind) return;
            LogOp("教程：完成了「" + s.Title + "」");
            TourNext();
        }

        private static void RefreshTutorial()
        {
            if (_tutRoot == null) return;

            // 没在讲、也不是「刚看完本页导览」的时候，只要整段引导还没走完就自动接回来
            // —— 关掉面板再打开能接着上次那一步继续，不用重新点「重看教程」。
            if (!_tourOpen && !_tourSkipAuto
                && StockState.TutorialStep >= 0 && StockState.TutorialStep < TutTotal)
            {
                _tourFrom = 0;
                _tourTo = TutTotal - 1;
                _tourAt = StockState.TutorialStep;
                _tourPage = false;
                _tourOpen = true;
            }

            if (!_tourOpen)
            {
                // 引导收摊了，教程那条演示路径也一起撤（只撤自己铺的，见 StockIntraday.DemoEnd）。
                // 只有真的撤掉了才顺手收读数：读数报的是刚被撤掉的那条假走势，
                // 留着它就等于「过完教程回来，旧数字还画在图上」（见用户反馈）。
                // DemoActive 兜底是为了别每帧都对着全量读数层扫一遍。
                if (StockIntraday.DemoActive)
                {
                    StockIntraday.DemoEnd();
                    HideReadouts();
                }
                Ui.SetActive(_tutRoot, false);
                SetPageInput(true);
                return;
            }

            if (_tourAt < _tourFrom) _tourAt = _tourFrom;
            if (_tourAt > _tourTo) _tourAt = _tourTo;

            TutStep s = TutAt(_tourAt);
            // 讲到「盘中实时交易」那一站时铺一条演示走势：玩家多半还没开店，分时图是空的，
            // 空图讲不明白。离开这一站就撤（有真实数据时这两个调用都是空转，见 StockIntraday）
            if (s.Page == PageIntra) StockIntraday.DemoBegin(_selectedId);
            else if (StockIntraday.DemoActive)
            {
                // 刚离开「盘中实时交易」那一站：撤掉这站铺的演示走势，
                // 并且把读数一起收掉 —— 它指着的是刚被撤掉的那条假路径，
                // 不收就会留在图上（见用户反馈）
                StockIntraday.DemoEnd();
                HideReadouts();
            }
            // 本页导览要玩家边看边试，带 Need 的站要玩家亲手做一次，两种都放开页面交互
            SetPageInput(_tourPage || CurrentNeed());

            if (_page != s.Page)
            {
                if (_tourPage)
                {
                    // 本页导览：玩家自己换页了，就收摊，别硬把人拽回来
                    LogOp("本页导览：玩家换页，自动结束");
                    SetStatus("你换页了，本页导览结束。", false);
                    EndTour();
                    return;
                }
                // 整段引导：这一站讲哪一页就去哪一页，讲完再往下走
                ShowPage(s.Page);
                return;
            }

            if (CurrentNeed()) FixTutorialPick();
            LayoutTour(s);
        }

        /// <summary>
        /// 摆当前这一站的气泡。宽高按正文行数估算（TMP 的 preferredHeight 要等排版跑完才有值，
        /// 这里等不了），再按「目标四周哪一边空得下」挑个方位贴上去，箭头从气泡指向目标。
        /// </summary>
        private static void LayoutTour(TutStep s)
        {
            if (_tutRoot == null || _tutBubble == null) return;
            Ui.SetActive(_tutRoot, true);

            // ── 文案 ────────────────────────────────────────────────
            if (_tutStepText != null)
            {
                if (_tourPage)
                {
                    _tutStepText.text = "本页导览 " + (_tourAt - _tourFrom + 1) + " / " + (_tourTo - _tourFrom + 1);
                    _tutStepText.color = Palette.Cyan;
                }
                else
                {
                    _tutStepText.text = "主线 第 " + (_tourAt + 1) + " / " + TutorialMain.Length + " 站";
                    _tutStepText.color = Palette.Cyan;
                }
            }
            if (_tutTitle != null) _tutTitle.text = s.Title;
            if (_tutBody != null) _tutBody.text = Palette.Rt(s.Body ?? "");

            // ── 气泡尺寸 ────────────────────────────────────────────
            float pad = 18f;
            // 比原来宽一档：正文越长，宽一点比高一点好读，也不容易折出界。
            float bw = Mathf.Clamp(W * 0.42f, 420f, 560f);
            // 钉边摆的那几站要留出底下那排按钮，气泡跟着收窄，免得横着铺到按钮上面去
            bool pinned = !string.IsNullOrEmpty(s.Bubble);
            if (pinned) bw = Mathf.Min(bw, Mathf.Max(320f, ContentW * 0.44f - 24f));
            float textW = bw - pad * 2f;

            // 正文高度：先让 TMP 自己量一遍。原来纯按「半角宽」估行数，长句会估少一两行，
            // 尾巴就被 Truncate 悄悄吃掉（见 问题截图/这里必须选一个股票 不能跳过）。
            // 量不出来（刚建好还没排版）再退回估算，并额外补一行保险。
            float bodyH = 0f;
            if (_tutBody != null)
            {
                try { bodyH = _tutBody.GetPreferredValues(s.Body ?? "", textW, 0f).y; }
                catch { bodyH = 0f; }
            }
            if (bodyH < 24f) bodyH = (EstLines(s.Body, (int)(textW / 9.5f)) + 1) * 27f;
            bodyH = Mathf.Ceil(bodyH) + 6f;

            // 气泡高度 = 上内边距 + 标题行(含进度条) + 正文 + 提示行 + 按钮行 + 下内边距
            const float headH = 44f;    // 标题 + 进度条
            const float footH = 20f;    // 「忘了？点右上角 ?」常驻提示
            const float btnH = 40f;
            float bh = pad + headH + bodyH + 8f + footH + 10f + btnH + pad;

            // ── 贴哪一边 ────────────────────────────────────────────
            Rect tgt = TutRect(s.Zone);
            bool has = tgt.width > 1f && tgt.height > 1f;
            const float gap = 30f;      // 气泡与目标之间留给箭头的那条缝
            const float top = 78f;      // 气泡往上别顶到顶栏
            float bx, by;
            int dir = -1;               // 0 下 / 1 上 / 2 右 / 3 左，-1 表示没方位

            if (pinned)
            {
                // 钉在内容区顶边：底下那排要玩家点的按钮整片露出来，气泡只占上面那块没人用的地方
                bx = s.Bubble == "right" ? ContentX + ContentW - 24f - bw : ContentX + 24f;
                by = BodyY + 4f;
                dir = 1;                // 气泡在目标上方，箭头朝下指
            }
            else if (!has)
            {
                bx = Mathf.Round((W - bw) * 0.5f);
                by = Mathf.Clamp(Mathf.Round(H * 0.56f - bh * 0.5f), top, H - bh - 20f);
            }
            else
            {
                if (H - (tgt.y + tgt.height) - gap >= bh) dir = 0;
                else if (tgt.y - gap >= bh) dir = 1;
                else if (W - (tgt.x + tgt.width) - gap >= bw) dir = 2;
                else if (tgt.x - gap >= bw) dir = 3;

                if (dir < 0)
                {
                    // 哪边都塞不下：不再压到目标身上 —— 旧写法把气泡糊在目标的右下角，
                    // 正好盖住聚光灯照着的内容（见 问题截图/我的持仓不是必要新手任务必备教学….
                    // png 后半句「优化窗口挡住聚焦的位置了」）。
                    // 改为量四边的净空，挑最宽的一边摆，再夹回面板内沿：
                    // 目标是整片（总览页顶栏数字 + 运营概览卡）时，下方净空最大，气泡就落到下面去。
                    float spB = H - (tgt.y + tgt.height) - gap;   // 下
                    float spT = tgt.y - gap;                      // 上
                    float spR = W - (tgt.x + tgt.width) - gap;    // 右
                    float spL = tgt.x - gap;                      // 左
                    float best = spB;
                    dir = 0;
                    if (spT > best) { best = spT; dir = 1; }
                    if (spR > best) { best = spR; dir = 2; }
                    if (spL > best) { best = spL; dir = 3; }

                    float loX = 16f, hiX = Mathf.Max(16f, W - bw - 16f);
                    float loY = top, hiY = Mathf.Max(top, H - bh - 20f);
                    if (dir == 0 || dir == 1)
                    {
                        bx = Mathf.Clamp(tgt.x + tgt.width * 0.5f - bw * 0.5f, loX, hiX);
                        by = Mathf.Clamp(dir == 0 ? tgt.y + tgt.height + gap : tgt.y - gap - bh, loY, hiY);
                    }
                    else
                    {
                        bx = Mathf.Clamp(dir == 2 ? tgt.x + tgt.width + gap : tgt.x - gap - bw, loX, hiX);
                        by = Mathf.Clamp(tgt.y + tgt.height * 0.5f - bh * 0.5f, loY, hiY);
                    }
                }
                else if (dir == 0 || dir == 1)
                {
                    bx = Mathf.Clamp(tgt.x + tgt.width * 0.5f - bw * 0.5f, 16f, Mathf.Max(16f, W - bw - 16f));
                    by = dir == 0 ? tgt.y + tgt.height + gap : tgt.y - gap - bh;
                }
                else
                {
                    bx = dir == 2 ? tgt.x + tgt.width + gap : tgt.x - gap - bw;
                    by = Mathf.Clamp(tgt.y + tgt.height * 0.5f - bh * 0.5f, top, Mathf.Max(top, H - bh - 20f));
                }
            }

            Ui.Place(_tutBubble, bx, by, bw, bh);
            // 下面这几个都是气泡的子物件，坐标一律相对气泡左上角量
            if (_tutRim != null) Ui.Stretch(_tutRim.gameObject, 0f);
            // 纸面缩 3 而不是 2：描边比卡片粗一圈，气泡的边界更清楚
            if (_tutFace != null) Ui.Stretch(_tutFace.gameObject, 3f);

            if (_tutTitle != null) Ui.Place(_tutTitle.gameObject, pad, pad - 4f, Mathf.Max(60f, bw - pad * 2f - 118f), 30f);
            if (_tutStepText != null) Ui.Place(_tutStepText.gameObject, bw - pad - 114f, pad, 114f, 22f);

            // 进度条：走到第几站一眼看得见，比任何「请继续」都管用
            float barY = pad + 30f;
            float prog = Mathf.Clamp01((_tourAt - _tourFrom + 1f) / Mathf.Max(1f, _tourTo - _tourFrom + 1f));
            if (_tutBarBg != null) Ui.Place(_tutBarBg.gameObject, pad, barY, textW, 5f);
            if (_tutBarFill != null) Ui.Place(_tutBarFill.gameObject, pad, barY, Mathf.Max(4f, textW * prog), 5f);

            if (_tutBody != null) Ui.Place(_tutBody.gameObject, pad, pad + headH, textW, bodyH);
            if (_tutFoot != null) Ui.Place(_tutFoot.gameObject, pad, pad + headH + bodyH + 8f, textW, footH);

            // ── 三个按钮 ────────────────────────────────────────────
            float btnY = pad + headH + bodyH + 8f + footH + 10f;
            float inner = bw - pad * 2f;
            float skipW = 84f, prevW = 84f, nextW = 124f;
            float needW = skipW + 12f + prevW + 10f + nextW;
            if (needW > inner)
            {
                // 窄屏（面板被放大、可用宽度小）时按比例收一收，别让三个按钮互相压
                float k = Mathf.Clamp(inner / needW, 0.62f, 1f);
                skipW *= k; prevW *= k; nextW *= k;
            }
            if (_tutSkip != null) _tutSkip.Place(pad, btnY, skipW, btnH);
            if (_tutNext != null) _tutNext.Place(bw - pad - nextW, btnY, nextW, btnH);
            if (_tutPrev != null) _tutPrev.Place(bw - pad - nextW - 10f - prevW, btnY, prevW, btnH);

            // 主线就是核心买卖闭环，不给「跳过」；只有这一站的动作真的做不成（口袋没钱、手里没股）
            // 才临时放一个「先跳过」的出口，免得玩家被钉死在一步上。
            bool mustDo = CurrentNeed() && NeedFeasible();
            bool canEsc = _tourPage || (CurrentNeed() && !NeedFeasible());
            if (_tutSkip != null)
            {
                _tutSkip.SetText(_tourPage ? "关闭" : "先跳过");
                Ui.SetActive(_tutSkip.Go, canEsc);
            }
            if (_tutNext != null)
            {
                if (_tourPage)
                {
                    _tutNext.SetLabel(_tourAt >= _tourTo ? "完成" : "下一步", Palette.Title);
                    _tutNext.SetBg(Palette.BtnGreen);
                    _tutNext.SetInteractable(true);
                }
                else if (mustDo)
                {
                    // 正路是自己去点那一下，做成了自动往下走；按钮只负责告诉他还差一步
                    _tutNext.SetLabel("先做这一步", Palette.Sub);
                    _tutNext.SetBg(Palette.SlotBg);
                    _tutNext.SetInteractable(false);
                }
                else
                {
                    _tutNext.SetLabel(_tourAt == TutTotal - 1 ? "开始交易" : "下一步", Palette.Title);
                    _tutNext.SetBg(Palette.BtnGreen);
                    _tutNext.SetInteractable(true);
                }
            }
            bool canPrev = _tourAt > _tourFrom;
            if (_tutPrev != null)
            {
                _tutPrev.SetInteractable(canPrev);
                _tutPrev.SetBg(canPrev ? Palette.BtnIdle : Palette.SlotBg);
                _tutPrev.SetTextColor(canPrev ? Palette.Title : Palette.Sub);
            }

            // ── 目标描边 + 灯光聚焦 + 箭头 ───────────────────────────
            if (has)
            {
                // 灯光聚焦：四块压暗板围着目标拼一圈，中间留出「灯」照到的那块。
                // 洞口比目标再外扩一圈、压暗也加深了 —— 原来只贴着目标描边、只压一半，
                // 亮的地方太小、暗的地方不够暗，玩家反馈「聚光灯不够大、懒得看下去」。
                const float mx = 16f, my = 14f;
                float hx = Mathf.Clamp(tgt.x - mx, 0f, W);
                float hy = Mathf.Clamp(tgt.y - my, 0f, H);
                float hw = Mathf.Clamp(tgt.width + mx * 2f, 0f, W - hx);
                float hh = Mathf.Clamp(tgt.height + my * 2f, 0f, H - hy);
                float da = _tourPage ? 0.34f : 0.62f;
                PlaceDim(0, 0f, 0f, W, hy, da);
                PlaceDim(1, 0f, hy + hh, W, H - (hy + hh), da);
                PlaceDim(2, 0f, hy, hx, hh, da);
                PlaceDim(3, hx + hw, hy, W - (hx + hw), hh, da);

                const float th = 4f;
                PlaceEdge(0, hx, hy, hw, th);
                PlaceEdge(1, hx, hy + hh - th, hw, th);
                PlaceEdge(2, hx, hy, th, hh);
                PlaceEdge(3, hx + hw - th, hy, th, hh);
            }
            else
            {
                for (int i = 0; i < 4; i++)
                {
                    Ui.SetActive(_tutEdge[i] != null ? _tutEdge[i].gameObject : null, false);
                    Ui.SetActive(_tutDim[i] != null ? _tutDim[i].gameObject : null, false);
                }
            }

            if (_tutArrow == null) return;
            if (!has || dir < 0)
            {
                Ui.SetActive(_tutArrow.gameObject, false);
                return;
            }
            Ui.SetActive(_tutArrow.gameObject, true);

            float cx = Mathf.Clamp(tgt.x + tgt.width * 0.5f - 30f, bx, Mathf.Max(bx, bx + bw - 60f));
            float cy = Mathf.Clamp(by + bh * 0.5f - 14f, 0f, H - 28f);
            if (dir == 0)
            {
                _tutArrow.text = "▲";
                Ui.Place(_tutArrow.gameObject, cx, by - gap + 1f, 60f, 28f);
            }
            else if (dir == 1)
            {
                _tutArrow.text = "▼";
                Ui.Place(_tutArrow.gameObject, cx, by + bh + 1f, 60f, 28f);
            }
            else if (dir == 2)
            {
                _tutArrow.text = "◀";
                Ui.Place(_tutArrow.gameObject, tgt.x + tgt.width + 1f, cy, 30f, 28f);
            }
            else
            {
                _tutArrow.text = "▶";
                Ui.Place(_tutArrow.gameObject, tgt.x - 31f, cy, 30f, 28f);
            }
        }

        private static void PlaceEdge(int i, float x, float y, float w, float h)
        {
            if (i < 0 || i >= _tutEdge.Length || _tutEdge[i] == null) return;
            Ui.SetActive(_tutEdge[i].gameObject, true);
            Ui.Place(_tutEdge[i].gameObject, x, y, w, h);
        }

        /// <summary>
        /// 摆一块压暗板。宽或高塌成 0 的就收起来 —— 目标正好贴着面板边时，
        /// 上下左右总有一两块是空的，留着会变成一条一像素的暗线。
        /// </summary>
        private static void PlaceDim(int i, float x, float y, float w, float h, float alpha)
        {
            if (i < 0 || i >= _tutDim.Length || _tutDim[i] == null) return;
            if (w <= 1f || h <= 1f)
            {
                Ui.SetActive(_tutDim[i].gameObject, false);
                return;
            }
            _tutDim[i].color = Palette.A(Palette.DimBase, alpha);
            Ui.SetActive(_tutDim[i].gameObject, true);
            Ui.Place(_tutDim[i].gameObject, x, y, w, h);
        }

        private static void FinishTutorial()
        {
            LogOp("结束新手引导");
            _tourOpen = false;
            _tourPage = false;
            StockState.TutorialStep = TutorialDone;
            StockState.Dirty = true;
            // 立刻写回 modData：玩家很可能跳过教程就直接退游戏，
            // 不主动写一次的话「已跳过」只留在内存里，下次进档又要跳过一遍。
            StockState.Flush(PlayerStore.instance);
            if (_tutRoot != null) Ui.SetActive(_tutRoot, false);
            SetPageInput(true);
            SetStatus("引导已结束。以后第一次点进某一页，会自动弹一遍那页的导览；【新手任务】里有奖励可领。", false);
            Refresh();
        }

        private static void RestartTutorial()
        {
            LogOp("重看新手教程");
            StockState.TutorialStep = 0;
            StockState.Dirty = true;
            SetStatus("新手引导已重新开始：气泡会跟着走，讲到哪指到哪。", false);
            StartTour(0, TutTotal - 1, 0, false);
        }

        // ── 每页的「?」说明卡 ────────────────────────────────────────

        /// <summary>打开某一页的说明卡。卡片只占内容区中间一块，四周留出页面的边角。</summary>
        private static void OpenHint(int page)
        {
            if (_hintRoot == null || _hintCard == null) return;
            if (page < 0 || page >= Guides.Length || page >= NavTitles.Length) return;

            if (_tourOpen)
            {
                SetStatus("引导正在讲解，先点气泡上的「下一步」或「跳过教程」，再看这页的说明。", false);
                Refresh();
                return;
            }

            LogOp("查看【" + NavTitles[page] + "】页说明");
            _hintPage = page;

            float pad = 22f;
            float hw = Mathf.Clamp(ContentW * 0.66f, 520f, 860f);
            float hh = Mathf.Clamp(BodyH * 0.84f, 320f, 470f);
            float hx = Mathf.Round((ContentW - hw) * 0.5f);
            float hy = Mathf.Round((BodyH - hh) * 0.5f);

            if (_hintTitle != null)
            {
                _hintTitle.text = NavTitles[page] + " · 这一页怎么用";
                Ui.Place(_hintTitle.gameObject, pad, 16f, hw - pad * 2f, 36f);
            }
            if (_hintBody != null)
            {
                _hintBody.text = Palette.Rt(Guides[page]);
                Ui.Place(_hintBody.gameObject, pad, 60f, hw - pad * 2f, hh - 132f);
            }

            Ui.Place(_hintCard, hx, hy, hw, hh);
            if (_hintRim != null) Ui.Stretch(_hintRim.gameObject, 0f);
            if (_hintFace != null) Ui.Stretch(_hintFace.gameObject, 2f);

            // 调试页没有分步导览，「带我走一遍」直接置灰
            bool canWalk = PageHasTour(page);
            if (_hintWalk != null)
            {
                _hintWalk.SetInteractable(canWalk);
                _hintWalk.SetBg(canWalk ? Palette.BtnGreen : Palette.SlotBg);
                _hintWalk.SetTextColor(canWalk ? Palette.Title : Palette.Sub);
                _hintWalk.Place(hw - pad - 110f - 10f - 150f, hh - 58f, 150f, 42f);
            }
            if (_hintOk != null) _hintOk.Place(hw - pad - 110f, hh - 58f, 110f, 42f);

            Ui.SetActive(_hintRoot, true);
        }

        private static void CloseHint()
        {
            if (_hintPage < 0) return;
            _hintPage = -1;
            Ui.SetActive(_hintRoot, false);
        }

        /// <summary>说明卡底部的「带我走一遍」：启动这一页的分步导览。</summary>
        private static void HintWalk()
        {
            int page = _hintPage;
            if (page < 0) page = _page;
            int from, to;
            PageTourRange(page, out from, out to);
            CloseHint();

            if (from < 0)
            {
                SetStatus("这一页没有分步导览，看上面的说明就够了。", false);
                Refresh();
                return;
            }
            LogOp("从说明卡进入【" + NavTitles[page] + "】页导览");
            StartTour(from, to, from, true);
        }

        private static void DbgFriendOffer()
        {
            LogOp("调试：让老K 立刻报价");
            if (StockFriend.ForceOffer())
            {
                SetStatus("[老K] " + StockFriend.Describe(StockState.Offer), false);
                ShowPage(PageFriend);
                return;
            }
            SetStatus("[老K] 现在开不出报价（没有持仓、也没钱接货），先买点股票再试。", true);
            Refresh();
        }

        private static void DbgFriendPing()
        {
            LogOp("调试：让好友立刻来一条消息");
            if (StockChat.ForceMessage())
            {
                SetStatus("[好友] 来新消息了，看左边导航上的红色数字气泡。", false);
                Refresh();
                return;
            }
            SetStatus("[好友] 现在没有能发消息的好友。", true);
            Refresh();
        }

        private static void DbgFinishQuests()
        {
            LogOp("调试：任务全部标记完成");
            StockQuest.ForceDoneAll();
            SetStatus("[任务] 6 条任务已全部标记完成，去【新手任务】页领奖。", false);
            ShowPage(PageQuest);
        }

        private static void DbgIntraSkip()
        {
            LogOp("调试：盘中快进 8 段");
            try
            {
                PlayerStore store = PlayerStore.instance;
                if (store == null) { SetStatus("尚未进入存档，无法快进盘中。", true); Refresh(); return; }
                StockState.EnsureLoaded(store);

                if (!StockIntraday.IsLive)
                {
                    SetStatus("现在不是营业时间，先开店（开始营业）再快进盘中。", true);
                }
                else
                {
                    StockIntraday.Skip(8);
                    SetStatus("盘中已快进 8 段，当前 " + StockIntraday.Step + "/"
                        + StockIntraday.Total + " 段。", false);
                }
            }
            catch (Exception ex)
            {
                SetStatus("盘中快进失败：" + ex.Message, true);
            }
            Refresh();
        }

        /// <summary>
        /// 走一遍收工流程：日内路径归位到今日收盘价、当日挂单全部撤销。
        /// 注意它不改游戏自己的营业状态，所以商店还开着的话盘面会紧接着重开一天。
        /// </summary>
        private static void DbgIntraClose()
        {
            LogOp("调试：立刻收工");
            try
            {
                if (!StockIntraday.IsLive)
                {
                    SetStatus("现在不是营业时间，没什么可收的。", true);
                }
                else
                {
                    StockIntraday.EndDay();
                    SetStatus("已调用收工：当日挂单全部撤销并解冻。商店还开着的话，盘面会紧接着重开一天。",
                        false);
                }
            }
            catch (Exception ex)
            {
                SetStatus("立刻收工失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void DbgOrderClear()
        {
            LogOp("调试：清空挂单");
            int n = StockOrders.CancelAll("调试面板");
            SetStatus(n > 0
                ? "已清空 " + n + " 笔挂单，冻结的资金和股数都还回来了。"
                : "当前没有挂单。", false);
            Refresh();
        }

        /// <summary>调试用：直接点亮六项高级工具。故意不走 Unlock，免得还要先给店铺塞钱。</summary>
        private static void DbgUnlockVip()
        {
            LogOp("调试：开通全部高级工具");
            int n = 0;
            for (int i = 0; i < StockVip.FeatCount; i++)
            {
                if (StockVip.Has(i)) continue;
                StockVip.Mask |= 1 << i;
                n++;
            }
            StockState.Dirty = true;
            SetStatus(n > 0
                ? "已点亮 " + n + " 项没开通的高级工具（不扣钱，纯调试）。"
                : "全部高级工具本来就是全开的。", false);
            Refresh();
        }

        private static void RefreshDebug()
        {
            if (_dbgText == null) return;

            // ── 左栏：账户 / 事件 / 席位 ──
            StringBuilder sb = new StringBuilder();
            sb.Append("今日 第 ").Append(StockState.Today).Append(" 天　距周期结算 ")
              .Append(StockDefs.CycleDays - StockState.DayCounter).Append(" 天\n");
            sb.Append("股票账户 ").Append(Money(StockEngine.PoolYuan())).Append(" 元　持仓市值 ")
              .Append(Money(StockEngine.StockValueYuan())).Append(" 元\n");
            sb.Append("浮动盈亏 ").Append(Sign(StockEngine.FloatingPnlYuan()))
              .Append(Money(StockEngine.FloatingPnlYuan())).Append(" 元　已实现 ")
              .Append(Sign(StockEngine.RealizedPnlYuan())).Append(Money(StockEngine.RealizedPnlYuan()))
              .Append(" 元\n");
            sb.Append("累计分红 +").Append(Money(StockEngine.DividendYuan()))
              .Append(" 元　总收益 ").Append(Sign(StockEngine.TotalGainYuan()))
              .Append(Money(StockEngine.TotalGainYuan())).Append(" 元\n");
            sb.Append("黑市开户 ").Append(StockState.License ? "已开通" : "未开通")
              .Append("　走势记录 ").Append(StockState.History.Count).Append(" 个交易日\n");
            sb.Append("活跃事件 ").Append(StockEngine.ActiveEventSummary()).Append('\n');
            // 席位一旦不再出手，行情就会退化成纯噪声；这一行是「市场里还有别人」的证据
            sb.Append("人机席位 ").Append(StockBots.SeatSummary()).Append('\n');
            sb.Append("今日资金 ").Append(StockBots.TopFlowText(3)).Append('\n');
            // 照旧把「还剩几天 / 下次哪天」摊开写，方便测随机节奏
            sb.Append("下次事件 第 ").Append(StockState.NextEventDay).Append(" 天　盘中快讯 ")
              .Append(StockEngine.TodayIntradayHeadline().Length > 0
                  ? StockEngine.TodayIntradayHeadline() : "无").Append('\n');
            sb.Append("高级工具 已开通 ").Append(StockVip.UnlockedCount).Append('/')
              .Append(StockVip.FeatCount).Append("　已花 ").Append(StockVip.Spent)
              .Append(" 元　选股器 第 ").Append(StockVip.PickerRoll + 1).Append('/')
              .Append(StockVip.PickerTotal).Append(" 批（")
              .Append(StockVip.PickerResetIn).Append(" 天后恢复）").Append('\n');
            sb.Append("今日传闻 ").Append(RumorSummary());
            _dbgText.text = sb.ToString();

            // ── 右栏：股评胜率 + 涨跌榜 ──
            // 股评这一行的用处是：一眼看出「精选博主真的更准、假博主真的更差」，
            // 不平衡的话就是 TruthRate 那几个数没调对。
            if (_dbgTextR == null) return;
            StringBuilder rb = new StringBuilder();
            rb.Append("今日股评 ").Append(StockReview.Today().Count).Append(" 条\n");
            for (int i = 0; i < StockReview.Bloggers.Length; i++)
            {
                StockReview.Blogger bg = StockReview.Bloggers[i];
                rb.Append("　").Append(bg.Name).Append(' ')
                  .Append(StockReview.WinRateText(bg.Id)).Append('\n');
            }
            // 26 支全列会撑爆栏高，只挑波动最大的 6 支——调试要看的本来就是极端值
            rb.Append("涨跌榜（今日幅度前 6）\n");
            List<StockDef> rank = new List<StockDef>();
            for (int i = 0; i < StockDefs.All.Length; i++) rank.Add(StockDefs.All[i]);
            rank.Sort((a, b) => Math.Abs(StockEngine.DayChangePercent(b.Id))
                .CompareTo(Math.Abs(StockEngine.DayChangePercent(a.Id))));
            int shown = Mathf.Min(6, rank.Count);
            for (int i = 0; i < shown; i++)
            {
                StockDef def = rank[i];
                double change = StockEngine.DayChangePercent(def.Id);
                rb.Append("　").Append(def.Name).Append(' ')
                  .Append(StockEngine.LivePriceYuan(def.Id).ToString("0.00"))
                  .Append("<color=#").Append(ColorHex(Palette.Change(change))).Append("> ")
                  .Append(Sign(change)).Append(change.ToString("0.0")).Append("%</color>\n");
            }
            _dbgTextR.text = rb.ToString();
        }

        // ══════════════════════════════════════════════════════════════
        //  页面切换与刷新
        // ══════════════════════════════════════════════════════════════

        private static void ShowPage(int page)
        {
            ShowPage(page, true);
        }

        /// <summary>
        /// 切页。autoTour=true 时才考虑「首次点进这一页就自动弹该页导览」——
        /// 面板刚打开、教程自己带着走的时候都传 false，免得一开面板就先糊一脸气泡。
        /// </summary>
        private static void ShowPage(int page, bool autoTour)
        {
            if (page < 0 || page >= PageCount) return;
            if (page != _page) LogOp("切换到【" + NavTitles[page] + "】页");
            // 切页就把「?」说明卡收起来，不然它会挂在新页面上讲旧页的事
            CloseHint();
            // 两页各挂着一个展开的标的列表，切页一律收起，免得浮在新页面上
            if (_tcDrop != null) _tcDrop.Close();
            if (_itDrop != null) _itDrop.Close();
            // 切页就把图上那层读数收干净：每张图的读数层都挂在自己那一页上，
            // 不收的话玩家转回来时就是一层旧数字浮在新图上（见用户反馈
            // 「过完新手教程再回来，之前的显示还绘制在上面」）。
            // 下一帧 UpdateReadouts 会在新页面上按鼠标位置自己重新长出来。
            HideReadouts();
            _page = page;
            // 母菜单跟着页面走：不然教程把页面带到「交易」组，左边还停在「主页」，
            // 玩家看不出自己被带到哪了，也点不回刚才那组
            ShowNavGroup(GroupOfPage(page), false);
            for (int i = 0; i < PageCount; i++) Ui.SetActive(_pages[i], i == page);
            Refresh();
            // 分时图不用等布局，所以不算在 _chartFrames 里（它每帧按状态自己决定重不重画）
            _chartFrames = (page == PageTrade || page == PageAsset || page == PageTech) ? 2 : 0;
            if (autoTour) MaybeAutoTour(page);
        }

        /// <summary>
        /// 「点进去再弹」：主线走完之后，第一次进某一页就自动放一遍该页的分步导览。
        /// 主线已经讲过的页不再弹；每页只弹一次，看过就写进位图，之后只留右上角的「?」。
        /// </summary>
        private static void MaybeAutoTour(int page)
        {
            // 已经在讲东西了就别插队（含整段引导、本页导览、说明卡）
            if (_tourOpen || _hintPage >= 0) return;
            // 只有主线走完（或跳过）之后才「点进去再弹」；tut=-1 的老存档从没跑过引导，
            // 那种档不弹 —— 老玩家早会了，挨页科普只会烦人，想看就点右上角的「?」。
            if (StockState.TutorialStep != TutorialDone) return;
            if (PageInMain(page)) return;
            if (!PageHasTour(page)) return;
            if ((StockState.TourSeen & (1 << page)) != 0) return;

            int from, to;
            PageTourRange(page, out from, out to);
            if (from < 0) return;

            StockState.TourSeen |= 1 << page;
            StockState.Dirty = true;
            LogOp("首次进入【" + NavTitles[page] + "】页，自动弹出本页导览");
            SetStatus("第一次来这页，先带你认一遍；点「关闭」或 ESC 就停。", false);
            StartTour(from, to, from, true);
        }

        private static void SetStatus(string text, bool isError)
        {
            _status = text;
            _statusError = isError;
        }

        /// <summary>
        /// 亮色主题下，把面板里所有富文本的写死色值过一遍 Palette.Rt。
        /// 面板里几十处文案是直接把深色主题的字面色值拼进去的（&lt;color=#FFC24A&gt; 这类），
        /// 一处一处包 Rt 既容易漏、也挡不住以后新加的文案，所以统一在 Refresh 收尾时扫一遍。
        /// 深色主题下 Rt 原样返回，这个循环第一步就退出了。
        /// </summary>
        private static void SweepThemeColors()
        {
            if (!Palette.IsLight || _root == null) return;
            try
            {
                TextMeshProUGUI[] all = _root.GetComponentsInChildren<TextMeshProUGUI>(true);
                if (all == null) return;
                for (int i = 0; i < all.Length; i++)
                {
                    TextMeshProUGUI t = all[i];
                    if (t == null) continue;
                    string s = t.text;
                    if (string.IsNullOrEmpty(s) || s.IndexOf('#') < 0) continue;
                    string fixedText = Palette.Rt(s);
                    if (fixedText != s) t.text = fixedText;
                }
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[界面] 主题色扫描失败：" + ex.Message);
            }
        }

        private static void Refresh()
        {
            try
            {
                if (_canvas == null) return;
                StockQuest.Sync();

                for (int i = 0; i < PageCount; i++)
                {
                    UiButton b = _navs[i];
                    if (b == null) continue;
                    bool on = i == _page;
                    b.SetBg(on ? Palette.NavOn : Palette.BtnIdle);
                    b.SetTextColor(on ? Palette.Title : Palette.Sub);
                    // 文案统一走 NavLabel：图标 + 名称 + 核心星标 + 待领奖提醒，
                    // 只在这里重建，别在别处拼字符串，否则星标会被覆盖掉
                    b.SetText(NavLabel(i));
                }

                if (_statusText != null)
                {
                    _statusText.text = _status;
                    _statusText.color = _statusError ? Palette.Up : Palette.Muted;
                }

                RefreshHeader();
                switch (_page)
                {
                    case PageOverview: RefreshOverview(); break;
                    case PageQuote: RefreshQuote(); break;
                    case PageTech: RefreshTech(); break;
                    case PageIntra: RefreshIntra(); break;
                    case PageHold: RefreshHold(); break;
                    case PageJournal: RefreshJournal(); break;
                    case PageTrade: RefreshTrade(); break;
                    case PageLever: RefreshLever(); break;
                    case PageEvent: RefreshEvent(); break;
                    case PageReport: RefreshReport(); break;
                    case PageReview: RefreshReview(); break;
                    case PageVip: RefreshVip(); break;
                    case PagePicker: RefreshPicker(); break;
                    case PageQuest: RefreshQuest(); break;
                    case PageFund: RefreshFund(); break;
                    case PageLicense: RefreshLicense(); break;
                    case PageFriend: RefreshChat(); break;
                    case PageDebug: RefreshDebug(); break;
                }

                // 好友未读的红色数字气泡（>9 显示 9+）。放在切页刷新之后算，
                // 因为 RefreshChat 会把「正看着的那位」标记成已读，这里才能拿到最新未读数。
                if (_navBadge != null)
                {
                    int un = StockChat.UnreadTotal();
                    bool on = un > 0;
                    string txt = un > 9 ? "9+" : un.ToString();
                    Ui.SetActive(_navBadge.gameObject, on);
                    Ui.SetActive(_navBadgeTx != null ? _navBadgeTx.gameObject : null, on);
                    if (on && _navBadgeTx != null) _navBadgeTx.text = txt;
                    Ui.SetActive(_navGrpBadge != null ? _navGrpBadge.gameObject : null, on);
                    Ui.SetActive(_navGrpBadgeTx != null ? _navGrpBadgeTx.gameObject : null, on);
                    if (on && _navGrpBadgeTx != null) _navGrpBadgeTx.text = txt;
                }

                RefreshTutorial();
                // 最后统一把富文本里写死的深色主题色值换成当前主题的（只对亮色主题生效）
                SweepThemeColors();
            }
            catch (Exception ex)
            {
                Core.Debug("刷新面板失败：" + ex.Message);
            }
        }

        private static void RefreshHeader()
        {
            long floating = StockEngine.FloatingPnlYuan();
            long total = StockEngine.TotalGainYuan();
            string[] values =
            {
                Money(StockEngine.TotalAssetYuan()),
                Sign(total) + Money(total),
                Sign(floating) + Money(floating),
                Money(StockEngine.PoolYuan()),
                Money(StockEngine.StockValueYuan())
            };
            Color[] colors =
            {
                Palette.Title, Palette.Pnl(total), Palette.Pnl(floating), Palette.Gold, Palette.Cyan
            };
            for (int i = 0; i < 5; i++)
            {
                if (_statValue[i] == null) continue;
                _statValue[i].text = values[i];
                _statValue[i].color = colors[i];
                _statValue[i].fontSize = i == 0 ? 20f : 17f;
            }
        }

        private static void RefreshOverview()
        {
            long realized = StockEngine.RealizedPnlYuan();
            long dividend = StockEngine.DividendYuan();
            string[] values =
            {
                "距结算 " + (StockDefs.CycleDays - StockState.DayCounter) + " 天",
                HeldCount() + " / " + StockDefs.All.Length + " 支",
                Sign(realized) + Money(realized) + " 元",
                "+" + Money(dividend) + " 元"
            };
            Color[] colors = { Palette.Gold, Palette.Gold, Palette.Pnl(realized), dividend > 0 ? Palette.Down : Palette.Muted };
            for (int i = 0; i < 4; i++)
            {
                if (_ovValue[i] == null) continue;
                _ovValue[i].text = values[i];
                _ovValue[i].color = colors[i];
            }

            if (_ovHold == null) return;
            StringBuilder sb = new StringBuilder();
            int lines = 0;
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef stock = StockDefs.All[i];
                int shares = StockState.GetPosition(stock.Id);
                if (shares <= 0) continue;
                long cost = StockState.GetCost(stock.Id);
                // 市值走盘中价：一是要跟着分时图跳，二是拿收盘价算等于把今日收盘写在脸上
                long value = shares * StockEngine.LivePrice(stock.Id);
                double pnl = StockState.ToYuan(value - cost);
                sb.Append(stock.Name).Append("　").Append(shares).Append(" 股　市值 ")
                  .Append(Money(StockState.ToYuan(value))).Append("　")
                  .Append(Sign(pnl)).Append(Money(pnl))
                  .Append("　<color=#").Append(ColorHex(Palette.Pnl(pnl))).Append(">")
                  .Append(Sign(pnl)).Append(pnl.ToString("0.0")).Append("%</color>\n");
                lines++;
            }
            if (lines == 0) sb.Append("暂无持仓。\n");
            sb.Append("\n浮动盈亏 ").Append(Sign(StockEngine.FloatingPnlYuan()))
              .Append(Money(StockEngine.FloatingPnlYuan()))
              .Append(" 元　已实现 ").Append(Sign(realized)).Append(Money(realized))
              .Append(" 元　累计分红 +").Append(Money(dividend))
              .Append(" 元　总收益 ").Append(Sign(StockEngine.TotalGainYuan()))
              .Append(Money(StockEngine.TotalGainYuan())).Append(" 元");
            sb.Append("\n黑市开户：").Append(StockState.License ? "已开通" : "未开通")
              .Append("　累计交易日 ").Append(StockState.Today).Append(" 天");
            _ovHold.text = sb.ToString();
        }

        private static void RefreshQuote()
        {
            if (_qCell == null) return;
            List<StockDef> list = DisplayList(false, true);
            int pages = PageCountOf(list, PageRows);
            _quotePage = Mathf.Clamp(_quotePage, 0, pages - 1);

            for (int i = 0; i < PageRows; i++)
            {
                StockDef def = At(list, _quotePage, i, PageRows);
                bool show = def != null;
                Ui.SetActive(_qRow != null ? _qRow[i] : null, show);
                if (_qStar != null && _qStar[i] != null) _qStar[i].SetActive(show);
                if (!show)
                {
                    for (int c = 0; c < QuoteCols; c++)
                    {
                        if (_qCell[i, c] != null) Ui.SetActive(_qCell[i, c].gameObject, false);
                    }
                    continue;
                }

                if (_qStar != null && _qStar[i] != null)
                {
                    bool fav = StockState.IsFavorite(def.Id);
                    _qStar[i].SetText(fav ? "★" : "☆");
                    _qStar[i].SetTextColor(fav ? Palette.Gold : Palette.Muted);
                }

                bool locked = def.NeedLicense && !StockState.License;
                double price = StockEngine.LivePriceYuan(def.Id);
                double change = StockEngine.DayChangePercent(def.Id);
                int held = StockState.GetPosition(def.Id);
                int trend = StockEngine.TrendOf(def.Id);

                string flowText = StockBots.FlowText(def.Id);
                double flowYuan = StockBots.NetInflowYuan(def.Id);

                string[] texts =
                {
                    def.Name,
                    StockDefs.Tag(def),
                    price.ToString("0.00"),
                    Sign(change) + change.ToString("0.00") + "%",
                    flowText,
                    StockEngine.TrendText(def.Id),
                    held > 0 ? "持仓 " + held + " 股" : (locked ? "未开户" : "可交易")
                };
                // 封板标记直接缀在公司名后面：涨跌停是当天最要紧的一条信息，
                // 扫一眼列表就得看见。名字最多五个字，这一列放得下。
                int board = StockEngine.BoardState(def.Id);
                if (board != 0)
                {
                    texts[0] = def.Name + " <color=#" + ColorHex(Palette.Change(board))
                        + ">" + (board > 0 ? "涨停" : "跌停") + "</color>";
                }
                Color[] colors =
                {
                    locked ? Palette.Muted : Palette.Title,
                    Palette.Muted,
                    locked ? Palette.Muted : Palette.CardBody,
                    Palette.Change(change),
                    Palette.Change(flowYuan),
                    trend == 0 ? Palette.Up : (trend == 1 ? Palette.Down : Palette.Muted),
                    held > 0 ? Palette.Cyan : (locked ? Palette.Danger : Palette.Muted)
                };
                for (int c = 0; c < QuoteCols; c++)
                {
                    TextMeshProUGUI cell = _qCell[i, c];
                    if (cell == null) continue;
                    Ui.SetActive(cell.gameObject, true);
                    cell.text = texts[c];
                    cell.color = colors[c];
                }
            }

            Ui.SetActive(_qEmpty != null ? _qEmpty.gameObject : null, list.Count == 0);
            if (_qEmpty != null && list.Count == 0)
            {
                _qEmpty.text = _query.Length > 0
                    ? "没有匹配「" + _query + "」的标的。"
                    : "暂无可交易标的。";
            }
            if (_qPageText != null)
            {
                _qPageText.text = list.Count == 0 ? "—"
                    : "第 " + (_quotePage + 1) + " / " + pages + " 页　共 " + list.Count + " 支"
                      + (StockState.Favorites.Count > 0 ? "　已收藏 " + StockState.Favorites.Count + " 支" : "");
            }
            DimPager(_qPrev, _qNext, _quotePage, pages);
        }

        private static void RefreshHold()
        {
            if (_hCell == null) return;
            List<StockDef> list = DisplayList(true, false);
            int pages = PageCountOf(list, PageRows);
            _holdPage = Mathf.Clamp(_holdPage, 0, pages - 1);

            for (int i = 0; i < PageRows; i++)
            {
                StockDef def = At(list, _holdPage, i, PageRows);
                bool show = def != null;
                Ui.SetActive(_hRow != null ? _hRow[i] : null, show);
                if (!show)
                {
                    for (int c = 0; c < 5; c++)
                    {
                        if (_hCell[i, c] != null) Ui.SetActive(_hCell[i, c].gameObject, false);
                    }
                    continue;
                }

                int shares = StockState.GetPosition(def.Id);
                long cost = StockState.GetCost(def.Id);
                long value = shares * StockEngine.LivePrice(def.Id);
                double pnl = StockState.ToYuan(value - cost);
                double rate = cost > 0 ? (double)(value - cost) / cost * 100.0 : 0.0;

                string[] texts =
                {
                    def.Name,
                    shares.ToString(),
                    Money(StockState.ToYuan(cost)),
                    Money(StockState.ToYuan(value)),
                    Sign(pnl) + Money(pnl) + "（" + Sign(rate) + rate.ToString("0.0") + "%）"
                };
                for (int c = 0; c < 5; c++)
                {
                    TextMeshProUGUI cell = _hCell[i, c];
                    if (cell == null) continue;
                    Ui.SetActive(cell.gameObject, true);
                    cell.text = texts[c];
                    cell.color = c == 0 ? Palette.Title : (c == 4 ? Palette.Pnl(pnl) : Palette.CardBody);
                }
            }

            Ui.SetActive(_hEmpty != null ? _hEmpty.gameObject : null, list.Count == 0);
            if (_hPageText != null)
            {
                _hPageText.text = list.Count == 0 ? "—"
                    : "第 " + (_holdPage + 1) + " / " + pages + " 页　共 " + list.Count + " 支持仓";
            }
            DimPager(_hPrev, _hNext, _holdPage, pages);

            if (_hSummary != null)
            {
                long floating = StockEngine.FloatingPnlYuan();
                _hSummary.text = "浮动盈亏（市值 − 含手续费成本）" + Sign(floating) + Money(floating)
                    + " 元　已实现 " + Sign(StockEngine.RealizedPnlYuan()) + Money(StockEngine.RealizedPnlYuan())
                    + " 元　累计分红 +" + Money(StockEngine.DividendYuan()) + " 元";
                _hSummary.color = Palette.Pnl(floating);
            }
        }

        private static void RefreshTrade()
        {
            List<StockDef> list = DisplayList(false, false);
            int pages = PageCountOf(list, PageRows);
            _tradePage = Mathf.Clamp(_tradePage, 0, pages - 1);

            if (_tPick != null)
            {
                for (int i = 0; i < _tPick.Length; i++)
                {
                    UiButton b = _tPick[i];
                    if (b == null) continue;
                    StockDef d = At(list, _tradePage, i, PageRows);
                    if (d == null)
                    {
                        b.SetActive(false);
                        continue;
                    }
                    b.SetActive(true);
                    bool locked = d.NeedLicense && !StockState.License;
                    bool on = d.Id == _selectedId;
                    b.SetBg(on ? Palette.BtnBlue : Palette.BtnIdle);
                    b.SetTextColor(on ? Palette.Title : (locked ? Palette.Muted : Palette.Sub));
                    b.SetText((locked ? "锁·" : "") + d.Name);
                }
            }
            if (_tPickPageText != null) _tPickPageText.text = (_tradePage + 1) + "/" + pages;
            DimPager(_tPickPrev, _tPickNext, _tradePage, pages);

            StockDef def = Current();
            if (def == null) return;    // 没选中标的（列表空 / 翻页越界）时收工，后面全是 def.xxx
            double price = StockEngine.LivePriceYuan(def.Id);
            double change = StockEngine.DayChangePercent(def.Id);
            double need = price * _amount * (1.0 + def.FeeRate);
            int held = StockState.GetPosition(def.Id);
            bool notLicensed = def.NeedLicense && !StockState.License;

            if (_tName != null)
            {
                _tName.text = def.Name;
                _tName.color = notLicensed ? Palette.Danger : Palette.Cyan;
            }
            if (_tTag != null)
            {
                _tTag.text = StockDefs.Tag(def);
                _tTag.color = notLicensed ? Palette.Danger : Palette.Cyan;
            }

            SetStat(0, price.ToString("0.00"), Palette.Change(change));
            if (_tPriceLab != null)
            {
                // 盘中读数和最终收盘价必须分得清，不然玩家不知道这个价还会不会变
                bool live = StockIntraday.IsLive;
                _tPriceLab.text = live ? "现价（盘中）" : "现价（收盘）";
                _tPriceLab.color = live ? Palette.Gold : Palette.Muted;
            }
            SetStat(1, Sign(change) + change.ToString("0.0") + "%", Palette.Change(change));
            int trend = StockEngine.TrendOf(def.Id);
            SetStat(2, StockEngine.TrendText(def.Id),
                trend == 0 ? Palette.Up : (trend == 1 ? Palette.Down : Palette.Muted));
            SetStat(3, held + " 股", Palette.CardBody);
            SetStat(4, notLicensed ? "未开户" : MaxBuyable() + " 股",
                notLicensed ? Palette.Danger : Palette.CardBody);
            SetStat(5, StockEngine.RiskStars(def.Risk),
                def.Risk >= 4 ? Palette.Danger : (def.Risk == 3 ? Palette.Gold : Palette.Down));

            // 数量输入框：黑市未开户时整行换成警告，免得玩家敲了半天才发现买不了
            if (_tQtyLab != null) Ui.SetActive(_tQtyLab.gameObject, !notLicensed);
            if (_tQtyInput != null) Ui.SetActive(_tQtyInput.gameObject, !notLicensed);
            if (_tQtyWarn != null) Ui.SetActive(_tQtyWarn.gameObject, notLicensed);
            if (_tQtyHint != null)
            {
                _tQtyHint.text = notLicensed
                    ? ""
                    : "约需 " + Money(need) + " 元（费 "
                      + (def.FeeRate * 100.0).ToString("0.0") + "%）";
                _tQtyHint.color = Palette.Title;
            }
            // 步进按钮或换标的后把当前股数回填一次；玩家正在输入时别抢他的光标
            if (_tQtyInput != null && !InputFocused(_tQtyInput)) SyncQtyInput();

            if (_tBuy != null)
            {
                _tBuy.SetInteractable(!notLicensed);
                _tBuy.SetBg(notLicensed ? Palette.BtnIdle : Palette.BtnBlue);
                _tBuy.SetTextColor(notLicensed ? Palette.Muted : Palette.Title);
            }
        }

        private static void SetStat(int i, string text, Color color)
        {
            if (i < 0 || i >= _tStat.Length || _tStat[i] == null) return;
            _tStat[i].text = text;
            _tStat[i].color = color;
        }

        private static void RefreshEvent()
        {
            if (_evInfo == null) return;
            float colW = Mathf.Floor((ContentW - 48f - 12f) / 2f);

            // 顺手把游戏事件重读一遍：玩家可能一整天开着面板，中间站里出事了他该看得见。
            // 传 false 表示「不出夜间新闻」，播报只归每日推进那一次管。
            StockGameEvents.Refresh(false);
            List<StockGameEvents.View> ge = StockGameEvents.Active;

            int left = StockDefs.CycleDays - StockState.DayCounter;
            if (left < 0) left = 0;
            _evInfo.text = "距结算 " + left + " / " + StockDefs.CycleDays
                + " 天（结算日按持仓派息、并结算派系声望）　行情记录 "
                + StockState.History.Count + " 个交易日";
            SetBar(_evCycleBar, ContentW - 48f, (float)StockState.DayCounter / StockDefs.CycleDays);

            // ── 左栏：空间站事件 ────────────────────────────────────────
            if (_evLHead != null)
            {
                string head = "空间站事件 · 游戏内实时　共 " + ge.Count + " 条";
                if (StockGameEvents.HiddenCount > 0)
                {
                    head += "　<color=" + Palette.HexOf(Palette.Muted) + ">另有 "
                        + StockGameEvents.HiddenCount + " 条未公开异动</color>";
                }
                _evLHead.text = Palette.Rt(head);
            }
            int shown = 0;
            for (int i = 0; i < _geRows; i++)
            {
                // 未公开的事件不点名：只报数量，让玩家知道「有事但查不到」
                while (shown < ge.Count && ge[shown].Hidden) shown++;
                StockGameEvents.View v = shown < ge.Count ? ge[shown] : null;
                if (v != null) shown++;
                bool show = v != null && _geRow[i] != null;
                Ui.SetActive(_geRow[i], show);
                if (!show) continue;

                Color tone = Palette.Change(v.Net);
                if (_geStrip[i] != null) _geStrip[i].color = tone;
                if (_geName[i] != null)
                {
                    _geName[i].text = Palette.Rt(v.Name
                        + "　<size=13><color=" + Palette.HexOf(Palette.Muted) + ">["
                        + v.Area + "·" + v.Kind + "]</color></size>");
                }
                if (_geLeft[i] != null)
                {
                    _geLeft[i].text = StockGameEvents.EndText(v.Left);
                    _geLeft[i].color = v.Left >= 0 ? Palette.GoldSoft : Palette.Muted;
                }
                if (_geTarget[i] != null)
                    _geTarget[i].text = Ui.Cut("影响　" + v.Targets, FsSmall, colW - 32f);
                if (_geDesc[i] != null) _geDesc[i].text = v.Summary;
            }
            Ui.SetActive(_geEmptyBox, ge.Count == 0);

            // ── 右栏：市场事件 ──────────────────────────────────────────
            for (int i = 0; i < EventCardRows; i++)
            {
                ActiveEvent ev = i < StockState.Events.Count ? StockState.Events[i] : null;
                StockEventDef def = ev != null ? StockDefs.GetEvent(ev.DefId) : null;
                bool show = def != null;
                Ui.SetActive(_evCard[i], show);
                if (!show) continue;

                Color tone = Palette.Change(def.Impact);
                if (_evName[i] != null) _evName[i].text = def.Name;
                if (_evStrip[i] != null) _evStrip[i].color = tone;
                if (_evImpact[i] != null)
                {
                    double pct = def.Impact * 100.0;
                    _evImpact[i].text = Sign(pct) + pct.ToString("0.0") + "%";
                    _evImpact[i].color = tone;
                }
                if (_evTarget[i] != null)
                    _evTarget[i].text = Ui.Cut("影响范围　" + TargetText(ev, def), FsSmall, colW - 36f);
                // 只说「有多猛、势头还在不在」，不写还剩几天 ——
                // 写清楚了玩家就能卡着最后一天进场，把事件持续性变成算术题。
                // （空间站事件是例外：那是游戏自己的日历，天数本来就摆在玩家眼前。）
                if (_evDays[i] != null)
                {
                    _evDays[i].text = StockEngine.SeverityText(def, ev.DaysLeft)
                        + (ev.IntradayDay == StockState.Today ? "　（今日盘中发布）" : string.Empty);
                }
                if (_evSummary[i] != null) _evSummary[i].text = def.Summary;
            }

            Ui.SetActive(_evEmptyBox, StockState.Events.Count == 0);
        }

        /// <summary>事件影响范围的文字描述：随机单支 / 指定几支 / 全表。</summary>
        private static string TargetText(ActiveEvent ev, StockEventDef def)
        {
            if (ev.TargetStockId != null)
            {
                StockDef t = StockDefs.Get(ev.TargetStockId);
                return t != null ? t.Name + "（" + t.Id + "）" : ev.TargetStockId;
            }
            // 目标铺满全表的（事件表里写的 AllIds()）也算全市场：
            // 把 27 个名字全列出来既读不完、也没信息量
            if (def.Targets == null || def.Targets.Length == 0 || def.Targets.Length >= StockDefs.All.Length)
                return "全市场所有标的";

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < def.Targets.Length; i++)
            {
                StockDef t = StockDefs.Get(def.Targets[i]);
                if (i > 0) sb.Append(" · ");
                sb.Append(t != null ? t.Name : def.Targets[i]);
            }
            return sb.ToString();
        }

        /// <summary>按比例缩放进度条填充块的宽度（ratio 0..1）。</summary>
        private static void SetBar(Image fill, float fullWidth, float ratio)
        {
            if (fill == null) return;
            if (ratio < 0f) ratio = 0f;
            if (ratio > 1f) ratio = 1f;
            RectTransform rt = fill.rectTransform;
            if (rt == null) return;
            rt.sizeDelta = new Vector2(fullWidth * ratio, rt.sizeDelta.y);
        }

        private static void RefreshFund()
        {
            if (_fdCash != null) _fdCash.text = StoreCash() + " 元";
            if (_fdPool != null) _fdPool.text = StockEngine.PoolYuan() + " 元";
            if (_fdIn != null) _fdIn.SetText("转入股市 " + _fundAmount + " 元");
            if (_fdOut != null) _fdOut.SetText("提现到店铺 " + _fundAmount + " 元");
            // 步进按钮改完金额后回填输入框；玩家正在输入时别抢他的光标
            if (_fdAmountInput != null && !InputFocused(_fdAmountInput)) SyncFundInput();
            if (_fdHint != null)
            {
                _fdHint.text = "店铺现金和股票账户是两个分开的钱包，只能在这一页互转。\n"
                    + "买入只从股票账户扣款，卖出的钱也留在股票账户，要用钱时再来提现。\n\n"
                    + "<color=#FFC24A>各层区手续费不一样（买、卖各收一次）：</color>\n"
                    + "治安部 0.6%　下层区 0.8%　上层区 1.2%　革命军 2.5%　黑市 4.5%\n"
                    + "手续费越低的层区波动也越小，想反复倒腾先去便宜的层区练手。";
            }
        }

        private static void RefreshLicense()
        {
            if (_lcInfo != null)
            {
                if (StockState.License)
                {
                    _lcInfo.text = "黑市开户证明已经办妥。\n\n"
                        + "黑鲨物流、德莱鲁、卡什尼科夫、巴斯福、拜尔制药\n"
                        + "这 5 支黑市标的已可在【行情报价】和【交易下单】里操作。\n\n"
                        + "<color=#FFC24A>注意：黑市股手续费 4.5%（买、卖各收一次），"
                        + "而且单日能跌 50% 以上，是给敢冒险的人准备的。</color>";
                }
                else
                {
                    long cash = StoreCash();
                    StringBuilder sb = new StringBuilder();
                    sb.Append("办一张黑市开户证明，之后就能交易这 5 支：\n\n");
                    sb.Append("　　黑鲨物流　　德莱鲁　　卡什尼科夫　　巴斯福　　拜尔制药\n\n");
                    sb.Append("价格　").Append(LicenseCost).Append(" 元\n");
                    sb.Append("店铺现金　").Append(cash).Append(" 元\n\n");
                    sb.Append(cash >= LicenseCost
                        ? "<color=#45C08A>现金充足，可以开户。</color>"
                        : "<color=#D05020>现金不足，先去凑够再来。</color>");
                    _lcInfo.text = sb.ToString();
                }
            }
            if (_lcBuy != null)
            {
                _lcBuy.SetText(StockState.License ? "已开通（无需重复购买）" : "购买黑市开户证明 · " + LicenseCost + " 元");
                _lcBuy.SetBg(StockState.License ? Palette.BtnIdle : Palette.BtnBlue);
                _lcBuy.SetInteractable(!StockState.License);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  技术分析
        // ══════════════════════════════════════════════════════════════

        private static void RefreshTech()
        {
            StockDef def = Current();
            if (def == null) return;

            if (_tcName != null) _tcName.text = def.Name;
            if (_tcTag != null) _tcTag.text = StockDefs.Tag(def);
            if (_tcDrop != null) _tcDrop.Sync(_selectedId);
            if (_tcKind != null) _tcKind.SetText(_tcAsLine ? "折线图" : "蜡烛图");
            if (_tcOverlay != null) _tcOverlay.SetText(OverlayText());
            // 标题就是换指标的按钮，所以提示直接写在标题里，不用另加一排按钮
            if (_tcSubTitle1 != null)
                _tcSubTitle1.SetText("副图①　" + SubNames[_tcSubIdx1] + "　点这里换");
            if (_tcSubTitle2 != null)
                _tcSubTitle2.SetText("副图②　" + SubNames[_tcSubIdx2] + "　点这里换");
            if (_tcSubLegend1 != null) _tcSubLegend1.text = SubLegend(_tcSubIdx1);
            if (_tcSubLegend2 != null) _tcSubLegend2.text = SubLegend(_tcSubIdx2);

            for (int i = 0; i < _tcRange.Length; i++)
            {
                UiButton b = _tcRange[i];
                if (b == null) continue;
                bool on = i == _tcRangeIdx;
                b.SetBg(on ? Palette.BtnBlue : Palette.BtnIdle);
                b.SetTextColor(on ? Palette.Title : Palette.Sub);
            }

            if (_tcInfo == null) return;
            int days = RangeDays[Mathf.Clamp(_tcRangeIdx, 0, RangeDays.Length - 1)];
            List<Bar> bars = StockIndicators.BuildBars(def.Id, StockState.PriceWindow(def.Id, days));
            if (bars.Count < 2)
            {
                _tcInfo.text = "行情记录不足，先推进几个游戏日再来看。";
                return;
            }
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < bars.Count; i++)
            {
                if (bars[i].Low < lo) lo = bars[i].Low;
                if (bars[i].High > hi) hi = bars[i].High;
            }
            _tcInfo.text = bars.Count + " 天　" + lo.ToString("0.00")
                + " ~ " + hi.ToString("0.00") + " 元";
        }

        private static string OverlayText()
        {
            switch (_tcOverlayIdx)
            {
                case 1: return "叠加：均线（金MA5 青MA20）";
                case 2: return "叠加：布林带（蓝上下轨 金中轨）";
                default: return "叠加：无";
            }
        }

        /// <summary>
        /// 副图里每条线分别代表什么。图上一共就两三条线，不写清楚玩家只能靠猜，
        /// 所以直接挂在副图标题右边，一眼对上颜色。
        /// </summary>
        private static string SubLegend(int idx)
        {
            switch (idx)
            {
                case 0: return "柱=成交量　金线=5日均量";
                case 1: return "蓝=DIF　金=DEA　柱=两者之差";
                case 2: return "蓝=K　金=D　青=J";
                case 3: return "单线=RSI　70 以上偏热，30 以下偏冷";
                case 4: return "单线=OBV　跟着成交量走的能量线";
                default: return "单线=CCI　±100 之外算异常";
            }
        }

        private static void SetRange(int idx)
        {
            if (idx == _tcRangeIdx) return;
            _tcRangeIdx = Mathf.Clamp(idx, 0, RangeNames.Length - 1);
            LogOp("技术分析时间档 → 近" + RangeNames[_tcRangeIdx]);
            SetStatus("技术分析：近" + RangeNames[_tcRangeIdx] + "走势。", false);
            Refresh();
            if (_page == PageTech) DrawTechChart();
        }

        private static void ToggleTechKind()
        {
            _tcAsLine = !_tcAsLine;
            LogOp("技术分析主图切换为" + (_tcAsLine ? "折线图" : "蜡烛图"));
            SetStatus(_tcAsLine ? "主图已切换为折线图。" : "主图已切换为蜡烛图。", false);
            Refresh();
            if (_page == PageTech) DrawTechChart();
        }

        private static void CycleOverlay()
        {
            _tcOverlayIdx = (_tcOverlayIdx + 1) % 3;
            LogOp("技术分析叠加 → " + OverlayText());
            SetStatus("主图叠加：" + OverlayText().Substring(3) + "。", false);
            Refresh();
            if (_page == PageTech) DrawTechChart();
        }

        private static void CycleSub(bool first)
        {
            // 两块副图撞成同一个指标等于白占一块地方，所以撞了就再往后顺一格
            int other = first ? _tcSubIdx2 : _tcSubIdx1;
            int next = ((first ? _tcSubIdx1 : _tcSubIdx2) + 1) % SubNames.Length;
            if (next == other) next = (next + 1) % SubNames.Length;
            if (first) _tcSubIdx1 = next;
            else _tcSubIdx2 = next;
            string name = SubNames[first ? _tcSubIdx1 : _tcSubIdx2];
            LogOp("副图" + (first ? "①" : "②") + "切换为 " + name);
            SetStatus("副图" + (first ? "①" : "②") + "已切换为 " + name + "。", false);
            Refresh();
            if (_page == PageTech) DrawTechChart();
        }

        /// <summary>在全部标的里前后翻一支。技术页和杠杆页共用。</summary>
        private static void TechStep(int delta)
        {
            List<StockDef> list = DisplayList(false, false);
            if (list.Count == 0) return;
            int cur = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Id == _selectedId) { cur = i; break; }
            }
            int next = (cur + delta + list.Count) % list.Count;
            SelectStock(list[next].Id);
        }

        /// <summary>
        /// 技术分析主图 + 两个副图。三张图共用同一份 K 线，横轴对齐，
        /// 主图的叠加线（均线 / 布林带）也一起参与纵轴取值，免得上下轨被裁掉。
        /// </summary>
        private static void DrawTechChart()
        {
            StockDef def = Current();
            if (def == null) return;

            int days = RangeDays[Mathf.Clamp(_tcRangeIdx, 0, RangeDays.Length - 1)];
            List<Bar> bars = StockIndicators.BuildBars(def.Id, StockState.PriceWindow(def.Id, days));
            int n = bars.Count;
            string[] labels = new string[n];
            for (int i = 0; i < n; i++) labels[i] = i == n - 1 ? "现在" : "-" + (n - 1 - i);

            float w = ContentW - 48f;
            List<double> closes = StockIndicators.Closes(bars);
            List<ChartSeries> overlays = BuildOverlays(closes);

            if (_tcMain != null)
            {
                if (_tcAsLine)
                {
                    // 折线图：收盘价一条线，叠加线跟在后面一起画
                    List<ChartSeries> series = new List<ChartSeries>();
                    series.Add(new ChartSeries
                    {
                        Label = def.Name,
                        Color = Palette.Series1,
                        Values = closes
                    });
                    if (overlays != null) series.AddRange(overlays);
                    StockChart.DrawLines(_tcMain, w, TechMainH, series, labels);
                    // 折线图没有四价可报，报这一格的收盘价 + 各条叠加线
                    TipData(_tipTechMain, "收盘", labels, null, false, series, null, null);
                }
                else
                {
                    StockChart.DrawCandles(_tcMain, w, TechMainH, bars, labels, overlays);
                    TipData(_tipTechMain, "K 线", labels, bars, true, overlays, null, null);
                }
            }

            DrawSub(_tcSub1, _tcSubIdx1, _tipTechSub1, bars, closes, labels, w, TechSubH);
            DrawSub(_tcSub2, _tcSubIdx2, _tipTechSub2, bars, closes, labels, w, TechSubH);
        }

        /// <summary>主图上的叠加线。返回 null 表示「不叠加」。</summary>
        private static List<ChartSeries> BuildOverlays(List<double> closes)
        {
            if (_tcOverlayIdx == 0 || closes.Count < 2) return null;
            List<ChartSeries> list = new List<ChartSeries>();
            if (_tcOverlayIdx == 1)
            {
                list.Add(new ChartSeries { Label = "MA5", Color = Palette.Gold,
                    Values = new List<double>(StockIndicators.MA(closes, 5)) });
                list.Add(new ChartSeries { Label = "MA20", Color = Palette.Cyan,
                    Values = new List<double>(StockIndicators.MA(closes, 20)) });
                return list;
            }
            double[] mid, up, low;
            StockIndicators.Boll(closes, 20, 2.0, out mid, out up, out low);
            list.Add(new ChartSeries { Label = "上轨", Color = Palette.Series1, Values = new List<double>(up) });
            list.Add(new ChartSeries { Label = "中轨", Color = Palette.Gold, Values = new List<double>(mid) });
            list.Add(new ChartSeries { Label = "下轨", Color = Palette.Series1, Values = new List<double>(low) });
            return list;
        }

        /// <summary>
        /// 画一个副图。idx 对应 SubNames 的顺序：
        /// 0=成交量 1=MACD 2=KDJ 3=RSI 4=OBV 5=CCI。
        /// </summary>
        private static void DrawSub(RectTransform host, int idx, ChartTip tip, List<Bar> bars,
            List<double> closes, string[] labels, float w, float h)
        {
            if (host == null) return;
            if (bars.Count < 2)
            {
                StockChart.DrawOsc(host, w, h, new List<ChartSeries>(), null, labels,
                    double.NaN, double.NaN);
                TipData(tip, SubNames[idx], null, null, false, null, null, null);
                return;
            }

            switch (idx)
            {
                case 0:
                {
                    double[] vma = StockIndicators.MA(StockIndicators.Volumes(bars), 5);
                    StockChart.DrawVolume(host, w, h, bars, new List<double>(vma), labels);
                    List<ChartSeries> lines = new List<ChartSeries>();
                    lines.Add(new ChartSeries { Label = "量均线", Color = Palette.Gold,
                        Values = new List<double>(vma) });
                    TipData(tip, "成交量", labels, bars, false, lines, null, null);
                    return;
                }
                case 1:
                {
                    double[] dif, dea, hist;
                    StockIndicators.Macd(closes, 12, 26, 9, out dif, out dea, out hist);
                    List<ChartSeries> lines = new List<ChartSeries>();
                    lines.Add(new ChartSeries { Label = "DIF", Color = Palette.Series1,
                        Values = new List<double>(dif) });
                    lines.Add(new ChartSeries { Label = "DEA", Color = Palette.Gold,
                        Values = new List<double>(dea) });
                    StockChart.DrawOsc(host, w, h, lines, new List<double>(hist), labels,
                        double.NaN, double.NaN);
                    TipData(tip, "MACD", labels, null, false, lines, hist, "MACD柱");
                    return;
                }
                case 2:
                {
                    double[] kk, dd, jj;
                    StockIndicators.Kdj(bars, 9, out kk, out dd, out jj);
                    List<ChartSeries> lines = new List<ChartSeries>();
                    lines.Add(new ChartSeries { Label = "K", Color = Palette.Series1,
                        Values = new List<double>(kk) });
                    lines.Add(new ChartSeries { Label = "D", Color = Palette.Gold,
                        Values = new List<double>(dd) });
                    lines.Add(new ChartSeries { Label = "J", Color = Palette.Cyan,
                        Values = new List<double>(jj) });
                    StockChart.DrawOsc(host, w, h, lines, null, labels, double.NaN, double.NaN);
                    TipData(tip, "KDJ", labels, null, false, lines, null, null);
                    return;
                }
                case 3:
                {
                    // RSI 固定 0~100 更好读：50 是分界，70/30 是常用的超买超卖线
                    List<ChartSeries> lines = new List<ChartSeries>();
                    lines.Add(new ChartSeries { Label = "RSI14", Color = Palette.Gold,
                        Values = new List<double>(StockIndicators.Rsi(closes, 14)) });
                    StockChart.DrawOsc(host, w, h, lines, null, labels, 0.0, 100.0);
                    TipData(tip, "RSI", labels, null, false, lines, null, null);
                    return;
                }
                case 4:
                {
                    double[] obv = StockIndicators.Obv(closes, StockIndicators.Volumes(bars));
                    List<ChartSeries> lines = new List<ChartSeries>();
                    lines.Add(new ChartSeries { Label = "OBV", Color = Palette.Series2,
                        Values = new List<double>(obv) });
                    lines.Add(new ChartSeries { Label = "OBV均线", Color = Palette.Gold,
                        Values = new List<double>(StockIndicators.MA(new List<double>(obv), 10)) });
                    StockChart.DrawOsc(host, w, h, lines, null, labels, double.NaN, double.NaN);
                    TipData(tip, "OBV", labels, null, false, lines, null, null);
                    return;
                }
                default:
                {
                    List<ChartSeries> lines = new List<ChartSeries>();
                    lines.Add(new ChartSeries { Label = "CCI14", Color = Palette.Series1,
                        Values = new List<double>(StockIndicators.Cci(bars, 14)) });
                    StockChart.DrawOsc(host, w, h, lines, null, labels, double.NaN, double.NaN);
                    TipData(tip, "CCI", labels, null, false, lines, null, null);
                    return;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  杠杆交易
        // ══════════════════════════════════════════════════════════════

        private static LeverageAccount LvAccount()
        {
            return _lvShadow ? StockLeverage.Shadow : StockLeverage.Reg;
        }

        private static void LvTab(bool shadow)
        {
            if (_lvShadow == shadow) return;
            _lvShadow = shadow;
            LogOp("杠杆页切换到【" + (shadow ? "场外配资" : "融资融券") + "】");
            SetStatus(shadow ? "场外配资：杠杆高、利息贵、跌一点就爆仓。" : "融资融券：正规渠道，稳一点。", false);
            Refresh();
        }

        private static void LvSetLevel(int idx)
        {
            LeverageAccount a = LvAccount();
            int[] levels = _lvShadow ? StockLeverage.ShadowLevels : RegLevels;
            if (idx < 0 || idx >= levels.Length) return;
            if (a.Leverage == levels[idx]) return;
            LogOp(a.Name + "杠杆档位 → " + levels[idx] + " 倍");
            string err;
            string msg = StockLeverage.SetLeverage(a, levels[idx], out err);
            if (msg == null) SetStatus(err, true);
            else { SetStatus(msg + "。", false); Core.Log.Msg("[杠杆] " + msg); }
            Refresh();
        }

        /// <summary>
        /// 杠杆页左卡明细的两列排布：把一格补到 22 个「半角宽」（汉字算 2、数字算 1），
        /// 第二列才真的对得齐。补的是全角空格，所以按 2 半角一格往上凑。
        /// </summary>
        private static string LvPad(string plain)
        {
            int w = 0;
            for (int i = 0; i < plain.Length; i++) w += plain[i] < 128 ? 1 : 2;
            int pad = 22 - w;
            if (pad <= 0) return string.Empty;
            return new string('　', (pad + 1) / 2);
        }

        private static void RefreshLever()
        {
            LeverageAccount a = LvAccount();
            StockDef def = Current();

            if (_lvTabReg != null)
            {
                _lvTabReg.SetBg(_lvShadow ? Palette.BtnIdle : Palette.BtnBlue);
                _lvTabReg.SetTextColor(_lvShadow ? Palette.Sub : Palette.Title);
            }
            if (_lvTabShadow != null)
            {
                _lvTabShadow.SetBg(_lvShadow ? Palette.BtnBlue : Palette.BtnIdle);
                _lvTabShadow.SetTextColor(_lvShadow ? Palette.Title : Palette.Sub);
            }

            if (_lvTitle != null)
            {
                _lvTitle.text = a.Name + (a.Open ? "　已开户" : "　未开户");
                _lvTitle.color = a.Open ? Palette.Title : Palette.Muted;
            }

            int[] levels = _lvShadow ? StockLeverage.ShadowLevels : RegLevels;
            for (int i = 0; i < _lvLevels.Length; i++)
            {
                UiButton b = _lvLevels[i];
                if (b == null) continue;
                if (i >= levels.Length) { b.SetActive(false); continue; }
                b.SetActive(true);
                bool on = a.Leverage == levels[i];
                b.SetText(levels[i] + " 倍");
                b.SetBg(on ? Palette.BtnBlue : Palette.BtnIdle);
                b.SetTextColor(on ? Palette.Title : Palette.Sub);
                b.SetInteractable(a.Open && a.Idle);
            }

            if (_lvPickName != null)
                _lvPickName.text = def != null ? def.Name : "-";

            // ── 担保比例 ──────────────────────────────────────────────
            long liab = StockLeverage.Liability(a);
            double ratio = StockLeverage.Ratio(a);
            bool noDebt = liab <= 0;
            if (_lvRatio != null)
            {
                _lvRatio.text = noDebt ? "—" : (ratio * 100.0).ToString("0") + "%";
                _lvRatio.color = noDebt ? Palette.Muted
                    : (ratio < a.CallRatio ? Palette.Danger
                       : (ratio < a.WarnRatio ? Palette.Gold : Palette.Down));
            }
            if (_lvRatioHint != null)
            {
                _lvRatioHint.text = noDebt
                    ? "还没欠钱"
                    : "强平 " + (a.CallRatio * 100.0).ToString("0")
                      + "%　预警 " + (a.WarnRatio * 100.0).ToString("0") + "%";
            }

            if (_lvRows != null)
            {
                // 两列排：每格按「半角宽」补全角空格，第二列才真的对得齐。
                // 颜色标签不参与算宽，所以先量纯文本、再补白、最后把颜色包上去。
                string a1 = "总资产 " + Money(StockEngine.TotalAssetYuan()) + " 元";
                string a2 = "股票账户 " + Money(StockState.ToYuan(StockState.Pool)) + " 元";
                string b1 = "持仓市值 " + Money(StockEngine.StockValueYuan()) + " 元";
                string b2 = "已借欠款 " + Money(StockState.ToYuan(a.Debt)) + " 元";
                string c1 = "累计付息 " + Money(StockState.ToYuan(a.PaidInterest)) + " 元";

                StringBuilder sb = new StringBuilder();
                sb.Append("<color=#FFC24A>").Append(a1).Append("</color>").Append(LvPad(a1))
                  .Append("<color=#9EC7DE>").Append(a2).Append("</color>\n");
                sb.Append("<color=#9EC7DE>").Append(b1).Append("</color>").Append(LvPad(b1))
                  .Append("<color=#E5533D>").Append(b2).Append("</color>\n");
                sb.Append("<color=#9EB3CC>").Append(c1).Append("</color>").Append(LvPad(c1));
                if (def != null)
                {
                    int shorted = StockLeverage.GetShort(a, def.Id);
                    if (shorted > 0)
                    {
                        sb.Append("<color=#D05020>融券 ").Append(def.Name).Append(' ')
                          .Append(shorted).Append(" 股</color>");
                    }
                }
                _lvRows.text = sb.ToString();
            }

            if (_lvNotice != null)
            {
                _lvNotice.text = a.LastNotice != null ? a.LastNotice : "";
                _lvNotice.color = a.Liquidated ? Palette.Danger : Palette.Gold;
            }

            // 步进按钮改完值后回填输入框；玩家正在打字时 SyncLvInputs 内部会自动跳过
            SyncLvInputs();

            // ── 按钮可用性 ────────────────────────────────────────────
            long room = StockLeverage.MaxBorrow(a);
            int shortedNow = def != null ? StockLeverage.GetShort(a, def.Id) : 0;
            SetLvBtn(_lvOpen, !a.Open, a.Open ? "已开户" : "开户 · " + a.OpenFee.ToString("0") + " 元",
                a.Open ? Palette.BtnIdle : Palette.BtnGreen);
            SetLvBtn(_lvBorrow, a.Open && room > 0, "融资借钱", Palette.BtnBlue);
            SetLvBtn(_lvRepay, a.Open && a.Debt > 0, "还款", Palette.BtnIdle);
            SetLvBtn(_lvShort, a.Open, "融券卖出", Palette.BtnOrange);
            SetLvBtn(_lvCover, a.Open && shortedNow > 0, "买券还券", Palette.BtnBlue);
            SetLvBtn(_lvSettle, a.Open && a.Debt > 0, "一键还清", Palette.BtnRed);

            // ── 右栏说明 ──────────────────────────────────────────────
            if (_lvPlay != null) _lvPlay.text = _lvShadow ? ShadowPlay : RegPlay;
            if (_lvRisk != null) _lvRisk.text = RiskText;
        }

        private static void SetLvBtn(UiButton b, bool on, string text, Color color)
        {
            if (b == null) return;
            b.SetText(text);
            b.SetInteractable(on);
            b.SetBg(on ? color : Palette.BtnIdle);
            b.SetTextColor(on ? Palette.Title : Palette.Muted);
        }

        private const string RegPlay =
            "【这是什么】跟券商借钱买股票。你有 100 元，能再借 100 元，一共拿 200 元去买。\n"
            + "【怎么玩】\n"
            + "① 先点「开户」——要 50 元，且总资产满 300 元才让开。\n"
            + "② 填好金额点「融资借钱」，借到的钱直接进股票账户。\n"
            + "③ 去【交易下单】用这笔钱买股票（和平时买法完全一样）。\n"
            + "④ 涨了卖掉，回来点「还款」还掉本息，剩下的才是你赚的。\n"
            + "【利息】每天 0.05%，借 100 元一天 5 分钱，每天结算时自动扣。\n"
            + "【什么时候用】看准一波上涨趋势，想多赚一点、又不想等太久。\n"
            + "【融券做空】反过来玩：先「融券卖出」借股票卖掉，等跌了再「买券还券」赚差价。";

        private const string ShadowPlay =
            "【这是什么】找野路子借钱，杠杆高得多，但利息贵得吓人。\n"
            + "【怎么玩】和融资融券一模一样：开户 → 借钱 → 买股票 → 还款。\n"
            + "【档位】3 / 5 / 8 倍，开户后、没欠钱时可以随时换。\n"
            + "8 倍就是你有 100 元能拿 800 元去买：涨 10% 你翻倍，跌 10% 你本金没了。\n"
            + "【利息】每天 0.30%，是正规的 6 倍。借 100 元一天要 3 毛。\n"
            + "【门槛】只要 20 元开户费、总资产满 100 元就行，比正规的低很多。\n"
            + "【什么时候用】只有你非常有把握、并且准备随时盯着盘面的时候。";

        private const string RiskText =
            "· 借的钱一分不少要还。股票跌了，跌的全是你的本金。\n"
            + "· 每天都会扣利息，横盘不动也是在慢慢亏钱。\n"
            + "· 担保比例跌破强平线，系统会自动卖掉你的持仓还债，没得商量。\n"
            + "· 融券做空如果股价反而涨了，亏损是没有上限的。\n"
            + "新手建议：先在【交易下单】用自有资金做几笔，摸清涨跌节奏再来这里。";

        private static void LvOpen()
        {
            try
            {
                StockState.EnsureLoaded(PlayerStore.instance);
                LeverageAccount a = LvAccount();
                LogOp("杠杆开户：" + a.Name);
                string err;
                string msg = StockLeverage.Open(a, a.Leverage, out err);
                if (msg == null) SetStatus(err + "。", true);
                else SetStatus(msg + "。", false);
            }
            catch (Exception ex)
            {
                SetStatus("开户失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void LvBorrow()
        {
            try
            {
                StockState.EnsureLoaded(PlayerStore.instance);
                LeverageAccount a = LvAccount();
                LogOp("融资借钱 " + _lvAmount + " 元（" + a.Name + "）");
                string err;
                string msg = StockLeverage.Borrow(a, _lvAmount, out err);
                if (msg == null) SetStatus(err + "。", true);
                else SetStatus(msg, false);
            }
            catch (Exception ex)
            {
                SetStatus("融资失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void LvRepay()
        {
            try
            {
                StockState.EnsureLoaded(PlayerStore.instance);
                LeverageAccount a = LvAccount();
                LogOp("杠杆还款 " + _lvAmount + " 元（" + a.Name + "）");
                string err;
                string msg = StockLeverage.Repay(a, _lvAmount, out err);
                if (msg == null) SetStatus(err + "。", true);
                else SetStatus(msg, false);
            }
            catch (Exception ex)
            {
                SetStatus("还款失败：" + ex.Message, true);
            }
            Refresh();
        }

        /// <summary>一键还清：按当前欠款金额走正常的还款流程，不做特殊分支。</summary>
        private static void LvSettle()
        {
            try
            {
                StockState.EnsureLoaded(PlayerStore.instance);
                LeverageAccount a = LvAccount();
                if (a.Debt <= 0) { SetStatus("现在没有欠款要还。", true); Refresh(); return; }
                double want = StockState.ToYuan(a.Debt);
                LogOp("一键还清欠款 " + want.ToString("0") + " 元（" + a.Name + "）");
                string err;
                string msg = StockLeverage.Repay(a, want, out err);
                if (msg == null) SetStatus(err + "。", true);
                else SetStatus(msg, false);
            }
            catch (Exception ex)
            {
                SetStatus("还款失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void LvShort()
        {
            try
            {
                StockState.EnsureLoaded(PlayerStore.instance);
                LeverageAccount a = LvAccount();
                StockDef def = Current();
                LogOp("融券卖出 " + def.Name + " " + _lvShares + " 股（" + a.Name + "）");
                string err;
                string msg = StockLeverage.ShortSell(a, def, _lvShares, out err);
                if (msg == null) SetStatus(err + "。", true);
                else SetStatus(msg, false);
            }
            catch (Exception ex)
            {
                SetStatus("融券失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void LvCover()
        {
            try
            {
                StockState.EnsureLoaded(PlayerStore.instance);
                LeverageAccount a = LvAccount();
                StockDef def = Current();
                LogOp("买券还券 " + def.Name + " " + _lvShares + " 股（" + a.Name + "）");
                string err;
                string msg = StockLeverage.BuyToCover(a, def, _lvShares, out err);
                if (msg == null) SetStatus(err + "。", true);
                else SetStatus(msg, false);
            }
            catch (Exception ex)
            {
                SetStatus("买券失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void AddLvAmount(int delta)
        {
            int before = _lvAmount;
            _lvAmount = Mathf.Clamp(_lvAmount + delta, 1, FundMax);
            LogOp("杠杆金额 " + before + " → " + _lvAmount + " 元");
            SetStatus("杠杆金额已设为 " + _lvAmount + " 元。", false);
            SyncLvInputs();
            Refresh();
        }

        private static void AddLvShares(int delta)
        {
            int before = _lvShares;
            _lvShares = Mathf.Clamp(_lvShares + delta, 1, QtyMax);
            LogOp("融券股数 " + before + " → " + _lvShares + " 股");
            SetStatus("融券股数已设为 " + _lvShares + " 股。", false);
            SyncLvInputs();
            Refresh();
        }

        private static void OnLvAmountChanged(string text)
        {
            if (_lvGuard) return;
            int v;
            if (!TryParsePositive(text, out v)) return;
            v = Mathf.Clamp(v, 1, FundMax);
            if (v == _lvAmount) return;
            _lvAmount = v;
            LogOp("杠杆金额 → " + _lvAmount + " 元");
            Refresh();
        }

        private static void OnLvSharesChanged(string text)
        {
            if (_lvGuard) return;
            int v;
            if (!TryParsePositive(text, out v)) return;
            v = Mathf.Clamp(v, 1, QtyMax);
            if (v == _lvShares) return;
            _lvShares = v;
            LogOp("融券股数 → " + _lvShares + " 股");
            Refresh();
        }

        private static void SyncLvInputs()
        {
            _lvGuard = true;
            try
            {
                if (_lvAmountInput != null && !InputFocused(_lvAmountInput))
                {
                    try { _lvAmountInput.SetTextWithoutNotify(_lvAmount.ToString()); }
                    catch { try { _lvAmountInput.text = _lvAmount.ToString(); } catch { } }
                }
                if (_lvShareInput != null && !InputFocused(_lvShareInput))
                {
                    try { _lvShareInput.SetTextWithoutNotify(_lvShares.ToString()); }
                    catch { try { _lvShareInput.text = _lvShares.ToString(); } catch { } }
                }
            }
            finally
            {
                _lvGuard = false;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  图表
        // ══════════════════════════════════════════════════════════════

        private static void DrawAssetChart()
        {
            if (_aChart == null) return;
            List<ChartSeries> series = new List<ChartSeries>();
            ChartSeries asset = new ChartSeries { Label = "总资产", Color = Palette.Series1 };
            ChartSeries pool = new ChartSeries { Label = "可用资金", Color = Palette.Series2 };
            ChartSeries stock = new ChartSeries { Label = "持仓市值", Color = Palette.Series3 };
            for (int i = 0; i < StockState.History.Count; i++)
            {
                DaySnapshot snap = StockState.History[i];
                double p = StockState.ToYuan(snap.Pool);
                double s = StockState.ToYuan(snap.StockValue);
                pool.Values.Add(p);
                stock.Values.Add(s);
                asset.Values.Add(p + s);
            }
            series.Add(asset);
            series.Add(pool);
            series.Add(stock);

            int n = StockState.History.Count;
            string[] labels = new string[n];
            for (int i = 0; i < n; i++) labels[i] = "D" + StockState.History[i].Day;
            StockChart.DrawLines(_aChart, ContentW - 48f, BodyH - 80f, series, labels);
            TipData(_tipAsset, "资产", labels, null, false, series, null, null);
        }

        /// <summary>交易页图表当前的形态文案。</summary>
        private static string ChartKindText()
        {
            return _chartAsLine ? "图形：折线图" : "图形：蜡烛图";
        }

        /// <summary>在蜡烛图与折线图之间切换，并立刻重绘。</summary>
        private static void ToggleChartKind()
        {
            _chartAsLine = !_chartAsLine;
            LogOp("走势图切换为" + (_chartAsLine ? "折线图" : "蜡烛图"));
            if (_tChartToggle != null) _tChartToggle.SetText(ChartKindText());
            SetStatus(_chartAsLine ? "走势图已切换为折线图。" : "走势图已切换为蜡烛图。", false);
            Refresh();
            DrawTradeChart();
        }

        /// <summary>
        /// 交易页的迷你走势：上面一张主图（蜡烛 / 折线），下面挂一条成交量副图。
        /// 两条图共用同一份 K 线数据，所以横轴对齐，可以直接对着看量价配合。
        /// </summary>
        private static void DrawTradeChart()
        {
            StockDef def = Current();
            if (def == null) return;

            // 交易页固定看最近 30 天：它是「下单前扫一眼」的位置，不需要长周期档位。
            // 想看 1 周 ~ 1 年的走势去【技术分析】页。
            List<Bar> bars = StockIndicators.BuildBars(def.Id, StockState.PriceWindow(def.Id, 30));
            int n = bars.Count;
            string[] labels = new string[n];
            for (int i = 0; i < n; i++) labels[i] = i == n - 1 ? "现在" : "-" + (n - 1 - i);

            float w = ContentW - 48f;
            if (_tChart != null)
            {
                if (_chartAsLine)
                {
                    List<ChartSeries> series = new List<ChartSeries>();
                    ChartSeries line = new ChartSeries { Label = def.Name, Color = Palette.Series1 };
                    line.Values.AddRange(StockIndicators.Closes(bars));
                    series.Add(line);
                    StockChart.DrawLines(_tChart, w, 148f, series, labels);
                    TipData(_tipTradeMain, "收盘", labels, null, false, series, null, null);
                }
                else
                {
                    StockChart.DrawCandles(_tChart, w, 148f, bars, labels);
                    TipData(_tipTradeMain, "K 线", labels, bars, true, null, null, null);
                }
            }

            if (_tVol != null)
            {
                double[] vma = StockIndicators.MA(StockIndicators.Volumes(bars), 5);
                StockChart.DrawVolume(_tVol, w, 68f, bars, new List<double>(vma), labels);
                List<ChartSeries> vl = new List<ChartSeries>();
                vl.Add(new ChartSeries { Label = "量均线", Color = Palette.Gold,
                    Values = new List<double>(vma) });
                TipData(_tipTradeVol, "成交量", labels, bars, false, vl, null, null);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  交互
        // ══════════════════════════════════════════════════════════════

        private static StockDef Current()
        {
            StockDef def = StockDefs.Get(_selectedId);
            if (def != null) return def;
            def = StockDefs.All.Length > 0 ? StockDefs.All[0] : null;
            if (def != null) _selectedId = def.Id;
            return def;
        }

        /// <summary>行情页第 row 行点击：选中这支，直接进它的技术分析图。</summary>
        private static void OpenTradeRow(int row)
        {
            StockDef def = At(DisplayList(false, true), _quotePage, row, PageRows);
            if (def == null) return;
            // 新手引导「动手：挑一支股票」那一站要的是「点一行 → 直接下单」，
            // 中间别再插一层技术分析图（见 问题截图/这个新手任务流程有问题….png）。
            if (_tourOpen && !_tourPage) OpenTradeFor(def.Id);
            else GoTechFor(def.Id, PageQuote);
            // 新手引导「动手：挑一支股票」那一站要求玩家真的点一行才放行
            TourActionDone("pick");
        }

        /// <summary>带着「从哪一页来的」进技术分析页。</summary>
        private static void GoTechFor(string id, int back)
        {
            StockDef def = StockDefs.Get(id);
            if (def == null) return;
            _techBack = back;
            LogOp("看 " + def.Name + "(" + id + ") 的技术分析");
            SelectStock(id);
            SetStatus("正在看 " + def.Name + " 的图。看完点「去下单」或「返回」。", false);
            ShowPage(PageTech);
        }

        /// <summary>技术分析页的「返回」：回到进来时那一页（默认行情报价）。</summary>
        private static void BackFromTech()
        {
            int back = _techBack;
            if (back < 0 || back >= PageCount || back == PageTech) back = PageQuote;
            LogOp("从技术分析返回【" + NavTitles[back] + "】");
            ShowPage(back);
        }

        /// <summary>技术分析页的「去下单」：带着当前标的进交易页。</summary>
        private static void GoTradeFromTech()
        {
            StockDef def = Current();
            if (def == null) return;
            OpenTradeFor(def.Id);
        }

        /// <summary>
        /// 交易明细第 row 行点击：看这笔标的的技术分析图。
        /// 复用阶段 6 的 _techBack，「返回」会回到交易明细页而不是行情页。
        /// </summary>
        private static void OpenTechFromJournal(int row)
        {
            if (_jnId == null || row < 0 || row >= _jnId.Length) return;
            string id = _jnId[row];
            if (string.IsNullOrEmpty(id)) return;
            GoTechFor(id, PageJournal);
        }

        /// <summary>持仓页第 row 行点击。</summary>
        private static void OpenTradeHeldRow(int row)
        {
            StockDef def = At(DisplayList(true, false), _holdPage, row, PageRows);
            if (def != null) OpenTradeFor(def.Id);
        }

        /// <summary>交易页第 row 个标的气泡。</summary>
        private static void PickSlot(int row)
        {
            StockDef def = At(DisplayList(false, false), _tradePage, row, PageRows);
            if (def != null) SelectStock(def.Id);
        }

        /// <summary>行情页第 row 行的星标：收藏 / 取消收藏。</summary>
        private static void ToggleFavSlot(int row)
        {
            StockDef def = At(DisplayList(false, true), _quotePage, row, PageRows);
            if (def == null) return;
            bool on = StockState.ToggleFavorite(def.Id);
            LogOp((on ? "收藏 " : "取消收藏 ") + def.Name + "(" + def.Id + ")");
            SetStatus(on ? "已收藏 " + def.Name + "，会排到列表最前面。" : "已取消收藏 " + def.Name + "。", false);
            Refresh();
        }

        private static void OpenTradeFor(string id)
        {
            SelectStock(id);
            ShowPage(PageTrade);
        }

        private static void SelectStock(string id)
        {
            StockDef def = StockDefs.Get(id);
            if (def == null) return;
            _selectedId = id;
            // 换标的就把挂着的展开列表收起来（不收会浮在刚画好的图上），
            // 挂单价也重新跟新标的的实时价走，否则会挂着上一支的价格。
            // _itDrawnStep 置 -1 是为了让分时图立刻重画成新标的 ——
            // Tick 只按「段数/天数/开收盘」判断要不要重画，换标的不在它的判断里。
            _itPriceCents = 0;
            _itDrawnStep = -1;
            if (_tcDrop != null) _tcDrop.Close();
            if (_itDrop != null) _itDrop.Close();
            LogOp("选择标的 " + def.Name + "(" + id + ")");

            // 选中项不在当前页时自动翻到它所在的页，否则会出现「选了却看不到高亮」
            List<StockDef> list = DisplayList(false, false);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Id != id) continue;
                _tradePage = i / PageRows;
                break;
            }
            SetStatus("已选择 " + def.Name + "。", false);
            Refresh();
            if (_page == PageTrade || _page == PageTech) _chartFrames = 2;
        }

        /// <summary>数量输入框上限：够买下整个盘面了，再大只是手滑。</summary>
        private const int QtyMax = 999999;
        /// <summary>划转金额上限（元）。</summary>
        private const int FundMax = 99999999;

        private static void AddAmount(int delta)
        {
            int before = _amount;
            _amount = Mathf.Clamp(_amount + delta, 1, QtyMax);
            LogOp("下单数量 " + before + " → " + _amount + " 股");
            SyncQtyInput();
            Refresh();
        }

        private static void AddFundAmount(int delta)
        {
            int before = _fundAmount;
            _fundAmount = Mathf.Clamp(_fundAmount + delta, 1, FundMax);
            LogOp("划转金额 " + before + " → " + _fundAmount + " 元");
            SetStatus("划转金额已设为 " + _fundAmount + " 元。", false);
            SyncFundInput();
            Refresh();
        }

        /// <summary>
        /// 交易页数量输入框：只收数字，非法输入直接忽略（保留上一次的有效值）。
        /// 不做「敲一半自动补正」——玩家删空重打时那样会很难受。
        /// </summary>
        private static void OnQtyChanged(string text)
        {
            if (_qtyGuard) return;
            int v;
            if (!TryParsePositive(text, out v)) return;
            v = Mathf.Clamp(v, 1, QtyMax);
            if (v == _amount) return;
            _amount = v;
            LogOp("下单数量 → " + _amount + " 股");
            Refresh();
        }

        private static void OnFundAmountChanged(string text)
        {
            if (_fdGuard) return;
            int v;
            if (!TryParsePositive(text, out v)) return;
            v = Mathf.Clamp(v, 1, FundMax);
            if (v == _fundAmount) return;
            _fundAmount = v;
            LogOp("划转金额 → " + _fundAmount + " 元");
            Refresh();
        }

        /// <summary>纯数字且 &gt; 0 才算合法；带负号、字母、小数点一律当没输入。</summary>
        private static bool TryParsePositive(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string s = text.Trim();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9') return false;
            }
            return int.TryParse(s, out value) && value > 0;
        }

        /// <summary>把 _amount 回填进输入框（步进按钮改完值后调用）。</summary>
        private static void SyncQtyInput()
        {
            if (_tQtyInput == null) return;
            _qtyGuard = true;
            try { _tQtyInput.SetTextWithoutNotify(_amount.ToString()); }
            catch { try { _tQtyInput.text = _amount.ToString(); } catch { } }
            _qtyGuard = false;
        }

        private static void SyncFundInput()
        {
            if (_fdAmountInput == null) return;
            _fdGuard = true;
            try { _fdAmountInput.SetTextWithoutNotify(_fundAmount.ToString()); }
            catch { try { _fdAmountInput.text = _fundAmount.ToString(); } catch { } }
            _fdGuard = false;
        }

        private static void DoBuy()
        {
            try
            {
                StockState.EnsureLoaded(PlayerStore.instance);
                StockDef def = Current();
                LogOp("买入 " + def.Name + " " + _amount + " 股（现价 " + StockEngine.LivePriceYuan(def.Id).ToString("0.00") + "）");
                string error;
                if (StockEngine.Buy(def, _amount, out error))
                {
                    Core.Log.Msg("[结果] 买入成功：" + def.Name + " " + _amount + " 股，成交 "
                        + Money(StockState.ToYuan(StockEngine.LivePrice(def.Id) * _amount)) + " 元 + 手续费，余额 "
                        + Money(StockEngine.PoolYuan()) + " 元");
                    SetStatus("买入 " + def.Name + " " + _amount + " 股。", false);
                    // 新手引导走到「动手买一次」那一站时，买入成功就自动进下一步
                    TourActionDone("buy");
                }
                else
                {
                    Core.Log.Msg("[结果] 买入失败：" + error);
                    SetStatus(error, true);
                }
            }
            catch (Exception ex)
            {
                SetStatus("买入失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void DoSell()
        {
            try
            {
                StockState.EnsureLoaded(PlayerStore.instance);
                StockDef def = Current();
                LogOp("卖出 " + def.Name + " " + _amount + " 股（现价 " + StockEngine.LivePriceYuan(def.Id).ToString("0.00") + "）");
                string error;
                if (StockEngine.Sell(def, _amount, out error))
                {
                    Core.Log.Msg("[结果] 卖出成功：" + def.Name + " " + _amount + " 股，余额 "
                        + Money(StockEngine.PoolYuan()) + " 元，已实现盈亏累计 "
                        + Money(StockEngine.RealizedPnlYuan()) + " 元");
                    SetStatus("卖出 " + def.Name + " " + _amount + " 股。", false);
                    // 新手引导走到「动手卖一次」那一站时，卖出成功就自动进下一步
                    TourActionDone("sell");
                }
                else
                {
                    Core.Log.Msg("[结果] 卖出失败：" + error);
                    SetStatus(error, true);
                }
            }
            catch (Exception ex)
            {
                SetStatus("卖出失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void DoSellAll()
        {
            try
            {
                StockState.EnsureLoaded(PlayerStore.instance);
                StockDef def = Current();
                int held = StockState.GetPosition(def.Id);
                LogOp("清仓 " + def.Name + "（当前持仓 " + held + " 股）");
                if (held <= 0)
                {
                    Core.Log.Msg("[结果] 清仓失败：没有可卖出的持仓");
                    SetStatus("没有可卖出的 " + def.Name + " 持仓。", true);
                }
                else
                {
                    StockEngine.SellAll(def);
                    Core.Log.Msg("[结果] 清仓成功：" + def.Name + " " + held + " 股，余额 "
                        + Money(StockEngine.PoolYuan()) + " 元，已实现盈亏累计 "
                        + Money(StockEngine.RealizedPnlYuan()) + " 元");
                    SetStatus("已清仓 " + def.Name + " " + held + " 股。", false);
                    TourActionDone("sell");
                }
            }
            catch (Exception ex)
            {
                SetStatus("清仓失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void DoTransfer(bool intoStock, int yuan)
        {
            try
            {
                PlayerStore store = PlayerStore.instance;
                if (store == null) return;
                StockState.EnsureLoaded(store);

                int amount = yuan;
                if (amount < 0)
                {
                    long available = intoStock ? store.playerCash : StockEngine.PoolYuan();
                    amount = (int)Math.Max(0, Math.Min(int.MaxValue, available));
                }
                LogOp((intoStock ? "转入股市 " : "提现到店铺 ") + amount + " 元"
                    + (yuan < 0 ? "（全部）" : ""));
                if (amount <= 0)
                {
                    SetStatus("可划转金额为 0。", true);
                    Refresh();
                    return;
                }

                string error;
                bool ok = intoStock
                    ? StockEngine.TransferIn(store, amount, out error)
                    : StockEngine.TransferOut(store, amount, out error);
                SetStatus(ok ? (intoStock ? "已转入 " : "已提现 ") + amount + " 元。" : error, !ok);
                // 新手引导走到「动手把钱搬进来」那一站时，转入成功就自动进下一步
                if (ok && intoStock) TourActionDone("transfer");
            }
            catch (Exception ex)
            {
                SetStatus("划转失败：" + ex.Message, true);
            }
            Refresh();
        }

        private static void DoBuyLicense()
        {
            LogOp("购买黑市开户证明（" + LicenseCost + " 元）");
            if (StockState.License)
            {
                SetStatus("黑市开户证明已经开好了。", false);
                Refresh();
                return;
            }
            string error = TryBuyLicense();
            Core.Log.Msg(error == null ? "[结果] 黑市开户成功" : "[结果] 黑市开户失败：" + error);
            SetStatus(error ?? "已取得黑市开户证明，黑市股解锁。", error != null);
            Refresh();
        }

        /// <summary>用店铺现金买下黑市开户证明。成功返回 null，失败返回原因。</summary>
        public static string TryBuyLicense()
        {
            try
            {
                PlayerStore store = PlayerStore.instance;
                if (store == null) return "尚未进入存档。";
                if (StockState.License) return null;
                if (store.playerCash < LicenseCost)
                {
                    return "现金不足：需要 " + LicenseCost + "，现有 " + store.playerCash + "。";
                }
                store.playerCash -= LicenseCost;
                GrantLicense("在星际证券办妥了黑市开户证明。");
                return null;
            }
            catch (Exception ex)
            {
                Core.Log.Warning("购买黑市开户证明失败：" + ex.Message);
                return "购买失败，详见日志。";
            }
        }

        /// <summary>发放黑市执照。</summary>
        public static void GrantLicense(string nightLogText)
        {
            try
            {
                if (StockState.License) return;
                StockState.License = true;
                StockState.Dirty = true;

                PlayerStore store = PlayerStore.instance;
                if (store != null && !string.IsNullOrEmpty(nightLogText))
                {
                    store.AddNightLog(nightLogText, StockEngine.NewsFlat);
                }
                SetStatus("已取得黑市开户证明，黑市股解锁。", false);
                Refresh();
            }
            catch (Exception ex)
            {
                Core.Log.Warning("发放黑市开户证明失败：" + ex.Message);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  小工具
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 操作日志。所有按钮动作在执行前先落一行，方便事后回放「点了哪些、顺序如何」。
        /// 统一走这里而不是各写各的，日志格式才整齐、也好过滤。
        /// </summary>
        private static void LogOp(string text)
        {
            try { Core.Log.Msg("[操作] " + text); }
            catch { }
        }

        /// <summary>
        /// 列表显示顺序：收藏在前（按收藏先后），其余按定义顺序。
        /// onlyHeld=true 时只保留有持仓的标的（持仓页用）。
        /// useQuery=true 时才按搜索词过滤——搜索框只在行情页，别让另外两页被看不见的条件筛掉。
        /// </summary>
        private static List<StockDef> DisplayList(bool onlyHeld, bool useQuery)
        {
            List<StockDef> list = new List<StockDef>();
            for (int i = 0; i < StockState.Favorites.Count; i++)
            {
                StockDef def = StockDefs.Get(StockState.Favorites[i]);
                if (def == null) continue;
                if (onlyHeld && StockState.GetPosition(def.Id) <= 0) continue;
                if (useQuery && !MatchQuery(def)) continue;
                list.Add(def);
            }
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef def = StockDefs.All[i];
                if (StockState.IsFavorite(def.Id)) continue;
                if (onlyHeld && StockState.GetPosition(def.Id) <= 0) continue;
                if (useQuery && !MatchQuery(def)) continue;
                list.Add(def);
            }
            return list;
        }

        private static bool MatchQuery(StockDef def)
        {
            string q = _query != null ? _query.Trim() : string.Empty;
            if (q.Length == 0) return true;
            return def.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || def.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>页数。rows 是每页行数——报表页一页只放 6 行，跟行情页的 8 行不是一回事。</summary>
        private static int PageCountOf(List<StockDef> list, int rows)
        {
            int n = (list.Count + rows - 1) / rows;
            return n < 1 ? 1 : n;
        }

        /// <summary>取第 page 页的第 row 行，越界返回 null。</summary>
        private static StockDef At(List<StockDef> list, int page, int row, int rows)
        {
            int index = page * rows + row;
            return index >= 0 && index < list.Count ? list[index] : null;
        }

        private static void OnQueryChanged(string text)
        {
            string next = text ?? string.Empty;
            if (next == _query) return;
            _query = next;
            _quotePage = 0;
            LogOp("搜索「" + _query + "」");
            Refresh();
        }

        private static void ClearQuery()
        {
            LogOp("清除搜索词");
            _query = string.Empty;
            _quotePage = 0;
            if (_qSearch != null) { try { _qSearch.SetTextWithoutNotify(""); } catch { } }
            SetStatus("已清除搜索条件。", false);
            Refresh();
        }

        /// <summary>列表底部翻页条：左箭头 / 页码 / 右箭头。</summary>
        private static void BuildPager(Transform parent, string name, float y,
            out UiButton prev, out TextMeshProUGUI text, out UiButton next, Action onPrev, Action onNext)
        {
            prev = Ui.MakeButton(parent, name + "Prev", "← 上一页", 17f, Palette.BtnIdle,
                Palette.Sub, onPrev, Ui.AlignCenter);
            if (prev != null) prev.Place(24f, y, 160f, 30f);
            next = Ui.MakeButton(parent, name + "Next", "下一页 →", 17f, Palette.BtnIdle,
                Palette.Sub, onNext, Ui.AlignCenter);
            if (next != null) next.Place(ContentW - 24f - 160f, y, 160f, 30f);
            text = Ui.MakeText(parent, name + "Page", "", FsSmall, Palette.Muted, Ui.AlignCenter, false);
            if (text != null) Ui.Place(text.gameObject, 194f, y + 4f, ContentW - 388f, 22f);
        }

        /// <summary>翻页到边界时把箭头压暗，省得玩家反复点空。</summary>
        private static void DimPager(UiButton prev, UiButton next, int page, int pages)
        {
            if (prev != null)
            {
                bool can = page > 0;
                prev.SetBg(can ? Palette.BtnIdle : Palette.Disabled);
                prev.SetTextColor(can ? Palette.Sub : Palette.DisabledFg);
            }
            if (next != null)
            {
                bool can = page < pages - 1;
                next.SetBg(can ? Palette.BtnIdle : Palette.Disabled);
                next.SetTextColor(can ? Palette.Sub : Palette.DisabledFg);
            }
        }

        private static void TurnPage(ref int page, int delta, string label, bool onlyHeld, bool useQuery)
        {
            List<StockDef> list = DisplayList(onlyHeld, useQuery);
            int pages = PageCountOf(list, PageRows);
            int next = Mathf.Clamp(page + delta, 0, pages - 1);
            if (next == page)
            {
                LogOp(label + "翻页：已经是" + (delta > 0 ? "最后一页" : "第一页") + "，忽略");
                SetStatus(label + "已经是" + (delta > 0 ? "最后一页" : "第一页") + "了。", false);
                Refresh();
                return;
            }
            page = next;
            LogOp(label + "翻到第 " + (page + 1) + " / " + pages + " 页");
            SetStatus(label + "第 " + (page + 1) + " / " + pages + " 页，共 " + list.Count + " 项。", false);
            Refresh();
        }

        private static long StoreCash()
        {
            PlayerStore store = PlayerStore.instance;
            return store != null ? store.playerCash : 0L;
        }

        private static int HeldCount()
        {
            int n = 0;
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                if (StockState.GetPosition(StockDefs.All[i].Id) > 0) n++;
            }
            return n;
        }

        private static long MaxBuyable()
        {
            StockDef def = Current();
            double price = StockEngine.LivePriceYuan(def.Id);
            if (price <= 0) return 0;
            return (long)Math.Floor(StockEngine.PoolYuan() / (price * (1.0 + def.FeeRate)));
        }

        private static string Money(double value)
        {
            return value.ToString("N2");
        }

        private static string Sign(double value)
        {
            return value >= 0 ? "+" : "";
        }

        private static string ColorHex(Color c)
        {
            return ((int)(Mathf.Clamp01(c.r) * 255f)).ToString("X2")
                + ((int)(Mathf.Clamp01(c.g) * 255f)).ToString("X2")
                + ((int)(Mathf.Clamp01(c.b) * 255f)).ToString("X2");
        }
    }
}
