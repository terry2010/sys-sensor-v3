# 安全设计文档（草案）

## 1. Named Pipe 与 ACL
- 名称：`\\.\pipe\sys_sensor_v3.rpc`
- ACL：SYSTEM/Administrators/当前交互用户；其他拒绝

## 2. Token 生成与存储
- 路径：`%ProgramData%\sys-sensor-v3\token`（ACL 600）
- 生成：随机 128bit+，十六进制；安装时生成；更新不变
- 握手：`hello.token` 必填；失败返回 `unauthorized(-32002)`

## 3. 握手流程时序
1) UI→Service: `hello(token, app_version, protocol_version)`
2) Service：校验 token/版本 → 返回 `session_id`
3) 失败重试：客户端指数退避

## 4. 更新签名验证
- 更新包：签名校验通过方可应用；失败回滚
- 提权：仅 `updater.exe` 提权执行

## 5. 权限最小化
- 服务以最小权限账户运行
- 仅本机通信，不开网络端口

## 6. MVP 放宽与后续收紧
- MVP 阶段：为便于联调，服务端允许“非空 token 视为通过”，即仅校验非空，不校验值匹配。
- 风险提示：仅用于本地开发调试，禁止在生产/内测分发中启用。
- 收紧路线：
  - 开启严格 token 匹配（服务端读取 `%ProgramData%` 存储对比）。
  - 增加会话级速率/次数限制与黑名单。
  - 升级至基于 HMAC 的签名校验与轮换机制（长远规划）。
