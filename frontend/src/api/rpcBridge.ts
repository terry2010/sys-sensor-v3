// 事件桥连接管理：指数退避重连 + 心跳
// 在 Tauri 环境生效；Web 环境下安全 no-op

let started = false;

export type BridgeStatus = 'idle' | 'connecting' | 'connected' | 'disconnected' | 'error';

export async function startBridgeManager(opts?: { heartbeatMs?: number; maxBackoffMs?: number }) {
  if (started) return; started = true;
  const heartbeatMs = opts?.heartbeatMs ?? 30_000;
  const maxBackoffMs = opts?.maxBackoffMs ?? 10_000;

  const w: any = typeof window !== 'undefined' ? window : {};
  const isTauri = !!(w && (w.__IS_TAURI__ || w.__TAURI__));
  if (!isTauri) return; // 非 Tauri 环境不做任何事

  let status: BridgeStatus = 'idle';
  let backoff = 500; // 初始 0.5s
  let hbTimer: any = null;
  let retryTimer: any = null;

  const setStatus = (s: BridgeStatus) => {
    status = s;
    try { w.__BRIDGE_STATUS__ = s; } catch {}
    try {
      const evt = new CustomEvent('bridge_status', { detail: { status: s } });
      window.dispatchEvent(evt);
    } catch { /* ignore */ }
  };

  const { listen } = await import('@tauri-apps/api/event');
  const { invoke } = await import('@tauri-apps/api/core');

  const ensureStarted = async () => {
    try {
      setStatus('connecting');
      await invoke('start_event_bridge');
      await invoke('bridge_set_subscribe', { enable: true });
      setStatus('connected');
      backoff = 500; // 连接成功，重置退避
    } catch (e) {
      setStatus('error');
      scheduleRetry();
    }
  };

  const scheduleRetry = () => {
    if (retryTimer) return;
    const delay = Math.min(backoff, maxBackoffMs);
    retryTimer = setTimeout(async () => {
      retryTimer = null;
      backoff = Math.min(backoff * 2, maxBackoffMs);
      try {
        await ensureStarted();
      } catch (e) {
        console.error('Failed to restart bridge:', e);
        // 避免无限重试导致的堆栈问题
        if (backoff >= maxBackoffMs) {
          setStatus('error');
          console.warn('Bridge restart failed at max backoff, will not auto-retry');
          // 不再自动重试，避免极端情况导致的堆栈问题
          return;
        }
      }
    }, delay);
  };

  // 事件监听：根据桥接事件调整状态与重试
  try {
    await listen('bridge_handshake', async () => { setStatus('connected'); backoff = 500; });
    await listen('bridge_disconnected', async () => { 
      setStatus('disconnected'); 
      // 避免重复触发重连
      if (status !== 'connecting') {
        scheduleRetry(); 
      }
    });
    await listen('bridge_error', async () => { 
      setStatus('error'); 
      // 避免重复触发重连
      if (status !== 'connecting') {
        scheduleRetry(); 
      }
    });
  } catch (e) { 
    console.error('Failed to setup bridge event listeners:', e);
  }

  // 心跳：周期性调用轻量 RPC（snapshot），失败则触发重连
  const startHeartbeat = () => {
    if (hbTimer) clearInterval(hbTimer);
    let consecutiveErrors = 0;
    hbTimer = setInterval(async () => {
      try {
        await invoke('rpc_call', { method: 'snapshot', params: {} });
        consecutiveErrors = 0; // 重置错误计数
      } catch (e) {
        consecutiveErrors++;
        console.warn(`Heartbeat failed (${consecutiveErrors})`, e);
        setStatus('error');
        // 避免在已知异常情况下重复触发重连
        if (consecutiveErrors <= 3 && status !== 'connecting') {
          scheduleRetry();
        }
      }
    }, heartbeatMs);
  };

  startHeartbeat();
  await ensureStarted();
}
