# SysSensorV3 前端（Tauri + Vue）

本目录为前端工程骨架，采用 Tauri(v2) + Vue3 + TypeScript + Vite。

当前提交内容：
- 目录与前端分层（api/service/组件/stores）
- DTO 类型与 Service 接口（与 `doc/api-reference.md` 对齐）
- 页面最小骨架（App + SnapshotPanel + HistoryChart 占位）
- 尚未创建 `src-tauri/`（需要本机 Rust 工具链），请见下方“环境准备与初始化”。

## 环境准备（Windows 10）
1) Node.js 18+（LTS 推荐）
2) Rust 工具链（MSVC）：
   - 安装 Rustup：https://www.rust-lang.org/tools/install
   - 安装 Visual Studio Build Tools（含“使用 C++ 的桌面开发”组件）
3) Tauri CLI：
   ```powershell
   npm i -g @tauri-apps/cli
   ```

## 初始化
在本目录下执行：
```powershell
npm install
```

首次初始化 Tauri（生成 `src-tauri/`）— 待确认已安装 Rust 后执行：
```powershell
# 生成最小 Rust 宿主（Windows 命名管道桥接后续补充）
# 我来在确认环境后为你自动生成 src-tauri 代码
```

## 开发与运行（待生成 src-tauri 后）
```powershell
npm run dev   # 启动 Vite + Tauri 开发模式
```

## 分层说明
- src/api/dto.ts：与后端契约一致的 TypeScript 类型
- src/api/rpc.ts：传输与协议适配层（通过 Tauri invoke/event；当前为占位实现）
- src/api/service.ts：提供语义化方法，UI 仅依赖本层
- src/stores/：Pinia 状态（session/metrics）
- src/components/：UI 组件，不直接依赖传输细节

## 下一步
- 确认你已安装 Node 与 Rust，我将生成 `src-tauri/` 并实现 Windows 命名管道 JSON-RPC 客户端，然后把 `rpc.ts` 接到 Tauri invoke/event。
