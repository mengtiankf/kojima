using System;
using Kojima.Core.Config;
using NAudio.Dsp;
using NAudio.Wave;

namespace Kojima.Shell.Services;

/// <summary>
/// 系统输出音频的实时频谱：WASAPI 环回采集默认播放设备，FFT 后归到 <see cref="Bands"/> 个对数频段。
/// 采集失败时保持 0，不抛异常。
/// </summary>
public sealed class SpectrumService : IDisposable
{
    public const int Bands = 28;

    private const int FftSize = 1024;
    private const int FftPower = 10; // 2^10 == 1024

    private readonly object _gate = new();
    private readonly double[] _levels = new double[Bands];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _samples = new float[FftSize];
    private readonly int[] _bandStart = new int[Bands];
    private readonly int[] _bandEnd = new int[Bands];
    private WasapiLoopbackCapture? _capture;
    private int _fill;
    private int _rate = 48000;
    private bool _started;

    public SpectrumService()
    {
        BuildBandMap();
    }

    /// <summary>0..1 的频段能量，低频在前。</summary>
    public double[] Levels
    {
        get
        {
            var copy = new double[Bands];
            lock (_gate)
            {
                Array.Copy(_levels, copy, Bands);
            }

            return copy;
        }
    }

    public bool Running => _capture is not null;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        try
        {
            _capture = new WasapiLoopbackCapture();
            _rate = _capture.WaveFormat.SampleRate;
            BuildBandMap();
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, _) => { };
            _capture.StartRecording();
            _started = true;
            AppLog.Info($"频谱采集已启动：{_rate} Hz / {_capture.WaveFormat.Channels} 声道");
        }
        catch (Exception ex)
        {
            AppLog.Warn("频谱采集启动失败：" + ex.Message);
            Cleanup();
        }
    }

    public void Stop()
    {
        try
        {
            _capture?.StopRecording();
        }
        catch (Exception ex)
        {
            AppLog.Warn("频谱采集停止失败：" + ex.Message);
        }

        Cleanup();
    }

    public void Dispose() => Stop();

    private void Cleanup()
    {
        if (_capture is not null)
        {
            try
            {
                _capture.DataAvailable -= OnData;
                _capture.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        _capture = null;
        _started = false;
        lock (_gate)
        {
            Array.Clear(_levels);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            var format = _capture?.WaveFormat;
            if (format is null || e.BytesRecorded <= 0)
            {
                return;
            }

            var channels = Math.Max(1, format.Channels);
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
            var step = isFloat ? 4 : 2;
            var frameSize = step * channels;
            var frames = e.BytesRecorded / frameSize;

            for (var i = 0; i < frames; i++)
            {
                var offset = i * frameSize;
                float value;
                if (isFloat)
                {
                    value = BitConverter.ToSingle(e.Buffer, offset);
                }
                else
                {
                    var sample = BitConverter.ToInt16(e.Buffer, offset);
                    value = sample / 32768f;
                }

                _samples[_fill++] = value;
                if (_fill < FftSize)
                {
                    continue;
                }

                _fill = 0;
                Analyse();
            }
        }
        catch
        {
            // 采集线程里的异常一律吞掉，避免拖垮宿主
        }
    }

    private void Analyse()
    {
        for (var i = 0; i < FftSize; i++)
        {
            var window = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1))));
            _fft[i].X = _samples[i] * window;
            _fft[i].Y = 0;
        }

        FastFourierTransform.FFT(false, FftPower, _fft);

        lock (_gate)
        {
            for (var band = 0; band < Bands; band++)
            {
                var start = _bandStart[band];
                var end = Math.Max(start + 1, _bandEnd[band]);
                var sum = 0.0;
                var count = 0;
                for (var bin = start; bin < end && bin < FftSize / 2; bin++)
                {
                    var re = _fft[bin].X;
                    var im = _fft[bin].Y;
                    sum += Math.Sqrt(re * re + im * im);
                    count++;
                }

                // 频段平均幅度 → dB 映射（-70 dB 为底），再快升慢降
                var mean = count > 0 ? sum / count / (FftSize / 4.0) : 0;
                var db = 20.0 * Math.Log10(mean + 1e-7);
                var level = Math.Clamp((db + 70.0) / 70.0, 0, 1);
                var previous = _levels[band];
                _levels[band] = level > previous ? level : previous * 0.82 + level * 0.18;
            }
        }
    }

    private void BuildBandMap()
    {
        var nyquist = _rate / 2.0;
        const double low = 60;
        var high = Math.Min(nyquist * 0.85, 14000);
        var logLow = Math.Log(low);
        var logHigh = Math.Log(high);

        for (var band = 0; band < Bands; band++)
        {
            var f0 = Math.Exp(logLow + (logHigh - logLow) * band / Bands);
            var f1 = Math.Exp(logLow + (logHigh - logLow) * (band + 1) / Bands);
            _bandStart[band] = Math.Clamp((int)(f0 / nyquist * FftSize / 2), 1, FftSize / 2 - 1);
            _bandEnd[band] = Math.Clamp((int)(f1 / nyquist * FftSize / 2), _bandStart[band] + 1, FftSize / 2 - 1);
        }
    }
}
