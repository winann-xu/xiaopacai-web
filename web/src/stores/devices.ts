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
  // [TASK-PRELAUNCH-P4] 时间额度口径：todayUsageMinutes=调整后已用，rawTodayUsageMinutes=原始累计
  todayUsageMinutes: number
  rawTodayUsageMinutes?: number
  todayRemainingMinutes?: number
  todayLimitMinutes: number
  lastResetOffsetMinutes?: number
  lastResetDate?: string | null
  lastReportAt?: string | null
}

export const useDeviceStore = defineStore('devices', () => {
  // ---- state ----
  const devices = ref<Device[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)
  // [TASK-PRELAUNCH-P4] 最后成功刷新时间（页面展示“最后刷新 HH:mm:ss”）
  const lastRefreshAt = ref<string | null>(null)

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
      lastRefreshAt.value = new Date().toISOString()
    } catch (e: any) {
      // [TASK-PRELAUNCH-P4] 移除 Mock 兜底：API 失败显示错误态 + 重试，绝不渲染假设备（需求 7 第 3 条）
      error.value = e.response?.data?.message || e.response?.data?.error || '获取设备列表失败'
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

  // [TASK-PRELAUNCH-P4] 重置限额成功后本地即时归零（不等下次轮询）；原始累计保留（报告口径不受影响）
  function applyResetLocally(deviceId: number, remainingMinutes: number) {
    const device = devices.value.find(d => d.id === deviceId)
    if (device) {
      device.lastResetOffsetMinutes = device.rawTodayUsageMinutes ?? 0
      device.todayUsageMinutes = 0
      device.todayRemainingMinutes = remainingMinutes
    }
  }

  return {
    devices, loading, error, lastRefreshAt,
    onlineCount, totalCount, offlineDevices,
    fetchDevices, unpairDevice, updateDeviceStatus, applyResetLocally,
  }
})
