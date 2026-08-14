// 小趴菜 Web 3.0 — 认证状态管理 (Pinia)
// [SEC-K5] 会话凭据由服务端 httpOnly Cookie 管理，本地不存任何 token（防 XSS 窃取）；
// 仅保留非敏感 user_role 到 localStorage 供路由守卫角色判断。
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
  const loading = ref(false)

  // ---- getters ----
  const isAuthenticated = computed(() => !!user.value)
  const isAdmin = computed(() => user.value?.role === 'admin')
  const isParent = computed(() => user.value?.role === 'parent')

  // ---- actions ----
  /** 登录：API 成功后服务端已写入 httpOnly Cookie，这里只取档案 */
  async function login(username: string, password: string): Promise<void> {
    loading.value = true
    try {
      const res = await authApi.login(username, password)
      // 服务端同时返回 profile 与 user 字段（兼容新旧前端）
      const u = (res.data?.profile ?? res.data?.user ?? null) as UserProfile | null
      user.value = u
      localStorage.setItem('user_role', u?.role || 'parent')
    } finally {
      loading.value = false
    }
  }

  /** 注册 / 扫码登录确认后：服务端已写入 httpOnly Cookie，这里只取档案（OPT12 需求 10） */
  async function loginWithAuthResponse(data: {
    accessToken?: string
    refreshToken?: string
    profile?: UserProfile | null
    user?: UserProfile | null
  }): Promise<void> {
    const u = data.profile ?? data.user ?? null
    user.value = u
    if (u) {
      // 路由守卫读取角色（admin 路由仅 admin 可访问）
      localStorage.setItem('user_role', u.role)
    }
  }

  /** 登出 */
  async function logout(): Promise<void> {
    try {
      await authApi.logout()
    } catch {
      // 即使 API 失败也清除本地状态（会话 Cookie 由服务端清除）
    } finally {
      user.value = null
      localStorage.removeItem('user_role')
      router.push('/login')
    }
  }

  /** 从服务端会话恢复用户信息（页面刷新后，走 httpOnly Cookie） */
  async function restoreSession(): Promise<boolean> {
    try {
      const res = await apiClient.get('/auth/profile')
      user.value = res.data
      return true
    } catch {
      // 401 时拦截器已自动尝试刷新会话，失败则跳转登录页
      user.value = null
      return false
    }
  }

  return {
    user, loading,
    isAuthenticated, isAdmin, isParent,
    login, loginWithAuthResponse, logout, restoreSession,
  }
})
