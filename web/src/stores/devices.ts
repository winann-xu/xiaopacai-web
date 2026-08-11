// 小趴菜 Web 3.0 — 设备状态管理 (Pinia)
import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { deviceApi } from '@/api'

export interface Device {
  id: number
  name: string
  deviceId: string       // 设备唯一标识
  ipAddress: string
  osVersion: string
  status: 'online' | 'reconnecting' | 'offline'
  lastSeen: string       // ISO datetime
  certFingerprint: string
  pairedAt: string
  todayUsageMinutes: number
  todayLimitMinutes: number
}

export const useDeviceStore = defineStore('devices', () => {
  // ---- state ----
  const devices = ref<Device[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)

  // ---- getters ----
  const onlineCount = computed(() => devices.value.filter(d => d.status === 'online').length)
  const totalCount = computed(() => devices.value.length)
  const offlineDevices = computed(() => devices.value.filter(d => d.status === 'offline'))

  // ---- actions ----
  async function fetchDevices() {
    loading.value = true
    error.value = null
    try {
      const res = await deviceApi.list()
      devices.value = res.data
    } catch (e: any) {
      error.value = e.response?.data?.message || '获取设备列表失败'
      // P3 阶段：API 失败时使用 mock 数据
      if (!devices.value.length) {
        devices.value = getMockDevices()
      }
    } finally {
      loading.value = false
    }
  }

  async function unpairDevice(deviceId: number) {
    await deviceApi.unpair(deviceId)
    devices.value = devices.value.filter(d => d.id !== deviceId)
  }

  function updateDeviceStatus(deviceId: number, status: Device['status']) {
    const device = devices.value.find(d => d.id === deviceId)
    if (device) device.status = status
  }

  return {
    devices, loading, error,
    onlineCount, totalCount, offlineDevices,
    fetchDevices, unpairDevice, updateDeviceStatus,
  }
})

// P3 阶段 mock 数据（P4 替换为真实 API）
function getMockDevices(): Device[] {
  return [
    {
      id: 1, name: '小明的手机', deviceId: 'AND-001',
      ipAddress: '192.168.1.101', osVersion: 'Android 13',
      status: 'online', lastSeen: new Date().toISOString(),
      certFingerprint: 'A1:B2:C3:D4:E5:F6:11:22:33:44:55:66:77:88:99:00',
      pairedAt: '2026-08-01T10:00:00Z', todayUsageMinutes: 87, todayLimitMinutes: 180,
    },
    {
      id: 2, name: '小红的平板', deviceId: 'AND-002',
      ipAddress: '192.168.1.102', osVersion: 'Android 12',
      status: 'offline', lastSeen: '2026-08-10T18:30:00Z',
      certFingerprint: 'B2:C3:D4:E5:F6:11:22:33:44:55:66:77:88:99:00:AA',
      pairedAt: '2026-08-02T14:00:00Z', todayUsageMinutes: 0, todayLimitMinutes: 120,
    },
    {
      id: 3, name: '测试设备', deviceId: 'AND-TEST',
      ipAddress: '192.168.1.200', osVersion: 'Android 14',
      status: 'reconnecting', lastSeen: '2026-08-11T07:00:00Z',
      certFingerprint: 'C3:D4:E5:F6:11:22:33:44:55:66:77:88:99:00:AA:BB',
      pairedAt: '2026-08-03T09:00:00Z', todayUsageMinutes: 45, todayLimitMinutes: 240,
    },
  ]
}
