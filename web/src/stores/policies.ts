// 小趴菜 Web 3.0 — 策略配置状态管理 (Pinia)
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { policyApi } from '@/api'

export interface CategoryLimit {
  // [TASK-PRELAUNCH-P2] 分类口径统一 learning（兼容旧 study）
  category: 'game' | 'social' | 'video' | 'learning'
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
  // [TASK-MILESTONE-V3] A2 服务端权威版本：保存时作为 expectedVersion 回传，冲突返回 409
  version?: number
}

export const usePolicyStore = defineStore('policies', () => {
  // ---- state ----
  const policies = ref<Record<number, Policy>>({})
  const loading = ref(false)
  const saving = ref(false)
  const resetting = ref(false)
  const error = ref<string | null>(null)

  // ---- actions ----
  async function fetchPolicy(deviceId: number) {
    loading.value = true
    error.value = null
    try {
      const res = await policyApi.get(deviceId)
      policies.value[deviceId] = res.data
    } catch (e: any) {
      // [TASK-PRELAUNCH-P2] 移除 Mock 兜底：API 失败显示错误态，绝不渲染假策略
      error.value = e.response?.data?.message || '策略加载失败'
      throw e
    } finally {
      loading.value = false
    }
  }

  async function savePolicy(deviceId: number, policy: Policy) {
    saving.value = true
    error.value = null
    try {
      // [TASK-MILESTONE-V3] A2 乐观并发：回传服务端版本，冲突时服务端返回 409 + 最新版
      const res = await policyApi.save(deviceId, {
        ...policy,
        expectedVersion: policy.version,
      })
      // 保存成功：服务端版本已递增，以响应为准（后端返回最新 policy）
      policies.value[deviceId] = { ...policy, ...(res.data?.policy ?? {}), updatedAt: new Date().toISOString() }
    } catch (e: any) {
      // [TASK-MILESTONE-V3] A2 409 冲突：采纳服务端最新版并提示刷新，不静默覆盖
      if (e.response?.status === 409 && e.response?.data?.policy) {
        policies.value[deviceId] = { ...e.response.data.policy, updatedAt: e.response.data.policy.updatedAt }
        error.value = e.response?.data?.error || '策略已被其他端修改，已加载服务端最新版本，请确认后重新保存'
      } else {
        error.value = e.response?.data?.message || '保存策略失败'
      }
      throw e
    } finally {
      saving.value = false
    }
  }

  // [REQ] 重置当日限额：调用服务端接口并下发 limit_reset 到儿童端
  async function resetLimit(deviceId: number) {
    resetting.value = true
    error.value = null
    try {
      const res = await policyApi.resetLimit(deviceId)
      return res.data
    } catch (e: any) {
      error.value = e.response?.data?.error || '重置当日限额失败'
      throw e
    } finally {
      resetting.value = false
    }
  }

  function getPolicy(deviceId: number): Policy | undefined {
    return policies.value[deviceId]
  }

  return {
    policies, loading, saving, resetting, error,
    fetchPolicy, savePolicy, resetLimit, getPolicy,
  }
})
