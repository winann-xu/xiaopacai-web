<script setup lang="ts">
// 小趴菜 Web 3.0 — 设备管理
import { ref, onMounted, computed } from 'vue'
import { useDeviceStore } from '@/stores/devices'
import type { Device } from '@/stores/devices'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Search, Monitor } from '@element-plus/icons-vue'

const deviceStore = useDeviceStore()
const searchText = ref('')
const showPairDialog = ref(false)
const pairingCode = ref('')
const manualIp = ref('')
const showDetailDialog = ref(false)
const detailDevice = ref<Device | null>(null)

const filteredDevices = computed(() => {
  if (!searchText.value) return deviceStore.devices
  const q = searchText.value.toLowerCase()
  return deviceStore.devices.filter(d =>
    d.name.toLowerCase().includes(q) || d.deviceId.toLowerCase().includes(q) || d.ipAddress.includes(q))
})

onMounted(() => { deviceStore.fetchDevices() })

function generatePairingCode() {
  pairingCode.value = String(Math.floor(100000 + Math.random() * 900000))
  showPairDialog.value = true
}

function confirmPair() {
  ElMessage.success(`配对码 ${pairingCode.value} 已生成，请在儿童设备上输入`)
  showPairDialog.value = false
}

function showDetail(device: Device) { detailDevice.value = device; showDetailDialog.value = true }

async function handleUnpair(device: Device) {
  try {
    await ElMessageBox.confirm(`确定要解绑设备「${device.name}」吗？解绑后该设备将无法连接。`, '确认解绑', { type: 'warning' })
    await deviceStore.unpairDevice(device.id)
    ElMessage.success('解绑成功')
  } catch { /* 取消 */ }
}

function statusTagType(s: string) { return s === 'online' ? 'success' : s === 'reconnecting' ? 'warning' : 'info' }
function statusText(s: string) { return s === 'online' ? '在线' : s === 'reconnecting' ? '重连中' : '离线' }
</script>

<template>
  <div class="devices-page">
    <div class="page-header">
      <h2 class="page-title">设备管理</h2>
      <div class="page-actions">
        <el-input v-model="searchText" placeholder="搜索设备" :prefix-icon="Search" clearable style="width: 220px" />
        <el-button type="primary" :icon="Plus" @click="generatePairingCode">添加设备</el-button>
      </div>
    </div>

    <div v-loading="deviceStore.loading" class="device-grid">
      <el-empty v-if="!deviceStore.devices.length" description="暂无设备" />
      <el-card v-for="device in filteredDevices" :key="device.id" shadow="hover" class="device-card"
        :class="{ 'is-offline': device.status === 'offline' }">
        <div class="card-body" @click="showDetail(device)">
          <div class="card-icon">
            <el-icon :size="40"><Monitor /></el-icon>
            <el-tag :type="statusTagType(device.status)" size="small" class="status-tag">{{ statusText(device.status) }}</el-tag>
          </div>
          <div class="card-info">
            <h3 class="device-name">{{ device.name }}</h3>
            <p class="device-meta">{{ device.deviceId }} · {{ device.osVersion }}</p>
            <p class="device-ip">IP: {{ device.ipAddress }}</p>
            <el-progress :percentage="device.todayLimitMinutes ? Math.round(device.todayUsageMinutes / device.todayLimitMinutes * 100) : 0"
              :stroke-width="10" :status="device.todayUsageMinutes >= device.todayLimitMinutes ? 'exception' : undefined"
              style="margin-top: 8px" />
            <p class="device-usage">今日：{{ device.todayUsageMinutes }} / {{ device.todayLimitMinutes }} 分钟</p>
          </div>
        </div>
        <div class="card-actions">
          <el-button size="small" text type="primary" @click.stop="showDetail(device)">详情</el-button>
          <el-button size="small" text type="danger" @click.stop="handleUnpair(device)">解绑</el-button>
        </div>
      </el-card>
    </div>

    <!-- 配对弹窗 -->
    <el-dialog v-model="showPairDialog" title="添加设备" width="460px">
      <el-form label-position="top">
        <el-form-item label="配对码">
          <el-input v-model="pairingCode" readonly
            style="font-size: 24px; letter-spacing: 6px; text-align: center; font-weight: 700" />
          <el-button size="small" style="margin-top:8px" @click="pairingCode = String(Math.floor(100000 + Math.random() * 900000))">重新生成</el-button>
        </el-form-item>
        <el-form-item label="手动指定 IP（可选）"><el-input v-model="manualIp" placeholder="192.168.1.xxx" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showPairDialog = false">取消</el-button>
        <el-button type="primary" :disabled="!pairingCode" @click="confirmPair">确认</el-button>
      </template>
    </el-dialog>

    <!-- 详情弹窗 -->
    <el-dialog v-model="showDetailDialog" title="设备详情" width="560px">
      <template v-if="detailDevice">
        <el-descriptions :column="2" border>
          <el-descriptions-item label="设备名称">{{ detailDevice.name }}</el-descriptions-item>
          <el-descriptions-item label="设备 ID">{{ detailDevice.deviceId }}</el-descriptions-item>
          <el-descriptions-item label="系统版本">{{ detailDevice.osVersion }}</el-descriptions-item>
          <el-descriptions-item label="状态">
            <el-tag :type="statusTagType(detailDevice.status)" size="small">{{ statusText(detailDevice.status) }}</el-tag>
          </el-descriptions-item>
          <el-descriptions-item label="IP 地址">{{ detailDevice.ipAddress }}</el-descriptions-item>
          <el-descriptions-item label="最后在线">{{ new Date(detailDevice.lastSeen).toLocaleString('zh-CN') }}</el-descriptions-item>
          <el-descriptions-item label="配对时间">{{ new Date(detailDevice.pairedAt).toLocaleString('zh-CN') }}</el-descriptions-item>
          <el-descriptions-item label="今日使用">{{ detailDevice.todayUsageMinutes }} / {{ detailDevice.todayLimitMinutes }} 分钟</el-descriptions-item>
          <el-descriptions-item label="证书指纹" :span="2">
            <code style="font-size:11px;word-break:break-all">{{ detailDevice.certFingerprint }}</code>
          </el-descriptions-item>
        </el-descriptions>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.devices-page { max-width: 1400px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px; flex-wrap: wrap; gap: 12px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
.page-actions { display: flex; gap: 8px; align-items: center; }
.device-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap: 16px; }
.device-card { cursor: pointer; transition: transform 0.2s; }
.device-card:hover { transform: translateY(-2px); }
.device-card.is-offline { opacity: 0.7; }
.card-body { display: flex; gap: 16px; }
.card-icon { position: relative; flex-shrink: 0; color: var(--el-color-primary); }
.status-tag { position: absolute; bottom: -4px; left: 50%; transform: translateX(-50%); white-space: nowrap; }
.card-info { flex: 1; min-width: 0; }
.device-name { font-size: 16px; font-weight: 600; margin: 0 0 4px; }
.device-meta, .device-ip { font-size: 12px; color: var(--el-text-color-secondary); margin: 0 0 2px; }
.device-usage { font-size: 12px; color: var(--el-text-color-secondary); margin: 4px 0 0; }
.card-actions { display: flex; justify-content: flex-end; gap: 4px; margin-top: 12px; padding-top: 12px; border-top: 1px solid var(--el-border-color-lighter); }

/* [TASK-PRELAUNCH-P1] 移动端：页头堆叠、搜索全宽、单列卡片、按钮触控区 */
@media (max-width: 768px) {
  .page-header { flex-direction: column; align-items: stretch; }
  .page-actions { flex-direction: column; }
  .page-actions .el-input { width: 100% !important; }
  .page-actions .el-button { min-height: 44px; }
  .device-grid { grid-template-columns: 1fr; }
  .card-actions .el-button { min-height: 44px; }
}
</style>
