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

export type DiskPerPhysical = DiskIOTotals & { disk_id?: string } & Record<string, any>;
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
  bus_type?: string | null; negotiated_link_speed?: string | null; is_removable?: boolean | null; eject_capable?: boolean | null;
};

export type SmartHealth = {
  disk_id: string;
  overall_health?: string | null;
  temperature_c?: number | null;
  power_on_hours?: number | null;
  reallocated_sector_count?: number | null;
  pending_sector_count?: number | null;
  udma_crc_error_count?: number | null;
  nvme_percentage_used?: number | null;
  nvme_data_units_read?: number | null;
  nvme_data_units_written?: number | null;
  nvme_controller_busy_time_min?: number | null;
  unsafe_shutdowns?: number | null;
  thermal_throttle_events?: number | null;
  // New NVMe health fields
  nvme_available_spare?: number | null;
  nvme_spare_threshold?: number | null;
  nvme_media_errors?: number | null;
  nvme_power_cycles?: number | null;
  nvme_power_on_hours?: number | null;
  nvme_critical_warning?: number | null; // byte
  nvme_temp_sensor1_c?: number | null;
  nvme_temp_sensor2_c?: number | null;
  nvme_temp_sensor3_c?: number | null;
  nvme_temp_sensor4_c?: number | null;
  // NVMe Identify/Namespace fields
  nvme_namespace_count?: number | null;
  nvme_namespace_lba_size_bytes?: number | null;
  nvme_namespace_size_lba?: number | null;
  nvme_namespace_inuse_lba?: number | null;
  nvme_namespace_capacity_bytes?: number | null;
  nvme_namespace_inuse_bytes?: number | null;
  nvme_eui64?: string | null;
  nvme_nguid?: string | null;
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
    vm_swapfiles_bytes?: number | null;
    per_volume?: DiskVolumeInfo[];
    per_physical_disk?: DiskPhysicalInfo[];
    smart_health?: SmartHealth[];
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
