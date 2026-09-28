using System;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(StockMarket.Core), "StockMarket", "1.0.0", StockMarket.StockVersion.Author)]
[assembly: MelonGame("Questing Goose Studio", "Probably Stolen")]

namespace StockMarket
{
    /// <summary>
    /// 模组入口。负责生命周期、快捷键、以及把各子系统串起来。
    /// </summary>
    public sealed class Core : MelonMod
    {
        public static MelonLogger.Instance Log;

        public static Core Instance { get; private set; }

        private MelonPreferences_Category _cfg;
        private MelonPreferences_Entry<KeyCode> _toggleKey;
        private MelonPreferences_Entry<bool> _verbose;
        private MelonPreferences_Entry<bool> _lightTheme;
        private MelonPreferences_Entry<bool> _chartInfo;

        /// <summary>
        /// 界面风格偏好（纯外观，跟存档无关）。面板里那个「日/夜」按钮改的就是它。
        /// </summary>
        public static bool LightThemePref
        {
            get { return Instance != null && Instance._lightTheme != null && Instance._lightTheme.Value; }
            set
            {
                if (Instance == null || Instance._lightTheme == null) return;
                if (Instance._lightTheme.Value == value) return;
                Instance._lightTheme.Value = value;
                try { MelonPreferences.Save(); } catch { }
            }
        }

        /// <summary>
        /// 图表悬停读数开关：鼠标压在 K 线 / 折线 / 副图上时要不要报数。
        /// 在任意一张图上右键就能开关，也存进偏好里，重启还记得。
        /// </summary>
        public static bool ChartInfoPref
        {
            get { return Instance == null || Instance._chartInfo == null || Instance._chartInfo.Value; }
            set
            {
                if (Instance == null || Instance._chartInfo == null) return;
                if (Instance._chartInfo.Value == value) return;
                Instance._chartInfo.Value = value;
                try { MelonPreferences.Save(); } catch { }
            }
        }

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            _cfg = MelonPreferences.CreateCategory("StockMarket", "星际证券");
            _toggleKey = _cfg.CreateEntry<KeyCode>("ToggleKey", KeyCode.F7, "打开/关闭星际证券面板");
            _verbose = _cfg.CreateEntry<bool>("Verbose", false, "输出调试日志");
            _lightTheme = _cfg.CreateEntry<bool>("LightTheme", false, "界面用亮色风格（false = 深色）");
            _chartInfo = _cfg.CreateEntry<bool>("ChartInfo", true, "图表上悬停显示读数（在图上右键可开关）");

            // 建面板之前先把上次选的风格刷进去，否则第一眼看到的是深色
            Palette.SetTheme(LightThemePref ? Palette.Theme.Light : Palette.Theme.Dark);

            try
            {
                PatchAll();
                Log.Msg("星际证券 " + StockVersion.Current + " 已加载。默认按 F7 打开面板。");
            }
            catch (Exception ex)
            {
                Log.Error("Harmony 补丁失败：" + ex);
            }

            // 版本检查放最后：网络那部分跑在后台线程，不挡加载
            StockVersion.Start();
        }

        /// <summary>
        /// 逐个补丁类挂载。PatchAll 是一锤子买卖——只要有一个目标方法对不上
        /// （游戏版本差异、方法改名），整批补丁都会失效；分开挂就互不牵连。
        /// </summary>
        private void PatchAll()
        {
            Type[] patches =
            {
                typeof(DayStartPatch),
                typeof(SavePatch),
                typeof(LoadGamePatch),
                typeof(StartNewGamePatch),
                typeof(NewGamePatch),
                typeof(QuitToMenuPatch),
                typeof(MainMenuAwakePatch),
                typeof(BeginDayPatch),
                typeof(ItemsSoldPatch),
                typeof(ClientItemSoldPatch),
                // 下面六个接了「客群识别 / 物价乘数」那条线：物价乘数挂在
                // GameItem.GetNegociatedValue 上（买卖双方开价都要乘），客群识别
                // 靠带 StoreClient 的那几个钩子。漏一个，物价层就整体失效。
                typeof(ClientItemBoughtPatch),
                typeof(BuyItemPatch),
                typeof(SellItemPatch),
                typeof(StoreItemBoughtPatch),
                typeof(PerformOnSoldCheckPatch),
                typeof(NegotiatedValuePatch),
            };
            for (int i = 0; i < patches.Length; i++)
            {
                try
                {
                    HarmonyInstance.CreateClassProcessor(patches[i]).Patch();
                }
                catch (Exception ex)
                {
                    Log.Error("补丁 " + patches[i].Name + " 挂载失败：" + ex.Message);
                }
            }
        }

        public static void Debug(string message)
        {
            if (Instance != null && Instance._verbose != null && Instance._verbose.Value)
            {
                Log.Msg("[调试] " + message);
            }
        }

        public override void OnUpdate()
        {
            try
            {
                StockEntry.Sync();
                // 左下角的版本牌：顺手把「本版本还受不受支持」的通知也管了
                StockVersion.Sync();
                // 分时图的推进与挂单撮合。刻意放在 StockUI.Tick 之外：
                // 玩家关着面板专心做生意的时候，盘面也该继续走。
                StockIntraday.Poll();
                StockUI.Tick();
                if (Input.GetKeyDown(_toggleKey.Value))
                {
                    StockUI.Toggle();
                }
                else if (StockUI.IsOpen && !StockUI.IsTyping && Input.GetKeyDown(KeyCode.Escape))
                {
                    // 搜索框正在输入时 ESC 交给输入框自己处理（先失焦），别顺手把面板也关了。
                    // 说明卡 / 引导气泡开着时，ESC 先收掉那一层，别一下把整个面板关了。
                    if (!StockUI.EscBack()) StockUI.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Warning("面板开关失败：" + ex.Message);
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            StockEntry.OnSceneLoaded();
            StockVersion.OnSceneLoaded();
            StockUI.OnSceneLoaded();
        }
    }
}
