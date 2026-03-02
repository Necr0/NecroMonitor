using System.Management;
using LibreHardwareMonitor.Hardware;

namespace NecroMonitor;

public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;

    public float? CpuTemp { get; private set; }
    public float? GpuTemp { get; private set; }
    public float CpuLoad { get; private set; }
    public float GpuLoad { get; private set; }
    public string CpuName { get; private set; } = "CPU";
    public string GpuName { get; private set; } = "GPU";
    public bool IsAvailable { get; private set; }
    public string CpuTempSource { get; private set; } = "none";

    // Cached sensor references (found once, reused every update)
    private ISensor? _cpuTempSensor;
    private ISensor? _gpuTempSensor;
    private ISensor? _cpuLoadSensor;
    private ISensor? _gpuLoadSensor;
    private bool _sensorsResolved;
    private int _resolveAttempts;

    // WMI fallback flag
    private bool _useWmiFallback;

    public HardwareMonitor()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true  // EC/SIO chips often have CPU temp on laptops
            };
            _computer.Open();
            IsAvailable = true;

            // Grab hardware names on first pass
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu)
                    CpuName = ShortenName(hw.Name);

                if (hw.HardwareType is HardwareType.GpuNvidia
                    or HardwareType.GpuAmd
                    or HardwareType.GpuIntel)
                    GpuName = ShortenName(hw.Name);
            }
        }
        catch
        {
            _computer = new Computer();
            IsAvailable = false;
        }
    }

    public void Update()
    {
        if (!IsAvailable) return;

        try
        {
            // Always update all hardware first
            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                foreach (var sub in hardware.SubHardware)
                    sub.Update();
            }

            // Keep trying to resolve CPU temp sensor until we have a working source
            _resolveAttempts++;
            if (!_sensorsResolved || (_cpuTempSensor?.Value == null && !_useWmiFallback))
                ResolveSensors();

            // Read cached sensors
            if (_useWmiFallback)
                CpuTemp = ReadWmiCpuTemp();
            else
                CpuTemp = _cpuTempSensor?.Value;

            GpuTemp = _gpuTempSensor?.Value;
            CpuLoad = _cpuLoadSensor?.Value ?? CpuLoad;
            GpuLoad = _gpuLoadSensor?.Value ?? GpuLoad;

            // If CPU temp is still null after a couple of cycles, try WMI immediately
            if (!_useWmiFallback && CpuTemp == null && _resolveAttempts >= 2)
            {
                var wmiTemp = ReadWmiCpuTemp();
                if (wmiTemp != null)
                {
                    _useWmiFallback = true;
                    CpuTempSource = "WMI (ACPI Thermal Zone)";
                    CpuTemp = wmiTemp;
                }
            }
        }
        catch { /* sensor read failure – keep last values */ }
    }

    private void ResolveSensors()
    {
        // ── CPU sensors ──
        // 1) Try the CPU hardware directly
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType == HardwareType.Cpu)
            {
                _cpuTempSensor ??= FindBestSensor(hardware, SensorType.Temperature,
                    "Package", "Tctl", "Tdie", "CCD", "Core Max", "Core Average",
                    "P-Core", "E-Core", "Core");
                _cpuLoadSensor ??= FindBestSensor(hardware, SensorType.Load,
                    "Total", "CPU Total");
            }
        }

        // 2) If CPU temp sensor is null or has no value, search motherboard/superIO
        //    Laptop ECs often report "CPU" or "CPU Core" temperature
        if (_cpuTempSensor?.Value == null)
        {
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Motherboard)
                {
                    // Motherboard temps are usually on sub-hardware (SuperIO/EC chips)
                    var mbSensor = FindBestSensor(hardware, SensorType.Temperature,
                        "CPU", "CPU Core", "CPU Package", "CPU Temp", "Processor",
                        "System", "Temperature #1", "CPUTIN", "PECI");
                    if (mbSensor != null)
                    {
                        _cpuTempSensor = mbSensor;
                        CpuTempSource = $"Motherboard ({mbSensor.Name})";
                    }
                }
            }
        }
        else
        {
            CpuTempSource = $"CPU ({_cpuTempSensor.Name})";
        }

        // ── GPU sensors ──
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType is HardwareType.GpuNvidia
                or HardwareType.GpuAmd
                or HardwareType.GpuIntel)
            {
                _gpuTempSensor ??= FindBestSensor(hardware, SensorType.Temperature,
                    "Core", "GPU Core", "GPU");
                _gpuLoadSensor ??= FindBestSensor(hardware, SensorType.Load,
                    "Core", "GPU Core", "GPU");
            }
        }

        _sensorsResolved = (_cpuTempSensor?.Value != null || _useWmiFallback)
                        || _gpuTempSensor != null;
    }

    /// <summary>
    /// WMI fallback: reads CPU temperature from the ACPI thermal zone.
    /// Works on most systems even when LibreHardwareMonitor's MSR approach fails.
    /// Returns Celsius, or null if not available.
    /// </summary>
    private static float? ReadWmiCpuTemp()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            float maxTemp = 0;
            foreach (var obj in searcher.Get())
            {
                // WMI returns temperature in tenths of Kelvin
                var raw = Convert.ToSingle(obj["CurrentTemperature"]);
                float celsius = (raw / 10f) - 273.15f;
                if (celsius > maxTemp && celsius < 150) // sanity check
                    maxTemp = celsius;
            }

            return maxTemp > 0 ? maxTemp : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the best sensor by trying preferred name patterns first,
    /// then falling back to any sensor of that type with a non-null value,
    /// and finally any sensor of that type at all.
    /// Searches both the hardware and all its sub-hardware.
    /// </summary>
    private static ISensor? FindBestSensor(IHardware hw, SensorType type, params string[] preferredNames)
    {
        var allSensors = GetAllSensors(hw, type);

        // 1) Try preferred names in order, preferring sensors with a value
        foreach (var name in preferredNames)
        {
            ISensor? withValue = null;
            ISensor? withoutValue = null;

            foreach (var s in allSensors)
            {
                if (!s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.Value.HasValue && s.Value.Value > 0)
                    withValue ??= s;
                else
                    withoutValue ??= s;
            }

            if (withValue != null) return withValue;
            if (withoutValue != null) return withoutValue;
        }

        // 2) Any sensor of this type with a non-null value > 0
        foreach (var s in allSensors)
            if (s.Value is > 0) return s;

        // 3) Any sensor of this type at all (value may come on next update)
        return allSensors.FirstOrDefault();
    }

    private static List<ISensor> GetAllSensors(IHardware hw, SensorType type)
    {
        var list = new List<ISensor>();
        foreach (var s in hw.Sensors)
            if (s.SensorType == type) list.Add(s);
        foreach (var sub in hw.SubHardware)
            foreach (var s in sub.Sensors)
                if (s.SensorType == type) list.Add(s);
        return list;
    }

    /// <summary>
    /// Writes all detected hardware and sensors to a diagnostic log file.
    /// </summary>
    public string DumpSensors()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== NecroMonitor Sensor Dump ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
        sb.AppendLine($"IsAvailable: {IsAvailable}");
        sb.AppendLine($"Resolved: {_sensorsResolved}, Attempts: {_resolveAttempts}");
        sb.AppendLine($"CPU Temp Source: {CpuTempSource}");
        sb.AppendLine($"CPU Sensor: {_cpuTempSensor?.Name ?? "(none)"} = {_cpuTempSensor?.Value}");
        sb.AppendLine($"GPU Sensor: {_gpuTempSensor?.Name ?? "(none)"} = {_gpuTempSensor?.Value}");
        sb.AppendLine($"WMI Fallback: {_useWmiFallback}");

        // Try WMI read for diagnostics
        var wmiTemp = ReadWmiCpuTemp();
        sb.AppendLine($"WMI Temp (ACPI): {wmiTemp}°C");
        sb.AppendLine();

        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            sb.AppendLine($"Hardware: {hw.Name} [{hw.HardwareType}]");
            foreach (var s in hw.Sensors)
                sb.AppendLine($"  Sensor: {s.Name} [{s.SensorType}] = {s.Value}");
            foreach (var sub in hw.SubHardware)
            {
                sub.Update();
                sb.AppendLine($"  SubHardware: {sub.Name} [{sub.HardwareType}]");
                foreach (var s in sub.Sensors)
                    sb.AppendLine($"    Sensor: {s.Name} [{s.SensorType}] = {s.Value}");
            }
        }

        return sb.ToString();
    }

    private static string ShortenName(string name)
    {
        return name
            .Replace("NVIDIA ", "")
            .Replace("AMD ", "")
            .Replace("Intel ", "")
            .Replace("(R)", "")
            .Replace("(TM)", "")
            .Replace("  ", " ")
            .Trim();
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { }
    }
}
