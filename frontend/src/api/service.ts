import type { QueryHistoryParams, QueryHistoryResult, SnapshotResult, HelloResult } from './dto';
import { rpc } from './rpc';

export const service = {
  hello(): Promise<HelloResult> { return rpc.hello(); },
  snapshot(): Promise<SnapshotResult> { return rpc.snapshot(); },
  queryHistory(p: QueryHistoryParams): Promise<QueryHistoryResult> { return rpc.query_history(p); },
  onMetrics: rpc.onMetrics,
  // new methods
  setConfig(p: import('./dto').SetConfigParams) { return (rpc as any).set_config?.(p); },
  start(p?: import('./dto').StartParams) { return (rpc as any).start?.(p); },
  stop(p: import('./dto').StopParams = {}) { return (rpc as any).stop?.(p); },
  burstSubscribe(p: import('./dto').BurstSubscribeParams) { return (rpc as any).burst_subscribe?.(p); },
};
