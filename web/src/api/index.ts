// 小趴菜 Web 3.0 — API 服务层（P3：完整端点）
import axios from 'axios'
import type { AxiosInstance } from 'axios'

const apiClient: AxiosInstance = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL || '/api',
  timeout: 15000,
  headers: { 'Content-Type': 'application/json' },
})

// ===== 请求拦截器：注入 JWT =====
apiClient.interceptors.request.use((config) => {
  const token = localStorage.getItem('access_token')
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

// ===== 响应拦截器：token 过期自动刷新 =====
let isRefreshing = false
let refreshSubscribers: ((token: string) => void)[] = []

function onRefreshed(token: string) {
  refreshSubscribers.forEach(cb => cb(token))
  refreshSubscribers = []
}

apiClient.interceptors.response.use(
  (response) => response,
  async (error) => {
    const originalRequest = error.config
    // 401 且非刷新请求 → 尝试刷新 token
    if (error.response?.status === 401 && !originalRequest._retry) {
      if (isRefreshing) {
        return new Promise(resolve => {
          refreshSubscribers.push((token: string) => {
            originalRequest.headers.Authorization = `Bearer ${token}`
            resolve(apiClient(originalRequest))
          })
        })
      }
      originalRequest._retry = true
      isRefreshing = true
      try {
        const refreshToken = localStorage.getItem('refresh_token')
        const res = await axios.post(
          `${apiClient.defaults.baseURL}/auth/refresh`,
          { refreshToken },
        )
        const newToken = res.data.accessToken
        localStorage.setItem('access_token', newToken)
        apiClient.defaults.headers.common['Authorization'] = `Bearer ${newToken}`
        onRefreshed(newToken)
        originalRequest.headers.Authorization = `Bearer ${newToken}`
        return apiClient(originalRequest)
      } catch {
        // 刷新失败，跳转登录
        localStorage.removeItem('access_token')
        localStorage.removeItem('refresh_token')
        window.location.href = '/login'
        return Promise.reject(error)
      } finally {
        isRefreshing = false
      }
    }
    console.error('[API Error]', error.response?.status, error.response?.data)
    return Promise.reject(error)
  },
)

export { apiClient }
export default apiClient

// ==================== 认证 ====================
export const authApi = {
  login: (username: string, password: string) =>
    apiClient.post('/auth/login', { username, password }),
  register: (email: string, password: string, displayName?: string) =>
    apiClient.post('/auth/register', { email, password, displayName }),
  logout: () => apiClient.post('/auth/logout'),
  refresh: () => apiClient.post('/auth/refresh'),
  profile: () => apiClient.get('/auth/profile'),
  changePassword: (oldPwd: string, newPwd: string) =>
    apiClient.put('/auth/password', { oldPassword: oldPwd, newPassword: newPwd }),
}

// ==================== 设备配对 ====================
export const pairingApi = {
  // 生成儿童端扫码绑定二维码内容（需登录）
  bindingQr: () => apiClient.post('/pairing/binding-qr'),
}

// ==================== 扫码登录 / 忘记密码 Ticket（OPT12 需求 10/12） ====================
export const ticketApi = {
  // 生成扫码登录 Ticket（未登录可调用，90 秒有效）
  createLogin: (clientId?: string) =>
    apiClient.post('/auth/login-ticket', { clientId }),
  // 轮询扫码登录状态（pending/confirmed/expired，confirmed 时首次返回 JWT）
  pollLogin: (ticket: string) => apiClient.get(`/auth/login-ticket/${ticket}`),
  // 生成重置密码 Ticket（未登录可调用，10 分钟有效）
  createReset: (username: string) =>
    apiClient.post('/auth/reset-ticket', { username }),
  // 轮询重置 Ticket 状态（pending/confirmed/expired）
  pollReset: (ticket: string) => apiClient.get(`/auth/reset-ticket/${ticket}`),
  // 设置新密码（需 Ticket 已确认，成功后吊销全部 refresh token）
  resetPassword: (ticket: string, newPassword: string) =>
    apiClient.post(`/auth/reset-ticket/${ticket}/reset`, { newPassword }),
}

// ==================== 设备 ====================
export const deviceApi = {
  list: () => apiClient.get('/devices'),
  get: (id: number) => apiClient.get(`/devices/${id}`),
  unpair: (id: number) => apiClient.delete(`/devices/${id}`),
  generatePairingCode: () => apiClient.post('/devices/pairing-code'),
  pair: (code: string, ip: string) =>
    apiClient.post('/devices/pair', { pairingCode: code, ipAddress: ip }),
  // 设备应用分类（OPT12 需求 1，分类口径：game/social/video/learning/other）
  getAppCategories: (id: number) => apiClient.get(`/devices/${id}/app-categories`),
  saveAppCategories: (id: number, categories: any[]) =>
    apiClient.put(`/devices/${id}/app-categories`, { categories }),
}

// ==================== 策略 ====================
export const policyApi = {
  get: (deviceId: number) => apiClient.get(`/policies/${deviceId}`),
  save: (deviceId: number, data: any) => apiClient.put(`/policies/${deviceId}`, data),
  push: (deviceId: number) => apiClient.post(`/policies/${deviceId}/push`),
  // [REQ] 重置当日使用限额：儿童端重新开始计时，报告仍保留重置前用量
  resetLimit: (deviceId: number) => apiClient.post(`/policies/${deviceId}/reset-limit`),
}

// ==================== 公告 ====================
export const announcementApi = {
  list: () => apiClient.get('/announcements'),
  get: (id: number) => apiClient.get(`/announcements/${id}`),
  create: (data: any) => apiClient.post('/announcements', data),
  update: (id: number, data: any) => apiClient.put(`/announcements/${id}`, data),
  delete: (id: number) => apiClient.delete(`/announcements/${id}`),
  publish: (id: number) => apiClient.post(`/announcements/${id}/publish`),
  revoke: (id: number) => apiClient.post(`/announcements/${id}/revoke`),
  // [TASK-PRELAUNCH-P3] 送达与回执明细 / 紧急公告未确认统计
  deliveries: (id: number) => apiClient.get(`/announcements/${id}/deliveries`),
  urgentStats: () => apiClient.get('/announcements/urgent-stats'),
}

// ==================== 使用报告 ====================
export const reportApi = {
  daily: (deviceId?: number, date?: string) =>
    apiClient.get('/reports/daily', { params: { deviceId, date } }),
  weekly: (deviceId?: number, weekStart?: string) =>
    apiClient.get('/reports/weekly', { params: { deviceId, weekStart } }),
  exportData: (format: 'txt' | 'json' | 'csv', params?: any) =>
    apiClient.get('/reports/export', { params: { format, ...params }, responseType: 'blob' }),
}

// ==================== 设置 ====================
export const settingsApi = {
  get: () => apiClient.get('/settings'),
  save: (data: any) => apiClient.put('/settings', data),
  backup: () => apiClient.post('/settings/backup'),
  restore: (file: File) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post('/settings/restore', form)
  },
  clearData: () => apiClient.post('/settings/clear-data'),
}

// ==================== 管理端：账号 ====================
export const adminAccountApi = {
  list: () => apiClient.get('/admin/accounts'),
  create: (data: any) => apiClient.post('/admin/accounts', data),
  update: (id: number, data: any) => apiClient.put(`/admin/accounts/${id}`, data),
  delete: (id: number) => apiClient.delete(`/admin/accounts/${id}`),
  resetPassword: (id: number) => apiClient.post(`/admin/accounts/${id}/reset-password`),
}

// ==================== 管理端：审计 ====================
export const adminAuditApi = {
  list: (params?: any) => apiClient.get('/admin/audit-logs', { params }),
  exportData: (format: string, params?: any) =>
    apiClient.get('/admin/audit-logs/export', { params: { format, ...params }, responseType: 'blob' }),
}

// ==================== 管理端：故障诊断（OPT12 需求 5） ====================
export const adminDiagnosticsApi = {
  // 诊断记录列表 / 筛选（deviceId、from/to 时间范围、limit）
  list: (params?: any) => apiClient.get('/admin/diagnostics', { params }),
  // 导出诊断数据（JSON 文件下载）
  exportData: (params?: any) =>
    apiClient.get('/admin/diagnostics/export', { params, responseType: 'blob' }),
}

// ==================== 管理端：云端中继会话（OPT12 需求 3） ====================
export const relayApi = {
  // 中继会话列表（status/role 筛选）
  sessions: (params?: any) => apiClient.get('/relay/sessions', { params }),
}

// ==================== 管理端：系统配置 ====================
export const adminSystemApi = {
  get: () => apiClient.get('/admin/system'),
  save: (data: any) => apiClient.put('/admin/system', data),
}

// ==================== 管理端：数据管理 ====================
export const adminDataApi = {
  status: () => apiClient.get('/admin/data/status'),
  backup: () => apiClient.post('/admin/data/backup'),
  restore: (file: File) => {
    const form = new FormData()
    form.append('file', file)
    return apiClient.post('/admin/data/restore', form)
  },
  clear: () => apiClient.post('/admin/data/clear'),
  rotateKeys: () => apiClient.post('/admin/data/rotate-keys'),
}

// ==================== 健康检查 ====================
export const healthApi = {
  check: () => apiClient.get('/health'),
}
