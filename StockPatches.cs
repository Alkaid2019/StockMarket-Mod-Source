using System;
using HarmonyLib;
using Il2Cpp;

namespace StockMarket
{
    /// <summary>每天开始时推进行情、刷新事件，并按周期结算派系声望。</summary>
    [HarmonyPatch(typeof(StoreEventManager), "OnDayStart")]
    internal static class DayStartPatch
    {
        private static void Postfix(StoreEventManager __instance)
        {
            try
            {
                PlayerStore store = PlayerStore.instance;
                if (store == null) return;

                // 兜底：日结算只会在存档里发生，万一进档事件没抓到，这里也能把入口放出来
                StockEntry.EnterSave();
                StockState.EnsureLoaded(store);
                // 一条新闻一条记录：涨的标红、跌的标绿，混在一行里就分不出方向了
                System.Collections.Generic.List<StockEngine.StockNews> news = StockEngine.DailyTickNews();
                for (int i = 0; i < news.Count; i++)
                {
                    StockEngine.StockNews n = news[i];
                    if (n == null || string.IsNullOrEmpty(n.Text)) continue;
                    store.AddNightLog("【星际证券】" + n.Text.Trim(), n.Color);
                }
                Core.Debug("日结算完成，今日=" + StockState.Today);
            }
            catch (Exception ex)
            {
                Core.Log.Error("日结算失败：" + ex);
            }
        }
    }

    /// <summary>
    /// 店铺里的货物流水：记一笔「实体经济需求」。
    ///
    /// 游戏这几个钩子都是站在「客人」角度命名的（客人把货卖给你 / 客人把你的货买走），
    /// 所以这里按方向分成两组接线，日志里也带方向标签，接反了一眼能看出来：
    ///   买入（客人卖给你 / 下单进货）：StoreClient.OnItemSold、PlayerStore.BuyItem、PlayerStore.OnItemBought
    ///   卖出（客人买走   / 结账上报）：StoreClient.OnItemBought、PlayerStore.SellItem、
    ///                                 PlayerStore.PerformOnSoldCheck、PlayerStore.OnItemsSold
    /// 只有卖出才加需求；买入会把 item.uniqueId 记进买入清单，
    /// 后面任何一条「卖出」钩子再碰到同一件货都会被挡掉，不会把进货算成生意。
    /// 同一笔被多条钩子同时报到时，靠 StockEconomy 内部的 uid 去重。
    /// </summary>
    [HarmonyPatch(typeof(PlayerStore), "OnItemsSold")]
    internal static class ItemsSoldPatch
    {
        private static void Postfix(Il2CppSystem.Collections.Generic.List<GameItem> gameItems)
        {
            try
            {
                StockEconomy.SoldBatch(gameItems, "批量结账");
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[实体] 批量卖出记账失败：" + ex.Message);
            }
        }
    }

    /// <summary>客人把这件货买走了 → 玩家卖出。</summary>
    [HarmonyPatch(typeof(StoreClient), "OnItemBought")]
    internal static class ClientItemBoughtPatch
    {
        private static void Postfix(StoreClient __instance, GameItem item, int cost)
        {
            try
            {
                StockEconomy.Sold(item, __instance, cost, "客人买走");
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[实体] 单笔卖出记账失败：" + ex.Message);
            }
        }
    }

    /// <summary>客人把自己的货卖给你 → 玩家买入（不算需求，只留痕）。</summary>
    [HarmonyPatch(typeof(StoreClient), "OnItemSold")]
    internal static class ClientItemSoldPatch
    {
        private static void Postfix(StoreClient __instance, GameItem item, int soldAmount)
        {
            try
            {
                StockEconomy.Bought(item, __instance, soldAmount, "客人卖你");
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[实体] 买入留痕失败：" + ex.Message);
            }
        }
    }

    /// <summary>下单买入。</summary>
    [HarmonyPatch(typeof(PlayerStore), "BuyItem")]
    internal static class BuyItemPatch
    {
        private static void Postfix(GameItem item)
        {
            try
            {
                StockEconomy.Bought(item, null, 0L, "下单");
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[实体] 买入留痕失败：" + ex.Message);
            }
        }
    }

    /// <summary>下单卖出。</summary>
    [HarmonyPatch(typeof(PlayerStore), "SellItem")]
    internal static class SellItemPatch
    {
        private static void Postfix(GameItem item)
        {
            try
            {
                StockEconomy.Sold(item, null, 0L, "下单");
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[实体] 记账失败：" + ex.Message);
            }
        }
    }

    /// <summary>玩家买入后 PlayerStore 自己的记账回调。</summary>
    [HarmonyPatch(typeof(PlayerStore), "OnItemBought")]
    internal static class StoreItemBoughtPatch
    {
        private static void Postfix(GameItem gameItem, int cost)
        {
            try
            {
                StockEconomy.Bought(gameItem, null, cost, "进货记账");
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[实体] 买入留痕失败：" + ex.Message);
            }
        }
    }

    /// <summary>卖出后按买家身份做的检查（带客人对象，能认出罪犯客群）。</summary>
    [HarmonyPatch(typeof(PlayerStore), "PerformOnSoldCheck")]
    internal static class PerformOnSoldCheckPatch
    {
        private static void Postfix(GameItem item, StoreClient currentClient)
        {
            try
            {
                StockEconomy.Sold(item, currentClient, 0L, "结账");
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[实体] 记账失败：" + ex.Message);
            }
        }
    }

    /// <summary>
    /// 店铺侧物价联动：客人开价时乘上这件货所属品类当前的物价乘数（0.92~1.12）。
    ///
    /// 涨价的时候同样的货能多要一点，跌价的时候只能让价 —— 这样「通货膨胀」对玩家来说
    /// 是手上真能摸到的东西，而不只是行情页上的一个数字。夹子夹得很窄：
    /// 物价层是给站里添纹理的，不该盖过讨价还价本身。
    /// </summary>
    [HarmonyPatch(typeof(GameItem), "GetNegociatedValue")]
    internal static class NegotiatedValuePatch
    {
        private static void Postfix(GameItem __instance, ref long __result)
        {
            try
            {
                if (__result <= 0) return;
                double mul = StockEconomy.PriceMulOf(__instance);
                if (mul > 0.999 && mul < 1.001) return;
                long v = (long)Math.Round(__result * mul);
                __result = v < 1 ? 1 : v;
            }
            catch { }
        }
    }

    /// <summary>游戏写存档前，把运行时状态刷进 modData。</summary>
    [HarmonyPatch(typeof(PlayerStore), "WriteSlotFile")]
    internal static class SavePatch
    {
        private static void Prefix()
        {
            try
            {
                StockState.Flush(PlayerStore.instance);
            }
            catch (Exception ex)
            {
                Core.Log.Error("存档写入失败：" + ex);
            }
        }
    }

    /// <summary>读档后恢复股票账目。</summary>
    [HarmonyPatch(typeof(PlayerStore), "LoadGame")]
    internal static class LoadGamePatch
    {
        private static void Postfix()
        {
            try
            {
                StockEntry.EnterSave();
                StockState.Loaded = false;
                StockState.EnsureLoaded(PlayerStore.instance);
                // 夜间报告是整条存进存档的，老版本写下的浅色条目得在这里洗一遍，
                // 否则玩家重启多少次都还是那片糊在米色纸上的浅蓝浅绿。
                StockEngine.RepairNightLog(PlayerStore.instance);
            }
            catch (Exception ex)
            {
                Core.Log.Error("读档恢复失败：" + ex);
            }
        }
    }

    /// <summary>新开一局时清空股票账目。</summary>
    [HarmonyPatch(typeof (PlayerStore), "StartNewGame")]
    internal static class StartNewGamePatch
    {
        private static void Postfix()
        {
            try
            {
                StockEntry.EnterSave();
                StockState.Loaded = false;
                StockState.EnsureLoaded(PlayerStore.instance);
            }
            catch (Exception ex)
            {
                Core.Log.Error("新档初始化失败：" + ex);
            }
        }
    }

    /// <summary>从主菜单开新局：这时才算真正进了存档，入口按钮可以露面了。</summary>
    [HarmonyPatch(typeof(GameMaster), "NewGame")]
    internal static class NewGamePatch
    {
        private static void Postfix()
        {
            try
            {
                StockEntry.EnterSave();
            }
            catch (Exception ex)
            {
                Core.Log.Error("新局入口状态同步失败：" + ex);
            }
        }
    }

    /// <summary>
    /// 退回主菜单：PlayerStore 单例这时并不会消失，光看它会把入口按钮留在主菜单上，
    /// 所以这里显式标记「已离开存档」，入口和面板一起收掉。
    /// </summary>
    [HarmonyPatch(typeof(GameMaster), "QuitToMenu")]
    internal static class QuitToMenuPatch
    {
        private static void Postfix()
        {
            try
            {
                StockEntry.LeaveSave();
            }
            catch (Exception ex)
            {
                Core.Log.Error("离开存档状态同步失败：" + ex);
            }
        }
    }

    /// <summary>主菜单界面被创建（含开机首次进菜单）：同样算离开存档。</summary>
    [HarmonyPatch(typeof(MainMenuUIController), "Awake")]
    internal static class MainMenuAwakePatch
    {
        private static void Postfix()
        {
            try
            {
                StockEntry.LeaveSave();
            }
            catch (Exception ex)
            {
                Core.Log.Error("主菜单状态同步失败：" + ex);
            }
        }
    }

    /// <summary>
    /// 营业日开始：确认状态就绪。
    /// 这里刻意不做「租金不够就自动变卖股票」的兜底：折损只保留在爆仓这类被强制的
    /// 行情事件里；交不起租金就让玩家自己去交易页卖票（卖多少、什么时候卖由玩家定）。
    /// </summary>
    [HarmonyPatch(typeof(PlayerStore), "BeginDay")]
    internal static class BeginDayPatch
    {
        private static void Postfix()
        {
            try
            {
                PlayerStore store = PlayerStore.instance;
                if (store == null) return;

                // 同上：营业日必然在存档里
                StockEntry.EnterSave();
                StockState.EnsureLoaded(store);
            }
            catch (Exception ex)
            {
                Core.Log.Error("营业日初始化失败：" + ex);
            }
        }
    }
}
