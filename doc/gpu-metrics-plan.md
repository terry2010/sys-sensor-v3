# GPU 指标开发计划（Windows 10）

本计划基于 `doc/istat-menus-metrics.md` 的“GPU（图形处理器）”条目，结合当前代码现状（`src/SystemMonitor.Service/Services/Collectors/GpuCollector.cs` 仅占位、`LhmSensors` 已集成 LibreHardwareMonitor）制定。目标是逐步实现 Windows 上可落地且稳定的 GPU 采集能力，并保持与全局契约的 snake_case 命名与 JSON-RPC 推流一致。

---

## 1. 指标清单（对齐 istat-menus-metrics.md 第 2 节）
- 使用率（usage）
  - 总体使用率（多引擎合并）
  - 分引擎使用率（3D/Copy/Compute/VideoDecode/VideoEncode 等）可选
  - 多 GPU（集显/独显/eGPU）分设备使用率
- 显存（VRAM）
  - dedicated_used_mb / dedicated_total_mb
  - shared_used_mb / shared_total_mb（UMA 主存共享）
- 频率/功耗/温度（若可用）
  - core_clock_mhz / memory_clock_mhz（可用即填）
  - power_draw_w（可用即填）
  - temperature_core_c（至少核心温度）
- 活跃 GPU 状态
  - active_adapter_index / active_adapter_name（启发式：使用率最高且>阈值者）
- 顶部进程（按 GPU 使用率，若可用）
  - top_processes[]: { name, pid, gpu_percent }
- Apple Neural Engine（ANE）
  - Windows 不适用，字段省略

说明：不可得字段返回 null；数组/对象在确无数据时返回 null 而非空数组，以和现有风格一致（参考 `CpuCollector` 某些可选字段）。

---

## 2. 数据来源与优先级（Windows 10）
- 使用率（优先顺序）
  1) Windows 性能计数器（Performance Counters）
     - Category: "GPU Engine"
     - Counter: "% Utilization"（按实例汇总，实例名包含引擎与适配器 GUID）
     - 策略：
       - 按适配器分组（从实例名解析 LUID/GPU#），对所有引擎求和/裁剪至 100。
       - 可选提供分引擎明细（engtype_3D/Compute/Copy/VideoDecode/VideoEncode）。
  2) 回退：WMI（若对应类可用，通常较弱）/返回 null。
- 显存（VRAM）
  1) 性能计数器
     - Category: "GPU Adapter Memory"
     - Counters: "Dedicated Usage", "Shared Usage", "Dedicated Limit", "Shared Limit"
     - 按适配器分组，换算为 MB。
  2) 回退：DXGI QueryVideoMemoryInfo（需要 P/Invoke，备选阶段二）。
- 频率/功耗/温度
  1) LibreHardwareMonitor（已集成 `LhmSensors`）
     - 读取 GPU 相关传感器：Temperature/Clock/Power（厂商/型号依赖）
     - 优点：统一多厂商（NVIDIA/AMD/Intel）
  2) 厂商 SDK（阶段三，可选）
     - NVML（NVIDIA）、ADLX/ADL（AMD）、Intel Power Gadget/IGCL
     - 用于提升精度与项覆盖度（如 per-rail 功耗、更多时钟）
- 活跃 GPU 状态
  - 启发式：在当前采样窗口内，使用率最高且超过阈值（如 5%）的适配器视作 active。
- 顶部进程（GPU）
  - 阶段二/三实现：
    - 选项 A：ETW/DXGI/D3DKMTQueryStatistics（复杂）
    - 选项 B：性能计数器若存在 per-process GPU 使用（部分环境不可用）
    - 首次交付先置为 null。

---

## 3. 接口与数据模型（对外 JSON，snake_case）
在 `snapshot()` 与 `metrics` 推流中增加 `gpu` 节点：

```json
{
  "gpu": {
    "adapters": [
      {
        "index": 0,
        "name": "NVIDIA GeForce RTX 3070",
        "usage_percent": 23.5,
        "usage_by_engine_percent": {
          "3d": 20.1,
          "compute": 0.0,
          "copy": 1.2,
          "video_decode": 2.0,
          "video_encode": 0.2
        },
        "vram_dedicated_used_mb": 1024,
        "vram_dedicated_total_mb": 8192,
        "vram_shared_used_mb": 256,
        "vram_shared_total_mb": 16384,
        "core_clock_mhz": 1800,
        "memory_clock_mhz": 7000,
        "power_draw_w": 75.3,
        "temperature_core_c": 62.5
      }
    ],
    "active_adapter_index": 0,
    "active_adapter_name": "NVIDIA GeForce RTX 3070",
    "top_processes": null
  }
}
```

注意：上述字段为目标模型，首次交付允许部分字段为 null，仅确保结构稳定、字段名不变。

---

## 4. 实施阶段与里程碑
- 阶段一（基础可用）
  - 使用 "GPU Engine" 与 "GPU Adapter Memory" 计数器实现：
    - per-adapter 使用率聚合、VRAM 使用/总量
  - 通过 `LhmSensors` 补充 temperature_core_c（若可得）
  - `GpuCollector` 返回上述基础字段；`RpcHostedService`/推流对接
  - 前端简单展示每个适配器使用率与 VRAM 条
- 阶段二（增强指标）
  - usage_by_engine_percent 明细（3D/Compute/Copy/Video*）
  - 时钟（core/memory）与功耗（power_draw_w）通过 LHM 能力最大化覆盖
  - 活跃适配器判定
- 阶段三（高级/可选）
  - 厂商 SDK（NVML/ADLX/Intel）接入，提高功耗/频率等准确性
  - 顶部进程 top_processes[] 实现

---

## 5. 采样与性能
- Collector 内部节流：200ms 窗口内复用上次结果（与 `todo-2025-08-22.md` 策略一致）
- 汇总裁剪：多引擎求和后 clamp 至 [0,100]
- 失败容错：创建计数器失败/读取异常 → 返回 null，并打印 Debug 日志（一次性/限频）
- 多 GPU 适配：按实例名解析适配器标识并稳定排序（名称 + GUID 片段）

---

## 6. 测试计划
- 单元测试（`SystemMonitor.Tests`）
  - `snapshot()`/`metrics` 中存在 `gpu` 节点
  - 在无性能计数器环境（CI 或 Server Core）下，`GpuCollector` 不抛异常、返回结构且字段为 null 或 0
  - 使用率范围校验（0..100），VRAM 使用不超过总量
- 端到端测试
  - 启动服务后能收到包含 `gpu.adapters[]` 的推流
  - 在启用 LHM 的环境下可返回温度字段

---

## 7. 代码改动点
- `src/SystemMonitor.Service/Services/Collectors/GpuCollector.cs`
  - 从占位实现为真实采集：
    - 初始化并缓存 PerformanceCounter/PDH 访问器
    - 解析 "GPU Engine" 与 "GPU Adapter Memory" 实例并分组
    - 调用 `SensorsProvider.Current`（LHM）补温度/时钟/功耗（若可得）
- `RpcServer.PushAndConfig.cs` / `RpcHostedService.cs`
  - 确保 `gpu` 被纳入 `snapshot()` 与 `metrics` 推流
- `doc/api-reference.md` / `doc/data-models.md`
  - 补充 `gpu` 节点字段说明与单位
- 前端：
  - `frontend/src/components/` 新增 `GpuPanel.vue`（或调试面板临时展示）

---

## 8. 风险与回退
- 某些系统缺少 GPU 相关性能计数器：返回 null，不中断服务
- LHM 对部分新显卡支持有限：相关字段保持 null；后续以厂商 SDK 补齐
- 不同 Windows 版本实例名差异：分组逻辑需兼容大小写与不同引擎命名

---

## 9. 验收标准（阶段一）
- 本地 Windows 10 机器：能实时看到每个 GPU 的使用率与 VRAM 使用
- JSON 契约稳定：字段命名与单位符合文档
- 单测与端到端测试通过
- 使用 `scripts/dev.ps1` 启动联调，日志无异常抛出
