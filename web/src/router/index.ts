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
const DataManagementPage = () => import('@/views/admin/DataManagementPage.vue')
const NotFoundPage = () => import('@/views/auth/NotFoundPage.vue')

// 用户端路由（parent）
const userRoutes: RouteRecordRaw[] = [
  { path: 'dashboard', name: 'dashboard', component: DashboardPage, meta: { title: '仪表盘', icon: 'Odometer' } },
  { path: 'devices', name: 'devices', component: DevicesPage, meta: { title: '设备管理', icon: 'Monitor' } },
  { path: 'policies', name: 'policies', component: PoliciesPage, meta: { title: '策略配置', icon: 'Setting' } },
  { path: 'announcements', name: 'announcements', component: AnnouncementsPage, meta: { title: '公告管理', icon: 'Notification' } },
  { path: 'reports', name: 'reports', component: ReportsPage, meta: { title: '使用报告', icon: 'DataAnalysis' } },
  { path: 'settings', name: 'settings', component: SettingsPage, meta: { title: '设置', icon: 'Tools' } },
]

// 管理端路由（admin）
const adminRoutes: RouteRecordRaw[] = [
  { path: 'admin/accounts', name: 'adminAccounts', component: AccountsPage, meta: { title: '账号管理', icon: 'UserFilled', role: 'admin' } },
  { path: 'admin/devices', name: 'adminDevices', component: AdminDevicesPage, meta: { title: '设备管理', icon: 'Monitor', role: 'admin' } },
  { path: 'admin/audit', name: 'adminAudit', component: AuditLogsPage, meta: { title: '审计日志', icon: 'DocumentChecked', role: 'admin' } },
  { path: 'admin/system', name: 'adminSystem', component: SystemConfigPage, meta: { title: '系统设置', icon: 'SetUp', role: 'admin' } },
  { path: 'admin/data', name: 'adminData', component: DataManagementPage, meta: { title: '数据管理', icon: 'FolderOpened', role: 'admin' } },
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
router.beforeEach(async (to, _from, next) => {
  const token = localStorage.getItem('access_token')

  // 登录页：已登录重定向到仪表盘
  if (to.name === 'login') {
    if (token) {
      return next('/dashboard')
    }
    return next()
  }

  // 未登录 → 跳转登录页
  if (!token) {
    return next({ name: 'login', query: { redirect: to.fullPath } })
  }

  // 角色鉴权：admin 路由仅 admin 角色可访问
  if (to.meta.role === 'admin') {
    // 简单从 localStorage 读取角色（P3 阶段）
    const userRole = localStorage.getItem('user_role')
    if (userRole !== 'admin') {
      return next('/dashboard')
    }
  }

  next()
})

export default router
