export type HelloResult = {
  server_version: string;
  protocol_version: number;
  capabilities: string[];
  session_id: string;
};

export type DiskIOTotals = {
  read_bytes_per_sec: number;
  write_bytes_per_sec: number;
  read_iops: number | null;
  write_iops: number | null;
  busy_percent: number | null;
  queue_length: number | null;
  avg_read_latency_ms: number | null;
  avg_write_latency_ms: number | null;
};

export type DiskPerPhysical = DiskIOTotals & { device_id?: string } & Record<string, any>;
export type DiskPerVolumeIO = DiskIOTotals & { volume_id: string; free_percent: number | null };

export type DiskCapacityTotals = { total_bytes: number | null; used_bytes: number | null; free_bytes: number | null };
export type DiskVolumeInfo = {
  id: string; mount_point: string; fs_type?: string | null;
  size_total_bytes?: number | null; size_used_bytes?: number | null; size_free_bytes?: number | null;
  read_only?: boolean | null; is_removable?: boolean | null; bitlocker_encryption?: string | null; free_percent?: number | null;
};
export type DiskPhysicalInfo = {
  id: string; model?: string | null; serial?: string | null; firmware?: string | null; size_total_bytes?: number | null;
  partitions?: number | null; media_type?: string | null; spindle_speed_rpm?: number | null; interface_type?: string | null; trim_supported?: boolean | null;
};

export type SnapshotResult = {
  ts: number;
  cpu?: { usage_percent: number };
  memory?: { total: number; used: number };
  disk?: {
    read_bytes_per_sec: number;
    write_bytes_per_sec: number;
    queue_length: number;
    totals_source?: 'per_volume' | 'per_physical' | 'totals_raw';
    totals?: DiskIOTotals;
    per_physical_disk_io?: DiskPerPhysical[];
    per_volume_io?: DiskPerVolumeIO[];
    capacity_totals?: DiskCapacityTotals;
    per_volume?: DiskVolumeInfo[];
    per_physical_disk?: DiskPhysicalInfo[];
  };
};

export type SnapshotParams = {
  modules?: string[];
};

export type QueryHistoryParams = {
  from_ts: number;
  to_ts: number;
  modules?: string[];
  step_ms?: number | null;
  agg?: 'raw' | '10s' | '1m';
};

export type QueryHistoryItem = {
  ts: number;
  cpu?: { usage_percent: number } | null;
  memory?: { total: number; used: number } | null;
  disk?: SnapshotResult['disk'] | null;
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
