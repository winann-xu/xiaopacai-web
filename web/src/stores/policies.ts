// 小趴菜 Web 3.0 — 策略配置状态管理 (Pinia)
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { policyApi } from '@/api'

export interface CategoryLimit {
  category: 'game' | 'social' | 'video' | 'study'
  label: string
  minutes: number
  enabled: boolean
}

export interface Policy {
  deviceId: number
  dailyLimitMinutes: number       // 30-480
  bedtimeStart: string            // "21:00"
  bedtimeEnd: string              // "07:00"
  categoryLimits: CategoryLimit[]
  whitelist: string[]             // 白名单应用包名
  blacklist: string[]             // 黑名单应用包名
  timeoutAction: 'full_lock' | 'partial_lock' | 'warn_only'
  updatedAt?: string
}

export const usePolicyStore = defineStore('policies', () => {
  // ---- state ----
  const policies = ref<Record<number, Policy>>({})
  const loading = ref(false)
  const saving = ref(false)
  const error = ref<string | null>(null)

  // ---- actions ----
  async function fetchPolicy(deviceId: number) {
    loading.value = true
    error.value = null
    try {
      const res = await policyApi.get(deviceId)
      policies.value[deviceId] = res.data
    } catch {
      // P3 阶段 mock
      if (!policies.value[deviceId]) {
        policies.value[deviceId] = getMockPolicy(deviceId)
      }
    } finally {
      loading.value = false
    }
  }

  async function savePolicy(deviceId: number, policy: Policy) {
    saving.value = true
    error.value = null
    try {
      await policyApi.save(deviceId, policy)
      policies.value[deviceId] = { ...policy, updatedAt: new Date().toISOString() }
    } catch (e: any) {
      error.value = e.response?.data?.message || '保存策略失败'
      throw e
    } finally {
      saving.value = false
    }
  }

  function getPolicy(deviceId: number): Policy | undefined {
    return policies.value[deviceId]
  }

  return {
    policies, loading, saving, error,
    fetchPolicy, savePolicy, getPolicy,
  }
})

// P3 mock 策略
function getMockPolicy(deviceId: number): Policy {
  return {
    deviceId,
    dailyLimitMinutes: 180,
    bedtimeStart: '21:00',
    bedtimeEnd: '07:00',
    categoryLimits: [
      { category: 'game', label: '游戏', minutes: 0, enabled: true },
      { category: 'social', label: '社交', minutes: 60, enabled: true },
      { category: 'video', label: '视频', minutes: 90, enabled: true },
      { category: 'study', label: '学习', minutes: 0, enabled: false },
    ],
    whitelist: ['com.example.calculator', 'com.example.dictionary'],
    blacklist: ['com.example.game1', 'com.example.game2'],
    timeoutAction: 'full_lock',
  }
}
