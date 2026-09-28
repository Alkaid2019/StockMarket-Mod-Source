using System;
using System.Collections.Generic;

namespace StockMarket
{
    /// <summary>一根 K 线：开高低收 + 成交量。</summary>
    internal struct Bar
    {
        public double Open, High, Low, Close;
        public double Volume;
    }

    /// <summary>
    /// 技术指标：纯数学，不碰 Unity，输入是「元」为单位的收盘价序列。
    ///
    /// 有个前提得说清楚：这套行情模型里本来**没有**成交量和盘中最高/最低，
    /// 存档里只存了每日收盘价。所以这两个量是从收盘价**确定性推导**出来的——
    /// 用「标的 Id + 第几天」当种子，保证每次进游戏算出来都一样，
    /// 也保证不用改存档格式、不用加新字段。推导规则是「涨跌越猛 → 当天振幅越大、
    /// 成交越活跃」，所以量价配合看起来是合理的，OBV、量价背离这些也才有意义。
    ///
    /// 所有指标返回的数组和输入等长，未完成预热的位置是 double.NaN，
    /// 画图时跳过 NaN 就不会把均线画到第一根 K 线上。
    /// </summary>
    internal static class StockIndicators
    {
        // ── K 线合成 ──────────────────────────────────────────────────

        /// <summary>把收盘价（分）序列加工成带高低价和成交量的 K 线。</summary>
        public static List<Bar> BuildBars(string id, List<long> cents)
        {
            List<Bar> bars = new List<Bar>();
            if (cents == null || cents.Count == 0) return bars;

            uint seed = StockState.Hash32(id + "|bar");
            for (int i = 0; i < cents.Count; i++)
            {
                double close = StockState.ToYuan(cents[i]);
                double open = i == 0 ? close : StockState.ToYuan(cents[i - 1]);
                double move = open > 0.0001 ? Math.Abs(close - open) / open : 0.0;

                // 影线：涨跌越猛影线越长，再叠一层确定性噪声，免得所有 K 线一个模样
                double wick = (0.15 + move * 1.4) * (0.30 + StockState.NextUnit(ref seed));
                double high = Math.Max(open, close) * (1.0 + wick * 0.5);
                double low = Math.Min(open, close) * (1.0 - wick * 0.5);
                if (low < 0.01) low = 0.01;

                bars.Add(new Bar { Open = open, High = high, Low = low, Close = close });
            }

            // 成交量要等高低价都算完才好算（它依赖当日振幅），所以单独走一遍。
            // 单价越低的标的「股数」越大，量级看着才像那么回事。
            StockDef def = StockDefs.Get(id);
            double unit = 20000.0 / Math.Max(1.0, def != null ? def.BasePrice : 10.0);
            uint vseed = StockState.Hash32(id + "|vol");
            for (int i = 0; i < bars.Count; i++)
            {
                Bar b = bars[i];
                double amp = b.Close > 0.0001 ? (b.High - b.Low) / b.Close : 0.05;
                double noise = 0.55 + StockState.NextUnit(ref vseed) * 0.90;
                b.Volume = Math.Round(unit * (0.40 + amp * 9.0) * noise);
                bars[i] = b;
            }
            return bars;
        }

        /// <summary>分（long）序列 → 元（double）序列。</summary>
        public static List<double> ToYuan(List<long> cents)
        {
            List<double> r = new List<double>();
            if (cents == null) return r;
            for (int i = 0; i < cents.Count; i++) r.Add(StockState.ToYuan(cents[i]));
            return r;
        }

        public static List<double> Closes(List<Bar> bars)
        {
            List<double> r = new List<double>();
            for (int i = 0; i < bars.Count; i++) r.Add(bars[i].Close);
            return r;
        }

        public static List<double> Volumes(List<Bar> bars)
        {
            List<double> r = new List<double>();
            for (int i = 0; i < bars.Count; i++) r.Add(bars[i].Volume);
            return r;
        }

        // ── 均线 ──────────────────────────────────────────────────────

        public static double[] MA(List<double> v, int n)
        {
            double[] r = Fill(v.Count);
            if (n <= 0) return r;
            double sum = 0;
            for (int i = 0; i < v.Count; i++)
            {
                sum += v[i];
                if (i >= n) sum -= v[i - n];
                if (i >= n - 1) r[i] = sum / n;
            }
            return r;
        }

        public static double[] EMA(List<double> v, int n)
        {
            double[] r = Fill(v.Count);
            if (v.Count == 0 || n <= 0) return r;
            double k = 2.0 / (n + 1.0);
            double e = v[0];
            r[0] = e;
            for (int i = 1; i < v.Count; i++)
            {
                e = v[i] * k + e * (1.0 - k);
                r[i] = e;
            }
            return r;
        }

        // ── 布林带 ────────────────────────────────────────────────────

        public static void Boll(List<double> v, int n, double k,
            out double[] mid, out double[] up, out double[] low)
        {
            mid = MA(v, n);
            up = Fill(v.Count);
            low = Fill(v.Count);
            if (n <= 0) return;
            for (int i = 0; i < v.Count; i++)
            {
                if (double.IsNaN(mid[i])) continue;
                double sum = 0;
                for (int j = i - n + 1; j <= i; j++)
                {
                    double d = v[j] - mid[i];
                    sum += d * d;
                }
                double sd = Math.Sqrt(sum / n);
                up[i] = mid[i] + k * sd;
                low[i] = mid[i] - k * sd;
            }
        }

        // ── MACD ──────────────────────────────────────────────────────

        public static void Macd(List<double> v, int fast, int slow, int signal,
            out double[] dif, out double[] dea, out double[] hist)
        {
            double[] ef = EMA(v, fast);
            double[] es = EMA(v, slow);
            dif = Fill(v.Count);
            for (int i = 0; i < v.Count; i++) dif[i] = ef[i] - es[i];
            dea = EMA(new List<double>(dif), signal);
            hist = Fill(v.Count);
            for (int i = 0; i < v.Count; i++) hist[i] = (dif[i] - dea[i]) * 2.0;
        }

        // ── KDJ ───────────────────────────────────────────────────────

        public static void Kdj(List<Bar> bars, int n,
            out double[] kk, out double[] dd, out double[] jj)
        {
            int c = bars.Count;
            kk = Fill(c);
            dd = Fill(c);
            jj = Fill(c);
            if (n <= 0) return;
            double k = 50.0, d = 50.0;
            for (int i = 0; i < c; i++)
            {
                int from = Math.Max(0, i - n + 1);
                double hh = double.MinValue, ll = double.MaxValue;
                for (int j = from; j <= i; j++)
                {
                    if (bars[j].High > hh) hh = bars[j].High;
                    if (bars[j].Low < ll) ll = bars[j].Low;
                }
                double rsv = hh - ll > 0.0001 ? (bars[i].Close - ll) / (hh - ll) * 100.0 : 50.0;
                k = (2.0 * k + rsv) / 3.0;
                d = (2.0 * d + k) / 3.0;
                kk[i] = k;
                dd[i] = d;
                jj[i] = 3.0 * k - 2.0 * d;
            }
        }

        // ── RSI ───────────────────────────────────────────────────────

        public static double[] Rsi(List<double> v, int n)
        {
            double[] r = Fill(v.Count);
            if (v.Count < 2 || n <= 0) return r;
            double gain = 0, loss = 0;
            for (int i = 1; i < v.Count; i++)
            {
                double ch = v[i] - v[i - 1];
                double up = ch > 0 ? ch : 0.0;
                double dn = ch < 0 ? -ch : 0.0;
                if (i <= n)
                {
                    gain += up;
                    loss += dn;
                }
                else
                {
                    gain = (gain * (n - 1) + up) / n;
                    loss = (loss * (n - 1) + dn) / n;
                }
                if (i >= n)
                {
                    r[i] = gain + loss > 1e-7 ? gain / (gain + loss) * 100.0 : 50.0;
                }
            }
            return r;
        }

        // ── OBV（能量潮）──────────────────────────────────────────────

        public static double[] Obv(List<double> closes, List<double> volumes)
        {
            double[] r = Fill(closes.Count);
            int c = Math.Min(closes.Count, volumes.Count);
            if (c == 0) return r;
            double o = 0;
            r[0] = 0;
            for (int i = 1; i < c; i++)
            {
                if (closes[i] > closes[i - 1]) o += volumes[i];
                else if (closes[i] < closes[i - 1]) o -= volumes[i];
                r[i] = o;
            }
            return r;
        }

        // ── CCI（顺势指标）────────────────────────────────────────────

        public static double[] Cci(List<Bar> bars, int n)
        {
            double[] r = Fill(bars.Count);
            if (n <= 0) return r;
            for (int i = 0; i < bars.Count; i++)
            {
                int from = Math.Max(0, i - n + 1);
                int cnt = i - from + 1;
                if (cnt < n) continue;
                double sum = 0;
                for (int j = from; j <= i; j++) sum += Tp(bars[j]);
                double ma = sum / cnt;
                double dev = 0;
                for (int j = from; j <= i; j++) dev += Math.Abs(Tp(bars[j]) - ma);
                dev /= cnt;
                r[i] = dev > 1e-7 ? (Tp(bars[i]) - ma) / (0.015 * dev) : 0.0;
            }
            return r;
        }

        private static double Tp(Bar b) { return (b.High + b.Low + b.Close) / 3.0; }

        // ── 工具 ──────────────────────────────────────────────────────

        private static double[] Fill(int n)
        {
            double[] r = new double[n];
            for (int i = 0; i < n; i++) r[i] = double.NaN;
            return r;
        }

        /// <summary>最后一个有效值；整条都是 NaN 就返回 NaN。</summary>
        public static double Latest(double[] a)
        {
            if (a == null) return double.NaN;
            for (int i = a.Length - 1; i >= 0; i--)
            {
                if (!double.IsNaN(a[i])) return a[i];
            }
            return double.NaN;
        }

        public static bool Has(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        /// <summary>序列里的最小/最大值，忽略 NaN。</summary>
        public static void Range(double[] a, out double min, out double max)
        {
            min = double.MaxValue;
            max = double.MinValue;
            if (a == null) return;
            for (int i = 0; i < a.Length; i++)
            {
                if (!Has(a[i])) continue;
                if (a[i] < min) min = a[i];
                if (a[i] > max) max = a[i];
            }
        }

        public static void RangeAll(double[][] arrays, out double min, out double max)
        {
            min = double.MaxValue;
            max = double.MinValue;
            if (arrays == null) return;
            for (int k = 0; k < arrays.Length; k++)
            {
                double lo, hi;
                Range(arrays[k], out lo, out hi);
                if (lo < min) min = lo;
                if (hi > max) max = hi;
            }
        }
    }
}
