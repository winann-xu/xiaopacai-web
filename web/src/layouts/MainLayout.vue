<script setup lang="ts">
// 小趴菜 Web 3.0 — 主布局（桌面：侧边栏+顶栏；移动：底部 Tab 导航+更多抽屉）
// [TASK-PRELAUNCH-P1] 需求 1：<768px 切换底部导航，功能不缺失
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { useIsMobile } from '@/composables/useIsMobile'
// [TASK-MILESTONE-V3] 需求 1：构建产物版本号（vite 构建时注入）
import { APP_VERSION } from '@/config/version'
import {
  Odometer, Monitor, Setting, Notification, DataAnalysis, Tools,
  UserFilled, DocumentChecked, SetUp, FolderOpened, FirstAidKit, Connection,
  Message, Expand, Fold, Moon, Sunny, SwitchButton, MoreFilled, Download,
  Document,
} from '@element-plus/icons-vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const ui = useUiStore()
const isMobile = useIsMobile()

// 菜单项
interface MenuItem {
  path: string
  title: string
  icon: any
  role?: string
}

// 移动端底部 Tab 只放 6 个主菜单，下载中心与后台放"更多"抽屉
const userMenuItems: MenuItem[] = [
  { path: '/dashboard', title: '仪表盘', icon: Odometer },
  { path: '/devices', title: '设备管理', icon: Monitor },
  { path: '/policies', title: '策略配置', icon: Setting },
  { path: '/announcements', title: '公告管理', icon: Notification },
  { path: '/reports', title: '使用报告', icon: DataAnalysis },
  // [TASK-MILESTONE-V3] 需求 14：运行日志（家长仅本账号，admin 进管理后台账号日志）
  { path: '/logs', title: '运行日志', icon: Document },
  { path: '/settings', title: '设置', icon: Tools },
]

const adminMenuItems: MenuItem[] = [
  { path: '/admin/accounts', title: '账号管理', icon: UserFilled, role: 'admin' },
  { path: '/admin/devices', title: '设备管理', icon: Monitor, role: 'admin' },
  { path: '/admin/audit', title: '审计日志', icon: DocumentChecked, role: 'admin' },
  { path: '/admin/system', title: '系统设置', icon: SetUp, role: 'admin' },
  // [TASK-ACCOUNT-V1-MAILCONFIG] 邮件设置：验证码邮件通道（DirectMail/SMTP）
  { path: '/admin/mail-config', title: '邮件设置', icon: Message, role: 'admin' },
  { path: '/admin/data', title: '数据管理', icon: FolderOpened, role: 'admin' },
  { path: '/admin/diagnostics', title: '故障诊断', icon: FirstAidKit, role: 'admin' },
  { path: '/admin/relay-sessions', title: '云端中继', icon: Connection, role: 'admin' },
  // [TASK-MILESTONE-V3] 需求 14：账号日志（admin 全部账号，可按账号筛选）
  { path: '/admin/logs', title: '账号日志', icon: Document, role: 'admin' },
]

const isAdmin = computed(() => auth.isAdmin)

// 默认展开管理菜单
const adminMenuOpen = ref(true)

// 移动端"更多"抽屉：下载中心 + 管理后台（仅 admin）
const moreDrawerVisible = ref(false)

function navigateTo(path: string) {
  moreDrawerVisible.value = false
  router.push(path)
}

async function handleLogout() {
  await auth.logout()
}

// 初始化：恢复会话
onMounted(async () => {
  if (!auth.isAuthenticated) {
    const ok = await auth.restoreSession()
    if (!ok) {
      router.push('/login')
    }
  }
})
</script>

<template>
  <!-- ===== 桌面端布局 ===== -->
  <el-container v-if="!isMobile" class="main-layout">
    <!-- 侧边栏 -->
    <el-aside :width="ui.sidebarCollapsed ? '64px' : '220px'" class="layout-aside">
      <div class="aside-header">
        <span v-if="!ui.sidebarCollapsed" class="aside-logo">
          <img src="/logo.png" alt="小趴菜" class="logo-icon" />
          <span class="logo-text">小趴菜</span>
        </span>
        <img v-else src="/logo.png" alt="小趴菜" class="aside-logo-collapsed" />
      </div>

      <el-menu
        :default-active="route.path"
        :collapse="ui.sidebarCollapsed"
        :collapse-transition="false"
        background-color="transparent"
        text-color="var(--el-menu-text-color)"
        active-text-color="var(--el-color-primary)"
        class="aside-menu"
      >
        <!-- 用户菜单 -->
        <el-menu-item
          v-for="item in userMenuItems"
          :key="item.path"
          :index="item.path"
          @click="navigateTo(item.path)"
        >
          <el-icon><component :is="item.icon" /></el-icon>
          <template #title>{{ item.title }}</template>
        </el-menu-item>

        <!-- 下载中心 -->
        <el-menu-item index="/download" @click="navigateTo('/download')">
          <el-icon><Download /></el-icon>
          <template #title>下载中心</template>
        </el-menu-item>

        <!-- 管理菜单（仅 admin） -->
        <template v-if="isAdmin">
          <el-divider style="margin: 8px 0" />
          <el-sub-menu index="admin-group" v-model:open="adminMenuOpen">
            <template #title>
              <el-icon><SetUp /></el-icon>
              <span>管理后台</span>
            </template>
            <el-menu-item
              v-for="item in adminMenuItems"
              :key="item.path"
              :index="item.path"
              @click="navigateTo(item.path)"
            >
              <el-icon><component :is="item.icon" /></el-icon>
              <template #title>{{ item.title }}</template>
            </el-menu-item>
          </el-sub-menu>
        </template>
      </el-menu>

      <!-- 底部控制 -->
      <div class="aside-footer">
        <!-- [TASK-MILESTONE-V3] 需求 1：构建产物携带版本号展示 -->
        <span v-if="!ui.sidebarCollapsed" class="aside-version">v{{ APP_VERSION }}</span>
        <el-button
          :icon="ui.sidebarCollapsed ? Expand : Fold"
          text
          @click="ui.toggleSidebar()"
          style="width: 100%"
        />
      </div>
    </el-aside>

    <!-- 主内容区 -->
    <el-container class="layout-main">
      <!-- 顶栏 -->
      <el-header class="layout-header" height="56px">
        <div class="header-left">
          <el-breadcrumb separator="/">
            <el-breadcrumb-item :to="{ path: '/' }">首页</el-breadcrumb-item>
            <el-breadcrumb-item v-if="route.meta.title">{{ route.meta.title }}</el-breadcrumb-item>
          </el-breadcrumb>
        </div>
        <div class="header-right">
          <el-switch
            v-model="ui.darkMode"
            :active-icon="Moon"
            :inactive-icon="Sunny"
            inline-prompt
            @change="ui.toggleDarkMode()"
          />
          <el-dropdown trigger="click">
            <span class="user-avatar">
              <el-avatar :size="32" icon="UserFilled" />
              <span class="username">{{ auth.user?.displayName || auth.user?.username || '用户' }}</span>
            </span>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item>
                  <span>角色：{{ isAdmin ? '管理员' : '家长' }}</span>
                </el-dropdown-item>
                <el-dropdown-item divided @click="handleLogout">
                  <el-icon><SwitchButton /></el-icon> 退出登录
                </el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </el-header>

      <!-- 内容区 -->
      <el-main class="layout-content">
        <router-view />
      </el-main>
    </el-container>
  </el-container>

  <!-- ===== 移动端布局：底部 Tab 导航 ===== -->
  <el-container v-else class="main-layout mobile-layout">
    <!-- 精简顶栏（隐藏面包屑，保留用户菜单与深色开关） -->
    <el-header class="layout-header mobile-header" height="48px">
      <div class="header-left">
        <span class="mobile-logo">
          <img src="/logo.png" alt="" class="mobile-logo-img" /> 小趴菜
        </span>
      </div>
      <div class="header-right">
        <el-switch
          v-model="ui.darkMode"
          :active-icon="Moon"
          :inactive-icon="Sunny"
          inline-prompt
          @change="ui.toggleDarkMode()"
        />
        <el-dropdown trigger="click">
          <span class="user-avatar">
            <el-avatar :size="28" icon="UserFilled" />
          </span>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item>
                <span>角色：{{ isAdmin ? '管理员' : '家长' }}</span>
              </el-dropdown-item>
              <el-dropdown-item divided @click="handleLogout">
                <el-icon><SwitchButton /></el-icon> 退出登录
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </div>
    </el-header>

    <!-- 内容区（底部留出 Tab 栏高度） -->
    <el-main class="layout-content mobile-content">
      <router-view />
    </el-main>

    <!-- 底部 Tab 导航 -->
    <nav class="mobile-tabbar">
      <button
        v-for="item in userMenuItems"
        :key="item.path"
        class="tabbar-item"
        :class="{ active: route.path.startsWith(item.path) }"
        @click="navigateTo(item.path)"
      >
        <el-icon :size="20"><component :is="item.icon" /></el-icon>
        <span class="tabbar-label">{{ item.title.replace('管理', '') }}</span>
      </button>
      <button
        class="tabbar-item"
        :class="{ active: route.path === '/download' || route.path.startsWith('/admin') }"
        @click="moreDrawerVisible = true"
      >
        <el-icon :size="20"><MoreFilled /></el-icon>
        <span class="tabbar-label">更多</span>
      </button>
    </nav>

    <!-- 更多抽屉：下载中心 + 管理后台（仅 admin） -->
    <el-drawer v-model="moreDrawerVisible" title="更多功能" direction="btt" size="60%">
      <div class="more-list">
        <div class="more-item" @click="navigateTo('/download')">
          <el-icon :size="20"><Download /></el-icon><span>下载中心</span>
        </div>
        <template v-if="isAdmin">
          <el-divider style="margin: 6px 0">管理后台</el-divider>
          <div v-for="item in adminMenuItems" :key="item.path" class="more-item" @click="navigateTo(item.path)">
            <el-icon :size="20"><component :is="item.icon" /></el-icon><span>{{ item.title }}</span>
          </div>
        </template>
      </div>
    </el-drawer>
  </el-container>
</template>

<style scoped>
.main-layout {
  height: 100vh;
  overflow: hidden;
}

.layout-aside {
  background: var(--el-bg-color);
  border-right: 1px solid var(--el-border-color-light);
  display: flex;
  flex-direction: column;
  transition: width 0.3s;
  overflow: hidden;
}

.aside-header {
  height: 56px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-bottom: 1px solid var(--el-border-color-lighter);
  flex-shrink: 0;
}

.aside-logo {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 18px;
  font-weight: 700;
  white-space: nowrap;
  color: var(--el-color-primary);
}

.logo-icon {
  width: 24px;
  height: 24px;
  flex-shrink: 0;
}

.aside-logo-collapsed {
  width: 24px;
  height: 24px;
}

.aside-menu {
  flex: 1;
  overflow-y: auto;
  border-right: none;
}

.aside-menu .el-divider {
  margin: 4px 0;
}

.aside-footer {
  border-top: 1px solid var(--el-border-color-lighter);
  padding: 4px;
  flex-shrink: 0;
}

/* [TASK-MILESTONE-V3] 需求 1：侧边栏底部版本号 */
.aside-version {
  display: block;
  text-align: center;
  font-size: 11px;
  color: var(--el-text-color-placeholder);
  padding: 2px 0 6px;
  user-select: all;
}

.layout-main {
  flex-direction: column;
  overflow: hidden;
}

.layout-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  border-bottom: 1px solid var(--el-border-color-light);
  background: var(--el-bg-color);
  padding: 0 20px;
  flex-shrink: 0;
}

.header-left {
  display: flex;
  align-items: center;
}

.header-right {
  display: flex;
  align-items: center;
  gap: 16px;
}

.user-avatar {
  display: flex;
  align-items: center;
  gap: 8px;
  cursor: pointer;
}

.username {
  font-size: 14px;
  color: var(--el-text-color-regular);
}

.layout-content {
  flex: 1;
  overflow-y: auto;
  padding: 20px;
  background: var(--el-bg-color-page);
}

/* ===== 移动端 ===== */
.mobile-header {
  padding: 0 12px;
}

.mobile-logo {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 15px;
  font-weight: 700;
  color: var(--el-color-primary);
}

.mobile-logo-img {
  width: 20px;
  height: 20px;
}

.mobile-content {
  padding: 12px;
  padding-bottom: calc(64px + env(safe-area-inset-bottom));
}

.mobile-tabbar {
  position: fixed;
  bottom: 0;
  left: 0;
  right: 0;
  height: calc(56px + env(safe-area-inset-bottom));
  padding-bottom: env(safe-area-inset-bottom);
  display: flex;
  justify-content: space-around;
  align-items: stretch;
  background: var(--el-bg-color);
  border-top: 1px solid var(--el-border-color-light);
  box-shadow: 0 -2px 8px rgba(0, 0, 0, 0.04);
  z-index: 1000;
}

.tabbar-item {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 2px;
  background: transparent;
  border: none;
  cursor: pointer;
  color: var(--el-text-color-secondary);
  min-height: 44px;
  font-family: inherit;
}

.tabbar-item.active {
  color: var(--el-color-primary);
}

.tabbar-label {
  font-size: 10px;
  line-height: 1;
  white-space: nowrap;
}

.more-list {
  display: flex;
  flex-direction: column;
}

.more-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 12px 4px;
  font-size: 14px;
  color: var(--el-text-color-regular);
  cursor: pointer;
  border-bottom: 1px solid var(--el-border-color-lighter);
  min-height: 44px;
}

.more-item:active {
  color: var(--el-color-primary);
}
</style>
