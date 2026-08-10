// 小趴菜 Web 3.0 — API 服务层（P1 骨架）
import axios from 'axios'

const apiClient = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL || '/api',
  timeout: 15000,
  headers: { 'Content-Type': 'application/json' },
})

// 请求拦截器：注入 JWT Token（P2 阶段实现）
apiClient.interceptors.request.use((config) => {
  const token = localStorage.getItem('access_token')
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

// 响应拦截器：统一错误处理
apiClient.interceptors.response.use(
  (response) => response,
  (error) => {
    console.error('[API Error]', error.response?.status, error.response?.data)
    return Promise.reject(error)
  }
)

export default apiClient

// ========== 各模块 API（P2 阶段扩展） ==========

// 健康检查
export const healthApi = {
  check: () => apiClient.get('/health'),
}

// 认证
export const authApi = {
  login: (username: string, password: string) =>
    apiClient.post('/auth/login', { username, password }),
  logout: () => apiClient.post('/auth/logout'),
  refresh: () => apiClient.post('/auth/refresh'),
}

// 设备
export const deviceApi = {
  list: () => apiClient.get('/devices'),
}

// 策略
export const policyApi = {
  get: (deviceId: number) => apiClient.get(`/policies/${deviceId}`),
}
