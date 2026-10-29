using Microsoft.Win32;

namespace WinUI3Shell.Services.Mc;

/// <summary>读取本机硬件信息：CPU、显卡（注册表）与物理内存。</summary>
public static class Hardware
{
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string GpuClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>CPU 名称（ProcessorNameString）。</summary>
    public static string CpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CpuKey);
            string? name = key?.GetValue("ProcessorNameString") as string;
            return string.IsNullOrWhiteSpace(name) ? "未知" : name.Trim();
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>显卡名称：显卡设备子键 0000、0001… 里的 DriverDesc，取第一个非空的。</summary>
    public static string GpuName()
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(GpuClassKey);
            if (classKey == null)
            {
                return "未知";
            }
            foreach (string sub in classKey.GetSubKeyNames())
            {
                // 显卡实例形如 0000 / 0001；其它子键（Properties、Configuration）跳过
                if (!int.TryParse(sub, out _))
                {
                    continue;
                }
                using var deviceKey = classKey.OpenSubKey(sub);
                string? desc = deviceKey?.GetValue("DriverDesc") as string;
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    return desc.Trim();
                }
            }
            return "未知";
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>物理内存 MB：(总量, 可用)。</summary>
    public static (long TotalMb, long AvailMb) Memory()
    {
        try
        {
            return SysMemory.GetPhysicalMemoryMb();
        }
        catch
        {
            return (0, 0);
        }
    }
}
