using System.Management;
using System.Text;
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
                UpdateHardwareTree(hardware);

            // Keep trying to resolve CPU temp sensor until we have a working source
            _resolveAttempts++;
            if (!_sensorsResolved || (!HasValidTemperature(_cpuTempSensor?.Value) && !_useWmiFallback))
                ResolveSensors();

            // Read cached sensors
            if (_useWmiFallback)
                CpuTemp = ReadWmiCpuTemp();
            else
                CpuTemp = _cpuTempSensor?.Value;

            if (!HasValidTemperature(CpuTemp))
                CpuTemp = null;

            GpuTemp = _gpuTempSensor?.Value;
            CpuLoad = _cpuLoadSensor?.Value ?? CpuLoad;
            GpuLoad = _gpuLoadSensor?.Value ?? GpuLoad;

            // If CPU temp is still invalid after a couple of cycles, try WMI immediately
            if (!_useWmiFallback && !HasValidTemperature(CpuTemp) && _resolveAttempts >= 2)
            {
                var fallbackTemp = ReadFallbackCpuTemp();
                if (HasValidTemperature(fallbackTemp))
                {
                    _useWmiFallback = true;
                    CpuTempSource = "Windows Thermal Zone";
                    CpuTemp = fallbackTemp;
                }
            }
        }
        catch { /* sensor read failure – keep last values */ }
    }

    private void ResolveSensors()
    {
        // ── CPU sensors ──
        // 1) Try CPU package/cores sensors first (Intel + AMD Ryzen)
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType == HardwareType.Cpu)
            {
                _cpuTempSensor = FindBestCpuTemperatureSensor(hardware, _cpuTempSensor);
                _cpuLoadSensor ??= FindBestSensor(hardware, SensorType.Load,
                    "Total", "CPU Total");
            }
        }

        // 2) If CPU temp is still invalid, search motherboard/superIO/EC hierarchy
        //    Laptop ECs often report "CPU" or "CPU Core" temperature
        if (!HasValidTemperature(_cpuTempSensor?.Value))
        {
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Motherboard)
                {
                    // Motherboard temps are usually on sub-hardware (SuperIO/EC chips)
                    var mbSensor = FindBestSensor(hardware, SensorType.Temperature,
                        "CPU", "CPU Core", "CPU Package", "CPU Temp", "Processor",
                        "System", "Temperature #1", "CPUTIN", "PECI");
                    if (mbSensor != null && HasValidTemperature(mbSensor.Value))
                    {
                        _cpuTempSensor = mbSensor;
                        CpuTempSource = $"Motherboard ({mbSensor.Name})";
                        break;
                    }
                }
            }
        }
        else
        {
            CpuTempSource = $"CPU ({_cpuTempSensor!.Name})";
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

        _sensorsResolved = HasValidTemperature(_cpuTempSensor?.Value)
                || _useWmiFallback
                        || _gpuTempSensor != null;
    }

    /// <summary>
    /// WMI fallback: reads CPU temperature from the ACPI thermal zone.
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

    private static float? ReadFallbackCpuTemp()
    {
        var wmiAcpi = ReadWmiCpuTemp();
        if (HasValidTemperature(wmiAcpi)) return wmiAcpi;

        var perfFormatted = ReadPerfThermalZoneFormatted();
        if (HasValidTemperature(perfFormatted)) return perfFormatted;

        var perfRaw = ReadPerfThermalZoneRaw();
        if (HasValidTemperature(perfRaw)) return perfRaw;

        return null;
    }

    private static float? ReadPerfThermalZoneFormatted()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");

            float maxTemp = 0;
            foreach (var obj in searcher.Get())
            {
                var temp = TryReadCelsius(obj, "HighPrecisionTemperature")
                        ?? TryReadCelsius(obj, "Temperature");
                if (temp is > 0 and < 150)
                    maxTemp = Math.Max(maxTemp, temp.Value);
            }

            return maxTemp > 0 ? maxTemp : null;
        }
        catch
        {
            return null;
        }
    }

    private static float? ReadPerfThermalZoneRaw()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfRawData_Counters_ThermalZoneInformation");

            float maxTemp = 0;
            foreach (var obj in searcher.Get())
            {
                var temp = TryReadCelsius(obj, "HighPrecisionTemperature")
                        ?? TryReadCelsius(obj, "Temperature");
                if (temp is > 0 and < 150)
                    maxTemp = Math.Max(maxTemp, temp.Value);
            }

            return maxTemp > 0 ? maxTemp : null;
        }
        catch
        {
            return null;
        }
    }

    private static float? TryReadCelsius(ManagementBaseObject obj, string propertyName)
    {
        try
        {
            if (obj[propertyName] == null) return null;

            var raw = Convert.ToSingle(obj[propertyName]);
            if (raw <= 0) return null;

            // Common encodings from thermal zone providers:
            //  - tenths of Kelvin (e.g. 3002 => 27.05C)
            //  - Kelvin (e.g. 300 => 26.85C)
            //  - Celsius already (e.g. 52)
            if (raw > 1000f) return (raw / 10f) - 273.15f;
            if (raw > 200f) return raw - 273.15f;
            if (raw is > 0 and < 150) return raw;

            return null;
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

        static bool IsValidValue(ISensor sensor)
        {
            if (!sensor.Value.HasValue) return false;
            if (sensor.SensorType == SensorType.Temperature)
                return HasValidTemperature(sensor.Value);
            return sensor.Value.Value > 0;
        }

        // 1) Try preferred names in order, preferring sensors with a value
        foreach (var name in preferredNames)
        {
            ISensor? withValue = null;

            foreach (var s in allSensors)
            {
                if (!s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsValidValue(s))
                    withValue ??= s;
            }

            if (withValue != null) return withValue;
        }

        // 2) Any sensor of this type with a non-null value > 0
        foreach (var s in allSensors)
            if (IsValidValue(s)) return s;

        // 3) For non-temperature sensors, return first matching sensor as fallback
        if (type != SensorType.Temperature)
            return allSensors.FirstOrDefault();

        // Temperature: no valid reading found
        return null;
    }

    private static ISensor? FindBestCpuTemperatureSensor(IHardware cpuHardware, ISensor? current)
    {
        if (current != null && HasValidTemperature(current.Value))
            return current;

        var preferred = FindBestSensor(cpuHardware, SensorType.Temperature,
            "Package", "CPU Package", "Tctl", "Tdie", "CCD", "Core Max", "Core Average",
            "P-Core", "E-Core", "Core");
        if (preferred != null)
            return preferred;

        var anyValidCpuTemp = GetAllSensors(cpuHardware, SensorType.Temperature)
            .FirstOrDefault(s => HasValidTemperature(s.Value));
        return anyValidCpuTemp;
    }

    private static void UpdateHardwareTree(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
            UpdateHardwareTree(sub);
    }

    private static void AppendHardwareTree(StringBuilder sb, IHardware hardware, string indent)
    {
        hardware.Update();
        sb.AppendLine($"{indent}Hardware: {hardware.Name} [{hardware.HardwareType}]");
        foreach (var s in hardware.Sensors)
            sb.AppendLine($"{indent}  Sensor: {s.Name} [{s.SensorType}] = {s.Value}");

        foreach (var sub in hardware.SubHardware)
            AppendHardwareTree(sb, sub, indent + "  ");
    }

    private static bool HasValidTemperature(float? value)
    {
        return value is > 0 and < 150;
    }

    private static List<ISensor> GetAllSensors(IHardware hw, SensorType type)
    {
        var list = new List<ISensor>();
        CollectSensorsRecursive(hw, type, list);
        return list;
    }

    private static void CollectSensorsRecursive(IHardware hw, SensorType type, List<ISensor> list)
    {
        foreach (var s in hw.Sensors)
            if (s.SensorType == type) list.Add(s);

        foreach (var sub in hw.SubHardware)
            CollectSensorsRecursive(sub, type, list);
    }

    /// <summary>
    /// Writes all detected hardware and sensors to a diagnostic log file.
    /// </summary>
    public string DumpSensors()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== NecroMonitor Sensor Dump ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
        sb.AppendLine($"IsAvailable: {IsAvailable}");
        sb.AppendLine($"Resolved: {_sensorsResolved}, Attempts: {_resolveAttempts}");
        sb.AppendLine($"CPU Temp Source: {CpuTempSource}");
        sb.AppendLine($"CPU Sensor: {_cpuTempSensor?.Name ?? "(none)"} = {_cpuTempSensor?.Value}");
        sb.AppendLine($"GPU Sensor: {_gpuTempSensor?.Name ?? "(none)"} = {_gpuTempSensor?.Value}");
        sb.AppendLine($"WMI Fallback: {_useWmiFallback}");

        // Try thermal zone fallbacks for diagnostics
        var wmiTemp = ReadWmiCpuTemp();
        var perfFormatted = ReadPerfThermalZoneFormatted();
        var perfRaw = ReadPerfThermalZoneRaw();
        var fallback = ReadFallbackCpuTemp();
        sb.AppendLine($"WMI Temp (ACPI): {wmiTemp}°C");
        sb.AppendLine($"Perf Temp (Formatted): {perfFormatted}°C");
        sb.AppendLine($"Perf Temp (Raw): {perfRaw}°C");
        sb.AppendLine($"Fallback Temp: {fallback}°C");
        sb.AppendLine();

        foreach (var hw in _computer.Hardware)
            AppendHardwareTree(sb, hw, string.Empty);

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
