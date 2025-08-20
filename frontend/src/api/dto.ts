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
