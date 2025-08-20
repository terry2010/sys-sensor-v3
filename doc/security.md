# 安全设计（M1 骨架）

## 1. 威胁模型与攻击面
- 本地 IPC（Named Pipe）劫持与权限提升
- 配置/日志/数据库中的敏感信息泄露
- 组件更新与执行完整性

## 2. Named Pipe ACL 与最小权限
- 管道名：`\\.\pipe\sys_sensor_v3.rpc`
- ACL 建议：`LocalSystem`、本机 `Administrators`、当前交互用户 SID
- 拒绝 `ANONYMOUS LOGON`、`Everyone`
- 服务启动时记录有效 ACL 到日志，异常立即失败

## 3. 握手与认证
- `hello` 必带 `token`（本地生成/安装时下发）
- 返回 `protocol_version`、`capabilities`，客户端做兼容分支
- 失败返回 JSON-RPC `unauthorized(-32040)`

## 4. 能力与授权
- 按能力位开放接口（如 `history_query`）
- 未来可按会话粒度限流/配额

## 5. 日志与隐私
- 统一结构化日志（禁止记录 token/PII）
- 滚动与保留：容量/天数双阈值
- 故障时仅打印摘要与哈希，避免原始载荷落盘

## 6. 更新与完整性（预研）
- 包签名与验签、回滚策略、受限更新通道
- 最小化执行权限（非管理员运行）

## 7. 安全基线
- 默认最小权限运行
- 对外接口最小集、参数严格校验（`invalid_params`）
- 单元与 E2E 含安全用例（认证失败、ACL 异常、权限不足）
