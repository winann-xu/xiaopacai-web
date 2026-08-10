// 小趴菜 Web 3.0 — 路由配置（P1 骨架）
import { createRouter, createWebHistory } from 'vue-router'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      name: 'home',
      // P3 阶段实现：登录检查，重定向至 dashboard 或 login
      component: () => import('@/views/HomePage.vue'),
    },
    // ---- 用户前端 6 大页面 ----
    {
      path: '/dashboard',
      name: 'dashboard',
      component: () => import('@/views/dashboard/DashboardPage.vue'),
    },
    {
      path: '/devices',
      name: 'devices',
      component: () => import('@/views/devices/DevicesPage.vue'),
    },
    {
      path: '/policies',
      name: 'policies',
      component: () => import('@/views/policies/PoliciesPage.vue'),
    },
    {
      path: '/announcements',
      name: 'announcements',
      component: () => import('@/views/announcements/AnnouncementsPage.vue'),
    },
    {
      path: '/reports',
      name: 'reports',
      component: () => import('@/views/reports/ReportsPage.vue'),
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('@/views/settings/SettingsPage.vue'),
    },
    // ---- 管理后端 5 个页面 ----
    {
      path: '/admin/accounts',
      name: 'adminAccounts',
      component: () => import('@/views/admin/AccountsPage.vue'),
    },
    {
      path: '/admin/devices',
      name: 'adminDevices',
      component: () => import('@/views/admin/AdminDevicesPage.vue'),
    },
    {
      path: '/admin/audit',
      name: 'adminAudit',
      component: () => import('@/views/admin/AuditLogsPage.vue'),
    },
    {
      path: '/admin/system',
      name: 'adminSystem',
      component: () => import('@/views/admin/SystemConfigPage.vue'),
    },
    {
      path: '/admin/data',
      name: 'adminData',
      component: () => import('@/views/admin/DataManagementPage.vue'),
    },
    // P3 阶段添加：登录页、404 页
    // { path: '/login', name: 'login', component: ... },
    // { path: '/:pathMatch(.*)*', name: 'notFound', component: ... },
  ],
})

export default router
