// 小趴菜 Web 3.0 — 认证状态管理 (Pinia)
import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { authApi, apiClient } from '@/api'
import router from '@/router'

export interface UserProfile {
  id: number
  username: string
  role: 'admin' | 'parent'
  displayName: string
  email?: string
}

export const useAuthStore = defineStore('auth', () => {
  // ---- state ----
  const user = ref<UserProfile | null>(null)
  const accessToken = ref<string | null>(localStorage.getItem('access_token'))
  const refreshToken = ref<string | null>(localStorage.getItem('refresh_token'))
  const loading = ref(false)

  // ---- getters ----
  const isAuthenticated = computed(() => !!accessToken.value && !!user.value)
  const isAdmin = computed(() => user.value?.role === 'admin')
  const isParent = computed(() => user.value?.role === 'parent')

  // ---- actions ----
  /** 登录：调用 API → 存 token → 取 profile */
  async function login(username: string, password: string): Promise<void> {
    loading.value = true
    try {
      const res = await authApi.login(username, password)
      const { accessToken: at, refreshToken: rt, user: u } = res.data
      accessToken.value = at
      refreshToken.value = rt
      user.value = u
      localStorage.setItem('access_token', at)
      localStorage.setItem('refresh_token', rt)
      // 注入 Axios 默认头
      apiClient.defaults.headers.common['Authorization'] = `Bearer ${at}`
    } finally {
      loading.value = false
    }
  }

  /** 扫码登录确认后：直接使用 Ticket 返回的 JWT 与用户档案完成登录（OPT12 需求 10） */
  async function loginWithAuthResponse(data: {
    accessToken: string
    refreshToken: string
    profile?: UserProfile | null
  }): Promise<void> {
    accessToken.value = data.accessToken
    refreshToken.value = data.refreshToken
    user.value = data.profile ?? null
    localStorage.setItem('access_token', data.accessToken)
    localStorage.setItem('refresh_token', data.refreshToken)
    if (data.profile) {
      // 路由守卫读取角色（admin 路由仅 admin 可访问）
      localStorage.setItem('user_role', data.profile.role)
    }
    apiClient.defaults.headers.common['Authorization'] = `Bearer ${data.accessToken}`
  }

  /** 登出 */
  async function logout(): Promise<void> {
    try {
      await authApi.logout()
    } catch {
      // 即使 API 失败也清除本地状态
    } finally {
      accessToken.value = null
      refreshToken.value = null
      user.value = null
      localStorage.removeItem('access_token')
      localStorage.removeItem('refresh_token')
      delete apiClient.defaults.headers.common['Authorization']
      router.push('/login')
    }
  }

  /** 刷新 token */
  async function refreshAccessToken(): Promise<boolean> {
    if (!refreshToken.value) return false
    try {
      const res = await authApi.refresh()
      const { accessToken: at, refreshToken: rt } = res.data
      accessToken.value = at
      refreshToken.value = rt || refreshToken.value
      localStorage.setItem('access_token', at)
      if (rt) localStorage.setItem('refresh_token', rt)
      apiClient.defaults.headers.common['Authorization'] = `Bearer ${at}`
      return true
    } catch {
      // 刷新失败，清除登录状态
      accessToken.value = null
      refreshToken.value = null
      user.value = null
      localStorage.removeItem('access_token')
      localStorage.removeItem('refresh_token')
      return false
    }
  }

  /** 从 token 恢复用户信息（页面刷新后） */
  async function restoreSession(): Promise<boolean> {
    const token = localStorage.getItem('access_token')
    if (!token) return false
    accessToken.value = token
    refreshToken.value = localStorage.getItem('refresh_token')
    apiClient.defaults.headers.common['Authorization'] = `Bearer ${token}`
    try {
      const res = await apiClient.get('/auth/profile')
      user.value = res.data
      return true
    } catch {
      // token 过期，尝试刷新
      return await refreshAccessToken()
    }
  }

  return {
    user, accessToken, refreshToken, loading,
    isAuthenticated, isAdmin, isParent,
    login, loginWithAuthResponse, logout, refreshAccessToken, restoreSession,
  }
})
