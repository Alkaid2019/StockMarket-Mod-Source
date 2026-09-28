using System;
using Il2Cpp;
using UnityEngine;

namespace StockMarket
{
    /// <summary>
    /// 屏幕左下角的【星际证券】常驻入口。
    ///
    /// 原先的做派是克隆 ESC 菜单里的列表按钮，有两个绕不开的毛病：
    ///   · 位置被菜单的 LayoutGroup 决定，只能排在「退出游戏」后面，会顶出菜单面板的边框；
    ///   · 克隆体带着源按钮的组件，文本会被游戏自己的按钮逻辑重置，
    ///     所以第一次打开菜单显示的还是源按钮的字，点过一次才被我们改回来。
    /// 现在改成自建一张独立小画布，钉在左下角，文案从创建那一刻起就是对的。
    /// </summary>
    internal static class StockEntry
    {
        private const float EntryW = 132f;
        private const float EntryH = 44f;
        private const float MarginX = 22f;
        private const float MarginY = 20f;
        private const int CanvasOrder = 31000;   // 低于主面板（32000），主面板压在它上面

        private static Canvas _canvas;
        private static bool _ready;     // 入口已建好，当前应该存在
        private static bool _visible;   // 上一次同步给引擎的可见性

        /// <summary>
        /// 是否正处于某个存档里。
        ///
        /// 不能只看 PlayerStore.instance：退回主菜单后这个单例仍然在，入口会跟着飘到主菜单上。
        /// 所以改用游戏自己的流程事件来标记——读档/新开一局算进入存档，
        /// GameMaster.QuitToMenu 或主菜单界面重新 Awake 算离开存档。
        /// </summary>
        public static bool InSave;

        /// <summary>场景切换后旧物体已被销毁，清掉引用重新来过。</summary>
        public static void OnSceneLoaded()
        {
            _canvas = null;
            _ready = false;
            _visible = false;
        }

        /// <summary>
        /// 每帧同步：进了存档才显示，回主菜单就藏起来；主面板打开时也让位，
        /// 免得小屏上两个画布叠在一起。
        /// </summary>
        public static void Sync()
        {
            bool inStore = false;
            try { inStore = InSave && PlayerStore.instance != null; } catch { }

            if (inStore && !_ready) _ready = Build();
            else if (!inStore && _ready) _ready = false;

            bool visible = _ready && !StockUI.IsOpen;
            if (visible == _visible) return;
            _visible = visible;
            if (_canvas != null && Alive(_canvas.gameObject))
            {
                Ui.SetActive(_canvas.gameObject, visible);
            }
        }

        /// <summary>
        /// 换界面风格时调：入口按钮的颜色是建的时候定死的，不重建还是旧配色。
        /// 只拆不建，下一帧 Sync 会自己把它建回来（面板开着时它本来就该藏着）。
        /// </summary>
        public static void Rebuild()
        {
            try
            {
                if (_canvas != null) UnityEngine.Object.Destroy(_canvas.gameObject);
            }
            catch { }
            _canvas = null;
            _ready = false;
            _visible = false;
        }

        private static bool Build()
        {
            try
            {
                Canvas canvas = Ui.NewCanvas("StockMarket_EntryCanvas");
                if (canvas == null) return false;
                canvas.sortingOrder = CanvasOrder;

                UiButton button = Ui.MakeButton(canvas.transform, "Entry", "星际证券", 20f,
                    Palette.BtnGreen, Palette.Title, OpenPanel, Ui.AlignCenter);
                if (button == null)
                {
                    UnityEngine.Object.Destroy(canvas.gameObject);
                    return false;
                }
                button.PlaceBottomLeft(MarginX, MarginY, EntryW, EntryH);

                _canvas = canvas;
                _visible = false;
                Ui.SetActive(canvas.gameObject, false);
                Core.Log.Msg("已在屏幕左下角放置【星际证券】入口。");
                return true;
            }
            catch (Exception ex)
            {
                Core.Log.Warning("创建【星际证券】入口失败：" + ex.Message);
                return false;
            }
        }

        private static void OpenPanel()
        {
            Core.Log.Msg("[操作] 点击【星际证券】入口按钮");
            StockUI.Toggle();
        }

        /// <summary>进入存档：读档成功或新开一局时调用。</summary>
        public static void EnterSave()
        {
            InSave = true;
        }

        /// <summary>离开存档：退回主菜单时调用，顺手把面板收掉并还原鼠标交互。</summary>
        public static void LeaveSave()
        {
            InSave = false;
            StockUI.Close();
        }

        private static bool Alive(GameObject go)
        {
            try { return go != null; } catch { return false; }
        }
    }
}
