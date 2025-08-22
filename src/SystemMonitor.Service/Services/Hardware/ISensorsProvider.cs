using System;

namespace SystemMonitor.Service.Services
{
    // 硬件传感器抽象接口：封装 LHM 读数与完整传感器表导出
    internal interface ISensorsProvider
    {
        (double? pkgTemp, double?[]? cores, double? pkgPower, int?[]? fans) Read();
        LhmSensorDto[] DumpAll();
    }
}
