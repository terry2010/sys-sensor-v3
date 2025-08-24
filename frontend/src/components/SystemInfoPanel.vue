<template>
  <div class="card">
    <h3>System Information</h3>
    <div class="sub">Hardware & Software Details</div>
    
    <div v-if="!systemInfo" class="empty">No system information available</div>
    
    <div v-else class="system-info">
      <!-- 机器信息 -->
      <div class="info-section">
        <h4>Machine</h4>
        <div class="info-grid">
          <div class="info-item">
            <span class="label">Manufacturer</span>
            <span class="value">{{ systemInfo.machine?.manufacturer || '—' }}</span>
          </div>
          <div class="info-item">
            <span class="label">Model</span>
            <span class="value">{{ systemInfo.machine?.model || '—' }}</span>
          </div>
          <div class="info-item">
            <span class="label">Serial Number</span>
            <span class="value">{{ systemInfo.machine?.serial_number || '—' }}</span>
          </div>
          <div class="info-item">
            <span class="label">Chassis Type</span>
            <span class="value">{{ systemInfo.machine?.chassis_type || '—' }}</span>
          </div>
          <div class="info-item" v-if="systemInfo.machine?.uuid">
            <span class="label">UUID</span>
            <span class="value mono">{{ systemInfo.machine.uuid }}</span>
          </div>
        </div>
      </div>

      <!-- 操作系统信息 -->
      <div class="info-section">
        <h4>Operating System</h4>
        <div class="info-grid">
          <div class="info-item">
            <span class="label">Name</span>
            <span class="value">{{ systemInfo.operating_system?.name || '—' }}</span>
          </div>
          <div class="info-item">
            <span class="label">Version</span>
            <span class="value">{{ systemInfo.operating_system?.version || '—' }}</span>
          </div>
          <div class="info-item">
            <span class="label">Build Number</span>
            <span class="value">{{ systemInfo.operating_system?.build_number || '—' }}</span>
          </div>
          <div class="info-item">
            <span class="label">Architecture</span>
            <span class="value">{{ systemInfo.operating_system?.architecture || '—' }}</span>
          </div>
          <div class="info-item" v-if="systemInfo.operating_system?.install_date">
            <span class="label">Install Date</span>
            <span class="value">{{ systemInfo.operating_system.install_date }}</span>
          </div>
          <div class="info-item" v-if="systemInfo.operating_system?.uptime_hours">
            <span class="label">Uptime</span>
            <span class="value">{{ formatUptime(systemInfo.operating_system.uptime_hours) }}</span>
          </div>
        </div>
      </div>

      <!-- 处理器信息 -->
      <div class="info-section">
        <h4>Processor</h4>
        <div class="info-grid">
          <div class="info-item">
            <span class="label">Name</span>
            <span class="value">{{ systemInfo.processor?.name || '—' }}</span>
          </div>
          <div class="info-item">
            <span class="label">Manufacturer</span>
            <span class="value">{{ systemInfo.processor?.manufacturer || '—' }}</span>
          </div>
          <div class="info-item">
            <span class="label">Cores</span>
            <span class="value">
              {{ systemInfo.processor?.physical_cores || '—' }} Physical, 
              {{ systemInfo.processor?.logical_cores || '—' }} Logical
            </span>
          </div>
          <div class="info-item" v-if="systemInfo.processor?.max_clock_speed_mhz">
            <span class="label">Max Clock Speed</span>
            <span class="value">{{ systemInfo.processor.max_clock_speed_mhz }} MHz</span>
          </div>
          <div class="info-item" v-if="systemInfo.processor?.l2_cache_size_kb">
            <span class="label">L2 Cache</span>
            <span class="value">{{ formatBytes(systemInfo.processor.l2_cache_size_kb * 1024) }}</span>
          </div>
          <div class="info-item" v-if="systemInfo.processor?.l3_cache_size_kb">
            <span class="label">L3 Cache</span>
            <span class="value">{{ formatBytes(systemInfo.processor.l3_cache_size_kb * 1024) }}</span>
          </div>
        </div>
      </div>

      <!-- 内存信息 -->
      <div class="info-section">
        <h4>Memory</h4>
        <div class="info-grid">
          <div class="info-item" v-if="systemInfo.memory?.total_physical_mb">
            <span class="label">Total Physical</span>
            <span class="value">{{ formatBytes(systemInfo.memory.total_physical_mb * 1024 * 1024) }}</span>
          </div>
          <div class="info-item" v-if="systemInfo.memory?.total_slots">
            <span class="label">Memory Slots</span>
            <span class="value">{{ systemInfo.memory.total_slots }}</span>
          </div>
        </div>
        
        <!-- 内存模块详情 -->
        <div v-if="Array.isArray(systemInfo.memory?.modules) && systemInfo.memory.modules.length > 0" class="memory-modules">
          <h5>Memory Modules</h5>
          <div class="modules-table">
            <div class="module-header">
              <span>Slot</span>
              <span>Capacity</span>
              <span>Speed</span>
              <span>Manufacturer</span>
            </div>
            <div v-for="(module, i) in systemInfo.memory.modules" :key="i" class="module-row">
              <span>{{ module.device_locator || `Slot ${i + 1}` }}</span>
              <span>{{ module.capacity_mb ? formatBytes(module.capacity_mb * 1024 * 1024) : '—' }}</span>
              <span>{{ module.speed_mhz ? `${module.speed_mhz} MHz` : '—' }}</span>
              <span>{{ module.manufacturer || '—' }}</span>
            </div>
          </div>
        </div>
      </div>

      <!-- 图形设备信息 -->
      <div class="info-section" v-if="Array.isArray(systemInfo.graphics) && systemInfo.graphics.length > 0">
        <h4>Graphics</h4>
        <div v-for="(gpu, i) in systemInfo.graphics" :key="i" class="gpu-item">
          <div class="info-grid">
            <div class="info-item">
              <span class="label">Name</span>
              <span class="value">{{ gpu.name || '—' }}</span>
            </div>
            <div class="info-item" v-if="gpu.adapter_ram_mb">
              <span class="label">Video Memory</span>
              <span class="value">{{ formatBytes(gpu.adapter_ram_mb * 1024 * 1024) }}</span>
            </div>
            <div class="info-item" v-if="gpu.driver_version">
              <span class="label">Driver Version</span>
              <span class="value">{{ gpu.driver_version }}</span>
            </div>
            <div class="info-item" v-if="gpu.driver_date">
              <span class="label">Driver Date</span>
              <span class="value">{{ gpu.driver_date }}</span>
            </div>
          </div>
        </div>
      </div>

      <!-- 固件信息 -->
      <div class="info-section">
        <h4>Firmware</h4>
        <div class="info-grid">
          <div class="info-item">
            <span class="label">Manufacturer</span>
            <span class="value">{{ systemInfo.firmware?.manufacturer || '—' }}</span>
          </div>
          <div class="info-item">
            <span class="label">Version</span>
            <span class="value">{{ systemInfo.firmware?.version || '—' }}</span>
          </div>
          <div class="info-item" v-if="systemInfo.firmware?.release_date">
            <span class="label">Release Date</span>
            <span class="value">{{ systemInfo.firmware.release_date }}</span>
          </div>
          <div class="info-item" v-if="systemInfo.firmware?.smbios_version">
            <span class="label">SMBIOS Version</span>
            <span class="value">{{ systemInfo.firmware.smbios_version }}</span>
          </div>
        </div>
      </div>

      <!-- 网络标识信息 -->
      <div class="info-section">
        <h4>Network Identity</h4>
        <div class="info-grid">
          <div class="info-item">
            <span class="label">Hostname</span>
            <span class="value">{{ systemInfo.network_identity?.hostname || '—' }}</span>
          </div>
          <div class="info-item" v-if="systemInfo.network_identity?.domain">
            <span class="label">Domain</span>
            <span class="value">{{ systemInfo.network_identity.domain }}</span>
          </div>
          <div class="info-item" v-if="systemInfo.network_identity?.primary_mac_address">
            <span class="label">Primary MAC</span>
            <span class="value mono">{{ systemInfo.network_identity.primary_mac_address }}</span>
          </div>
          <div class="info-item" v-if="systemInfo.network_identity?.public_ip_address">
            <span class="label">Public IP</span>
            <span class="value mono">{{ systemInfo.network_identity.public_ip_address }}</span>
          </div>
        </div>
        
        <!-- 本地IP地址列表 -->
        <div v-if="Array.isArray(systemInfo.network_identity?.local_ip_addresses) && systemInfo.network_identity.local_ip_addresses.length > 0" class="local-ips">
          <h5>Local IP Addresses</h5>
          <div class="ip-list">
            <span v-for="ip in systemInfo.network_identity.local_ip_addresses" :key="ip" class="ip-item mono">{{ ip }}</span>
          </div>
        </div>
      </div>

      <!-- 错误信息 -->
      <div v-if="systemInfo.error" class="error-section">
        <h4>Collection Error</h4>
        <div class="error-message">{{ systemInfo.error }}</div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { useMetricsStore } from '../stores/metrics';

const metrics = useMetricsStore();

// 获取系统信息数据
const systemInfo = computed(() => {
  const latest = metrics.latest as any;
  return latest?.system_info || null;
});

// 格式化运行时间
const formatUptime = (hours: number): string => {
  const days = Math.floor(hours / 24);
  const remainingHours = Math.floor(hours % 24);
  const minutes = Math.floor((hours % 1) * 60);
  
  if (days > 0) {
    return `${days}d ${remainingHours}h ${minutes}m`;
  } else if (remainingHours > 0) {
    return `${remainingHours}h ${minutes}m`;
  } else {
    return `${minutes}m`;
  }
};

// 格式化字节大小
const formatBytes = (bytes: number): string => {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let size = bytes;
  let unitIndex = 0;
  
  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024;
    unitIndex++;
  }
  
  return `${size.toFixed(unitIndex > 0 ? 1 : 0)} ${units[unitIndex]}`;
};
</script>

<style scoped>
.card {
  border: 1px solid #ddd;
  border-radius: 8px;
  padding: 12px;
  margin-bottom: 16px;
}

.sub {
  color: #666;
  font-size: 12px;
  margin-bottom: 12px;
}

.empty {
  color: #999;
  font-style: italic;
  text-align: center;
  padding: 20px;
}

.system-info {
  display: grid;
  gap: 16px;
}

.info-section {
  border: 1px solid #eee;
  border-radius: 6px;
  padding: 12px;
}

.info-section h4 {
  margin: 0 0 8px 0;
  color: #333;
  font-size: 14px;
  font-weight: 600;
}

.info-section h5 {
  margin: 12px 0 6px 0;
  color: #555;
  font-size: 12px;
  font-weight: 600;
}

.info-grid {
  display: grid;
  gap: 6px;
}

.info-item {
  display: grid;
  grid-template-columns: 120px 1fr;
  gap: 12px;
  align-items: center;
  font-size: 12px;
  padding: 4px 0;
  border-bottom: 1px dashed #f0f0f0;
}

.info-item:last-child {
  border-bottom: none;
}

.label {
  color: #666;
  font-weight: 500;
}

.value {
  color: #333;
  word-break: break-all;
}

.mono {
  font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
  font-size: 11px;
}

.memory-modules {
  margin-top: 12px;
}

.modules-table {
  border: 1px solid #eee;
  border-radius: 4px;
  overflow: hidden;
}

.module-header {
  display: grid;
  grid-template-columns: 1fr 1fr 1fr 1fr;
  gap: 8px;
  background: #f8f9fa;
  padding: 8px 12px;
  font-size: 11px;
  font-weight: 600;
  color: #666;
  border-bottom: 1px solid #eee;
}

.module-row {
  display: grid;
  grid-template-columns: 1fr 1fr 1fr 1fr;
  gap: 8px;
  padding: 8px 12px;
  font-size: 11px;
  border-bottom: 1px dashed #f0f0f0;
}

.module-row:last-child {
  border-bottom: none;
}

.gpu-item {
  margin-bottom: 12px;
  padding: 8px;
  background: #f8f9fa;
  border-radius: 4px;
}

.gpu-item:last-child {
  margin-bottom: 0;
}

.local-ips {
  margin-top: 12px;
}

.ip-list {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.ip-item {
  background: #f0f0f0;
  padding: 4px 8px;
  border-radius: 4px;
  font-size: 11px;
}

.error-section {
  border-color: #ffcdd2;
  background: #ffebee;
}

.error-message {
  color: #c62828;
  font-size: 12px;
  font-family: monospace;
}

@media (min-width: 900px) {
  .info-grid {
    grid-template-columns: 1fr 1fr;
  }
  
  .info-item {
    grid-template-columns: 140px 1fr;
  }
}
</style>