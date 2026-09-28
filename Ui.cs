using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace StockMarket
{
    /// <summary>一条悬停记录：按钮的矩形 + 底色，鼠标压上去时自动提亮。</summary>
    internal sealed class UiHover
    {
        public RectTransform Rect;
        public Image Bg;
        public Color Base;
    }

    /// <summary>一个自建按钮的句柄，方便后面改文字、改底色、显隐。</summary>
    internal sealed class UiButton
    {
        public GameObject Go;
        public RectTransform Rect;
        public Image Bg;
        public Button Btn;
        public TextMeshProUGUI Label;
        public UiHover Hover;
        public float X, Y, W, H;

        public void Place(float x, float y, float w, float h)
        {
            X = x; Y = y; W = w; H = h;
            Ui.Place(Go, x, y, w, h);
        }

        /// <summary>相对父容器左下角定位。</summary>
        public void PlaceBottomLeft(float x, float y, float w, float h)
        {
            X = x; Y = y; W = w; H = h;
            Ui.PlaceBottomLeft(Go, x, y, w, h);
        }

        public void SetBg(Color c)
        {
            try
            {
                if (Bg != null) Bg.color = c;
                if (Hover != null) Hover.Base = c;
            }
            catch { }
        }

        public void SetText(string text)
        {
            try { if (Label != null) Label.text = Palette.Rt(text ?? ""); } catch { }
        }

        public void SetTextColor(Color c)
        {
            try { if (Label != null) Label.color = c; } catch { }
        }

        public void SetLabel(string text, Color c)
        {
            SetText(text);
            SetTextColor(c);
        }

        public void SetActive(bool on)
        {
            Ui.SetActive(Go, on);
        }

        public void SetInteractable(bool on)
        {
            try { if (Btn != null) Btn.interactable = on; } catch { }
        }
    }

    /// <summary>
    /// 自建 UGUI 工具集。API 手法照搬 PSPDA 的 Ugui.cs：
    /// 一律用 TypeOf&lt;T&gt; + AddComponent，绝不用 new GameObject(name, typeof(T))。
    /// 布局基础是 Place()：锚点左上、轴心左上，坐标就是「从父容器左上角量」。
    /// </summary>
    internal static class Ui
    {
        // TMP 对齐枚举用数值，避免枚举名解析问题。
        public const int AlignCenter = 514;    // 水平居中 / 垂直居中
        public const int AlignLeft = 513;      // 左对齐 / 垂直居中
        public const int AlignRight = 516;     // 右对齐 / 垂直居中
        public const int AlignTopLeft = 257;   // 左上

        private static Sprite _white;
        private static Sprite _card;   // 大圆角（卡片）
        private static Sprite _chip;   // 小圆角（按钮、槽）
        private static Sprite _circle; // 正圆（「?」这类圆形按钮）

        // ── 基础 ──────────────────────────────────────────────────────

        /// <summary>1×1 白图，所有纯色块都用它。</summary>
        public static Sprite White()
        {
            if (_white == null)
            {
                _white = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f));
            }
            return _white;
        }

        /// <summary>运行时生成的圆角九宫格图（卡片用，圆角 12px）。</summary>
        public static Sprite Card()
        {
            if (_card == null) _card = Round(40, 12, 13f);
            return _card;
        }

        /// <summary>运行时生成的圆角九宫格图（按钮/槽用，圆角 8px）。</summary>
        public static Sprite Chip()
        {
            if (_chip == null) _chip = Round(24, 8, 9f);
            return _chip;
        }

        /// <summary>正圆白图。「?」这种圆形按钮用，必须配 Image.Type.Simple（正方形按钮）。</summary>
        public static Sprite Circle()
        {
            if (_circle == null) _circle = Disc(64, 30f);
            return _circle;
        }

        /// <summary>画一张边缘抗锯齿的实心圆。</summary>
        private static Sprite Disc(int size, float radius)
        {
            try
            {
                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                float c = (size - 1) * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = x - c, dy = y - c;
                        float d = radius - Mathf.Sqrt(dx * dx + dy * dy);
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(d + 0.5f)));
                    }
                }
                tex.Apply();
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[界面] 生成圆形图失败，改用圆角图：" + ex.Message);
                return null;
            }
        }

        /// <summary>运行时画一张圆角白图，交给九宫格拉伸。失败返回 null，调用方会自动退回纯色块。</summary>
        private static Sprite Round(int size, int radius, float border)
        {
            try
            {
                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, CornerAlpha(x, y, size, radius)));
                    }
                }
                tex.Apply();
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f),
                    100f, 0u, 0, new Vector4(border, border, border, border));
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[界面] 生成圆角图失败，改用直角：" + ex.Message);
                return null;
            }
        }

        /// <summary>像素到圆角矩形边界的距离，用来做 1px 抗锯齿。</summary>
        private static float CornerAlpha(int x, int y, int size, int r)
        {
            float cx = Mathf.Min(x + 0.5f, size - x - 0.5f);
            float cy = Mathf.Min(y + 0.5f, size - y - 0.5f);
            if (cx >= r || cy >= r) return 1f;
            float dx = r - cx;
            float dy = r - cy;
            float d = r - Mathf.Sqrt(dx * dx + dy * dy);
            return Mathf.Clamp01(d + 0.5f);
        }

        /// <summary>拿 Il2Cpp 类型句柄，AddComponent 要用它。</summary>
        public static Il2CppSystem.Type TypeOf<T>() where T : Il2CppObjectBase
        {
            return Il2CppSystem.Type.internal_from_handle(
                IL2CPP.il2cpp_class_get_type(Il2CppClassPointerStore<T>.NativeClassPtr));
        }

        public static T Add<T>(GameObject go, string what) where T : Il2CppObjectBase
        {
            try
            {
                Component c = go.AddComponent(TypeOf<T>());
                if (c == null) return default(T);
                return c.TryCast<T>();
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[界面] 挂 " + what + " 失败：" + ex.Message);
                return default(T);
            }
        }

        /// <summary>建一个带 RectTransform 的空物件。</summary>
        public static GameObject New(string name, Transform parent)
        {
            GameObject go = new GameObject(name);
            RectTransform rt = Add<RectTransform>(go, name + ".Rect");
            if (rt == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            if (parent != null) rt.SetParent(parent, false);
            return go;
        }

        public static RectTransform Rect(GameObject go)
        {
            try { return go == null ? null : go.transform.TryCast<RectTransform>(); }
            catch { return null; }
        }

        // ── 布局 ──────────────────────────────────────────────────────

        /// <summary>铺满父容器，四周留 pad。</summary>
        public static void Stretch(GameObject go, float pad)
        {
            RectTransform rt = Rect(go);
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(pad, pad);
            rt.offsetMax = new Vector2(-pad, -pad);
        }

        /// <summary>铺满父容器，四边分别留白。</summary>
        public static void Inset(GameObject go, float left, float bottom, float right, float top)
        {
            RectTransform rt = Rect(go);
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>相对父容器左上角定位。整个界面的布局基础。</summary>
        public static void Place(GameObject go, float x, float y, float w, float h)
        {
            RectTransform rt = Rect(go);
            if (rt == null) return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            rt.localRotation = Quaternion.identity;
        }

        /// <summary>以父容器中心为原点定位（画布根节点用）。</summary>
        public static void PlaceCentered(GameObject go, float dx, float dy, float w, float h)
        {
            RectTransform rt = Rect(go);
            if (rt == null) return;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(dx, dy);
            rt.sizeDelta = new Vector2(w, h);
        }

        /// <summary>相对父容器左下角定位，用于钉在屏幕角落的常驻元素。</summary>
        public static void PlaceBottomLeft(GameObject go, float x, float y, float w, float h)
        {
            RectTransform rt = Rect(go);
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
            rt.localRotation = Quaternion.identity;
        }

        /// <summary>定位 + 旋转，画折线用。</summary>
        public static void PlaceRot(GameObject go, float x, float y, float w, float h, float angle)
        {
            RectTransform rt = Rect(go);
            if (rt == null) return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            rt.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        public static void SetActive(GameObject go, bool on)
        {
            try { if (go != null && go.activeSelf != on) go.SetActive(on); } catch { }
        }

        // ── 元件 ──────────────────────────────────────────────────────

        /// <summary>纯色块。</summary>
        public static Image MakeImage(Transform parent, string name, Color color, bool raycast)
        {
            GameObject go = New(name, parent);
            if (go == null) return null;
            Image img = Add<Image>(go, name + ".Image");
            if (img == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            img.sprite = White();
            img.type = Image.Type.Simple;
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        /// <summary>九宫格圆角块。sprite 为空时自动退回纯色块。</summary>
        public static Image MakeSliced(Transform parent, string name, Sprite sprite, Color tint, bool raycast)
        {
            GameObject go = New(name, parent);
            if (go == null) return null;
            Image img = Add<Image>(go, name + ".Image");
            if (img == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            img.sprite = sprite != null ? sprite : White();
            try { img.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple; } catch { }
            img.color = tint;
            img.raycastTarget = raycast;
            return img;
        }

        /// <summary>
        /// 一张卡片：先画描边色圆角块，再往里缩 2px 画深色纸面。
        /// 返回纸面 Image，需要改底色时用它。
        /// </summary>
        public static Image MakeCard(Transform parent, string name, float x, float y, float w, float h)
        {
            Image rim = MakeSliced(parent, name + ".Rim", Card(), Palette.CardRim, false);
            if (rim != null) Place(rim.gameObject, x, y, w, h);
            Image face = MakeSliced(parent, name + ".Face", Card(), Palette.CardBg, false);
            if (face != null) Place(face.gameObject, x + 2f, y + 2f, w - 4f, h - 4f);
            return face;
        }

        /// <summary>卡内嵌槽（表格底、图表底）。</summary>
        public static Image MakeSlot(Transform parent, string name, float x, float y, float w, float h, Color color)
        {
            Image img = MakeSliced(parent, name, Chip(), color, false);
            if (img != null) Place(img.gameObject, x, y, w, h);
            return img;
        }

        public static TextMeshProUGUI MakeText(Transform parent, string name, string text, float fontPx,
            Color color, int align, bool wrap)
        {
            GameObject go = New(name, parent);
            if (go == null) return null;
            TextMeshProUGUI t = Add<TextMeshProUGUI>(go, name + ".TMP");
            if (t == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            TMP_FontAsset font = Fonts.Font();
            if (font != null) t.font = font;
            // 富文本里的色值是按深色主题写死的，统一过一遍主题替换（见 Palette.Rt）
            t.text = Palette.Rt(text ?? "");
            t.fontSize = fontPx;
            t.color = color;
            try { t.alignment = (TextAlignmentOptions)align; } catch { }
            t.raycastTarget = false;
            try { t.enableWordWrapping = wrap; } catch { }
            try { t.overflowMode = 0; } catch { } // 0 = Overflow，不省略
            try { t.richText = true; } catch { }
            return t;
        }

        /// <summary>
        /// 开 TMP 的「截断」（overflowMode = Truncate）。
        ///
        /// 只在允许换行（enableWordWrapping = true）的文字块上真的开 —— 单行文字
        /// （wrap = false）只要内容比矩形宽，Truncate 会让整段一个字都不画：
        /// 好友列表就是被这一条坑的，行里只剩头像圆圈，名字和预览全没了
        /// （见 问题截图/优化过后左边太空了 可以做出微信的感觉.png）。
        /// 单行的溢出改用 Cut() 先把字符串截短。
        /// </summary>
        public static void Truncate(TextMeshProUGUI t)
        {
            if (t == null) return;
            try { if (t.enableWordWrapping) t.overflowMode = (TextOverflowModes)3; } catch { }
        }

        /// <summary>
        /// 按「半角宽」把一句话截短到能放进 maxW 像素，超出补「…」。
        /// 汉字算 2 个半角、ASCII 算 1 个，跟 StockUI.EstLines 一套口径。
        /// 富文本标签不计宽度；截断点若落在标签中间，会先退到标签前面。
        /// </summary>
        public static string Cut(string s, float fontPx, float maxW)
        {
            if (string.IsNullOrEmpty(s) || fontPx <= 0f) return s ?? "";
            int units = (int)(maxW / (fontPx * 0.5f));
            if (units < 6) units = 6;
            int col = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<')
                {
                    int e = s.IndexOf('>', i);
                    if (e > i) { i = e; continue; }
                }
                int w = c < 128 ? 1 : 2;
                // 留 2 个半角给结尾的「…」
                if (col + w > units - 2) return s.Substring(0, i) + "…";
                col += w;
            }
            return s;
        }

        /// <summary>
        /// 带悬停高亮和点击的按钮。transition 用 ColorTint，悬停会自己变亮。
        /// sprite 传 null 就是默认的圆角片；传 Ui.Circle() 能做出圆形按钮
        /// （圆形必须走 Simple，九宫格拉伸会把圆压成方块）。
        /// </summary>
        public static UiButton MakeButton(Transform parent, string name, string label, float fontPx,
            Color bg, Color fg, Action onClick, int align, Sprite sprite = null)
        {
            GameObject go = New(name, parent);
            if (go == null) return null;

            Image img = Add<Image>(go, name + ".Image");
            if (img == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            img.sprite = sprite != null ? sprite : Chip();
            try { img.type = sprite != null ? Image.Type.Simple : Image.Type.Sliced; } catch { }
            img.color = bg;
            img.raycastTarget = true;

            UiButton h = new UiButton { Go = go, Rect = Rect(go), Bg = img };
            h.Hover = RegisterHover(h.Rect, img, bg);

            TextMeshProUGUI t = MakeText(go.transform, "Label", label, fontPx, fg, align, false);
            if (t != null)
            {
                float padX = align == AlignLeft ? 14f : 0f;
                Place(t.gameObject, padX, 0f, 0f, 0f);
                RectTransform tr = Rect(t.gameObject);
                if (tr != null)
                {
                    tr.anchorMin = Vector2.zero;
                    tr.anchorMax = Vector2.one;
                    tr.offsetMin = new Vector2(padX, 0f);
                    tr.offsetMax = new Vector2(-padX, 0f);
                }
                h.Label = t;
            }

            Button b = Add<Button>(go, name + ".Button");
            if (b != null)
            {
                b.targetGraphic = img;
                try
                {
                    // 悬停高亮自己算（见 UpdateHover），不用 ColorTint：
                    // 这套 Il2Cpp 绑定里 ColorBlock 的属性是只读的，改不了。
                    b.transition = Selectable.Transition.None;
                }
                catch (Exception ex)
                {
                    Core.Debug("[界面] 关闭按钮过渡失败：" + ex.Message);
                }
                if (onClick != null)
                {
                    try
                    {
                        b.onClick.RemoveAllListeners();
                        b.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityAction>(onClick));
                    }
                    catch (Exception ex)
                    {
                        Core.Log.Warning("[界面] 绑定按钮失败：" + ex.Message);
                    }
                }
                h.Btn = b;
            }
            return h;
        }

        /// <summary>
        /// 单行输入框（TMP_InputField）。TMP 要求 viewport + textComponent + placeholder 三件套齐备，
        /// 少任何一个都会变成「点了没反应」或者「能打字但看不见字」。
        /// </summary>
        public static TMP_InputField MakeInput(Transform parent, string name, string hint, float fontPx,
            Color bg, Color fg, Color hintColor, Action<string> onChanged)
        {
            GameObject go = New(name, parent);
            if (go == null) return null;

            Image img = Add<Image>(go, name + ".Image");
            if (img == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            img.sprite = Chip();
            try { img.type = Image.Type.Sliced; } catch { }
            img.color = bg;
            img.raycastTarget = true;

            // viewport 负责裁掉溢出的文字。只留左右内边距、上下各缩 1px：
            // 输入框本身只有 30 上下高，四边各缩 10px 会把字裁掉一半，
            // 实机表现就是「数字只剩上半截」（见 问题截图/文字显示不全*.png）。
            GameObject vp = New(name + ".Viewport", go.transform);
            if (vp != null)
            {
                Add<RectMask2D>(vp, name + ".Mask");
                RectTransform vpr = Rect(vp);
                if (vpr != null)
                {
                    vpr.anchorMin = Vector2.zero;
                    vpr.anchorMax = Vector2.one;
                    vpr.pivot = new Vector2(0.5f, 0.5f);
                    vpr.offsetMin = new Vector2(9f, 1f);
                    vpr.offsetMax = new Vector2(-9f, -1f);
                }
            }
            Transform textParent = vp != null ? vp.transform : go.transform;
            TextMeshProUGUI txt = MakeText(textParent, name + ".Text", "", fontPx, fg, AlignLeft, false);
            TextMeshProUGUI ph = MakeText(textParent, name + ".Hint", hint, fontPx, hintColor, AlignLeft, false);
            // 正文与占位符都要铺满 viewport：TMP 是按矩形算对齐和换行的，
            // 留成 0×0 的话字会贴在左上角、被遮罩裁掉，看着就是「打了字但不显示」
            if (txt != null) Stretch(txt.gameObject, 0f);
            if (ph != null) Stretch(ph.gameObject, 0f);

            TMP_InputField f = Add<TMP_InputField>(go, name + ".Input");
            if (f == null) return null;
            try
            {
                if (vp != null) f.textViewport = Rect(vp);
                if (txt != null) f.textComponent = txt;
                if (ph != null) f.placeholder = ph;
                f.targetGraphic = img;
                f.richText = false;
                try { f.lineType = TMP_InputField.LineType.SingleLine; } catch { }
                TMP_FontAsset font = Fonts.Font();
                if (font != null) f.fontAsset = font;
                if (onChanged != null)
                {
                    f.onValueChanged.RemoveAllListeners();
                    f.onValueChanged.AddListener(DelegateSupport.ConvertDelegate<UnityAction<string>>(onChanged));
                }
                f.ForceLabelUpdate();
            }
            catch (Exception ex)
            {
                Core.Log.Warning("[界面] 配置输入框失败：" + ex.Message);
            }
            return f;
        }

        // ── 悬停高亮 ──────────────────────────────────────────────────

        private static readonly List<UiHover> _hovers = new List<UiHover>();
        private static int _hoverIndex = -1;

        private static UiHover RegisterHover(RectTransform rect, Image bg, Color baseColor)
        {
            UiHover h = new UiHover { Rect = rect, Bg = bg, Base = baseColor };
            _hovers.Add(h);
            return h;
        }

        /// <summary>重建界面前清空悬停表。</summary>
        public static void ResetHovers()
        {
            _hovers.Clear();
            _hoverIndex = -1;
        }

        /// <summary>
        /// 每帧调一次：鼠标压在哪个按钮上就把它提亮。
        /// 不用 UGUI 的悬停事件，因为自定义 MonoBehaviour 在 Il2Cpp 里要额外注册类型，太重。
        /// </summary>
        public static void UpdateHover()
        {
            try
            {
                int found = -1;
                if (Input.mousePresent)
                {
                    Vector2 mouse = Input.mousePosition;
                    for (int i = 0; i < _hovers.Count; i++)
                    {
                        UiHover h = _hovers[i];
                        if (h.Rect == null) continue;
                        if (!h.Rect.gameObject.activeInHierarchy) continue;
                        if (RectTransformUtility.RectangleContainsScreenPoint(h.Rect, mouse, null))
                        {
                            found = i;
                            break;
                        }
                    }
                }
                if (found == _hoverIndex) return;

                if (_hoverIndex >= 0 && _hoverIndex < _hovers.Count)
                {
                    Paint(_hovers[_hoverIndex], false);
                }
                _hoverIndex = found;
                if (_hoverIndex >= 0) Paint(_hovers[_hoverIndex], true);
            }
            catch (Exception ex)
            {
                Core.Debug("[界面] 悬停检测失败：" + ex.Message);
            }
        }

        private static void Paint(UiHover h, bool on)
        {
            try
            {
                if (h == null || h.Bg == null) return;
                h.Bg.color = on ? Palette.Shift(h.Base, 0.22f) : h.Base;
            }
            catch { }
        }

        /// <summary>建一张独立画布（覆盖模式、最高排序、像素级缩放）。</summary>
        public static Canvas NewCanvas(string name)
        {
            GameObject go = new GameObject(name);
            Canvas canvas = Add<Canvas>(go, name + ".Canvas");
            if (canvas == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            try
            {
                canvas.renderMode = 0;      // 0 = ScreenSpaceOverlay
                canvas.sortingOrder = 32000;
                canvas.overrideSorting = true;
            }
            catch (Exception ex)
            {
                Core.Debug("[界面] 设置画布失败：" + ex.Message);
            }
            CanvasScaler scaler = Add<CanvasScaler>(go, name + ".Scaler");
            if (scaler != null)
            {
                try
                {
                    scaler.uiScaleMode = 0;   // 0 = ConstantPixelSize
                    scaler.scaleFactor = 1f;
                    scaler.referencePixelsPerUnit = 100f;
                }
                catch { }
            }
            GraphicRaycaster raycaster = Add<GraphicRaycaster>(go, name + ".Raycaster");
            if (raycaster == null) Core.Log.Warning("[界面] 没挂上 GraphicRaycaster，按钮会点不动。");
            return canvas;
        }

        /// <summary>把画布缩放改成指定倍率。</summary>
        public static void SetScale(Canvas canvas, float scale)
        {
            try
            {
                if (canvas == null) return;
                CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler != null) scaler.scaleFactor = scale;
            }
            catch (Exception ex)
            {
                Core.Debug("[界面] 缩放失败：" + ex.Message);
            }
        }
    }
}
