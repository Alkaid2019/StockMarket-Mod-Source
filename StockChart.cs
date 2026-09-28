using System;
using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StockMarket
{
    /// <summary>一条折线。Values 里可以是 double.NaN，表示「这一天没有值」，画的时候会断开。</summary>
    internal sealed class ChartSeries
    {
        public string Label;
        public Color Color;
        public List<double> Values = new List<double>();
    }

    /// <summary>
    /// 走势图与副图。全部画在自建 UGUI 上：白图 + RectTransform，不用第三方库，也不碰顶点流。
    /// 坐标系是「相对图表宿主左上角」，向右 x 增大，向下 y 增大。
    ///
    /// 主图（价格）和副图（成交量 / MACD / KDJ / RSI / OBV / CCI）走的是两套边距：
    /// 副图不需要那么多刻度宽度，留窄一点把画图区让给数据。
    /// </summary>
    internal static class StockChart
    {
        // 主图边距
        private const float PadLeft = 58f;
        private const float PadRight = 14f;
        private const float PadTop = 14f;
        private const float PadBottom = 26f;

        // 副图边距
        private const float SubPadLeft = 54f;
        private const float SubPadRight = 14f;
        private const float SubPadTop = 8f;
        private const float SubPadBottom = 20f;

        private const float FsTick = 14f;
        private const float FsSubTick = 12f;

        // 分时图自己的边距 + 底部量图占比。放出来共用：StockUI 的悬停读数要按同一套
        // 坐标反查鼠标压在哪一段，两边各写一份数字迟早会对不上。
        public const float IntraPadL = 58f;
        public const float IntraPadR = 14f;
        public const float IntraPadT = 12f;
        public const float IntraPadB = 22f;
        public const float IntraVolRatio = 0.28f;

        private static float _padL, _padR, _padT, _padB, _fs;

        /// <summary>
        /// 主图的边距。放出来给悬停读数用：反查「鼠标压在哪一格」必须跟画图共用同一套坐标，
        /// 两边各写一份数字，竖线迟早会偏出蜡烛。
        /// </summary>
        public static void MainPads(out float l, out float r, out float t, out float b)
        {
            l = PadLeft; r = PadRight; t = PadTop; b = PadBottom;
        }

        /// <summary>副图（成交量 / 振荡指标）的边距，同上。</summary>
        public static void SubPads(out float l, out float r, out float t, out float b)
        {
            l = SubPadLeft; r = SubPadRight; t = SubPadTop; b = SubPadBottom;
        }

        private static void UseMainPad()
        {
            _padL = PadLeft; _padR = PadRight; _padT = PadTop; _padB = PadBottom; _fs = FsTick;
        }

        private static void UseSubPad()
        {
            _padL = SubPadLeft; _padR = SubPadRight; _padT = SubPadTop; _padB = SubPadBottom; _fs = FsSubTick;
        }

        // ══════════════════════════════════════════════════════════════
        //  折线图（主图）
        // ══════════════════════════════════════════════════════════════

        public static void DrawLines(RectTransform host, float hostW, float hostH,
            List<ChartSeries> series, string[] labels)
        {
            try
            {
                UseMainPad();
                Clear(host);
                if (host == null) return;

                float plotW = hostW - _padL - _padR;
                float plotH = hostH - _padT - _padB;
                if (plotW <= 10f || plotH <= 10f) return;

                int n = 0;
                for (int s = 0; s < series.Count; s++)
                {
                    if (series[s] != null && series[s].Values.Count > n) n = series[s].Values.Count;
                }
                // 只有 1 个点时画出来就是个孤零零的圆点，配上坐标轴更像坏了，直接走空态
                if (n < 2) { Empty(host, hostW, hostH); return; }

                double min = double.MaxValue;
                double max = double.MinValue;
                for (int s = 0; s < series.Count; s++)
                {
                    List<double> v = series[s] != null ? series[s].Values : null;
                    if (v == null) continue;
                    for (int i = 0; i < v.Count; i++)
                    {
                        if (double.IsNaN(v[i])) continue;
                        if (v[i] < min) min = v[i];
                        if (v[i] > max) max = v[i];
                    }
                }
                if (min > max) { Empty(host, hostW, hostH); return; }
                Pad(ref min, ref max);

                // 横坐标一律用「槽位中心」：蜡烛、量柱、指标线、刻度线共用同一套，
                // 否则线会和柱子错开半格（副图 MACD 之前就是这个毛病）。
                float slot = plotW / n;
                Axis(host, plotW, plotH, min, max, SlotTicks(n, labels), "元");

                for (int s = 0; s < series.Count; s++)
                {
                    ChartSeries cs = series[s];
                    if (cs == null || cs.Values.Count == 0) continue;
                    List<double> v = cs.Values;
                    for (int i = 0; i < v.Count; i++)
                    {
                        if (double.IsNaN(v[i])) continue;
                        float x = _padL + (i + 0.5f) * slot;
                        float y = Y(v[i], min, max, plotH);
                        if (i + 1 < v.Count && !double.IsNaN(v[i + 1]))
                        {
                            Segment(host, x, y, _padL + (i + 1.5f) * slot,
                                Y(v[i + 1], min, max, plotH), cs.Color);
                        }
                        Dot(host, x, y, cs.Color);
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Log.Msg("绘制折线图失败：" + ex);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  K 线蜡烛图（主图）
        // ══════════════════════════════════════════════════════════════

        public static void DrawCandles(RectTransform host, float hostW, float hostH,
            List<Bar> bars, string[] labels)
        {
            DrawCandles(host, hostW, hostH, bars, labels, null);
        }

        /// <summary>
        /// overlays 是叠加在主图上的线（均线、布林带）。它们参与纵轴取值范围的计算，
        /// 否则布林带的上轨会被裁到画布外面去。
        /// </summary>
        public static void DrawCandles(RectTransform host, float hostW, float hostH,
            List<Bar> bars, string[] labels, List<ChartSeries> overlays)
        {
            try
            {
                UseMainPad();
                Clear(host);
                if (host == null) return;

                float plotW = hostW - _padL - _padR;
                float plotH = hostH - _padT - _padB;
                if (plotW <= 10f || plotH <= 10f) return;

                int n = bars != null ? bars.Count : 0;
                if (n < 2) { Empty(host, hostW, hostH); return; }

                double min = double.MaxValue;
                double max = double.MinValue;
                for (int i = 0; i < n; i++)
                {
                    if (bars[i].Low < min) min = bars[i].Low;
                    if (bars[i].High > max) max = bars[i].High;
                }
                if (overlays != null)
                {
                    for (int s = 0; s < overlays.Count; s++)
                    {
                        List<double> v = overlays[s] != null ? overlays[s].Values : null;
                        if (v == null) continue;
                        for (int i = 0; i < v.Count; i++)
                        {
                            if (double.IsNaN(v[i])) continue;
                            if (v[i] < min) min = v[i];
                            if (v[i] > max) max = v[i];
                        }
                    }
                }
                Pad(ref min, ref max);

                float slot = plotW / n;
                Axis(host, plotW, plotH, min, max, SlotTicks(n, labels), "元");

                float bodyW = Mathf.Clamp(slot * 0.62f, 1.5f, 14f);
                for (int i = 0; i < n; i++)
                {
                    Bar c = bars[i];
                    float cx = _padL + (i + 0.5f) * slot;
                    float yHigh = Y(c.High, min, max, plotH);
                    float yLow = Y(c.Low, min, max, plotH);
                    float yOpen = Y(c.Open, min, max, plotH);
                    float yClose = Y(c.Close, min, max, plotH);
                    Color col = c.Close >= c.Open ? Palette.Up : Palette.Down;

                    // 影线
                    float wickH = Mathf.Max(yLow - yHigh, 1.5f);
                    Block(host, "W" + i, cx - 0.75f, yHigh, 1.5f, wickH, col);

                    // 实体
                    float top = Mathf.Min(yOpen, yClose);
                    float bodyH = Mathf.Max(Mathf.Abs(yClose - yOpen), 1.5f);
                    Block(host, "B" + i, cx - bodyW * 0.5f, top, bodyW, bodyH, col);
                }

                if (overlays != null)
                {
                    for (int s = 0; s < overlays.Count; s++)
                    {
                        ChartSeries cs = overlays[s];
                        if (cs == null) continue;
                        List<double> v = cs.Values;
                        for (int i = 0; i + 1 < v.Count && i + 1 < n; i++)
                        {
                            if (double.IsNaN(v[i]) || double.IsNaN(v[i + 1])) continue;
                            Segment(host,
                                _padL + (i + 0.5f) * slot, Y(v[i], min, max, plotH),
                                _padL + (i + 1.5f) * slot, Y(v[i + 1], min, max, plotH), cs.Color);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Log.Msg("绘制 K 线图失败：" + ex);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  副图：成交量
        // ══════════════════════════════════════════════════════════════

        /// <summary>成交量柱：阳线红柱、阴线绿柱，另叠一条量能均线。</summary>
        public static void DrawVolume(RectTransform host, float hostW, float hostH,
            List<Bar> bars, List<double> ma, string[] labels)
        {
            try
            {
                UseSubPad();
                Clear(host);
                if (host == null) return;

                float plotW = hostW - _padL - _padR;
                float plotH = hostH - _padT - _padB;
                if (plotW <= 10f || plotH <= 10f) return;

                int n = bars != null ? bars.Count : 0;
                if (n < 2) { Empty(host, hostW, hostH); return; }

                double max = 0;
                for (int i = 0; i < n; i++) if (bars[i].Volume > max) max = bars[i].Volume;
                if (ma != null)
                {
                    for (int i = 0; i < ma.Count; i++)
                    {
                        if (!double.IsNaN(ma[i]) && ma[i] > max) max = ma[i];
                    }
                }
                if (max <= 0) { Empty(host, hostW, hostH); return; }
                max *= 1.08;

                float slot = plotW / n;
                Axis(host, plotW, plotH, 0, max, SlotTicks(n, labels), null);

                float barW = Mathf.Clamp(slot * 0.62f, 1.5f, 14f);
                float baseY = _padT + plotH;   // 0 成交量的位置就是画图区底线
                for (int i = 0; i < n; i++)
                {
                    float cx = _padL + (i + 0.5f) * slot;
                    float y = Y(bars[i].Volume, 0, max, plotH);
                    float h = Mathf.Max(baseY - y, 1f);
                    Block(host, "V" + i, cx - barW * 0.5f, y, barW, h,
                        bars[i].Close >= bars[i].Open ? Palette.Up : Palette.Down);
                }

                if (ma != null)
                {
                    for (int i = 0; i + 1 < ma.Count && i + 1 < n; i++)
                    {
                        if (double.IsNaN(ma[i]) || double.IsNaN(ma[i + 1])) continue;
                        Segment(host,
                            _padL + (i + 0.5f) * slot, Y(ma[i], 0, max, plotH),
                            _padL + (i + 1.5f) * slot, Y(ma[i + 1], 0, max, plotH), Palette.Gold);
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Log.Msg("绘制成交量图失败：" + ex);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  副图：通用振荡指标（MACD / KDJ / RSI / OBV / CCI）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 通用副图。lines 是若干条线，hist 是可选柱（MACD 的柱状体用）。
        /// fixedMin / fixedMax 传 NaN 表示按数据自适应；RSI 传 0 / 100 固定量程更好读。
        /// </summary>
        public static void DrawOsc(RectTransform host, float hostW, float hostH,
            List<ChartSeries> lines, List<double> hist, string[] labels,
            double fixedMin, double fixedMax)
        {
            try
            {
                UseSubPad();
                Clear(host);
                if (host == null) return;

                float plotW = hostW - _padL - _padR;
                float plotH = hostH - _padT - _padB;
                if (plotW <= 10f || plotH <= 10f) return;

                int n = 0;
                for (int s = 0; s < lines.Count; s++)
                {
                    if (lines[s] != null && lines[s].Values.Count > n) n = lines[s].Values.Count;
                }
                if (hist != null && hist.Count > n) n = hist.Count;
                if (n < 2) { Empty(host, hostW, hostH); return; }

                double min, max;
                if (!double.IsNaN(fixedMin) && !double.IsNaN(fixedMax))
                {
                    min = fixedMin;
                    max = fixedMax;
                }
                else
                {
                    min = double.MaxValue;
                    max = double.MinValue;
                    for (int s = 0; s < lines.Count; s++)
                    {
                        List<double> v = lines[s] != null ? lines[s].Values : null;
                        if (v == null) continue;
                        for (int i = 0; i < v.Count; i++)
                        {
                            if (double.IsNaN(v[i])) continue;
                            if (v[i] < min) min = v[i];
                            if (v[i] > max) max = v[i];
                        }
                    }
                    if (hist != null)
                    {
                        for (int i = 0; i < hist.Count; i++)
                        {
                            if (double.IsNaN(hist[i])) continue;
                            if (hist[i] < min) min = hist[i];
                            if (hist[i] > max) max = hist[i];
                        }
                    }
                    if (min > max) { Empty(host, hostW, hostH); return; }
                    Pad(ref min, ref max);
                    // 有正有负的指标（MACD / CCI）把 0 轴放进量程里，不然零轴会跑出画面
                    if (min > 0 && min < (max - min) * 0.5) min = 0;
                    if (max < 0 && max > (min - max) * 0.5) max = 0;
                }

                float slot = plotW / n;
                Axis(host, plotW, plotH, min, max, SlotTicks(n, labels), null);

                // 柱状体（MACD）
                if (hist != null)
                {
                    float barW = Mathf.Clamp(slot * 0.5f, 1.5f, 12f);
                    float zeroY = Y(0, min, max, plotH);
                    for (int i = 0; i < hist.Count && i < n; i++)
                    {
                        if (double.IsNaN(hist[i])) continue;
                        float cx = _padL + (i + 0.5f) * slot;
                        float y = Y(hist[i], min, max, plotH);
                        float top = Mathf.Min(y, zeroY);
                        float h = Mathf.Max(Mathf.Abs(y - zeroY), 1f);
                        Block(host, "H" + i, cx - barW * 0.5f, top, barW, h,
                            hist[i] >= 0 ? Palette.Up : Palette.Down);
                    }
                }

                // 线：和柱子、刻度线共用槽位中心，不能再用「首尾贴边」的算法
                for (int s = 0; s < lines.Count; s++)
                {
                    ChartSeries cs = lines[s];
                    if (cs == null || cs.Values.Count == 0) continue;
                    List<double> v = cs.Values;
                    for (int i = 0; i < v.Count; i++)
                    {
                        if (double.IsNaN(v[i])) continue;
                        float x = _padL + (i + 0.5f) * slot;
                        float y = Y(v[i], min, max, plotH);
                        if (i + 1 < v.Count && !double.IsNaN(v[i + 1]))
                        {
                            Segment(host, x, y, _padL + (i + 1.5f) * slot,
                                Y(v[i + 1], min, max, plotH), cs.Color);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Log.Msg("绘制副图失败：" + ex);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  分时图（日内图）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 分时图：上半是价格（白线 = 实时价，黄线 = 当日均价，灰虚线 = 昨收），
        /// 下半是逐段成交量柱。
        ///
        /// 这版走自己的边距：价格区和量能区共用一张画布，用 PriceY / 两个区域各自的
        /// 高度分开算，不复用主图那套 _padT + plotH 的换算，免得两边打架。
        ///
        /// path 只传**已走段**，x 还是按 totalSlots 铺格子，所以没走到的地方自然留白，
        /// 图上一眼能看出「今天走到哪儿了」。
        /// </summary>
        public static void DrawIntraday(RectTransform host, float hostW, float hostH,
            double[] path, double[] vol, double prevClose, int totalSlots, string[] labels)
        {
            try
            {
                Clear(host);
                if (host == null) return;

                const float padL = IntraPadL, padR = IntraPadR, padT = IntraPadT, padB = IntraPadB;
                const float volRatio = IntraVolRatio;
                float plotW = hostW - padL - padR;
                float plotH = hostH - padT - padB;
                if (plotW <= 10f || plotH <= 24f || totalSlots < 2) return;

                int n = path != null ? path.Length : 0;
                if (n < 2)
                {
                    Text(host, "Empty", 0f, hostH * 0.5f - 12f, hostW, 24f,
                        "开盘了，接待第一位客人后开始记录盘中走势", Palette.Muted, Ui.AlignCenter);
                    return;
                }

                float volH = plotH * volRatio;
                float priceH = plotH - volH - 6f;
                float volTop = padT + priceH + 6f;
                float volBase = padT + plotH;

                // 纵轴量程：可见价格 ∪ 昨收。昨收必须在视野里，虚线才有参照意义。
                double min, max;
                IntraRange(path, prevClose, out min, out max);

                double volMax = 0;
                if (vol != null) for (int i = 0; i < vol.Length; i++) if (vol[i] > volMax) volMax = vol[i];
                if (volMax <= 0) volMax = 1.0;

                _fs = FsTick;
                float slot = plotW / totalSlots;

                // ── 纵网格 + 右侧价格刻度（只铺价格区）──
                double span = max - min;
                for (int k = 0; k < 3; k++)
                {
                    float y = padT + priceH * k / 2f;
                    Block(host, "Grid" + k, padL, y, plotW, 1f, Palette.Grid);
                    string txt = Fmt(max - span * k / 2.0, span);
                    if (k == 0) txt += "元";
                    Text(host, "Y" + k, 0f, y - 9f, padL - 6f, 18f, txt, Palette.Muted, Ui.AlignRight);
                }

                // ── 昨收虚线 ──
                float prevY = padT + (float)((1.0 - (prevClose - min) / span) * priceH);
                if (prevClose > 0 && prevY >= padT && prevY <= padT + priceH)
                {
                    for (float x = 0f; x < plotW; x += 12f)
                    {
                        Block(host, "Prev", padL + x, prevY, 6f, 1f, Palette.Muted);
                    }
                }

                // ── 横网格 + 底部刻度：刻度按「槽位中心」取，和折线共用同一套坐标 ──
                int last = totalSlots - 1;
                for (int k = 0; k < labels.Length; k++)
                {
                    int idx = labels.Length > 1 ? k * last / (labels.Length - 1) : 0;
                    float x = padL + (idx + 0.5f) * slot;
                    if (k > 0 && k < labels.Length - 1) Block(host, "VG" + k, x, padT, 1f, plotH, Palette.Grid);
                    float cx = Mathf.Clamp(x, 38f, Mathf.Max(38f, hostW - 38f));
                    Text(host, "X" + k, cx - 38f, padT + plotH + 2f, 76f, 18f, labels[k],
                        Palette.Muted, Ui.AlignCenter);
                }

                // ── 量柱（画在价格线下面）──
                float barW = Mathf.Clamp(slot * 0.62f, 1.5f, 14f);
                int bars = vol != null ? vol.Length : 0;
                for (int i = 0; i < bars; i++)
                {
                    float cx = padL + (i + 0.5f) * slot;
                    double t = vol[i] / volMax;
                    if (t < 0.0) t = 0.0;
                    if (t > 1.0) t = 1.0;
                    float h = Mathf.Max((float)t * volH, 1f);
                    bool rise = (i + 1 < n) ? path[i + 1] >= path[i] : true;
                    Block(host, "V" + i, cx - barW * 0.5f, volBase - h, barW, h,
                        rise ? Palette.Up : Palette.Down);
                }

                // ── 黄线：当日均价（按各段成交量加权的累计均价）──
                double cumSum = 0.0, cumW = 0.0;
                float prevAx = 0f, prevAy = 0f;
                for (int i = 0; i < n; i++)
                {
                    double ax = path[0];
                    if (i > 0)
                    {
                        double w = (vol != null && i - 1 < vol.Length && vol[i - 1] > 0) ? vol[i - 1] : 1.0;
                        cumSum += path[i] * w;
                        cumW += w;
                        ax = cumW > 0 ? cumSum / cumW : path[i];
                    }
                    float x = padL + (i + 0.5f) * slot;
                    float y = padT + (float)((1.0 - (ax - min) / span) * priceH);
                    if (i > 0) Segment(host, prevAx, prevAy, x, y, Palette.Gold);
                    prevAx = x;
                    prevAy = y;
                }

                // ── 白线：实时价 ──
                float prevX = 0f, prevY2 = 0f;
                for (int i = 0; i < n; i++)
                {
                    float x = padL + (i + 0.5f) * slot;
                    float y = padT + (float)((1.0 - (path[i] - min) / span) * priceH);
                    if (i > 0) Segment(host, prevX, prevY2, x, y, Palette.Title);
                    prevX = x;
                    prevY2 = y;
                }

                // ── 现价光标：那一点画实心点，右边挂价格标签 ──
                Dot(host, prevX, prevY2, Palette.Title);
                float lx = prevX + 8f;
                float lw = 96f;
                if (lx + lw > hostW) lx = prevX - lw - 8f;
                Text(host, "Now", lx, prevY2 - 10f, lw, 20f,
                    path[n - 1].ToString("0.00"), Palette.Title,
                    lx < prevX ? Ui.AlignRight : Ui.AlignLeft);
            }
            catch (Exception ex)
            {
                Core.Log.Msg("绘制分时图失败：" + ex);
            }
        }

        // ── 坐标轴 ────────────────────────────────────────────────────

        /// <summary>一个横轴刻度：T 是 0~1 的横向位置，Text 是刻度文案。</summary>
        private struct Tick
        {
            public float T;
            public string Text;
        }

        /// <summary>
        /// 蜡烛图刻度：对齐每根蜡烛的中心，而不是槽位边界。
        ///
        /// 关键是「步长必须是整数」：先估一个步长再按它铺满，间距就一定均匀。
        /// 之前是先定刻度个数（固定 5 个）再按等分取索引，7 天档位算出来是
        /// 0,1.5,3,4.5,6，取整后成了 0,2,3,4,6，标签就是 -6 / -4 / -3 / -2 这种忽宽忽窄的样子。
        /// </summary>
        private static Tick[] SlotTicks(int n, string[] labels)
        {
            int span = Mathf.Max(n - 1, 0);
            int step = StepFor(span);
            int count = span / step + 1;
            if (count < 2) count = 2;
            Tick[] ticks = new Tick[count];
            for (int k = 0; k < count; k++)
            {
                int idx = Mathf.Min(k * step, span);
                ticks[k].T = n > 0 ? (idx + 0.5f) / n : 0.5f;
                ticks[k].Text = TickLabel(labels, idx);
            }
            return ticks;
        }

        /// <summary>
        /// 把跨度切成 3~5 段，挑「最接近整数」的那个步长。
        /// 只从 3 段起试：2 段永远能整除，会让长档位只剩首尾两个标签。
        /// </summary>
        private static int StepFor(int span)
        {
            if (span < 1) return 1;
            int best = span;
            float bestErr = float.MaxValue;
            for (int c = 5; c >= 3; c--)
            {
                float raw = (float)span / (c - 1);
                int s = Mathf.Max(1, Mathf.RoundToInt(raw));
                float err = Mathf.Abs(raw - s);
                if (err < bestErr - 0.0001f) { bestErr = err; best = s; }
            }
            return best;
        }

        private static string TickLabel(string[] labels, int idx)
        {
            return (labels != null && idx >= 0 && idx < labels.Length) ? labels[idx] : "";
        }

        private static void Axis(RectTransform host, float plotW, float plotH,
            double min, double max, Tick[] ticks, string unit)
        {
            // 横网格 + 纵轴刻度（3 个，右对齐）。单位只挂在最上面那一格上。
            double span = max - min;
            for (int k = 0; k < 3; k++)
            {
                float y = _padT + plotH * k / 2f;
                Block(host, "Grid" + k, _padL, y, plotW, 1f, Palette.Grid);
                double val = max - span * k / 2.0;
                string txt = Fmt(val, span);
                if (k == 0 && !string.IsNullOrEmpty(unit)) txt += unit;
                Text(host, "Y" + k, 0f, y - 8f, _padL - 6f, 18f, txt,
                    Palette.Muted, Ui.AlignRight);
            }

            // 纵网格 + 横轴刻度。刻度文案宽 76、居中，贴边的两个会被顶到画布外面
            // （最右边的「现在」正好压在面板边缘上），所以把中心夹在画布内。
            const float half = 38f;
            float hostW = plotW + _padL + _padR;
            for (int k = 0; k < ticks.Length; k++)
            {
                float x = _padL + plotW * ticks[k].T;
                if (k > 0 && k < ticks.Length - 1)
                {
                    Block(host, "VGrid" + k, x, _padT, 1f, plotH, Palette.Grid);
                }
                float cx = Mathf.Clamp(x, half, Mathf.Max(half, hostW - half));
                Text(host, "X" + k, cx - half, _padT + plotH + 2f, 76f, 18f, ticks[k].Text,
                    Palette.Muted, Ui.AlignCenter);
            }
        }

        private static void Empty(RectTransform host, float hostW, float hostH)
        {
            Text(host, "Empty", 0f, hostH * 0.5f - 12f, hostW, 24f,
                "暂无行情数据，过一个交易日再来看", Palette.Muted, Ui.AlignCenter);
        }

        // ── 图元 ──────────────────────────────────────────────────────

        /// <summary>
        /// 分时图的纵轴量程：可见价格 ∪ 昨收，再上下各留 10% 的边。
        /// 画图和悬停读数共用这一套 —— 两边各算一次的话，点迟早会偏出折线。
        /// </summary>
        public static void IntraRange(double[] path, double prevClose, out double min, out double max)
        {
            min = prevClose > 0.0 ? prevClose : (path != null && path.Length > 0 ? path[0] : 0.0);
            max = min;
            if (path != null)
            {
                for (int i = 0; i < path.Length; i++)
                {
                    if (path[i] < min) min = path[i];
                    if (path[i] > max) max = path[i];
                }
            }
            if (max - min < 0.0001) { min -= 0.5; max += 0.5; }
            else { double m = (max - min) * 0.10; min -= m; max += m; }
        }

        private static void Block(RectTransform host, string name, float x, float y, float w, float h, Color c)
        {
            Image img = Ui.MakeImage(host, "P" + name, c, false);
            if (img != null) Ui.Place(img.gameObject, x, y, w, h);
        }

        private static void Dot(RectTransform host, float x, float y, Color c)
        {
            Image img = Ui.MakeImage(host, "Dot", c, false);
            if (img != null) Ui.Place(img.gameObject, x - 2f, y - 2f, 4f, 4f);
        }

        /// <summary>两点之间的一段线：先算长度和角度，再用旋转的细长块表示。</summary>
        private static void Segment(RectTransform host, float x1, float y1, float x2, float y2, Color c)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 0.5f) return;
            float angle = Mathf.Atan2(-dy, dx) * Mathf.Rad2Deg;
            Image img = Ui.MakeImage(host, "Seg", c, false);
            if (img != null) Ui.PlaceRot(img.gameObject, (x1 + x2) * 0.5f, (y1 + y2) * 0.5f, len, 1.6f, angle);
        }

        private static void Text(RectTransform host, string name, float x, float y, float w, float h,
            string text, Color c, int align)
        {
            TextMeshProUGUI t = Ui.MakeText(host, "T" + name, text, _fs, c, align, false);
            if (t != null) Ui.Place(t.gameObject, x, y, w, h);
        }

        // ── 换算 ──────────────────────────────────────────────────────

        private static float Y(double v, double min, double max, float plotH)
        {
            double span = max - min;
            double t = span > 0.0000001 ? (v - min) / span : 0.5;
            return _padT + (float)((1.0 - t) * plotH);
        }

        private static void Pad(ref double min, ref double max)
        {
            if (max - min < 0.0001)
            {
                double mid = (max + min) * 0.5;
                double span = Math.Abs(mid) > 1.0 ? Math.Abs(mid) * 0.1 : 1.0;
                min = mid - span;
                max = mid + span;
                return;
            }
            double margin = (max - min) * 0.08;
            min -= margin;
            max += margin;
        }

        /// <summary>
        /// 纵轴刻度文案。小数位跟着量程走：MACD 这种量程 2 左右的指标，
        /// 固定一位小数会渲染成 0.1 / -0.9 / -2，看着像不等距，其实是四舍五入吃掉的。
        /// </summary>
        private static string Fmt(double v, double span)
        {
            double a = Math.Abs(v);
            if (a >= 10000.0) return (v / 10000.0).ToString("0.#") + "万";
            if (span >= 100.0) return v.ToString("0");
            if (span >= 10.0) return v.ToString("0.#");
            if (span >= 1.0) return v.ToString("0.##");
            if (span >= 0.1) return v.ToString("0.###");
            return v.ToString("0.####");
        }

        /// <summary>清掉宿主下所有旧图元。</summary>
        public static void Clear(RectTransform host)
        {
            try
            {
                if (host == null) return;
                for (int i = host.childCount - 1; i >= 0; i--)
                {
                    Transform child = host.GetChild(i);
                    if (child != null) UnityEngine.Object.Destroy(child.gameObject);
                }
            }
            catch (Exception ex)
            {
                Core.Debug("清理图表失败：" + ex.Message);
            }
        }
    }
}
