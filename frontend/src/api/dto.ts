export type HelloResult = {
  server_version: string;
  protocol_version: number;
  capabilities: string[];
  session_id: string;
};

export type SnapshotResult = {
  ts: number;
  cpu?: { usage_percent: number };
  memory?: { total: number; used: number };
};

export type QueryHistoryParams = {
  from_ts: number;
  to_ts: number;
  modules?: string[];
  step_ms?: number | null;
};

export type QueryHistoryItem = {
  ts: number;
  cpu?: { usage_percent: number } | null;
  memory?: { total: number; used: number } | null;
};

export type QueryHistoryResult = {
  ok: boolean;
  items: QueryHistoryItem[];
};

// below: new DTOs
export type SetConfigParams = {
  base_interval_ms?: number;
  module_intervals?: Record<string, number>;
  persist?: boolean;
};

export type StartParams = { modules?: string[] } | undefined;
export type StopParams = {};

export type BurstSubscribeParams = {
  modules?: string[];
  interval_ms: number;
  ttl_ms: number;
};
