// 小趴菜 Web 3.0 — 路由配置（P3 完整版）
import { createRouter, createWebHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'

// 懒加载页面组件
const LoginPage = () => import('@/views/auth/LoginPage.vue')
const MainLayout = () => import('@/layouts/MainLayout.vue')
const DashboardPage = () => import('@/views/dashboard/DashboardPage.vue')
const DevicesPage = () => import('@/views/devices/DevicesPage.vue')
const PoliciesPage = () => import('@/views/policies/PoliciesPage.vue')
const AnnouncementsPage = () => import('@/views/announcements/AnnouncementsPage.vue')
const ReportsPage = () => import('@/views/reports/ReportsPage.vue')
const SettingsPage = () => import('@/views/settings/SettingsPage.vue')
const AccountsPage = () => import('@/views/admin/AccountsPage.vue')
const AdminDevicesPage = () => import('@/views/admin/AdminDevicesPage.vue')
const AuditLogsPage = () => import('@/views/admin/AuditLogsPage.vue')
const SystemConfigPage = () => import('@/views/admin/SystemConfigPage.vue')
const MailConfigPage = () => import('@/views/admin/MailConfigPage.vue')
const DataManagementPage = () => import('@/views/admin/DataManagementPage.vue')
const DiagnosticsView = () => import('@/views/admin/DiagnosticsView.vue')
const RelaySessionsView = () => import('@/views/admin/RelaySessionsView.vue')
const LogsPage = () => import('@/views/logs/LogsPage.vue')
const NotFoundPage = () => import('@/views/auth/NotFoundPage.vue')
const DownloadPage = () => import('@/views/public/DownloadPage.vue')

// 用户端路由（parent）
const userRoutes: RouteRecordRaw[] = [
  { path: 'dashboard', name: 'dashboard', component: DashboardPage, meta: { title: '仪表盘', icon: 'Odometer' } },
  { path: 'devices', name: 'devices', component: DevicesPage, meta: { title: '设备管理', icon: 'Monitor' } },
  { path: 'policies', name: 'policies', component: PoliciesPage, meta: { title: '策略配置', icon: 'Setting' } },
  { path: 'announcements', name: 'announcements', component: AnnouncementsPage, meta: { title: '公告管理', icon: 'Notification' } },
  { path: 'reports', name: 'reports', component: ReportsPage, meta: { title: '使用报告', icon: 'DataAnalysis' } },
  // [TASK-MILESTONE-V3] 需求 14：运行日志（普通家长仅本账号，服务端过滤）
  { path: 'logs', name: 'logs', component: LogsPage, meta: { title: '运行日志', icon: 'Document' } },
  { path: 'settings', name: 'settings', component: SettingsPage, meta: { title: '设置', icon: 'Tools' } },
]

// 管理端路由（admin）
const adminRoutes: RouteRecordRaw[] = [
  { path: 'admin/accounts', name: 'adminAccounts', component: AccountsPage, meta: { title: '账号管理', icon: 'UserFilled', role: 'admin' } },
  { path: 'admin/devices', name: 'adminDevices', component: AdminDevicesPage, meta: { title: '设备管理', icon: 'Monitor', role: 'admin' } },
  { path: 'admin/audit', name: 'adminAudit', component: AuditLogsPage, meta: { title: '审计日志', icon: 'DocumentChecked', role: 'admin' } },
  { path: 'admin/system', name: 'adminSystem', component: SystemConfigPage, meta: { title: '系统设置', icon: 'SetUp', role: 'admin' } },
  { path: 'admin/mail-config', name: 'adminMailConfig', component: MailConfigPage, meta: { title: '邮件设置', icon: 'Message', role: 'admin' } },
  { path: 'admin/data', name: 'adminData', component: DataManagementPage, meta: { title: '数据管理', icon: 'FolderOpened', role: 'admin' } },
  { path: 'admin/diagnostics', name: 'adminDiagnostics', component: DiagnosticsView, meta: { title: '故障诊断', icon: 'FirstAidKit', role: 'admin' } },
  { path: 'admin/relay-sessions', name: 'adminRelaySessions', component: RelaySessionsView, meta: { title: '云端中继', icon: 'Connection', role: 'admin' } },
  // [TASK-MILESTONE-V3] 需求 14：账号日志（admin 全部账号，可按账号筛选）
  { path: 'admin/logs', name: 'adminLogs', component: LogsPage, meta: { title: '账号日志', icon: 'Document', role: 'admin' } },
]

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/login',
      name: 'login',
      component: LoginPage,
      meta: { guest: true },
    },
    {
      path: '/download',
      name: 'download',
      component: DownloadPage,
      meta: { guest: true, title: '下载中心' },
    },
    {
      path: '/',
      component: MainLayout,
      redirect: '/dashboard',
      children: [
        ...userRoutes,
        ...adminRoutes,
      ],
    },
    {
      path: '/:pathMatch(.*)*',
      name: 'notFound',
      component: NotFoundPage,
    },
  ],
})

// ===== 路由守卫：登录检查 + 角色鉴权 =====
// [SEC-K5] 登录态由服务端 httpOnly Cookie 的 logged_in 标记判断（token 不可被 JS 读取）
function hasLoginCookie(): boolean {
  return document.cookie.split(';').some(c => c.trim().startsWith('logged_in='))
}

router.beforeEach(async (to, _from, next) => {
  const loggedIn = hasLoginCookie()

  // 登录页：已登录重定向到仪表盘
  if (to.name === 'login') {
    if (loggedIn) {
      return next('/dashboard')
    }
    return next()
  }

  // 公共页面（下载中心等）：登录前后均可访问
  if (to.meta.guest) {
    return next()
  }

  // 未登录 → 跳转登录页
  if (!loggedIn) {
    return next({ name: 'login', query: { redirect: to.fullPath } })
  }

  // 角色鉴权：admin 路由仅 admin 角色可访问
  if (to.meta.role === 'admin') {
    // 非敏感角色标记（localStorage 保留，不含凭据）
    const userRole = localStorage.getItem('user_role')
    if (userRole !== 'admin') {
      return next('/dashboard')
    }
  }

  next()
})

export default router
