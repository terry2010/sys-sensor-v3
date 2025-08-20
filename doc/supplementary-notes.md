# 补充说明（草案）

## 1. PowerShell 示例（同行以`;`分隔）
```
# 构建占位
pwsh -NoProfile -Command "echo build start; dotnet --info; node -v; echo done"
```

## 2. 调试模式
- 如需 WebSocket 临时调试端口，需文档声明用途与关闭方法（默认关闭）

## 3. 配置热重载
- `FileSystemWatcher` 监控 `%ProgramData%/sys-sensor-v3/config.json`

## 4. 降级方案
- 采集器不可用 → 返回 `module_unavailable(-32005)`，UI 适配

## 5. 已知限制
- 指标覆盖度受硬件与驱动限制
