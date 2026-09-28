using System;
using System.Collections.Generic;
using System.Text;

namespace StockMarket
{
    /// <summary>一条聊天记录。Mine=true 是玩家自己发出去的那句。</summary>
    public sealed class ChatLine
    {
        public bool Mine;
        public int Day;
        public string Text;
    }

    /// <summary>
    /// 一位好友。Kind 决定他发什么消息、底部给哪几个按钮：
    ///   KindTrade（老K）：报价由 StockFriend 驱动，可接受 / 还价 / 拒绝；
    ///   KindInfo：卖你一条「某支股票这几天要涨/要跌」的消息，可听 / 砍价 / 不听；
    ///   KindChat：纯闲聊，回一句就行。
    /// </summary>
    public sealed class ChatFriend
    {
        public string Id;
        public string Name;
        public string Tag;       // [层区-身份]
        public string Note;      // 右栏副标题：这人是谁
        public string ColorHex;  // 头像底色
        public int Kind;
        public int Rel;          // 靠谱度 1~5 星，决定情报说真话的概率

        // ── 运行时（落存档）──
        public int Unread;
        public int Cooldown;
        public readonly List<ChatLine> Log = new List<ChatLine>();

        // 待处理的情报（KindInfo 用）
        public string InfoId;
        public int InfoDir;      // 0 要涨 / 1 要跌
        public long InfoPrice;   // 要价（分）
        public int InfoDays;     // 还剩几天有效

        public bool HasInfo { get { return !string.IsNullOrEmpty(InfoId); } }
    }

    /// <summary>
    /// 好友与聊天。
    ///
    /// 定位：把「老K 一个人挂在新手任务页底下」扩成一页像聊天软件的好友页 ——
    /// 左边是我的头像（点状态点切在线/离线）和好友列表，右边是对话。
    /// 有新消息时导航和列表上冒红色数字气泡，不再用星星（星星留给任务奖励）。
    ///
    /// 消息全部走确定性派生：内容由随机数生成一次就落进聊天记录里，
    /// 读档、快进都不会重放，也不会因为读档而变出一批没见过的消息。
    /// 离线只是不能还价/砍价，接受与拒绝照常 —— 该收的钱和该拒的坑一个都不少。
    /// </summary>
    public static class StockChat
    {
        public const int KindTrade = 0;
        public const int KindInfo = 1;
        public const int KindChat = 2;

        public const int MaxLog = 24;         // 每位好友最多留几条记录
        public const int InfoMinYuan = 6;
        public const int InfoMaxYuan = 22;

        /// <summary>玩家自己的在线状态。离线时只能接受 / 拒绝，不能还价。</summary>
        public static bool Online = true;

        public static readonly ChatFriend[] All =
        {
            new ChatFriend
            {
                Id = "k", Name = "老K", Tag = "[黑市-场外报价]", ColorHex = "B0431C",
                Kind = KindTrade, Rel = 4,
                Note = "黑市的门路，隔几天就来谈一笔：收你的货，或者往你手里塞货。场外不收手续费。",
            },
            new ChatFriend
            {
                Id = "su", Name = "苏姐", Tag = "[上层区-消息灵通]", ColorHex = "2E6C93",
                Kind = KindInfo, Rel = 5,
                Note = "上层区太太圈里消息最快的人，要价也最高。她说往哪边走，多半真往那边走。",
            },
            new ChatFriend
            {
                Id = "liu", Name = "刀疤刘", Tag = "[治安部-线人]", ColorHex = "4F7391",
                Kind = KindInfo, Rel = 4,
                Note = "治安部的线人，清查、缉毒这类动静他先知道。话糙，但一般不骗人。",
            },
            new ChatFriend
            {
                Id = "man", Name = "小满", Tag = "[革命军-联络员]", ColorHex = "2E7D5B",
                Kind = KindInfo, Rel = 3,
                Note = "革命军的联络员，手上的消息真真假假，听完自己再想想。",
            },
            new ChatFriend
            {
                Id = "qiang", Name = "强哥", Tag = "[下层区-倒腾旧货]", ColorHex = "8A5A2E",
                Kind = KindChat, Rel = 2,
                Note = "下层区倒腾旧货的，话多、爱放风。他说的话当乐子听就好。",
            },
            new ChatFriend
            {
                Id = "dun", Name = "胖墩", Tag = "[下层区-老散户]", ColorHex = "59809E",
                Kind = KindChat, Rel = 1,
                Note = "一起炒股的街坊，追涨杀跌的老毛病改不掉，找他聊天解闷。",
            },
        };

        public static ChatFriend Get(string id)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Id == id) return All[i];
            }
            return null;
        }

        /// <summary>所有好友的未读消息总数，导航上的红色数字气泡就取这个。</summary>
        public static int UnreadTotal()
        {
            int n = 0;
            for (int i = 0; i < All.Length; i++) n += All[i].Unread;
            return n;
        }

        /// <summary>有未读的好友数（列表上的小气泡用）。</summary>
        public static int UnreadFriends()
        {
            int n = 0;
            for (int i = 0; i < All.Length; i++) if (All[i].Unread > 0) n++;
            return n;
        }

        /// <summary>靠谱度星星，界面直接显示。</summary>
        public static string RelStars(ChatFriend f)
        {
            if (f == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 5; i++) sb.Append(i < f.Rel ? "★" : "☆");
            return sb.ToString();
        }

        /// <summary>说真话的概率：1 星 40%，5 星 80%。</summary>
        public static double TruthOf(ChatFriend f)
        {
            if (f == null) return 0.5;
            return 0.30 + 0.10 * f.Rel;
        }

        // ── 重置 / 推进 ───────────────────────────────────────────────

        /// <summary>开新档：清空记录，每人先留一句开场白（算未读，让玩家一眼看到气泡）。</summary>
        public static void Reset()
        {
            Online = true;
            _offerSig = "";
            for (int i = 0; i < All.Length; i++)
            {
                ChatFriend f = All[i];
                f.Unread = 1;
                f.Cooldown = StockEngine.RandRange(2, 5);
                f.InfoId = null;
                f.InfoDir = 0;
                f.InfoPrice = 0;
                f.InfoDays = 0;
                f.Log.Clear();
                f.Log.Add(new ChatLine { Mine = false, Day = StockState.Today, Text = Greeting(f.Id) });
            }
        }

        private static string Greeting(string id)
        {
            switch (id)
            {
                case "k": return "刚收工。手上要是有货，随时找我，我出的价向来比盘面痛快。";
                case "su": return "小兄弟，做股票先学会听话：有消息我告诉你，别自己乱追。";
                case "liu": return "我是刀疤刘。治安部那边有动静我先递个话，价钱好说。";
                case "man": return "革命军小满。缺消息来找我，缺胆量就别碰股票。";
                case "qiang": return "哎哟，听说你也玩股票了？哥几个都在里面泡着呢。";
                default: return "哥们儿，昨天那支我又追高了，现在套着……你别学我。";
            }
        }

        /// <summary>由 StockEngine.DailyTickNews 每天调一次。</summary>
        public static void Tick()
        {
            // 老K 的报价一变就在聊天里留一句（新报价、还价改口都算）
            SyncOffer();

            for (int i = 0; i < All.Length; i++)
            {
                ChatFriend f = All[i];
                if (f.Kind == KindTrade) continue;

                if (f.HasInfo)
                {
                    f.InfoDays--;
                    if (f.InfoDays <= 0)
                    {
                        f.InfoId = null;
                        Push(f.Id, false, "刚才那条消息过时了，当我没说。");
                        f.Cooldown = StockEngine.RandRange(2, 4);
                    }
                    continue;
                }

                if (f.Cooldown > 0)
                {
                    f.Cooldown--;
                    continue;
                }

                if (f.Kind == KindInfo) MakeInfo(f); else MakeChat(f);
                f.Cooldown = StockEngine.RandRange(3, 6);
            }
        }

        private static string _offerSig = "";

        private static string Sig(FriendOffer o)
        {
            return o == null ? "" : o.DefId + ":" + o.Direction + ":" + o.Shares + ":" + o.Price;
        }

        /// <summary>报价变了就往聊天里补一句。</summary>
        private static void SyncOffer()
        {
            FriendOffer o = StockState.Offer;
            string sig = Sig(o);
            if (sig == _offerSig) return;
            _offerSig = sig;
            if (o == null) return;
            Push("k", false, OfferLine(o));
        }

        /// <summary>
        /// 把当前报价记成「已经在聊天里说过了」。玩家在界面上刚做完还价时调一下，
        /// 免得第二天日结又把同一句话播一遍。
        /// </summary>
        public static void NoteOffer()
        {
            _offerSig = Sig(StockState.Offer);
        }

        private static string OfferLine(FriendOffer o)
        {
            StockDef def = StockDefs.Get(o.DefId);
            string name = def != null ? def.Name : o.DefId;
            string money = StockState.ToYuan(o.Total).ToString("N2");
            string price = StockState.ToYuan(o.Price).ToString("0.00");
            return o.Direction == 0
                ? "你手上那支「" + name + "」我收了：" + o.Shares + " 股，单价 " + price
                  + " 元，合计 " + money + " 元，免手续费。剩 " + o.DaysLeft + " 天。"
                : "我这有批「" + name + "」的货：" + o.Shares + " 股，单价 " + price
                  + " 元，合计 " + money + " 元，免手续费。要不要？剩 " + o.DaysLeft + " 天。";
        }

        /// <summary>情报型好友：挑一支股票，按靠谱度决定说真话还是说反话。</summary>
        private static void MakeInfo(ChatFriend f)
        {
            StockDef def = RandomStock();
            if (def == null) return;

            int actual = StockEngine.TrendOf(def.Id);            // 0 涨 / 1 跌 / 2 横
            bool truth = StockEngine.RandUnit() < TruthOf(f);
            int dir;
            if (actual == 2) dir = StockEngine.RandUnit() < 0.5 ? 0 : 1;   // 横盘就随便说一边
            else dir = truth ? actual : (actual == 0 ? 1 : 0);

            f.InfoId = def.Id;
            f.InfoDir = dir;
            f.InfoPrice = StockState.ToCents(StockEngine.RandRange(InfoMinYuan, InfoMaxYuan));
            f.InfoDays = StockEngine.RandRange(2, 3);
            Push(f.Id, false, "有支「" + def.Name + "」的消息，要 "
                + StockState.ToYuan(f.InfoPrice).ToString("0") + " 元。听不听？");
        }

        /// <summary>闲聊型好友：大半是废话，三成会扯到某支股票。</summary>
        private static void MakeChat(ChatFriend f)
        {
            string[] plain =
            {
                "今天收工早，来我这儿坐坐？",
                "你说这行情，涨一天跌三天，谁受得了。",
                "我隔壁老王家昨天把货全清了，说是不玩了。",
                "最近治安部查得严，你手上那些东西小心点。",
                "今天没客人，闲得发慌，随便聊聊。",
                "别老盯着盘，眼睛要瞎的。",
            };
            string line = plain[StockEngine.RandRange(0, plain.Length - 1)];

            if (StockEngine.RandUnit() < 0.35)
            {
                StockDef def = RandomStock();
                if (def != null)
                {
                    string[] about =
                    {
                        "「" + def.Name + "」你看了没？我盯好几天了。",
                        "听说「" + def.Name + "」那边在折腾，你咋看？",
                        "我把「" + def.Name + "」割了，割完就涨，气死我了。",
                    };
                    line = about[StockEngine.RandRange(0, about.Length - 1)];
                }
            }
            Push(f.Id, false, line);
        }

        /// <summary>调试用：立刻让一位好友发条消息，不用等冷却。</summary>
        public static bool ForceMessage()
        {
            for (int i = 0; i < All.Length; i++)
            {
                ChatFriend f = All[All.Length - 1 - i];
                if (f.Kind == KindTrade || f.HasInfo) continue;
                if (f.Kind == KindInfo) MakeInfo(f); else MakeChat(f);
                f.Cooldown = StockEngine.RandRange(3, 6);
                return true;
            }
            return false;
        }

        private static StockDef RandomStock()
        {
            List<StockDef> pool = new List<StockDef>();
            for (int i = 0; i < StockDefs.All.Length; i++)
            {
                StockDef d = StockDefs.All[i];
                if (d.NeedLicense && !StockState.License) continue;   // 没开户就别提黑市股，提了也买不了
                pool.Add(d);
            }
            if (pool.Count == 0) return null;
            return pool[StockEngine.RandRange(0, pool.Count - 1)];
        }

        // ── 玩家操作 ──────────────────────────────────────────────────

        /// <summary>往某位好友的聊天里记一句。mine=false 时顺带 +1 未读。</summary>
        public static void Push(string friendId, bool mine, string text)
        {
            ChatFriend f = Get(friendId);
            if (f == null || string.IsNullOrEmpty(text)) return;
            f.Log.Add(new ChatLine { Mine = mine, Day = StockState.Today, Text = text });
            while (f.Log.Count > MaxLog) f.Log.RemoveAt(0);
            if (!mine) f.Unread++;
            StockState.Dirty = true;
            Core.Log.Msg("[" + f.Name + "] " + (mine ? "你：" : "") + text);
        }

        public static void MarkRead(ChatFriend f)
        {
            if (f == null || f.Unread == 0) return;
            f.Unread = 0;
            StockState.Dirty = true;
        }

        /// <summary>付钱听消息。返回错误文本，成功返回 null。</summary>
        public static string PayInfo(ChatFriend f)
        {
            if (f == null || !f.HasInfo) return "现在没有要听的消息";
            if (StockState.Pool < f.InfoPrice)
                return "股票账户里不够 " + StockState.ToYuan(f.InfoPrice).ToString("0") + " 元，先转点钱进来";

            StockState.Pool -= f.InfoPrice;
            StockDef def = StockDefs.Get(f.InfoId);
            string name = def != null ? def.Name : f.InfoId;
            string dir = f.InfoDir == 0 ? "要涨" : "要跌";
            string pay = StockState.ToYuan(f.InfoPrice).ToString("0");

            Push(f.Id, true, "钱转你了，" + pay + " 元，说吧。");
            Push(f.Id, false, "「" + name + "」这几天" + dir + "。我只能说到这儿，你自己拿主意。");
            f.InfoId = null;
            f.InfoDays = 0;
            f.Cooldown = StockEngine.RandRange(2, 4);
            return null;
        }

        /// <summary>砍价：五成砍下三成，五成整条消息作废。</summary>
        public static bool Haggle(ChatFriend f, out string message)
        {
            if (f == null || !f.HasInfo)
            {
                message = "现在没有要听的消息";
                return false;
            }

            if (StockEngine.RandUnit() < 0.5)
            {
                long old = f.InfoPrice;
                f.InfoPrice = Math.Max(1L, (long)Math.Round(f.InfoPrice * 0.7));
                Push(f.Id, false, "行行行，看你顺眼，" + StockState.ToYuan(f.InfoPrice).ToString("0")
                    + " 元，别再讲了。");
                message = "砍价成功：" + StockState.ToYuan(old).ToString("0") + " → "
                    + StockState.ToYuan(f.InfoPrice).ToString("0") + " 元";
                return true;
            }

            f.InfoId = null;
            f.InfoDays = 0;
            f.Cooldown = StockEngine.RandRange(2, 4);
            Push(f.Id, false, "你这人真磨叽，消息我卖给别人了。");
            message = "砍价被拒，这条消息作废了";
            return false;
        }

        /// <summary>不听这条情报。</summary>
        public static void DropInfo(ChatFriend f)
        {
            if (f == null || !f.HasInfo) return;
            f.InfoId = null;
            f.InfoDays = 0;
            f.Cooldown = StockEngine.RandRange(2, 4);
            Push(f.Id, true, "算了，不听了。");
        }

        /// <summary>闲聊时玩家自己敲的一句。空话不发，超长截断。</summary>
        public static bool Say(ChatFriend f, string text)
        {
            if (f == null) return false;
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return false;
            if (text.Length > 40) text = text.Substring(0, 40);
            // 聊天气泡走富文本渲染，玩家敲的尖括号会当成标签解析，先把它们换成全角
            text = text.Replace('<', '＜').Replace('>', '＞');
            Push(f.Id, true, text);
            MarkRead(f);
            return true;
        }

        // ── 存档 ──────────────────────────────────────────────────────
        // 格式：在线|好友Id:未读:冷却:情报标的:方向:要价:剩余天数:记录
        //   记录 = 行~行~行，每行 = 谁,第几天,内容
        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Online ? "1" : "0");
            for (int i = 0; i < All.Length; i++)
            {
                ChatFriend f = All[i];
                sb.Append('|').Append(f.Id).Append(':').Append(f.Unread).Append(':').Append(f.Cooldown)
                  .Append(':').Append(Clean(f.InfoId)).Append(':').Append(f.InfoDir).Append(':')
                  .Append(f.InfoPrice).Append(':').Append(f.InfoDays).Append(':');
                for (int n = 0; n < f.Log.Count; n++)
                {
                    if (n > 0) sb.Append('~');
                    sb.Append(f.Log[n].Mine ? "1" : "0").Append(',')
                      .Append(f.Log[n].Day).Append(',').Append(Clean(f.Log[n].Text));
                }
            }
            return sb.ToString();
        }

        public static void Parse(string text)
        {
            Reset();
            if (string.IsNullOrEmpty(text)) return;

            string[] parts = text.Split('|');
            if (parts.Length > 0) Online = parts[0] != "0";

            for (int i = 1; i < parts.Length; i++)
            {
                string[] seg = parts[i].Split(':');
                if (seg.Length < 8) continue;
                ChatFriend f = Get(seg[0]);
                if (f == null) continue;

                f.Unread = ParseInt(seg[1], 0);
                f.Cooldown = ParseInt(seg[2], 2);
                string infoId = seg[3];
                f.InfoId = string.IsNullOrEmpty(infoId) || StockDefs.Get(infoId) == null ? null : infoId;
                f.InfoDir = ParseInt(seg[4], 0) == 0 ? 0 : 1;
                f.InfoPrice = ParseLong(seg[5], 0);
                f.InfoDays = ParseInt(seg[6], 0);
                if (f.HasInfo && (f.InfoPrice <= 0 || f.InfoDays <= 0)) f.InfoId = null;

                f.Log.Clear();
                if (seg[7].Length > 0)
                {
                    string[] lines = seg[7].Split('~');
                    for (int n = 0; n < lines.Length; n++)
                    {
                        string[] lf = lines[n].Split(',');
                        if (lf.Length < 3) continue;
                        f.Log.Add(new ChatLine
                        {
                            Mine = lf[0] == "1",
                            Day = ParseInt(lf[1], 0),
                            Text = lf[2]
                        });
                    }
                }
                while (f.Log.Count > MaxLog) f.Log.RemoveAt(0);
            }
        }

        /// <summary>把会和分隔符打架的字符换掉，免得聊天内容把存档格式拆散。</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '|' || c == ':' || c == '~' || c == ',' || c == '\n' || c == '\r') sb.Append('、');
                else sb.Append(c);
            }
            return sb.ToString();
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
    }
}
