using Xunit;
using System.Runtime.Versioning;

// 禁用并行化，避免多个测试同时启动服务并争用同一个命名管道
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// 测试代码调用了仅在 Windows 上受支持的 API（例如 PerformanceCounter、命名管道等），
// 通过在程序集层面声明支持平台，避免 CA1416 噪声告警。
[assembly: SupportedOSPlatform("windows")]
