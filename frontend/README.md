# SysSensorV3 前端（Tauri + Vue）

本目录为前端工程骨架，采用 Tauri(v2) + Vue3 + TypeScript + Vite。

当前提交内容：
- 目录与前端分层（api/service/组件/stores）
- DTO 类型与 Service 接口（与 `doc/api-reference.md` 对齐）
- 页面最小骨架（App + SnapshotPanel + HistoryChart 占位）
 已包含 `src-tauri/`（最小 Rust 宿主已就绪，后续在此内实现 Windows 命名管道 JSON-RPC 客户端与事件桥）。

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
在本目录下执行依赖安装：
```powershell
npm install
```

## 开发与运行
推荐使用仓库统一脚本在仓库根目录启动本地联调（会自动构建后端并等待命名管道就绪，再启动前端 Tauri 开发模式）：
```powershell
./scripts/dev.ps1
```
如需仅在前端目录单独启动（要求服务端已单独运行）：
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
- 在 `src-tauri/` 内完善 Windows 命名管道 JSON-RPC 客户端与自动重连逻辑，并把 `src/api/rpc.ts` 接到 Tauri invoke/event。
