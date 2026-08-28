<script setup lang="ts">
// 小趴菜 Web 3.0 — 管理端：设备管理（总览）
import { ref, computed, onMounted } from 'vue'
import { useDeviceStore, type Device } from '@/stores/devices'
import { authApi } from '@/api'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Search, Refresh } from '@element-plus/icons-vue'

const deviceStore = useDeviceStore()
const searchText = ref('')
const statusFilter = ref('all')
const detailVisible = ref(false)
const detailDevice = ref<Device | null>(null)

onMounted(() => deviceStore.fetchDevices())

const filteredDevices = computed(() => {
  let list = deviceStore.devices
  if (statusFilter.value !== 'all') list = list.filter(d => d.status === statusFilter.value)
  const q = searchText.value.trim().toLowerCase()
  if (q) {
    list = list.filter(d =>
      d.name.toLowerCase().includes(q) ||
      d.deviceId.toLowerCase().includes(q) ||
      (d.ownerAccount || '').toLowerCase().includes(q) ||
      d.ipAddress.toLowerCase().includes(q))
  }
  return list
})

function fmtTime(iso?: string | null) { return iso ? new Date(iso).toLocaleString('zh-CN', { hour12: false }) : '—' }

async function handleRename(device: Device) {
  try {
    const { value } = await ElMessageBox.prompt(
      `为设备「${device.name}」输入新的名称`,
      '重命名设备',
      {
        inputValue: device.name,
        inputPattern: /\S+/,
        inputErrorMessage: '名称不能为空',
        confirmButtonText: '保存',
        cancelButtonText: '取消',
      })
    await deviceStore.renameDevice(device.id, value)
    ElMessage.success('已重命名')
  } catch (e: any) {
    if (e === 'cancel' || e === 'close') return
    ElMessage.error(e.response?.data?.error || '重命名失败')
  }
}

function showDetail(device: Device) {
  detailDevice.value = device
  detailVisible.value = true
}

async function handleDeauthorize(deviceId: number) {
  try {
    await ElMessageBox.confirm('确定取消该设备授权？设备将无法连接。', '确认', { type: 'warning' })
  } catch { return }

  try {
    const { value: password } = await ElMessageBox.prompt(
      '取消授权是敏感操作，请输入登录密码确认身份（验证通过后 5 分钟内有效）',
      '安全验证',
      {
        inputType: 'password',
        inputPlaceholder: '登录密码',
        confirmButtonText: '验证并取消授权',
        inputValidator: (v: string) => (v ? true : '请输入登录密码'),
      })
    const res = await authApi.verifyPassword(password)
    await deviceStore.unpairDevice(deviceId, res.data.actionToken)
    ElMessage.success('已取消授权')
  } catch (e: any) {
    if (e === 'cancel' || e === 'close') return
    ElMessage.error(e.response?.data?.error || '操作失败，请重试')
  }
}
</script>

<template>
  <div class="admin-page">
    <div class="page-header">
      <h2 class="page-title">设备管理（总览）</h2>
      <div class="header-stats">
        <el-tag type="success">在线 {{ deviceStore.onlineCount }}</el-tag>
        <el-tag type="info" style="margin-left:8px">总数 {{ deviceStore.totalCount }}</el-tag>
      </div>
    </div>

    <div class="filter-bar">
      <el-input v-model="searchText" placeholder="搜索设备名 / 设备 ID / 账号 / IP"
        :prefix-icon="Search" clearable class="filter-search" />
      <el-select v-model="statusFilter" class="filter-status">
        <el-option label="全部状态" value="all" />
        <el-option label="在线" value="online" />
        <el-option label="重连" value="reconnecting" />
        <el-option label="离线" value="offline" />
      </el-select>
      <el-button :icon="Refresh" @click="deviceStore.fetchDevices()" :loading="deviceStore.loading">刷新</el-button>
    </div>

    <div class="table-wrap">
      <el-table :data="filteredDevices" v-loading="deviceStore.loading" stripe>
        <el-table-column label="设备名称" min-width="150">
          <template #default="{ row }">
            <span>{{ row.name }}</span>
            <el-button size="small" text type="primary" style="margin-left:4px" @click="handleRename(row)">重命名</el-button>
          </template>
        </el-table-column>
        <el-table-column prop="deviceId" label="设备 ID" width="150" />
        <el-table-column label="归属账号" min-width="150">
          <template #default="{ row }">
            <span :class="{ 'owner-empty': !row.ownerAccount }">{{ row.ownerAccount || '未绑定' }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="osVersion" label="系统" width="110" />
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag :type="row.status==='online'?'success':row.status==='reconnecting'?'warning':'info'" size="small">
              {{ row.status==='online'?'在线':row.status==='reconnecting'?'重连':'离线' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="ipAddress" label="IP" width="130" />
        <el-table-column label="今日使用" width="160">
          <template #default="{ row }">
            {{ row.todayUsageMinutes }} / {{ row.todayLimitMinutes }} 分钟
            <el-tag v-if="row.lastResetOffsetMinutes" size="small" type="warning" effect="plain">已重置</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="最近上报" width="170">
          <template #default="{ row }">{{ fmtTime(row.lastReportAt) }}</template>
        </el-table-column>
        <el-table-column label="最后在线" width="170">
          <template #default="{ row }">{{ fmtTime(row.lastSeen) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="160" fixed="right">
          <template #default="{ row }">
            <el-button size="small" text type="primary" @click="showDetail(row)">详情</el-button>
            <el-button size="small" text type="danger" @click="handleDeauthorize(row.id)">取消授权</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="detailVisible" title="设备详情" width="680px">
      <el-descriptions v-if="detailDevice" :column="2" border>
        <el-descriptions-item label="设备名称">{{ detailDevice.name }}</el-descriptions-item>
        <el-descriptions-item label="归属账号">{{ detailDevice.ownerAccount || '未绑定' }}</el-descriptions-item>
        <el-descriptions-item label="设备 ID">{{ detailDevice.deviceId }}</el-descriptions-item>
        <el-descriptions-item label="系统版本">{{ detailDevice.osVersion }}</el-descriptions-item>
        <el-descriptions-item label="状态">{{ detailDevice.status }}</el-descriptions-item>
        <el-descriptions-item label="IP 地址">{{ detailDevice.ipAddress }}</el-descriptions-item>
        <el-descriptions-item label="最后在线">{{ fmtTime(detailDevice.lastSeen) }}</el-descriptions-item>
        <el-descriptions-item label="最近上报">{{ fmtTime(detailDevice.lastReportAt) }}</el-descriptions-item>
        <el-descriptions-item label="今日使用">{{ detailDevice.todayUsageMinutes }} / {{ detailDevice.todayLimitMinutes }} 分钟</el-descriptions-item>
        <el-descriptions-item label="原始累计">{{ detailDevice.rawTodayUsageMinutes ?? detailDevice.todayUsageMinutes }} 分钟</el-descriptions-item>
        <el-descriptions-item label="配对时间">{{ fmtTime(detailDevice.pairedAt) }}</el-descriptions-item>
        <el-descriptions-item label="证书指纹" :span="2">
          <code class="fingerprint">{{ detailDevice.certFingerprint || '—' }}</code>
        </el-descriptions-item>
      </el-descriptions>
      <template #footer>
        <el-button @click="detailVisible = false">关闭</el-button>
        <el-button type="primary" @click="detailVisible = false; handleRename(detailDevice!)">重命名</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.admin-page { max-width: 1500px; }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; flex-wrap: wrap; gap: 10px; }
.page-title { font-size: 22px; font-weight: 600; margin: 0; }
.filter-bar { display: flex; gap: 10px; align-items: center; margin-bottom: 14px; flex-wrap: wrap; }
.filter-search { max-width: 360px; }
.filter-status { width: 120px; }
.table-wrap { overflow-x: auto; }
.owner-empty { color: var(--el-text-color-placeholder); }
.fingerprint { font-size: 11px; word-break: break-all; }

@media (max-width: 768px) {
  .filter-bar { flex-direction: column; align-items: stretch; }
  .filter-search { max-width: none; }
  .filter-status { width: 100%; }
}
</style>
