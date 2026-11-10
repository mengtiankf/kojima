using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using Kojima.Agents.Native;
using Kojima.Core.Ipc;
using Kojima.Core.Models;

namespace Kojima.Agents.Features;

/// <summary>系统监控：CPU/内存/GPU/网络/磁盘/温度/电源/进程。</summary>
public sealed class MonitorFeature : IAgentFeature
{
    private static string _tempSource = "auto";

    private readonly Sampler _sampler = new();
    private AgentPipeServer? _pipe;
    private CancellationTokenSource? _stream;
    private int _intervals;
    private DateTime _lastCpuAlert = DateTime.MinValue;

    public bool Busy => _stream is { IsCancellationRequested: false };

    public void Attach(AgentPipeServer pipe) => _pipe = pipe;

    public Task<object?> HandleAsync(string method, JsonElement? args) => method switch
    {
        "ping" => Task.FromResult<object?>(new { pong = true, feature = "monitor" }),
        "sample" => Task.FromResult<object?>(_sampler.Sample()),
        "subscribe" => Task.FromResult<object?>(StartStream(args)),
        "monitor.sources" => Task.FromResult<object?>(new { sources = HardwareSensors.Detect() }),
        "unsubscribe" => Task.FromResult<object?>(StopStream()),
        _ => Task.FromResult<object?>(new { error = "unknown method " + method }),
    };

    private object StartStream(JsonElement? args)
    {
        var interval = 1500;
        if (args is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("intervalMs", out var property)
            && property.TryGetInt32(out var parsed))
        {
            interval = Math.Clamp(parsed, 400, 10000);
        }

        if (args is { ValueKind: JsonValueKind.Object } element2
            && element2.TryGetProperty("tempSource", out var tempSource)
            && tempSource.ValueKind == JsonValueKind.String)
        {
            _tempSource = tempSource.GetString() ?? "auto";
        }

        StopStream();
        var cts = new CancellationTokenSource();
        _stream = cts;
        var id = Interlocked.Increment(ref _intervals);
        _ = Task.Run(() => StreamAsync(interval, id, cts.Token));
        return new { streaming = true, intervalMs = interval, id };
    }

    private object StopStream()
    {
        var cts = _stream;
        _stream = null;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // 忽略
            }
        }

        return new { streaming = false };
    }

    private async Task StreamAsync(int intervalMs, int id, CancellationToken token)
    {
        // 首帧立刻推一次，避免面板等一个周期
        await PushAsync(id, token).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await PushAsync(id, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            Core.Config.AppLog.Warn("monitor stream: " + ex.Message);
        }
    }

    private async Task PushAsync(int id, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        var pipe = _pipe;
        if (pipe is null)
        {
            return;
        }

        var sample = _sampler.Sample();
        await pipe.PushAsync("sample", new { id, sample }).ConfigureAwait(false);
        if (sample.Cpu.Percent >= 90 && DateTime.Now - _lastCpuAlert > TimeSpan.FromMinutes(10))
        {
            _lastCpuAlert = DateTime.Now;
            await pipe.PushAsync("notice", new { title = "CPU 占用过高", body = $"当前 {sample.Cpu.Percent:0}%，可打开监控页查看占用进程。", kind = "warn" }).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        StopStream();
        return ValueTask.CompletedTask;
    }

    /// <summary>无状态采样器：内部保存上一帧做差。</summary>
    private sealed class Sampler
    {
        private ulong _prevIdle;
        private ulong _prevKernel;
        private ulong _prevUser;
        private long _prevTick;
        private ulong _prevDown;
        private ulong _prevUp;
        private Dictionary<int, TimeSpan> _prevProc = new();
        private long _prevProcTick;
        private DateTime _gpuAt = DateTime.MinValue;
        private List<GpuReading> _gpuCache = [];
        private DateTime _tempAt = DateTime.MinValue;
        private double? _tempCache;
        private DateTime _clockAt = DateTime.MinValue;
        private double _clockCache;
        private DateTime _swapAt = DateTime.MinValue;
        private ulong _swapUsed;
        private ulong _swapTotal;
        

        public MonitorSample Sample()
        {
            var sample = new MonitorSample { Ts = DateTimeOffset.Now };
            var now = Stopwatch.GetTimestamp();
            var freq = (double)Stopwatch.Frequency;
            var dt = _prevTick == 0 ? 1.5 : (now - _prevTick) / freq;
            if (dt <= 0.05)
            {
                dt = 1.5;
            }

            var cores = Environment.ProcessorCount;
            sample.Cpu = ReadCpu(dt, cores);
            sample.Mem = ReadMemory();
            sample.Gpus = ReadGpu();
            sample.Net = ReadNetwork(dt);
            sample.Disks = ReadDisks();
            sample.Temp = ReadTemps(sample.Gpus);
            sample.Power = ReadPower(sample.Gpus);
            sample.Processes = ReadProcesses(dt, cores);
            sample.UptimeSeconds = MonitorNative.UptimeSeconds();

            var threads = ReadThreads();
            if (threads > 0)
            {
                sample.Cpu.Threads = threads;
            }

            _prevTick = now;
            return sample;
        }

        private CpuReading ReadCpu(double dt, int cores)
        {
            var reading = new CpuReading();
            try
            {
                if (MonitorNative.TryCpuTimes(out var idle, out var kernel, out var user)
                    && _prevIdle != 0
                    && idle >= _prevIdle
                    && kernel >= _prevKernel
                    && user >= _prevUser)
                {
                    var idleDelta = idle - _prevIdle;
                    var totalDelta = (kernel - _prevKernel) + (user - _prevUser);
                    if (totalDelta > 0)
                    {
                        reading.Percent = Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
                    }
                }
                else
                {
                    // 第一帧：用 WMI 的 _Total 兜底
                    _ = MonitorNative.PerCoreCpu(out var total);
                    reading.Percent = total;
                }

                _prevIdle = idle;
                _prevKernel = kernel;
                _prevUser = user;

                var perCore = MonitorNative.PerCoreCpu(out var wmiTotal);
                if (perCore.Length > 0)
                {
                    reading.PerCore = perCore;
                    if (reading.Percent <= 0.01)
                    {
                        reading.Percent = wmiTotal;
                    }
                }
                else
                {
                    reading.PerCore = Enumerable.Repeat(reading.Percent, cores).ToArray();
                }

                reading.Processes = Process.GetProcesses().Length;

                if (_clockAt == DateTime.MinValue || (DateTime.UtcNow - _clockAt).TotalSeconds > 20)
                {
                    _clockCache = MonitorNative.CpuClockMhz();
                    _clockAt = DateTime.UtcNow;
                }

                reading.FreqMhz = _clockCache;
            }
            catch (Exception ex)
            {
                Core.Config.AppLog.Warn("cpu: " + ex.Message);
            }

            return reading;
        }

        private int ReadThreads()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Threads FROM Win32_PerfFormattedData_PerfOS_System");
                foreach (System.Management.ManagementObject item in searcher.Get())
                {
                    var value = MonitorNative.ToDouble(item["Threads"]);
                    if (value > 0)
                    {
                        return (int)value;
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return 0;
        }

        private MemReading ReadMemory()
        {
            var reading = new MemReading();
            try
            {
                var (total, avail, _, _) = MonitorNative.Memory();
                if (total > 0)
                {
                    reading.Total = total;
                    reading.Used = total - avail;
                    reading.Percent = Math.Clamp((double)reading.Used / total * 100.0, 0, 100);
                }

                // 页面文件用量走 WMI，比用提交上限反推准确
                if (_swapAt == DateTime.MinValue || (DateTime.UtcNow - _swapAt).TotalSeconds > 15)
                {
                    (_swapUsed, _swapTotal) = MonitorNative.PageFile();
                    _swapAt = DateTime.UtcNow;
                }

                if (_swapTotal > 0)
                {
                    reading.SwapTotal = _swapTotal;
                    reading.SwapUsed = _swapUsed;
                    reading.SwapPercent = Math.Clamp((double)_swapUsed / _swapTotal * 100.0, 0, 100);
                }
            }
            catch (Exception ex)
            {
                Core.Config.AppLog.Warn("mem: " + ex.Message);
            }

            return reading;
        }

        private List<GpuReading> ReadGpu()
        {
            if ((DateTime.UtcNow - _gpuAt).TotalMilliseconds < 1200 && _gpuCache.Count > 0)
            {
                return _gpuCache;
            }

            try
            {
                var rows = MonitorNative.NvidiaGpus();
                if (rows.Count > 0)
                {
                    _gpuCache = rows.Select(row => new GpuReading
                    {
                        Name = row.Name,
                        Util = row.Util,
                        MemUsed = row.MemUsed,
                        MemTotal = row.MemTotal,
                        Temp = row.Temp,
                        Power = row.Power,
                    }).ToList();
                    _gpuAt = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Core.Config.AppLog.Warn("gpu: " + ex.Message);
            }

            return _gpuCache;
        }

        private NetReading ReadNetwork(double dt)
        {
            var reading = new NetReading();
            try
            {
                ulong down = 0;
                ulong up = 0;
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up
                        || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    try
                    {
                        var stats = nic.GetIPStatistics();
                        down += (ulong)stats.BytesReceived;
                        up += (ulong)stats.BytesSent;
                    }
                    catch
                    {
                        // 单个网卡失败忽略
                    }
                }

                reading.TotalDown = down;
                reading.TotalUp = up;
                if (_prevDown > 0 && down >= _prevDown && up >= _prevUp)
                {
                    reading.DownBps = (down - _prevDown) / dt;
                    reading.UpBps = (up - _prevUp) / dt;
                }

                _prevDown = down;
                _prevUp = up;
            }
            catch (Exception ex)
            {
                Core.Config.AppLog.Warn("net: " + ex.Message);
            }

            return reading;
        }

        private static List<DiskReading> ReadDisks()
        {
            var list = new List<DiskReading>();
            Dictionary<string, (double Read, double Write)> rates;
            try
            {
                rates = MonitorNative.DiskRates();
            }
            catch
            {
                rates = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    {
                        continue;
                    }

                    var name = drive.Name.TrimEnd('\\', ':');
                    var reading = new DiskReading
                    {
                        Name = name,
                        Mount = drive.RootDirectory.FullName,
                        Total = (ulong)Math.Max(0, drive.TotalSize),
                        Used = (ulong)Math.Max(0, drive.TotalSize - drive.TotalFreeSpace),
                    };

                    if (rates.TryGetValue(name, out var rate))
                    {
                        reading.ReadBps = rate.Read;
                        reading.WriteBps = rate.Write;
                    }

                    list.Add(reading);
                }
            }
            catch (Exception ex)
            {
                Core.Config.AppLog.Warn("disk: " + ex.Message);
            }

            return list;
        }

        private (double? Cpu, double? Gpu, double? Power, string? Source) _externalCache;
        private DateTime _externalAt = DateTime.MinValue;

        private TempReading ReadTemps(List<GpuReading> gpus)
        {
            var reading = new TempReading();
            try
            {
                // 先试用户选择的/自动检测的外部监控软件
                if (_tempSource != "internal")
                {
                    if (_externalAt == DateTime.MinValue || (DateTime.UtcNow - _externalAt).TotalSeconds > 5)
                    {
                        _externalCache = HardwareSensors.Read(_tempSource);
                        _externalAt = DateTime.UtcNow;
                    }

                    var (cpuExt, gpuExt, powerExt, source) = _externalCache;
                    if (source is not null)
                    {
                        reading.Cpu = cpuExt;
                        reading.Gpu = gpuExt ?? (gpus.Count > 0 ? gpus[0].Temp : null);
                        reading.Power = powerExt;
                        reading.Source = source;
                        return reading;
                    }
                }

                if (_tempAt == DateTime.MinValue || (DateTime.UtcNow - _tempAt).TotalSeconds > 30)
                {
                    _tempCache = MonitorNative.ThermalCelsius();
                    _tempAt = DateTime.UtcNow;
                }

                reading.Cpu = _tempCache;
                reading.Gpu = gpus.Count > 0 ? gpus[0].Temp : null;
                reading.Source = reading.Cpu is not null ? "acpi" : reading.Gpu is not null ? "nvidia" : null;
            }
            catch (Exception ex)
            {
                Core.Config.AppLog.Warn("temp: " + ex.Message);
            }

            return reading;
        }

        private static PowerReading ReadPower(List<GpuReading> gpus)
        {
            var reading = new PowerReading();
            try
            {
                var (percent, plugged, seconds) = MonitorNative.Power();
                reading.BatteryPercent = percent;
                reading.Plugged = plugged;
                reading.SecondsLeft = seconds;
                if (gpus.Count > 0 && gpus[0].Power is { } power)
                {
                    reading.DrainW = power;
                }
            }
            catch
            {
                // 忽略
            }

            return reading;
        }

        private bool _primed;

        private List<ProcReading> ReadProcesses(double dt, int cores)
        {
            var list = new List<ProcReading>();
            if (!_primed)
            {
                _primed = true;
                try
                {
                    foreach (var warm in Process.GetProcesses())
                    {
                        try
                        {
                            _prevProc[warm.Id] = warm.TotalProcessorTime;
                        }
                        catch
                        {
                            // 进程可能已退出
                        }
                        finally
                        {
                            warm.Dispose();
                        }
                    }

                    _prevProcTick = Stopwatch.GetTimestamp();
                }
                catch
                {
                    // 预热失败不影响后续采样
                }
            }

            var current = new Dictionary<int, TimeSpan>();
            var procTick = Stopwatch.GetTimestamp();
            var procDt = _prevProcTick == 0 ? dt : (procTick - _prevProcTick) / (double)Stopwatch.Frequency;
            if (procDt <= 0.05)
            {
                procDt = dt;
            }

            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        var pid = process.Id;
                        var cpuTime = process.TotalProcessorTime;
                        current[pid] = cpuTime;

                        var percent = 0.0;
                        if (_prevProc.TryGetValue(pid, out var previous) && cpuTime >= previous)
                        {
                            percent = (cpuTime - previous).TotalSeconds / procDt / cores * 100.0;
                        }

                        list.Add(new ProcReading
                        {
                            Name = string.IsNullOrWhiteSpace(process.ProcessName) ? "?" : process.ProcessName,
                            Pid = pid,
                            Cpu = Math.Clamp(percent, 0, 100 * cores),
                            Mem = (ulong)Math.Max(0, process.WorkingSet64),
                        });
                    }
                    catch
                    {
                        // 进程可能在枚举期间退出
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Config.AppLog.Warn("proc: " + ex.Message);
            }

            _prevProc = current;
            _prevProcTick = procTick;

            return list
                .OrderByDescending(p => p.Cpu)
                .ThenByDescending(p => p.Mem)
                .Take(12)
                .ToList();
        }
    }
}
