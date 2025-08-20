import type { QueryHistoryParams, QueryHistoryResult, SnapshotResult, HelloResult } from './dto';
import { rpc } from './rpc';

export const service = {
  hello(): Promise<HelloResult> { return rpc.hello(); },
  snapshot(): Promise<SnapshotResult> { return rpc.snapshot(); },
  queryHistory(p: QueryHistoryParams): Promise<QueryHistoryResult> { return rpc.query_history(p); },
  onMetrics: rpc.onMetrics
};
