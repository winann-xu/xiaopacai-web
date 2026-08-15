// 小趴菜 Web 3.0 — API 服务层（P3：完整端点）
import axios from 'axios'
import type { AxiosInstance } from 'axios'

const apiClient: AxiosInstance = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL || '/api',
  timeout: 15000,
  headers: { 'Content-Type': 'application/json' },
})

// ===== [SEC-K5] Cookie 会话：本地不再存储任何 token =====
// access_token / refresh_token 由服务端写入 httpOnly Cookie（JS 不可读，防 XSS 窃取）。
// 请求自动携带 Cookie，无需手动注入 Authorization。
// 401 时调用 /auth/refresh（服务端从 Cookie 读取 refresh_token 并轮换，新 Cookie 自动落盘），
// 失败则登出并跳转登录页。

// ===== 响应拦截器：401 自动刷新会话 =====
let isRefreshing = false
let refreshSubscribers: (() => void)[] = []

function onRefreshed() {
  refreshSubscribers.forEach(cb => cb())
  refreshSubscribers = []
}

apiClient.interceptors.response.use(
  (response) => response,
  async (error) => {
    const originalRequest = error.config
    // 401 且非刷新请求 → 尝试刷新会话（httpOnly Cookie 携带 refresh_token）
    if (error.response?.status === 401 && !originalRequest._retry) {
      if (isRefreshing) {
        return new Promise(resolve => {
          refreshSubscribers.push(() => resolve(apiClient(originalRequest)))
        })
      }
      originalRequest._retry = true
      isRefreshing = true
      try {
        await axios.post(`${apiClient.defaults.baseURL}/auth/refresh`, {})
        onRefreshed()
        return apiClient(originalRequest)
      } catch {
        // 刷新失败：登出（清除会话 Cookie）并跳转登录
        try {
          await axios.post(`${apiClient.defaults.baseURL}/auth/logout`, {})
        } catch {
          // 登出失败忽略，页面跳转后 Cookie 自然过期
        }
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
  // [TASK-ACCOUNT-V1] 邮箱验证码：purpose ∈ register | login | reset_password
  emailCode: (email: string, purpose: 'register' | 'login' | 'reset_password') =>
    apiClient.post('/auth/email-code', { email, purpose }),
  // [TASK-ACCOUNT-V1] 注册需验证码（先调 emailCode 获取）
  register: (email: string, code: string, password: string, displayName?: string) =>
    apiClient.post('/auth/register', { email, code, password, displayName }),
  // [TASK-ACCOUNT-V1] 验证码登录（辅助登录方式）
  codeLogin: (email: string, code: string) =>
    apiClient.post('/auth/login/code', { email, code }),
  // [TASK-ACCOUNT-V1] 找回密码（邮箱验证码 + 新密码，成功后吊销全部 refresh token）
  passwordReset: (email: string, code: string, newPassword: string) =>
    apiClient.post('/auth/password-reset', { email, code, newPassword }),
  // [TASK-ACCOUNT-V1] 登录态密码二次验证 → 一次性 actionToken（解绑前置）
  verifyPassword: (password: string) =>
    apiClient.post('/auth/verify-password', { password }),
  // [SEC-K5] 空 body（{}）：[ApiController] 对空 body 的 [FromBody] 会 400，传空对象走 Cookie 吊销
  logout: () => apiClient.post('/auth/logout', {}),
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

// ==================== 扫码登录 Ticket（OPT12 需求 10；reset-ticket 已随 ACCOUNT-V1 退役） ====================
export const ticketApi = {
  // 生成扫码登录 Ticket（未登录可调用，90 秒有效）
  createLogin: (clientId?: string) =>
    apiClient.post('/auth/login-ticket', { clientId }),
  // 轮询扫码登录状态（pending/confirmed/expired，confirmed 时首次返回 JWT）
  pollLogin: (ticket: string) => apiClient.get(`/auth/login-ticket/${ticket}`),
}

// ==================== 设备 ====================
export const deviceApi = {
  list: () => apiClient.get('/devices'),
  get: (id: number) => apiClient.get(`/devices/${id}`),
  // [TASK-ACCOUNT-V1] 解绑需携带密码二次验证签发的 X-Action-Token
  unpair: (id: number, actionToken: string) =>
    apiClient.delete(`/devices/${id}`, { headers: { 'X-Action-Token': actionToken } }),
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

// ==================== 运行日志（TASK-MILESTONE-V3 需求 14） ====================
export const logsApi = {
  // 列表：普通家长仅本账号；admin 全部 + accountId/level/from/to/limit/offset 筛选
  list: (params?: any) => apiClient.get('/logs', { params }),
}

// ==================== 管理端：系统配置 ====================
export const adminSystemApi = {
  get: () => apiClient.get('/admin/system'),
  save: (data: any) => apiClient.put('/admin/system', data),
}

// ==================== 管理端：邮件设置（[TASK-ACCOUNT-V1-MAILCONFIG]，仅 admin） ====================
export const mailConfigApi = {
  // Secret 脱敏回显（「已设置」/「」），永不返回明文
  get: () => apiClient.get('/admin/mail-config'),
  // Secret 字段留空 = 保持不变；保存即热生效
  save: (data: any) => apiClient.put('/admin/mail-config', data),
  // 发送测试邮件（使用当前已保存配置）
  test: (to: string) => apiClient.post('/admin/mail-config/test', { to }),
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
